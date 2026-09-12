# MyUsbIP

基于 **.NET 10** 封装的跨平台 USB/IP 服务端/客户端类库，用统一 API 管理 USB 设备网络共享，目标是在业务系统中逐步替代 VirtualHere。

> 当前阶段重点是：统一 .NET API、Windows/Linux 平台适配、超时与异常恢复、结构化日志、链路 TraceId、指标埋点。底层 USB 虚拟总线仍复用各平台成熟的 USB/IP 驱动/工具链，避免在第一阶段重复实现高风险内核驱动。

## 设计参考

项目设计参考以下实现的架构思想与 USB/IP 协议行为，但 MyUsbIP 代码为独立封装：

- `sunkejava/usbipdcpp`：参考其 `Server / Session / UsbDevice / DeviceHandler` 的清晰分层及异步 I/O 思路。
- `sunkejava/usbipd-win`：参考 Windows USB/IP 服务、设备生命周期和驱动交互方式。

第三方项目、驱动和命令行工具仍受它们各自许可证约束；不要把第三方二进制文件直接当作 MyUsbIP 自有组件重新分发。

## 当前架构

```text
业务系统
  ├─ IMyUsbIpServer
  │    └─ MyUsbIpServer
  │          └─ IUsbIpServerBackend
  │                ├─ WindowsUsbIpBackend -> usbipd-win
  │                └─ LinuxUsbIpBackend   -> usbip_host / usbip
  │
  └─ IMyUsbIpClient
       └─ MyUsbIpClient
             └─ IUsbIpClientBackend
                   ├─ WindowsUsbIpBackend -> usbip-win / VHCI
                   └─ LinuxUsbIpBackend   -> vhci_hcd / usbip

公共层
  ├─ MyUsbIP.Abstractions  设备模型、接口、诊断模型
  ├─ MyUsbIP.Protocol      USB/IP 网络协议编解码基础
  └─ MyUsbIP.Platform      Windows/Linux 平台后端
```

这样设计的目的，是让上层业务永远不依赖某一个驱动。后续将 Windows 后端从命令行替换成驱动 IOCTL/PInvoke，或增加自研 VHCI 驱动时，上层代码无需修改。

## 支持情况

| 系统 | 服务端共享物理 USB | 客户端映射远程 USB | 当前底层 |
|---|---:|---:|---|
| Windows 10/11 | ✅ | ✅ | 服务端 usbipd-win；客户端 usbip-win/VHCI |
| Linux | ✅ | ✅ | usbip_host + vhci_hcd + usbip 工具 |
| macOS | ⏳ | ⏳ | 已预留 Backend 接口 |

> USB/IP 默认 TCP 端口为 `3240`。生产环境不要直接把 3240 暴露到公网，建议通过内网、VPN、Tailscale/WireGuard 等可信网络使用，并在业务层增加设备授权。

## 项目结构

```text
src/
  MyUsbIP.Abstractions/   # 公共接口、模型、诊断
  MyUsbIP.Protocol/       # USB/IP 网络字节序、公共协议头、BusId 编解码
  MyUsbIP.Server/         # 服务端类库
  MyUsbIP.Client/         # 客户端类库
  MyUsbIP.Platform/       # Windows/Linux 平台实现
samples/
  MyUsbIP.Server.Sample/  # 服务端调用示例
  MyUsbIP.Client.Sample/  # 客户端调用示例
```

## 构建

需要 .NET 10 SDK：

```bash
dotnet build MyUsbIP.slnx -c Release
```

## Windows 服务端部署

### 前置条件

1. 安装可用的 `usbipd-win`，确保 `usbipd.exe` 在 PATH 中，或实例化 `WindowsUsbIpBackend` 时传入完整路径。
2. `usbipd` 服务应处于运行状态。
3. 防火墙允许可信网段访问 TCP 3240。
4. 共享/取消共享设备通常需要管理员权限。

### 类库调用

```csharp
using MyUsbIP.Abstractions;
using MyUsbIP.Platform;
using MyUsbIP.Server;

IUsbIpServerBackend backend = new WindowsUsbIpBackend();
IMyUsbIpServer server = new MyUsbIpServer(backend);

var devices = await server.GetDevicesAsync();
foreach (var device in devices)
{
    Console.WriteLine($"{device.BusId} {device.VidPid} {device.Product} {device.State}");
}

await server.ShareAsync("1-3");
// await server.UnshareAsync("1-3");
```

## Linux 服务端部署

不同发行版的软件包名称可能不同，核心要求是内核 USB/IP 支持和 `usbip` 命令可用。

典型准备流程：

```bash
sudo modprobe usbip_core
sudo modprobe usbip_host
sudo usbipd -D
usbip list -l
```

.NET 调用：

```csharp
IUsbIpServerBackend backend = new LinuxUsbIpBackend();
IMyUsbIpServer server = new MyUsbIpServer(backend);

var devices = await server.GetDevicesAsync();
await server.ShareAsync("1-2");
```

Linux 下绑定 USB 设备一般需要 root 或相应 udev/capability 权限。

## Windows 客户端部署

Windows 客户端需要能够提供虚拟 USB Host Controller 的 USB/IP 客户端驱动，例如 `usbip-win/VHCI`，并确保其 `usbip.exe` 可用。

```csharp
using MyUsbIP.Abstractions;
using MyUsbIP.Client;
using MyUsbIP.Platform;

IUsbIpClientBackend backend = new WindowsUsbIpBackend();
IMyUsbIpClient client = new MyUsbIpClient(backend);

var devices = await client.GetRemoteDevicesAsync("192.168.1.20");
var result = await client.AttachAsync("192.168.1.20", "1-3");
Console.WriteLine(result.Success);
```

