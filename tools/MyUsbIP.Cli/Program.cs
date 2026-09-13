using System.Text.Json;
using MyUsbIP.Abstractions;
using MyUsbIP.Client;
using MyUsbIP.Platform;
using MyUsbIP.Runtime;
using MyUsbIP.Server;

var configPath = Environment.GetEnvironmentVariable("MYUSBIP_CLIENT_CONFIG");
if (string.IsNullOrWhiteSpace(configPath)) configPath = Path.Combine(AppContext.BaseDirectory, "clientsettings.json");

var config = File.Exists(configPath)
    ? JsonSerializer.Deserialize<ClientCliConfig>(await File.ReadAllTextAsync(configPath), new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
    }) ?? new ClientCliConfig()
    : new ClientCliConfig();

var configuredDirectory = Environment.ExpandEnvironmentVariables(config.Logging.Directory);
var logDirectory = Path.IsPathRooted(configuredDirectory)
    ? configuredDirectory
    : Path.Combine(AppContext.BaseDirectory, configuredDirectory);
Directory.CreateDirectory(logDirectory);
LogFileMaintenance.Cleanup(logDirectory, "myusbip-client-*.jsonl", config.Logging.RetentionDays);

var logPath = Path.Combine(logDirectory, $"myusbip-client-{DateTime.Now:yyyyMMdd}.jsonl");
await using JsonLinesUsbIpEventSink? fileSink = config.Logging.Enabled ? new JsonLinesUsbIpEventSink(logPath) : null;
var memorySink = new MemoryUsbIpEventSink(1000);
IUsbIpEventSink sink = fileSink is null
    ? memorySink
    : new CompositeUsbIpEventSink(fileSink, memorySink);

var timeout = TimeSpan.FromSeconds(Math.Max(1, config.CommandTimeoutSeconds));
var attachTimeout = TimeSpan.FromSeconds(Math.Max(config.CommandTimeoutSeconds, config.AttachTimeoutSeconds));
var backendObject = UsbIpBackendFactory.CreateDefault(sink, timeout);
var serverBackend = (IUsbIpServerBackend)backendObject;
UsbipWinVhciClientBackend? windowsClientBackend = null;
IUsbIpClientBackend clientBackend;
if (OperatingSystem.IsWindows())
{
    windowsClientBackend = new UsbipWinVhciClientBackend(
        usbipPath: config.UsbipWinPath,
        eventSink: sink,
        commandTimeout: timeout,
        attachTimeout: attachTimeout,
        receiveMode: config.ReceiveMode);
    clientBackend = windowsClientBackend;
}
else
{
    clientBackend = (IUsbIpClientBackend)backendObject;
}

var server = new MyUsbIpServer(serverBackend, sink);
var client = new MyUsbIpClient(clientBackend, sink);
var diagnostics = new UsbIpDiagnosticsService(serverBackend, clientBackend, sink);

if (args.Length == 0)
{
    PrintHelp();
    return;
}

try
{
    await sink.WriteAsync(new UsbIpEvent(DateTimeOffset.Now, "cli.command", "Information", null, null, null,
        string.Join(' ', args), new Dictionary<string, object?>
        {
            ["arguments"] = args,
            ["configPath"] = configPath,
            ["logPath"] = config.Logging.Enabled ? logPath : null,
            ["commandTimeoutSeconds"] = config.CommandTimeoutSeconds,
            ["attachTimeoutSeconds"] = config.AttachTimeoutSeconds,
            ["receiveMode"] = config.ReceiveMode,
        }));

    switch (args[0].ToLowerInvariant())
    {
        case "server" when args.Length >= 2:
            await HandleServerAsync(args[1..]);
            break;
        case "client" when args.Length >= 2:
            await HandleClientAsync(args[1..]);
            break;
        case "diag":
            await PrintReportAsync(await diagnostics.CheckServerAsync());
            await PrintReportAsync(await diagnostics.CheckClientAsync());
            break;
        default:
            PrintHelp();
            Environment.ExitCode = 2;
            break;
    }
}
catch (Exception ex)
{
    await sink.WriteAsync(new UsbIpEvent(DateTimeOffset.Now, "cli.failed", "Error", null, null, null,
        ex.Message, Exception: ex));
    Console.Error.WriteLine($"[失败] {ex.Message}");
    if (config.Logging.Enabled) Console.Error.WriteLine($"诊断日志: {logPath}");
    Environment.ExitCode = 1;
}

