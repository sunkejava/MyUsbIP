using MyUsbIP.Abstractions;
using MyUsbIP.NativeServer;
using MyUsbIP.Protocol;

namespace MyUsbIP.UsbDk;

/// <summary>基于 UsbDk 的 Windows 服务端管理后端。</summary>
public sealed class UsbDkServerBackend : IUsbIpServerBackend
{
    private readonly UsbDkDeviceManager manager;

    public UsbDkServerBackend(UsbDkDeviceManager manager)
        => this.manager = manager ?? throw new ArgumentNullException(nameof(manager));

    public Task<UsbIpBackendCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new UsbIpBackendCapabilities(
            CanList: true,
            CanShare: true,
            CanAttach: false,
            CanDetach: false,
            BackendName: "Windows UsbDk Capture"));

    public Task<IReadOnlyList<UsbIpDeviceInfo>> ListDevicesAsync(CancellationToken cancellationToken = default)
        => manager.ListAsync(cancellationToken);

    public Task ShareAsync(string busId, CancellationToken cancellationToken = default)
        => manager.ShareAsync(busId, cancellationToken);

    public Task UnshareAsync(string busId, CancellationToken cancellationToken = default)
        => manager.UnshareAsync(busId, cancellationToken);
}

/// <summary>
/// MyUsbIP NativeServer 与 UsbDk 之间的数据面适配器。
/// 标准 USB/IP SUBMIT 最终转换为 UsbDk ReadPipe/WritePipe。
/// </summary>
public sealed class UsbDkExportTransport : IUsbIpExportTransport, IUsbDescriptorProvider
{
    private readonly UsbDkDeviceManager manager;

    public UsbDkExportTransport(UsbDkDeviceManager manager)
        => this.manager = manager ?? throw new ArgumentNullException(nameof(manager));

    public Task<IReadOnlyList<UsbIpDeviceInfo>> ListAsync(CancellationToken cancellationToken = default)
        => manager.ListAsync(cancellationToken);

    public async Task<UsbIpDeviceInfo?> FindAsync(string busId, CancellationToken cancellationToken = default)
        => (await manager.ListAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(x => string.Equals(x.BusId, busId, StringComparison.OrdinalIgnoreCase));

    public Task BeginSessionAsync(string busId, CancellationToken cancellationToken = default)
        => manager.ShareAsync(busId, cancellationToken);

    public Task EndSessionAsync(string busId, CancellationToken cancellationToken = default)
    {
        // Share 生命周期由管理层控制。TCP 会话断开时不自动释放设备，方便自动重连。
        return Task.CompletedTask;
    }

    public Task<UsbIpSubmitCompletion> SubmitAsync(string busId, UsbIpSubmitRequest request, CancellationToken cancellationToken = default)
        => manager.SubmitAsync(busId, request, cancellationToken);

    public Task CancelAsync(string busId, uint sequence, CancellationToken cancellationToken = default)
        => manager.CancelAsync(busId, sequence, cancellationToken);

    public Task<UsbDescriptorSet> GetDescriptorSetAsync(string busId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(manager.GetDescriptorSet(busId));
    }
}
