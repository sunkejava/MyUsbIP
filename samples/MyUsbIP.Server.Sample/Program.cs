using MyUsbIP.Abstractions;
using MyUsbIP.Platform;
using MyUsbIP.Server;

var sink = new ConsoleEventSink();
IUsbIpServerBackend backend = OperatingSystem.IsWindows()
    ? new WindowsUsbIpBackend(eventSink: sink)
    : OperatingSystem.IsLinux()
        ? new LinuxUsbIpBackend(sink)
        : throw new PlatformNotSupportedException("示例当前支持 Windows/Linux。 ");

var server = new MyUsbIpServer(backend, sink);
var devices = await server.GetDevicesAsync();

Console.WriteLine("本机 USB 设备：");
foreach (var device in devices)
    Console.WriteLine($"{device.BusId,-10} {device.VidPid} {device.Product} [{device.State}]");

if (args.Length >= 2 && args[0].Equals("share", StringComparison.OrdinalIgnoreCase))
{
    await server.ShareAsync(args[1]);
    Console.WriteLine($"已共享设备 {args[1]}。 ");
}
else if (args.Length >= 2 && args[0].Equals("unshare", StringComparison.OrdinalIgnoreCase))
{
    await server.UnshareAsync(args[1]);
    Console.WriteLine($"已取消共享设备 {args[1]}。 ");
}
else
{
    Console.WriteLine("用法: MyUsbIP.Server.Sample share <busId> | unshare <busId>");
}

sealed class ConsoleEventSink : IUsbIpEventSink
{
    public ValueTask WriteAsync(UsbIpEvent evt, CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"[{evt.Timestamp:O}] [{evt.Level}] trace={evt.TraceId} event={evt.EventName} bus={evt.BusId} host={evt.RemoteHost} {evt.Message}");
        return ValueTask.CompletedTask;
    }
}
