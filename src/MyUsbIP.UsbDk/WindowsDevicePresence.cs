using System.Runtime.InteropServices;

namespace MyUsbIP.UsbDk;

/// <summary>
/// 使用 Configuration Manager 判断设备实例当前是否仍然物理存在。
/// CM_Locate_DevNodeW 在 flags=0 时只定位当前存在的设备节点；已拔出的 phantom 设备不会被视为存在。
/// </summary>
internal static class WindowsDevicePresence
{
    private const uint CrSuccess = 0;

    public static bool IsPresent(string? instanceId)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(instanceId)) return false;
        return CM_Locate_DevNodeW(out _, instanceId, 0) == CrSuccess;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);
}
