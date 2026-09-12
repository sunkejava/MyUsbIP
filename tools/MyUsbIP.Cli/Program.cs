using MyUsbIP.Abstractions;
using MyUsbIP.Client;
using MyUsbIP.Platform;
using MyUsbIP.Runtime;
using MyUsbIP.Server;

var logPath = Path.Combine(AppContext.BaseDirectory, "logs", $"myusbip-{DateTime.Now:yyyyMMdd}.jsonl");
await using var fileSink = new JsonLinesUsbIpEventSink(logPath);
var memorySink = new MemoryUsbIpEventSink(500);
var sink = new CompositeUsbIpEventSink(fileSink, memorySink);

var backendObject = UsbIpBackendFactory.CreateDefault(sink, TimeSpan.FromSeconds(15));
var serverBackend = (IUsbIpServerBackend)backendObject;
var clientBackend = (IUsbIpClientBackend)backendObject;
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
    Console.Error.WriteLine($"[失败] {ex.Message}");
    Console.Error.WriteLine($"诊断日志: {logPath}");
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
                Console.WriteLine($"{change.OccurredAt:HH:mm:ss} {change.Kind,-7} {change.Device.BusId,-8} {change.Device.VidPid} {change.Device.Product}");
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
        case "attach" when command.Length >= 3:
            var result = await client.AttachAsync(command[1], command[2]);
            Console.WriteLine(result.Success ? $"已挂载 {command[2]}，本地端口: {result.Port?.ToString() ?? "由 VHCI 分配"}" : result.Message);
            break;
        case "detach" when command.Length >= 2 && int.TryParse(command[1], out var port):
            await client.DetachAsync(port);
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
    Console.WriteLine($"{ "BUSID",-10} {"VID:PID",-10} {"STATE",-10} PRODUCT");
    foreach (var d in devices)
        Console.WriteLine($"{d.BusId,-10} {d.VidPid,-10} {d.State,-10} {d.Product}");
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
    Console.WriteLine("MyUsbIP v1.0 CLI");
    Console.WriteLine("  myusbip server list");
    Console.WriteLine("  myusbip server share <busid>");
    Console.WriteLine("  myusbip server unshare <busid>");
    Console.WriteLine("  myusbip server watch");
    Console.WriteLine("  myusbip server diag");
    Console.WriteLine("  myusbip client list <host>");
    Console.WriteLine("  myusbip client attach <host> <busid>");
    Console.WriteLine("  myusbip client detach <local-port>");
    Console.WriteLine("  myusbip client diag");
    Console.WriteLine("  myusbip diag");
}