async Task HandleServerAsync(string[] command)
{
    switch (command[0].ToLowerInvariant())
    {
        case "list":
            PrintDevices(await server.GetDevicesAsync());
            break;
        case "share" when command.Length >= 2:
            await server.ShareAsync(command[1]);
            Console.WriteLine($"已共享 {command[1]}");
            break;
        case "unshare" when command.Length >= 2:
            await server.UnshareAsync(command[1]);
            Console.WriteLine($"已取消共享 {command[1]}");
            break;
        case "watch":
            var monitor = new UsbIpDeviceMonitor(server, sink);
            await foreach (var change in monitor.WatchAsync())
                Console.WriteLine($"{change.OccurredAt:HH:mm:ss} {change.Kind,-7} {change.Device.BusId,-20} {change.Device.VidPid} {change.Device.Product}");
            break;
        case "diag":
            await PrintReportAsync(await diagnostics.CheckServerAsync());
            break;
        default:
            PrintHelp();
            break;
    }
}

async Task HandleClientAsync(string[] command)
{
    switch (command[0].ToLowerInvariant())
    {
        case "list" when command.Length >= 2:
            PrintDevices(await client.GetRemoteDevicesAsync(command[1]));
            break;
        case "port":
        {
            if (windowsClientBackend is null)
                throw new PlatformNotSupportedException("myusbip client port 当前仅支持 Windows usbip-win2 UDE/VHCI。 ");

            int? localPort = null;
            if (command.Length >= 2)
            {
                if (!int.TryParse(command[1], out var parsedPort) || parsedPort <= 0)
                    throw new ArgumentException("本地端口必须是大于 0 的整数。示例：myusbip client port 1");
                localPort = parsedPort;
            }

            Console.WriteLine(await windowsClientBackend.GetPortOutputAsync(localPort));
            break;
        }
        case "attach" when command.Length >= 3:
        {
            await sink.WriteAsync(new UsbIpEvent(DateTimeOffset.Now, "client.attach.request", "Information", null,
                command[2], command[1], "请求远程挂载设备", new Dictionary<string, object?>
                {
                    ["attachTimeoutSeconds"] = config.AttachTimeoutSeconds,
                    ["receiveMode"] = config.ReceiveMode,
                }));
            var result = await client.AttachAsync(command[1], command[2]);
            await sink.WriteAsync(new UsbIpEvent(DateTimeOffset.Now, "client.attach.result",
                result.Success ? "Information" : "Warning", null, command[2], command[1], result.Message,
                new Dictionary<string, object?> { ["localPort"] = result.Port, ["success"] = result.Success, ["receiveMode"] = config.ReceiveMode }));
            Console.WriteLine(result.Success
                ? $"已挂载 {command[2]}，本地端口: {result.Port?.ToString() ?? "由 VHCI 分配"}，receive-mode={config.ReceiveMode}"
                : result.Message);
            break;
        }
        case "detach" when command.Length >= 2 && int.TryParse(command[1], out var port):
            await sink.WriteAsync(new UsbIpEvent(DateTimeOffset.Now, "client.detach.request", "Information", null,
                null, null, $"请求卸载 VHCI 端口 {port}", new Dictionary<string, object?> { ["localPort"] = port }));
            await client.DetachAsync(port);
            await sink.WriteAsync(new UsbIpEvent(DateTimeOffset.Now, "client.detach.completed", "Information", null,
                null, null, $"已卸载 VHCI 端口 {port}", new Dictionary<string, object?> { ["localPort"] = port }));
            Console.WriteLine($"已卸载本地 VHCI 端口 {port}");
            break;
        case "diag":
            await PrintReportAsync(await diagnostics.CheckClientAsync());
            break;
        default:
            PrintHelp();
            break;
    }
}

