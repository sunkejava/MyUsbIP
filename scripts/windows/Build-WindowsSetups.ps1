param(
    [string]$ArtifactsRoot = "$PSScriptRoot\..\..\artifacts\win-x64",
    [string]$OutputRoot = "$PSScriptRoot\..\..\artifacts\bundles"
)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$ArtifactsRoot = [IO.Path]::GetFullPath($ArtifactsRoot)
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$project = Join-Path $repoRoot 'tools/MyUsbIP.WindowsSetup/MyUsbIP.WindowsSetup.csproj'
$setupRoot = Join-Path $ArtifactsRoot 'setup'
New-Item -ItemType Directory -Force -Path $setupRoot,$OutputRoot | Out-Null
foreach ($role in @('server','client')) {
    $title = if ($role -eq 'server') { 'Server' } else { 'Client' }
    foreach ($mode in @('online','offline')) {
        $publish = Join-Path $ArtifactsRoot "setup-$role-$mode"
        Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue
        # 同一工程先 clean 再 publish，防止在线产物残留上一轮离线资源。
        dotnet clean $project -c Release -r win-x64 -v quiet -m:1
        if ($LASTEXITCODE -ne 0) { throw 'Setup clean 失败。' }
        dotnet publish $project -c Release -r win-x64 "-p:SetupRole=$role" "-p:SetupMode=$mode" -p:PublishAot=true -m:1 -o $publish
        if ($LASTEXITCODE -ne 0) { throw "NativeAOT publish 失败: $role/$mode" }
        $source = Join-Path $publish 'MyUsbIP.Setup.exe'
        if (-not (Test-Path $source)) { throw '缺少 NativeAOT EXE。' }
        if (Get-ChildItem $publish -Filter '*.runtimeconfig.json') { throw '安装器仍包含托管运行时配置。' }
        $suffix = if ($mode -eq 'offline') { '-Offline' } else { '' }
        $name = "MyUsbIP-$title$suffix-Setup.exe"
        Copy-Item $source "$setupRoot/$name" -Force
        Copy-Item $source "$OutputRoot/$name" -Force
        $size = (Get-Item $source).Length
        if ($mode -eq 'online' -and $size -gt 20MB) { throw "在线安装器体积异常: $size bytes" }
        Write-Host "[SETUP] $name $size bytes ($([Math]::Round($size/1MB,2)) MiB)"
    }
}
Get-ChildItem $OutputRoot -File | Where-Object { $_.Extension -in @('.exe','.zip') } | Sort-Object Name |
    Get-FileHash -Algorithm SHA256 | ForEach-Object { "$($_.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($_.Path))" } |
    Set-Content "$OutputRoot/checksums.sha256" -Encoding Ascii
