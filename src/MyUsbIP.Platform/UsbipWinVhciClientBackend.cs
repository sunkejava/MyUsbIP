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
    private readonly UsbIdsResolver usbIdsResolver;
    private readonly string usbipPath;
    private readonly string receiveMode;

    public UsbipWinVhciClientBackend(
        string usbipPath = "usbip.exe",
        IUsbIpEventSink? eventSink = null,
        TimeSpan? commandTimeout = null,
        TimeSpan? attachTimeout = null,
        string receiveMode = "zero-copy",
        string? usbIdsPath = null)
    {
        this.usbipPath = ResolveUsbipPath(usbipPath);
        this.receiveMode = NormalizeReceiveMode(receiveMode);
        usbIdsResolver = new UsbIdsResolver(usbIdsPath);
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

        // usbip-win2 自己也使用 usb.ids，但其数据库可能缺少某些 PID，输出 unknown vendor/product。
        // MyUsbIP 再做一次本地解析增强：优先设备自身信息，其次 usb.ids，最后使用中性 VID/PID 兜底名称。
        var standard = ParseRemoteList(result.StandardOutput)
            .Select(usbIdsResolver.Enrich)
            .ToArray();

        try
        {
            var detailed = await QueryMyUsbIpDeviceStatusAsync(host, port, cancellationToken).ConfigureAwait(false);
            var byBusId = detailed.ToDictionary(x => x.BusId, StringComparer.OrdinalIgnoreCase);
            return standard.Select(x =>
            {
                if (!byBusId.TryGetValue(x.BusId, out var d)) return x;

                var detailedProductUseful = !UsbIdsResolver.IsUnknownProduct(d.Product)
                                            && !LooksLikeHardwareId(d.Product);
                var standardProductGeneric = x.Product?.StartsWith("USB device ", StringComparison.OrdinalIgnoreCase) == true;
                var product = detailedProductUseful && standardProductGeneric ? d.Product : x.Product;
                if (UsbIdsResolver.IsUnknownProduct(product)) product = d.Product;

                var manufacturer = !UsbIdsResolver.IsUnknownVendor(d.Manufacturer)
                    ? d.Manufacturer
                    : x.Manufacturer;

                return d with
                {
                    Manufacturer = manufacturer,
                    Product = product,
                };
            }).Concat(detailed
                    .Where(d => standard.All(x => !string.Equals(x.BusId, d.BusId, StringComparison.OrdinalIgnoreCase)))
                    .Select(usbIdsResolver.Enrich))
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

    public async Task<string> GetPortOutputAsync(int? localPort = null, CancellationToken cancellationToken = default)
    {
        var arguments = localPort is null
            ? "port"
            : $"port {localPort.Value.ToString(CultureInfo.InvariantCulture)}";
        var result = await commandRunner.RunAsync(
            usbipPath,
            arguments,
            "client.port.list",
            null,
            null,
            cancellationToken).ConfigureAwait(false);

        var output = result.StandardOutput.Trim();
        if (!string.IsNullOrWhiteSpace(result.StandardError))
            output = string.IsNullOrWhiteSpace(output)
                ? result.StandardError.Trim()
                : output + Environment.NewLine + result.StandardError.Trim();
        return string.IsNullOrWhiteSpace(output) ? "当前没有已导入的 USB/IP 设备。" : output;
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
            var name = match.Groups["name"].Value.Trim();
            var manufacturer = default(string);
            var product = name;
            var separator = name.IndexOf(" : ", StringComparison.Ordinal);
            if (separator >= 0)
            {
                manufacturer = name[..separator].Trim();
                product = name[(separator + 3)..].Trim();
            }

            devices.Add(new UsbIpDeviceInfo
            {
                BusId = match.Groups["bus"].Value,
                Manufacturer = manufacturer,
                Product = product,
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

    private static bool LooksLikeHardwareId(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && (value.Contains("VID_", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase));

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";
}
