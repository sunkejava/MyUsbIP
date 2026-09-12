param(
    [string]$InstallDir = 'C:\Program Files\MyUsbIP',
    [string]$SourceDir = "$PSScriptRoot\..\..\artifacts\win-x64\daemon",
    [int]$Port = 3240,
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
    & "$PSScriptRoot\Install-Dependencies.ps1" -Role Server -Install
    if ($LASTEXITCODE -ne 0) { throw 'UsbDk 依赖安装失败。' }
}

if (-not (Test-Path $SourceDir)) { throw "服务端发布目录不存在: $SourceDir" }
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item (Join-Path $SourceDir '*') $InstallDir -Recurse -Force

$helper = 'C:\Program Files\UsbDk Runtime Library\UsbDkHelper.dll'
if (Test-Path $helper) { Copy-Item $helper (Join-Path $InstallDir 'UsbDkHelper.dll') -Force }

$ruleName = 'MyUsbIP USB-IP Server'
if (-not (Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Action Allow -Protocol TCP -LocalPort $Port -Profile Any | Out-Null
}

$exe = Join-Path $InstallDir 'myusbipd.exe'
$config = Join-Path $InstallDir 'appsettings.json'
if (-not (Test-Path $exe)) { throw "缺少 $exe" }

$svc = Get-Service -Name 'MyUsbIP' -ErrorAction SilentlyContinue
if ($svc) {
    Stop-Service MyUsbIP -Force -ErrorAction SilentlyContinue
    sc.exe delete MyUsbIP | Out-Null
    Start-Sleep -Seconds 1
}
$binPath = '"' + $exe + '" "' + $config + '"'
sc.exe create MyUsbIP binPath= $binPath start= auto DisplayName= 'MyUsbIP USB/IP Server' | Out-Null
sc.exe description MyUsbIP 'MyUsbIP UsbDk USB/IP Server' | Out-Null
Start-Service MyUsbIP
Start-Sleep -Seconds 2

& "$PSScriptRoot\Test-Server.ps1" -MyUsbIpDir $InstallDir -Port $Port
