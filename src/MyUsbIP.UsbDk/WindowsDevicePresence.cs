using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace MyUsbIP.UsbDk;

/// <summary>
/// Windows USB 设备物理存在性检测。
///
/// UsbDk 的 InstanceId 对部分设备（例如 CH340）只返回端口片段，例如 "4"，
/// 它并不是可以直接传给 Configuration Manager 的完整 PnP InstanceId。
/// 因此这里通过 SetupAPI 枚举当前真正 PRESENT 的 USB DevNode，再使用 DeviceId +
/// UsbDk InstanceId/端口片段进行匹配，避免把 Redirect 快照永久当成在线设备。
/// </summary>
internal static class WindowsDevicePresence
{
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfAllClasses = 0x00000004;
    private const int ErrorNoMoreItems = 259;
    private static readonly nint InvalidHandleValue = new(-1);

    /// <summary>一次性取得当前 Windows PnP 中真正存在的 USB 设备实例，供一次 DEVLIST 批量判断复用。</summary>
    public static IReadOnlyList<string> EnumeratePresentUsbInstanceIds()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<string>();

        var infoSet = SetupDiGetClassDevsW(0, "USB", 0, DigcfPresent | DigcfAllClasses);
        if (infoSet == 0 || infoSet == InvalidHandleValue) return Array.Empty<string>();

        try
        {
            var result = new List<string>();
            for (uint index = 0; ; index++)
            {
                var info = new SpDevInfoData { CbSize = (uint)Marshal.SizeOf<SpDevInfoData>() };
                if (!SetupDiEnumDeviceInfo(infoSet, index, ref info))
                {
                    if (Marshal.GetLastWin32Error() == ErrorNoMoreItems) break;
                    continue;
                }

                var buffer = new StringBuilder(512);
                if (SetupDiGetDeviceInstanceIdW(infoSet, ref info, buffer, (uint)buffer.Capacity, out var required))
                {
                    if (buffer.Length > 0) result.Add(buffer.ToString());
                    continue;
                }

                // 极少数实例 ID 超过默认缓冲区时按 SetupAPI 返回长度重试。
                if (required <= buffer.Capacity) continue;
                buffer = new StringBuilder(checked((int)required + 1));
                if (SetupDiGetDeviceInstanceIdW(infoSet, ref info, buffer, (uint)buffer.Capacity, out _) && buffer.Length > 0)
                    result.Add(buffer.ToString());
            }

            return result;
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(infoSet);
        }
    }

    /// <summary>
    /// 判断 UsbDk 快照对应的设备是否仍然物理存在。
    /// DeviceId 例如 USB\VID_1A86&amp;PID_7523，InstanceId 可能仅为端口号 "4"。
    /// </summary>
    public static bool IsPresent(string? deviceId, string? instanceId, IReadOnlyList<string> presentUsbInstanceIds)
        => FindMatchingInstances(deviceId, instanceId, presentUsbInstanceIds).Length > 0;

    public static string? FindPresentInstanceId(
        string? deviceId,
        string? instanceId,
        IReadOnlyList<string> presentUsbInstanceIds)
    {
        var matches = FindMatchingInstances(deviceId, instanceId, presentUsbInstanceIds);
        // 多个 Hub 上的 CH340 都可能返回短实例 "4"，绝不能选择第一个并重启错误设备。
        return matches.Length == 1 ? matches[0] : null;
    }

    private static string[] FindMatchingInstances(string? deviceId, string? instanceId, IReadOnlyList<string> presentUsbInstanceIds)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return [];

        var device = deviceId.Trim().TrimEnd('\\');
        var instance = instanceId?.Trim().Trim('\\');
        var prefix = device + "\\";

        var candidates = presentUsbInstanceIds
            .Where(x => string.Equals(x, device, StringComparison.OrdinalIgnoreCase) ||
                        x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        if (candidates.Length == 0 || string.IsNullOrWhiteSpace(instance)) return candidates;
        if (instance.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return candidates.Where(x => string.Equals(x, instance, StringComparison.OrdinalIgnoreCase)).ToArray();

        // 有真实序列号的设备：UsbDk InstanceId 往往就是完整实例尾段。
        var exact = candidates.Where(x => x.Length > prefix.Length && string.Equals(x[prefix.Length..], instance, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (exact.Length > 0) return exact;

        // 无序列号的 USB 设备（CH340 很常见）Windows 实例尾段通常形如 6&xxxx&0&4，
        // 而 UsbDk 只给出 "4"。按最后一级端口号匹配，而不是把 "4" 当完整 PnP ID。
        if (uint.TryParse(instance, out _))
        {
            return candidates.Where(x => x.Length > prefix.Length && x[prefix.Length..].EndsWith("&" + instance, StringComparison.OrdinalIgnoreCase)).ToArray();
        }

        return [];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public uint CbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public nuint Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SetupDiGetClassDevsW(
        nint classGuid,
        string? enumerator,
        nint hwndParent,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(nint deviceInfoSet, uint memberIndex, ref SpDevInfoData deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceIdW(
        nint deviceInfoSet,
        ref SpDevInfoData deviceInfoData,
        StringBuilder deviceInstanceId,
        uint deviceInstanceIdSize,
        out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(nint deviceInfoSet);
}
