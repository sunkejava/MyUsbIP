using System.Buffers.Binary;
using System.Text;
using MyUsbIP.Abstractions;

namespace MyUsbIP.Protocol;

/// <summary>USB/IP 协议常量（TCP 默认端口 3240）。</summary>
public static class UsbIpProtocolConstants
{
    public const ushort Version = 0x0111;
    public const int DefaultPort = 3240;
    public const ushort OpReqDevList = 0x8005;
    public const ushort OpRepDevList = 0x0005;
    public const ushort OpReqImport = 0x8003;
    public const ushort OpRepImport = 0x0003;
    public const uint StatusOk = 0;
}

/// <summary>USB/IP OP_COMMON 头。</summary>
public readonly record struct UsbIpOperationHeader(ushort Version, ushort Code, uint Status);

/// <summary>
/// USB/IP 大端序编解码工具。
/// 协议层只处理网络字节序与定长字段，不参与平台驱动控制。
/// </summary>
public static class UsbIpCodec
{
    public const int OperationHeaderSize = 8;
    public const int BusIdFieldSize = 32;

    public static async ValueTask<UsbIpOperationHeader> ReadOperationHeaderAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var buffer = new byte[OperationHeaderSize];
        await ReadExactlyAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
        UsbIpDiagnostics.BytesReceived.Add(buffer.Length);
        return new(
            BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(0, 2)),
            BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(2, 2)),
            BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(4, 4)));
    }

    public static async ValueTask WriteOperationHeaderAsync(Stream stream, UsbIpOperationHeader header, CancellationToken cancellationToken = default)
    {
        var buffer = new byte[OperationHeaderSize];
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(0, 2), header.Version);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2, 2), header.Code);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(4, 4), header.Status);
        await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        UsbIpDiagnostics.BytesSent.Add(buffer.Length);
    }

    public static async ValueTask WriteBusIdAsync(Stream stream, string busId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(busId)) throw new ArgumentException("BusId 不能为空。", nameof(busId));
        var buffer = new byte[BusIdFieldSize];
        var written = Encoding.ASCII.GetBytes(busId.AsSpan(), buffer);
        if (written >= BusIdFieldSize) throw new ArgumentOutOfRangeException(nameof(busId), "BusId 超过 USB/IP 协议允许的长度。 ");
        await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        UsbIpDiagnostics.BytesSent.Add(buffer.Length);
    }

    public static async ValueTask<string> ReadBusIdAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var buffer = new byte[BusIdFieldSize];
        await ReadExactlyAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
        UsbIpDiagnostics.BytesReceived.Add(buffer.Length);
        var end = Array.IndexOf(buffer, (byte)0);
        if (end < 0) end = buffer.Length;
        return Encoding.ASCII.GetString(buffer, 0, end);
    }

    public static async ValueTask ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException($"连接提前关闭，还需要读取 {buffer.Length - offset} 字节。 ");
            offset += read;
        }
    }
}
