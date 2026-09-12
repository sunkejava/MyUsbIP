using System.Buffers.Binary;
using System.Text.Encodings.Web;
using System.Text.Json;
using MyUsbIP.Abstractions;

namespace MyUsbIP.Protocol;

/// <summary>
/// MyUsbIP 私有管理扩展：查询设备完整元数据及当前连接客户端。
/// 标准 USB/IP DEVLIST 不包含“谁正在占用设备”等运行态信息，因此单独提供本控制面扩展。
/// 不影响第三方标准 USB/IP 客户端使用 DEVLIST/IMPORT/SUBMIT/UNLINK。
/// </summary>
public static class UsbDeviceStatusProtocol
{
    public const ushort OpReqDeviceStatus = 0x80F1;
    public const ushort OpRepDeviceStatus = 0x00F1;
    public const int MaxPayloadLength = 4 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static ValueTask WriteRequestAsync(Stream stream, CancellationToken cancellationToken = default)
        => UsbIpCodec.WriteOperationHeaderAsync(stream,
            new UsbIpOperationHeader(UsbIpProtocolConstants.Version, OpReqDeviceStatus, 0), cancellationToken);

    public static async ValueTask WriteReplyAsync(Stream stream, IReadOnlyList<UsbIpDeviceInfo> devices,
        CancellationToken cancellationToken = default)
    {
        await UsbIpCodec.WriteOperationHeaderAsync(stream,
            new UsbIpOperationHeader(UsbIpProtocolConstants.Version, OpRepDeviceStatus, 0), cancellationToken)
            .ConfigureAwait(false);

        var payload = JsonSerializer.SerializeToUtf8Bytes(devices, JsonOptions);
        if (payload.Length > MaxPayloadLength)
            throw new InvalidDataException("设备状态数据超过安全限制。 ");

        var length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)payload.Length));
        await stream.WriteAsync(length, cancellationToken).ConfigureAwait(false);
        if (payload.Length > 0)
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<IReadOnlyList<UsbIpDeviceInfo>> ReadReplyAsync(Stream stream,
        CancellationToken cancellationToken = default)
    {
        var header = await UsbIpCodec.ReadOperationHeaderAsync(stream, cancellationToken).ConfigureAwait(false);
        if (header.Code != OpRepDeviceStatus || header.Status != 0)
            throw new InvalidDataException($"设备状态查询失败：Code=0x{header.Code:X4}, Status={header.Status}。 ");

        var lengthBytes = new byte[4];
        await UsbIpCodec.ReadExactlyAsync(stream, lengthBytes, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt32BigEndian(lengthBytes);
        if (length > MaxPayloadLength)
            throw new InvalidDataException("设备状态数据超过安全限制。 ");
        if (length == 0) return Array.Empty<UsbIpDeviceInfo>();

        var payload = new byte[checked((int)length)];
        await UsbIpCodec.ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<List<UsbIpDeviceInfo>>(payload, JsonOptions) ?? [];
    }
}
