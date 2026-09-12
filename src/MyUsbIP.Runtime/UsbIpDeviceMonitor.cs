using System.Runtime.CompilerServices;
using MyUsbIP.Abstractions;

namespace MyUsbIP.Runtime;

/// <summary>
/// 基于设备快照差异实现的跨平台 USB/IP 设备监控器。
/// 采用轮询而不是绑定某个系统的 PnP/udev API，保证 Windows/Linux 行为一致；
/// 后续可在不改变上层接口的情况下替换为原生事件实现。
/// </summary>
public sealed class UsbIpDeviceMonitor(IMyUsbIpServer server, IUsbIpEventSink? eventSink = null) : IUsbIpDeviceMonitor
{
    private readonly IUsbIpEventSink sink = eventSink ?? NullUsbIpEventSink.Instance;

    public async IAsyncEnumerable<UsbIpDeviceChange> WatchAsync(
        UsbIpMonitorOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options ??= new UsbIpMonitorOptions();
        var previous = new Dictionary<string, UsbIpDeviceInfo>(StringComparer.OrdinalIgnoreCase);
        var first = true;

        while (!cancellationToken.IsCancellationRequested)
        {
            IReadOnlyList<UsbIpDeviceInfo> devices;
            try
            {
                devices = await server.GetDevicesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                yield break;
            }
            catch (Exception ex)
            {
                await sink.WriteAsync(new(DateTimeOffset.Now, "monitor.scan.failed", "Error", null, null, null, ex.Message), cancellationToken);
                await Task.Delay(options.PollInterval, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var current = devices.ToDictionary(GetIdentity, StringComparer.OrdinalIgnoreCase);

            foreach (var pair in current)
            {
                if (!previous.TryGetValue(pair.Key, out var old))
                {
                    if (!first || options.EmitInitialDevices)
                    {
                        var change = new UsbIpDeviceChange(UsbIpDeviceChangeKind.Added, pair.Value, DateTimeOffset.Now);
                        await EmitAsync(change, cancellationToken).ConfigureAwait(false);
                        yield return change;
                    }
                }
                else if (!Equals(old, pair.Value))
                {
                    var change = new UsbIpDeviceChange(UsbIpDeviceChangeKind.Changed, pair.Value, DateTimeOffset.Now);
                    await EmitAsync(change, cancellationToken).ConfigureAwait(false);
                    yield return change;
                }
            }

            foreach (var pair in previous)
            {
                if (!current.ContainsKey(pair.Key))
                {
                    var change = new UsbIpDeviceChange(UsbIpDeviceChangeKind.Removed, pair.Value, DateTimeOffset.Now);
                    await EmitAsync(change, cancellationToken).ConfigureAwait(false);
                    yield return change;
                }
            }

            previous = current;
            first = false;
            await Task.Delay(options.PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask EmitAsync(UsbIpDeviceChange change, CancellationToken cancellationToken)
    {
        await sink.WriteAsync(new(
            change.OccurredAt,
            $"device.{change.Kind.ToString().ToLowerInvariant()}",
            "Information",
            null,
            change.Device.BusId,
            null,
            $"{change.Device.VidPid} {change.Device.Product}"), cancellationToken);
    }

    private static string GetIdentity(UsbIpDeviceInfo device) =>
        !string.IsNullOrWhiteSpace(device.InstanceId)
            ? device.InstanceId
            : !string.IsNullOrWhiteSpace(device.SerialNumber)
                ? $"{device.VidPid}:{device.SerialNumber}"
                : $"{device.BusId}:{device.VidPid}";
}
