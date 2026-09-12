param(
    [string]$ServerHost,
    [string]$UsbipPath = "usbip.exe",
    [int]$Port = 3240,
    [string]$BusId = ""
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

if ([string]::IsNullOrWhiteSpace($ServerHost)) {
    throw '必须指定 -ServerHost。'
}

Test-Step '管理员权限' {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw '当前进程不是管理员。'
    }
}

Test-Step 'usbip.exe 可执行' {
    $cmd = Get-Command $UsbipPath -ErrorAction Stop
    Write-Host $cmd.Source
}

Test-Step '服务端 TCP 3240 可达' {
    $r = Test-NetConnection -ComputerName $ServerHost -Port $Port -WarningAction SilentlyContinue
    if (-not $r.TcpTestSucceeded) { throw "$ServerHost`:$Port 无法连接。" }
    $r | Select-Object ComputerName,RemoteAddress,RemotePort,TcpTestSucceeded | Format-List
}

Test-Step 'VHCI 驱动存在' {
    $drivers = Get-CimInstance Win32_SystemDriver | Where-Object {
        $_.Name -match 'usbip|vhci' -or $_.DisplayName -match 'USB/IP|VHCI'
    }
    if (-not $drivers) { throw '未发现 USB/IP VHCI 驱动。' }
    $drivers | Select-Object Name,DisplayName,State,PathName | Format-Table -AutoSize
}

Test-Step '远程设备列表' {
    & $UsbipPath list -r $ServerHost
    if ($LASTEXITCODE -ne 0) { throw "usbip list 返回 $LASTEXITCODE" }
}

if (-not [string]::IsNullOrWhiteSpace($BusId)) {
    Test-Step "挂载设备 $BusId" {
        & $UsbipPath attach -r $ServerHost -b $BusId
        if ($LASTEXITCODE -ne 0) { throw "usbip attach 返回 $LASTEXITCODE" }
        Start-Sleep -Seconds 2
        & $UsbipPath port
        if ($LASTEXITCODE -ne 0) { throw "usbip port 返回 $LASTEXITCODE" }
    }
}

Write-Host "`n==== 客户端自检完成 ===="
if ($failed -gt 0) {
    Write-Host "失败项: $failed"
    exit 1
}
Write-Host '全部检查通过。'
exit 0
