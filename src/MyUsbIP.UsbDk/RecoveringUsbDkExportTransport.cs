using System.Collections.Concurrent;
using MyUsbIP.Abstractions;
using MyUsbIP.NativeServer;
using MyUsbIP.Protocol;

namespace MyUsbIP.UsbDk;

/// <summary>
/// UsbDk ExportTransport 恢复装饰器。
/// CH340 在 UsbDk_StartRedirect 失败后，触发父 USB Hub 的 PnP 重新枚举，
/// 重新定位同一 PnP 实例后再次尝试 Redirect。
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
        catch (Exception firstException) when (firstException is not OperationCanceledException)
        {
            var requested = await inner.FindAsync(busId, cancellationToken).ConfigureAwait(false);
            if (!IsCh340(requested)) throw;

            var fullInstanceId = requested!.InstanceId;
            if (!WindowsDeviceRecovery.TryReenumerate(fullInstanceId, out var recoveryDetail))
                throw new InvalidOperationException(
                    $"CH340 {busId} UsbDk_StartRedirect 失败，父 USB Hub PnP 重新枚举也未成功。" +
                    $"InstanceId={fullInstanceId}；Recovery={recoveryDetail}",
                    firstException);

            UsbIpDeviceInfo? recovered = null;
            // USB Hub 重新枚举及 UsbDk Filter 重新建立并非瞬时完成，最多等待约 5 秒。
            for (var attempt = 0; attempt < 20 && !cancellationToken.IsCancellationRequested; attempt++)
            {
                await Task.Delay(attempt == 0 ? 400 : 250, cancellationToken).ConfigureAwait(false);
                var devices = await manager.ListAsync(cancellationToken).ConfigureAwait(false);
                recovered = devices.FirstOrDefault(x =>
                    string.Equals(x.InstanceId, fullInstanceId, StringComparison.OrdinalIgnoreCase) && IsCh340(x));
                if (recovered is not null) break;
            }

            if (recovered is null)
                throw new InvalidOperationException(
                    $"CH340 {busId} 父 USB Hub PnP 重新枚举已提交，但设备未在等待窗口内重新出现在 UsbDk 列表。" +
                    $"InstanceId={fullInstanceId}；Recovery={recoveryDetail}",
                    firstException);

            try
            {
                await inner.BeginSessionAsync(recovered.BusId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception retryException) when (retryException is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    $"CH340 Redirect 自动恢复失败：原 BUSID={busId}，重新枚举 BUSID={recovered.BusId}，" +
                    $"InstanceId={fullInstanceId}；Recovery={recoveryDetail}",
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
