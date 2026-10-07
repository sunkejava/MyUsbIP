using System.Text.Json;
using MyUsbIP.Platform;

internal static class ConfigurationSmokeChecks
{
    public static void Run(List<string> failures)
    {
        RunCase(nameof(ClientConfig_OmittedFields_PreserveDefaults), ClientConfig_OmittedFields_PreserveDefaults);
        RunCase(nameof(ClientConfig_LegacyPartialConfig_PreservesDefaultsAndOverrides), ClientConfig_LegacyPartialConfig_PreservesDefaultsAndOverrides);
        RunCase(nameof(ClientConfig_ExplicitValuesAndCaseInsensitiveNames_AreHonored), ClientConfig_ExplicitValuesAndCaseInsensitiveNames_AreHonored);
        RunCase(nameof(DaemonConfig_OmittedAndNestedFields_PreserveDefaults), DaemonConfig_OmittedAndNestedFields_PreserveDefaults);
        RunCase(nameof(DaemonConfig_PartialRulesAndConnections_PreserveDefaultsAndOverrides), DaemonConfig_PartialRulesAndConnections_PreserveDefaultsAndOverrides);
        RunCase(nameof(UsbipPath_NullOrBlank_UsesDefaultAndPreservesCustomPath), UsbipPath_NullOrBlank_UsesDefaultAndPreservesCustomPath);

        void RunCase(string name, Action action)
        {
            try { action(); Console.WriteLine($"[PASS] {name}"); }
            catch (Exception ex) { failures.Add($"[FAIL] {name}: {ex.Message}"); }
        }
    }

    private static void ClientConfig_OmittedFields_PreserveDefaults()
    {
        var config = ReadClient("{}");
        Check(config.UsbipWinPath == "usbip.exe", "缺省 UsbipWinPath 未保留 usbip.exe");
        Check(config.CommandTimeoutSeconds == 15 && config.AttachTimeoutSeconds == 120, "缺省命令/连接超时丢失");
        Check(config.ReceiveMode == "zero-copy" && config.UsbIdsPath == "", "缺省接收模式/USB 数据库路径丢失");
        Check(config.Logging is { Enabled: true, RetentionDays: 30 }, "缺省日志配置丢失");
        Check(config.Logging.Directory == new ClientLoggingConfig().Directory, "缺省日志目录丢失");
    }

