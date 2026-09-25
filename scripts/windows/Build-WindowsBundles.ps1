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
$setupServer = Join-Path $ArtifactsRoot 'setup\server\MyUsbIP.Setup.exe'
$setupClient = Join-Path $ArtifactsRoot 'setup\client\MyUsbIP.Setup.exe'
if (-not (Test-Path $daemon)) { throw "Daemon 发布目录不存在: $daemon" }
if (-not (Test-Path $cli)) { throw "CLI 发布目录不存在: $cli" }
if (-not (Test-Path $setupServer)) { throw "Server Windows Setup 不存在: $setupServer" }
if (-not (Test-Path $setupClient)) { throw "Client Windows Setup 不存在: $setupClient" }

$bundleDirs = @(
    (Join-Path $serverRoot 'payload\server'),
    (Join-Path $clientRoot 'payload\client'),
    (Join-Path $serverRoot 'config'),
    (Join-Path $clientRoot 'config')
)
New-Item -ItemType Directory -Force -Path $bundleDirs | Out-Null

Copy-Item "$daemon\*" (Join-Path $serverRoot 'payload\server') -Recurse -Force
Copy-Item "$cli\*" (Join-Path $clientRoot 'payload\client') -Recurse -Force

# 双击安装器是独立 Release 资产，不再重复塞进备用 ZIP。
# Server/Client Setup 已分别只内嵌本角色 payload + 驱动依赖，避免旧版本两个 EXE 内容完全相同。
$serverSetupOut = Join-Path $OutputRoot 'MyUsbIP-Server-Setup.exe'
$clientSetupOut = Join-Path $OutputRoot 'MyUsbIP-Client-Setup.exe'
Copy-Item $setupServer $serverSetupOut -Force
Copy-Item $setupClient $clientSetupOut -Force

$manifestPath = Join-Path $repoRoot 'config\dependencies.windows.json'
Copy-Item $manifestPath (Join-Path $serverRoot 'config\dependencies.windows.json') -Force
Copy-Item $manifestPath (Join-Path $clientRoot 'config\dependencies.windows.json') -Force

if ($IncludeCachedDependencies) {
    $cache = Join-Path $repoRoot 'dependencies\cache'
    if (-not (Test-Path $cache)) { throw "发布要求包含离线依赖，但缓存目录不存在: $cache" }

    $cfg = Get-Content $manifestPath -Raw | ConvertFrom-Json
    $serverPkg = $cfg.packages | Where-Object { $_.enabled -eq $true -and $_.role -eq 'server' } | Select-Object -First 1
    $clientPkg = $cfg.packages | Where-Object { $_.enabled -eq $true -and $_.role -eq 'client' } | Select-Object -First 1
    if (-not $serverPkg -or -not $clientPkg) { throw '依赖清单缺少 server/client 包。' }

    $serverCache = Join-Path $serverRoot 'dependencies\cache'
    $clientCache = Join-Path $clientRoot 'dependencies\cache'
    New-Item -ItemType Directory -Force -Path $serverCache,$clientCache | Out-Null

    $serverDependency = Join-Path $cache $serverPkg.fileName
    $clientDependency = Join-Path $cache $clientPkg.fileName
    if (-not (Test-Path $serverDependency)) { throw "服务端离线依赖不存在: $serverDependency" }
    if (-not (Test-Path $clientDependency)) { throw "客户端离线依赖不存在: $clientDependency" }

    Copy-Item $serverDependency (Join-Path $serverCache $serverPkg.fileName) -Force
    Copy-Item $clientDependency (Join-Path $clientCache $clientPkg.fileName) -Force

    # 正式发布时角色专用 Setup 必须不同；相同说明 SetupRole/EmbeddedResource 条件没有生效。
    $serverSetupHash = (Get-FileHash $serverSetupOut -Algorithm SHA256).Hash
    $clientSetupHash = (Get-FileHash $clientSetupOut -Algorithm SHA256).Hash
    if ($serverSetupHash -eq $clientSetupHash) {
        throw 'Server/Client Setup SHA256 完全相同，角色专用内嵌资源未生效。'
    }
}

@'
MyUsbIP Server
================
推荐：直接运行 Release 中的 MyUsbIP-Server-Setup.exe。
ZIP 为精简的离线展开/排障备用包，不再重复包含独立 Setup.exe。
正式安装请直接使用 Release 中的 MyUsbIP-Server-Setup.exe。

安装器会自动完成管理员提权、UsbDk、程序部署、防火墙、开机启动和自检。
不需要安装 .NET 运行时，不需要执行 PowerShell/CMD/BAT 脚本。
'@ | Set-Content (Join-Path $serverRoot 'README.txt') -Encoding UTF8

@'
MyUsbIP Client
================
推荐：直接运行 Release 中的 MyUsbIP-Client-Setup.exe。
ZIP 为精简的离线展开/排障备用包，不再重复包含独立 Setup.exe。
正式安装请直接使用 Release 中的 MyUsbIP-Client-Setup.exe。

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

Write-Host "[MyUsbIP] 发布产物体积："
Get-Item $checksumFiles | ForEach-Object {
    Write-Host ("  {0,-32} {1,9:N2} MB" -f $_.Name, ($_.Length / 1MB))
}
Write-Host "[MyUsbIP] 双击安装 EXE 与精简备用 ZIP 生成完成: $OutputRoot"
