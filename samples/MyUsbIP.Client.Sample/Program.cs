using MyUsbIP.Abstractions;
using MyUsbIP.Client;
using MyUsbIP.Platform;

if (args.Length < 1)
{
    Console.WriteLine("用法: MyUsbIP.Client.Sample <server-host> [busId]");
    return;
}

var host = args[0];
var sink = new ConsoleEventSink();
IUsbIpClientBackend backend = OperatingSystem.IsWindows()
    ? new WindowsUsbIpBackend(eventSink: sink)
    : OperatingSystem.IsLinux()
        ? new LinuxUsbIpBackend(sink)
        : throw new PlatformNotSupportedException("示例当前支持 Windows/Linux。 ");

var client = new MyUsbIpClient(backend, sink);
var devices = await client.GetRemoteDevicesAsync(host);
Console.WriteLine($"{host} 可共享设备：");
foreach (var device in devices)
    Console.WriteLine($"{device.BusId,-10} {device.VidPid} {device.Product}");

if (args.Length >= 2)
{
    var result = await client.AttachAsync(host, args[1]);
    Console.WriteLine(result.Success ? $"挂载成功: {result.BusId}" : $"挂载失败: {result.Message}");
}

sealed class ConsoleEventSink : IUsbIpEventSink
{
    public ValueTask WriteAsync(UsbIpEvent evt, CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"[{evt.Timestamp:O}] [{evt.Level}] trace={evt.TraceId} event={evt.EventName} bus={evt.BusId} host={evt.RemoteHost} {evt.Message}");
        return ValueTask.CompletedTask;
    }
}
