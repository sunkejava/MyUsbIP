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
if (-not (Test-Path $daemon)) { throw "Daemon 发布目录不存在: $daemon" }
if (-not (Test-Path $cli)) { throw "CLI 发布目录不存在: $cli" }
Copy-Item "$daemon\*" $serverRoot -Recurse -Force
Copy-Item "$cli\*" $clientRoot -Recurse -Force

foreach ($root in @($serverRoot,$clientRoot)) {
    New-Item -ItemType Directory -Force -Path (Join-Path $root 'scripts\windows'),(Join-Path $root 'config') | Out-Null
    Copy-Item "$repoRoot\config\dependencies.windows.json" (Join-Path $root 'config\dependencies.windows.json') -Force
    Copy-Item "$repoRoot\scripts\windows\Install-Dependencies.ps1" (Join-Path $root 'scripts\windows\Install-Dependencies.ps1') -Force
}
Copy-Item "$repoRoot\scripts\windows\Install-Server.ps1","$repoRoot\scripts\windows\Test-Server.ps1" (Join-Path $serverRoot 'scripts\windows') -Force
Copy-Item "$repoRoot\scripts\windows\Install-Client.ps1","$repoRoot\scripts\windows\Test-Client.ps1" (Join-Path $clientRoot 'scripts\windows') -Force

@'
@echo off
chcp 65001 >nul
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\windows\Install-Server.ps1" -SourceDir "%~dp0"
if errorlevel 1 pause
'@ | Set-Content (Join-Path $serverRoot 'Install-Server.cmd') -Encoding Ascii

@'
@echo off
chcp 65001 >nul
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\windows\Install-Client.ps1" -SourceDir "%~dp0"
if errorlevel 1 pause
'@ | Set-Content (Join-Path $clientRoot 'Install-Client.cmd') -Encoding Ascii

if ($IncludeCachedDependencies) {
    $cache = Join-Path $repoRoot 'dependencies\cache'
    if (Test-Path $cache) {
        Copy-Item $cache (Join-Path $serverRoot 'dependencies\cache') -Recurse -Force
        Copy-Item $cache (Join-Path $clientRoot 'dependencies\cache') -Recurse -Force
    }
}

$zipServer = "$serverRoot.zip"
$zipClient = "$clientRoot.zip"
Remove-Item $zipServer,$zipClient -Force -ErrorAction SilentlyContinue
Compress-Archive -Path "$serverRoot\*" -DestinationPath $zipServer -CompressionLevel Optimal
Compress-Archive -Path "$clientRoot\*" -DestinationPath $zipClient -CompressionLevel Optimal
Get-FileHash $zipServer,$zipClient -Algorithm SHA256 | ForEach-Object { "$($_.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($_.Path))" } | Set-Content (Join-Path $OutputRoot 'checksums.sha256') -Encoding Ascii
Write-Host "[MyUsbIP] Bundle 生成完成: $OutputRoot"
