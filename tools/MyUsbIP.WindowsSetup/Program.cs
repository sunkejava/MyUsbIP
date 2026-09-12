using System.Diagnostics;
using System.IO.Compression;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Win32;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("MyUsbIP Windows 安装器仅支持 Windows。");
    return 10;
}

Console.OutputEncoding = System.Text.Encoding.UTF8;
var baseDir = AppContext.BaseDirectory;
var exeName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? string.Empty);
var role = exeName.Contains("Server", StringComparison.OrdinalIgnoreCase) ? "server"
    : exeName.Contains("Client", StringComparison.OrdinalIgnoreCase) ? "client"
    : string.Empty;

if (string.IsNullOrEmpty(role))
{
    Console.WriteLine("请选择安装类型：1=服务端  2=客户端");
    role = Console.ReadKey(true).KeyChar == '1' ? "server" : "client";
}

var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MyUsbIP", "InstallerLogs");
Directory.CreateDirectory(logDir);
var logPath = Path.Combine(logDir, $"setup-{role}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
using var log = new StreamWriter(logPath, append: false) { AutoFlush = true };

void Write(string text)
{
    var line = $"[{DateTime.Now:HH:mm:ss}] {text}";
    Console.WriteLine(line);
    log.WriteLine(line);
}

try
{
    Write($"MyUsbIP {role} 安装开始");
    Write($"安装包目录: {baseDir}");

    var manifestPath = Path.Combine(baseDir, "config", "dependencies.windows.json");
    if (!File.Exists(manifestPath)) throw new InvalidOperationException($"缺少依赖清单: {manifestPath}");
    var manifest = JsonSerializer.Deserialize<DependencyManifest>(File.ReadAllText(manifestPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidOperationException("依赖清单解析失败。");

    var package = manifest.Packages.FirstOrDefault(x => x.Enabled && string.Equals(x.Role, role, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"依赖清单中未找到 {role} 依赖。");
    var dependencyPath = Path.Combine(baseDir, "dependencies", "cache", package.FileName);
    if (!File.Exists(dependencyPath)) throw new FileNotFoundException("安装包缺少离线驱动依赖。", dependencyPath);

    var actualHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(dependencyPath))).ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(package.Sha256) || !actualHash.Equals(package.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException($"驱动依赖 SHA256 校验失败。期望={package.Sha256} 实际={actualHash}");
    Write($"依赖校验通过: {package.FileName}");

    if (role == "server")
        InstallServer(baseDir, dependencyPath, Write);
    else
        InstallClient(baseDir, dependencyPath, Write);

    Write("安装与内置自检全部完成。");
    Write($"安装日志: {logPath}");
    Console.WriteLine();
    Console.WriteLine("安装成功。按任意键关闭窗口。");
    Console.ReadKey(true);
    return 0;
}
catch (Exception ex)
{
    Write($"安装失败: {ex}");
    Console.WriteLine();
    Console.WriteLine($"安装失败，日志已保存：{logPath}");
    Console.WriteLine("按任意键关闭窗口。");
    Console.ReadKey(true);
    return 1;
}

static void InstallServer(string baseDir, string dependencyPath, Action<string> write)
{
    var installDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MyUsbIP", "Server");
    var payload = Path.Combine(baseDir, "payload", "server");
    if (!Directory.Exists(payload)) throw new DirectoryNotFoundException($"缺少服务端程序目录: {payload}");

    write("安装 UsbDk...");
    var msiLog = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MyUsbIP", "InstallerLogs", "usbdk-msi.log");
    var code = Run("msiexec.exe", $"/i \"{dependencyPath}\" /qn /norestart /l*v \"{msiLog}\"");
    if (code is not (0 or 3010)) throw new InvalidOperationException($"UsbDk 安装失败，ExitCode={code}，日志={msiLog}");

    write("部署服务端程序...");
    CopyDirectory(payload, installDir);
    var helper = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "UsbDk Runtime Library", "UsbDkHelper.dll");
    if (!File.Exists(helper)) throw new FileNotFoundException("UsbDk 已安装但未找到 UsbDkHelper.dll。", helper);
    File.Copy(helper, Path.Combine(installDir, "UsbDkHelper.dll"), true);

    var daemon = Path.Combine(installDir, "myusbipd.exe");
    var config = Path.Combine(installDir, "appsettings.json");
    if (!File.Exists(daemon) || !File.Exists(config)) throw new InvalidOperationException("服务端程序发布文件不完整。");

    write("配置 Windows 防火墙...");
    Run("netsh.exe", "advfirewall firewall delete rule name=\"MyUsbIP USB-IP Server\"");
    EnsureSuccess(Run("netsh.exe", "advfirewall firewall add rule name=\"MyUsbIP USB-IP Server\" dir=in action=allow protocol=TCP localport=3240 profile=any"), "创建防火墙规则");

    write("配置开机自动启动任务...");
    Run("schtasks.exe", "/Delete /TN \"MyUsbIP USB-IP Server\" /F");
    var taskCommand = $"\"{daemon}\" \"{config}\"";
    EnsureSuccess(Run("schtasks.exe", $"/Create /TN \"MyUsbIP USB-IP Server\" /SC ONSTART /RU SYSTEM /RL HIGHEST /TR \"{taskCommand.Replace("\"", "\\\"")}\" /F"), "创建开机任务");

    write("启动 MyUsbIP 服务端...");
    Run("taskkill.exe", "/F /IM myusbipd.exe");
    EnsureSuccess(Run("schtasks.exe", "/Run /TN \"MyUsbIP USB-IP Server\""), "启动服务端任务");

    write("执行服务端自检...");
    var usbdk = RunCapture("sc.exe", "query UsbDk");
    if (usbdk.ExitCode != 0) throw new InvalidOperationException("未检测到 UsbDk 驱动服务。\n" + usbdk.Output);
    if (!WaitPort(3240, TimeSpan.FromSeconds(15))) throw new InvalidOperationException("MyUsbIP 已启动但 TCP 3240 未监听，请检查安装日志和 ProgramData\\MyUsbIP 下日志。");
    write("服务端自检通过：UsbDk 正常，TCP 3240 正常监听。");
}

