using System.Buffers.Binary;
using MyUsbIP.Abstractions;
using MyUsbIP.NativeServer;
using MyUsbIP.Protocol;

namespace MyUsbIP.WindowsNative;

/// <summary>
/// 标准 USB/IP 服务与 MyUsbIP.Exporter.sys 之间的桥接层。
/// </summary>
public sealed class WindowsExporterTransport : IUsbIpExportTransport
{
    private readonly WindowsNativeServerBackend backend;
    private readonly string devicePath;

    public WindowsExporterTransport(string? devicePath = null)
    {
        this.devicePath = devicePath ?? DriverControlProtocol.ExporterDevicePath;
        backend = new WindowsNativeServerBackend(this.devicePath);
    }

    public Task<IReadOnlyList<UsbIpDeviceInfo>> ListAsync(CancellationToken cancellationToken = default)
        => backend.ListDevicesAsync(cancellationToken);

    public async Task<UsbIpDeviceInfo?> FindAsync(string busId, CancellationToken cancellationToken = default)
        => (await backend.ListDevicesAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(x => string.Equals(x.BusId, busId, StringComparison.OrdinalIgnoreCase));

    public Task BeginSessionAsync(string busId, CancellationToken cancellationToken = default)
        => backend.ShareAsync(busId, cancellationToken);

    public Task EndSessionAsync(string busId, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task CancelAsync(string busId, uint sequence, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var device = new KernelDeviceClient(devicePath);
        Span<byte> request = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(request[..4], DriverControlProtocol.ApiVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(request[4..8], sequence);
        device.Ioctl(DriverControlProtocol.IoctlExporterCancelUrb, request);
        return Task.CompletedTask;
    }

    public Task<UsbIpSubmitCompletion> SubmitAsync(string busId, UsbIpSubmitRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var input = BuildSubmitRequest(request);
        using var device = new KernelDeviceClient(devicePath);
        var output = device.Ioctl(DriverControlProtocol.IoctlExporterSubmitUrb, input, Math.Max(64, request.TransferBufferLength + 64));
        return Task.FromResult(ParseCompletion(request, output));
    }

    private static byte[] BuildSubmitRequest(UsbIpSubmitRequest request)
    {
        var payloadLength = request.Payload.Length;
        var buffer = new byte[48 + payloadLength];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), DriverControlProtocol.ApiVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4, 4), request.Sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8, 4), request.DeviceId);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(12, 4), request.Endpoint);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(16, 4), request.Direction);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(20, 4), request.TransferFlags);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(24, 4), request.TransferBufferLength);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(28, 4), request.Interval);
        request.SetupPacket.AsSpan(0, Math.Min(8, request.SetupPacket.Length)).CopyTo(buffer.AsSpan(32, 8));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(40, 4), checked((uint)payloadLength));
        if (payloadLength > 0) request.Payload.CopyTo(buffer, 44);
        return buffer;
    }

    private static UsbIpSubmitCompletion ParseCompletion(UsbIpSubmitRequest request, ReadOnlySpan<byte> data)
    {
        if (data.Length < 24) throw new InvalidDataException("Exporter URB 完成数据长度不足。 ");
        var version = BinaryPrimitives.ReadUInt32LittleEndian(data[..4]);
        if (version != DriverControlProtocol.ApiVersion) throw new InvalidDataException("Exporter URB ABI 版本不匹配。 ");
        var sequence = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, 4));
        var status = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(8, 4));
        var actualLength = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(12, 4));
        var errorCount = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(16, 4));
        var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(20, 4));
        if (payloadLength > data.Length - 24) throw new InvalidDataException("Exporter URB Payload 长度异常。 ");
        var payload = data.Slice(24, checked((int)payloadLength)).ToArray();
        return new UsbIpSubmitCompletion(sequence, request.DeviceId, request.Direction, request.Endpoint, status, actualLength, request.StartFrame, request.NumberOfPackets, checked((int)errorCount), payload);
    }
}
