using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MyUsbIP.Abstractions;

namespace MyUsbIP.Platform;

/// <summary>外部进程执行结果。</summary>
internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
/// 带超时、取消和诊断事件的进程执行器。
/// 所有平台命令都统一从这里经过，便于后期定位“命令卡死/驱动无响应/权限不足”。
/// </summary>
internal sealed class UsbIpProcessRunner(IUsbIpEventSink sink, TimeSpan timeout)
{
    public async Task<ProcessResult> RunAsync(string fileName, string arguments, string operation, string? busId, string? host, CancellationToken cancellationToken)
    {
        using var activity = UsbIpDiagnostics.StartActivity(operation, busId, host);
        var started = Stopwatch.GetTimestamp();
        await sink.WriteAsync(new(DateTimeOffset.Now, "process.start", "Information", Activity.Current?.TraceId.ToString(), busId, host,
            $"执行: {fileName} {arguments}", new Dictionary<string, object?>
            {
                ["fileName"] = fileName,
                ["arguments"] = arguments,
                ["operation"] = operation,
            }), cancellationToken);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true,
            },
            EnableRaisingEvents = true,
        };

        ProcessResult? completedResult = null;
        try
        {
            if (!process.Start()) throw new InvalidOperationException($"无法启动进程 {fileName}。 ");
            var stdoutTask = process.StandardOutput.ReadToEndAsync(linked.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(linked.Token);
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            completedResult = new ProcessResult(process.ExitCode, await stdoutTask, await stderrTask);

            await sink.WriteAsync(new(DateTimeOffset.Now, "process.result",
                completedResult.ExitCode == 0 ? "Information" : "Warning",
                Activity.Current?.TraceId.ToString(), busId, host,
                $"{operation} ExitCode={completedResult.ExitCode}", new Dictionary<string, object?>
                {
                    ["operation"] = operation,
                    ["exitCode"] = completedResult.ExitCode,
                    ["stdout"] = completedResult.StandardOutput,
                    ["stderr"] = completedResult.StandardError,
                }), CancellationToken.None);

            if (completedResult.ExitCode != 0)
            {
                UsbIpDiagnostics.Failures.Add(1, new KeyValuePair<string, object?>("operation", operation));
                var error = string.IsNullOrWhiteSpace(completedResult.StandardError)
                    ? completedResult.StandardOutput
                    : completedResult.StandardError;
                throw new InvalidOperationException($"{fileName} 返回 ExitCode={completedResult.ExitCode}: {error.Trim()}");
            }
            return completedResult;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            TryKill(process);
            UsbIpDiagnostics.Failures.Add(1, new KeyValuePair<string, object?>("operation", operation));
            await sink.WriteAsync(new(DateTimeOffset.Now, "process.timeout", "Error", Activity.Current?.TraceId.ToString(),
                busId, host, $"执行 {fileName} 超过 {timeout.TotalSeconds:0.#} 秒，已终止进程。",
                new Dictionary<string, object?>
                {
                    ["operation"] = operation,
                    ["timeoutSeconds"] = timeout.TotalSeconds,
                }), CancellationToken.None);
            throw new TimeoutException($"执行 {fileName} 超过 {timeout.TotalSeconds:0.#} 秒，已终止进程。 ");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 调用方取消时也必须结束第三方 usbip/usbipd 进程。
            // 否则上层已经认为 Attach 取消，usbip.exe 仍可能在后台继续完成挂载，形成“幽灵连接”。
            TryKill(process);
            await sink.WriteAsync(new(DateTimeOffset.Now, "process.cancelled", "Information",
                Activity.Current?.TraceId.ToString(), busId, host,
                $"调用方已取消 {operation}，外部进程已终止。",
                new Dictionary<string, object?> { ["operation"] = operation }), CancellationToken.None);
            throw;
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            UsbIpDiagnostics.OperationDurationMs.Record(elapsed, new KeyValuePair<string, object?>("operation", operation));
            UsbIpDiagnostics.Operations.Add(1, new KeyValuePair<string, object?>("operation", operation));
            await sink.WriteAsync(new(DateTimeOffset.Now, "process.end", "Information", Activity.Current?.TraceId.ToString(), busId, host,
                $"{operation} 完成，耗时 {elapsed:0.0}ms", new Dictionary<string, object?>
                {
                    ["operation"] = operation,
                    ["elapsedMs"] = elapsed,
                    ["exitCode"] = completedResult?.ExitCode,
                }), CancellationToken.None);
        }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }
}

