param(
    [string]$ArtifactsRoot = "$PSScriptRoot\..\..\artifacts\win-x64",
    [string]$OutputRoot = "$PSScriptRoot\..\..\artifacts\bundles",
    [switch]$IncludeCachedDependencies
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$serverRoot = Join-Path $OutputRoot 'MyUsbIP-Server-win-x64'
$clientRoot = Join-Path $OutputRoot 'MyUsbIP-Client-win-x64'
Remove-Item $serverRoot,$clientRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $serverRoot,$clientRoot,$OutputRoot | Out-Null

$daemon = Join-Path $ArtifactsRoot 'daemon'
$cli = Join-Path $ArtifactsRoot 'cli'
$setup = Join-Path $ArtifactsRoot 'setup\MyUsbIP.Setup.exe'
if (-not (Test-Path $daemon)) { throw "Daemon 发布目录不存在: $daemon" }
if (-not (Test-Path $cli)) { throw "CLI 发布目录不存在: $cli" }
if (-not (Test-Path $setup)) { throw "Windows Setup 不存在: $setup" }

$bundleDirs = @(
    (Join-Path $serverRoot 'payload\server'),
    (Join-Path $clientRoot 'payload\client'),
    (Join-Path $serverRoot 'config'),
    (Join-Path $clientRoot 'config')
)
New-Item -ItemType Directory -Force -Path $bundleDirs | Out-Null

Copy-Item "$daemon\*" (Join-Path $serverRoot 'payload\server') -Recurse -Force
Copy-Item "$cli\*" (Join-Path $clientRoot 'payload\client') -Recurse -Force
Copy-Item $setup (Join-Path $serverRoot 'MyUsbIP-Server-Setup.exe') -Force
Copy-Item $setup (Join-Path $clientRoot 'MyUsbIP-Client-Setup.exe') -Force
Copy-Item $setup (Join-Path $OutputRoot 'MyUsbIP-Server-Setup.exe') -Force
Copy-Item $setup (Join-Path $OutputRoot 'MyUsbIP-Client-Setup.exe') -Force
Copy-Item "$repoRoot\config\dependencies.windows.json" (Join-Path $serverRoot 'config\dependencies.windows.json') -Force
Copy-Item "$repoRoot\config\dependencies.windows.json" (Join-Path $clientRoot 'config\dependencies.windows.json') -Force

if ($IncludeCachedDependencies) {
    $cache = Join-Path $repoRoot 'dependencies\cache'
    if (-not (Test-Path $cache)) { throw "发布要求包含离线依赖，但缓存目录不存在: $cache" }
    New-Item -ItemType Directory -Force -Path @((Join-Path $serverRoot 'dependencies'),(Join-Path $clientRoot 'dependencies')) | Out-Null
    Copy-Item $cache (Join-Path $serverRoot 'dependencies\cache') -Recurse -Force
    Copy-Item $cache (Join-Path $clientRoot 'dependencies\cache') -Recurse -Force
}

@'
MyUsbIP Server
================
推荐：直接运行 Release 中的 MyUsbIP-Server-Setup.exe。
ZIP 仅作为离线展开/排障备用包。

安装器会自动完成管理员提权、UsbDk、程序部署、防火墙、开机启动和自检。
不需要安装 .NET 运行时，不需要执行 PowerShell/CMD/BAT 脚本。
'@ | Set-Content (Join-Path $serverRoot 'README.txt') -Encoding UTF8

@'
MyUsbIP Client
================
推荐：直接运行 Release 中的 MyUsbIP-Client-Setup.exe。
ZIP 仅作为离线展开/排障备用包。

安装器会自动完成管理员提权、usbip-win2 UDE/VHCI、CLI 部署、PATH 配置、旧 usbip-win 迁移清理和自检。
不需要安装 .NET 运行时，不需要执行 PowerShell/CMD/BAT 脚本。

注意：首次安装或升级 usbip-win2 驱动时，Windows USB 3.x Hub/设备可能短暂重新枚举，请避开正在进行的 U 盘复制、摄像头、音频等关键 USB 操作。
'@ | Set-Content (Join-Path $clientRoot 'README.txt') -Encoding UTF8

foreach ($root in @($serverRoot,$clientRoot)) {
    $bad = Get-ChildItem $root -Recurse -File | Where-Object { $_.Extension -in @('.ps1','.cmd','.bat') }
    if ($bad) { throw "最终用户 Bundle 中不允许存在脚本入口: $($bad.FullName -join ', ')" }
}

$zipServer = "$serverRoot.zip"
$zipClient = "$clientRoot.zip"
Remove-Item $zipServer,$zipClient -Force -ErrorAction SilentlyContinue
Compress-Archive -Path "$serverRoot\*" -DestinationPath $zipServer -CompressionLevel Optimal
Compress-Archive -Path "$clientRoot\*" -DestinationPath $zipClient -CompressionLevel Optimal

$checksumFiles = @(
    $zipServer,
    $zipClient,
    (Join-Path $OutputRoot 'MyUsbIP-Server-Setup.exe'),
    (Join-Path $OutputRoot 'MyUsbIP-Client-Setup.exe')
)
Get-FileHash $checksumFiles -Algorithm SHA256 | ForEach-Object { "$($_.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($_.Path))" } | Set-Content (Join-Path $OutputRoot 'checksums.sha256') -Encoding Ascii
Write-Host "[MyUsbIP] 双击安装 EXE 与备用 ZIP 生成完成: $OutputRoot"
