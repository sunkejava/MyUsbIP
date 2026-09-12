param(
    [ValidateSet('Server','Client','All')]
    [string]$Role = 'All',
    [string]$Manifest = "$PSScriptRoot\..\..\config\dependencies.windows.json",
    [switch]$Install,
    [switch]$ForceDownload,
    [switch]$PrepareHash,
    [switch]$FreezeHash
)

$ErrorActionPreference = 'Stop'
function Write-Step([string]$Text) { Write-Host "[MyUsbIP] $Text" }
function Require-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw '请使用管理员 PowerShell/CMD 运行该脚本。' }
}
function Get-AbsolutePath([string]$Path, [string]$BaseDir) {
    if ([IO.Path]::IsPathRooted($Path)) { return $Path }
    return [IO.Path]::GetFullPath((Join-Path $BaseDir $Path))
}

if ($FreezeHash -and -not $PrepareHash) { throw '-FreezeHash 必须与 -PrepareHash 一起使用。' }
if ($Install) { Require-Administrator }
if (-not (Test-Path $Manifest)) { throw "依赖清单不存在: $Manifest" }
$manifestFile = Get-Item $Manifest
$repoRoot = [IO.Path]::GetFullPath((Join-Path $manifestFile.DirectoryName '..'))
$config = Get-Content $manifestFile.FullName -Raw | ConvertFrom-Json
$cacheRoot = Get-AbsolutePath $config.cacheDirectory $repoRoot
New-Item -ItemType Directory -Force -Path $cacheRoot | Out-Null

$packages = @($config.packages | Where-Object {
    $_.enabled -eq $true -and ($Role -eq 'All' -or ($Role -eq 'Server' -and $_.role -eq 'server') -or ($Role -eq 'Client' -and $_.role -eq 'client'))
})

foreach ($pkg in $packages) {
    Write-Step "处理依赖 $($pkg.id) / $($pkg.version)"
    if ([string]::IsNullOrWhiteSpace($pkg.downloadUrl)) { throw "依赖 $($pkg.id) 未配置固定 downloadUrl。" }

    $target = Join-Path $cacheRoot $pkg.fileName
    if ($ForceDownload -or -not (Test-Path $target)) {
        Write-Step "下载 $($pkg.downloadUrl)"
        Invoke-WebRequest -Uri $pkg.downloadUrl -OutFile $target -UseBasicParsing
    } else { Write-Step "使用缓存 $target" }

    $actual = (Get-FileHash -Path $target -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($PrepareHash) {
        Write-Host "[HASH] $($pkg.id) $actual"
        if ($FreezeHash) {
            $pkg.sha256 = $actual
            Write-Step "已在当前工作区冻结 $($pkg.id) SHA256"
        }
        continue
    }

    if ([string]::IsNullOrWhiteSpace($pkg.sha256)) {
        throw "依赖 $($pkg.id) 尚未固定 sha256。请先执行 -PrepareHash，将输出写回 dependencies.windows.json 后再安装。"
    }
    $expected = ([string]$pkg.sha256).Replace(' ', '').ToLowerInvariant()
    if ($actual -ne $expected) {
        Remove-Item $target -Force -ErrorAction SilentlyContinue
        throw "SHA256 校验失败: $($pkg.id)`n期望: $expected`n实际: $actual"
    }
    Write-Step 'SHA256 校验通过'

    if ($Install -and $pkg.installType -eq 'zip') {
        $extractTo = Get-AbsolutePath $pkg.extractTo $repoRoot
        if (Test-Path $extractTo) { Remove-Item $extractTo -Recurse -Force }
        New-Item -ItemType Directory -Force -Path $extractTo | Out-Null
        Expand-Archive -Path $target -DestinationPath $extractTo -Force
        Write-Step "已解压到 $extractTo"
    }

    if ($Install -and $pkg.installType -eq 'msi') {
        $log = Join-Path $cacheRoot "$($pkg.id)-install.log"
        $p = Start-Process msiexec.exe -ArgumentList @('/i', "`"$target`"", '/qn', '/norestart', '/l*v', "`"$log`"") -Wait -PassThru
        if ($p.ExitCode -notin @(0,3010)) { throw "MSI 安装失败，ExitCode=$($p.ExitCode)，日志: $log" }
        if ($p.ExitCode -eq 3010) { Write-Warning '安装完成，但系统要求重启。' }
    }
}

if ($PrepareHash -and $FreezeHash) {
    $json = $config | ConvertTo-Json -Depth 10
    [IO.File]::WriteAllText($manifestFile.FullName, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    Write-Step "已写回临时发布清单: $($manifestFile.FullName)"
}

Write-Step ($PrepareHash ? ($FreezeHash ? '哈希准备并冻结完成。' : '哈希准备完成；请固定清单后再执行生产安装。') : '依赖处理完成。')
