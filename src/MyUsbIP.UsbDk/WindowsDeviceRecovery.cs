using System.Runtime.InteropServices;
using System.Text;

namespace MyUsbIP.UsbDk;

/// <summary>
/// Windows PnP 设备恢复辅助。
/// UsbDk_StartRedirect 失败后，不对 USB 功能设备叶子节点自身做 Reenumerate，
/// 而是定位其父 USB Hub/总线 DevNode，让父节点重新枚举子设备。
/// </summary>
internal static class WindowsDeviceRecovery
{
    private const uint CrSuccess = 0;

    public static bool TryReenumerate(string? fullInstanceId, out string detail)
    {
        detail = string.Empty;
        if (!OperatingSystem.IsWindows())
        {
            detail = "当前系统不是 Windows";
            return false;
        }

        if (string.IsNullOrWhiteSpace(fullInstanceId))
        {
            detail = "设备 InstanceId 为空";
            return false;
        }

        var locateResult = CM_Locate_DevNodeW(out var deviceDevInst, fullInstanceId, 0);
        if (locateResult != CrSuccess)
        {
            detail = $"CM_Locate_DevNodeW 失败，CONFIGRET=0x{locateResult:X8}，InstanceId={fullInstanceId}";
            return false;
        }

        var deviceStatusResult = CM_Get_DevNode_Status(out var deviceStatus, out var deviceProblem, deviceDevInst, 0);

        var parentResult = CM_Get_Parent(out var parentDevInst, deviceDevInst, 0);
        if (parentResult != CrSuccess)
        {
            detail = $"CM_Get_Parent 失败，CONFIGRET=0x{parentResult:X8}，InstanceId={fullInstanceId}，" +
                     $"DeviceStatus={FormatStatus(deviceStatusResult, deviceStatus, deviceProblem)}";
            return false;
        }

        var parentInstanceId = GetDeviceInstanceId(parentDevInst);
        var parentStatusResult = CM_Get_DevNode_Status(out var parentStatus, out var parentProblem, parentDevInst, 0);

        // CM_Reenumerate_DevNode 的语义是让总线/父节点重新枚举它的子设备。
        // 对 USB 功能设备本身调用通常不会导致上游 Hub 重新发现该设备。
        var reenumerateResult = CM_Reenumerate_DevNode(parentDevInst, 0);
        detail = $"Device={fullInstanceId}，Parent={parentInstanceId ?? $"DEVINST:{parentDevInst}"}，" +
                 $"DeviceStatus={FormatStatus(deviceStatusResult, deviceStatus, deviceProblem)}，" +
                 $"ParentStatus={FormatStatus(parentStatusResult, parentStatus, parentProblem)}，" +
                 $"CM_Reenumerate_DevNode(parent)=0x{reenumerateResult:X8}";

        return reenumerateResult == CrSuccess;
    }

    private static string FormatStatus(uint result, uint status, uint problem)
        => result == CrSuccess
            ? $"Status=0x{status:X8},Problem={problem}"
            : $"CM_Get_DevNode_Status=0x{result:X8}";

    private static string? GetDeviceInstanceId(uint devInst)
    {
        var sizeResult = CM_Get_Device_ID_Size(out var length, devInst, 0);
        if (sizeResult != CrSuccess) return null;

        var buffer = new StringBuilder(checked((int)length + 1));
        var idResult = CM_Get_Device_IDW(devInst, buffer, (uint)buffer.Capacity, 0);
        return idResult == CrSuccess ? buffer.ToString() : null;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Parent(out uint parentDevInst, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_DevNode_Status(out uint status, out uint problemNumber, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_IDW(uint devInst, StringBuilder buffer, uint bufferLength, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Device_ID_Size(out uint length, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Reenumerate_DevNode(uint devInst, uint flags);
}
