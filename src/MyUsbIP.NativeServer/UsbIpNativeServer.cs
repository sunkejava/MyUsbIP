using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using MyUsbIP.Abstractions;
using MyUsbIP.Protocol;

namespace MyUsbIP.NativeServer;

public interface IUsbIpExportTransport
{
    Task<IReadOnlyList<UsbIpDeviceInfo>> ListAsync(CancellationToken cancellationToken = default);
    Task<UsbIpDeviceInfo?> FindAsync(string busId, CancellationToken cancellationToken = default);
    Task BeginSessionAsync(string busId, CancellationToken cancellationToken = default);
    Task<UsbIpSubmitCompletion> SubmitAsync(string busId, UsbIpSubmitRequest request, CancellationToken cancellationToken = default);
    Task CancelAsync(string busId, uint sequence, CancellationToken cancellationToken = default);
    Task EndSessionAsync(string busId, CancellationToken cancellationToken = default);
}

public interface IUsbDescriptorProvider
{
    Task<UsbDescriptorSet> GetDescriptorSetAsync(string busId, CancellationToken cancellationToken = default);
}

/// <summary>USB/IP 原生服务日志选项。</summary>
public sealed record UsbIpNativeServerLoggingOptions
{
    public bool LogSuccessfulUrbs { get; init; }
    public bool LogDeviceListRequests { get; init; } = true;
}

/// <summary>MyUsbIP 自研 USB/IP TCP 服务。</summary>
public sealed class UsbIpNativeServer : IAsyncDisposable
{
    private readonly IUsbIpExportTransport transport;
    private readonly IUsbIpEventSink eventSink;
    private readonly UsbIpNativeServerLoggingOptions logging;
    private readonly TcpListener listener;
    private readonly CancellationTokenSource stopCts = new();
    private readonly List<Task> sessions = [];
    private readonly ConcurrentDictionary<string, ActiveImport> activeImports = new(StringComparer.OrdinalIgnoreCase);

    private sealed record ActiveImport(string SessionId, string? ClientAddress, DateTimeOffset ConnectedAt);

