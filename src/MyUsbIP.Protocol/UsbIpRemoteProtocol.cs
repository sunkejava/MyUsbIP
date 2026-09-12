using System.Buffers.Binary;
using System.Text;
using MyUsbIP.Abstractions;

namespace MyUsbIP.Protocol;

/// <summary>
/// USB/IP 客户端侧协议辅助方法，用于 MyUsbIP 自研 Windows VHCI 用户态桥。
/// </summary>
public static class UsbIpRemoteProtocol
{
    public static async ValueTask RequestDeviceListAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        await UsbIpCodec.WriteOperationHeaderAsync(stream,
            new UsbIpOperationHeader(UsbIpProtocolConstants.Version, UsbIpProtocolConstants.OpReqDevList, 0), cancellationToken);
    }

    public static async ValueTask<IReadOnlyList<UsbIpDeviceInfo>> ReadDeviceListReplyAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var header = await UsbIpCodec.ReadOperationHeaderAsync(stream, cancellationToken).ConfigureAwait(false);
        if (header.Code != UsbIpProtocolConstants.OpRepDevList || header.Status != UsbIpProtocolConstants.StatusOk)
            throw new InvalidDataException($"DEVLIST 失败，Code=0x{header.Code:X4}, Status={header.Status}。 ");

        var countBytes = new byte[4];
        await UsbIpCodec.ReadExactlyAsync(stream, countBytes, cancellationToken).ConfigureAwait(false);
        var count = BinaryPrimitives.ReadUInt32BigEndian(countBytes);
        if (count > 4096) throw new InvalidDataException("远端返回的 USB 设备数量异常。 ");

        var list = new List<UsbIpDeviceInfo>((int)count);
        for (var i = 0u; i < count; i++)
        {
            var device = await ReadDeviceAsync(stream, cancellationToken).ConfigureAwait(false);
            list.Add(device);

            // usbip_usb_device 最后一个字段是 bNumInterfaces；标准 DEVLIST 后跟接口描述。
            // 当前 ReadDeviceAsync 将该字段返回到内部元数据中；这里重新读取原始结构不可行，
            // 所以 v1 Native 协议固定服务端 bNumInterfaces=0，避免额外接口段。
        }
        return list;
    }

    public static async ValueTask RequestImportAsync(Stream stream, string busId, CancellationToken cancellationToken = default)
    {
        await UsbIpCodec.WriteOperationHeaderAsync(stream,
            new UsbIpOperationHeader(UsbIpProtocolConstants.Version, UsbIpProtocolConstants.OpReqImport, 0), cancellationToken);
        await UsbIpCodec.WriteBusIdAsync(stream, busId, cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<UsbIpDeviceInfo> ReadImportReplyAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var header = await UsbIpCodec.ReadOperationHeaderAsync(stream, cancellationToken).ConfigureAwait(false);
        if (header.Code != UsbIpProtocolConstants.OpRepImport || header.Status != UsbIpProtocolConstants.StatusOk)
            throw new InvalidDataException($"IMPORT 失败，Code=0x{header.Code:X4}, Status={header.Status}。 ");
        return await ReadDeviceAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask WriteSubmitAsync(Stream stream, UsbIpSubmitRequest request, CancellationToken cancellationToken = default)
    {
        var header = new byte[UsbIpWire.BasicHeaderSize + UsbIpWire.SubmitBodySize];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), UsbIpDataCommands.CmdSubmit);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), request.Sequence);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8, 4), request.DeviceId);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12, 4), request.Direction);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16, 4), request.Endpoint);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20, 4), request.TransferFlags);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(24, 4), request.TransferBufferLength);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(28, 4), request.StartFrame);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(32, 4), request.NumberOfPackets);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(36, 4), request.Interval);
        request.SetupPacket.AsSpan(0, Math.Min(8, request.SetupPacket.Length)).CopyTo(header.AsSpan(40, 8));
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        UsbIpDiagnostics.BytesSent.Add(header.Length);

        if (request.Direction == 0 && request.Payload.Length > 0)
        {
            await stream.WriteAsync(request.Payload, cancellationToken).ConfigureAwait(false);
            UsbIpDiagnostics.BytesSent.Add(request.Payload.Length);
        }
    }

    public static async ValueTask<UsbIpSubmitCompletion> ReadSubmitCompletionAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var basic = await UsbIpWire.ReadBasicHeaderAsync(stream, cancellationToken).ConfigureAwait(false);
        if (basic.Command != UsbIpDataCommands.RetSubmit)
            throw new InvalidDataException($"期望 RET_SUBMIT，实际 0x{basic.Command:X8}。 ");

        var body = new byte[UsbIpWire.SubmitBodySize];
        await UsbIpCodec.ReadExactlyAsync(stream, body, cancellationToken).ConfigureAwait(false);
        var status = BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(0, 4));
        var actualLength = BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(4, 4));
        var startFrame = BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(8, 4));
        var packets = BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(12, 4));
        var errorCount = BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(16, 4));

        byte[] payload = Array.Empty<byte>();
        if (basic.Direction != 0 && actualLength > 0)
        {
            if (actualLength > 16 * 1024 * 1024) throw new InvalidDataException("RET_SUBMIT Payload 超过安全限制。 ");
            payload = new byte[actualLength];
            await UsbIpCodec.ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
            UsbIpDiagnostics.BytesReceived.Add(payload.Length);
        }

        return new UsbIpSubmitCompletion(basic.Sequence, basic.DeviceId, basic.Direction, basic.Endpoint,
            status, actualLength, startFrame, packets, errorCount, payload);
    }

    private static async ValueTask<UsbIpDeviceInfo> ReadDeviceAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[UsbIpWire.DeviceWireSize];
        await UsbIpCodec.ReadExactlyAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
        UsbIpDiagnostics.BytesReceived.Add(buffer.Length);
        return new UsbIpDeviceInfo
        {
            InstanceId = ReadAscii(buffer.AsSpan(0, 256)),
            BusId = ReadAscii(buffer.AsSpan(256, 32)),
            VendorId = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(300, 2)),
            ProductId = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(302, 2)),
            State = UsbIpDeviceState.Available,
        };
    }

    private static string ReadAscii(ReadOnlySpan<byte> source)
    {
        var end = source.IndexOf((byte)0);
        if (end < 0) end = source.Length;
        return Encoding.ASCII.GetString(source[..end]);
    }
}
