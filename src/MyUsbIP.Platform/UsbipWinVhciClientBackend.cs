using System.Globalization;
using System.Text.RegularExpressions;
using MyUsbIP.Abstractions;

namespace MyUsbIP.Platform;

/// <summary>
/// Windows 客户端专用后端，仅依赖 usbip-win 的 usbip.exe 与 VHCI 驱动。
/// 不包含任何 usbipd-win 服务端依赖。
/// </summary>
public sealed class UsbipWinVhciClientBackend : IUsbIpClientBackend
{
    private readonly UsbIpProcessRunner runner;
    private readonly string usbipPath;

    public UsbipWinVhciClientBackend(
        string usbipPath = "usbip.exe",
        IUsbIpEventSink? eventSink = null,
        TimeSpan? commandTimeout = null)
    {
        this.usbipPath = usbipPath;
        runner = new UsbIpProcessRunner(
            eventSink ?? NullUsbIpEventSink.Instance,
            commandTimeout ?? TimeSpan.FromSeconds(10));
    }

    public Task<UsbIpBackendCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new UsbIpBackendCapabilities(
            CanList: true,
            CanShare: false,
            CanAttach: true,
            CanDetach: true,
            BackendName: "Windows usbip-win VHCI"));

    public async Task<IReadOnlyList<UsbIpDeviceInfo>> ListRemoteDevicesAsync(
        string host,
        int port = 3240,
        CancellationToken cancellationToken = default)
    {
        EnsureDefaultPort(port);
        var result = await runner.RunAsync(
            usbipPath,
            $"list -r {Quote(host)}",
            "client.remote.list",
            null,
            host,
            cancellationToken).ConfigureAwait(false);

        return ParseRemoteList(result.StandardOutput);
    }

    public async Task<UsbIpAttachResult> AttachAsync(
        string host,
        string busId,
        int port = 3240,
        CancellationToken cancellationToken = default)
    {
        EnsureDefaultPort(port);
        await runner.RunAsync(
            usbipPath,
            $"attach -r {Quote(host)} -b {Quote(busId)}",
            "client.attach",
            busId,
            host,
            cancellationToken).ConfigureAwait(false);

        var localPort = await TryResolveLocalPortAsync(host, busId, port, cancellationToken).ConfigureAwait(false);
        return new UsbIpAttachResult(
            true,
            busId,
            localPort,
            localPort is null
                ? "已提交到 usbip-win VHCI，未能解析本地端口。"
                : $"已挂载到 usbip-win VHCI 端口 {localPort}。");
    }

    public async Task DetachAsync(int port, CancellationToken cancellationToken = default)
    {
        await runner.RunAsync(
            usbipPath,
            $"detach --port={port.ToString(CultureInfo.InvariantCulture)}",
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
            var result = await runner.RunAsync(
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

    /// <summary>
    /// usbip list -r 的 BusId 本质是服务端提供的固定字段，不能假设一定为 Linux 的 1-2.3 形式。
    /// 因此这里允许 MyUsbIP/UsbDk 生成的十六进制 BusId。
    /// </summary>
    internal static IReadOnlyList<UsbIpDeviceInfo> ParseRemoteList(string text)
    {
        var devices = new List<UsbIpDeviceInfo>();

        // 常见格式：
        //   - 1-2: Name (1234:5678)
        //   - 00000002-00000004: Name (096e:0303)
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

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

    private static void EnsureDefaultPort(int port)
    {
        if (port != 3240)
        {
            throw new NotSupportedException(
                "usbip-win CLI 客户端当前固定使用标准 USB/IP TCP 3240 端口。" +
                "MyUsbIP UsbDk 服务端请保持 NativeServer.Port=3240。");
        }
    }
}
