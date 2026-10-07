using System.Buffers.Binary;
using System.ComponentModel;
using System.Text;
using MyUsbIP.UsbDk;

internal static class Ch340RecoverySmokeChecks
{
    private const string DeviceId = @"USB\VID_1A86&PID_7523";
    private const string InstanceA = @"USB\VID_1A86&PID_7523\6&AAAA&0&4";
    private const string InstanceB = @"USB\VID_1A86&PID_7523\7&BBBB&0&4";

    public static async Task RunAsync(List<string> failures)
    {
        var initialFailureCount = failures.Count;
        await RunCaseAsync(nameof(Recovery_HealthyAfterRestart_DoesNotCycle), Recovery_HealthyAfterRestart_DoesNotCycle);
        await RunCaseAsync(nameof(Recovery_UnhealthyAfterRestart_CyclesThenChecksAgain), Recovery_UnhealthyAfterRestart_CyclesThenChecksAgain);
        await RunCaseAsync(nameof(Recovery_CycleFailure_RemainsFailedWithoutSecondWait), Recovery_CycleFailure_RemainsFailedWithoutSecondWait);
        await RunCaseAsync(nameof(Recovery_UnhealthyAfterCycle_RejectsCapture), Recovery_UnhealthyAfterCycle_RejectsCapture);
        await RunCaseAsync(nameof(Recovery_CancelledBeforeStart_PerformsNoAction), Recovery_CancelledBeforeStart_PerformsNoAction);
        await RunCaseAsync(nameof(Recovery_CancelledByRestart_DoesNotWaitOrCycle), Recovery_CancelledByRestart_DoesNotWaitOrCycle);
        await RunCaseAsync(nameof(Recovery_CancelledByFirstWait_DoesNotCycle), Recovery_CancelledByFirstWait_DoesNotCycle);
        await RunCaseAsync(nameof(Recovery_CancelledByHealthyFirstWait_DoesNotReportReady), Recovery_CancelledByHealthyFirstWait_DoesNotReportReady);
        await RunCaseAsync(nameof(Recovery_CancelledByCycle_DoesNotWaitAgain), Recovery_CancelledByCycle_DoesNotWaitAgain);
        await RunCaseAsync(nameof(Recovery_CancelledByFinalWait_DoesNotReportReady), Recovery_CancelledByFinalWait_DoesNotReportReady);
        await RunCaseAsync(nameof(Recovery_RestartException_DoesNotReportSuccessOrContinue), Recovery_RestartException_DoesNotReportSuccessOrContinue);
        await RunCaseAsync(nameof(Recovery_CycleException_DoesNotReportSuccessOrContinue), Recovery_CycleException_DoesNotReportSuccessOrContinue);
        await RunCaseAsync(nameof(Recovery_ReadinessException_DoesNotCycle), Recovery_ReadinessException_DoesNotCycle);
        RunCase(nameof(Readiness_StartedWithoutProblem_IsReady), Readiness_StartedWithoutProblem_IsReady);
        RunCase(nameof(Readiness_NotStarted_IsNotReady), Readiness_NotStarted_IsNotReady);
        RunCase(nameof(Readiness_ProblemFlag_IsNotReady), Readiness_ProblemFlag_IsNotReady);
        RunCase(nameof(Readiness_ProblemNumberWithoutFlag_IsIgnored), Readiness_ProblemNumberWithoutFlag_IsIgnored);
        RunCase(nameof(Readiness_StatusQueryFailure_IsNotReady), Readiness_StatusQueryFailure_IsNotReady);
        RunCase(nameof(Identity_FullExactMatch_SelectsOnlyTarget), Identity_FullExactMatch_SelectsOnlyTarget);
        RunCase(nameof(Identity_UniqueTailAndPortMatch_SelectsTarget), Identity_UniqueTailAndPortMatch_SelectsTarget);
        RunCase(nameof(Identity_AmbiguousSamePort_DoesNotChooseDevice), Identity_AmbiguousSamePort_DoesNotChooseDevice);
        RunCase(nameof(Identity_MissingDevice_IsAbsent), Identity_MissingDevice_IsAbsent);
        RunCase(nameof(Identity_WithoutInstance_RequiresUniqueCandidate), Identity_WithoutInstance_RequiresUniqueCandidate);
        RunCase(nameof(Identity_DeviceIdOnlyCandidate_DoesNotThrowOrMatchUnrelatedTail), Identity_DeviceIdOnlyCandidate_DoesNotThrowOrMatchUnrelatedTail);
        RunCase(nameof(DriverKey_ValidAbiBuffer_ReadsUtf16AtOffsetEight), DriverKey_ValidAbiBuffer_ReadsUtf16AtOffsetEight);
        RunCase(nameof(DriverKey_TruncatedHeader_IsRejected), DriverKey_TruncatedHeader_IsRejected);
        RunCase(nameof(DriverKey_InvalidDeclaredLength_IsRejected), DriverKey_InvalidDeclaredLength_IsRejected);
        RunCase(nameof(DriverKey_MissingTerminator_IsRejected), DriverKey_MissingTerminator_IsRejected);
        RunCase(nameof(DriverKey_EmptyOrBlankKey_IsRejected), DriverKey_EmptyOrBlankKey_IsRejected);
        RunCase(nameof(HubPort_ValidVerifiedTopology_CanCycle), HubPort_ValidVerifiedTopology_CanCycle);
        RunCase(nameof(HubPort_MissingIdentity_CannotCycle), HubPort_MissingIdentity_CannotCycle);
        RunCase(nameof(HubPort_PortBounds_AreEnforced), HubPort_PortBounds_AreEnforced);
        RunCase(nameof(HubPort_CurrentDriverKeyMustMatchSavedDevice), HubPort_CurrentDriverKeyMustMatchSavedDevice);
        RunCase(nameof(HubPort_MissingKeyRequiresConfirmedEmptyPort), HubPort_MissingKeyRequiresConfirmedEmptyPort);
        if (failures.Count == initialFailureCount)
            Console.WriteLine("CH340 recovery checks: PASS (34)");

        async Task RunCaseAsync(string name, Func<Task> action)
        {
            try { await action(); Console.WriteLine($"[PASS] {name}"); }
            catch (Exception ex) { failures.Add($"[FAIL] {name}: {ex.Message}"); }
        }

        void RunCase(string name, Action action)
        {
            try { action(); Console.WriteLine($"[PASS] {name}"); }
            catch (Exception ex) { failures.Add($"[FAIL] {name}: {ex.Message}"); }
        }
    }

