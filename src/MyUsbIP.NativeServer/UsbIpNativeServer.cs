using System.Net;
using System.Net.Sockets;
using MyUsbIP.Abstractions;
using MyUsbIP.Protocol;

namespace MyUsbIP.NativeServer;

/// <summary>
/// 负责把标准 USB/IP 网络请求转交给底层 USB 设备传输实现。
/// Windows 下最终由 MyUsbIP.Exporter.sys 提供真实 USB URB 转发能力。
/// </summary>
public interface IUsbIpExportTransport
{
    Task<IReadOnlyList<UsbIpDeviceInfo>> ListAsync(CancellationToken cancellationToken = default);
    Task<UsbIpDeviceInfo?> FindAsync(string busId, CancellationToken cancellationToken = default);
    Task BeginSessionAsync(string busId, CancellationToken cancellationToken = default);
    Task<UsbIpSubmitCompletion> SubmitAsync(string busId, UsbIpSubmitRequest request, CancellationToken cancellationToken = default);
    Task CancelAsync(string busId, uint sequence, CancellationToken cancellationToken = default);
    Task EndSessionAsync(string busId, CancellationToken cancellationToken = default);
}

/// <summary>
/// 可选的描述符提供器。自研 Windows UdeCx 客户端在创建虚拟 USB 设备之前需要预取真实设备描述符。
/// </summary>
public interface IUsbDescriptorProvider
{
    Task<UsbDescriptorSet> GetDescriptorSetAsync(string busId, CancellationToken cancellationToken = default);
}

/// <summary>
/// MyUsbIP 自研 USB/IP TCP 服务。
/// 不依赖 usbipd-win；直接实现 USB/IP DEVLIST / IMPORT / SUBMIT 数据路径，
/// 并额外提供 UdeCx 所需的描述符预取扩展。
/// </summary>
public sealed class UsbIpNativeServer : IAsyncDisposable
{
    private readonly IUsbIpExportTransport transport;
    private readonly IUsbIpEventSink eventSink;
    private readonly TcpListener listener;
    private readonly CancellationTokenSource stopCts = new();
    private readonly List<Task> sessions = [];

    public UsbIpNativeServer(
        IUsbIpExportTransport transport,
        IPAddress? address = null,
        int port = UsbIpProtocolConstants.DefaultPort,
        IUsbIpEventSink? eventSink = null)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.eventSink = eventSink ?? NullUsbIpEventSink.Instance;
        listener = new TcpListener(address ?? IPAddress.Any, port);
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopCts.Token);
        listener.Start();
        await eventSink.WriteAsync(new(DateTimeOffset.Now, "native.server.started", "Information", null, null, null, "MyUsbIP 原生 USB/IP 服务已启动"), linked.Token);

        try
        {
            while (!linked.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(linked.Token).ConfigureAwait(false);
                var task = HandleClientAsync(client, linked.Token);
                lock (sessions) sessions.Add(task);
                _ = task.ContinueWith(_ => { lock (sessions) sessions.Remove(task); }, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
        finally
        {
            listener.Stop();
        }
    }

    public async ValueTask DisposeAsync()
    {
        stopCts.Cancel();
        listener.Stop();
        Task[] current;
        lock (sessions) current = [.. sessions];
        try { await Task.WhenAll(current).ConfigureAwait(false); } catch { }
        stopCts.Dispose();
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        await using var stream = client.GetStream();
        string? importedBusId = null;
        try
        {
            var op = await UsbIpCodec.ReadOperationHeaderAsync(stream, cancellationToken).ConfigureAwait(false);
            if (op.Version != UsbIpProtocolConstants.Version)
                throw new InvalidDataException($"不支持的 USB/IP 版本 0x{op.Version:X4}。 ");

            if (op.Code == UsbIpProtocolConstants.OpReqDevList)
            {
                var devices = await transport.ListAsync(cancellationToken).ConfigureAwait(false);
                await UsbIpWire.WriteDevListReplyAsync(stream, devices, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (op.Code == UsbDescriptorControlProtocol.OpReqDescriptors)
            {
                if (transport is not IUsbDescriptorProvider descriptorProvider)
                    throw new NotSupportedException("当前 Exporter 未实现 USB 描述符查询。 ");

                var busId = await UsbIpCodec.ReadBusIdAsync(stream, cancellationToken).ConfigureAwait(false);
                var descriptors = await descriptorProvider.GetDescriptorSetAsync(busId, cancellationToken).ConfigureAwait(false);
                await UsbDescriptorControlProtocol.WriteReplyAsync(stream, descriptors, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (op.Code != UsbIpProtocolConstants.OpReqImport)
                throw new InvalidDataException($"不支持的 USB/IP OP=0x{op.Code:X4}。 ");

            importedBusId = await UsbIpCodec.ReadBusIdAsync(stream, cancellationToken).ConfigureAwait(false);
            var device = await transport.FindAsync(importedBusId, cancellationToken).ConfigureAwait(false)
                         ?? throw new InvalidOperationException($"设备 {importedBusId} 不存在。 ");

            await transport.BeginSessionAsync(importedBusId, cancellationToken).ConfigureAwait(false);
            await UsbIpWire.WriteImportReplyAsync(stream, device, cancellationToken).ConfigureAwait(false);
            await PumpUrbAsync(stream, importedBusId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or EndOfStreamException or OperationCanceledException)
        {
            await eventSink.WriteAsync(new(DateTimeOffset.Now, "native.session.closed", "Information", null, importedBusId, client.Client.RemoteEndPoint?.ToString(), ex.Message), CancellationToken.None);
        }
        catch (Exception ex)
        {
            await eventSink.WriteAsync(new(DateTimeOffset.Now, "native.session.failed", "Error", null, importedBusId, client.Client.RemoteEndPoint?.ToString(), ex.Message, Exception: ex), CancellationToken.None);
        }
        finally
        {
            if (importedBusId is not null)
            {
                try { await transport.EndSessionAsync(importedBusId, CancellationToken.None).ConfigureAwait(false); } catch { }
            }
            client.Dispose();
        }
    }

    private async Task PumpUrbAsync(Stream stream, string busId, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var basic = await UsbIpWire.ReadBasicHeaderAsync(stream, cancellationToken).ConfigureAwait(false);
            switch (basic.Command)
            {
                case UsbIpDataCommands.CmdSubmit:
                {
                    var request = await UsbIpWire.ReadSubmitAsync(stream, basic.Sequence, basic.DeviceId, basic.Direction, basic.Endpoint, cancellationToken).ConfigureAwait(false);
                    var completion = await transport.SubmitAsync(busId, request, cancellationToken).ConfigureAwait(false);
                    await UsbIpWire.WriteSubmitCompletionAsync(stream, completion, cancellationToken).ConfigureAwait(false);
                    break;
                }
                case UsbIpDataCommands.CmdUnlink:
                    await transport.CancelAsync(busId, basic.Sequence, cancellationToken).ConfigureAwait(false);
                    throw new NotSupportedException("UNLINK 应答路径将在 Exporter 异步取消队列完成后接入。 ");
                default:
                    throw new InvalidDataException($"未知 USB/IP 数据命令 0x{basic.Command:X8}。 ");
            }
        }
    }
}
