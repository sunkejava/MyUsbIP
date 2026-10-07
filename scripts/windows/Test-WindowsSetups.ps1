param([string]$OutputRoot = "$PSScriptRoot\..\..\artifacts\bundles")
$ErrorActionPreference = 'Stop'
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
# 从实际 PE 的 RT_MANIFEST 资源读取权限要求，防止 NativeAOT 发布丢失 UAC 清单。
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class SetupPeResources {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);
    [DllImport("kernel32.dll")] public static extern uint SizeofResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")] public static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")] public static extern IntPtr LockResource(IntPtr resource);
    [DllImport("kernel32.dll")] public static extern bool FreeLibrary(IntPtr module);
}
'@
function Verify-AdministratorManifest([string]$Executable) {
    $module = [SetupPeResources]::LoadLibraryEx($Executable, [IntPtr]::Zero, 2)
    if ($module -eq [IntPtr]::Zero) { throw 'Cannot read NativeAOT PE resources.' }
    try {
        $resource = [SetupPeResources]::FindResource($module, [IntPtr]1, [IntPtr]24)
        if ($resource -eq [IntPtr]::Zero) { throw 'NativeAOT executable has no application manifest.' }
        $size = [SetupPeResources]::SizeofResource($module, $resource)
        $data = [SetupPeResources]::LockResource([SetupPeResources]::LoadResource($module, $resource))
        if ($data -eq [IntPtr]::Zero -or $size -eq 0) { throw 'Cannot read application manifest.' }
        $bytes = [byte[]]::new($size)
        [Runtime.InteropServices.Marshal]::Copy($data, $bytes, 0, $bytes.Length)
        $xml = [xml][Text.Encoding]::UTF8.GetString($bytes)
        $level = $xml.SelectSingleNode("//*[local-name()='requestedExecutionLevel']").GetAttribute('level')
        if ($level -ne 'requireAdministrator') { throw "Installer UAC requirement changed: $level" }
    } finally { [void][SetupPeResources]::FreeLibrary($module) }
}
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('MyUsbIP-SetupTest-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
function Expect-Code([string]$Executable, [string[]]$Arguments, [int]$Expected) {
    & $Executable @Arguments
    if ($LASTEXITCODE -ne $Expected) { throw "ExitCode=$LASTEXITCODE，期望=$Expected，参数=$Arguments" }
}
try {
    foreach ($title in @('Server','Client')) {
        $online = Join-Path $temporary "MyUsbIP-$title-Setup.exe"
        $offline = Join-Path $temporary "MyUsbIP-$title-Offline-Setup.exe"
        $zipName = "MyUsbIP-$title-win-x64.zip"
        $zip = Join-Path $OutputRoot $zipName
        Copy-Item "$OutputRoot/MyUsbIP-$title-Setup.exe" $online
        Copy-Item "$OutputRoot/MyUsbIP-$title-Offline-Setup.exe" $offline
        Verify-AdministratorManifest $online
        Verify-AdministratorManifest $offline
        Expect-Code $online @('--help') 0
        Expect-Code $online @('--mode','invalid','--quiet') 2
        Expect-Code $online @('--mode','online','--package',$zip,'--quiet') 2
        # 在线 EXE 没有内置载荷，offline 缺包必须失败，不能静默联网。
        Expect-Code $online @('--mode','offline','--verify-only','--quiet') 1
        Expect-Code $online @('--package',$zip,'--verify-only','--quiet') 0
        Expect-Code $offline @('--mode','offline','--verify-only','--quiet') 0
        Copy-Item $zip "$temporary/$zipName"
        Expect-Code $online @('--verify-only','--quiet') 0
        # 文件仍是可解压 ZIP，但修改一个 byte 后必须在安装前拒绝。
        $badZip = Join-Path $temporary 'bad.zip'
        Copy-Item $zip $badZip -Force
        $stream = [IO.File]::Open($badZip, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite)
        try { $stream.Position = 40; $value = $stream.ReadByte(); $stream.Position = 40; $stream.WriteByte($value -bxor 1) }
        finally { $stream.Dispose() }
        Expect-Code $online @('--package',$badZip,'--verify-only','--quiet') 1
        # 不得用内置载荷掩盖显式离线包丢失。
        Expect-Code $offline @('--package',"$temporary/missing.zip",'--verify-only','--quiet') 1
        Remove-Item "$temporary/$zipName"
    }
    Write-Host '[PASS] NativeAOT setup help, options, local/embedded offline, missing and corrupted package checks.'
} finally { Remove-Item $temporary -Recurse -Force -ErrorAction SilentlyContinue }

# 负向用例最后一次调用返回 1，但整套验证成功时必须返回 0。
$global:LASTEXITCODE = 0
