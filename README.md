# MyUsbIP

基于 **.NET 10 + 标准 USB/IP** 的跨平台 USB 网络共享封装，目标是为 Windows/Linux 提供一套可维护、可诊断、可自动恢复的 VirtualHere 替代方案。

> 当前版本：**v1.0.0**

## 1. v1.0 能做什么

- Windows / Linux 服务端统一 API。
- Windows / Linux 客户端统一 API。
- 本机 USB 设备枚举。
- Share / Unshare。
- 远端 USB/IP 设备枚举。
- Attach / Detach。
- 跨平台设备热插拔监控。
- VID/PID/序列号/BusId 前缀自动共享。
- 客户端受管连接与掉线恢复。
- 操作超时与卡死进程树终止。
- Activity / TraceId 全链路诊断。
- `System.Diagnostics.Metrics` 指标。
- JSON Lines 文件日志。
- 内存事件缓存，方便后续管理页面实时展示。
- `myusbip` 跨平台 CLI。
- `myusbipd` 长期运行守护进程。
- Windows/Linux GitHub Actions 编译和发布产物。

## 2. 重要边界

MyUsbIP 的托管层全部使用 .NET 10，但 **Windows 客户端将远端设备映射成本机 USB 设备仍然必须依赖虚拟 USB Host Controller/VHCI 内核驱动**。

v1.0 默认组合：

| 平台 | 服务端 | 客户端 |
|---|---|---|
| Windows | usbipd-win | usbip-win/VHCI 或兼容驱动 |
| Linux | usbip_host + usbip | vhci_hcd + usbip |

MyUsbIP 把这些依赖全部隔离在 `MyUsbIP.Platform`。以后改成自己的 KMDF 虚拟总线驱动，只需要实现 `IUsbIpServerBackend` / `IUsbIpClientBackend`，业务层不用重写。

这也是本项目与“直接在业务代码里启动 usbipd.exe”的主要区别。

## 3. 项目结构

```text
MyUsbIP
├─ src
│  ├─ MyUsbIP.Abstractions   公共模型、接口、日志接收器
│  ├─ MyUsbIP.Protocol       USB/IP 协议基础编解码
│  ├─ MyUsbIP.Server         服务端门面
│  ├─ MyUsbIP.Client         客户端门面
│  ├─ MyUsbIP.Platform       Windows/Linux 平台后端
│  └─ MyUsbIP.Runtime        热插拔、自动共享、自动恢复、健康检查
├─ apps
│  └─ MyUsbIP.Daemon         长期运行守护进程 myusbipd
├─ tools
│  └─ MyUsbIP.Cli            管理工具 myusbip
├─ samples
│  ├─ MyUsbIP.Server.Sample
│  └─ MyUsbIP.Client.Sample
└─ docs
   ├─ ARCHITECTURE.md
   ├─ DEPLOYMENT.md
   └─ TROUBLESHOOTING.md
```

## 4. 最简单的类库使用方式

### 服务端

```csharp
using MyUsbIP.Abstractions;
using MyUsbIP.Platform;
using MyUsbIP.Server;

var backendObject = UsbIpBackendFactory.CreateDefault();
var backend = (IUsbIpServerBackend)backendObject;
var server = new MyUsbIpServer(backend);

var devices = await server.GetDevicesAsync();
foreach (var device in devices)
{
    Console.WriteLine($"{device.BusId} {device.VidPid} {device.Product}");
}

await server.ShareAsync("2-3");
```

### 客户端

```csharp
using MyUsbIP.Abstractions;
using MyUsbIP.Client;
using MyUsbIP.Platform;

var backendObject = UsbIpBackendFactory.CreateDefault();
var backend = (IUsbIpClientBackend)backendObject;
var client = new MyUsbIpClient(backend);

var devices = await client.GetRemoteDevicesAsync("192.168.1.100");
var result = await client.AttachAsync("192.168.1.100", "2-3");

Console.WriteLine(result.Message);
```

## 5. 加入诊断日志

```csharp
await using var fileSink = new JsonLinesUsbIpEventSink("logs/myusbip.jsonl");
var memorySink = new MemoryUsbIpEventSink(1000);
var sink = new CompositeUsbIpEventSink(fileSink, memorySink);

var backendObject = UsbIpBackendFactory.CreateDefault(
    sink,
    TimeSpan.FromSeconds(15));

var server = new MyUsbIpServer((IUsbIpServerBackend)backendObject, sink);
var client = new MyUsbIpClient((IUsbIpClientBackend)backendObject, sink);
```

所有底层平台命令都有超时保护。命令超时后会终止整个子进程树，避免 usbip/usbipd/驱动异常把上层业务永久卡住。

## 6. 设备热插拔

```csharp
var monitor = new UsbIpDeviceMonitor(server, sink);

await foreach (var change in monitor.WatchAsync())
{
    Console.WriteLine($"{change.Kind}: {change.Device.BusId} {change.Device.VidPid}");
}
```

v1.0 使用跨平台设备快照差异实现统一行为。后续可以增加 Windows PnP/SetupAPI 与 Linux udev 原生事件后端，上层 API 无需改变。

## 7. 自动共享 UKey

```csharp
var monitor = new UsbIpDeviceMonitor(server, sink);
var autoShare = new UsbIpAutoShareService(server, monitor, sink);

await autoShare.RunAsync([
    new UsbIpAutoShareRule
    {
        VendorId = 0x096E,
        ProductId = 0x0303,
    }
], cancellationToken: stoppingToken);
```

还可以按：

- VID
- PID
- SerialNumber
- BusIdPrefix

组合匹配。

