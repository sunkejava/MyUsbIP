#Requires -Version 7.0
param(
    [string]$RuntimeIdentifier = 'win-x64',
    [string]$DotNetPath = 'dotnet',
    [string]$OutputDirectory,
    [switch]$Publish,
    [switch]$Smoke
)
$ErrorActionPreference = 'Stop'
# 自定义 SDK 路径也必须能被 ILLink 的独立 MSBuild task host 找到。
if ([IO.Path]::IsPathRooted($DotNetPath)) {
    $sdkHome = Split-Path $DotNetPath -Parent
    if (Test-Path (Join-Path $sdkHome 'sdk')) {
        $env:DOTNET_ROOT = $sdkHome
        $env:PATH = $sdkHome + [IO.Path]::PathSeparator + $env:PATH
    }
}
$repo = Split-Path $PSScriptRoot -Parent
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repo 'artifacts/trimming-audit' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$projects = @{
    cli = 'tools/MyUsbIP.Cli/MyUsbIP.Cli.csproj'
    daemon = 'apps/MyUsbIP.Daemon/MyUsbIP.Daemon.csproj'
}
$summary = @()
function Invoke-DotNetAudit([string[]]$Arguments, [string]$LogPath) {
    # 全局属性必须流入所有项目引用；仅根 csproj 的开关不能代表依赖审计结果。
    & $DotNetPath @Arguments *> $LogPath
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed; see $LogPath" }
}
Push-Location $repo
try {
    foreach ($name in @('cli', 'daemon')) {
        $project = $projects[$name]
        $log = Join-Path $OutputDirectory "$name-analyzers.log"
        Invoke-DotNetAudit @('build', $project, '-c', 'Release', '--no-incremental', '-m:1',
            '-p:MyUsbIpTrimmingAudit=true', '-p:EnableTrimAnalyzer=true', '-p:EnableAotAnalyzer=true') $log
        $warnings = @(Get-Content $log | Where-Object { $_ -match 'warning IL\d{4}' } | Sort-Object -Unique)
        $warnings | Set-Content (Join-Path $OutputDirectory "$name-il-warnings.txt") -Encoding utf8
        foreach ($warning in $warnings) { Write-Host $warning }
        $entry = [ordered]@{ project = $name; analyzerWarnings = $warnings.Count; warnings = $warnings }
        if ($Publish -or $Smoke) {
            foreach ($mode in @('untrimmed', 'trimmed')) {
                $dest = Join-Path $OutputDirectory "$RuntimeIdentifier/$name-$mode"
                $isTrimmed = if ($mode -eq 'trimmed') { 'true' } else { 'false' }
                Invoke-DotNetAudit @('publish', $project, '-c', 'Release', '-r', $RuntimeIdentifier,
                    '--self-contained', 'true', '-m:1', '-o', $dest, "-p:PublishTrimmed=$isTrimmed",
                    '-p:PublishAot=false', '-p:EnableTrimAnalyzer=true', '-p:EnableAotAnalyzer=true') `
                    (Join-Path $OutputDirectory "$name-$mode-publish.log")
                $entry["${mode}Bytes"] = (Get-ChildItem $dest -File -Recurse | Measure-Object Length -Sum).Sum
            }
        }
        $summary += [pscustomobject]$entry
    }
    $summary | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $OutputDirectory 'summary.json') -Encoding utf8
    if ($Smoke) {
        & (Join-Path $PSScriptRoot 'Verify-TrimmingRuntime.ps1') -RuntimeIdentifier $RuntimeIdentifier `
            -AuditDirectory $OutputDirectory
        Invoke-DotNetAudit @('build', 'tests/MyUsbIP.SmokeTests/MyUsbIP.SmokeTests.csproj', '-c', 'Release', '-m:1') `
            (Join-Path $OutputDirectory 'existing-smoke-build.log')
        Invoke-DotNetAudit @('tests/MyUsbIP.SmokeTests/bin/Release/net10.0/MyUsbIP.SmokeTests.dll') `
            (Join-Path $OutputDirectory 'existing-smoke.log')
    }
} finally { Pop-Location }
Write-Host "Audit results: $OutputDirectory"
