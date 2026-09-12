using System.Buffers.Binary;
using System.Text;
using MyUsbIP.Protocol;

namespace MyUsbIP.WindowsNative;

/// <summary>
/// MyUsbIP 自研 Windows 驱动与 .NET 服务之间的控制协议。
/// 用户态只与设备接口交互，不直接依赖任何 usbipd-win/usbip-win API。
/// </summary>
public static class DriverControlProtocol
{
    public const string ExporterDevicePath = @"\\.\MyUsbIPExporter";
    public const string VhciDevicePath = @"\\.\MyUsbIPVhci";

    // FILE_DEVICE_UNKNOWN = 0x22，METHOD_BUFFERED = 0，FILE_ANY_ACCESS = 0。
    public static uint CtlCode(uint function) => (0x22u << 16) | (function << 2);

    public static readonly uint IoctlExporterGetVersion = CtlCode(0x800);
    public static readonly uint IoctlExporterListDevices = CtlCode(0x801);
    public static readonly uint IoctlExporterShare = CtlCode(0x802);
    public static readonly uint IoctlExporterUnshare = CtlCode(0x803);
    public static readonly uint IoctlExporterSubmitUrb = CtlCode(0x804);
    public static readonly uint IoctlExporterCancelUrb = CtlCode(0x805);
    public static readonly uint IoctlExporterGetCompletion = CtlCode(0x806);
    public static readonly uint IoctlExporterGetDescriptors = CtlCode(0x807);

    public static readonly uint IoctlVhciGetVersion = CtlCode(0x900);
    public static readonly uint IoctlVhciCreatePort = CtlCode(0x901);
    public static readonly uint IoctlVhciRemovePort = CtlCode(0x902);
    public static readonly uint IoctlVhciGetPendingUrb = CtlCode(0x903);
    public static readonly uint IoctlVhciCompleteUrb = CtlCode(0x904);
    public static readonly uint IoctlVhciResetPort = CtlCode(0x905);

    public const uint ApiVersion = 0x0001_0000;

    /// <summary>将 UTF-8 字符串写入固定长度字段。</summary>
    public static void WriteFixedUtf8(Span<byte> destination, string? value)
    {
        destination.Clear();
        if (string.IsNullOrEmpty(value)) return;
        var count = Encoding.UTF8.GetByteCount(value);
        if (count >= destination.Length) count = destination.Length - 1;
        Encoding.UTF8.GetBytes(value.AsSpan(), destination[..count]);
    }

    /// <summary>读取驱动返回的固定长度 UTF-8 字段。</summary>
    public static string ReadFixedUtf8(ReadOnlySpan<byte> source)
    {
        var end = source.IndexOf((byte)0);
        if (end < 0) end = source.Length;
        return Encoding.UTF8.GetString(source[..end]);
    }

    public static byte[] BuildBusIdRequest(string busId)
    {
        var buffer = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), ApiVersion);
        WriteFixedUtf8(buffer.AsSpan(4), busId);
        return buffer;
    }

    /// <summary>构造 UdeCx 创建虚拟 USB 设备请求。</summary>
    public static byte[] BuildVhciCreatePortRequest(uint deviceId, string busId, UsbDescriptorSet descriptors)
    {
        const int headerSize = 96;
        var total = checked(headerSize
                            + descriptors.DeviceDescriptor.Length
                            + descriptors.ConfigurationDescriptor.Length
                            + descriptors.BosDescriptor.Length
                            + descriptors.StringDescriptorBlob.Length);
        var buffer = new byte[total];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), ApiVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4, 4), deviceId);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(12, 4), checked((uint)descriptors.DeviceDescriptor.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(16, 4), checked((uint)descriptors.ConfigurationDescriptor.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(20, 4), checked((uint)descriptors.BosDescriptor.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(24, 4), checked((uint)descriptors.StringDescriptorBlob.Length));
        WriteFixedUtf8(buffer.AsSpan(28, 64), busId);
        var offset = headerSize;
        Copy(descriptors.DeviceDescriptor);
        Copy(descriptors.ConfigurationDescriptor);
        Copy(descriptors.BosDescriptor);
        Copy(descriptors.StringDescriptorBlob);
        return buffer;

        void Copy(byte[] data)
        {
            data.CopyTo(buffer, offset);
            offset += data.Length;
        }
    }
}
