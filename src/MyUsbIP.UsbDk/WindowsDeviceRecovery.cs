using System.Runtime.InteropServices;

namespace MyUsbIP.UsbDk;

/// <summary>
/// Windows PnP 设备恢复辅助。
/// UsbDk_StartRedirect 失败后仅触发安全的 PnP 重新枚举，不主动禁用/启用设备。
/// </summary>
internal static class WindowsDeviceRecovery
{
    private const uint CrSuccess = 0;

    public static bool TryReenumerate(string? fullInstanceId)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(fullInstanceId)) return false;
        if (CM_Locate_DevNodeW(out var devInst, fullInstanceId, 0) != CrSuccess) return false;
        return CM_Reenumerate_DevNode(devInst, 0) == CrSuccess;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Reenumerate_DevNode(uint devInst, uint flags);
}
