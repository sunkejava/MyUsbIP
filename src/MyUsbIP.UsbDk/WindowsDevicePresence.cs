namespace MyUsbIP.UsbDk;

internal static class WindowsDevicePresence
{
    public static bool IsPresent(string? deviceId, string? instanceId)
        => OperatingSystem.IsWindows();

    public static string? BuildFullInstanceId(string? deviceId, string? instanceId)
        => !string.IsNullOrWhiteSpace(instanceId)
            ? instanceId.Trim()
            : string.IsNullOrWhiteSpace(deviceId) ? null : deviceId.Trim();
}
