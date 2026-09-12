using System.Buffers.Binary;

namespace MyUsbIP.Protocol;

/// <summary>
/// MyUsbIP 对标准 USB/IP 的私有控制面扩展。
/// UdeCx 在创建虚拟设备前需要完整描述符，因此客户端先通过本扩展预取描述符，
/// 随后的真实 USB 传输仍使用标准 USB/IP SUBMIT/UNLINK 数据面。
/// </summary>
public static class UsbDescriptorControlProtocol
{
    public const ushort OpReqDescriptors = 0x80F0;
    public const ushort OpRepDescriptors = 0x00F0;
    public const int MaxDescriptorBlobLength = 1024 * 1024;

    public static async ValueTask WriteRequestAsync(Stream stream, string busId, CancellationToken cancellationToken = default)
    {
        await UsbIpCodec.WriteOperationHeaderAsync(stream,
            new UsbIpOperationHeader(UsbIpProtocolConstants.Version, OpReqDescriptors, 0), cancellationToken);
        await UsbIpCodec.WriteBusIdAsync(stream, busId, cancellationToken);
    }

    public static async ValueTask WriteReplyAsync(Stream stream, UsbDescriptorSet descriptors, CancellationToken cancellationToken = default)
    {
        await UsbIpCodec.WriteOperationHeaderAsync(stream,
            new UsbIpOperationHeader(UsbIpProtocolConstants.Version, OpRepDescriptors, 0), cancellationToken);

        var lengths = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(lengths.AsSpan(0, 4), checked((uint)descriptors.DeviceDescriptor.Length));
        BinaryPrimitives.WriteUInt32BigEndian(lengths.AsSpan(4, 4), checked((uint)descriptors.ConfigurationDescriptor.Length));
        BinaryPrimitives.WriteUInt32BigEndian(lengths.AsSpan(8, 4), checked((uint)descriptors.BosDescriptor.Length));
        BinaryPrimitives.WriteUInt32BigEndian(lengths.AsSpan(12, 4), checked((uint)descriptors.StringDescriptorBlob.Length));
        await stream.WriteAsync(lengths, cancellationToken);
        await WriteBlobAsync(stream, descriptors.DeviceDescriptor, cancellationToken);
        await WriteBlobAsync(stream, descriptors.ConfigurationDescriptor, cancellationToken);
        await WriteBlobAsync(stream, descriptors.BosDescriptor, cancellationToken);
        await WriteBlobAsync(stream, descriptors.StringDescriptorBlob, cancellationToken);
    }

    public static async ValueTask<UsbDescriptorSet> ReadReplyAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var header = await UsbIpCodec.ReadOperationHeaderAsync(stream, cancellationToken);
        if (header.Code != OpRepDescriptors || header.Status != 0)
            throw new InvalidDataException($"描述符查询失败：Code=0x{header.Code:X4}, Status={header.Status}。 ");

        var lengths = new byte[16];
        await UsbIpCodec.ReadExactlyAsync(stream, lengths, cancellationToken);
        var device = ValidateLength(BinaryPrimitives.ReadUInt32BigEndian(lengths.AsSpan(0, 4)));
        var config = ValidateLength(BinaryPrimitives.ReadUInt32BigEndian(lengths.AsSpan(4, 4)));
        var bos = ValidateLength(BinaryPrimitives.ReadUInt32BigEndian(lengths.AsSpan(8, 4)));
        var strings = ValidateLength(BinaryPrimitives.ReadUInt32BigEndian(lengths.AsSpan(12, 4)));
        return new UsbDescriptorSet(
            await ReadBlobAsync(stream, device, cancellationToken),
            await ReadBlobAsync(stream, config, cancellationToken),
            await ReadBlobAsync(stream, bos, cancellationToken),
            await ReadBlobAsync(stream, strings, cancellationToken));
    }

    private static int ValidateLength(uint value)
    {
        if (value > MaxDescriptorBlobLength) throw new InvalidDataException("描述符数据超过安全限制。 ");
        return checked((int)value);
    }

    private static async ValueTask WriteBlobAsync(Stream stream, byte[] data, CancellationToken cancellationToken)
    {
        if (data.Length > 0) await stream.WriteAsync(data, cancellationToken);
    }

    private static async ValueTask<byte[]> ReadBlobAsync(Stream stream, int length, CancellationToken cancellationToken)
    {
        if (length == 0) return Array.Empty<byte>();
        var data = new byte[length];
        await UsbIpCodec.ReadExactlyAsync(stream, data, cancellationToken);
        return data;
    }
}

/// <summary>创建 UdeCx 虚拟设备所需的远端 USB 描述符集合。</summary>
public sealed record UsbDescriptorSet(
    byte[] DeviceDescriptor,
    byte[] ConfigurationDescriptor,
    byte[] BosDescriptor,
    byte[] StringDescriptorBlob);
