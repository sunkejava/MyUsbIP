using System.Diagnostics;
using MyUsbIP.Abstractions;

namespace MyUsbIP.Server;

/// <summary>
/// MyUsbIP 服务器端门面。
/// 业务系统只依赖本类，不需要知道底层是 usbipd-win、Linux usbip_host 或后续自研驱动。
/// </summary>
public sealed class MyUsbIpServer : IMyUsbIpServer
{
    private readonly IUsbIpServerBackend backend;
    private readonly IUsbIpEventSink eventSink;

    public MyUsbIpServer(IUsbIpServerBackend backend, IUsbIpEventSink? eventSink = null)
    {
        this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
        this.eventSink = eventSink ?? NullUsbIpEventSink.Instance;
    }

    public Task<IReadOnlyList<UsbIpDeviceInfo>> GetDevicesAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync("server.device.list", null, () => backend.ListDevicesAsync(cancellationToken), cancellationToken);

    public Task ShareAsync(string busId, CancellationToken cancellationToken = default) =>
        ExecuteAsync("server.device.share", busId, async () => { await backend.ShareAsync(busId, cancellationToken); return true; }, cancellationToken);

    public Task UnshareAsync(string busId, CancellationToken cancellationToken = default) =>
        ExecuteAsync("server.device.unshare", busId, async () => { await backend.UnshareAsync(busId, cancellationToken); return true; }, cancellationToken);

    private async Task<T> ExecuteAsync<T>(string operation, string? busId, Func<Task<T>> action, CancellationToken cancellationToken)
    {
        using var activity = UsbIpDiagnostics.StartActivity(operation, busId);
        var started = Stopwatch.GetTimestamp();
        var traceId = Activity.Current?.TraceId.ToString();
        await eventSink.WriteAsync(new(DateTimeOffset.Now, operation + ".start", "Information", traceId, busId, null, "操作开始"), cancellationToken);
        try
        {
            var result = await action().ConfigureAwait(false);
            await eventSink.WriteAsync(new(DateTimeOffset.Now, operation + ".success", "Information", traceId, busId, null, "操作成功"), cancellationToken);
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            UsbIpDiagnostics.Failures.Add(1, new KeyValuePair<string, object?>("operation", operation));
            await eventSink.WriteAsync(new(DateTimeOffset.Now, operation + ".failed", "Error", traceId, busId, null, ex.Message, Exception: ex), CancellationToken.None);
            throw;
        }
        finally
        {
            UsbIpDiagnostics.OperationDurationMs.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds, new KeyValuePair<string, object?>("operation", operation));
        }
    }
}
