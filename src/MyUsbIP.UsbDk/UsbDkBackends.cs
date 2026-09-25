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
            speeds.TryGetValue(x.BusId, out var speed) ? speed : x.Speed,
            TryReadDescriptorMetadata(x.BusId))).ToArray();
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
            manager.MarkSessionActive(busId);
        }
        catch
        {
            manager.MarkSessionInactive(busId);
            activeSessions.TryRemove(busId, out _);
            throw;
        }
    }

    /// <summary>
    /// USB/IP 会话结束后必须真正停止 UsbDk Redirect，把设备归还 Windows 原驱动栈。
    ///
    /// 旧实现为了规避部分 USB 串口设备二次 StartRedirect 的 UsbDk 已知问题，长期保留 Redirect 句柄并调用
    /// UsbDk_ResetDevice。该策略会让设备在服务端继续以 UsbDk device/重定向驱动形态存在，并且 ResetDevice
    /// 可能触发端口复位、PnP 重建以及后续枚举抖动，表现为 detach 后驱动类型变化或设备偶发消失。
    ///
    /// 新策略：结束会话 -> StopRedirect -> 等待 Windows 原驱动重新绑定且 UsbDk 原生枚举连续稳定 -> 才完成释放。
    /// 这样下一次 IMPORT 从一个正常的宿主驱动状态重新执行 StartRedirect，避免在尚未完成 PnP 回绑时立即二次捕获。
    /// </summary>
    public async Task EndSessionAsync(string busId, CancellationToken cancellationToken = default)
    {
        // BUSID 的会话占用必须保持到 StopRedirect + 宿主驱动回绑全部完成。
        // 如果一进入 EndSession 就释放 activeSessions，新客户端可能在旧会话仍清理时 BeginSession：
        // ShareAsync 会看到旧 Redirect 仍存在并直接返回，随后旧 EndSession 又执行 Unshare，
        // 造成“新会话刚成功，旧会话把设备 StopRedirect 掉”的竞态。
        UsbIpDeviceInfo? releasedDevice = null;
        try
        {
            try
            {
                releasedDevice = (await manager.ListAsync(CancellationToken.None).ConfigureAwait(false))
                    .FirstOrDefault(x => string.Equals(x.BusId, busId, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                // 保存身份仅用于释放后的稳定性等待；读取失败不能阻止真正 StopRedirect。
            }

            await manager.UnshareAsync(busId, CancellationToken.None).ConfigureAwait(false);

            if (releasedDevice is not null)
                await WaitForHostDriverRebindAsync(releasedDevice, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            manager.MarkSessionInactive(busId);
            activeSessions.TryRemove(busId, out _);
        }
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

    /// <summary>
    /// StopRedirect 返回后 Windows 仍需要一小段时间重新加载设备原驱动。
    /// 对 CH340/智能卡/UKey 这类驱动启动较慢的设备，立即再次 StartRedirect 容易命中 UsbDk 二次重定向失败。
    /// 这里要求同一物理身份连续三次出现在 UsbDk 原生列表中，再认为宿主驱动/PnP 已稳定。
    /// </summary>
    private async Task WaitForHostDriverRebindAsync(UsbIpDeviceInfo releasedDevice, CancellationToken cancellationToken)
    {
        const int maxAttempts = 32;      // 最长约 8 秒
        const int requiredStableHits = 3;
        var stableHits = 0;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);

            IReadOnlyList<UsbIpDeviceInfo> devices;
            try
            {
                devices = await manager.ListAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                stableHits = 0;
                continue;
            }

            var rebound = devices.Any(x =>
                x.State != UsbIpDeviceState.Attached &&
                x.VendorId == releasedDevice.VendorId &&
                x.ProductId == releasedDevice.ProductId &&
                string.Equals(x.Product, releasedDevice.Product, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(releasedDevice.InstanceId) ||
                 string.Equals(x.InstanceId, releasedDevice.InstanceId, StringComparison.OrdinalIgnoreCase)));

            if (rebound)
            {
                stableHits++;
                if (stableHits >= requiredStableHits) return;
            }
            else
            {
                stableHits = 0;
            }
        }

        throw new TimeoutException(
            $"设备 {releasedDevice.BusId} 已停止 UsbDk Redirect，但等待 Windows 原驱动重新绑定超时。" +
            $" VID:PID={releasedDevice.VidPid}, InstanceId={releasedDevice.InstanceId ?? "<null>"}。 ");
    }

    private DescriptorMetadata TryReadDescriptorMetadata(string busId)
    {
        try
        {
            var set = manager.GetDescriptorSet(busId);
            var d = set.DeviceDescriptor;
            var c = set.ConfigurationDescriptor;
            var interfaces = ParseInterfaces(c);
            return new DescriptorMetadata(
                d.Length >= 18 ? ReadUInt16LittleEndian(d, 2) : (ushort)0,
                d.Length >= 18 ? ReadUInt16LittleEndian(d, 12) : (ushort)0,
                d.Length >= 18 ? d[4] : (byte)0,
                d.Length >= 18 ? d[5] : (byte)0,
                d.Length >= 18 ? d[6] : (byte)0,
                d.Length >= 18 ? d[17] : (byte)1,
                c.Length >= 6 ? c[5] : (byte)1,
                c.Length >= 5 ? c[4] : (byte)interfaces.Count,
                interfaces);
        }
        catch
        {
            return default;
        }
    }

    private static IReadOnlyList<UsbIpInterfaceInfo> ParseInterfaces(byte[] configuration)
    {
        if (configuration.Length < 9) return Array.Empty<UsbIpInterfaceInfo>();

        // 一个接口可能包含多个 Alternate Setting。USB/IP DEVLIST 只需要每个 interface 的一条
        // class/subclass/protocol，因此优先保留 AlternateSetting=0，按 InterfaceNumber 排序输出。
        var byNumber = new SortedDictionary<byte, UsbIpInterfaceInfo>();
        var offset = 0;
        while (offset + 2 <= configuration.Length)
        {
            var length = configuration[offset];
            var type = configuration[offset + 1];
            if (length < 2 || offset + length > configuration.Length) break;

            if (type == 4 && length >= 9)
            {
                var interfaceNumber = configuration[offset + 2];
                var alternateSetting = configuration[offset + 3];
                var info = new UsbIpInterfaceInfo
                {
                    Class = configuration[offset + 5],
                    SubClass = configuration[offset + 6],
                    Protocol = configuration[offset + 7],
                };

                if (alternateSetting == 0 || !byNumber.ContainsKey(interfaceNumber))
                    byNumber[interfaceNumber] = info;
            }

            offset += length;
        }

        return byNumber.Values.ToArray();
    }

    private static ushort ReadUInt16LittleEndian(byte[] data, int offset)
        => (ushort)(data[offset] | (data[offset + 1] << 8));

    private static UsbIpDeviceInfo DecorateWireMetadata(UsbIpDeviceInfo device, bool attached, uint speed,
        DescriptorMetadata descriptor)
    {
        uint busNumber = 0;
        uint deviceNumber = 0;
        var parts = device.BusId.Split('-', 2, StringSplitOptions.TrimEntries);
        if (parts.Length == 2)
        {
            _ = uint.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out busNumber);
            _ = uint.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out deviceNumber);
        }

        var interfaces = descriptor.InterfaceItems ?? Array.Empty<UsbIpInterfaceInfo>();
        var interfaceCount = descriptor.InterfaceCount;
        if (interfaceCount == 0 && interfaces.Count > 0)
            interfaceCount = checked((byte)Math.Min(byte.MaxValue, interfaces.Count));

        return device with
        {
            Path = device.InstanceId ?? device.BusId,
            BusNumber = busNumber,
            DeviceNumber = deviceNumber,
            Speed = speed,
            State = attached ? UsbIpDeviceState.Attached : device.State,
            UsbVersion = descriptor.UsbVersion,
            DeviceVersion = descriptor.DeviceVersion,
            DeviceClass = descriptor.DeviceClass,
            DeviceSubClass = descriptor.DeviceSubClass,
            DeviceProtocol = descriptor.DeviceProtocol,
            ConfigurationCount = descriptor.ConfigurationCount == 0 ? (byte)1 : descriptor.ConfigurationCount,
            ConfigurationValue = descriptor.ConfigurationValue == 0 ? (byte)1 : descriptor.ConfigurationValue,
            InterfaceCount = interfaceCount,
            Interfaces = interfaces,
        };
    }

    private readonly record struct DescriptorMetadata(
        ushort UsbVersion,
        ushort DeviceVersion,
        byte DeviceClass,
        byte DeviceSubClass,
        byte DeviceProtocol,
        byte ConfigurationCount,
        byte ConfigurationValue,
        byte InterfaceCount,
        IReadOnlyList<UsbIpInterfaceInfo>? InterfaceItems);

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
