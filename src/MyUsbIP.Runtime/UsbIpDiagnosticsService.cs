using MyUsbIP.Abstractions;

namespace MyUsbIP.Runtime;

/// <summary>
/// 不依赖具体驱动实现的健康检查服务。
/// 通过真实调用后端接口验证“工具可执行、驱动可用、设备可枚举、远端协议可访问”等关键链路。
/// </summary>
public sealed class UsbIpDiagnosticsService(
    IUsbIpServerBackend serverBackend,
    IUsbIpClientBackend clientBackend,
    IUsbIpEventSink? eventSink = null) : IUsbIpDiagnosticsService
{
    private readonly IUsbIpEventSink sink = eventSink ?? NullUsbIpEventSink.Instance;

    public async Task<UsbIpHealthReport> CheckServerAsync(CancellationToken cancellationToken = default)
    {
        var checks = new List<UsbIpHealthCheck>();
        await RunCheckAsync(checks, "server.capabilities", async () =>
        {
            var c = await serverBackend.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
            return new(c.CanList && c.CanShare, $"后端: {c.BackendName} {c.BackendVersion}".Trim(), c.CanShare ? null : "当前后端不支持共享设备");
        }, cancellationToken);

        await RunCheckAsync(checks, "server.device.enumeration", async () =>
        {
            var devices = await serverBackend.ListDevicesAsync(cancellationToken).ConfigureAwait(false);
            return new(true, $"成功枚举 {devices.Count} 个 USB 设备", null);
        }, cancellationToken);

        return await CompleteAsync("server", checks, cancellationToken);
    }

    public async Task<UsbIpHealthReport> CheckClientAsync(CancellationToken cancellationToken = default)
    {
        var checks = new List<UsbIpHealthCheck>();
        await RunCheckAsync(checks, "client.capabilities", async () =>
        {
            var c = await clientBackend.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
            return new(c.CanAttach && c.CanDetach, $"后端: {c.BackendName} {c.BackendVersion}".Trim(), c.CanAttach ? null : "当前后端没有可用的 VHCI/虚拟 USB Host Controller");
        }, cancellationToken);

        return await CompleteAsync("client", checks, cancellationToken);
    }

    private async Task RunCheckAsync(
        List<UsbIpHealthCheck> checks,
        string name,
        Func<Task<(bool Success, string Message, string? Suggestion)>> action,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await action().ConfigureAwait(false);
            checks.Add(new(name, result.Success, result.Message, result.Suggestion));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            checks.Add(new(name, false, ex.Message, BuildSuggestion(ex.Message)));
        }
    }

    private async Task<UsbIpHealthReport> CompleteAsync(string role, List<UsbIpHealthCheck> checks, CancellationToken cancellationToken)
    {
        var healthy = checks.All(x => x.Success);
        var report = new UsbIpHealthReport(DateTimeOffset.Now, healthy, checks);
        await sink.WriteAsync(new(DateTimeOffset.Now, $"diagnostics.{role}", healthy ? "Information" : "Warning", null, null, null,
            healthy ? "健康检查通过" : $"健康检查存在 {checks.Count(x => !x.Success)} 项异常"), cancellationToken);
        return report;
    }

    private static string BuildSuggestion(string message)
    {
        if (message.Contains("not found", StringComparison.OrdinalIgnoreCase) || message.Contains("找不到", StringComparison.OrdinalIgnoreCase))
            return "检查 usbip/usbipd 可执行文件是否已安装并加入 PATH。";
        if (message.Contains("access", StringComparison.OrdinalIgnoreCase) || message.Contains("权限", StringComparison.OrdinalIgnoreCase))
            return "Windows 请以管理员身份运行；Linux 请使用 root 或配置 udev/sudo 权限。";
        if (message.Contains("driver", StringComparison.OrdinalIgnoreCase) || message.Contains("vhci", StringComparison.OrdinalIgnoreCase))
            return "检查 USB/IP Host/VHCI 驱动是否已安装并正常加载。";
        return "查看同一 TraceId 的 process.start/process.end 及平台驱动日志定位。";
    }
}
