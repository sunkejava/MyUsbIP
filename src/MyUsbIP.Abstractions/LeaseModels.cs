namespace MyUsbIP.Abstractions;

/// <summary>USB 设备独占租约。</summary>
public sealed record UsbIpDeviceLease
{
    public required string LeaseId { get; init; }
    public required string BusId { get; init; }
    public required string ClientId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public string? UserId { get; init; }
    public string? Purpose { get; init; }

    public bool IsExpired(DateTimeOffset now) => ExpiresAt <= now;
}

/// <summary>租约申请结果。</summary>
public sealed record UsbIpLeaseAcquireResult(bool Success, UsbIpDeviceLease? Lease, string? Message);

/// <summary>
/// USB 设备租约管理器。用于在真正 Attach 前完成业务侧独占控制。
/// </summary>
public interface IUsbIpDeviceLeaseManager
{
    IReadOnlyList<UsbIpDeviceLease> GetActiveLeases();
    Task<UsbIpLeaseAcquireResult> AcquireAsync(
        string busId,
        string clientId,
        TimeSpan ttl,
        string? userId = null,
        string? purpose = null,
        CancellationToken cancellationToken = default);
    Task<bool> RenewAsync(string leaseId, TimeSpan ttl, CancellationToken cancellationToken = default);
    Task<bool> ReleaseAsync(string leaseId, CancellationToken cancellationToken = default);
}
