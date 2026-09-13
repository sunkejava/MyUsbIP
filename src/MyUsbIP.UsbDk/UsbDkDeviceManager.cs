using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using MyUsbIP.Abstractions;
using MyUsbIP.Protocol;

namespace MyUsbIP.UsbDk;

/// <summary>
/// UsbDk 设备管理器。负责枚举、独占重定向、描述符读取以及 USB 传输。
/// </summary>
public sealed class UsbDkDeviceManager : IDisposable
{
    private readonly ConcurrentDictionary<string, RedirectedDevice> redirected = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(string BusId, uint Sequence), ulong> pendingEndpoints = new();
    private bool disposed;

    public Task<IReadOnlyList<UsbIpDeviceInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        EnsureWindows();
        cancellationToken.ThrowIfCancellationRequested();

        var devices = new Dictionary<string, UsbIpDeviceInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in EnumerateNativeDevices())
        {
            var info = ToDeviceInfo(snapshot);
            devices[info.BusId] = info;
        }

        // UsbDk_StartRedirect 后，部分设备可能暂时/持续不再出现在 UsbDk_GetDevicesList 中，
        // 因此必须保留 Redirect 快照以支持 CH340/UKey detach 后再次 IMPORT。
        // 但物理拔出后 Windows PnP 节点会消失；此时不能继续把旧快照暴露给 DEVLIST。
        foreach (var pair in redirected.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!WindowsDevicePresence.IsPresent(pair.Value.Native.Id.InstanceId))
            {
                if (redirected.TryRemove(pair.Key, out var removed))
                {
                    foreach (var key in pendingEndpoints.Keys
                                 .Where(x => string.Equals(x.BusId, pair.Key, StringComparison.OrdinalIgnoreCase))
                                 .ToArray())
                        pendingEndpoints.TryRemove(key, out _);

                    try { removed.Dispose(); }
                    catch { /* 设备已经物理消失，StopRedirect 失败不应阻断设备列表刷新。 */ }
                }
                devices.Remove(pair.Key);
                continue;
            }

            var info = ToDeviceInfo(pair.Value.Native) with { State = UsbIpDeviceState.Shared };
            devices[pair.Key] = info;
        }

        IReadOnlyList<UsbIpDeviceInfo> result = devices.Values.ToArray();
        return Task.FromResult(result);
    }

    public Task ShareAsync(string busId, CancellationToken cancellationToken = default)
    {
        EnsureWindows();
        cancellationToken.ThrowIfCancellationRequested();
        if (redirected.ContainsKey(busId)) return Task.CompletedTask;

        var native = FindNativeDevice(busId)
                     ?? throw new InvalidOperationException($"UsbDk 设备 {busId} 不存在。 ");

        var id = native.Id;
        var handle = UsbDkNative.UsbDk_StartRedirect(ref id);
        if (handle == 0 || handle == UsbDkNative.InvalidHandleValue)
            UsbDkNative.ThrowLastWin32($"UsbDk_StartRedirect({busId}) 失败");

        var device = new RedirectedDevice(native, handle, ReadConfigurationDescriptors(native));
        if (!redirected.TryAdd(busId, device)) UsbDkNative.UsbDk_StopRedirect(handle);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 重置已重定向设备但保持 Redirect 句柄。
    /// 用于 USB/IP detach 后清理设备/端点状态，同时避免部分 USB 串口设备 StopRedirect 后无法二次 StartRedirect。
    /// </summary>
    public Task ResetAsync(string busId, CancellationToken cancellationToken = default)
    {
        EnsureWindows();
        cancellationToken.ThrowIfCancellationRequested();
        if (!redirected.TryGetValue(busId, out var device)) return Task.CompletedTask;

        foreach (var key in pendingEndpoints.Keys.Where(x => string.Equals(x.BusId, busId, StringComparison.OrdinalIgnoreCase)).ToArray())
            pendingEndpoints.TryRemove(key, out _);

        if (!UsbDkNative.UsbDk_ResetDevice(device.Handle))
            UsbDkNative.ThrowLastWin32($"UsbDk_ResetDevice({busId}) 失败");

        return Task.CompletedTask;
    }

    public Task UnshareAsync(string busId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!redirected.TryRemove(busId, out var device)) return Task.CompletedTask;
        device.Dispose();
        return Task.CompletedTask;
    }

    public Task<UsbIpSubmitCompletion> SubmitAsync(string busId, UsbIpSubmitRequest request, CancellationToken cancellationToken = default)
    {
        EnsureWindows();
        if (!redirected.TryGetValue(busId, out var device))
            throw new InvalidOperationException($"设备 {busId} 尚未通过 UsbDk 重定向。 ");

        return Task.Run(() => SubmitCore(device, busId, request, cancellationToken), cancellationToken);
    }

    public Task CancelAsync(string busId, uint sequence, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!redirected.TryGetValue(busId, out var device)) return Task.CompletedTask;
        if (!pendingEndpoints.TryRemove((busId, sequence), out var endpoint)) return Task.CompletedTask;

        UsbDkNative.UsbDk_AbortPipe(device.Handle, endpoint);
        UsbDkNative.UsbDk_ResetPipe(device.Handle, endpoint);
        return Task.CompletedTask;
    }

    public UsbDescriptorSet GetDescriptorSet(string busId)
    {
        UsbDkDeviceInfoNative native;
        if (redirected.TryGetValue(busId, out var redirectedDevice))
        {
            native = redirectedDevice.Native;
        }
        else
        {
            native = FindNativeDevice(busId)
                     ?? throw new InvalidOperationException($"UsbDk 设备 {busId} 不存在。 ");
        }

        var configs = redirected.TryGetValue(busId, out redirectedDevice)
            ? redirectedDevice.ConfigurationDescriptors
            : ReadConfigurationDescriptors(native);

        return new UsbDescriptorSet(
            SerializeDeviceDescriptor(native.DeviceDescriptor),
            configs.FirstOrDefault() ?? Array.Empty<byte>(),
            Array.Empty<byte>(),
            Array.Empty<byte>());
    }

    private UsbIpSubmitCompletion SubmitCore(RedirectedDevice device, string busId, UsbIpSubmitRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var endpoint = (ulong)(request.Endpoint & 0x0F);
        if (request.Direction != 0) endpoint |= 0x80;
        var transferType = ResolveTransferType(device, endpoint);
        var isControl = request.Endpoint == 0;
        var prefixLength = isControl ? 8 : 0;
        var dataLength = Math.Max(0, request.TransferBufferLength);
        var bufferLength = checked(prefixLength + dataLength);

        var buffer = Marshal.AllocHGlobal(Math.Max(1, bufferLength));
        var eventHandle = UsbDkNative.CreateEventW(0, false, false, null);
        if (eventHandle == 0)
        {
            Marshal.FreeHGlobal(buffer);
            UsbDkNative.ThrowLastWin32("CreateEventW 失败");
        }

        var overlappedPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlappedData>());
        var requestPtr = Marshal.AllocHGlobal(Marshal.SizeOf<UsbDkTransferRequest>());
        var pendingKey = (busId, request.Sequence);
        try
        {
            if (bufferLength > 0) Marshal.Copy(new byte[bufferLength], 0, buffer, bufferLength);

            if (isControl)
            {
                var setup = request.SetupPacket.AsSpan(0, Math.Min(8, request.SetupPacket.Length)).ToArray();
                Marshal.Copy(setup, 0, buffer, setup.Length);
                if (request.Direction == 0 && request.Payload.Length > 0)
                    Marshal.Copy(request.Payload, 0, buffer + 8, Math.Min(request.Payload.Length, dataLength));
            }
            else if (request.Direction == 0 && request.Payload.Length > 0)
            {
                Marshal.Copy(request.Payload, 0, buffer, Math.Min(request.Payload.Length, dataLength));
            }

            Marshal.StructureToPtr(new NativeOverlappedData { EventHandle = eventHandle }, overlappedPtr, false);
            Marshal.StructureToPtr(new UsbDkTransferRequest
            {
                EndpointAddress = endpoint,
                Buffer = buffer,
                BufferLength = checked((ulong)bufferLength),
                TransferType = (ulong)transferType,
            }, requestPtr, false);

            pendingEndpoints[pendingKey] = endpoint;
            var result = request.Direction != 0
                ? UsbDkNative.UsbDk_ReadPipe(device.Handle, requestPtr, overlappedPtr)
                : UsbDkNative.UsbDk_WritePipe(device.Handle, requestPtr, overlappedPtr);

            if (result == UsbDkTransferResult.SuccessAsync)
            {
                var systemHandle = UsbDkNative.UsbDk_GetRedirectorSystemHandle(device.Handle);
                if (systemHandle == 0 || systemHandle == UsbDkNative.InvalidHandleValue)
                    throw new IOException("UsbDk_GetRedirectorSystemHandle 返回无效句柄。 ");
                if (!UsbDkNative.GetOverlappedResult(systemHandle, overlappedPtr, out _, true))
                    UsbDkNative.ThrowLastWin32("UsbDk 异步传输完成失败");
            }
            else if (result == UsbDkTransferResult.Failure)
            {
                throw new IOException($"UsbDk 传输失败，BusId={busId}, EP=0x{endpoint:X2}。 ");
            }

            var nativeRequest = Marshal.PtrToStructure<UsbDkTransferRequest>(requestPtr);
            var transferred = checked((int)Math.Min((ulong)int.MaxValue, nativeRequest.Result.Generic.BytesTransferred));
            var status = nativeRequest.Result.Generic.UsbdStatus == 0 ? 0 : -5;
            var actualLength = Math.Max(0, transferred);

            byte[] payload = Array.Empty<byte>();
            if (request.Direction != 0 && actualLength > 0)
            {
                payload = new byte[Math.Min(actualLength, dataLength)];
                Marshal.Copy(buffer + prefixLength, payload, 0, payload.Length);
            }

            return new UsbIpSubmitCompletion(
                request.Sequence,
                request.DeviceId,
                request.Direction,
                request.Endpoint,
                status,
                actualLength,
                request.StartFrame,
                request.NumberOfPackets,
                status == 0 ? 0 : 1,
                payload);
        }
        finally
        {
            pendingEndpoints.TryRemove(pendingKey, out _);
            Marshal.FreeHGlobal(requestPtr);
            Marshal.FreeHGlobal(overlappedPtr);
            UsbDkNative.CloseHandle(eventHandle);
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static UsbDkTransferType ResolveTransferType(RedirectedDevice device, ulong endpoint)
    {
        if ((endpoint & 0x0F) == 0) return UsbDkTransferType.Control;
        if (device.EndpointTypes.TryGetValue((byte)endpoint, out var type)) return type;
        return UsbDkTransferType.Bulk;
    }

    private UsbIpDeviceInfo ToDeviceInfo(UsbDkDeviceInfoNative native)
    {
        var busId = GetBusId(native);
        return new UsbIpDeviceInfo
        {
            BusId = busId,
            InstanceId = native.Id.InstanceId,
            Path = native.Id.InstanceId,
            BusNumber = unchecked((uint)native.FilterId),
            DeviceNumber = unchecked((uint)native.Port),
            Speed = MapUsbDkSpeed(native.Speed),
            VendorId = native.DeviceDescriptor.VendorId,
            ProductId = native.DeviceDescriptor.ProductId,
            UsbVersion = native.DeviceDescriptor.BcdUsb,
            DeviceVersion = native.DeviceDescriptor.BcdDevice,
            DeviceClass = native.DeviceDescriptor.DeviceClass,
            DeviceSubClass = native.DeviceDescriptor.DeviceSubClass,
            DeviceProtocol = native.DeviceDescriptor.DeviceProtocol,
            ConfigurationCount = native.DeviceDescriptor.NumberConfigurations == 0 ? (byte)1 : native.DeviceDescriptor.NumberConfigurations,
            Product = native.Id.DeviceId,
            State = redirected.ContainsKey(busId) ? UsbIpDeviceState.Shared : UsbIpDeviceState.Available,
        };
    }

    private static uint MapUsbDkSpeed(ulong speed) => speed switch
    {
        1 => 1,
        2 => 2,
        3 => 3,
        4 => 5,
        5 => 6,
        _ => 0,
    };

    private static string GetBusId(UsbDkDeviceInfoNative native)
        => $"{unchecked((uint)native.FilterId):X8}-{unchecked((uint)native.Port):X8}";

    private static UsbDkDeviceInfoNative? FindNativeDevice(string busId)
    {
        foreach (var item in EnumerateNativeDevices())
        {
            if (string.Equals(GetBusId(item), busId, StringComparison.OrdinalIgnoreCase)) return item;
        }
        return null;
    }

    private static IReadOnlyList<UsbDkDeviceInfoNative> EnumerateNativeDevices()
    {
        if (!UsbDkNative.UsbDk_GetDevicesList(out var basePtr, out var count))
            UsbDkNative.ThrowLastWin32("UsbDk_GetDevicesList 失败");

        try
        {
            var result = new List<UsbDkDeviceInfoNative>(checked((int)count));
            var size = Marshal.SizeOf<UsbDkDeviceInfoNative>();
            for (uint i = 0; i < count; i++)
            {
                var ptr = basePtr + checked((int)i * size);
                result.Add(Marshal.PtrToStructure<UsbDkDeviceInfoNative>(ptr));
            }
            return result;
        }
        finally
        {
            if (basePtr != 0) UsbDkNative.UsbDk_ReleaseDevicesList(basePtr);
        }
    }

    private static IReadOnlyList<byte[]> ReadConfigurationDescriptors(UsbDkDeviceInfoNative native)
    {
        var count = Math.Max(1, (int)native.DeviceDescriptor.NumberConfigurations);
        var result = new List<byte[]>(count);
        for (ulong index = 0; index < (ulong)count; index++)
        {
            var request = new UsbDkConfigDescriptorRequest { Id = native.Id, Index = index };
            if (!UsbDkNative.UsbDk_GetConfigurationDescriptor(ref request, out var descriptor, out var length)) continue;
            try
            {
                if (descriptor == 0 || length == 0) continue;
                var bytes = new byte[checked((int)length)];
                Marshal.Copy(descriptor, bytes, 0, bytes.Length);
                result.Add(bytes);
            }
            finally
            {
                if (descriptor != 0) UsbDkNative.UsbDk_ReleaseConfigurationDescriptor(descriptor);
            }
        }
        return result;
    }

    private static byte[] SerializeDeviceDescriptor(UsbDeviceDescriptor descriptor)
    {
        var bytes = new byte[18];
        bytes[0] = descriptor.Length;
        bytes[1] = descriptor.DescriptorType;
        WriteU16(2, descriptor.BcdUsb);
        bytes[4] = descriptor.DeviceClass;
        bytes[5] = descriptor.DeviceSubClass;
        bytes[6] = descriptor.DeviceProtocol;
        bytes[7] = descriptor.MaxPacketSize0;
        WriteU16(8, descriptor.VendorId);
        WriteU16(10, descriptor.ProductId);
        WriteU16(12, descriptor.BcdDevice);
        bytes[14] = descriptor.ManufacturerIndex;
        bytes[15] = descriptor.ProductIndex;
        bytes[16] = descriptor.SerialNumberIndex;
        bytes[17] = descriptor.NumberConfigurations;
        return bytes;

        void WriteU16(int offset, ushort value)
        {
            bytes[offset] = (byte)value;
            bytes[offset + 1] = (byte)(value >> 8);
        }
    }

    private static Dictionary<byte, UsbDkTransferType> ParseEndpointTypes(IEnumerable<byte[]> configs)
    {
        var result = new Dictionary<byte, UsbDkTransferType>();
        foreach (var config in configs)
        {
            var offset = 0;
            while (offset + 2 <= config.Length)
            {
                var length = config[offset];
                var type = config[offset + 1];
                if (length < 2 || offset + length > config.Length) break;
                if (type == 5 && length >= 7)
                {
                    var address = config[offset + 2];
                    result[address] = (config[offset + 3] & 0x03) switch
                    {
                        1 => UsbDkTransferType.Isochronous,
                        2 => UsbDkTransferType.Bulk,
                        3 => UsbDkTransferType.Interrupt,
                        _ => UsbDkTransferType.Control,
                    };
                }
                offset += length;
            }
        }
        return result;
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("UsbDk 后端仅支持 Windows。 ");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var item in redirected.Values) item.Dispose();
        redirected.Clear();
        GC.SuppressFinalize(this);
    }

    private sealed class RedirectedDevice : IDisposable
    {
        public RedirectedDevice(UsbDkDeviceInfoNative native, nint handle, IReadOnlyList<byte[]> configs)
        {
            Native = native;
            Handle = handle;
            ConfigurationDescriptors = configs.Select(x => x.ToArray()).ToArray();
            EndpointTypes = ParseEndpointTypes(ConfigurationDescriptors);
        }

        public UsbDkDeviceInfoNative Native { get; }
        public nint Handle { get; }
        public IReadOnlyList<byte[]> ConfigurationDescriptors { get; }
        public Dictionary<byte, UsbDkTransferType> EndpointTypes { get; }

        public void Dispose()
        {
            if (Handle != 0 && Handle != UsbDkNative.InvalidHandleValue)
                UsbDkNative.UsbDk_StopRedirect(Handle);
        }
    }
}
