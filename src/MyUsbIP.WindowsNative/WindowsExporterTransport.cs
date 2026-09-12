using System.Buffers.Binary;
using MyUsbIP.Abstractions;
using MyUsbIP.NativeServer;
using MyUsbIP.Protocol;

namespace MyUsbIP.WindowsNative;

/// <summary>
/// 标准 USB/IP 服务与 MyUsbIP.Exporter.sys 之间的桥接层。
/// </summary>
public sealed class WindowsExporterTransport : IUsbIpExportTransport, IUsbDescriptorProvider
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

    public Task<UsbDescriptorSet> GetDescriptorSetAsync(string busId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var device = new KernelDeviceClient(devicePath);
        var output = device.Ioctl(
            DriverControlProtocol.IoctlExporterGetDescriptors,
            DriverControlProtocol.BuildBusIdRequest(busId),
            UsbDescriptorControlProtocol.MaxDescriptorBlobLength + 64);
        return Task.FromResult(ParseDescriptorSet(output));
    }

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
        var outputSize = Math.Max(64, Math.Max(0, request.TransferBufferLength) + 64);
        var output = device.Ioctl(DriverControlProtocol.IoctlExporterSubmitUrb, input, outputSize);
        return Task.FromResult(ParseCompletion(request, output));
    }

    private static UsbDescriptorSet ParseDescriptorSet(ReadOnlySpan<byte> data)
    {
        if (data.Length < 20) throw new InvalidDataException("Exporter 描述符响应长度不足。 ");
        var version = BinaryPrimitives.ReadUInt32LittleEndian(data[..4]);
        if (version != DriverControlProtocol.ApiVersion) throw new InvalidDataException("Exporter 描述符 ABI 版本不匹配。 ");

        var deviceLength = ReadLength(4);
        var configLength = ReadLength(8);
        var bosLength = ReadLength(12);
        var stringsLength = ReadLength(16);
        var total = checked(deviceLength + configLength + bosLength + stringsLength);
        if (total > data.Length - 20) throw new InvalidDataException("Exporter 描述符数据被截断。 ");

        var offset = 20;
        var deviceDescriptor = Take(deviceLength);
        var configDescriptor = Take(configLength);
        var bosDescriptor = Take(bosLength);
        var strings = Take(stringsLength);
        return new UsbDescriptorSet(deviceDescriptor, configDescriptor, bosDescriptor, strings);

        int ReadLength(int offsetValue)
        {
            var value = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offsetValue, 4));
            if (value > UsbDescriptorControlProtocol.MaxDescriptorBlobLength) throw new InvalidDataException("描述符长度超过限制。 ");
            return checked((int)value);
        }

        byte[] Take(int length)
        {
            var result = data.Slice(offset, length).ToArray();
            offset += length;
            return result;
        }
    }

    private static byte[] BuildSubmitRequest(UsbIpSubmitRequest request)
    {
        var payloadLength = request.Payload.Length;
        var buffer = new byte[44 + payloadLength];
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
        if (payloadLength > (uint)(data.Length - 24)) throw new InvalidDataException("Exporter URB Payload 长度异常。 ");
        var payload = data.Slice(24, checked((int)payloadLength)).ToArray();
        return new UsbIpSubmitCompletion(sequence, request.DeviceId, request.Direction, request.Endpoint, status, actualLength, request.StartFrame, request.NumberOfPackets, checked((int)errorCount), payload);
    }
}
