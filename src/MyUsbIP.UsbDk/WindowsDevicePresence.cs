using System.Runtime.InteropServices;

namespace MyUsbIP.UsbDk;

/// <summary>
/// 使用 Configuration Manager 判断设备实例当前是否仍然物理存在。
/// UsbDk 的 DeviceId 与 InstanceId 是分开的，例如 DeviceId=USB\VID_1A86&PID_7523、InstanceId=4；
/// Windows Configuration Manager 需要完整的 PnP InstanceId：USB\VID_1A86&PID_7523\4。
/// </summary>
internal static class WindowsDevicePresence
{
    private const uint CrSuccess = 0;

    public static bool IsPresent(string? deviceId, string? instanceId)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var fullInstanceId = BuildFullInstanceId(deviceId, instanceId);
        if (string.IsNullOrWhiteSpace(fullInstanceId)) return false;
        return CM_Locate_DevNodeW(out _, fullInstanceId, 0) == CrSuccess;
    }

    /// <summary>将 UsbDk 分离的 DeviceId / InstanceId 还原为 Windows PnP InstanceId。</summary>
    public static string? BuildFullInstanceId(string? deviceId, string? instanceId)
    {
        var device = deviceId?.Trim().TrimEnd('\\');
        var instance = instanceId?.Trim().TrimStart('\\');

        if (string.IsNullOrWhiteSpace(instance)) return string.IsNullOrWhiteSpace(device) ? null : device;
        if (instance.Contains('\\')) return instance;
        if (string.IsNullOrWhiteSpace(device)) return instance;
        return $"{device}\\{instance}";
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);
}