## 8. 客户端自动恢复

```csharp
var manager = new UsbIpConnectionManager(client, sink);

await manager.ConnectAsync("192.168.1.100", "2-3");

await manager.RunRecoveryLoopAsync(new UsbIpReconnectOptions
{
    Enabled = true,
    CheckInterval = TimeSpan.FromSeconds(5),
    RetryDelay = TimeSpan.FromSeconds(3),
}, stoppingToken);
```

当远端设备临时消失后再次出现，连接管理器会重新尝试 Attach，并记录：

- `connection.remote.missing`
- `connection.health.failed`
- `connection.recovered`
- `connection.recovery.stopped`

## 9. CLI

```text
myusbip server list
myusbip server share 2-3
myusbip server unshare 2-3
myusbip server watch
myusbip server diag

myusbip client list 192.168.1.100
myusbip client attach 192.168.1.100 2-3
myusbip client detach 0
myusbip client diag

myusbip diag
```

## 10. 守护进程

`myusbipd` 用于 7x24 小时运行。

默认 `appsettings.json`：

```json
{
  "LogDirectory": "logs",
  "CommandTimeoutSeconds": 15,
  "MonitorIntervalSeconds": 2,
  "AutoShareRules": [
    {
      "VendorId": "096E",
      "ProductId": "0303",
      "Enabled": true
    }
  ],
  "ManagedConnections": [
    {
      "Host": "192.168.1.100",
      "BusId": "2-3",
      "Port": 3240,
      "Enabled": true
    }
  ],
  "Reconnect": {
    "Enabled": true,
    "CheckIntervalSeconds": 5,
    "RetryDelaySeconds": 3,
    "MaxConsecutiveFailures": 0
  }
}
```

启动：

```text
myusbipd appsettings.json
```

## 11. 健康检查

```csharp
var diagnostics = new UsbIpDiagnosticsService(
    (IUsbIpServerBackend)backendObject,
    (IUsbIpClientBackend)backendObject,
    sink);

var serverReport = await diagnostics.CheckServerAsync();
var clientReport = await diagnostics.CheckClientAsync();
```

健康检查用于快速定位：

- usbip/usbipd 是否可执行。
- 服务端是否能枚举 USB。
- 后端是否具备 Share 能力。
- 客户端是否具备 Attach/Detach 能力。
- VHCI/内核模块缺失等问题。

## 12. 日志与指标

关键事件：

```text
server.device.list.*
server.device.share.*
server.device.unshare.*
client.remote.list.*
client.attach.*
client.detach.*
device.added
device.removed
device.changed
autoshare.*
connection.*
diagnostics.*
process.start
process.end
```

指标：

```text
myusbip.operations
myusbip.failures
myusbip.operation.duration
myusbip.network.bytes.sent
myusbip.network.bytes.received
myusbip.connections.active
```

建议生产环境按 TraceId 聚合同一次连接行为的完整日志。

## 13. Windows 部署

服务端：

```bat
usbipd list
myusbip server list
myusbip server share 2-3
```

客户端：

```bat
myusbip client list 192.168.1.100
myusbip client attach 192.168.1.100 2-3
```

需要管理员权限。

## 14. Linux 部署

服务端：

```bash
sudo modprobe usbip_host
sudo myusbip server list
sudo myusbip server share 1-2
```

客户端：

```bash
sudo modprobe vhci_hcd
sudo myusbip client list 192.168.1.100
sudo myusbip client attach 192.168.1.100 1-2
```

## 15. 安全说明

USB/IP 3240 是设备传输协议，不建议直接暴露到公网。

推荐：

- 内网 ACL
- WireGuard
- Tailscale
- ZeroTier
- IPSec
- 专用 VPN

MyUsbIP 不修改标准 USB/IP 数据协议去加入私有认证字段，避免失去 Linux/Windows 标准 USB/IP 互操作性。

## 16. 与 VirtualHere 的对应关系

| VirtualHere | MyUsbIP v1.0 |
|---|---|
| USB Server | usbipd-win / usbip_host + MyUsbIP Server |
| USB Client | VHCI + MyUsbIP Client |
| LIST | `GetDevicesAsync` / `GetRemoteDevicesAsync` |
| USE/BIND | `ShareAsync` + `AttachAsync` |
| STOP USING | `DetachAsync` |
| 自动发现 | `UsbIpDeviceMonitor` |
| 自动共享 | `UsbIpAutoShareService` |
| 自动恢复 | `UsbIpConnectionManager` |
| 日志 | TraceId + JSONL + Metrics |
| CLI | `myusbip` |
| 长期服务 | `myusbipd` |

## 17. 后续自研驱动

如果最终目标是彻底不依赖 usbipd-win / usbip-win，可继续实现：

```text
MyUsbIP.Platform.Windows.NativeServerBackend
MyUsbIP.Platform.Windows.NativeVhciBackend
```

内部使用：

- SetupAPI
- cfgmgr32
- CreateFile
- DeviceIoControl
- KMDF 虚拟 USB 总线/VHCI 驱动

上层 API、守护进程、日志、自动恢复全部可以保持不变。

## 18. 文档

- `docs/ARCHITECTURE.md`：整体架构与驱动边界。
- `docs/DEPLOYMENT.md`：Windows/Linux 部署。
- `docs/TROUBLESHOOTING.md`：连接链路及 UKey/CH340 排障。
- `CHANGELOG.md`：版本变更。

## License

本项目自身代码请按仓库最终 LICENSE 使用。引用或部署 usbipd-win、usbip-win、Linux USB/IP 时，请分别遵守对应上游项目许可证。
