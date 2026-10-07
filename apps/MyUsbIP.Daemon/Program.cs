using System.Net;
using System.Text.Json;
using MyUsbIP.Abstractions;
using MyUsbIP.Client;
using MyUsbIP.NativeServer;
using MyUsbIP.Platform;
using MyUsbIP.Runtime;
using MyUsbIP.Server;
using MyUsbIP.UsbDk;
using MyUsbIP.WindowsNative;

var configPath = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(AppContext.BaseDirectory, "appsettings.json");
if (!File.Exists(configPath))
{
    Console.Error.WriteLine($"配置文件不存在: {configPath}");
    return 2;
}

var config = JsonSerializer.Deserialize(await File.ReadAllTextAsync(configPath), DaemonJsonContext.Default.DaemonConfig) ?? new DaemonConfig();

var configuredDirectory = Environment.ExpandEnvironmentVariables(config.Logging.Directory);
var logDirectory = Path.IsPathRooted(configuredDirectory)
    ? configuredDirectory
    : Path.Combine(AppContext.BaseDirectory, configuredDirectory);
Directory.CreateDirectory(logDirectory);
LogFileMaintenance.Cleanup(logDirectory, "myusbip-server-*.jsonl", config.Logging.RetentionDays);

var logPath = Path.Combine(logDirectory, $"myusbip-server-{DateTime.Now:yyyyMMdd}.jsonl");
await using JsonLinesUsbIpEventSink? fileSink = config.Logging.Enabled ? new JsonLinesUsbIpEventSink(logPath) : null;
var memorySink = new MemoryUsbIpEventSink(2000);
IUsbIpEventSink sink = fileSink is null
    ? memorySink
    : new CompositeUsbIpEventSink(fileSink, memorySink);

IUsbIpServerBackend serverBackend;
IUsbIpClientBackend clientBackend;
UsbIpNativeServer? nativeServer = null;
WindowsNativeClientBackend? nativeClientBackend = null;
UsbDkDeviceManager? usbDkManager = null;

var nativeLogging = new UsbIpNativeServerLoggingOptions
{
    LogSuccessfulUrbs = config.Logging.LogSuccessfulUrbs,
    LogDeviceListRequests = config.Logging.LogDeviceListRequests,
};

if (string.Equals(config.BackendMode, "UsbDkUsbipWin", StringComparison.OrdinalIgnoreCase))
{
    if (!OperatingSystem.IsWindows())
    {
        Console.Error.WriteLine("BackendMode=UsbDkUsbipWin 仅支持 Windows。 ");
        return 3;
    }

    usbDkManager = new UsbDkDeviceManager(sink);
    serverBackend = new UsbDkServerBackend(usbDkManager);
    clientBackend = new UsbipWinVhciClientBackend(
        usbipPath: config.UsbipWinPath,
        eventSink: sink,
        commandTimeout: TimeSpan.FromSeconds(Math.Max(1, config.CommandTimeoutSeconds)));

    if (config.NativeServer.Enabled)
    {
        var address = string.IsNullOrWhiteSpace(config.NativeServer.ListenAddress)
            ? IPAddress.Any
            : IPAddress.Parse(config.NativeServer.ListenAddress);
        nativeServer = new UsbIpNativeServer(
            new UsbDkExportTransport(usbDkManager),
            address,
            config.NativeServer.Port,
            sink,
            nativeLogging);
    }
}
else if (string.Equals(config.BackendMode, "NativeWindows", StringComparison.OrdinalIgnoreCase))
{
    if (!OperatingSystem.IsWindows())
    {
        Console.Error.WriteLine("BackendMode=NativeWindows 仅支持 Windows。 ");
        return 3;
    }

    serverBackend = new WindowsNativeServerBackend();
    nativeClientBackend = new WindowsNativeClientBackend(sink);
    clientBackend = nativeClientBackend;

    if (config.NativeServer.Enabled)
    {
        var address = string.IsNullOrWhiteSpace(config.NativeServer.ListenAddress)
            ? IPAddress.Any
            : IPAddress.Parse(config.NativeServer.ListenAddress);
        nativeServer = new UsbIpNativeServer(
            new WindowsExporterTransport(),
            address,
            config.NativeServer.Port,
            sink,
            nativeLogging);
    }
}
else
{
    var backendObject = UsbIpBackendFactory.CreateDefault(sink, TimeSpan.FromSeconds(Math.Max(1, config.CommandTimeoutSeconds)));
    serverBackend = (IUsbIpServerBackend)backendObject;
    clientBackend = (IUsbIpClientBackend)backendObject;
}

