using System.Buffers.Binary;
using System.Text;
using MyUsbIP.Abstractions;

namespace MyUsbIP.Protocol;

/// <summary>USB/IP 数据阶段命令。</summary>
public static class UsbIpDataCommands
{
    public const uint CmdSubmit = 0x00000001;
    public const uint CmdUnlink = 0x00000002;
    public const uint RetSubmit = 0x00000003;
    public const uint RetUnlink = 0x00000004;
}

/// <summary>USB/IP SUBMIT 请求。</summary>
public sealed record UsbIpSubmitRequest(
    uint Sequence,
    uint DeviceId,
    uint Direction,
    uint Endpoint,
    uint TransferFlags,
    int TransferBufferLength,
    int StartFrame,
    int NumberOfPackets,
    int Interval,
    byte[] SetupPacket,
    byte[] Payload);

/// <summary>USB/IP SUBMIT 完成结果。</summary>
public sealed record UsbIpSubmitCompletion(
    uint Sequence,
    uint DeviceId,
    uint Direction,
    uint Endpoint,
    int Status,
    int ActualLength,
    int StartFrame,
    int NumberOfPackets,
    int ErrorCount,
    byte[] Payload);

/// <summary>USB/IP UNLINK 请求。</summary>
public sealed record UsbIpUnlinkRequest(
    uint Sequence,
    uint DeviceId,
    uint Direction,
    uint Endpoint,
    uint TargetSequence);

/// <summary>
/// 标准 USB/IP 线协议辅助方法。
/// 所有整数均使用网络字节序（大端）。
/// </summary>
public static class UsbIpWire
{
    public const int DeviceWireSize = 312;
    public const int BasicHeaderSize = 20;
    public const int SubmitBodySize = 28;
    public const int UnlinkBodySize = 28;

