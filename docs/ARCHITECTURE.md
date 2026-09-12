# MyUsbIP v1.0 架构

## 目标

MyUsbIP 的目标是提供一套基于标准 USB/IP 的跨平台 USB 网络共享封装，用来替代 VirtualHere 的主要使用场景，同时保持底层驱动可替换。

## 分层

```text
业务系统 / 管理页面 / RPA / 自动化服务
            |
            v
+-----------------------------+
| MyUsbIP.Runtime             |
| 热插拔 / 自动共享 / 自动恢复 |
| 健康检查 / 长期运行状态      |
+-----------------------------+
      |                 |
      v                 v
+-------------+   +-------------+
| Server      |   | Client      |
+-------------+   +-------------+
      \                 /
       \               /
        v             v
       +---------------+
       | Abstractions  |
       +---------------+
              |
              v
       +---------------+
       | Platform      |
       | Win / Linux   |
       +---------------+
              |
              v
+------------------------------------+
| usbipd-win / usbip-win / Linux USBIP|
+------------------------------------+
```

## 为什么不把驱动逻辑直接写进业务层

Windows 上将远程 USB 设备表现为本机 USB 设备必须依赖内核驱动或虚拟 Host Controller。纯 .NET 代码无法替代这一层。因此 v1.0 把驱动交互封装在 `IUsbIpServerBackend` / `IUsbIpClientBackend` 中。

如果后续开发自研 KMDF 虚拟 USB 总线驱动，只需要增加新的 Backend，上层 `IMyUsbIpServer`、`IMyUsbIpClient`、自动共享和连接恢复代码无需改动。

## 连接链路

```text
设备插入
 -> OS 枚举
 -> MyUsbIP 发现设备
 -> 自动共享/手动 Share
 -> USB/IP 服务监听 3240
 -> 客户端 ListRemote
 -> Attach
 -> VHCI 创建本地虚拟 USB 设备
 -> Windows/Linux PnP 枚举驱动
 -> 业务程序访问设备
```

每个关键操作都会产生结构化事件，底层进程调用还会额外产生 `process.start/process.end` 事件。

## 可观测性

主要事件：

- `server.device.list.*`
- `server.device.share.*`
- `server.device.unshare.*`
- `client.remote.list.*`
- `client.attach.*`
- `client.detach.*`
- `device.added/removed/changed`
- `autoshare.*`
- `connection.*`
- `diagnostics.*`
- `process.start/process.end`

主要指标：

- `myusbip.operations`
- `myusbip.failures`
- `myusbip.operation.duration`
- `myusbip.network.bytes.sent`
- `myusbip.network.bytes.received`
- `myusbip.connections.active`

## v1.0 边界

v1.0 已经提供完整托管层替代方案，但不重新实现 Windows 内核虚拟 USB 总线驱动。Windows 客户端需要 usbip-win/VHCI 或其他兼容驱动；Windows 服务端默认使用 usbipd-win 驱动栈。Linux 使用内核 USB/IP。