    public UsbIpNativeServer(IUsbIpExportTransport transport, IPAddress? address = null,
        int port = UsbIpProtocolConstants.DefaultPort, IUsbIpEventSink? eventSink = null,
        UsbIpNativeServerLoggingOptions? logging = null)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.eventSink = eventSink ?? NullUsbIpEventSink.Instance;
        this.logging = logging ?? new UsbIpNativeServerLoggingOptions();
        listener = new TcpListener(address ?? IPAddress.Any, port);
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopCts.Token);
        listener.Start();
        await eventSink.WriteAsync(new(DateTimeOffset.Now, "native.server.started", "Information", null, null, null,
            "MyUsbIP 原生 USB/IP 服务已启动"), linked.Token);
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
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        finally { listener.Stop(); }
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

    private async Task<IReadOnlyList<UsbIpDeviceInfo>> GetDetailedDevicesAsync(CancellationToken cancellationToken)
    {
        var devices = await transport.ListAsync(cancellationToken).ConfigureAwait(false);
        return devices.Select(device =>
        {
            if (!activeImports.TryGetValue(device.BusId, out var active)) return device;
            return device with
            {
                State = UsbIpDeviceState.Attached,
                ClientAddress = active.ClientAddress,
                SessionId = active.SessionId,
                ConnectedAt = active.ConnectedAt,
            };
        }).ToArray();
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        await using var stream = client.GetStream();
        string? importedBusId = null;
        var sessionStarted = false;
        var remote = client.Client.RemoteEndPoint?.ToString();
        var remoteAddress = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? remote;
        var sessionId = Guid.NewGuid().ToString("N");
        try
        {
            var op = await UsbIpCodec.ReadOperationHeaderAsync(stream, cancellationToken).ConfigureAwait(false);
            if (op.Version != UsbIpProtocolConstants.Version)
                throw new InvalidDataException($"不支持的 USB/IP 版本 0x{op.Version:X4}。 ");

            if (op.Code == UsbIpProtocolConstants.OpReqDevList)
            {
                var devices = await transport.ListAsync(cancellationToken).ConfigureAwait(false);
                await UsbIpWire.WriteDevListReplyAsync(stream, devices, cancellationToken).ConfigureAwait(false);
                if (logging.LogDeviceListRequests)
                {
                    await eventSink.WriteAsync(new(DateTimeOffset.Now, "native.devlist", "Information", sessionId, null,
                        remote, $"返回 {devices.Count} 个 USB 设备", new Dictionary<string, object?>
                        {
                            ["deviceCount"] = devices.Count,
                            ["clientAddress"] = remoteAddress,
                        }), CancellationToken.None);
                }
                return;
            }

            if (op.Code == UsbDeviceStatusProtocol.OpReqDeviceStatus)
            {
                var devices = await GetDetailedDevicesAsync(cancellationToken).ConfigureAwait(false);
                await UsbDeviceStatusProtocol.WriteReplyAsync(stream, devices, cancellationToken).ConfigureAwait(false);
                await eventSink.WriteAsync(new(DateTimeOffset.Now, "native.device.status", "Debug", sessionId, null,
                    remote, $"返回 {devices.Count} 个设备的完整状态", new Dictionary<string, object?>
                    {
                        ["deviceCount"] = devices.Count,
                        ["clientAddress"] = remoteAddress,
                    }), CancellationToken.None);
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
            await eventSink.WriteAsync(new(DateTimeOffset.Now, "native.import.request", "Information", sessionId,
                importedBusId, remote, "收到 USB/IP IMPORT 请求", new Dictionary<string, object?>
                {
                    ["clientAddress"] = remoteAddress,
                }), CancellationToken.None);

            var device = await transport.FindAsync(importedBusId, cancellationToken).ConfigureAwait(false);
            if (device is null)
            {
                await UsbIpWire.WriteImportFailureAsync(stream, 1, cancellationToken).ConfigureAwait(false);
                await eventSink.WriteAsync(new(DateTimeOffset.Now, "native.import.notfound", "Warning", sessionId,
                    importedBusId, remote, "IMPORT 失败：设备不存在"), CancellationToken.None);
                return;
            }

            try
            {
                await transport.BeginSessionAsync(importedBusId, cancellationToken).ConfigureAwait(false);
                sessionStarted = true;
            }
            catch (InvalidOperationException ex)
            {
                await UsbIpWire.WriteImportFailureAsync(stream, 1, cancellationToken).ConfigureAwait(false);
                await eventSink.WriteAsync(new(DateTimeOffset.Now, "native.import.rejected", "Warning", sessionId,
                    importedBusId, remote, ex.Message), CancellationToken.None);
                return;
            }

            var connectedAt = DateTimeOffset.Now;
            activeImports[importedBusId] = new ActiveImport(sessionId, remoteAddress, connectedAt);

            await UsbIpWire.WriteImportReplyAsync(stream, device, cancellationToken).ConfigureAwait(false);
            await eventSink.WriteAsync(new(DateTimeOffset.Now, "native.import.accepted", "Information", sessionId,
                importedBusId, remote, $"IMPORT 成功 {device.VidPid} {device.Product}，客户端 {remoteAddress}",
                new Dictionary<string, object?>
                {
                    ["vidPid"] = device.VidPid,
                    ["product"] = device.Product,
                    ["instanceId"] = device.InstanceId,
                    ["clientAddress"] = remoteAddress,
                    ["connectedAt"] = connectedAt,
                    ["speed"] = device.Speed,
                    ["busNumber"] = device.BusNumber,
                    ["deviceNumber"] = device.DeviceNumber,
                    ["usbVersion"] = device.UsbVersion,
                    ["deviceVersion"] = device.DeviceVersion,
                    ["deviceClass"] = device.DeviceClass,
                    ["deviceSubClass"] = device.DeviceSubClass,
                    ["deviceProtocol"] = device.DeviceProtocol,
                    ["configurationCount"] = device.ConfigurationCount,
                    ["interfaceCount"] = device.InterfaceCount,
                }), CancellationToken.None);

            await PumpUrbAsync(stream, importedBusId, sessionId, remote, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or EndOfStreamException or OperationCanceledException)
        {
            await eventSink.WriteAsync(new(DateTimeOffset.Now, "native.session.closed", "Information", sessionId,
                importedBusId, remote, ex.Message), CancellationToken.None);
        }
        catch (Exception ex)
        {
            await eventSink.WriteAsync(new(DateTimeOffset.Now, "native.session.failed", "Error", sessionId,
                importedBusId, remote, ex.Message, Exception: ex), CancellationToken.None);
        }
        finally
        {
            if (sessionStarted && importedBusId is not null)
            {
                if (activeImports.TryGetValue(importedBusId, out var current) && current.SessionId == sessionId)
                    activeImports.TryRemove(importedBusId, out _);

                try
                {
                    await transport.EndSessionAsync(importedBusId, CancellationToken.None).ConfigureAwait(false);
                    await eventSink.WriteAsync(new(DateTimeOffset.Now, "native.session.released", "Information", sessionId,
                        importedBusId, remote, "USB/IP 会话已释放，设备已重置并保留 UsbDk Redirect",
                        new Dictionary<string, object?> { ["clientAddress"] = remoteAddress }), CancellationToken.None);
                }
                catch (Exception ex)
                {
                    await eventSink.WriteAsync(new(DateTimeOffset.Now, "native.session.release.failed", "Error", sessionId,
                        importedBusId, remote, ex.Message, Exception: ex), CancellationToken.None);
                }
            }
            client.Dispose();
        }
    }

    private async Task PumpUrbAsync(Stream stream, string busId, string sessionId, string? remote,
        CancellationToken cancellationToken)
    {
        using var writeGate = new SemaphoreSlim(1, 1);
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = new ConcurrentDictionary<uint, Task>();

        async Task WriteSubmitAsync(UsbIpSubmitCompletion completion)
        {
            await writeGate.WaitAsync(sessionCts.Token).ConfigureAwait(false);
            try
            {
                await UsbIpWire.WriteSubmitCompletionAsync(stream, completion, sessionCts.Token).ConfigureAwait(false);
            }
            finally
            {
                writeGate.Release();
            }
        }

        async Task ProcessSubmitAsync(UsbIpSubmitRequest request)
        {
            try
            {
                var completion = await transport.SubmitAsync(busId, request, sessionCts.Token).ConfigureAwait(false);
                await WriteSubmitAsync(completion).ConfigureAwait(false);
                if (logging.LogSuccessfulUrbs)
                {
                    await eventSink.WriteAsync(new(DateTimeOffset.Now, "native.urb.completed", "Debug", sessionId,
                        busId, remote, $"URB Seq={request.Sequence} EP={request.Endpoint} 完成", new Dictionary<string, object?>
                        {
                            ["sequence"] = request.Sequence,
                            ["endpoint"] = request.Endpoint,
                            ["direction"] = request.Direction,
                            ["requestedLength"] = request.TransferBufferLength,
                            ["actualLength"] = completion.ActualLength,
                            ["status"] = completion.Status,
                        }), CancellationToken.None);
                }
            }
            catch (OperationCanceledException) when (sessionCts.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                try
                {
                    var failure = new UsbIpSubmitCompletion(request.Sequence, request.DeviceId, request.Direction,
                        request.Endpoint, -5, 0, request.StartFrame, request.NumberOfPackets, 1, Array.Empty<byte>());
                    await WriteSubmitAsync(failure).ConfigureAwait(false);
                }
                catch
                {
                    sessionCts.Cancel();
                }

                await eventSink.WriteAsync(new(DateTimeOffset.Now, "native.urb.failed", "Warning", sessionId,
                    busId, remote, $"URB Seq={request.Sequence} EP={request.Endpoint} 失败: {ex.Message}",
                    new Dictionary<string, object?>
                    {
                        ["sequence"] = request.Sequence,
                        ["endpoint"] = request.Endpoint,
                        ["direction"] = request.Direction,
                        ["requestedLength"] = request.TransferBufferLength,
                    }, ex), CancellationToken.None);
            }
            finally
            {
                pending.TryRemove(request.Sequence, out _);
            }
        }

        try
        {
            while (!sessionCts.IsCancellationRequested)
            {
                var basic = await UsbIpWire.ReadBasicHeaderAsync(stream, sessionCts.Token).ConfigureAwait(false);
                switch (basic.Command)
                {
                    case UsbIpDataCommands.CmdSubmit:
                    {
                        var request = await UsbIpWire.ReadSubmitAsync(stream, basic.Sequence, basic.DeviceId,
                            basic.Direction, basic.Endpoint, sessionCts.Token).ConfigureAwait(false);
                        var task = ProcessSubmitAsync(request);
                        pending[request.Sequence] = task;
                        break;
                    }
                    case UsbIpDataCommands.CmdUnlink:
                    {
                        var request = await UsbIpWire.ReadUnlinkAsync(stream, basic.Sequence, basic.DeviceId,
                            basic.Direction, basic.Endpoint, sessionCts.Token).ConfigureAwait(false);
                        var status = 0;
                        try
                        {
                            await transport.CancelAsync(busId, request.TargetSequence, sessionCts.Token).ConfigureAwait(false);
                        }
                        catch
                        {
                            status = -1;
                        }

                        await writeGate.WaitAsync(sessionCts.Token).ConfigureAwait(false);
                        try
                        {
                            await UsbIpWire.WriteUnlinkCompletionAsync(stream, request, status, sessionCts.Token).ConfigureAwait(false);
                        }
                        finally
                        {
                            writeGate.Release();
                        }
                        await eventSink.WriteAsync(new(DateTimeOffset.Now, "native.urb.unlink", "Information", sessionId,
                            busId, remote, $"UNLINK target={request.TargetSequence} status={status}"), CancellationToken.None);
                        break;
                    }
                    default:
                        throw new InvalidDataException($"未知 USB/IP 数据命令 0x{basic.Command:X8}。 ");
                }
            }
        }
        finally
        {
            sessionCts.Cancel();

            // 连接中断时主动 Abort 所有尚未完成的 UsbDk 请求，避免异步 I/O 阻止会话清理。
            var sequences = pending.Keys.ToArray();
            foreach (var sequence in sequences)
            {
                try { await transport.CancelAsync(busId, sequence, CancellationToken.None).ConfigureAwait(false); } catch { }
            }

            var current = pending.Values.ToArray();
            if (current.Length > 0)
            {
                try { await Task.WhenAll(current).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { }
            }
        }
    }
}
