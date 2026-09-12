using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<string, byte> activeSessions = new(StringComparer.OrdinalIgnoreCase);

    public UsbDkExportTransport(UsbDkDeviceManager manager)
        => this.manager = manager ?? throw new ArgumentNullException(nameof(manager));

    public async Task<IReadOnlyList<UsbIpDeviceInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        var devices = await manager.ListAsync(cancellationToken).ConfigureAwait(false);
        return devices.Select(x => activeSessions.ContainsKey(x.BusId)
            ? x with { State = UsbIpDeviceState.Attached }
            : x).ToArray();
    }

    public async Task<UsbIpDeviceInfo?> FindAsync(string busId, CancellationToken cancellationToken = default)
        => (await ListAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(x => string.Equals(x.BusId, busId, StringComparison.OrdinalIgnoreCase));

    public async Task BeginSessionAsync(string busId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!activeSessions.TryAdd(busId, 0))
            throw new InvalidOperationException($"设备 {busId} 已被其他 USB/IP 会话占用。 ");

        try
        {
            await manager.ShareAsync(busId, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            activeSessions.TryRemove(busId, out _);
            throw;
        }
    }

    public Task EndSessionAsync(string busId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        activeSessions.TryRemove(busId, out _);

        // 保持 UsbDk Redirect，便于客户端断线后快速重连；这里只释放网络会话独占锁。
        // 管理层执行 Unshare 时才真正 UsbDk_StopRedirect。
        return Task.CompletedTask;
    }

    public Task<UsbIpSubmitCompletion> SubmitAsync(string busId, UsbIpSubmitRequest request, CancellationToken cancellationToken = default)
    {
        if (!activeSessions.ContainsKey(busId))
            throw new InvalidOperationException($"设备 {busId} 当前没有活动 USB/IP 会话。 ");
        return manager.SubmitAsync(busId, request, cancellationToken);
    }

    public Task CancelAsync(string busId, uint sequence, CancellationToken cancellationToken = default)
        => manager.CancelAsync(busId, sequence, cancellationToken);

    public Task<UsbDescriptorSet> GetDescriptorSetAsync(string busId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(manager.GetDescriptorSet(busId));
    }
}