    private static async Task Recovery_HealthyAfterRestart_DoesNotCycle()
    {
        var calls = new List<string>();
        var result = await Ch340RecoveryWorkflow.RunAsync(
            () => { calls.Add("restart"); return "accepted"; },
            _ => { calls.Add("wait"); return Task.FromResult(true); },
            () => { calls.Add("cycle"); return (true, "accepted"); }, CancellationToken.None);
        Check(result.Ready, "已就绪设备被误判恢复失败");
        CheckCalls(calls, "restart", "wait");
        Check(result.Detail.Contains("accepted", StringComparison.Ordinal), "恢复诊断丢失重启结果");
        result.EnsureReady("47-4");
    }

    private static async Task Recovery_UnhealthyAfterRestart_CyclesThenChecksAgain()
    {
        var calls = new List<string>();
        var ready = new Queue<bool>([false, true]);
        var result = await Ch340RecoveryWorkflow.RunAsync(
            () => { calls.Add("restart"); return "not found"; },
            _ => { calls.Add("wait"); return Task.FromResult(ready.Dequeue()); },
            () => { calls.Add("cycle"); return (true, "target-port-4"); }, CancellationToken.None);
        Check(result.Ready, "端口恢复后就绪仍被误判失败");
        CheckCalls(calls, "restart", "wait", "cycle", "wait");
        Check(result.Detail.Contains("not found", StringComparison.Ordinal) && result.Detail.Contains("target-port-4", StringComparison.Ordinal),
            "恢复诊断必须保留重启失败及端口恢复过程");
        result.EnsureReady("47-4");
    }

    private static async Task Recovery_CycleFailure_RemainsFailedWithoutSecondWait()
    {
        var calls = new List<string>();
        var result = await Ch340RecoveryWorkflow.RunAsync(
            () => { calls.Add("restart"); return "accepted"; },
            _ => { calls.Add("wait"); return Task.FromResult(false); },
            () => { calls.Add("cycle"); return (false, "driver-key mismatch"); }, CancellationToken.None);
        Check(!result.Ready, "端口恢复失败被误报成功");
        CheckCalls(calls, "restart", "wait", "cycle");
        var error = Throws<InvalidOperationException>(() => result.EnsureReady("47-4"));
        Check(error.Message.Contains("47-4", StringComparison.Ordinal) && error.Message.Contains("driver-key mismatch", StringComparison.Ordinal),
            "拒绝捕获的错误没有保留设备及失败原因");
    }

