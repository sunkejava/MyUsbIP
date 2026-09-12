using System.Collections.Concurrent;
using MyUsbIP.Abstractions;

namespace MyUsbIP.Runtime;

/// <summary>
/// 进程内设备租约管理器。
/// 适合单实例服务直接使用；多实例部署时可按同一接口替换为 Redis/数据库实现。
/// </summary>
public sealed class UsbIpDeviceLeaseManager(IUsbIpEventSink? eventSink = null) : IUsbIpDeviceLeaseManager
{
    private readonly ConcurrentDictionary<string, UsbIpDeviceLease> byBusId = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> leaseToBusId = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly IUsbIpEventSink sink = eventSink ?? NullUsbIpEventSink.Instance;

    public IReadOnlyList<UsbIpDeviceLease> GetActiveLeases()
    {
        CleanupExpired(DateTimeOffset.Now);
        return byBusId.Values.OrderBy(x => x.BusId, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<UsbIpLeaseAcquireResult> AcquireAsync(
        string busId,
        string clientId,
        TimeSpan ttl,
        string? userId = null,
        string? purpose = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(busId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        if (ttl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ttl), "租约 TTL 必须大于 0。 ");

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.Now;
            CleanupExpired(now);

            if (byBusId.TryGetValue(busId, out var existing))
            {
                if (string.Equals(existing.ClientId, clientId, StringComparison.OrdinalIgnoreCase))
                {
                    var renewed = existing with { ExpiresAt = now.Add(ttl) };
                    byBusId[busId] = renewed;
                    await WriteAsync("lease.renewed", renewed, "同一客户端重复申请，已自动续租", cancellationToken);
                    return new(true, renewed, "已续租");
                }

                return new(false, null, $"设备 {busId} 已被客户端 {existing.ClientId} 占用，租约到期时间 {existing.ExpiresAt:O}。 ");
            }

            var lease = new UsbIpDeviceLease
            {
                LeaseId = Guid.NewGuid().ToString("N"),
                BusId = busId,
                ClientId = clientId,
                UserId = userId,
                Purpose = purpose,
                CreatedAt = now,
                ExpiresAt = now.Add(ttl),
            };
            byBusId[busId] = lease;
            leaseToBusId[lease.LeaseId] = busId;
            await WriteAsync("lease.acquired", lease, "设备租约申请成功", cancellationToken);
            return new(true, lease, "租约申请成功");
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> RenewAsync(string leaseId, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        if (ttl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ttl));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CleanupExpired(DateTimeOffset.Now);
            if (!leaseToBusId.TryGetValue(leaseId, out var busId)) return false;
            if (!byBusId.TryGetValue(busId, out var lease) || !string.Equals(lease.LeaseId, leaseId, StringComparison.OrdinalIgnoreCase)) return false;

            var renewed = lease with { ExpiresAt = DateTimeOffset.Now.Add(ttl) };
            byBusId[busId] = renewed;
            await WriteAsync("lease.renewed", renewed, "设备租约续期成功", cancellationToken);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> ReleaseAsync(string leaseId, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!leaseToBusId.TryRemove(leaseId, out var busId)) return false;
            if (!byBusId.TryRemove(busId, out var lease)) return false;
            await WriteAsync("lease.released", lease, "设备租约已释放", cancellationToken);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    private void CleanupExpired(DateTimeOffset now)
    {
        foreach (var pair in byBusId.ToArray())
        {
            if (!pair.Value.IsExpired(now)) continue;
            if (byBusId.TryRemove(pair.Key, out var removed))
                leaseToBusId.TryRemove(removed.LeaseId, out _);
        }
    }

    private ValueTask WriteAsync(string name, UsbIpDeviceLease lease, string message, CancellationToken cancellationToken) =>
        sink.WriteAsync(new(DateTimeOffset.Now, name, "Information", null, lease.BusId, lease.ClientId, message), cancellationToken);
}