/// <summary>解析 usbip port 输出，取得远端设备对应的本地 VHCI 端口。</summary>
internal static partial class UsbIpPortParser
{
    [GeneratedRegex(@"(?ims)Port\s+(?<port>\d+):.*?usbip://(?<host>[^:/\s]+):(?<tcp>\d+)/(?<bus>[^\s]+)", RegexOptions.CultureInvariant)]
    private static partial Regex PortRegex();

    public static int? FindPort(string text, string host, int serverPort, string busId)
    {
        foreach (Match match in PortRegex().Matches(text))
        {
            if (!string.Equals(match.Groups["host"].Value, host, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(match.Groups["bus"].Value, busId, StringComparison.OrdinalIgnoreCase)) continue;
            if (!int.TryParse(match.Groups["tcp"].Value, out var tcpPort) || tcpPort != serverPort) continue;
            if (int.TryParse(match.Groups["port"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var localPort)) return localPort;
        }
        return null;
    }
}

/// <summary>Linux USB/IP 后端，依赖 usbip 工具及内核 usbip_host/vhci_hcd 模块。</summary>
public sealed class LinuxUsbIpBackend : IUsbIpServerBackend, IUsbIpClientBackend
{
    private readonly UsbIpProcessRunner runner;
    public LinuxUsbIpBackend(IUsbIpEventSink? eventSink = null, TimeSpan? commandTimeout = null) => runner = new(eventSink ?? NullUsbIpEventSink.Instance, commandTimeout ?? TimeSpan.FromSeconds(10));

    public Task<UsbIpBackendCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new UsbIpBackendCapabilities(true, true, true, true, "Linux usbip"));

    public async Task<IReadOnlyList<UsbIpDeviceInfo>> ListDevicesAsync(CancellationToken cancellationToken = default)
    {
        var r = await runner.RunAsync("usbip", "list -l", "server.device.list", null, null, cancellationToken);
        return ParseUsbIpList(r.StandardOutput);
    }

    public Task ShareAsync(string busId, CancellationToken cancellationToken = default) => RunNoResult("usbip", $"bind -b {Quote(busId)}", "server.device.share", busId, null, cancellationToken);
    public Task UnshareAsync(string busId, CancellationToken cancellationToken = default) => RunNoResult("usbip", $"unbind -b {Quote(busId)}", "server.device.unshare", busId, null, cancellationToken);

    public async Task<IReadOnlyList<UsbIpDeviceInfo>> ListRemoteDevicesAsync(string host, int port = 3240, CancellationToken cancellationToken = default)
    {
        var portOption = BuildTcpPortOption(port);
        var r = await runner.RunAsync("usbip", $"{portOption}list -r {Quote(host)}", "client.remote.list", null, host, cancellationToken);
        return ParseUsbIpList(r.StandardOutput);
    }

    public async Task<UsbIpAttachResult> AttachAsync(string host, string busId, int port = 3240, CancellationToken cancellationToken = default)
    {
        var portOption = BuildTcpPortOption(port);
        await runner.RunAsync("usbip", $"{portOption}attach -r {Quote(host)} -b {Quote(busId)}", "client.attach", busId, host, cancellationToken);
        var localPort = await TryResolveLocalPortAsync(host, busId, port, cancellationToken).ConfigureAwait(false);
        return new(true, busId, localPort, localPort is null ? "已提交到 Linux vhci_hcd，未能解析本地端口。 " : $"已挂载到 Linux VHCI 端口 {localPort}。 ");
    }

    public Task DetachAsync(int port, CancellationToken cancellationToken = default) => RunNoResult("usbip", $"detach --port={port.ToString(CultureInfo.InvariantCulture)}", "client.detach", null, null, cancellationToken);

    private async Task<int?> TryResolveLocalPortAsync(string host, string busId, int serverPort, CancellationToken cancellationToken)
    {
        try
        {
            var r = await runner.RunAsync("usbip", "port", "client.port.list", busId, host, cancellationToken);
            return UsbIpPortParser.FindPort(r.StandardOutput, host, serverPort, busId);
        }
        catch
        {
            return null;
        }
    }

    private async Task RunNoResult(string file, string args, string op, string? busId, string? host, CancellationToken ct) => await runner.RunAsync(file, args, op, busId, host, ct);
    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";
    private static string BuildTcpPortOption(int port) => port == 3240 ? string.Empty : $"--tcp-port {port.ToString(CultureInfo.InvariantCulture)} ";

    internal static IReadOnlyList<UsbIpDeviceInfo> ParseUsbIpList(string text)
    {
        var list = new List<UsbIpDeviceInfo>();
        var rx = new Regex(@"(?im)^\s*-?\s*(?<bus>[0-9]+-[0-9.]+):\s*(?<name>.*?)\s*\((?<vid>[0-9a-f]{4}):(?<pid>[0-9a-f]{4})\)", RegexOptions.Compiled);
        foreach (Match m in rx.Matches(text))
        {
            list.Add(new UsbIpDeviceInfo
            {
                BusId = m.Groups["bus"].Value,
                Product = m.Groups["name"].Value.Trim(),
                VendorId = Convert.ToUInt16(m.Groups["vid"].Value, 16),
                ProductId = Convert.ToUInt16(m.Groups["pid"].Value, 16),
                State = UsbIpDeviceState.Available,
            });
        }
        return list;
    }
}

/// <summary>
/// Windows USB/IP 后端。
/// 服务端依赖 usbipd-win 的 usbipd.exe；客户端依赖 usbip-win/VHCI 提供的 usbip.exe。
/// </summary>
public sealed class WindowsUsbIpBackend : IUsbIpServerBackend, IUsbIpClientBackend
{
    private readonly UsbIpProcessRunner runner;
    private readonly string usbipdPath;
    private readonly string usbipPath;

