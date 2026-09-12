using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using MyUsbIP.Abstractions;
using MyUsbIP.Protocol;

namespace MyUsbIP.NativeServer;

/// <summary>
/// 负责把标准 USB/IP 网络请求转交给底层 USB 设备传输实现。
/// Windows 生产模式下由 UsbDkExportTransport 提供真实 USB 访问能力；
/// 实验模式下也可以由自研 Exporter 驱动实现。
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
/// 可选的描述符提供器。实验性 UdeCx 客户端在创建虚拟 USB 设备之前需要预取真实设备描述符。
/// usbip-win VHCI 客户端正常通过 EP0 标准请求读取描述符，不依赖此扩展。
/// </summary>
public interface IUsbDescriptorProvider
{
    Task<UsbDescriptorSet> GetDescriptorSetAsync(string busId, CancellationToken cancellationToken = default);
}

/// <summary>
/// MyUsbIP 自研 USB/IP TCP 服务。
/// 不依赖 usbipd-win；直接实现 USB/IP DEVLIST / IMPORT / SUBMIT / UNLINK 数据路径。
/// </summary>
public sealed class UsbIpNativeServer : IAsyncDisposable
{
    private readonly IUsbIpExportTransport transport;
    private readonly IUsbIpEventSink eventSink;
    private readonly TcpListener listener;
    private readonly CancellationTokenSource stopCts = new();
    private readonly List<Task> sessions = [];

    public UsbIpNativeServer(IUsbIpExportTransport transport, IPAddress? address = null,
        int port = UsbIpProtocolConstants.DefaultPort, IUsbIpEventSink? eventSink = null)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.eventSink = eventSink ?? NullUsbIpEventSink.Instance;
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

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        await using var stream = client.GetStream();
        string? importedBusId = null;
        var sessionStarted = false;
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
            var device = await transport.FindAsync(importedBusId, cancellationToken).ConfigureAwait(false);
            if (device is null)
            {
                await UsbIpWire.WriteImportFailureAsync(stream, 1, cancellationToken).ConfigureAwait(false);
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
                await eventSink.WriteAsync(new(DateTimeOffset.Now, "native.import.rejected", "Warning", null,
                    importedBusId, client.Client.RemoteEndPoint?.ToString(), ex.Message), CancellationToken.None);
                return;
            }

            await UsbIpWire.WriteImportReplyAsync(stream, device, cancellationToken).ConfigureAwait(false);
            await PumpUrbAsync(stream, importedBusId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or EndOfStreamException or OperationCanceledException)
        {
            await eventSink.WriteAsync(new(DateTimeOffset.Now, "native.session.closed", "Information", null,
                importedBusId, client.Client.RemoteEndPoint?.ToString(), ex.Message), CancellationToken.None);
        }
        catch (Exception ex)
        {
            await eventSink.WriteAsync(new(DateTimeOffset.Now, "native.session.failed", "Error", null,
                importedBusId, client.Client.RemoteEndPoint?.ToString(), ex.Message, Exception: ex), CancellationToken.None);
        }
        finally
        {
            if (sessionStarted && importedBusId is not null)
            {
                try { await transport.EndSessionAsync(importedBusId, CancellationToken.None).ConfigureAwait(false); } catch { }
            }
            client.Dispose();
        }
    }

    /// <summary>
    /// USB/IP 数据阶段允许多个 URB 同时在途。这里读取请求时只负责解析和分发，
    /// 不等待某个物理 USB 请求结束后才继续读网络，避免 Interrupt/CCID 长轮询阻塞后续 Control/Bulk 请求。
    /// 返回包允许乱序完成，但同一 NetworkStream 的写操作必须串行化，防止帧内容交叉。
    /// </summary>
    private async Task PumpUrbAsync(Stream stream, string busId, CancellationToken cancellationToken)
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
            }
            catch (OperationCanceledException) when (sessionCts.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                try
                {
                    var failure = new UsbIpSubmitCompletion(
                        request.Sequence,
                        request.DeviceId,
                        request.Direction,
                        request.Endpoint,
                        -5,
                        0,
                        request.StartFrame,
                        request.NumberOfPackets,
                        1,
                        Array.Empty<byte>());
                    await WriteSubmitAsync(failure).ConfigureAwait(false);
                }
                catch
                {
                    sessionCts.Cancel();
                }

                await eventSink.WriteAsync(new(DateTimeOffset.Now, "native.urb.failed", "Warning", null,
                    busId, null, $"URB Seq={request.Sequence} 失败: {ex.Message}", Exception: ex), CancellationToken.None);
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
            var current = pending.Values.ToArray();
            if (current.Length > 0)
            {
                try { await Task.WhenAll(current).ConfigureAwait(false); } catch { }
            }
        }
    }
}
