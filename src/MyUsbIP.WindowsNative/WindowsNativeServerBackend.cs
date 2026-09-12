using System.Buffers.Binary;
using MyUsbIP.Abstractions;

namespace MyUsbIP.WindowsNative;

/// <summary>
/// Windows 原生服务端后端。
/// 只依赖 MyUsbIP.Exporter.sys，不需要安装 usbipd-win、usbip.exe 或第三方 USB/IP 服务。
/// </summary>
public sealed class WindowsNativeServerBackend : IUsbIpServerBackend
{
    private const int DeviceRecordSize = 256;
    private readonly string devicePath;

    public WindowsNativeServerBackend(string? devicePath = null)
        => this.devicePath = devicePath ?? DriverControlProtocol.ExporterDevicePath;

    public Task<UsbIpBackendCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var device = new KernelDeviceClient(devicePath);
        var version = device.GetVersion(DriverControlProtocol.IoctlExporterGetVersion);
        return Task.FromResult(new UsbIpBackendCapabilities(
            CanList: true,
            CanShare: true,
            CanAttach: false,
            CanDetach: false,
            BackendName: "MyUsbIP Windows Native Exporter",
            BackendVersion: FormatVersion(version)));
    }

    public Task<IReadOnlyList<UsbIpDeviceInfo>> ListDevicesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var device = new KernelDeviceClient(devicePath);
        var data = device.Ioctl(DriverControlProtocol.IoctlExporterListDevices, ReadOnlySpan<byte>.Empty, 1024 * 1024);
        return Task.FromResult(ParseDeviceList(data));
    }

    public Task ShareAsync(string busId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var device = new KernelDeviceClient(devicePath);
        device.Ioctl(DriverControlProtocol.IoctlExporterShare, DriverControlProtocol.BuildBusIdRequest(busId));
        return Task.CompletedTask;
    }

    public Task UnshareAsync(string busId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var device = new KernelDeviceClient(devicePath);
        device.Ioctl(DriverControlProtocol.IoctlExporterUnshare, DriverControlProtocol.BuildBusIdRequest(busId));
        return Task.CompletedTask;
    }

    internal static IReadOnlyList<UsbIpDeviceInfo> ParseDeviceList(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8) return Array.Empty<UsbIpDeviceInfo>();
        var version = BinaryPrimitives.ReadUInt32LittleEndian(data[..4]);
        if (version != DriverControlProtocol.ApiVersion)
            throw new InvalidDataException($"Exporter ABI 版本不匹配：0x{version:X8}。 ");

        var count = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, 4));
        if (count > 4096) throw new InvalidDataException("Exporter 返回的设备数量异常。 ");

        var result = new List<UsbIpDeviceInfo>((int)count);
        var offset = 8;
        for (var i = 0u; i < count; i++)
        {
            if (offset + DeviceRecordSize > data.Length) throw new InvalidDataException("Exporter 设备列表数据被截断。 ");
            var row = data.Slice(offset, DeviceRecordSize);
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(196, 4));
            result.Add(new UsbIpDeviceInfo
            {
                BusId = DriverControlProtocol.ReadFixedUtf8(row[..64]),
                InstanceId = DriverControlProtocol.ReadFixedUtf8(row.Slice(64, 128)),
                VendorId = BinaryPrimitives.ReadUInt16LittleEndian(row.Slice(192, 2)),
                ProductId = BinaryPrimitives.ReadUInt16LittleEndian(row.Slice(194, 2)),
                State = (flags & 1) != 0 ? UsbIpDeviceState.Shared : UsbIpDeviceState.Available,
            });
            offset += DeviceRecordSize;
        }
        return result;
    }

    private static string FormatVersion(uint version)
        => $"{(version >> 16) & 0xFFFF}.{version & 0xFFFF}";
}