挂载成功后，Windows 应通过 VHCI 把远端设备重新枚举为本地 USB 设备；之后上层软件按照本地 USB 设备使用，不需要理解网络转发过程。

## Linux 客户端部署

```bash
sudo modprobe vhci_hcd
usbip list -r 192.168.1.20
sudo usbip attach -r 192.168.1.20 -b 1-3
usbip port
```

对应 .NET：

```csharp
IUsbIpClientBackend backend = new LinuxUsbIpBackend();
IMyUsbIpClient client = new MyUsbIpClient(backend);

var devices = await client.GetRemoteDevicesAsync("192.168.1.20");
await client.AttachAsync("192.168.1.20", "1-3");
```

## 日志、链路追踪与监控

MyUsbIP 从第一版就把排障作为核心能力。

### 结构化事件

实现 `IUsbIpEventSink` 即可接入你自己的日志系统：

```csharp
public sealed class MyEventSink : IUsbIpEventSink
{
    public ValueTask WriteAsync(UsbIpEvent evt, CancellationToken cancellationToken = default)
    {
        // 可写入 Serilog、SQLite、Elastic、MQTT、Prometheus 告警系统等。
        Console.WriteLine($"{evt.TraceId} {evt.EventName} {evt.BusId} {evt.Message}");
        return ValueTask.CompletedTask;
    }
}
```

当前主要事件包括：

- `server.device.list.start/success/failed`
- `server.device.share.start/success/failed`
- `server.device.unshare.start/success/failed`
- `client.remote.list.start/success/failed`
- `client.attach.start/success/failed`
- `client.detach.start/success/failed`
- `process.start/process.end`

每次操作自动创建 `System.Diagnostics.Activity`，事件携带 `TraceId`，后续可以把“业务连接请求 -> Hub 上电 -> USB 枚举 -> Share -> Client Attach -> VHCI 枚举”全部串成一条链路。

### Metrics

`UsbIpDiagnostics` 使用 .NET 原生 `System.Diagnostics.Metrics`，已经预留：

- `myusbip.operations`
- `myusbip.failures`
- `myusbip.operation.duration`
- `myusbip.network.bytes.sent`
- `myusbip.network.bytes.received`
- `myusbip.connections.active`

可直接通过 OpenTelemetry 导出到 Prometheus/Grafana。

### 防止底层命令卡死

所有平台命令默认都有 10 秒超时：

- 支持外部 `CancellationToken`；
- 超时后终止整个进程树；
- 记录命令开始、结束、耗时和失败信息；
- 不允许 `usbip/usbipd` 的异常永久卡住业务线程。

可自定义：

```csharp
var backend = new WindowsUsbIpBackend(
    usbipdPath: @"C:\Program Files\usbipd-win\usbipd.exe",
    usbipPath: @"C:\usbip-win\usbip.exe",
    eventSink: sink,
    commandTimeout: TimeSpan.FromSeconds(5));
```

## 与 VirtualHere 的差异

VirtualHere 是完整商业产品，包含自有 Windows/Linux USB 捕获与虚拟总线实现。MyUsbIP 当前阶段的定位是：

1. 用 .NET 10 提供你可控、易读、带中文注释的统一 SDK；
2. 把 Windows/Linux 已成熟的 USB/IP 驱动隐藏在 Backend 后；
3. 先解决你当前最需要的设备共享、远程挂载、状态管理和故障链路监控；
4. 后续逐步把命令行 Backend 替换成原生 API/IOCTL，实现更低延迟、更完整状态和更好的异常恢复。

## 后续规划

### v0.2：稳定性与自动恢复

- 设备热插拔事件；
- 设备 InstanceId/VID/PID/序列号与 BusId 稳定映射；
- Share/Attach 幂等；
- 断线检测与可配置自动重连；
- 连接状态机；
- Windows PnP/SetupAPI 诊断；
- Linux sysfs/udev 诊断；
- 按设备、端口、客户端 IP 的连接历史。

### v0.3：服务化

- Windows Service / systemd Worker；
- ASP.NET Core 管理 API；
- 设备授权与租约；
- JWT/mTLS；
- SignalR/MQTT 状态推送；
- Prometheus `/metrics`；
- 健康检查与诊断快照。

### v0.4：原生驱动接口

- Windows 后端由启动命令改为直接驱动 IOCTL；
- Linux 后端直接读取 sysfs 并控制 usbip 内核接口；
- 客户端 VHCI 状态直接读取；
- URB/端点级别统计与慢请求分析。

### v1.0：完整替代方案

- 自研/可控 Windows 虚拟 USB 总线适配层；
- 服务端/客户端安装包；
- 自动驱动检测与修复；
- UKey/智能卡/CH340/U盘等兼容性矩阵；
- 多 Hub、大量设备并发、租约和权限控制。

## 特别说明：UKey/智能卡

USB/IP 能否稳定转发某个 UKey，不只取决于 TCP。需要同时关注：

- 设备是否为复合设备；
- CCID/WUDF 驱动行为；
- 是否包含特殊等时/中断端点；
- 设备驱动是否依赖物理拓扑/序列号；
- 网络抖动和 URB 超时；
- 客户端虚拟总线驱动兼容性。

因此后续会针对你常用的 `096E:0303/0312`、FT ePass、CH340 等设备增加专项诊断事件和兼容性测试。
