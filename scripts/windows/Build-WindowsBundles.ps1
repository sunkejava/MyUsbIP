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
New-Item -ItemType Directory -Force -Path $serverRoot,$clientRoot | Out-Null

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
1. 解压整个 ZIP。
2. 双击 MyUsbIP-Server-Setup.exe。
3. 同意 Windows UAC 管理员授权。
4. 安装器会自动完成 UsbDk、程序部署、防火墙、开机启动和自检。

不需要安装 .NET 运行时，不需要执行 PowerShell/CMD 脚本。
'@ | Set-Content (Join-Path $serverRoot 'README.txt') -Encoding UTF8

@'
MyUsbIP Client
================
1. 解压整个 ZIP。
2. 双击 MyUsbIP-Client-Setup.exe。
3. 同意 Windows UAC 管理员授权。
4. 安装器会自动完成 usbip-win VHCI、CLI 部署、PATH 配置和自检。

不需要安装 .NET 运行时，不需要执行 PowerShell/CMD 脚本。
'@ | Set-Content (Join-Path $clientRoot 'README.txt') -Encoding UTF8

foreach ($root in @($serverRoot,$clientRoot)) {
    $bad = Get-ChildItem $root -Recurse -File | Where-Object { $_.Extension -in @('.ps1','.cmd','.bat') }
    if ($bad) { throw "最终用户 Bundle 中不允许存在脚本入口: $($bad.FullName -join ', ')" }
}
if (-not (Test-Path (Join-Path $serverRoot 'MyUsbIP-Server-Setup.exe'))) { throw '服务端安装器缺失。' }
if (-not (Test-Path (Join-Path $clientRoot 'MyUsbIP-Client-Setup.exe'))) { throw '客户端安装器缺失。' }

$zipServer = "$serverRoot.zip"
$zipClient = "$clientRoot.zip"
Remove-Item $zipServer,$zipClient -Force -ErrorAction SilentlyContinue
Compress-Archive -Path "$serverRoot\*" -DestinationPath $zipServer -CompressionLevel Optimal
Compress-Archive -Path "$clientRoot\*" -DestinationPath $zipClient -CompressionLevel Optimal
Get-FileHash $zipServer,$zipClient -Algorithm SHA256 | ForEach-Object { "$($_.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($_.Path))" } | Set-Content (Join-Path $OutputRoot 'checksums.sha256') -Encoding Ascii
Write-Host "[MyUsbIP] 双击安装 Bundle 生成完成: $OutputRoot"
