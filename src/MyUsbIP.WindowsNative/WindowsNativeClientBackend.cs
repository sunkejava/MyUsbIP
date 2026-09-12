using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Sockets;
using MyUsbIP.Abstractions;
using MyUsbIP.Protocol;

namespace MyUsbIP.WindowsNative;

/// <summary>
/// Windows 自研客户端后端。
/// 网络侧直接连接 MyUsbIP NativeServer，内核侧使用 MyUsbIP.Vhci.sys + UdeCx，
/// 不依赖 usbip.exe / usbip-win。
/// </summary>
public sealed class WindowsNativeClientBackend : IUsbIpClientBackend, IAsyncDisposable
{
    private readonly WindowsVhciController vhci;
    private readonly IUsbIpEventSink sink;
    private readonly ConcurrentDictionary<int, NativeTunnelSession> sessions = new();
    private int nextDeviceId;

    public WindowsNativeClientBackend(IUsbIpEventSink? eventSink = null, string? vhciDevicePath = null)
    {
        vhci = new WindowsVhciController(vhciDevicePath);
        sink = eventSink ?? NullUsbIpEventSink.Instance;
    }

    public Task<UsbIpBackendCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var version = vhci.GetVersion();
        return Task.FromResult(new UsbIpBackendCapabilities(true, false, true, true,
            "MyUsbIP Windows UdeCx VHCI", $"{version >> 16}.{version & 0xFFFF}"));
    }

    public async Task<IReadOnlyList<UsbIpDeviceInfo>> ListRemoteDevicesAsync(string host, int port = 3240, CancellationToken cancellationToken = default)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        await using var stream = tcp.GetStream();
        await UsbIpRemoteProtocol.RequestDeviceListAsync(stream, cancellationToken).ConfigureAwait(false);
        return await UsbIpRemoteProtocol.ReadDeviceListReplyAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    public async Task<UsbIpAttachResult> AttachAsync(string host, string busId, int port = 3240, CancellationToken cancellationToken = default)
    {
        var descriptorSet = await QueryDescriptorsAsync(host, port, busId, cancellationToken).ConfigureAwait(false);
        var deviceId = checked((uint)Interlocked.Increment(ref nextDeviceId));

        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            var stream = tcp.GetStream();
            await UsbIpRemoteProtocol.RequestImportAsync(stream, busId, cancellationToken).ConfigureAwait(false);
            var remoteDevice = await UsbIpRemoteProtocol.ReadImportReplyAsync(stream, cancellationToken).ConfigureAwait(false);
            var localPort = vhci.CreatePort(deviceId, busId, descriptorSet);

            var session = new NativeTunnelSession(localPort, deviceId, busId, host, tcp, stream, vhci, sink);
            if (!sessions.TryAdd(localPort, session))
            {
                vhci.RemovePort(localPort);
                await session.DisposeAsync();
                return UsbIpAttachResult.Failed(busId, $"本地 VHCI 端口 {localPort} 已存在。 ");
            }

            session.Start();
            await sink.WriteAsync(new(DateTimeOffset.Now, "native.client.attached", "Information", null, busId, host,
                $"UdeCx 虚拟设备已创建，LocalPort={localPort}, VID:PID={remoteDevice.VidPid}"), cancellationToken);
            return new UsbIpAttachResult(true, busId, localPort, "已通过 MyUsbIP UdeCx VHCI 创建虚拟 USB 设备。 ");
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    public async Task DetachAsync(int port, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (sessions.TryRemove(port, out var session))
            await session.DisposeAsync().ConfigureAwait(false);
        vhci.RemovePort(port);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var pair in sessions.ToArray())
        {
            if (sessions.TryRemove(pair.Key, out var session))
                await session.DisposeAsync().ConfigureAwait(false);
            try { vhci.RemovePort(pair.Key); } catch { }
        }
    }

    private static async Task<UsbDescriptorSet> QueryDescriptorsAsync(string host, int port, string busId, CancellationToken cancellationToken)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        await using var stream = tcp.GetStream();
        await UsbDescriptorControlProtocol.WriteRequestAsync(stream, busId, cancellationToken).ConfigureAwait(false);
        return await UsbDescriptorControlProtocol.ReadReplyAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private sealed class NativeTunnelSession : IAsyncDisposable
    {
        private readonly int localPort;
        private readonly uint deviceId;
        private readonly string busId;
        private readonly string host;
        private readonly TcpClient tcp;
        private readonly NetworkStream stream;
        private readonly WindowsVhciController vhci;
        private readonly IUsbIpEventSink sink;
        private readonly CancellationTokenSource stop = new();
        private Task? pumpTask;

        public NativeTunnelSession(int localPort, uint deviceId, string busId, string host, TcpClient tcp,
            NetworkStream stream, WindowsVhciController vhci, IUsbIpEventSink sink)
        {
            this.localPort = localPort;
            this.deviceId = deviceId;
            this.busId = busId;
            this.host = host;
            this.tcp = tcp;
            this.stream = stream;
            this.vhci = vhci;
            this.sink = sink;
        }

        public void Start() => pumpTask = Task.Run(PumpAsync);

        private async Task PumpAsync()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    // DeviceIoControl 为阻塞调用，放在线程池线程等待 UdeCx 产生 URB。
                    var nativeRequest = await Task.Run(() => vhci.GetPendingUrb(), stop.Token).ConfigureAwait(false);
                    var submit = ParseNativeRequest(nativeRequest, deviceId);
                    await UsbIpRemoteProtocol.WriteSubmitAsync(stream, submit, stop.Token).ConfigureAwait(false);
                    var completion = await UsbIpRemoteProtocol.ReadSubmitCompletionAsync(stream, stop.Token).ConfigureAwait(false);
                    vhci.CompleteUrb(BuildNativeCompletion(completion));
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                await sink.WriteAsync(new(DateTimeOffset.Now, "native.client.tunnel.failed", "Error", null, busId, host,
                    $"LocalPort={localPort}: {ex.Message}", Exception: ex), CancellationToken.None);
            }
        }

        public async ValueTask DisposeAsync()
        {
            stop.Cancel();
            tcp.Dispose();
            if (pumpTask is not null)
            {
                try { await pumpTask.ConfigureAwait(false); } catch { }
            }
            stop.Dispose();
        }

        private static UsbIpSubmitRequest ParseNativeRequest(ReadOnlySpan<byte> data, uint fallbackDeviceId)
        {
            if (data.Length < 44) throw new InvalidDataException("VHCI URB 请求长度不足。 ");
            var version = BinaryPrimitives.ReadUInt32LittleEndian(data[..4]);
            if (version != DriverControlProtocol.ApiVersion) throw new InvalidDataException("VHCI URB ABI 版本不匹配。 ");
            var sequence = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, 4));
            var nativeDeviceId = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(8, 4));
            var endpoint = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(12, 4));
            var direction = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(16, 4));
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(20, 4));
            var transferLength = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(24, 4));
            var interval = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(28, 4));
            var setup = data.Slice(32, 8).ToArray();
            var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(40, 4));
            if (payloadLength > (uint)(data.Length - 44)) throw new InvalidDataException("VHCI URB Payload 长度异常。 ");
            var payload = data.Slice(44, checked((int)payloadLength)).ToArray();
            return new UsbIpSubmitRequest(sequence, nativeDeviceId == 0 ? fallbackDeviceId : nativeDeviceId,
                direction, endpoint, flags, transferLength, 0, 0, interval, setup, payload);
        }

        private static byte[] BuildNativeCompletion(UsbIpSubmitCompletion completion)
        {
            var buffer = new byte[24 + completion.Payload.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), DriverControlProtocol.ApiVersion);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4, 4), completion.Sequence);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(8, 4), completion.Status);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(12, 4), completion.ActualLength);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(16, 4), checked((uint)Math.Max(0, completion.ErrorCount)));
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(20, 4), checked((uint)completion.Payload.Length));
            if (completion.Payload.Length > 0) completion.Payload.CopyTo(buffer, 24);
            return buffer;
        }
    }
}
