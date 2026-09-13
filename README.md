# MyUsbIP

基于 **.NET 10 + 标准 USB/IP** 的跨平台 USB 网络共享封装，目标是为 Windows/Linux 提供一套可维护、可诊断、可自动恢复的 VirtualHere 替代方案。

> 当前稳定版本：**v1.1.6**

## Windows 推荐架构

```text
Windows 服务端
真实 USB/UKey
  -> UsbDk
  -> MyUsbIP.UsbDk
  -> MyUsbIP.NativeServer（USB/IP protocol v1.1.1）
  -> TCP 3240

Windows 客户端
  -> vadimgrn/usbip-win2 0.9.8.0 UDE/VHCI
  -> Windows USB/PnP
  -> 原厂 UKey/USB 驱动
```

服务端不需要安装 usbip-win2；usbip-win2 仅用于 Windows 客户端。服务端负责严格实现标准 USB/IP DEVLIST / IMPORT / SUBMIT / UNLINK，同时通过 MyUsbIP 私有管理扩展补充连接客户端、会话和完整设备元数据。

## Windows 最终用户安装

Windows Release 采用 **self-contained + single-file** 发布，目标机器无需预装 .NET 10 Runtime。

### 服务端

直接运行 Release 中的：

```text
MyUsbIP-Server-Setup.exe
```

安装器自动完成管理员提权、UsbDk 校验与安装、服务端部署、防火墙、SYSTEM 开机启动任务、启动和自检。

### 客户端

直接运行 Release 中的：

```text
MyUsbIP-Client-Setup.exe
```

安装器自动完成管理员提权、usbip-win2 0.9.8.0 UDE/VHCI 安装或复用、CLI 部署、PATH 配置和驱动自检。

**最终用户无需执行任何 PowerShell、CMD 或 BAT 安装/测试脚本。** `scripts/windows` 只保留给项目维护和 CI 使用。

安装日志统一写入：

```text
C:\ProgramData\MyUsbIP\InstallerLogs
```

## 运行时说明

Windows 发布使用 self-contained 单文件模式，因此 `myusbipd.exe`、`myusbip.exe` 和两个 Setup EXE 均不依赖目标机预装 .NET Runtime。

服务端日志：

```text
C:\ProgramData\MyUsbIP\ServerLogs
```

客户端日志：

```text
C:\ProgramData\MyUsbIP\ClientLogs
```

JSONL 日志使用 UTF-8，中文直接可读。

## CLI

客户端安装完成后常用命令：

```text
myusbip client list 192.168.1.100
myusbip client port
myusbip client port 1
myusbip client attach 192.168.1.100 <busid>
myusbip client detach <port>
myusbip client diag
```

其中：

- `client list <host>`：查看远端服务器当前实际存在、可连接或已被占用的 USB 设备，并显示 BUS/DEV、速度、接口信息、连接客户端等扩展状态。
- `client port`：查看本机 usbip-win2 UDE/VHCI 当前已经导入的全部 USB/IP 设备。
- `client port <local-port>`：只查看指定 UDE/VHCI 本地端口。
- `client attach`：通过 usbip-win2 挂载远端设备，默认 `ReceiveMode=zero-copy`。
- `client detach`：释放指定本地 UDE/VHCI 端口。

服务端默认监听 TCP 3240。

## 设备拔插与 Redirect 生命周期

为了兼容 CH340 等设备在 `UsbDk_StopRedirect -> StartRedirect` 后可能无法二次重定向的问题，MyUsbIP 会在设备仍物理存在时保留 Redirect 句柄和描述符快照。

设备物理拔出后，服务端使用 Windows Configuration Manager 检查 PnP Device Instance 是否仍处于 Present 状态；已拔出的设备会立即从 Redirect 快照、DEVLIST 和活动会话占用中清理，不应继续出现在 `myusbip client list <host>` 中。重新插入后按新的实时枚举状态重新进入列表。

## 主要能力

- Windows 服务端 UsbDk 捕获与原生 USB/IP 导出。
- Windows 客户端 usbip-win2 0.9.8.0 UDE/VHCI Attach/Detach/Port。
- Linux USB/IP 后端。
- USB 设备发现、共享、远程枚举、Attach、Detach。
- VID/PID/序列号/BusId 自动共享规则。
- 客户端连接恢复。
- TraceId、Activity、Metrics、JSON Lines 日志。
- 连接健康检查与诊断。
- 并发 USB/IP URB 转发。
- 逐接口 Class/SubClass/Protocol 元数据。
- 当前设备占用客户端、SessionId、ConnectedAt 状态查询。
- GitHub Actions 自动生成 Server/Client/Linux Release 包。

## 安全说明

TCP 3240 不建议直接暴露公网。生产环境应放在可信局域网、专线或 VPN 后面，并通过 Windows 防火墙限制来源网段。

## 文档

- `docs/WINDOWS-USBDK-USBIPWIN.md`
- `docs/WINDOWS-DEPLOYMENT-TEST.md`
- `docs/ARCHITECTURE.md`
- `docs/TROUBLESHOOTING.md`
- `CHANGELOG.md`

## License

MyUsbIP 自身代码按仓库 LICENSE 使用。UsbDk、usbip-win2 等第三方组件分别遵循其上游许可证。
