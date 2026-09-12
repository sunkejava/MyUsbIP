namespace MyUsbIP.Abstractions;

/// <summary>USB 设备变化类型。</summary>
public enum UsbIpDeviceChangeKind
{
    Added = 1,
    Removed = 2,
    Changed = 3,
}

/// <summary>USB 设备变化事件。</summary>
public sealed record UsbIpDeviceChange(
    UsbIpDeviceChangeKind Kind,
    UsbIpDeviceInfo Device,
    DateTimeOffset OccurredAt);

/// <summary>
/// 自动共享规则。VID/PID 为空表示不限制该字段；SerialNumber 可用于只共享指定 UKey。
/// </summary>
public sealed record UsbIpAutoShareRule
{
    public ushort? VendorId { get; init; }
    public ushort? ProductId { get; init; }
    public string? SerialNumber { get; init; }
    public string? BusIdPrefix { get; init; }
    public bool Enabled { get; init; } = true;

    public bool IsMatch(UsbIpDeviceInfo device)
    {
        if (!Enabled) return false;
        if (VendorId is not null && VendorId != device.VendorId) return false;
        if (ProductId is not null && ProductId != device.ProductId) return false;
        if (!string.IsNullOrWhiteSpace(SerialNumber) && !string.Equals(SerialNumber, device.SerialNumber, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrWhiteSpace(BusIdPrefix) && !device.BusId.StartsWith(BusIdPrefix, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }
}

/// <summary>设备监控配置。</summary>
public sealed record UsbIpMonitorOptions
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);
    public bool EmitInitialDevices { get; init; } = true;
}

/// <summary>客户端连接状态。</summary>
public enum UsbIpConnectionState
{
    Disconnected = 0,
    Connecting = 1,
    Connected = 2,
    Reconnecting = 3,
    Faulted = 4,
}

/// <summary>受管连接信息。</summary>
public sealed record UsbIpManagedConnection
{
    public required string ConnectionId { get; init; }
    public required string Host { get; init; }
    public required string BusId { get; init; }
    public int ServerPort { get; init; } = 3240;
    public int? LocalPort { get; init; }
    public UsbIpConnectionState State { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastConnectedAt { get; init; }
    public string? LastError { get; init; }
}

/// <summary>自动恢复配置。</summary>
public sealed record UsbIpReconnectOptions
{
    public bool Enabled { get; init; } = true;
    public TimeSpan CheckInterval { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(3);
    public int MaxConsecutiveFailures { get; init; } = 0; // 0 表示无限重试
}

/// <summary>健康检查项。</summary>
public sealed record UsbIpHealthCheck(string Name, bool Success, string Message, string? Suggestion = null);

/// <summary>健康检查报告。</summary>
public sealed record UsbIpHealthReport(
    DateTimeOffset CheckedAt,
    bool Healthy,
    IReadOnlyList<UsbIpHealthCheck> Checks);
