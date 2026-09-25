using System.Collections.Concurrent;
using System.ComponentModel;
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
    private readonly ConcurrentDictionary<string, byte> activeSessionBusIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(string BusId, uint Sequence), PendingTransfer> pendingTransfers = new();
    private readonly ConcurrentDictionary<string, UsbDkDeviceInfoNative> nativeCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, WindowsDeviceRecovery.Identity> ch340RecoveryIdentities = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim redirectGate = new(1, 1);
    private readonly IUsbIpEventSink eventSink;
    private int redirectOperations;
    private bool disposed;

    public UsbDkDeviceManager(IUsbIpEventSink? eventSink = null)
        => this.eventSink = eventSink ?? NullUsbIpEventSink.Instance;

    /// <summary>
    /// UsbDk 控制平面当前正在处理 Redirect。UsbDk 内核控制队列是串行队列，
    /// AddRedirect 最坏会等待 120 秒；上层在此期间必须避免再次发起原生枚举/描述符 IOCTL。
    /// </summary>
    internal bool IsControlPlaneBusy => Volatile.Read(ref redirectOperations) > 0;

    public async Task<IReadOnlyList<UsbIpDeviceInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        EnsureWindows();
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<UsbDkDeviceInfoNative> nativeSnapshots;
        if (Volatile.Read(ref redirectOperations) > 0 && !nativeCache.IsEmpty)
        {
            // UsbDk 的控制设备是串行队列。AddRedirect 最坏会在内核等待 120 秒，
            // 此时再调用 UsbDk_GetDevicesList 会失败/被阻塞。Redirect 期间直接使用最后一次成功快照，
            // 让 client list/status 始终可用，不让单个 CH340 重连拖死整个服务器设备列表。
            nativeSnapshots = GetPresentCachedNativeDevices();
        }
        else
        {
            try
            {
                nativeSnapshots = EnumerateNativeDevices();
                ReplaceNativeCache(nativeSnapshots);
            }
            catch (Win32Exception ex) when (!nativeCache.IsEmpty)
            {
                nativeSnapshots = GetPresentCachedNativeDevices();
                await eventSink.WriteAsync(new(DateTimeOffset.Now, "usbdk.list.cache-fallback", "Warning", null,
                    null, null, $"UsbDk_GetDevicesList 失败，已使用最近成功设备快照继续响应 DEVLIST: {ex.Message}",
                    new Dictionary<string, object?>
                    {
                        ["nativeErrorCode"] = ex.NativeErrorCode,
                        ["cachedDeviceCount"] = nativeSnapshots.Count,
                        ["redirectOperations"] = Volatile.Read(ref redirectOperations),
                    }, ex), CancellationToken.None);
            }
        }

        var devices = new Dictionary<string, UsbIpDeviceInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in nativeSnapshots)
        {
            var info = ToDeviceInfo(snapshot);
            devices[info.BusId] = info;
        }

        // StartRedirect 后设备可能不再出现在 UsbDk_GetDevicesList 中，因此必须保留 Redirect 快照。
        // 物理拔出后的非活动 Redirect 才允许通过 Windows PRESENT DevNode 清理。
        var needPresenceCheck = redirected.Keys.Any(x => !activeSessionBusIds.ContainsKey(x));
        var presentUsbInstanceIds = needPresenceCheck
            ? WindowsDevicePresence.EnumeratePresentUsbInstanceIds()
            : Array.Empty<string>();

        foreach (var pair in redirected.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!activeSessionBusIds.ContainsKey(pair.Key) &&
                !WindowsDevicePresence.IsPresent(
                    pair.Value.Native.Id.DeviceId,
                    pair.Value.Native.Id.InstanceId,
                    presentUsbInstanceIds))
            {
                if (redirected.TryRemove(pair.Key, out var removed))
                {
                    foreach (var pending in pendingTransfers
                                 .Where(x => string.Equals(x.Key.BusId, pair.Key, StringComparison.OrdinalIgnoreCase))
                                 .Select(x => x.Value)
                                 .ToArray())
                    {
                        TryCancelPendingTransfer(removed, pending);
                    }

                    try { removed.Dispose(); }
                    catch { }
                }

                devices.Remove(pair.Key);
                continue;
            }

            var info = ToDeviceInfo(pair.Value.Native) with { State = UsbIpDeviceState.Shared };
            devices[pair.Key] = info;
        }

        return devices.Values.ToArray();
    }

    public async Task ShareAsync(string busId, CancellationToken cancellationToken = default)
    {
        EnsureWindows();
        cancellationToken.ThrowIfCancellationRequested();
        if (redirected.ContainsKey(busId)) return;

        // UsbDk 控制设备本身是串行队列，同一时刻只允许一个 Redirect 建立过程。
        // 多个设备同时 StartRedirect 只会互相放大 PnP 抖动，并使 GetDevicesList 更容易失败。
        await redirectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref redirectOperations);
        try
        {
            if (redirected.ContainsKey(busId)) return;

            var native = FindNativeDeviceWithCache(busId)
                         ?? throw new InvalidOperationException($"UsbDk 设备 {busId} 不存在。 ");

            var isCh340 = IsCh340(native);
            WindowsDeviceRecovery.Identity? recoveryIdentity = null;
            if (isCh340)
            {
                recoveryIdentity = WindowsDeviceRecovery.ResolveIdentity(
                    native.Id.DeviceId,
                    native.Id.InstanceId);

                if (recoveryIdentity is not null)
                    ch340RecoveryIdentities[busId] = recoveryIdentity;
                else
                    ch340RecoveryIdentities.TryGetValue(busId, out recoveryIdentity);
            }

            await eventSink.WriteAsync(new(DateTimeOffset.Now, "usbdk.redirect.start", "Information", null,
                busId, null, $"开始 UsbDk Redirect {ToVidPid(native)}",
                new Dictionary<string, object?>
                {
                    ["deviceId"] = native.Id.DeviceId,
                    ["instanceId"] = native.Id.InstanceId,
                    ["fullInstanceId"] = recoveryIdentity?.FullInstanceId,
                    ["isCh340"] = isCh340,
                }), CancellationToken.None);

            var id = native.Id;
            // GetLastWin32Error 是线程局部状态，必须在执行 P/Invoke 的同一个线程立即读取。
            var redirectResult = await Task.Run(() =>
            {
                var redirectId = id;
                var resultHandle = UsbDkNative.UsbDk_StartRedirect(ref redirectId);
                var error = resultHandle == 0 || resultHandle == UsbDkNative.InvalidHandleValue
                    ? Marshal.GetLastWin32Error()
                    : 0;
                return (Handle: resultHandle, Error: error);
            }).ConfigureAwait(false);

            var handle = redirectResult.Handle;
            if (handle == 0 || handle == UsbDkNative.InvalidHandleValue)
            {
                var nativeError = redirectResult.Error;
                string? recoveryDetail = null;

                if (isCh340 && recoveryIdentity is not null)
                    recoveryDetail = await RecoverCh340AfterRedirectFailureAsync(
                        busId, native, recoveryIdentity, CancellationToken.None).ConfigureAwait(false);

                var exception = new Win32Exception(
                    nativeError,
                    $"UsbDk_StartRedirect({busId}) 失败" +
                    (string.IsNullOrWhiteSpace(recoveryDetail) ? string.Empty : $"；Recovery={recoveryDetail}"));

                await eventSink.WriteAsync(new(DateTimeOffset.Now, "usbdk.redirect.failed", "Error", null,
                    busId, null, exception.Message,
                    new Dictionary<string, object?>
                    {
                        ["nativeErrorCode"] = nativeError,
                        ["deviceId"] = native.Id.DeviceId,
                        ["instanceId"] = native.Id.InstanceId,
                        ["fullInstanceId"] = recoveryIdentity?.FullInstanceId,
                        ["recovery"] = recoveryDetail,
                    }, exception), CancellationToken.None);
                throw exception;
            }

            var device = new RedirectedDevice(native, handle, ReadConfigurationDescriptors(native));
            if (!redirected.TryAdd(busId, device))
            {
                UsbDkNative.UsbDk_StopRedirect(handle);
                return;
            }

            await eventSink.WriteAsync(new(DateTimeOffset.Now, "usbdk.redirect.ready", "Information", null,
                busId, null, $"UsbDk Redirect 已建立 {ToVidPid(native)}",
                new Dictionary<string, object?>
                {
                    ["deviceId"] = native.Id.DeviceId,
                    ["instanceId"] = native.Id.InstanceId,
                    ["fullInstanceId"] = recoveryIdentity?.FullInstanceId,
                }), CancellationToken.None);
        }
        finally
        {
            Interlocked.Decrement(ref redirectOperations);
            redirectGate.Release();
        }
    }

    /// <summary>标记 Redirect 当前被真实 USB/IP 会话占用，防止 DEVLIST 查询误清理活动设备。</summary>
    internal void MarkSessionActive(string busId) => activeSessionBusIds[busId] = 0;

    /// <summary>USB/IP 会话结束后解除保护，后续 DEVLIST 可清理已经物理拔出的 Redirect。</summary>
    internal void MarkSessionInactive(string busId) => activeSessionBusIds.TryRemove(busId, out _);

    /// <summary>
    /// 重置已重定向设备但保持 Redirect 句柄。
    /// 用于 USB/IP detach 后清理设备/端点状态，同时避免部分 USB 串口设备 StopRedirect 后无法二次 StartRedirect。
    /// </summary>
    public Task ResetAsync(string busId, CancellationToken cancellationToken = default)
    {
        EnsureWindows();
        cancellationToken.ThrowIfCancellationRequested();
        if (!redirected.TryGetValue(busId, out var device)) return Task.CompletedTask;

        foreach (var pending in pendingTransfers
                     .Where(x => string.Equals(x.Key.BusId, busId, StringComparison.OrdinalIgnoreCase))
                     .Select(x => x.Value)
                     .ToArray())
        {
            TryCancelPendingTransfer(device, pending);
        }

        if (!UsbDkNative.UsbDk_ResetDevice(device.Handle))
            UsbDkNative.ThrowLastWin32($"UsbDk_ResetDevice({busId}) 失败");

        return Task.CompletedTask;
    }

    public async Task UnshareAsync(string busId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!redirected.ContainsKey(busId))
            return;

        // StopRedirect、PnP 重启以及恢复后的稳定确认都属于 UsbDk 控制平面操作。
        // 整个窗口与 StartRedirect 使用同一把门，并设置 Busy 标志，让 DEVLIST/status 只读缓存，
        // 避免释放 CH340 时客户端查询再次向 UsbDk 控制设备插入 IOCTL。
        await redirectGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        Interlocked.Increment(ref redirectOperations);
        try
        {
            if (!redirected.TryGetValue(busId, out var current))
                return;

        // PumpUrbAsync 已会先逐个 UNLINK；这里再做一道兜底，确保 StopRedirect 前尽量没有遗留 OVERLAPPED。
        var remaining = await CancelAndDrainPendingTransfersAsync(
            busId, current, TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
        if (remaining > 0)
        {
            await eventSink.WriteAsync(new(DateTimeOffset.Now, "usbdk.release.pending-timeout", "Warning", null,
                busId, null, $"StopRedirect 前仍有 {remaining} 个 URB 未完成，继续关闭 Redirect 句柄以终止会话",
                new Dictionary<string, object?> { ["pendingCount"] = remaining }), CancellationToken.None);
        }

        if (!redirected.TryRemove(busId, out var device))
            return;

        var native = device.Native;
        var isCh340 = IsCh340(native);
        ch340RecoveryIdentities.TryGetValue(busId, out var identity);

        await eventSink.WriteAsync(new(DateTimeOffset.Now, "usbdk.release.start", "Information", null,
            busId, null, $"停止 UsbDk Redirect {ToVidPid(native)}",
            new Dictionary<string, object?>
            {
                ["deviceId"] = native.Id.DeviceId,
                ["instanceId"] = native.Id.InstanceId,
                ["fullInstanceId"] = identity?.FullInstanceId,
                ["isCh340"] = isCh340,
            }), CancellationToken.None);

        device.Dispose();

        if (isCh340)
        {
            // CH340 是本次实机日志中可稳定复现“StopRedirect 后宿主驱动回来，
            // 但下一次 StartRedirect 等满 UsbDk 内核 120 秒并拖死 GetDevicesList”的设备。
            // 因此释放后主动重启一次叶子设备栈，让 CH341SER/UsbDk filter 从干净状态重新绑定。
            identity ??= await ResolveRecoveryIdentityAsync(native, TimeSpan.FromSeconds(5), CancellationToken.None)
                .ConfigureAwait(false);

            if (identity is not null)
            {
                ch340RecoveryIdentities[busId] = identity;
                var recoveryDetail = WindowsDeviceRecovery.TryRestart(identity, out var detail)
                    ? detail
                    : "restart-failed: " + detail;

                var stable = await WaitForNativeDeviceStableAsync(
                    native, identity, TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);

                await eventSink.WriteAsync(new(DateTimeOffset.Now,
                    stable ? "usbdk.ch340.release.recovered" : "usbdk.ch340.release.recovery-failed",
                    stable ? "Information" : "Warning",
                    null, busId, null,
                    stable
                        ? $"CH340 已归还宿主驱动并完成 PnP 重启，下一次 Redirect 可重新建立。{recoveryDetail}"
                        : $"CH340 StopRedirect 后未在 10 秒内恢复为稳定 UsbDk 可枚举状态。{recoveryDetail}",
                    new Dictionary<string, object?>
                    {
                        ["fullInstanceId"] = identity.FullInstanceId,
                        ["parentInstanceId"] = identity.ParentInstanceId,
                        ["stable"] = stable,
                        ["recovery"] = recoveryDetail,
                    }), CancellationToken.None);
            }
            else
            {
                await eventSink.WriteAsync(new(DateTimeOffset.Now, "usbdk.ch340.release.identity-missing", "Warning", null,
                    busId, null, "CH340 StopRedirect 后未能解析完整 PnP InstanceId，无法执行定向设备栈重启",
                    new Dictionary<string, object?>
                    {
                        ["deviceId"] = native.Id.DeviceId,
                        ["instanceId"] = native.Id.InstanceId,
                    }), CancellationToken.None);
            }
        }
        }
        finally
        {
            Interlocked.Decrement(ref redirectOperations);
            redirectGate.Release();
        }
    }

    public Task<UsbIpSubmitCompletion> SubmitAsync(string busId, UsbIpSubmitRequest request, CancellationToken cancellationToken = default)
    {
        EnsureWindows();
        if (!redirected.TryGetValue(busId, out var device))
            throw new InvalidOperationException($"设备 {busId} 尚未通过 UsbDk 重定向。 ");

        return Task.Run(() => SubmitCore(device, busId, request, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// USB/IP UNLINK 只取消指定 Sequence 对应的 OVERLAPPED I/O。
    /// 旧实现使用 UsbDk_AbortPipe + ResetPipe，会把同一端点上其它挂起 URB 一并清掉；
    /// CH340 Bulk-IN 通常同时挂多个读取，这会直接造成串口回包随机丢失。
    /// </summary>
    public Task CancelAsync(string busId, uint sequence, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!redirected.TryGetValue(busId, out var device)) return Task.CompletedTask;
        if (!pendingTransfers.TryGetValue((busId, sequence), out var pending)) return Task.CompletedTask;

        TryCancelPendingTransfer(device, pending);
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

    private UsbIpSubmitCompletion SubmitCore(
        RedirectedDevice device,
        string busId,
        UsbIpSubmitRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var endpoint = (ulong)(request.Endpoint & 0x0F);
        if (request.Direction != 0) endpoint |= 0x80;
        var transferType = ResolveTransferType(device, endpoint);
        if (transferType == UsbDkTransferType.Isochronous)
            throw new NotSupportedException(
                $"当前 MyUsbIP UsbDk 数据面尚未实现 Isochronous packet/result 数组，拒绝提交 EP=0x{endpoint:X2}，避免构造不完整的 UsbDk ISO 请求。 ");

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
        var pending = new PendingTransfer(endpoint, overlappedPtr);

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

            if (!pendingTransfers.TryAdd(pendingKey, pending))
                throw new InvalidDataException($"检测到重复 USB/IP SUBMIT Sequence={request.Sequence}，BusId={busId}。");

            UsbDkTransferResult result;
            lock (pending.SyncRoot)
            {
                if (pending.CancellationRequested)
                    return CreateCancelledCompletion(request);

                pending.IoStarted = true;
                result = request.Direction != 0
                    ? UsbDkNative.UsbDk_ReadPipe(device.Handle, requestPtr, overlappedPtr)
                    : UsbDkNative.UsbDk_WritePipe(device.Handle, requestPtr, overlappedPtr);
            }

            if (result == UsbDkTransferResult.SuccessAsync)
            {
                var systemHandle = UsbDkNative.UsbDk_GetRedirectorSystemHandle(device.Handle);
                if (systemHandle == 0 || systemHandle == UsbDkNative.InvalidHandleValue)
                    throw new IOException("UsbDk_GetRedirectorSystemHandle 返回无效句柄。 ");

                if (!UsbDkNative.GetOverlappedResult(systemHandle, overlappedPtr, out _, true))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == UsbDkNative.ErrorOperationAborted && pending.CancellationRequested)
                        return CreateCancelledCompletion(request);

                    throw new Win32Exception(error, "UsbDk 异步传输完成失败");
                }
            }
            else if (result == UsbDkTransferResult.Failure)
            {
                if (pending.CancellationRequested)
                    return CreateCancelledCompletion(request);

                throw new IOException($"UsbDk 传输失败，BusId={busId}, EP=0x{endpoint:X2}。 ");
            }

            var nativeRequest = Marshal.PtrToStructure<UsbDkTransferRequest>(requestPtr);
            var transferred = checked((int)Math.Min((ulong)int.MaxValue, nativeRequest.Result.Generic.BytesTransferred));
            var status = nativeRequest.Result.Generic.UsbdStatus == 0 ? 0 : -5;
            // USB/IP actual_length 不能大于客户端请求长度。虽然正常 UsbDk 不会越界返回，
            // 但一旦底层异常值大于 dataLength，若头部仍写原始长度而 Payload 被截断，
            // 客户端会继续等待不存在的字节并导致后续 USB/IP 帧整体错位。
            var actualLength = Math.Min(dataLength, Math.Max(0, transferred));

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
            lock (pending.SyncRoot)
            {
                pending.Completed = true;
            }

            pendingTransfers.TryRemove(pendingKey, out _);
            Marshal.FreeHGlobal(requestPtr);
            Marshal.FreeHGlobal(overlappedPtr);
            UsbDkNative.CloseHandle(eventHandle);
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static UsbIpSubmitCompletion CreateCancelledCompletion(UsbIpSubmitRequest request)
        => new(
            request.Sequence,
            request.DeviceId,
            request.Direction,
            request.Endpoint,
            -104, // Linux ECONNRESET：USB/IP unlink 后目标 SUBMIT 的标准取消语义。
            0,
            request.StartFrame,
            request.NumberOfPackets,
            1,
            Array.Empty<byte>());

    private static void TryCancelPendingTransfer(RedirectedDevice device, PendingTransfer pending)
    {
        lock (pending.SyncRoot)
        {
            if (pending.Completed || pending.CancellationRequested) return;
            pending.CancellationRequested = true;
            if (!pending.IoStarted) return;

            var systemHandle = UsbDkNative.UsbDk_GetRedirectorSystemHandle(device.Handle);
            if (systemHandle == 0 || systemHandle == UsbDkNative.InvalidHandleValue) return;

            if (UsbDkNative.CancelIoEx(systemHandle, pending.OverlappedPtr)) return;

            // ERROR_NOT_FOUND 表示该 OVERLAPPED 已经完成，不需要再取消。
            var error = Marshal.GetLastWin32Error();
            if (error != UsbDkNative.ErrorNotFound)
            {
                // UNLINK 是恢复性路径。取消失败不能退化成 AbortPipe，否则又会误伤同端点其它 URB。
                // 让原 I/O 自然完成，由 USB/IP 会话后续状态决定是否继续。
            }
        }
    }

    private static UsbDkTransferType ResolveTransferType(RedirectedDevice device, ulong endpoint)
    {
        if ((endpoint & 0x0F) == 0) return UsbDkTransferType.Control;
        if (device.EndpointTypes.TryGetValue((byte)endpoint, out var type)) return type;
        return UsbDkTransferType.Bulk;
    }

    private async Task<int> CancelAndDrainPendingTransfersAsync(
        string busId,
        RedirectedDevice device,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        foreach (var pending in pendingTransfers
                     .Where(x => string.Equals(x.Key.BusId, busId, StringComparison.OrdinalIgnoreCase))
                     .Select(x => x.Value)
                     .ToArray())
        {
            TryCancelPendingTransfer(device, pending);
        }

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = pendingTransfers.Keys.Count(x =>
                string.Equals(x.BusId, busId, StringComparison.OrdinalIgnoreCase));
            if (count == 0) return 0;
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        return pendingTransfers.Keys.Count(x =>
            string.Equals(x.BusId, busId, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<WindowsDeviceRecovery.Identity?> ResolveRecoveryIdentityAsync(
        UsbDkDeviceInfoNative native,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identity = WindowsDeviceRecovery.ResolveIdentity(native.Id.DeviceId, native.Id.InstanceId);
            if (identity is not null) return identity;

            if (DateTime.UtcNow >= deadline) break;
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        } while (true);

        return null;
    }

    private async Task<bool> WaitForNativeDeviceStableAsync(
        UsbDkDeviceInfoNative expected,
        WindowsDeviceRecovery.Identity identity,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        const int requiredStableHits = 3;
        var stableHits = 0;
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);

            IReadOnlyList<UsbDkDeviceInfoNative> current;
            try
            {
                current = EnumerateNativeDevices();
                ReplaceNativeCache(current);
            }
            catch (Win32Exception)
            {
                stableHits = 0;
                continue;
            }

            var present = WindowsDevicePresence.EnumeratePresentUsbInstanceIds();
            var matched = current.FirstOrDefault(x =>
                x.DeviceDescriptor.VendorId == expected.DeviceDescriptor.VendorId &&
                x.DeviceDescriptor.ProductId == expected.DeviceDescriptor.ProductId &&
                string.Equals(
                    WindowsDevicePresence.FindPresentInstanceId(x.Id.DeviceId, x.Id.InstanceId, present),
                    identity.FullInstanceId,
                    StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(matched.Id.DeviceId))
            {
                stableHits++;
                var currentBusId = GetBusId(matched);
                ch340RecoveryIdentities[currentBusId] = identity;
                if (stableHits >= requiredStableHits) return true;
            }
            else
            {
                stableHits = 0;
            }
        }

        return false;
    }

    private async Task<string> RecoverCh340AfterRedirectFailureAsync(
        string busId,
        UsbDkDeviceInfoNative native,
        WindowsDeviceRecovery.Identity identity,
        CancellationToken cancellationToken)
    {
        var details = new List<string>();

        if (WindowsDeviceRecovery.TryRestart(identity, out var restartDetail))
            details.Add("restart=" + restartDetail);
        else
            details.Add("restart-failed=" + restartDetail);

        var stable = await WaitForNativeDeviceStableAsync(
            native, identity, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);

        if (!stable && WindowsDeviceRecovery.TryReenumerateParent(identity, out var parentDetail))
        {
            details.Add("parent=" + parentDetail);
            stable = await WaitForNativeDeviceStableAsync(
                native, identity, TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
        }

        details.Add("stable=" + stable);
        if (stable)
            ch340RecoveryIdentities[busId] = identity;

        return string.Join("; ", details);
    }

    private UsbDkDeviceInfoNative? FindNativeDeviceWithCache(string busId)
    {
        if (nativeCache.TryGetValue(busId, out var cached))
            return cached;

        try
        {
            var current = EnumerateNativeDevices();
            ReplaceNativeCache(current);
            return current.FirstOrDefault(x =>
                string.Equals(GetBusId(x), busId, StringComparison.OrdinalIgnoreCase)) is var found &&
                   !string.IsNullOrWhiteSpace(found.Id.DeviceId)
                ? found
                : null;
        }
        catch (Win32Exception) when (nativeCache.TryGetValue(busId, out cached))
        {
            return cached;
        }
    }

    private IReadOnlyList<UsbDkDeviceInfoNative> GetPresentCachedNativeDevices()
    {
        var cached = nativeCache.Values.ToArray();
        if (cached.Length == 0) return cached;

        var present = WindowsDevicePresence.EnumeratePresentUsbInstanceIds();
        if (present.Count == 0) return cached;

        return cached.Where(x =>
        {
            var busId = GetBusId(x);
            return activeSessionBusIds.ContainsKey(busId) ||
                   WindowsDevicePresence.IsPresent(x.Id.DeviceId, x.Id.InstanceId, present);
        }).ToArray();
    }

    private void ReplaceNativeCache(IReadOnlyList<UsbDkDeviceInfoNative> snapshots)
    {
        nativeCache.Clear();
        foreach (var snapshot in snapshots)
            nativeCache[GetBusId(snapshot)] = snapshot;
    }

    private static bool IsCh340(UsbDkDeviceInfoNative native)
        => native.DeviceDescriptor.VendorId == 0x1A86 &&
           native.DeviceDescriptor.ProductId == 0x7523;

    private static string ToVidPid(UsbDkDeviceInfoNative native)
        => $"{native.DeviceDescriptor.VendorId:X4}:{native.DeviceDescriptor.ProductId:X4}";

    private UsbIpDeviceInfo ToDeviceInfo(UsbDkDeviceInfoNative native)
    {
        var busId = GetBusId(native);
        return new UsbIpDeviceInfo
        {
            BusId = busId,
            InstanceId = native.Id.InstanceId,
            Path = native.Id.DeviceId,
            BusNumber = unchecked((uint)native.FilterId),
            DeviceNumber = unchecked((uint)native.Port),
            Speed = MapUsbDkSpeed(native.Speed),
            UsbVersion = native.DeviceDescriptor.BcdUsb,
            DeviceVersion = native.DeviceDescriptor.BcdDevice,
            DeviceClass = native.DeviceDescriptor.DeviceClass,
            DeviceSubClass = native.DeviceDescriptor.DeviceSubClass,
            DeviceProtocol = native.DeviceDescriptor.DeviceProtocol,
            ConfigurationCount = native.DeviceDescriptor.NumberConfigurations,
            VendorId = native.DeviceDescriptor.VendorId,
            ProductId = native.DeviceDescriptor.ProductId,
            Product = native.Id.DeviceId,
            State = redirected.ContainsKey(busId) || activeSessionBusIds.ContainsKey(busId)
                ? UsbIpDeviceState.Shared
                : UsbIpDeviceState.Available,
        };
    }

    private static string GetBusId(UsbDkDeviceInfoNative native)
        => $"{unchecked((uint)native.FilterId):X8}-{unchecked((uint)native.Port):X8}";

    private static uint MapUsbDkSpeed(ulong speed) => speed switch
    {
        1 => 1, // Low
        2 => 2, // Full
        3 => 3, // High
        4 => 5, // Super；USB/IP 4 保留给 Wireless
        5 => 6, // SuperPlus
        _ => 0,
    };

    private UsbDkDeviceInfoNative? FindNativeDevice(string busId)
        => FindNativeDeviceWithCache(busId);

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
        activeSessionBusIds.Clear();
        pendingTransfers.Clear();
        nativeCache.Clear();
        ch340RecoveryIdentities.Clear();
        redirectGate.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class PendingTransfer(ulong endpoint, nint overlappedPtr)
    {
        public object SyncRoot { get; } = new();
        public ulong Endpoint { get; } = endpoint;
        public nint OverlappedPtr { get; } = overlappedPtr;
        public bool IoStarted { get; set; }
        public bool CancellationRequested { get; set; }
        public bool Completed { get; set; }
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
