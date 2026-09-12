param(
    [ValidateSet('Server','Client','All')]
    [string]$Role = 'All',
    [string]$Manifest = "$PSScriptRoot\..\..\config\dependencies.windows.json",
    [switch]$Install,
    [switch]$ForceDownload
)

$ErrorActionPreference = 'Stop'

function Write-Step([string]$Text) {
    Write-Host "[MyUsbIP] $Text"
}

function Require-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw '请使用管理员 PowerShell/CMD 运行该脚本。'
    }
}

function Get-AbsolutePath([string]$Path, [string]$BaseDir) {
    if ([IO.Path]::IsPathRooted($Path)) { return $Path }
    return [IO.Path]::GetFullPath((Join-Path $BaseDir $Path))
}

Require-Administrator

if (-not (Test-Path $Manifest)) {
    throw "依赖清单不存在: $Manifest"
}

$manifestFile = Get-Item $Manifest
$repoRoot = [IO.Path]::GetFullPath((Join-Path $manifestFile.DirectoryName '..'))
$config = Get-Content $manifestFile.FullName -Raw | ConvertFrom-Json
$cacheRoot = Get-AbsolutePath $config.cacheDirectory $repoRoot
New-Item -ItemType Directory -Force -Path $cacheRoot | Out-Null

$packages = @($config.packages | Where-Object {
    $_.enabled -eq $true -and (
        $Role -eq 'All' -or
        ($Role -eq 'Server' -and $_.role -eq 'server') -or
        ($Role -eq 'Client' -and $_.role -eq 'client')
    )
})

foreach ($pkg in $packages) {
    Write-Step "处理依赖 $($pkg.id) / $($pkg.version)"

    if ([string]::IsNullOrWhiteSpace($pkg.downloadUrl)) {
        throw "依赖 $($pkg.id) 尚未配置 downloadUrl。请从 sourcePage 人工确认版本后写入固定下载地址。Source: $($pkg.sourcePage)"
    }
    if ([string]::IsNullOrWhiteSpace($pkg.sha256)) {
        throw "依赖 $($pkg.id) 尚未配置 sha256。生产环境禁止无哈希下载。"
    }

    $target = Join-Path $cacheRoot $pkg.fileName
    if ($ForceDownload -or -not (Test-Path $target)) {
        Write-Step "下载 $($pkg.downloadUrl)"
        Invoke-WebRequest -Uri $pkg.downloadUrl -OutFile $target -UseBasicParsing
    } else {
        Write-Step "使用缓存 $target"
    }

    $actual = (Get-FileHash -Path $target -Algorithm SHA256).Hash.ToLowerInvariant()
    $expected = ([string]$pkg.sha256).Replace(' ', '').ToLowerInvariant()
    if ($actual -ne $expected) {
        Remove-Item $target -Force -ErrorAction SilentlyContinue
        throw "SHA256 校验失败: $($pkg.id)`n期望: $expected`n实际: $actual"
    }
    Write-Step "SHA256 校验通过"

    if ($pkg.extractTo) {
        $extractTo = Get-AbsolutePath $pkg.extractTo $repoRoot
        if ($Install) {
            Write-Step "解压到 $extractTo"
            New-Item -ItemType Directory -Force -Path $extractTo | Out-Null
            Expand-Archive -Path $target -DestinationPath $extractTo -Force
        }
    }

    if ($Install -and $pkg.id -eq 'usbdk-x64') {
        $log = Join-Path $cacheRoot 'usbdk-install.log'
        Write-Step "静默安装 UsbDk，日志: $log"
        $p = Start-Process msiexec.exe -ArgumentList @('/i', "`"$target`"", '/qn', '/norestart', '/l*v', "`"$log`"") -Wait -PassThru
        if ($p.ExitCode -notin @(0,3010)) {
            throw "UsbDk 安装失败，ExitCode=$($p.ExitCode)，请检查 $log"
        }
        if ($p.ExitCode -eq 3010) {
            Write-Warning 'UsbDk 安装完成，但系统要求重启。'
        }
    }
}

Write-Step '依赖处理完成。'
Write-Step '建议继续执行 Test-Server.ps1 或 Test-Client.ps1。'
