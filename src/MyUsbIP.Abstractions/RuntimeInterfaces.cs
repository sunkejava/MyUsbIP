namespace MyUsbIP.Abstractions;

/// <summary>跨平台 USB 设备变化监控。</summary>
public interface IUsbIpDeviceMonitor
{
    IAsyncEnumerable<UsbIpDeviceChange> WatchAsync(
        UsbIpMonitorOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>服务器自动共享服务。</summary>
public interface IUsbIpAutoShareService
{
    Task RunAsync(
        IReadOnlyList<UsbIpAutoShareRule> rules,
        UsbIpMonitorOptions? monitorOptions = null,
        CancellationToken cancellationToken = default);
}

/// <summary>客户端受管连接服务，负责连接状态记录与掉线恢复。</summary>
public interface IUsbIpConnectionManager
{
    IReadOnlyList<UsbIpManagedConnection> GetConnections();
    Task<UsbIpManagedConnection> ConnectAsync(
        string host,
        string busId,
        int port = 3240,
        CancellationToken cancellationToken = default);
    Task DisconnectAsync(string connectionId, CancellationToken cancellationToken = default);
    Task RunRecoveryLoopAsync(UsbIpReconnectOptions? options = null, CancellationToken cancellationToken = default);
}

/// <summary>部署环境与后端健康检查。</summary>
public interface IUsbIpDiagnosticsService
{
    Task<UsbIpHealthReport> CheckServerAsync(CancellationToken cancellationToken = default);
    Task<UsbIpHealthReport> CheckClientAsync(CancellationToken cancellationToken = default);
}
