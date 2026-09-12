namespace MyUsbIP.Abstractions;

/// <summary>
/// 服务器端平台后端。
/// Windows/Linux 的具体实现只负责与本机 USB/IP 驱动或工具交互，不承担业务授权逻辑。
/// </summary>
public interface IUsbIpServerBackend
{
    Task<UsbIpBackendCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<UsbIpDeviceInfo>> ListDevicesAsync(CancellationToken cancellationToken = default);
    Task ShareAsync(string busId, CancellationToken cancellationToken = default);
    Task UnshareAsync(string busId, CancellationToken cancellationToken = default);
}

/// <summary>
/// 客户端平台后端。
/// 负责把远端 USB/IP 设备挂载到当前系统的虚拟 USB Host Controller。
/// </summary>
public interface IUsbIpClientBackend
{
    Task<UsbIpBackendCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<UsbIpDeviceInfo>> ListRemoteDevicesAsync(string host, int port = 3240, CancellationToken cancellationToken = default);
    Task<UsbIpAttachResult> AttachAsync(string host, string busId, int port = 3240, CancellationToken cancellationToken = default);
    Task DetachAsync(int port, CancellationToken cancellationToken = default);
}

/// <summary>MyUsbIP 服务器端统一入口。</summary>
public interface IMyUsbIpServer
{
    Task<IReadOnlyList<UsbIpDeviceInfo>> GetDevicesAsync(CancellationToken cancellationToken = default);
    Task ShareAsync(string busId, CancellationToken cancellationToken = default);
    Task UnshareAsync(string busId, CancellationToken cancellationToken = default);
}

/// <summary>MyUsbIP 客户端统一入口。</summary>
public interface IMyUsbIpClient
{
    Task<IReadOnlyList<UsbIpDeviceInfo>> GetRemoteDevicesAsync(string host, int port = 3240, CancellationToken cancellationToken = default);
    Task<UsbIpAttachResult> AttachAsync(string host, string busId, int port = 3240, CancellationToken cancellationToken = default);
    Task DetachAsync(int port, CancellationToken cancellationToken = default);
}

/// <summary>
/// 结构化事件接收器。调用方可以把事件写入文件、数据库、MQTT、OpenTelemetry 或自己的告警系统。
/// </summary>
public interface IUsbIpEventSink
{
    ValueTask WriteAsync(UsbIpEvent evt, CancellationToken cancellationToken = default);
}