static void InstallClient(string baseDir, string dependencyPath, Action<string> write)
{
    var installDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MyUsbIP", "Client");
    var payload = Path.Combine(baseDir, "payload", "client");
    if (!Directory.Exists(payload)) throw new DirectoryNotFoundException($"缺少客户端程序目录: {payload}");

    write("部署客户端程序...");
    CopyDirectory(payload, installDir);

    var usbipDir = Path.Combine(installDir, "usbip-win");
    if (Directory.Exists(usbipDir)) Directory.Delete(usbipDir, true);
    ZipFile.ExtractToDirectory(dependencyPath, usbipDir, overwriteFiles: true);
    var usbip = Directory.EnumerateFiles(usbipDir, "usbip.exe", SearchOption.AllDirectories).FirstOrDefault()
        ?? throw new FileNotFoundException("usbip-win 压缩包中未找到 usbip.exe。");

    write("安装 usbip-win VHCI(UDE)...");
    var installResult = RunCapture(usbip, "install -u");
    if (installResult.ExitCode != 0)
    {
        write("UDE 安装返回失败，尝试 usbip.exe install 自动模式...");
        installResult = RunCapture(usbip, "install");
    }
    if (installResult.ExitCode != 0) throw new InvalidOperationException($"VHCI 安装失败，ExitCode={installResult.ExitCode}\n{installResult.Output}");

    write("配置 usbip.exe 系统 PATH...");
    var binDir = Path.GetDirectoryName(usbip)!;
    using var envKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment", writable: true)
        ?? throw new InvalidOperationException("无法打开系统环境变量注册表项。");
    var path = Convert.ToString(envKey.GetValue("Path", string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames)) ?? string.Empty;
    if (!path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(binDir, StringComparer.OrdinalIgnoreCase))
        envKey.SetValue("Path", path.TrimEnd(';') + ";" + binDir, RegistryValueKind.ExpandString);

    write("执行客户端自检...");
    var portResult = RunCapture(usbip, "port");
    if (portResult.ExitCode != 0) throw new InvalidOperationException($"usbip.exe port 执行失败，VHCI 可能未正常安装。\n{portResult.Output}");
    write("客户端自检通过：usbip.exe 可执行，VHCI 已响应。");
}

static void CopyDirectory(string source, string destination)
{
    Directory.CreateDirectory(destination);
    foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        Directory.CreateDirectory(dir.Replace(source, destination, StringComparison.OrdinalIgnoreCase));
    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
    {
        var target = file.Replace(source, destination, StringComparison.OrdinalIgnoreCase);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(file, target, true);
    }
}

static int Run(string fileName, string arguments)
{
    using var process = Process.Start(new ProcessStartInfo(fileName, arguments) { UseShellExecute = false, CreateNoWindow = true });
    if (process is null) return -1;
    process.WaitForExit();
    return process.ExitCode;
}

static (int ExitCode, string Output) RunCapture(string fileName, string arguments)
{
    using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    });
    if (process is null) return (-1, "进程启动失败");
    var output = process.StandardOutput.ReadToEnd() + Environment.NewLine + process.StandardError.ReadToEnd();
    process.WaitForExit();
    return (process.ExitCode, output);
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
            if (client.ConnectAsync("127.0.0.1", port).Wait(TimeSpan.FromMilliseconds(500))) return true;
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
    public string FileName { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
}
