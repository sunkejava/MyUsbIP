param(
    [string]$InstallDir = 'C:\Program Files\MyUsbIP',
    [string]$SourceDir = "$PSScriptRoot\..\..\artifacts\win-x64\cli",
    [ValidateSet('ude','wdm','auto')]
    [string]$Vhci = 'ude',
    [switch]$SkipDependencies
)

$ErrorActionPreference = 'Stop'
function Require-Administrator {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p = New-Object Security.Principal.WindowsPrincipal($id)
    if (-not $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw '请使用管理员权限运行。' }
}
Require-Administrator

if (-not $SkipDependencies) {
    & "$PSScriptRoot\Install-Dependencies.ps1" -Role Client -Install
    if ($LASTEXITCODE -ne 0) { throw 'usbip-win 依赖准备失败。' }
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$usbipRoot = Join-Path $repoRoot 'dependencies\runtime\usbip-win'
$usbip = Get-ChildItem -Path $usbipRoot -Filter usbip.exe -Recurse -ErrorAction Stop | Select-Object -First 1
if (-not $usbip) { throw "未找到 usbip.exe: $usbipRoot" }

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
if (Test-Path $SourceDir) { Copy-Item (Join-Path $SourceDir '*') $InstallDir -Recurse -Force }
Copy-Item $usbip.Directory.FullName (Join-Path $InstallDir 'usbip-win') -Recurse -Force
$installedUsbip = Get-ChildItem (Join-Path $InstallDir 'usbip-win') -Filter usbip.exe -Recurse | Select-Object -First 1
if (-not $installedUsbip) { throw '复制后未找到 usbip.exe。' }

$arguments = switch ($Vhci) {
    'ude' { @('install','-u') }
    'wdm' { @('install','-w') }
    default { @('install') }
}
& $installedUsbip.FullName @arguments
if ($LASTEXITCODE -ne 0) { throw "VHCI 安装失败，ExitCode=$LASTEXITCODE" }

$binDir = $installedUsbip.Directory.FullName
$currentMachinePath = [Environment]::GetEnvironmentVariable('Path','Machine')
if (($currentMachinePath -split ';') -notcontains $binDir) {
    [Environment]::SetEnvironmentVariable('Path', ($currentMachinePath.TrimEnd(';') + ';' + $binDir), 'Machine')
}

Write-Host "[MyUsbIP] 客户端已安装。usbip.exe: $($installedUsbip.FullName)"
Write-Host '[MyUsbIP] 请重新打开终端后执行 Test-Client.ps1 -ServerHost <服务器IP>。'
