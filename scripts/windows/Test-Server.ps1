param(
    [string]$MyUsbIpDir = "C:\Program Files\MyUsbIP",
    [int]$Port = 3240
)

$ErrorActionPreference = 'Continue'
$failed = 0

function Test-Step([string]$Name, [scriptblock]$Action) {
    Write-Host "`n==== $Name ===="
    try {
        & $Action
        Write-Host "[PASS] $Name"
    } catch {
        $script:failed++
        Write-Host "[FAIL] $Name : $($_.Exception.Message)"
    }
}

Test-Step '管理员权限' {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw '当前进程不是管理员。'
    }
}

Test-Step 'UsbDk 服务存在并运行' {
    $svc = Get-Service -Name UsbDk -ErrorAction Stop
    if ($svc.Status -ne 'Running') { throw "UsbDk 状态=$($svc.Status)" }
    $svc | Format-Table Name,Status,StartType -AutoSize
}

Test-Step 'UsbDk UpperFilters 注册' {
    $path = 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{36fc9e60-c465-11cf-8056-444553540000}'
    $value = (Get-ItemProperty -Path $path -Name UpperFilters -ErrorAction Stop).UpperFilters
    Write-Host "UpperFilters=$($value -join ',')"
    if ($value -notcontains 'UsbDk') { throw 'UpperFilters 中未发现 UsbDk。' }
}

Test-Step 'UsbDkHelper.dll 存在' {
    $candidates = @(
        'C:\Program Files\UsbDk Runtime Library\UsbDkHelper.dll',
        (Join-Path $MyUsbIpDir 'UsbDkHelper.dll')
    )
    $found = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $found) { throw '未找到 UsbDkHelper.dll。' }
    Write-Host "Found: $found"
}

Test-Step 'MyUsbIP 服务端文件存在' {
    $exe = Join-Path $MyUsbIpDir 'myusbipd.exe'
    $config = Join-Path $MyUsbIpDir 'appsettings.json'
    if (-not (Test-Path $exe)) { throw "缺少 $exe" }
    if (-not (Test-Path $config)) { throw "缺少 $config" }
}

Test-Step 'TCP 端口监听' {
    $listen = Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction Stop
    $listen | Format-Table LocalAddress,LocalPort,OwningProcess -AutoSize
}

Test-Step '防火墙规则' {
    $rules = Get-NetFirewallRule -Enabled True -ErrorAction Stop | Where-Object DisplayName -Like '*MyUsbIP*'
    if (-not $rules) { throw '未找到启用状态的 MyUsbIP 防火墙规则。' }
    $rules | Format-Table DisplayName,Enabled,Direction,Action -AutoSize
}

Write-Host "`n==== 服务端自检完成 ===="
if ($failed -gt 0) {
    Write-Host "失败项: $failed"
    exit 1
}
Write-Host '全部检查通过。'
exit 0