var server = new MyUsbIpServer(serverBackend, sink);
var client = new MyUsbIpClient(clientBackend, sink);
var monitor = new UsbIpDeviceMonitor(server, sink);
var autoShare = new UsbIpAutoShareService(server, monitor, sink);
var connectionManager = new UsbIpConnectionManager(client, sink);
var diagnostics = new UsbIpDiagnosticsService(serverBackend, clientBackend, sink);

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdown.Cancel();
};

Console.WriteLine("MyUsbIP Daemon");
Console.WriteLine($"Backend: {config.BackendMode}");
Console.WriteLine($"配置: {configPath}");
Console.WriteLine($"日志: {(config.Logging.Enabled ? logPath : "已禁用")}");

await sink.WriteAsync(new UsbIpEvent(DateTimeOffset.Now, "daemon.started", "Information", null, null, null,
    "MyUsbIP Daemon 已启动", new Dictionary<string, object?>
    {
        ["backendMode"] = config.BackendMode,
        ["configPath"] = configPath,
        ["logPath"] = config.Logging.Enabled ? logPath : null,
        ["retentionDays"] = config.Logging.RetentionDays,
        ["logSuccessfulUrbs"] = config.Logging.LogSuccessfulUrbs,
    }));

try
{
    var serverHealth = await diagnostics.CheckServerAsync(shutdown.Token);
    var clientHealth = await diagnostics.CheckClientAsync(shutdown.Token);
    Console.WriteLine($"Server Health: {(serverHealth.Healthy ? "PASS" : "FAIL")}");
    Console.WriteLine($"Client Health: {(clientHealth.Healthy ? "PASS" : "FAIL")}");

    var tasks = new List<Task>();
    if (nativeServer is not null)
    {
        Console.WriteLine($"MyUsbIP USB/IP Server: {config.NativeServer.ListenAddress}:{config.NativeServer.Port}");
        tasks.Add(nativeServer.RunAsync(shutdown.Token));
    }

    var rules = config.AutoShareRules.Select(x => new UsbIpAutoShareRule
    {
        VendorId = ParseHex(x.VendorId),
        ProductId = ParseHex(x.ProductId),
        SerialNumber = x.SerialNumber,
        BusIdPrefix = x.BusIdPrefix,
        Enabled = x.Enabled,
    }).ToArray();

    if (rules.Length > 0)
    {
        tasks.Add(autoShare.RunAsync(rules, new UsbIpMonitorOptions
        {
            PollInterval = TimeSpan.FromSeconds(Math.Max(1, config.MonitorIntervalSeconds)),
            EmitInitialDevices = true,
        }, shutdown.Token));
    }

    foreach (var item in config.ManagedConnections.Where(x => x.Enabled))
    {
        try
        {
            await connectionManager.ConnectAsync(item.Host, item.BusId, item.Port, shutdown.Token);
        }
        catch (Exception ex)
        {
            await sink.WriteAsync(new UsbIpEvent(DateTimeOffset.Now, "managed.connection.initial.failed", "Warning", null,
                item.BusId, item.Host, ex.Message, Exception: ex));
            Console.Error.WriteLine($"初始连接失败 {item.Host}/{item.BusId}: {ex.Message}，后台恢复循环会继续尝试。 ");
        }
    }

    if (config.ManagedConnections.Any(x => x.Enabled))
    {
        tasks.Add(connectionManager.RunRecoveryLoopAsync(new UsbIpReconnectOptions
        {
            Enabled = config.Reconnect.Enabled,
            CheckInterval = TimeSpan.FromSeconds(Math.Max(1, config.Reconnect.CheckIntervalSeconds)),
            RetryDelay = TimeSpan.FromSeconds(Math.Max(1, config.Reconnect.RetryDelaySeconds)),
            MaxConsecutiveFailures = config.Reconnect.MaxConsecutiveFailures,
        }, shutdown.Token));
    }

    if (tasks.Count == 0)
    {
        Console.WriteLine("未启用 NativeServer、AutoShareRules 或 ManagedConnections，守护进程仅保持运行并提供日志。 ");
        try { await Task.Delay(Timeout.InfiniteTimeSpan, shutdown.Token); } catch (OperationCanceledException) { }
    }
    else
    {
        try { await Task.WhenAll(tasks); } catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
    }
}
finally
{
    if (nativeServer is not null) await nativeServer.DisposeAsync();
    if (nativeClientBackend is not null) await nativeClientBackend.DisposeAsync();
    usbDkManager?.Dispose();
    await sink.WriteAsync(new UsbIpEvent(DateTimeOffset.Now, "daemon.stopped", "Information", null, null, null,
        "MyUsbIP Daemon 已停止"));
}