    public static async ValueTask WriteDeviceAsync(Stream stream, UsbIpDeviceInfo device, CancellationToken cancellationToken = default)
    {
        var buffer = new byte[DeviceWireSize];
        WriteAscii(buffer.AsSpan(0, 256), device.InstanceId ?? device.Product ?? device.BusId);
        WriteAscii(buffer.AsSpan(256, 32), device.BusId);

        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(288, 4), 0);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(292, 4), 0);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(296, 4), 2);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(300, 2), device.VendorId);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(302, 2), device.ProductId);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(304, 2), 0x0100);
        buffer[306] = 0;
        buffer[307] = 0;
        buffer[308] = 0;
        buffer[309] = 1;
        buffer[310] = 1;
        buffer[311] = 0;

        await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        UsbIpDiagnostics.BytesSent.Add(buffer.Length);
    }

    public static async ValueTask WriteDevListReplyAsync(Stream stream, IReadOnlyList<UsbIpDeviceInfo> devices, CancellationToken cancellationToken = default)
    {
        await UsbIpCodec.WriteOperationHeaderAsync(stream,
            new UsbIpOperationHeader(UsbIpProtocolConstants.Version, UsbIpProtocolConstants.OpRepDevList, UsbIpProtocolConstants.StatusOk),
            cancellationToken);

        var count = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(count, checked((uint)devices.Count));
        await stream.WriteAsync(count, cancellationToken).ConfigureAwait(false);
        UsbIpDiagnostics.BytesSent.Add(4);

        foreach (var device in devices)
            await WriteDeviceAsync(stream, device, cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask WriteImportReplyAsync(Stream stream, UsbIpDeviceInfo device, CancellationToken cancellationToken = default)
    {
        await UsbIpCodec.WriteOperationHeaderAsync(stream,
            new UsbIpOperationHeader(UsbIpProtocolConstants.Version, UsbIpProtocolConstants.OpRepImport, UsbIpProtocolConstants.StatusOk),
            cancellationToken);
        await WriteDeviceAsync(stream, device, cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<UsbIpSubmitRequest> ReadSubmitAsync(Stream stream, uint sequence, uint deviceId, uint direction, uint endpoint, CancellationToken cancellationToken = default)
    {
        var body = new byte[SubmitBodySize];
        await UsbIpCodec.ReadExactlyAsync(stream, body, cancellationToken).ConfigureAwait(false);
        UsbIpDiagnostics.BytesReceived.Add(body.Length);

        var flags = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(0, 4));
        var length = BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(4, 4));
        var startFrame = BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(8, 4));
        var packets = BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(12, 4));
        var interval = BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(16, 4));
        var setup = body.AsSpan(20, 8).ToArray();

        byte[] payload = Array.Empty<byte>();
        if (direction == 0 && length > 0)
        {
            if (length > 16 * 1024 * 1024) throw new InvalidDataException("USB/IP 单次 OUT 传输长度超过安全限制。 ");
            payload = new byte[length];
            await UsbIpCodec.ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
            UsbIpDiagnostics.BytesReceived.Add(payload.Length);
        }

        return new UsbIpSubmitRequest(sequence, deviceId, direction, endpoint, flags, length, startFrame, packets, interval, setup, payload);
    }

    public static async ValueTask<UsbIpUnlinkRequest> ReadUnlinkAsync(Stream stream, uint sequence, uint deviceId, uint direction, uint endpoint, CancellationToken cancellationToken = default)
    {
        var body = new byte[UnlinkBodySize];
        await UsbIpCodec.ReadExactlyAsync(stream, body, cancellationToken).ConfigureAwait(false);
        UsbIpDiagnostics.BytesReceived.Add(body.Length);
        var targetSequence = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(0, 4));
        return new UsbIpUnlinkRequest(sequence, deviceId, direction, endpoint, targetSequence);
    }

    public static async ValueTask WriteSubmitCompletionAsync(Stream stream, UsbIpSubmitCompletion completion, CancellationToken cancellationToken = default)
    {
        var header = new byte[BasicHeaderSize + SubmitBodySize];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), UsbIpDataCommands.RetSubmit);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), completion.Sequence);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8, 4), completion.DeviceId);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12, 4), completion.Direction);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16, 4), completion.Endpoint);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(20, 4), completion.Status);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(24, 4), completion.ActualLength);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(28, 4), completion.StartFrame);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(32, 4), completion.NumberOfPackets);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(36, 4), completion.ErrorCount);

        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        UsbIpDiagnostics.BytesSent.Add(header.Length);
        if (completion.Direction != 0 && completion.Payload.Length > 0)
        {
            await stream.WriteAsync(completion.Payload, cancellationToken).ConfigureAwait(false);
            UsbIpDiagnostics.BytesSent.Add(completion.Payload.Length);
        }
    }

    public static async ValueTask WriteUnlinkCompletionAsync(Stream stream, UsbIpUnlinkRequest request, int status, CancellationToken cancellationToken = default)
    {
        var buffer = new byte[BasicHeaderSize + UnlinkBodySize];
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(0, 4), UsbIpDataCommands.RetUnlink);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(4, 4), request.Sequence);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(8, 4), request.DeviceId);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(12, 4), request.Direction);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(16, 4), request.Endpoint);
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(20, 4), status);
        await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        UsbIpDiagnostics.BytesSent.Add(buffer.Length);
    }

    public static async ValueTask<(uint Command, uint Sequence, uint DeviceId, uint Direction, uint Endpoint)> ReadBasicHeaderAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var buffer = new byte[BasicHeaderSize];
        await UsbIpCodec.ReadExactlyAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
        UsbIpDiagnostics.BytesReceived.Add(buffer.Length);
        return (
            BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(0, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(4, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(8, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(12, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(16, 4)));
    }

    private static void WriteAscii(Span<byte> destination, string value)
    {
        destination.Clear();
        var bytes = Encoding.ASCII.GetBytes(value);
        bytes.AsSpan(0, Math.Min(bytes.Length, destination.Length - 1)).CopyTo(destination);
    }
}
