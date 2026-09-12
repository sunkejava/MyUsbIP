param(
    [string]$Manifest = "$PSScriptRoot\..\..\config\dependencies.windows.json",
    [switch]$RequireCachedFiles
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Manifest)) { throw "依赖清单不存在: $Manifest" }
$manifestFile = Get-Item $Manifest
$repoRoot = [IO.Path]::GetFullPath((Join-Path $manifestFile.DirectoryName '..'))
$config = Get-Content $manifestFile.FullName -Raw | ConvertFrom-Json
$cacheRoot = if ([IO.Path]::IsPathRooted($config.cacheDirectory)) { $config.cacheDirectory } else { Join-Path $repoRoot $config.cacheDirectory }
$failed = 0

foreach ($pkg in @($config.packages | Where-Object enabled -eq $true)) {
    Write-Host "[MyUsbIP] 校验 $($pkg.id) $($pkg.version)"
    if ([string]::IsNullOrWhiteSpace($pkg.downloadUrl)) { Write-Error "$($pkg.id): downloadUrl 为空"; $failed++; continue }
    if ($pkg.downloadUrl -match '/latest/') { Write-Error "$($pkg.id): 禁止 latest 动态地址"; $failed++ }
    if ([string]::IsNullOrWhiteSpace($pkg.sha256)) { Write-Warning "$($pkg.id): sha256 尚未冻结"; if ($RequireCachedFiles) { $failed++ } }
    elseif ($pkg.sha256 -notmatch '^[0-9a-fA-F]{64}$') { Write-Error "$($pkg.id): sha256 格式无效"; $failed++ }

    if ($RequireCachedFiles) {
        $file = Join-Path $cacheRoot $pkg.fileName
        if (-not (Test-Path $file)) { Write-Error "$($pkg.id): 缓存文件不存在 $file"; $failed++; continue }
        $actual = (Get-FileHash $file -Algorithm SHA256).Hash
        if ($actual -ne $pkg.sha256) { Write-Error "$($pkg.id): 缓存文件 SHA256 不匹配"; $failed++ }
    }
}

if ($failed -gt 0) { throw "依赖清单校验失败，失败项=$failed" }
Write-Host '[MyUsbIP] 依赖清单结构校验通过。'
