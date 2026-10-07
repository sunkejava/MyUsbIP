using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MyUsbIP.UsbDk;

/// <summary>捕获前保存并核验物理 Hub/端口；叶子 DevNode 消失后只恢复该端口。</summary>
internal static partial class WindowsUsbHubPortRecovery
{
    private const uint CmDrpDriver = 0x0A;
    private const uint CmDrpAddress = 0x1D;
    private const uint IoctlDriverKey = 0x220420;
    private const uint IoctlCyclePort = 0x220444;
    private static readonly Guid HubInterface = new("F18A0E88-C30C-11D0-8815-00A0C906BED8");

    internal sealed record Topology(string HubPath, string HubInstanceId, uint ConnectionIndex, string DriverKey);

    internal static bool CanCycle(Topology? topology)
        => topology is { ConnectionIndex: > 0 and <= 255 }
           && !string.IsNullOrWhiteSpace(topology.HubPath)
           && !string.IsNullOrWhiteSpace(topology.HubInstanceId)
           && !string.IsNullOrWhiteSpace(topology.DriverKey);

    internal static bool CanCycleCurrentPort(string savedKey, string? currentKey, bool confirmedEmpty)
        => !string.IsNullOrWhiteSpace(savedKey) &&
           (string.Equals(savedKey, currentKey, StringComparison.OrdinalIgnoreCase) ||
            (currentKey is null && confirmedEmpty));

    internal static Topology? Capture(uint deviceNode, string parentId)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var address = ReadProperty(deviceNode, CmDrpAddress, 4);
        var driver = ReadProperty(deviceNode, CmDrpDriver, 1);
        if (address is not { Length: 4 } || driver is null) return null;
        var port = BinaryPrimitives.ReadUInt32LittleEndian(address);
        var key = Encoding.Unicode.GetString(driver).TrimEnd('\0');
        if (port is 0 or > 255 || string.IsNullOrWhiteSpace(key)) return null;

        var guid = HubInterface;
        if (CM_Get_Device_Interface_List_SizeW(out var length, in guid, parentId, 0) != 0 || length is 0 or > 32768)
            return null;
        var buffer = Marshal.AllocHGlobal(checked((int)length * 2));
        try
        {
            if (CM_Get_Device_Interface_ListW(in guid, parentId, buffer, length, 0) != 0) return null;
            var paths = Marshal.PtrToStringUni(buffer, checked((int)length))!.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            foreach (var path in paths)
            {
                using var hub = OpenHub(path);
                if (hub.IsInvalid) continue;
                var portKey = ReadPortDriverKey(hub, port);
                // 地址必须与该 Hub 端口上的实际驱动键一致，不能猜测 UsbDk Port/短 InstanceId。
                if (string.Equals(portKey, key, StringComparison.OrdinalIgnoreCase))
                    return new(path, parentId, port, key);
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return null;
    }

    internal static bool TryCycle(Topology? topology, out string detail)
    {
        if (!OperatingSystem.IsWindows() || !CanCycle(topology))
        {
            detail = "未保存经过驱动键核验的目标 Hub 端口，拒绝猜测端口或重启整个 Hub";
            return false;
        }
        using var hub = OpenHub(topology!.HubPath);
        if (hub.IsInvalid)
        {
            detail = $"打开目标 Hub 失败，Win32={Marshal.GetLastPInvokeError()}";
            return false;
        }
        var currentKey = ReadPortDriverKey(hub, topology.ConnectionIndex);
        // 查键失败不能等同于空端口；必须另行确认 NoDeviceConnected 才允许恢复消失的原设备。
        if (!CanCycleCurrentPort(topology.DriverKey, currentKey,
                currentKey is null && IsPortEmpty(hub, topology.ConnectionIndex)))
        {
            detail = "目标端口当前驱动键已变化，拒绝恢复其他设备";
            return false;
        }
        var parameters = new CyclePortParameters { ConnectionIndex = topology.ConnectionIndex };
        var ok = CyclePort(hub, IoctlCyclePort, ref parameters, 8, 0, 0, out _, 0);
        var error = ok ? 0 : Marshal.GetLastPInvokeError();
        detail = $"IOCTL_USB_HUB_CYCLE_PORT Hub={topology.HubInstanceId}, Port={topology.ConnectionIndex}, Win32={error}";
        return ok;
    }

    internal static string? ParseDriverKey(byte[] buffer)
    {
        if (buffer.Length < 10) return null;
        var length = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(4));
        if (length < 10 || length > buffer.Length || (length & 1) != 0) return null;
        var text = Encoding.Unicode.GetString(buffer, 8, checked((int)length - 8));
        var end = text.IndexOf('\0');
        return end <= 0 || string.IsNullOrWhiteSpace(text[..end]) ? null : text[..end];
    }