    public WindowsUsbIpBackend(string usbipdPath = "usbipd.exe", string usbipPath = "usbip.exe", IUsbIpEventSink? eventSink = null, TimeSpan? commandTimeout = null)
    {
        this.usbipdPath = usbipdPath;
        this.usbipPath = usbipPath;
        runner = new(eventSink ?? NullUsbIpEventSink.Instance, commandTimeout ?? TimeSpan.FromSeconds(10));
    }

    public Task<UsbIpBackendCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new UsbIpBackendCapabilities(true, true, true, true, "Windows usbipd-win + usbip-win"));

    public async Task<IReadOnlyList<UsbIpDeviceInfo>> ListDevicesAsync(CancellationToken cancellationToken = default)
    {
        var r = await runner.RunAsync(usbipdPath, "list", "server.device.list", null, null, cancellationToken);
        return ParseUsbipdList(r.StandardOutput);
    }

    public Task ShareAsync(string busId, CancellationToken cancellationToken = default) => RunNoResult(usbipdPath, $"bind --busid {Quote(busId)}", "server.device.share", busId, null, cancellationToken);
    public Task UnshareAsync(string busId, CancellationToken cancellationToken = default) => RunNoResult(usbipdPath, $"unbind --busid {Quote(busId)}", "server.device.unshare", busId, null, cancellationToken);

    public async Task<IReadOnlyList<UsbIpDeviceInfo>> ListRemoteDevicesAsync(string host, int port = 3240, CancellationToken cancellationToken = default)
    {
        EnsureDefaultPort(port);
        var r = await runner.RunAsync(usbipPath, $"list -r {Quote(host)}", "client.remote.list", null, host, cancellationToken);
        return LinuxUsbIpBackend.ParseUsbIpList(r.StandardOutput);
    }

