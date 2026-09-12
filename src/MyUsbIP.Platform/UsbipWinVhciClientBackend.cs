using System.Globalization;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using MyUsbIP.Abstractions;
using MyUsbIP.Protocol;

namespace MyUsbIP.Platform;

/// <summary>
/// Windows 客户端专用后端。
/// 默认配合 usbip-win2 的 UDE/VHCI 使用，同时保留标准 USB/IP list/attach/detach 命令兼容。
/// </summary>
public sealed class UsbipWinVhciClientBackend : IUsbIpClientBackend
{
    private readonly UsbIpProcessRunner commandRunner;
    private readonly UsbIpProcessRunner attachRunner;
    private readonly string usbipPath;
    private readonly string receiveMode;

    public UsbipWinVhciClientBackend(
        string usbipPath = "usbip.exe",
        IUsbIpEventSink? eventSink = null,
        TimeSpan? commandTimeout = null,
        TimeSpan? attachTimeout = null,
        string receiveMode = "zero-copy")
    {
        this.usbipPath = ResolveUsbipPath(usbipPath);
        this.receiveMode = NormalizeReceiveMode(receiveMode);
        var sink = eventSink ?? NullUsbIpEventSink.Instance;
        var normalTimeout = commandTimeout ?? TimeSpan.FromSeconds(30);
        commandRunner = new UsbIpProcessRunner(sink, normalTimeout);
        attachRunner = new UsbIpProcessRunner(sink, attachTimeout ?? normalTimeout);
    }

