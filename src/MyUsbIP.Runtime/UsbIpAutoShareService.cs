using MyUsbIP.Abstractions;

namespace MyUsbIP.Runtime;

/// <summary>
/// 根据规则自动共享新插入的 USB 设备。
/// 只处理 Added/Changed 事件，已经 Shared/Attached 的设备不会重复 bind。
/// </summary>
public sealed class UsbIpAutoShareService(
    IMyUsbIpServer server,
    IUsbIpDeviceMonitor monitor,
    IUsbIpEventSink? eventSink = null) : IUsbIpAutoShareService
{
    private readonly IUsbIpEventSink sink = eventSink ?? NullUsbIpEventSink.Instance;

    public async Task RunAsync(
        IReadOnlyList<UsbIpAutoShareRule> rules,
        UsbIpMonitorOptions? monitorOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rules);

        await foreach (var change in monitor.WatchAsync(monitorOptions, cancellationToken).ConfigureAwait(false))
        {
            if (change.Kind == UsbIpDeviceChangeKind.Removed) continue;
            var device = change.Device;
            if (device.State is UsbIpDeviceState.Shared or UsbIpDeviceState.Attached) continue;
            if (!rules.Any(x => x.IsMatch(device))) continue;

            try
            {
                await sink.WriteAsync(new(DateTimeOffset.Now, "autoshare.match", "Information", null, device.BusId, null, $"匹配自动共享规则: {device.VidPid} {device.Product}"), cancellationToken);
                await server.ShareAsync(device.BusId, cancellationToken).ConfigureAwait(false);
                await sink.WriteAsync(new(DateTimeOffset.Now, "autoshare.success", "Information", null, device.BusId, null, "设备已自动共享"), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await sink.WriteAsync(new(DateTimeOffset.Now, "autoshare.failed", "Error", null, device.BusId, null, ex.Message), cancellationToken);
            }
        }
    }
}
