param(
    [string]$ArtifactsRoot = "$PSScriptRoot\..\..\artifacts\win-x64",
    [string]$OutputRoot = "$PSScriptRoot\..\..\artifacts\bundles",
    [string]$ReleaseTag,
    [string]$Repository = 'sunkejava/MyUsbIP',
    [switch]$IncludeCachedDependencies
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$ArtifactsRoot = [IO.Path]::GetFullPath($ArtifactsRoot)
if (-not $ReleaseTag) { $ReleaseTag = 'v' + (Get-Content "$repoRoot/VERSION" -Raw).Trim() }
if ($ReleaseTag -notmatch '^v[0-9]+\.[0-9]+\.[0-9]+([-.][0-9A-Za-z.-]+)?$') { throw '无效 ReleaseTag。' }
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw '无效 Repository。' }
# 双模式使用同一个完整离线包；不允许生成缺少驱动的包。
& "$PSScriptRoot/Validate-Dependencies.ps1" -RequireCachedFiles
$manifest = Get-Content "$repoRoot/config/dependencies.windows.json" -Raw | ConvertFrom-Json
$cache = if ([IO.Path]::IsPathRooted($manifest.cacheDirectory)) { $manifest.cacheDirectory } else { Join-Path $repoRoot $manifest.cacheDirectory }
$embed = Join-Path $repoRoot 'tools/MyUsbIP.WindowsSetup/Embedded'
New-Item -ItemType Directory -Force -Path $OutputRoot,$embed | Out-Null

foreach ($role in @('server','client')) {
    $title = if ($role -eq 'server') { 'Server' } else { 'Client' }
    $name = "MyUsbIP-$title-win-x64"
    $root = Join-Path $OutputRoot $name
    $source = Join-Path $ArtifactsRoot $(if ($role -eq 'server') { 'daemon' } else { 'cli' })
    $exe = if ($role -eq 'server') { 'myusbipd.exe' } else { 'myusbip.exe' }
    $config = if ($role -eq 'server') { 'appsettings.json' } else { 'clientsettings.json' }
    if (-not (Test-Path "$source/$exe") -or -not (Test-Path "$source/$config")) { throw "发布目录不完整: $source" }
    $packages = @($manifest.packages | Where-Object { $_.enabled -eq $true -and $_.role -eq $role })
    if ($packages.Count -ne 1) { throw "$role 必须有且仅有一个启用的驱动包。" }
    $package = $packages[0]
    if ([IO.Path]::GetFileName($package.fileName) -ne $package.fileName -or $package.fileName -match '[:\\/]') { throw '驱动文件名无效。' }
    Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
    $payload = Join-Path $root "payload/$role"
    New-Item -ItemType Directory -Force -Path $payload,"$root/config","$root/dependencies/cache" | Out-Null
    Copy-Item "$source/*" $payload -Recurse -Force
    Copy-Item "$repoRoot/config/dependencies.windows.json" "$root/config/" -Force
    Copy-Item (Join-Path $cache $package.fileName) "$root/dependencies/cache/" -Force
    @"
MyUsbIP $title $ReleaseTag (win-x64)
在线：运行 MyUsbIP-$title-Setup.exe，自动下载当前版本完整安装包。
离线：运行 MyUsbIP-$title-Offline-Setup.exe，全部文件已内置。
也可以将本 ZIP 与在线 EXE 放在同一目录，自动使用本地包；无须解压。
强制离线：MyUsbIP-$title-Setup.exe --mode offline --package `"$name.zip`"
自动化安装：增加 --quiet。仅验证：增加 --verify-only --quiet。
无需安装 .NET 运行时，安装器会提权并完成驱动安装、部署及自检。
安装/升级驱动可能重新枚举 USB，请避开正在进行的关键 USB 操作。
"@ | Set-Content "$root/README.txt" -Encoding UTF8
    $bad = Get-ChildItem $root -Recurse -File | Where-Object { $_.Extension -in @('.ps1','.cmd','.bat') }
    if ($bad) { throw "用户安装包不允许包含脚本入口: $($bad.FullName -join ', ')" }
    $zip = Join-Path $OutputRoot "$name.zip"
    Remove-Item $zip -Force -ErrorAction SilentlyContinue
    Compress-Archive -Path "$root/*" -DestinationPath $zip -CompressionLevel Optimal
    $size = (Get-Item $zip).Length
    if ($size -le 0 -or $size -gt 1GB) { throw '安装包大小超过限制。' }
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    Copy-Item $zip "$embed/$role-bundle.zip" -Force
    # URL 固定到本次 Release tag；SHA256 固定在原生 EXE 中，不读取远端可变清单。
    $setup = [ordered]@{
        schemaVersion = 1
        version = $ReleaseTag.Substring(1)
        role = $role
        runtimeIdentifier = 'win-x64'
        fileName = "$name.zip"
        downloadUrl = "https://github.com/$Repository/releases/download/$ReleaseTag/$name.zip"
        sha256 = $hash
        size = $size
    }
    $json = ($setup | ConvertTo-Json) + [Environment]::NewLine
    [IO.File]::WriteAllText("$embed/$role-setup.json", $json, [Text.UTF8Encoding]::new($false))
    Write-Host "[BUNDLE] $name.zip $size bytes SHA256=$hash"
}