    private static void ClientConfig_LegacyPartialConfig_PreservesDefaultsAndOverrides()
    {
        // 复现用户旧配置：仅含日志和命令超时，不能要求升级时重写配置。
        var config = ReadClient("""{"Logging":{"Directory":"custom-logs"},"CommandTimeoutSeconds":9}""");
        Check(config.UsbipWinPath == "usbip.exe" && config.ReceiveMode == "zero-copy" && config.AttachTimeoutSeconds == 120,
            "旧配置缺省字段覆盖了属性初始化值");
        Check(config.CommandTimeoutSeconds == 9 && config.Logging.Directory == "custom-logs", "用户配置未保留");
        Check(config.Logging.Enabled && config.Logging.RetentionDays == 30, "部分日志配置丢失默认值");

        var bundled = ReadClient(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "clientsettings.json")));
        Check(bundled.UsbipWinPath == "usbip.exe" && bundled.AttachTimeoutSeconds == 120 && bundled.ReceiveMode == "zero-copy",
            "实际发行配置缺少 UsbipWinPath 时无法保留默认路径");
    }

    private static void ClientConfig_ExplicitValuesAndCaseInsensitiveNames_AreHonored()
    {
        var config = ReadClient("""{"usbipwinpath":"D:\\USB tools\\usbip.exe","commandtimeoutseconds":7,"attachtimeoutseconds":240,"receivemode":"low-latency","usbIdspath":"custom.ids","logging":{"enabled":false,"directory":"custom","retentiondays":0}}""");
        Check(config.UsbipWinPath == @"D:\USB tools\usbip.exe" && config.ReceiveMode == "low-latency" && config.UsbIdsPath == "custom.ids",
            "大小写不敏感/自定义路径失效");
        Check(config.CommandTimeoutSeconds == 7 && config.AttachTimeoutSeconds == 240, "自定义超时丢失");
        Check(config.Logging is { Enabled: false, Directory: "custom", RetentionDays: 0 }, "显式 false/0 被误替换成默认值");
    }

    private static void DaemonConfig_OmittedAndNestedFields_PreserveDefaults()
    {
        foreach (var json in new[] { "{}", """{"Logging":{},"NativeServer":{},"Reconnect":{}}""" })
        {
            var config = ReadDaemon(json);
            Check(config.BackendMode == "UsbDkUsbipWin" && config.UsbipWinPath == "usbip.exe", "Daemon 默认后端/路径丢失");
            Check(config.CommandTimeoutSeconds == 15 && config.MonitorIntervalSeconds == 2, "Daemon 默认轮询/超时丢失");
            Check(config.Logging is { Enabled: true, RetentionDays: 30, LogDeviceListRequests: true }, "Daemon 默认日志配置丢失");
            Check(config.Logging.Directory == new DaemonLoggingConfig().Directory, "Daemon 默认日志目录丢失");
            Check(config.NativeServer is { Enabled: true, ListenAddress: "0.0.0.0", Port: 3240 }, "Daemon 默认监听配置丢失");
            Check(config.Reconnect is { Enabled: true, CheckIntervalSeconds: 5, RetryDelaySeconds: 3 }, "Daemon 默认重连配置丢失");
            Check(config.AutoShareRules.Count == 0 && config.ManagedConnections.Count == 0, "Daemon 默认集合丢失");
        }
    }

    private static void DaemonConfig_PartialRulesAndConnections_PreserveDefaultsAndOverrides()
    {
        var config = ReadDaemon("""{"backendmode":"NativeWindows","logging":{"enabled":false},"nativeserver":{"port":4321},"reconnect":{"retrydelayseconds":8},"autoshareRules":[{"VendorId":"1A86"},{"Enabled":false}],"managedConnections":[{"Host":"192.168.3.102","BusId":"2-1"},{"Host":"host2","BusId":"2-2","Port":4321,"Enabled":false}]}""");
        Check(config.BackendMode == "NativeWindows" && !config.Logging.Enabled && config.Logging.RetentionDays == 30,
            "Daemon 配置覆盖或嵌套默认值丢失");
        Check(config.NativeServer is { Port: 4321, Enabled: true, ListenAddress: "0.0.0.0" }, "部分监听配置丢失默认值");
        Check(config.Reconnect is { Enabled: true, CheckIntervalSeconds: 5, RetryDelaySeconds: 8 }, "部分重连配置丢失默认值");
        Check(config.AutoShareRules is [{ VendorId: "1A86", Enabled: true }, { Enabled: false }], "分享规则默认启用或显式禁用丢失");
        Check(config.ManagedConnections is [{ Host: "192.168.3.102", BusId: "2-1", Port: 3240, Enabled: true }, { Port: 4321, Enabled: false }],
            "连接默认端口/启用标志或显式配置丢失");
    }

    private static ClientCliConfig ReadClient(string json)
        => JsonSerializer.Deserialize(json, ClientCliJsonContext.Default.ClientCliConfig) ?? throw new InvalidOperationException("CLI 配置为空");

    private static void UsbipPath_NullOrBlank_UsesDefaultAndPreservesCustomPath()
    {
        var expected = UsbipWinVhciClientBackend.ResolveUsbipPath("usbip.exe");
        foreach (var json in new[] { """{"UsbipWinPath":null}""", """{"UsbipWinPath":""}""", """{"UsbipWinPath":"  "}""" })
        {
            var config = ReadClient(json);
            var resolved = UsbipWinVhciClientBackend.ResolveUsbipPath(config.UsbipWinPath);
            Check(!string.IsNullOrWhiteSpace(resolved) && resolved == expected && Path.GetFileName(resolved) == "usbip.exe",
                "显式空路径未使用默认 usbip.exe");
        }
        Check(UsbipWinVhciClientBackend.ResolveUsbipPath("custom-usbip.exe") == "custom-usbip.exe", "自定义路径被覆盖");
    }

    private static DaemonConfig ReadDaemon(string json)
        => JsonSerializer.Deserialize(json, DaemonJsonContext.Default.DaemonConfig) ?? throw new InvalidOperationException("Daemon 配置为空");

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