static void PrintDevices(IReadOnlyList<UsbIpDeviceInfo> devices)
{
    Console.WriteLine($"{"BUSID",-20} {"VID:PID",-10} {"STATE",-10} {"SPEED",-10} {"CONNECTED BY",-20} PRODUCT");
    foreach (var d in devices)
    {
        Console.WriteLine($"{d.BusId,-20} {d.VidPid,-10} {d.State,-10} {FormatSpeed(d.Speed),-10} {(d.ClientAddress ?? "-"),-20} {d.Product}");
        Console.WriteLine($"  bus/dev={d.BusNumber}/{d.DeviceNumber} usb={FormatBcd(d.UsbVersion)} device={FormatBcd(d.DeviceVersion)} class={d.DeviceClass:X2}/{d.DeviceSubClass:X2}/{d.DeviceProtocol:X2} configs={d.ConfigurationCount} config={d.ConfigurationValue} interfaces={d.InterfaceCount}");
        for (var i = 0; i < d.Interfaces.Count; i++)
        {
            var iface = d.Interfaces[i];
            Console.WriteLine($"  interface[{i}]={iface.Class:X2}/{iface.SubClass:X2}/{iface.Protocol:X2}");
        }
        if (!string.IsNullOrWhiteSpace(d.InstanceId)) Console.WriteLine($"  instance={d.InstanceId}");
        if (!string.IsNullOrWhiteSpace(d.SerialNumber)) Console.WriteLine($"  serial={d.SerialNumber}");
        if (d.ConnectedAt is not null || !string.IsNullOrWhiteSpace(d.SessionId))
            Console.WriteLine($"  connectedAt={d.ConnectedAt:yyyy-MM-dd HH:mm:ss zzz} session={d.SessionId ?? "-"}");
    }
}

static string FormatSpeed(uint speed) => speed switch
{
    1 => "Low",
    2 => "Full",
    3 => "High",
    4 => "Wireless",
    5 => "Super",
    6 => "Super+",
    _ => "Unknown",
};

static string FormatBcd(ushort value)
{
    if (value == 0) return "-";
    return $"{(value >> 8):X}.{((value >> 4) & 0xF):X}{(value & 0xF):X}";
}

static Task PrintReportAsync(UsbIpHealthReport report)
{
    Console.WriteLine(report.Healthy ? "健康检查: PASS" : "健康检查: FAIL");
    foreach (var check in report.Checks)
    {
        Console.WriteLine($"[{(check.Success ? "OK" : "FAIL")}] {check.Name}: {check.Message}");
        if (!string.IsNullOrWhiteSpace(check.Suggestion)) Console.WriteLine($"      建议: {check.Suggestion}");
    }
    return Task.CompletedTask;
}

static void PrintHelp()
{
    Console.WriteLine("MyUsbIP CLI");
    Console.WriteLine("  myusbip server list");
    Console.WriteLine("  myusbip server share <busid>");
    Console.WriteLine("  myusbip server unshare <busid>");
    Console.WriteLine("  myusbip server watch");
    Console.WriteLine("  myusbip server diag");
    Console.WriteLine("  myusbip client list <host>");
    Console.WriteLine("  myusbip client port [local-port]");
    Console.WriteLine("  myusbip client attach <host> <busid>");
    Console.WriteLine("  myusbip client detach <local-port>");
    Console.WriteLine("  myusbip client diag");
    Console.WriteLine("  myusbip diag");
}

internal sealed record ClientCliConfig
{
    public string UsbipWinPath { get; init; } = "usbip.exe";
    public int CommandTimeoutSeconds { get; init; } = 15;
    public int AttachTimeoutSeconds { get; init; } = 120;
    public string ReceiveMode { get; init; } = "zero-copy";
    public ClientLoggingConfig Logging { get; init; } = new();
}

internal sealed record ClientLoggingConfig
{
    public bool Enabled { get; init; } = true;
    public string Directory { get; init; } = OperatingSystem.IsWindows()
        ? "%ProgramData%\\MyUsbIP\\ClientLogs"
        : "logs";
    public int RetentionDays { get; init; } = 30;
}