    private static async Task Recovery_UnhealthyAfterCycle_RejectsCapture()
    {
        var calls = new List<string>();
        var result = await Ch340RecoveryWorkflow.RunAsync(
            () => { calls.Add("restart"); return "accepted"; },
            _ => { calls.Add("wait"); return Task.FromResult(false); },
            () => { calls.Add("cycle"); return (true, "accepted"); }, CancellationToken.None);
        Check(!result.Ready, "原生恢复请求被接受不等于设备已启动");
        CheckCalls(calls, "restart", "wait", "cycle", "wait");
        Throws<InvalidOperationException>(() => result.EnsureReady("47-4"));
    }

    private static async Task Recovery_CancelledBeforeStart_PerformsNoAction()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var calls = new List<string>();
        await ThrowsAsync<OperationCanceledException>(() => Ch340RecoveryWorkflow.RunAsync(
            () => { calls.Add("restart"); return "accepted"; },
            _ => { calls.Add("wait"); return Task.FromResult(false); },
            () => { calls.Add("cycle"); return (true, "accepted"); }, cancel.Token));
        CheckCalls(calls);
    }

    private static async Task Recovery_CancelledByRestart_DoesNotWaitOrCycle()
    {
        using var cancel = new CancellationTokenSource();
        var calls = new List<string>();
        await ThrowsAsync<OperationCanceledException>(() => Ch340RecoveryWorkflow.RunAsync(
            () => { calls.Add("restart"); cancel.Cancel(); return "accepted"; },
            _ => { calls.Add("wait"); return Task.FromResult(false); },
            () => { calls.Add("cycle"); return (true, "accepted"); }, cancel.Token));
        CheckCalls(calls, "restart");
    }

    private static async Task Recovery_CancelledByFirstWait_DoesNotCycle()
    {
        using var cancel = new CancellationTokenSource();
        var calls = new List<string>();
        await ThrowsAsync<OperationCanceledException>(() => Ch340RecoveryWorkflow.RunAsync(
            () => { calls.Add("restart"); return "accepted"; },
            _ => { calls.Add("wait"); cancel.Cancel(); return Task.FromResult(false); },
            () => { calls.Add("cycle"); return (true, "accepted"); }, cancel.Token));
        CheckCalls(calls, "restart", "wait");
    }

    private static async Task Recovery_CancelledByCycle_DoesNotWaitAgain()
    {
        using var cancel = new CancellationTokenSource();
        var calls = new List<string>();
        await ThrowsAsync<OperationCanceledException>(() => Ch340RecoveryWorkflow.RunAsync(
            () => { calls.Add("restart"); return "accepted"; },
            _ => { calls.Add("wait"); return Task.FromResult(false); },
            () => { calls.Add("cycle"); cancel.Cancel(); return (true, "accepted"); }, cancel.Token));
        CheckCalls(calls, "restart", "wait", "cycle");
    }

    private static async Task Recovery_CancelledByHealthyFirstWait_DoesNotReportReady()
    {
        using var cancel = new CancellationTokenSource();
        var calls = new List<string>();
        await ThrowsAsync<OperationCanceledException>(() => Ch340RecoveryWorkflow.RunAsync(
            () => { calls.Add("restart"); return "accepted"; },
            _ => { calls.Add("wait"); cancel.Cancel(); return Task.FromResult(true); },
            () => { calls.Add("cycle"); return (true, "accepted"); }, cancel.Token));
        CheckCalls(calls, "restart", "wait");
    }

    private static async Task Recovery_CancelledByFinalWait_DoesNotReportReady()
    {
        using var cancel = new CancellationTokenSource();
        var calls = new List<string>();
        var waitCount = 0;
        await ThrowsAsync<OperationCanceledException>(() => Ch340RecoveryWorkflow.RunAsync(
            () => { calls.Add("restart"); return "accepted"; },
            _ =>
            {
                calls.Add("wait");
                if (++waitCount == 1) return Task.FromResult(false);
                cancel.Cancel();
                return Task.FromResult(true);
            },
            () => { calls.Add("cycle"); return (true, "accepted"); }, cancel.Token));
        CheckCalls(calls, "restart", "wait", "cycle", "wait");
    }

    private static async Task Recovery_RestartException_DoesNotReportSuccessOrContinue()
    {
        var calls = new List<string>();
        var expected = new Win32Exception(1167);
        var actual = await ThrowsAsync<Win32Exception>(() => Ch340RecoveryWorkflow.RunAsync(
            () => { calls.Add("restart"); throw expected; },
            _ => { calls.Add("wait"); return Task.FromResult(true); },
            () => { calls.Add("cycle"); return (true, "accepted"); }, CancellationToken.None));
        Check(ReferenceEquals(expected, actual), "原生重启异常丢失或被替换");
        CheckCalls(calls, "restart");
    }

    private static async Task Recovery_CycleException_DoesNotReportSuccessOrContinue()
    {
        var calls = new List<string>();
        var expected = new Win32Exception(1167);
        var actual = await ThrowsAsync<Win32Exception>(() => Ch340RecoveryWorkflow.RunAsync(
            () => { calls.Add("restart"); return "accepted"; },
            _ => { calls.Add("wait"); return Task.FromResult(false); },
            () => { calls.Add("cycle"); throw expected; }, CancellationToken.None));
        Check(ReferenceEquals(expected, actual), "原生端口恢复异常丢失或被替换");
        CheckCalls(calls, "restart", "wait", "cycle");
    }

    private static async Task Recovery_ReadinessException_DoesNotCycle()
    {
        var calls = new List<string>();
        var expected = new Win32Exception(1167);
        var actual = await ThrowsAsync<Win32Exception>(() => Ch340RecoveryWorkflow.RunAsync(
            () => { calls.Add("restart"); return "accepted"; },
            _ => { calls.Add("wait"); return Task.FromException<bool>(expected); },
            () => { calls.Add("cycle"); return (true, "accepted"); }, CancellationToken.None));
        Check(ReferenceEquals(expected, actual), "状态检测异常丢失或被替换");
        CheckCalls(calls, "restart", "wait");
    }

    private static void Readiness_StartedWithoutProblem_IsReady()
        => Check(WindowsDeviceRecovery.IsReadyStatus(0, 0x8 | 0x2, 0), "已启动且无问题的 DevNode 应就绪");

    private static void Readiness_NotStarted_IsNotReady()
        => Check(!WindowsDeviceRecovery.IsReadyStatus(0, 0x2, 0), "仅存在但未启动的 DevNode 不可捕获");

    private static void Readiness_ProblemFlag_IsNotReady()
        => Check(!WindowsDeviceRecovery.IsReadyStatus(0, 0x8 | 0x400, 0), "DN_HAS_PROBLEM 标志未阻止捕获");

    private static void Readiness_ProblemNumberWithoutFlag_IsIgnored()
        => Check(WindowsDeviceRecovery.IsReadyStatus(0, 0x8, 22), "没有 DN_HAS_PROBLEM 时未定义的问题码不应阻止就绪判断");

    private static void Readiness_StatusQueryFailure_IsNotReady()
        => Check(!WindowsDeviceRecovery.IsReadyStatus(0xD, 0x8, 0), "状态查询失败不能使用返回缓存值判为就绪");

    private static void Identity_FullExactMatch_SelectsOnlyTarget()
    {
        string[] present = [InstanceB, InstanceA];
        Check(WindowsDevicePresence.FindPresentInstanceId(DeviceId.ToLowerInvariant(), InstanceA.ToLowerInvariant(), present) == InstanceA,
            "完整实例 ID 应精确定位目标，不受另一个相同端口设备影响");
        Check(WindowsDevicePresence.IsPresent(DeviceId, InstanceA, present), "精确实例匹配没有返回存在");
    }

    private static void Identity_UniqueTailAndPortMatch_SelectsTarget()
    {
        string[] present = [@"USB\VID_1A86&PID_7523\7&BBBB&0&5", InstanceA, @"USB\VID_FFFF&PID_0001\6&CCCC&0&4"];
        Check(WindowsDevicePresence.FindPresentInstanceId(DeviceId, "6&AAAA&0&4", present) == InstanceA, "完整尾段匹配失败");
        Check(WindowsDevicePresence.FindPresentInstanceId(DeviceId, "4", present) == InstanceA, "唯一同厂商/产品端口匹配失败");
    }

    private static void Identity_AmbiguousSamePort_DoesNotChooseDevice()
    {
        string[] present = [InstanceA, InstanceB];
        Check(WindowsDevicePresence.FindPresentInstanceId(DeviceId, "4", present) is null, "相同端口号多个 CH340 不能任选第一个");
        Check(WindowsDevicePresence.FindPresentInstanceId(DeviceId, "4", present.Reverse().ToArray()) is null, "改变枚举顺序不能解除身份歧义");
        Check(WindowsDevicePresence.IsPresent(DeviceId, "4", present), "身份歧义不能误报物理拔出");
    }

    private static void Identity_MissingDevice_IsAbsent()
    {
        Check(WindowsDevicePresence.FindPresentInstanceId(DeviceId, "4", Array.Empty<string>()) is null, "空快照不应返回设备");
        Check(!WindowsDevicePresence.IsPresent(DeviceId, "4", [@"USB\VID_FFFF&PID_0001\6&CCCC&0&4"]), "其他型号的同端口不应匹配");
        Check(!WindowsDevicePresence.IsPresent(DeviceId, "5", [InstanceA]), "目标端口缺失不应匹配相邻设备");
        Check(WindowsDevicePresence.FindPresentInstanceId(" ", "4", [InstanceA]) is null, "空 DeviceId 不应匹配");
    }

    private static void Identity_WithoutInstance_RequiresUniqueCandidate()
    {
        Check(WindowsDevicePresence.FindPresentInstanceId(DeviceId, null, [InstanceA]) == InstanceA, "单一候选无法解析身份");
        Check(WindowsDevicePresence.FindPresentInstanceId(DeviceId, null, [InstanceA, InstanceB]) is null, "缺少实例且多个候选不能任选一个");
        Check(WindowsDevicePresence.IsPresent(DeviceId, null, [InstanceA, InstanceB]), "候选仍存在时不能误报拔出");
    }

    private static void Identity_DeviceIdOnlyCandidate_DoesNotThrowOrMatchUnrelatedTail()
    {
        Check(WindowsDevicePresence.FindPresentInstanceId(DeviceId, "4", [DeviceId]) is null, "没有实例尾段的候选不能匹配端口号");
        Check(!WindowsDevicePresence.IsPresent(DeviceId, "4", [DeviceId]), "没有实例尾段的候选不能确认目标存在");
        Check(WindowsDevicePresence.FindPresentInstanceId(DeviceId, null, [DeviceId]) == DeviceId, "没有要求实例时仍应接受单一精确候选");
    }

    private static void DriverKey_ValidAbiBuffer_ReadsUtf16AtOffsetEight()
    {
        const string key = @"{36fc9e60-c465-11cf-8056-444553540000}\0004";
        var buffer = DriverKeyBuffer(key, port: 4);
        Check(BinaryPrimitives.ReadUInt32LittleEndian(buffer) == 4 && BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(4)) == buffer.Length,
            "测试夹具应保留两个 DWORD 的 Windows ABI 前缀");
        Check(WindowsUsbHubPortRecovery.ParseDriverKey(buffer) == key, "UTF-16 DriverKey 没有从偏移 8 读取");
        var trailingBuffer = new byte[buffer.Length + 8];
        buffer.CopyTo(trailingBuffer, 0);
        Array.Fill(trailingBuffer, (byte)0xFF, buffer.Length, 8);
        Check(WindowsUsbHubPortRecovery.ParseDriverKey(trailingBuffer) == key, "ActualLength 外的缓冲区内容影响了解析");
    }

    private static void DriverKey_TruncatedHeader_IsRejected()
    {
        foreach (var length in new[] { 0, 1, 4, 7, 8, 9 })
            Check(WindowsUsbHubPortRecovery.ParseDriverKey(new byte[length]) is null, $"长度 {length} 的截断头未被拒绝");
    }

    private static void DriverKey_InvalidDeclaredLength_IsRejected()
    {
        foreach (var declared in new uint[] { 0, 7, 8, 9, 11, 1024, uint.MaxValue })
        {
            var buffer = DriverKeyBuffer("target");
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), declared);
            Check(WindowsUsbHubPortRecovery.ParseDriverKey(buffer) is null, $"无效 ActualLength={declared} 未被拒绝");
        }
    }

    private static void DriverKey_MissingTerminator_IsRejected()
    {
        var buffer = DriverKeyBuffer("target");
        buffer[^2] = (byte)'x';
        Check(WindowsUsbHubPortRecovery.ParseDriverKey(buffer) is null, "未终止的 DriverKey 不可用于授权端口恢复");
        buffer = DriverKeyBuffer("target");
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), (uint)buffer.Length - 2);
        Check(WindowsUsbHubPortRecovery.ParseDriverKey(buffer) is null, "ActualLength 外的终止符不能使截断内容有效");
    }

    private static void DriverKey_EmptyOrBlankKey_IsRejected()
    {
        Check(WindowsUsbHubPortRecovery.ParseDriverKey(DriverKeyBuffer("")) is null, "空 DriverKey 不能授权端口恢复");
        Check(WindowsUsbHubPortRecovery.ParseDriverKey(DriverKeyBuffer("  ")) is null, "空白 DriverKey 不能授权端口恢复");
    }

    private static void HubPort_ValidVerifiedTopology_CanCycle()
    {
        Check(WindowsUsbHubPortRecovery.CanCycle(Topology(1)), "已验证的第一个子端口应允许恢复");
        Check(WindowsUsbHubPortRecovery.CanCycle(Topology(255)), "已验证的最后合法子端口应允许恢复");
    }

    private static void HubPort_MissingIdentity_CannotCycle()
    {
        Check(!WindowsUsbHubPortRecovery.CanCycle(null), "未保存拓扑时不能恢复端口");
        foreach (var blank in new[] { "", "  " })
        {
            Check(!WindowsUsbHubPortRecovery.CanCycle(new(blank, "USB\\HUB\\1", 4, "driver-key")), "缺少 Hub 路径不能恢复");
            Check(!WindowsUsbHubPortRecovery.CanCycle(new(@"\\?\hub", blank, 4, "driver-key")), "缺少父 Hub 身份不能恢复");
            Check(!WindowsUsbHubPortRecovery.CanCycle(new(@"\\?\hub", "USB\\HUB\\1", 4, blank)), "缺少目标 DriverKey 不能恢复");
        }
    }

    private static void HubPort_PortBounds_AreEnforced()
    {
        Check(!WindowsUsbHubPortRecovery.CanCycle(Topology(0)), "端口零不能恢复");
        Check(!WindowsUsbHubPortRecovery.CanCycle(Topology(256)), "超出 USB 子端口范围不能恢复");
        Check(!WindowsUsbHubPortRecovery.CanCycle(Topology(uint.MaxValue)), "超大端口不能恢复");
    }

    private static void HubPort_CurrentDriverKeyMustMatchSavedDevice()
    {
        Check(WindowsUsbHubPortRecovery.CanCycleCurrentPort("driver-key-A", "DRIVER-KEY-A", false), "相同 DriverKey 应允许目标端口恢复");
        Check(!WindowsUsbHubPortRecovery.CanCycleCurrentPort("driver-key-A", "driver-key-B", false), "端口被另一设备占用时不能恢复");
        Check(!WindowsUsbHubPortRecovery.CanCycleCurrentPort("driver-key-A", "driver-key-B", true), "已读取不同 DriverKey 时不能用空端口状态覆盖身份不匹配");
        Check(!WindowsUsbHubPortRecovery.CanCycleCurrentPort("", "", true), "未保存 DriverKey 时不能授权端口恢复");
        Check(!WindowsUsbHubPortRecovery.CanCycleCurrentPort("  ", null, true), "空白已保存 DriverKey 不能授权空端口恢复");
    }

    private static void HubPort_MissingKeyRequiresConfirmedEmptyPort()
    {
        Check(WindowsUsbHubPortRecovery.CanCycleCurrentPort("driver-key-A", null, true), "已确认目标端口为空时应允许恢复消失的设备");
        Check(!WindowsUsbHubPortRecovery.CanCycleCurrentPort("driver-key-A", null, false), "DriverKey 查询失败且没有确认空端口时不能恢复");
    }

    private static WindowsUsbHubPortRecovery.Topology Topology(uint port)
        => new(@"\\?\usb#root_hub30#verified", @"USB\ROOT_HUB30\parent", port, "driver-key");

    private static byte[] DriverKeyBuffer(string key, uint port = 4)
    {
        var encoded = Encoding.Unicode.GetBytes(key + '\0');
        var buffer = new byte[8 + encoded.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, port);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), (uint)buffer.Length);
        encoded.CopyTo(buffer, 8);
        return buffer;
    }

    private static T Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T exception) { return exception; }
        throw new InvalidOperationException($"应抛出 {typeof(T).Name}");
    }

    private static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T exception) { return exception; }
        throw new InvalidOperationException($"应抛出 {typeof(T).Name}");
    }

    private static void CheckCalls(List<string> actual, params string[] expected)
        => Check(actual.SequenceEqual(expected), $"恢复调用顺序不符：实际 [{string.Join(",", actual)}]，预期 [{string.Join(",", expected)}]");

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
