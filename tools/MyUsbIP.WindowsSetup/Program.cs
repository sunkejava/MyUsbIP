using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Win32;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("MyUsbIP Windows 安装器仅支持 Windows。");
    return 10;
}

Console.OutputEncoding = System.Text.Encoding.UTF8;
var exeName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? string.Empty);
var role = EmbeddedPayload.DetectEmbeddedRole()
    ?? (exeName.Contains("Server", StringComparison.OrdinalIgnoreCase) ? "server"
        : exeName.Contains("Client", StringComparison.OrdinalIgnoreCase) ? "client"
        : string.Empty);

if (string.IsNullOrEmpty(role))
{
    Console.WriteLine("请选择安装类型：1=服务端  2=客户端");
    role = Console.ReadKey(true).KeyChar == '1' ? "server" : "client";
}

var baseDir = EmbeddedPayload.PrepareWorkingDirectory(AppContext.BaseDirectory, role);
var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MyUsbIP", "InstallerLogs");
Directory.CreateDirectory(logDir);
var logPath = Path.Combine(logDir, $"setup-{role}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
using var log = new StreamWriter(logPath, append: false, new System.Text.UTF8Encoding(false)) { AutoFlush = true };

void Write(string text)
{
    var line = $"[{DateTime.Now:HH:mm:ss}] {text}";
    Console.WriteLine(line);
    log.WriteLine(line);
}

try
{
    Write($"MyUsbIP {role} 安装开始");
    Write($"安装工作目录: {baseDir}");

    var manifestPath = Path.Combine(baseDir, "config", "dependencies.windows.json");
    if (!File.Exists(manifestPath)) throw new InvalidOperationException($"缺少依赖清单: {manifestPath}");
    var manifest = JsonSerializer.Deserialize<DependencyManifest>(File.ReadAllText(manifestPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidOperationException("依赖清单解析失败。");

    var package = manifest.Packages.FirstOrDefault(x => x.Enabled && string.Equals(x.Role, role, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"依赖清单中未找到 {role} 依赖。");
    var dependencyPath = Path.Combine(baseDir, "dependencies", "cache", package.FileName);
    if (!File.Exists(dependencyPath)) throw new FileNotFoundException("安装包缺少离线驱动依赖。", dependencyPath);

    using (var stream = File.OpenRead(dependencyPath))
    {
        var actualHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(package.Sha256) || !actualHash.Equals(package.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"驱动依赖 SHA256 校验失败。期望={package.Sha256} 实际={actualHash}");
    }
    Write($"依赖校验通过: {package.FileName}");

    StopExistingRuntime(role, Write);

    if (role == "server") InstallServer(baseDir, dependencyPath, package, Write);
    else InstallClient(baseDir, dependencyPath, package, Write);

    Write("安装与内置自检全部完成。");
    Write($"安装日志: {logPath}");
    Console.WriteLine("\n安装成功。按任意键关闭窗口。");
    Console.ReadKey(true);
    return 0;
}
catch (Exception ex)
{
    Write($"安装失败: {ex}");
    Console.WriteLine($"\n安装失败，日志已保存：{logPath}\n按任意键关闭窗口。");
    Console.ReadKey(true);
    return 1;
}

static void StopExistingRuntime(string role, Action<string> write)
{
    write("检查并停止已运行的旧版本...");

    if (string.Equals(role, "server", StringComparison.OrdinalIgnoreCase))
    {
        var task = RunCaptureArgs("schtasks.exe", "/Query", "/TN", "MyUsbIP USB-IP Server");
        if (task.ExitCode == 0)
        {
            write("检测到 MyUsbIP 服务端计划任务，正在停止...");
            RunArgs("schtasks.exe", "/End", "/TN", "MyUsbIP USB-IP Server");
        }

        foreach (var serviceName in new[] { "MyUsbIP", "MyUsbIP.Server", "MyUsbIP USB-IP Server" })
        {
            var service = RunCaptureArgs("sc.exe", "query", serviceName);
            if (service.ExitCode != 0) continue;
            write($"检测到旧版 Windows 服务 {serviceName}，正在停止...");
            RunArgs("sc.exe", "stop", serviceName);
            WaitServiceStopped(serviceName, TimeSpan.FromSeconds(15));
        }
        KillProcesses(write, "myusbipd");
    }
    else
    {
        foreach (var serviceName in new[] { "MyUsbIP.Client", "MyUsbIP Client" })
        {
            var service = RunCaptureArgs("sc.exe", "query", serviceName);
            if (service.ExitCode != 0) continue;
            write($"检测到客户端 Windows 服务 {serviceName}，正在停止...");
            RunArgs("sc.exe", "stop", serviceName);
            WaitServiceStopped(serviceName, TimeSpan.FromSeconds(15));
        }

        // 旧 cezanne/usbip-win attach 会派生长期运行的 attacher.exe；升级前必须一起结束。
        KillProcesses(write, "myusbip", "usbip", "attacher", "wusbip");
    }

    Thread.Sleep(800);
    write("旧版本运行实例已停止，可以执行覆盖升级。");
}

static void KillProcesses(Action<string> write, params string[] processNames)
{
    foreach (var name in processNames)
    {
        Process[] processes;
        try { processes = Process.GetProcessesByName(name); }
        catch { continue; }

        foreach (var process in processes)
        {
            using (process)
            {
                try
                {
                    write($"停止进程 {process.ProcessName}.exe (PID={process.Id})...");
                    process.Kill(entireProcessTree: true);
                    if (!process.WaitForExit(10000))
                        throw new InvalidOperationException($"进程 {process.ProcessName}.exe PID={process.Id} 在 10 秒内未退出。");
                }
                catch (ArgumentException) { }
            }
        }
    }
}

static void WaitServiceStopped(string serviceName, TimeSpan timeout)
{
    var sw = Stopwatch.StartNew();
    while (sw.Elapsed < timeout)
    {
        var state = RunCaptureArgs("sc.exe", "query", serviceName);
        if (state.ExitCode != 0 || state.Output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase)) return;
        Thread.Sleep(500);
    }
}

static void InstallServer(string baseDir, string dependencyPath, DependencyPackage package, Action<string> write)
{
    var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    var installDir = Path.Combine(programFiles, "MyUsbIP", "Server");
    var payload = Path.Combine(baseDir, "payload", "server");
    if (!Directory.Exists(payload)) throw new DirectoryNotFoundException($"缺少服务端程序目录: {payload}");

    var helper = Path.Combine(programFiles, "UsbDk Runtime Library", "UsbDkHelper.dll");
    var existingService = RunCaptureArgs("sc.exe", "query", "UsbDk");
    var helperVersion = File.Exists(helper) ? FileVersionInfo.GetVersionInfo(helper).FileVersion : null;
    var usbDkHealthy = File.Exists(helper) && existingService.ExitCode == 0 && VersionMatches(helperVersion, package.Version);
    var rebootRequired = false;

    if (usbDkHealthy)
    {
        write($"检测到 UsbDk {helperVersion ?? package.Version} 且驱动服务存在，跳过重复 MSI 安装，避免覆盖升级扰动 USB/CH340 驱动栈。");
    }
    else
    {
        write($"安装/修复 UsbDk {package.Version}...");
        var msiLog = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MyUsbIP", "InstallerLogs", "usbdk-msi.log");
        var code = RunArgsWithTimeout("msiexec.exe", TimeSpan.FromMinutes(5),
            "/i", dependencyPath, "/qn", "/norestart", "/l*v", msiLog);
        if (code is not (0 or 3010))
            throw new InvalidOperationException($"UsbDk 安装失败，ExitCode={code}，日志={msiLog}");
        rebootRequired = code == 3010;
        if (rebootRequired)
            write("UsbDk 安装成功，但 Windows 返回 3010：需要重启后驱动栈才能完全生效。");
    }

    write("部署服务端程序...");
    CopyDirectory(payload, installDir, new[] { "appsettings.json" });
    Directory.CreateDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MyUsbIP", "ServerLogs"));
    if (!File.Exists(helper)) throw new FileNotFoundException("UsbDk 已安装但未找到 UsbDkHelper.dll。", helper);
    File.Copy(helper, Path.Combine(installDir, "UsbDkHelper.dll"), true);

    var daemon = Path.Combine(installDir, "myusbipd.exe");
    var config = Path.Combine(installDir, "appsettings.json");
    if (!File.Exists(daemon) || !File.Exists(config)) throw new InvalidOperationException("服务端程序发布文件不完整。");

    write("配置 Windows 防火墙...");
    RunArgs("netsh.exe", "advfirewall", "firewall", "delete", "rule", "name=MyUsbIP USB-IP Server");
    EnsureSuccess(RunArgs("netsh.exe", "advfirewall", "firewall", "add", "rule", "name=MyUsbIP USB-IP Server", "dir=in", "action=allow", "protocol=TCP", "localport=3240", "profile=any"), "创建防火墙规则");

    write("配置 SYSTEM 开机自动启动任务...");
    RunArgs("schtasks.exe", "/Delete", "/TN", "MyUsbIP USB-IP Server", "/F");
    var taskCommand = $"\"{daemon}\" \"{config}\"";
    EnsureSuccess(RunArgs("schtasks.exe", "/Create", "/TN", "MyUsbIP USB-IP Server", "/SC", "ONSTART", "/RU", "SYSTEM", "/RL", "HIGHEST", "/TR", taskCommand, "/F"), "创建开机任务");

    if (rebootRequired)
    {
        write("因 UsbDk 要求重启，本次不强行启动 MyUsbIP 服务端；重启后计划任务会自动启动，避免在半更新驱动栈上执行 USB Redirect。");
        return;
    }

    write("启动更新后的 MyUsbIP 服务端...");
    EnsureSuccess(RunArgs("schtasks.exe", "/Run", "/TN", "MyUsbIP USB-IP Server"), "启动服务端任务");

    write("执行服务端自检...");
    var usbdk = RunCaptureArgs("sc.exe", "query", "UsbDk");
    if (usbdk.ExitCode != 0) throw new InvalidOperationException("未检测到 UsbDk 驱动服务。\n" + usbdk.Output);
    if (!WaitPort(3240, TimeSpan.FromSeconds(20))) throw new InvalidOperationException("MyUsbIP 已启动但 TCP 3240 未监听。请查看 ProgramData\\MyUsbIP\\InstallerLogs 与 ServerLogs。 ");
    write("服务端自检通过：UsbDk 正常，TCP 3240 正常监听。");
}

static void InstallClient(string baseDir, string dependencyPath, DependencyPackage package, Action<string> write)
{
    var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    var installDir = Path.Combine(programFiles, "MyUsbIP", "Client");
    var payload = Path.Combine(baseDir, "payload", "client");
    if (!Directory.Exists(payload)) throw new DirectoryNotFoundException($"缺少客户端程序目录: {payload}");

    write("部署 MyUsbIP 客户端程序...");
    CopyDirectory(payload, installDir, new[] { "clientsettings.json" });
    Directory.CreateDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MyUsbIP", "ClientLogs"));

    // 从旧 cezanne/usbip-win 迁移到 usbip-win2。两个 VHCI 不应并存，否则 PATH/驱动状态容易混淆。
    var legacyDir = Path.Combine(installDir, "usbip-win");
    var legacyUsbip = Directory.Exists(legacyDir)
        ? Directory.EnumerateFiles(legacyDir, "usbip.exe", SearchOption.AllDirectories).FirstOrDefault()
        : null;
    if (!string.IsNullOrWhiteSpace(legacyUsbip) && File.Exists(legacyUsbip))
    {
        write("检测到旧 usbip-win VHCI，执行卸载迁移...");
        var uninstall = RunCaptureArgs(legacyUsbip, "uninstall", "-f");
        write($"旧 VHCI 卸载 ExitCode={uninstall.ExitCode} {uninstall.Output.Trim()}");
    }
    if (Directory.Exists(legacyDir))
    {
        try { Directory.Delete(legacyDir, true); }
        catch (Exception ex) { write($"清理旧 usbip-win 目录失败，将继续安装 usbip-win2：{ex.Message}"); }
    }

    var usbipDir = Path.Combine(programFiles, "USBip");
    var usbip = Path.Combine(usbipDir, "usbip.exe");
    var needInstall = true;
    if (File.Exists(usbip))
    {
        var version = RunCaptureArgs(usbip, "-V");
        var port = RunCaptureArgs(usbip, "port");
        if (version.ExitCode == 0 && port.ExitCode == 0 &&
            version.Output.Contains(package.Version, StringComparison.OrdinalIgnoreCase))
        {
            needInstall = false;
            write($"检测到 usbip-win2 {package.Version} 且 UDE/VHCI 正常，跳过重复驱动安装。");
        }
    }

    var rebootRequired = false;
    if (needInstall)
    {
        write($"安装/升级 usbip-win2 {package.Version}...");
        var code = RunArgsWithTimeout(dependencyPath, TimeSpan.FromMinutes(5),
            "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-", "/TYPE=compact", "/CLOSEAPPLICATIONS");
        if (code is not (0 or 3010))
            throw new InvalidOperationException($"usbip-win2 安装失败，ExitCode={code}");
        rebootRequired = code == 3010;
        if (rebootRequired)
            write("usbip-win2 安装成功，但 Windows 返回 3010：需要重启后 UDE/VHCI 才能完全生效。");
    }

    if (!File.Exists(usbip))
        throw new FileNotFoundException("usbip-win2 安装完成但未找到 usbip.exe。", usbip);

    write("配置 usbip-win2 系统 PATH...");
    UpdateMachinePath(usbipDir, legacyDir);

    if (rebootRequired)
    {
        write("因 usbip-win2 要求重启，本次跳过 UDE/VHCI 运行态自检；重启后再执行 myusbip client port 验证。");
        return;
    }

    write("执行客户端自检...");
    var versionResult = RunCaptureArgs(usbip, "-V");
    if (versionResult.ExitCode != 0)
        throw new InvalidOperationException($"usbip.exe -V 执行失败。\n{versionResult.Output}");
    var portResult = RunCaptureArgs(usbip, "port");
    if (portResult.ExitCode != 0)
        throw new InvalidOperationException($"usbip.exe port 执行失败，usbip-win2 UDE/VHCI 可能未正常安装。\n{portResult.Output}");
    write($"客户端自检通过：{versionResult.Output.Trim()}，UDE/VHCI 已响应。");
}

static void UpdateMachinePath(string addDirectory, string legacyDirectory)
{
    using var envKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment", writable: true)
        ?? throw new InvalidOperationException("无法打开系统环境变量注册表项。");
    var current = Convert.ToString(envKey.GetValue("Path", string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames)) ?? string.Empty;
    var entries = current.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(x => !string.Equals(x.TrimEnd('\\'), legacyDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        .Where(x => !x.Contains(Path.Combine("MyUsbIP", "Client", "usbip-win"), StringComparison.OrdinalIgnoreCase))
        .ToList();
    if (!entries.Any(x => string.Equals(x.TrimEnd('\\'), addDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
        entries.Add(addDirectory);
    envKey.SetValue("Path", string.Join(';', entries), RegistryValueKind.ExpandString);
}

static void CopyDirectory(string source, string destination, string[]? preserveExistingRelativeFiles = null)
{
    Directory.CreateDirectory(destination);
    foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
    {
        var relative = Path.GetRelativePath(source, file);
        var target = Path.Combine(destination, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (preserveExistingRelativeFiles?.Contains(relative, StringComparer.OrdinalIgnoreCase) == true && File.Exists(target)) continue;
        File.Copy(file, target, true);
    }
}

static ProcessStartInfo CreateStart(string fileName, bool capture, params string[] args)
{
    var start = new ProcessStartInfo(fileName)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = capture,
        RedirectStandardError = capture,
    };
    if (Path.IsPathRooted(fileName) && File.Exists(fileName))
        start.WorkingDirectory = Path.GetDirectoryName(fileName)!;
    foreach (var arg in args) start.ArgumentList.Add(arg);
    return start;
}

static int RunArgs(string fileName, params string[] args)
    => RunArgsWithTimeout(fileName, TimeSpan.FromMinutes(2), args);

static int RunArgsWithTimeout(string fileName, TimeSpan timeout, params string[] args)
{
    using var process = Process.Start(CreateStart(fileName, false, args));
    if (process is null) return -1;
    if (process.WaitForExit(checked((int)Math.Min(int.MaxValue, timeout.TotalMilliseconds))))
        return process.ExitCode;

    try { process.Kill(entireProcessTree: true); } catch { }
    try { process.WaitForExit(5000); } catch { }
    return -2;
}

static (int ExitCode, string Output) RunCaptureArgs(string fileName, params string[] args)
{
    using var process = Process.Start(CreateStart(fileName, true, args));
    if (process is null) return (-1, "进程启动失败");

    var stdoutTask = process.StandardOutput.ReadToEndAsync();
    var stderrTask = process.StandardError.ReadToEndAsync();
    if (!process.WaitForExit(120000))
    {
        try { process.Kill(entireProcessTree: true); } catch { }
        try { process.WaitForExit(5000); } catch { }
        return (-2, $"进程执行超过 120 秒已终止: {fileName}");
    }

    Task.WaitAll(stdoutTask, stderrTask);
    return (process.ExitCode, stdoutTask.Result + Environment.NewLine + stderrTask.Result);
}

static bool VersionMatches(string? installed, string required)
{
    if (string.IsNullOrWhiteSpace(installed) || string.IsNullOrWhiteSpace(required)) return false;
    if (!Version.TryParse(installed.Split(' ', '-', '+')[0], out var installedVersion)) return false;
    if (!Version.TryParse(required.Split(' ', '-', '+')[0], out var requiredVersion)) return false;
    return installedVersion.Major == requiredVersion.Major
           && installedVersion.Minor == requiredVersion.Minor
           && installedVersion.Build == requiredVersion.Build;
}

static void EnsureSuccess(int exitCode, string operation)
{
    if (exitCode != 0) throw new InvalidOperationException($"{operation}失败，ExitCode={exitCode}");
}

static bool WaitPort(int port, TimeSpan timeout)
{
    var sw = Stopwatch.StartNew();
    while (sw.Elapsed < timeout)
    {
        try
        {
            using var client = new TcpClient();
            client.Connect("127.0.0.1", port);
            if (client.Connected) return true;
        }
        catch { }
        Thread.Sleep(500);
    }
    return false;
}

internal sealed class DependencyManifest
{
    public List<DependencyPackage> Packages { get; set; } = [];
}

internal sealed class DependencyPackage
{
    public string Role { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public string Version { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public string InstallType { get; set; } = string.Empty;
}
