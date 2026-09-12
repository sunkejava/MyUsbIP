using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
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
        var speeds = ReadNativeSpeeds();
        return devices.Select(x => DecorateWireMetadata(
            x,
            activeSessions.ContainsKey(x.BusId),
            speeds.TryGetValue(x.BusId, out var speed) ? speed : x.Speed)).ToArray();
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
            // UsbDk 对部分 USB 串口设备（包括部分 CH340 类设备）执行 StopRedirect 后，
            // 再次 StartRedirect 可能失败或长时间阻塞。因此 Redirect 句柄作为服务端设备捕获生命周期保留。
            // 首次 IMPORT 只执行 StartRedirect；后续会话复用上一次 EndSession 已 Reset 的句柄。
            await manager.ShareAsync(busId, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            activeSessions.TryRemove(busId, out _);
            throw;
        }
    }

    public async Task EndSessionAsync(string busId, CancellationToken cancellationToken = default)
    {
        // 先无条件释放 USB/IP 会话所有权。
        // 即使 UsbDk ResetDevice 自身异常/阻塞，也不能让 BUSID 永久处于 Busy 状态。
        activeSessions.TryRemove(busId, out _);
        await manager.ResetAsync(busId, cancellationToken).ConfigureAwait(false);
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

    private static UsbIpDeviceInfo DecorateWireMetadata(UsbIpDeviceInfo device, bool attached, uint speed)
    {
        uint busNumber = 0;
        uint deviceNumber = 0;
        var parts = device.BusId.Split('-', 2, StringSplitOptions.TrimEntries);
        if (parts.Length == 2)
        {
            _ = uint.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out busNumber);
            _ = uint.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out deviceNumber);
        }

        return device with
        {
            Path = device.InstanceId ?? device.BusId,
            BusNumber = busNumber,
            DeviceNumber = deviceNumber,
            Speed = speed,
            State = attached ? UsbIpDeviceState.Attached : device.State,
        };
    }

    /// <summary>
    /// UsbDk Speed：1=Low、2=Full、3=High、4=Super；
    /// USB/IP 在 3 与 5 之间额外保留了 Wireless=4，因此 Super 需要映射为 5。
    /// </summary>
    private static Dictionary<string, uint> ReadNativeSpeeds()
    {
        var result = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        if (!OperatingSystem.IsWindows()) return result;
        if (!UsbDkNative.UsbDk_GetDevicesList(out var basePtr, out var count)) return result;

        try
        {
            var size = Marshal.SizeOf<UsbDkDeviceInfoNative>();
            for (uint i = 0; i < count; i++)
            {
                var ptr = basePtr + checked((int)i * size);
                var native = Marshal.PtrToStructure<UsbDkDeviceInfoNative>(ptr);
                var busId = $"{unchecked((uint)native.FilterId):X8}-{unchecked((uint)native.Port):X8}";
                result[busId] = native.Speed switch
                {
                    1 => 1,
                    2 => 2,
                    3 => 3,
                    4 => 5,
                    5 => 6,
                    _ => 0,
                };
            }
        }
        finally
        {
            if (basePtr != 0) UsbDkNative.UsbDk_ReleaseDevicesList(basePtr);
        }

        return result;
    }
}
