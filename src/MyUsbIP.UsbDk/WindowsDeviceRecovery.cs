using System.Runtime.InteropServices;

namespace MyUsbIP.UsbDk;

/// <summary>
/// Windows PnP 设备恢复辅助。
/// 仅在 UsbDk_StartRedirect 首次失败且目标为已知 CH340 时使用，
/// 通过 Configuration Manager 对目标设备执行一次禁用/启用，让 UsbDk 与原厂串口驱动重新完成 PnP 绑定。
/// </summary>
internal static class WindowsDeviceRecovery
{
    private const uint CrSuccess = 0;

    public static bool TryRestart(string? deviceId, string? instanceId, out string? fullInstanceId)
    {
        fullInstanceId = WindowsDevicePresence.BuildFullInstanceId(deviceId, instanceId);
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(fullInstanceId)) return false;

        if (CM_Locate_DevNodeW(out var devInst, fullInstanceId, 0) != CrSuccess)
            return false;

        var disabled = CM_Disable_DevNode(devInst, 0) == CrSuccess;
        if (!disabled) return false;

        Thread.Sleep(350);
        var enabled = CM_Enable_DevNode(devInst, 0) == CrSuccess;
        return enabled;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Disable_DevNode(uint devInst, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Enable_DevNode(uint devInst, uint flags);
}
