#Requires -Version 7.0
param(
    [string]$RuntimeIdentifier = 'win-x64',
    [Parameter(Mandatory)][string]$AuditDirectory
)
$ErrorActionPreference = 'Stop'
$hostRidPrefix = if ($IsWindows -or $env:OS -eq 'Windows_NT') { 'win-' } elseif ($IsMacOS) { 'osx-' } else { 'linux-' }
if (!$RuntimeIdentifier.StartsWith($hostRidPrefix)) { throw 'Smoke requires a runtime matching the current host.' }
$work = Join-Path $AuditDirectory ('runtime-smoke/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $work | Out-Null
$results = @()
function Invoke-CliSmoke([string]$Exe, [string]$Config, [string]$Label) {
    $start = [Diagnostics.ProcessStartInfo]::new($Exe)
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment['MYUSBIP_CLIENT_CONFIG'] = $Config
    $start.ArgumentList.Add('audit-invalid-command')
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        [void]$process.Start()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(20000)) { $process.Kill($true); throw "Smoke timeout: $Label" }
        $text = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
        $text | Set-Content (Join-Path $work "$Label.log") -Encoding utf8
        return [pscustomobject]@{ label = $Label; exitCode = $process.ExitCode; output = $text }
    } finally { $process.Dispose() }
}
foreach ($mode in @('untrimmed', 'trimmed')) {
    $suffix = if ($hostRidPrefix -eq 'win-') { '.exe' } else { '' }
    $exe = Join-Path $AuditDirectory "$RuntimeIdentifier/cli-$mode/myusbip$suffix"
    foreach ($logging in @($false, $true)) {
        $label = "$mode-logging-$logging"
        $logs = Join-Path $work "$label-logs"
        $usbIdsPath = Join-Path $work "中文设备库/usb.ids"
        New-Item -ItemType Directory -Force -Path (Split-Path $usbIdsPath -Parent) | Out-Null
        "096e  中文厂商" | Set-Content $usbIdsPath -Encoding utf8
        $config = Join-Path $work "$label.json"
        # 全大写键验证大小写兼容；省略路径/连接超时/接收模式，验证旧配置的默认值。
        @{ COMMANDTIMEOUTSECONDS = 7; USBIDSPATH = $usbIdsPath;
            LOGGING = @{ ENABLED = $logging; DIRECTORY = $logs; RETENTIONDAYS = 5 } } |
            ConvertTo-Json -Depth 5 | Set-Content $config -Encoding utf8
        $result = Invoke-CliSmoke $exe $config $label
        if (!$logging -or $mode -eq 'untrimmed') {
            if ($result.exitCode -ne 2 -or !$result.output.Contains('MyUsbIP CLI')) { throw "Unexpected CLI behavior: $label; exit=$($result.exitCode); output=$($result.output)" }
        }
        if ($logging -and $mode -eq 'untrimmed') {
            $file = Get-ChildItem $logs -Filter '*.jsonl' | Select-Object -First 1
            if (!$file) { throw 'No JSON Lines event produced.' }
            $evt = (Get-Content $file.FullName | Select-Object -First 1) | ConvertFrom-Json
            if ($evt.eventName -ne 'cli.command' -or $evt.properties.arguments[0] -ne 'audit-invalid-command' -or
                $evt.properties.usbipWinPath -ne 'usbip.exe' -or
                $evt.properties.commandTimeoutSeconds -ne 7 -or $evt.properties.attachTimeoutSeconds -ne 120 -or
                $evt.properties.receiveMode -ne 'zero-copy' -or $evt.properties.usbIdsPath -ne $usbIdsPath) { throw 'Configuration/event fields changed.' }
        }
        if ($logging -and $mode -eq 'trimmed') {
            # 当前已知阻断：任意 object 事件仍依赖反射。若修复，应升级此审计断言。
            $files = @(Get-ChildItem $logs -Filter '*.jsonl')
            $lines = @($files | ForEach-Object { Get-Content $_.FullName } | Where-Object { ![string]::IsNullOrWhiteSpace($_) })
            if ($result.exitCode -ne 2 -or $lines.Count -ne 0) {
                throw 'Expected trimmed CLI silent event loss changed; inspect and update the audit.'
            }
        }
        $results += $result
    }
}
$results | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $work 'results.json') -Encoding utf8
Write-Host 'CLI config/event smoke completed; trimmed logging blocker reproduced.'
foreach ($mode in @('untrimmed', 'trimmed')) {
    $suffix = if ($hostRidPrefix -eq 'win-') { '.exe' } else { '' }
    $exe = Join-Path $AuditDirectory "$RuntimeIdentifier/daemon-$mode/myusbipd$suffix"
    $config = Join-Path $work "daemon-$mode.json"
    $logs = Join-Path $work "daemon-$mode-logs"
    # 无共享规则、无连接、原生监听禁用；只启动配置/日志路径，不操作 USB。
    @{ BACKENDMODE = 'AuditNoDevices'; COMMANDTIMEOUTSECONDS = 1;
        LOGGING = @{ ENABLED = $true; DIRECTORY = $logs; RETENTIONDAYS = 5 };
        NATIVESERVER = @{ ENABLED = $false }; AUTOSHARERULES = @(); MANAGEDCONNECTIONS = @() } |
        ConvertTo-Json -Depth 5 | Set-Content $config -Encoding utf8
    $start = [Diagnostics.ProcessStartInfo]::new($exe)
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.ArgumentList.Add($config)
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        [void]$process.Start()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $exited = $process.WaitForExit(6000)
        if (!$exited) { $process.Kill($true); $process.WaitForExit() }
        $text = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
        $text | Set-Content (Join-Path $work "daemon-$mode.log") -Encoding utf8
        if ($mode -eq 'trimmed') {
            $files = @(Get-ChildItem $logs -Filter '*.jsonl')
            $lines = @($files | ForEach-Object { Get-Content $_.FullName } | Where-Object { ![string]::IsNullOrWhiteSpace($_) })
            if (!$text.Contains('Backend: AuditNoDevices') -or $lines.Count -ne 0) {
                throw 'Expected trimmed Daemon silent event loss changed; inspect and update the audit.'
            }
        } else {
            $file = Get-ChildItem $logs -Filter '*.jsonl' | Select-Object -First 1
            if (!$file -or !$text.Contains('Backend: AuditNoDevices')) { throw 'Daemon configuration/startup failed.' }
            $evt = (Get-Content $file.FullName | Select-Object -First 1) | ConvertFrom-Json
            if ($evt.eventName -ne 'daemon.started' -or $evt.properties.backendMode -ne 'AuditNoDevices' -or
                $evt.properties.retentionDays -ne 5) { throw 'Daemon event/config fields changed.' }
        }
        $results += [pscustomobject]@{ label = "daemon-$mode"; exitCode = $process.ExitCode; terminatedByAudit = !$exited; output = $text }
    } finally { $process.Dispose() }
}
$results | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $work 'results.json') -Encoding utf8
Write-Host 'Daemon config/startup event smoke completed; trimmed logging blocker reproduced.'