Console.WriteLine("MyUsbIP Daemon 已停止。 ");
return 0;

static ushort? ParseHex(string? value)
{
    if (string.IsNullOrWhiteSpace(value)) return null;
    value = value.Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase).Replace(":", string.Empty);
    return Convert.ToUInt16(value, 16);
}

// 使用 set 让 JSON 源生成只设置实际存在的字段，保留旧配置缺省字段的初始化值。
internal sealed record DaemonConfig
{
    public string BackendMode { get; set; } = "UsbDkUsbipWin";
    public string UsbipWinPath { get; set; } = "usbip.exe";
    public int CommandTimeoutSeconds { get; set; } = 15;
    public int MonitorIntervalSeconds { get; set; } = 2;
    public DaemonLoggingConfig Logging { get; set; } = new();
    public NativeServerConfig NativeServer { get; set; } = new();
    public List<AutoShareRuleConfig> AutoShareRules { get; set; } = [];
    public List<ManagedConnectionConfig> ManagedConnections { get; set; } = [];
    public ReconnectConfig Reconnect { get; set; } = new();
}

internal sealed record DaemonLoggingConfig
{
    public bool Enabled { get; set; } = true;
    public string Directory { get; set; } = OperatingSystem.IsWindows()
        ? "%ProgramData%\\MyUsbIP\\ServerLogs"
        : "logs";
    public int RetentionDays { get; set; } = 30;
    public bool LogSuccessfulUrbs { get; set; }
    public bool LogDeviceListRequests { get; set; } = true;
}

internal sealed record NativeServerConfig
{
    public bool Enabled { get; set; } = true;
    public string ListenAddress { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 3240;
}

internal sealed record AutoShareRuleConfig
{
    public string? VendorId { get; set; }
    public string? ProductId { get; set; }
    public string? SerialNumber { get; set; }
    public string? BusIdPrefix { get; set; }
    public bool Enabled { get; set; } = true;
}

internal sealed record ManagedConnectionConfig
{
    public required string Host { get; set; }
    public required string BusId { get; set; }
    public int Port { get; set; } = 3240;
    public bool Enabled { get; set; } = true;
}

internal sealed record ReconnectConfig
{
    public bool Enabled { get; set; } = true;
    public int CheckIntervalSeconds { get; set; } = 5;
    public int RetryDelaySeconds { get; set; } = 3;
    public int MaxConsecutiveFailures { get; set; }
}