    public async Task<UsbIpAttachResult> AttachAsync(string host, string busId, int port = 3240, CancellationToken cancellationToken = default)
    {
        EnsureDefaultPort(port);
        await runner.RunAsync(usbipPath, $"attach -r {Quote(host)} -b {Quote(busId)}", "client.attach", busId, host, cancellationToken);
        var localPort = await TryResolveLocalPortAsync(host, busId, port, cancellationToken).ConfigureAwait(false);
        return new(true, busId, localPort, localPort is null ? "已提交到 Windows VHCI，未能解析本地端口。 " : $"已挂载到 Windows VHCI 端口 {localPort}。 ");
    }

    public Task DetachAsync(int port, CancellationToken cancellationToken = default) => RunNoResult(usbipPath, $"detach --port={port.ToString(CultureInfo.InvariantCulture)}", "client.detach", null, null, cancellationToken);

    private async Task<int?> TryResolveLocalPortAsync(string host, string busId, int serverPort, CancellationToken cancellationToken)
    {
        try
        {
            var r = await runner.RunAsync(usbipPath, "port", "client.port.list", busId, host, cancellationToken);
            return UsbIpPortParser.FindPort(r.StandardOutput, host, serverPort, busId);
        }
        catch
        {
            return null;
        }
    }

    private static void EnsureDefaultPort(int port)
    {
        if (port != 3240)
            throw new NotSupportedException("当前 Windows usbip-win CLI 后端仅保证标准 TCP 3240 端口兼容。需要自定义端口时请实现自定义 IUsbIpClientBackend。 ");
    }

    private async Task RunNoResult(string file, string args, string op, string? busId, string? host, CancellationToken ct) => await runner.RunAsync(file, args, op, busId, host, ct);
    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

    private static IReadOnlyList<UsbIpDeviceInfo> ParseUsbipdList(string text)
    {
        var list = new List<UsbIpDeviceInfo>();
        var rx = new Regex(@"(?im)^\s*(?<bus>\d+-\d+)\s+(?<vid>[0-9a-f]{4}):(?<pid>[0-9a-f]{4})\s+(?<name>.+?)(?:\s{2,}(?<state>Shared|Not shared|Attached|Persisted).*)?$", RegexOptions.Compiled);
        foreach (Match m in rx.Matches(text))
        {
            var stateText = m.Groups["state"].Value;
            list.Add(new UsbIpDeviceInfo
            {
                BusId = m.Groups["bus"].Value,
                Product = m.Groups["name"].Value.Trim(),
                VendorId = Convert.ToUInt16(m.Groups["vid"].Value, 16),
                ProductId = Convert.ToUInt16(m.Groups["pid"].Value, 16),
                State = stateText.Contains("Shared", StringComparison.OrdinalIgnoreCase) || stateText.Contains("Persisted", StringComparison.OrdinalIgnoreCase) ? UsbIpDeviceState.Shared : UsbIpDeviceState.Available,
            });
        }
        return list;
    }
}

/// <summary>根据当前操作系统创建默认后端。</summary>
public static class UsbIpBackendFactory
{
    public static object CreateDefault(IUsbIpEventSink? eventSink = null, TimeSpan? commandTimeout = null)
    {
        if (OperatingSystem.IsWindows()) return new WindowsUsbIpBackend(eventSink: eventSink, commandTimeout: commandTimeout);
        if (OperatingSystem.IsLinux()) return new LinuxUsbIpBackend(eventSink, commandTimeout);
        throw new PlatformNotSupportedException("当前默认后端仅支持 Windows 与 Linux。macOS 可通过实现 IUsbIpServerBackend/IUsbIpClientBackend 扩展。 ");
    }

    public static IUsbIpServerBackend CreateServerBackend(IUsbIpEventSink? eventSink = null, TimeSpan? commandTimeout = null) =>
        (IUsbIpServerBackend)CreateDefault(eventSink, commandTimeout);

    public static IUsbIpClientBackend CreateClientBackend(IUsbIpEventSink? eventSink = null, TimeSpan? commandTimeout = null) =>
        (IUsbIpClientBackend)CreateDefault(eventSink, commandTimeout);
}