    public Task<UsbIpBackendCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new UsbIpBackendCapabilities(
            CanList: true,
            CanShare: false,
            CanAttach: true,
            CanDetach: true,
            BackendName: "Windows usbip-win2 UDE/VHCI"));

    public async Task<IReadOnlyList<UsbIpDeviceInfo>> ListRemoteDevicesAsync(
        string host,
        int port = 3240,
        CancellationToken cancellationToken = default)
    {
        var result = await commandRunner.RunAsync(
            usbipPath,
            $"{BuildTcpPortOption(port)}list -r {Quote(host)}",
            "client.remote.list",
            null,
            host,
            cancellationToken).ConfigureAwait(false);

        var standard = ParseRemoteList(result.StandardOutput);

        // MyUsbIP 服务端额外提供运行态管理信息：连接客户端、会话、完整 USB 描述符等。
        // 查询失败时仍然保持对任意标准 USB/IP 服务端的兼容。
        try
        {
            var detailed = await QueryMyUsbIpDeviceStatusAsync(host, port, cancellationToken).ConfigureAwait(false);
            var byBusId = detailed.ToDictionary(x => x.BusId, StringComparer.OrdinalIgnoreCase);
            return standard.Select(x =>
            {
                if (!byBusId.TryGetValue(x.BusId, out var d)) return x;
                return d with
                {
                    // usbip.exe 带 usb.ids 数据库，优先保留其更易读的产品名称。
                    Product = string.IsNullOrWhiteSpace(x.Product) ? d.Product : x.Product,
                };
            }).Concat(detailed.Where(d => standard.All(x => !string.Equals(x.BusId, d.BusId, StringComparison.OrdinalIgnoreCase))))
              .ToArray();
        }
        catch
        {
            return standard;
        }
    }

    public async Task<UsbIpAttachResult> AttachAsync(
        string host,
        string busId,
        int port = 3240,
        CancellationToken cancellationToken = default)
    {
        // usbip-win2 0.9.8.0：
        // --once 只执行一次 Attach，不进入自动重试；
        // --receive-mode 可选择 zero-copy 或 low-latency，默认 zero-copy 适合大多数 UKey/串口/存储设备。
        var result = await attachRunner.RunAsync(
            usbipPath,
            $"{BuildTcpPortOption(port)}attach -r {Quote(host)} -b {Quote(busId)} --once --receive-mode={receiveMode}",
            "client.attach",
            busId,
            host,
            cancellationToken).ConfigureAwait(false);

        var localPort = ParseAttachedPort(result.StandardOutput)
                        ?? await TryResolveLocalPortAsync(host, busId, port, cancellationToken).ConfigureAwait(false);

        return new UsbIpAttachResult(
            true,
            busId,
            localPort,
            localPort is null
                ? $"设备已提交到 usbip-win2 UDE/VHCI，receive-mode={receiveMode}，未能解析本地端口。"
                : $"已挂载到 usbip-win2 UDE/VHCI 端口 {localPort}，receive-mode={receiveMode}。 ");
    }

    public async Task DetachAsync(int port, CancellationToken cancellationToken = default)
    {
        await commandRunner.RunAsync(
            usbipPath,
            $"detach -p {port.ToString(CultureInfo.InvariantCulture)}",
            "client.detach",
            null,
            null,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<int?> TryResolveLocalPortAsync(
        string host,
        string busId,
        int serverPort,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await commandRunner.RunAsync(
                usbipPath,
                "port",
                "client.port.list",
                busId,
                host,
                cancellationToken).ConfigureAwait(false);
            return UsbIpPortParser.FindPort(result.StandardOutput, host, serverPort, busId);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<IReadOnlyList<UsbIpDeviceInfo>> QueryMyUsbIpDeviceStatusAsync(
        string host, int port, CancellationToken cancellationToken)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        await using var stream = tcp.GetStream();
        await UsbDeviceStatusProtocol.WriteRequestAsync(stream, cancellationToken).ConfigureAwait(false);
        return await UsbDeviceStatusProtocol.ReadReplyAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    internal static IReadOnlyList<UsbIpDeviceInfo> ParseRemoteList(string text)
    {
        var devices = new List<UsbIpDeviceInfo>();
        var regex = new Regex(
            @"(?im)^\s*-?\s*(?<bus>[^\s:]+)\s*:\s*(?<name>.*?)\s*\((?<vid>[0-9a-f]{4}):(?<pid>[0-9a-f]{4})\)\s*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        foreach (Match match in regex.Matches(text))
        {
            devices.Add(new UsbIpDeviceInfo
            {
                BusId = match.Groups["bus"].Value,
                Product = match.Groups["name"].Value.Trim(),
                VendorId = Convert.ToUInt16(match.Groups["vid"].Value, 16),
                ProductId = Convert.ToUInt16(match.Groups["pid"].Value, 16),
                State = UsbIpDeviceState.Available,
            });
        }

        return devices;
    }

    private static int? ParseAttachedPort(string text)
    {
        var match = Regex.Match(text, @"(?im)successfully\s+attached\s+to\s+port\s+(?<port>\d+)");
        if (!match.Success)
            match = Regex.Match(text, @"(?im)succesfully\s+attached\s+to\s+port\s+(?<port>\d+)");
        return match.Success && int.TryParse(match.Groups["port"].Value, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var port) ? port : null;
    }

    private static string BuildTcpPortOption(int port)
        => port == UsbIpProtocolConstants.DefaultPort
            ? string.Empty
            : $"--tcp-port {port.ToString(CultureInfo.InvariantCulture)} ";

    private static string NormalizeReceiveMode(string value)
    {
        var mode = string.IsNullOrWhiteSpace(value) ? "zero-copy" : value.Trim().ToLowerInvariant();
        return mode switch
        {
            "zero-copy" => "zero-copy",
            "low-latency" => "low-latency",
            _ => throw new ArgumentOutOfRangeException(nameof(value), value,
                "usbip-win2 ReceiveMode 仅支持 zero-copy 或 low-latency。"),
        };
    }

    private static string ResolveUsbipPath(string configuredPath)
    {
        if (!OperatingSystem.IsWindows()) return configuredPath;
        if (Path.IsPathRooted(configuredPath) && File.Exists(configuredPath)) return configuredPath;

        // 优先固定到 usbip-win2 官方安装目录，避免覆盖升级后旧 CMD 的 PATH 仍指向 cezanne/usbip-win。
        if (string.Equals(configuredPath, "usbip.exe", StringComparison.OrdinalIgnoreCase))
        {
            var usbipWin2 = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "USBip",
                "usbip.exe");
            if (File.Exists(usbipWin2)) return usbipWin2;
        }

        return configuredPath;
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";
}
