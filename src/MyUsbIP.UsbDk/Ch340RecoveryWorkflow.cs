namespace MyUsbIP.UsbDk;

/// <summary>恢复请求被接受不代表硬件就绪；每个步骤后都必须用当前设备状态确认。</summary>
internal static class Ch340RecoveryWorkflow
{
    internal sealed record Result(bool Ready, string Detail)
    {
        public void EnsureReady(string busId)
        {
            if (!Ready)
                throw new InvalidOperationException($"CH340 {busId} 尚未恢复为可捕获状态，拒绝继续 Redirect。{Detail}");
        }
    }

    internal static async Task<Result> RunAsync(
        Func<string> restart,
        Func<CancellationToken, Task<bool>> waitReady,
        Func<(bool Success, string Detail)> cyclePort,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var detail = "restart=" + restart();
        cancellationToken.ThrowIfCancellationRequested();
        var initiallyReady = await waitReady(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (initiallyReady) return new(true, detail + "; stable=True");
        var cycle = cyclePort();
        cancellationToken.ThrowIfCancellationRequested();
        detail += "; port-cycle=" + cycle.Detail;
        if (!cycle.Success) return new(false, detail + "; stable=False");
        var ready = await waitReady(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new(ready, detail + "; stable=" + ready);
    }
}