    private static bool IsPortEmpty(SafeFileHandle hub, uint port)
    {
        // usbioctl.h 使用 pack(1)：ConnectionStatus 在第 31 字节，固定头长度为 35。
        const int size = 35;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, buffer, size);
            Marshal.WriteInt32(buffer, unchecked((int)port));
            return DeviceIoControl(hub, 0x220448, buffer, size, buffer, size, out var returned, 0)
                   && returned >= size && Marshal.ReadInt32(buffer, 31) == 0;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static byte[]? ReadProperty(uint node, uint property, uint expectedType)
    {
        uint length = 0;
        var result = CM_Get_DevNode_Registry_PropertyW(node, property, out _, 0, ref length, 0);
        if ((result != 0 && result != 0x1A) || length is 0 or > 65536) return null;
        var size = length;
        var buffer = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            if (CM_Get_DevNode_Registry_PropertyW(node, property, out var type, buffer, ref length, 0) != 0
                || type != expectedType || length > size) return null;
            var bytes = new byte[length];
            Marshal.Copy(buffer, bytes, 0, bytes.Length);
            return bytes;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string? ReadPortDriverKey(SafeFileHandle hub, uint port)
    {
        var buffer = Marshal.AllocHGlobal(12);
        try
        {
            Marshal.WriteInt32(buffer, unchecked((int)port));
            Marshal.WriteInt32(buffer, 4, 0);
            Marshal.WriteInt32(buffer, 8, 0);
            if (!DeviceIoControl(hub, IoctlDriverKey, buffer, 12, buffer, 12, out _, 0)) return null;
            var length = unchecked((uint)Marshal.ReadInt32(buffer, 4));
            if (length is < 10 or > 65536) return null;
            Marshal.FreeHGlobal(buffer);
            buffer = 0;
            buffer = Marshal.AllocHGlobal(checked((int)length));
            Marshal.WriteInt32(buffer, unchecked((int)port));
            Marshal.WriteInt32(buffer, 4, 0);
            var size = length;
            if (!DeviceIoControl(hub, IoctlDriverKey, buffer, size, buffer, size, out var returned, 0)
                || returned < 10 || returned > size) return null;
            var bytes = new byte[returned];
            Marshal.Copy(buffer, bytes, 0, bytes.Length);
            return ParseDriverKey(bytes);
        }
        finally { if (buffer != 0) Marshal.FreeHGlobal(buffer); }
    }

    private static SafeFileHandle OpenHub(string path) => CreateFileW(path, 0x40000000, 3, 0, 3, 0, 0);

    [StructLayout(LayoutKind.Sequential)]
    private struct CyclePortParameters { public uint ConnectionIndex; public uint StatusReturned; }

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Get_Device_Interface_List_SizeW(out uint length, in Guid interfaceClass, string deviceId, uint flags);
    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Get_Device_Interface_ListW(in Guid interfaceClass, string deviceId, nint buffer, uint length, uint flags);
    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Get_DevNode_Registry_PropertyW(uint node, uint property, out uint type, nint buffer, ref uint length, uint flags);
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint creation, uint attributes, nint template);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle handle, uint code, nint input, uint inputLength, nint output, uint outputLength, out uint returned, nint overlapped);
    [LibraryImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CyclePort(SafeFileHandle handle, uint code, ref CyclePortParameters input, uint inputLength, nint output, uint outputLength, out uint returned, nint overlapped);
}
