using System.Collections.Concurrent;
using MyUsbIP.Abstractions;
using MyUsbIP.NativeServer;

namespace MyUsbIP.UsbDk;

/// <summary>
/// UsbDk ExportTransport 恢复装饰器。
/// CH340 在历史 StopRedirect/驱动重新绑定后可能进入 UsbDk_StartRedirect 暂时失败状态；
/// 首次失败时触发一次安全 PnP 重新枚举，重新定位同一 PnP 实例后再尝试 Redirect。
/// 若 UsbDk FilterId 因重新枚举发生变化，使用会话别名把客户端请求的旧 BUSID 映射到新 BUSID。
/// </summary>
public sealed class RecoveringUsbDkExportTransport : IUsbIpExportTransport, IUsbDescriptorProvider
{
    private readonly UsbDkDeviceManager manager;
    private readonly UsbDkExportTransport inner;
    private readonly ConcurrentDictionary<string, string> sessionBusAliases = new(StringComparer.OrdinalIgnoreCase);

    public RecoveringUsbDkExportTransport(UsbDkDeviceManager manager)
    {
        this.manager = manager ?? throw new ArgumentNullException(nameof(manager));
        inner = new UsbDkExportTransport(manager);
    }

    public Task<IReadOnlyList<UsbIpDeviceInfo>> ListAsync(CancellationToken cancellationToken = default)
        => inner.ListAsync(cancellationToken);

    public Task<UsbIpDeviceInfo?> FindAsync(string busId, CancellationToken cancellationToken = default)
        => inner.FindAsync(busId, cancellationToken);

    public async Task BeginSessionAsync(string busId, CancellationToken cancellationToken = default)
    {
        try
        {
            await inner.BeginSessionAsync(busId, cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (Exception firstException)
        {
            var requested = await inner.FindAsync(busId, cancellationToken).ConfigureAwait(false);
            if (!IsCh340(requested)) throw;

            var fullInstanceId = requested!.InstanceId;
            if (!WindowsDeviceRecovery.TryReenumerate(fullInstanceId))
                throw new InvalidOperationException(
                    $"CH340 {busId} UsbDk_StartRedirect 失败，PnP 重新枚举也未成功。InstanceId={fullInstanceId}",
                    firstException);

            UsbIpDeviceInfo? recovered = null;
            for (var attempt = 0; attempt < 12 && !cancellationToken.IsCancellationRequested; attempt++)
            {
                await Task.Delay(attempt == 0 ? 300 : 200, cancellationToken).ConfigureAwait(false);
                var devices = await manager.ListAsync(cancellationToken).ConfigureAwait(false);
                recovered = devices.FirstOrDefault(x =>
                    string.Equals(x.InstanceId, fullInstanceId, StringComparison.OrdinalIgnoreCase) && IsCh340(x));
                if (recovered is not null) break;
            }

            if (recovered is null)
                throw new InvalidOperationException(
                    $"CH340 {busId} PnP 重新枚举后未重新出现在 UsbDk 列表。InstanceId={fullInstanceId}",
                    firstException);

            try
            {
                await inner.BeginSessionAsync(recovered.BusId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception retryException)
            {
                throw new InvalidOperationException(
                    $"CH340 Redirect 自动恢复失败：原 BUSID={busId}，重新枚举 BUSID={recovered.BusId}，InstanceId={fullInstanceId}",
                    new AggregateException(firstException, retryException));
            }

            if (!string.Equals(busId, recovered.BusId, StringComparison.OrdinalIgnoreCase))
                sessionBusAliases[busId] = recovered.BusId;
        }
    }

    public Task<UsbIpSubmitCompletion> SubmitAsync(string busId, UsbIpSubmitRequest request,
        CancellationToken cancellationToken = default)
        => inner.SubmitAsync(ResolveSessionBusId(busId), request, cancellationToken);

    public Task CancelAsync(string busId, uint sequence, CancellationToken cancellationToken = default)
        => inner.CancelAsync(ResolveSessionBusId(busId), sequence, cancellationToken);

    public async Task EndSessionAsync(string busId, CancellationToken cancellationToken = default)
    {
        var resolved = ResolveSessionBusId(busId);
        try
        {
            await inner.EndSessionAsync(resolved, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            sessionBusAliases.TryRemove(busId, out _);
        }
    }

    public Task<UsbDescriptorSet> GetDescriptorSetAsync(string busId, CancellationToken cancellationToken = default)
        => inner.GetDescriptorSetAsync(ResolveSessionBusId(busId), cancellationToken);

    private string ResolveSessionBusId(string busId)
        => sessionBusAliases.TryGetValue(busId, out var actualBusId) ? actualBusId : busId;

    private static bool IsCh340(UsbIpDeviceInfo? device)
        => device is not null && device.VendorId == 0x1A86 && device.ProductId == 0x7523;
}
