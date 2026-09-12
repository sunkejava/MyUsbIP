using System.Diagnostics;
using MyUsbIP.Abstractions;

namespace MyUsbIP.Client;

/// <summary>
/// MyUsbIP 客户端门面。
/// 对上提供“发现远程设备/挂载/卸载”三个核心动作，并统一生成 TraceId 与诊断事件。
/// </summary>
public sealed class MyUsbIpClient : IMyUsbIpClient
{
    private readonly IUsbIpClientBackend backend;
    private readonly IUsbIpEventSink eventSink;

    public MyUsbIpClient(IUsbIpClientBackend backend, IUsbIpEventSink? eventSink = null)
    {
        this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
        this.eventSink = eventSink ?? NullUsbIpEventSink.Instance;
    }

    public Task<IReadOnlyList<UsbIpDeviceInfo>> GetRemoteDevicesAsync(string host, int port = 3240, CancellationToken cancellationToken = default) =>
        ExecuteAsync("client.remote.list", null, host, () => backend.ListRemoteDevicesAsync(host, port, cancellationToken), cancellationToken);

    public Task<UsbIpAttachResult> AttachAsync(string host, string busId, int port = 3240, CancellationToken cancellationToken = default) =>
        ExecuteAsync("client.attach", busId, host, () => backend.AttachAsync(host, busId, port, cancellationToken), cancellationToken);

    public Task DetachAsync(int port, CancellationToken cancellationToken = default) =>
        ExecuteAsync("client.detach", null, null, async () => { await backend.DetachAsync(port, cancellationToken); return true; }, cancellationToken);

    private async Task<T> ExecuteAsync<T>(string operation, string? busId, string? host, Func<Task<T>> action, CancellationToken cancellationToken)
    {
        using var activity = UsbIpDiagnostics.StartActivity(operation, busId, host);
        var started = Stopwatch.GetTimestamp();
        var traceId = Activity.Current?.TraceId.ToString();
        await eventSink.WriteAsync(new(DateTimeOffset.Now, operation + ".start", "Information", traceId, busId, host, "操作开始"), cancellationToken);
        try
        {
            var result = await action().ConfigureAwait(false);
            await eventSink.WriteAsync(new(DateTimeOffset.Now, operation + ".success", "Information", traceId, busId, host, "操作成功"), cancellationToken);
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            UsbIpDiagnostics.Failures.Add(1, new KeyValuePair<string, object?>("operation", operation));
            await eventSink.WriteAsync(new(DateTimeOffset.Now, operation + ".failed", "Error", traceId, busId, host, ex.Message, Exception: ex), CancellationToken.None);
            throw;
        }
        finally
        {
            UsbIpDiagnostics.OperationDurationMs.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds, new KeyValuePair<string, object?>("operation", operation));
        }
    }
}
