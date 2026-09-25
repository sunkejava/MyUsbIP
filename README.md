# MyUsbIP

基于 **.NET 10 + 标准 USB/IP** 的跨平台 USB 网络共享封装，目标是为 Windows/Linux 提供一套可维护、可诊断、可自动恢复的 VirtualHere 替代方案。

> 当前稳定版本：**v1.1.14**

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

安装器自动完成管理员提权、UsbDk 健康检测/按需安装、服务端部署、按实际 NativeServer.Port 配置防火墙、SYSTEM 开机启动任务、启动和自检。驱动安装若要求重启会明确停止后续启动流程。

### 客户端

直接运行 Release 中的：

```text
MyUsbIP-Client-Setup.exe
```

安装器自动完成管理员提权、usbip-win2 0.9.8.0 UDE/VHCI 安装或复用、CLI 部署、PATH 配置和驱动自检；驱动安装要求重启时不会继续执行半完成状态下的自检。

**最终用户无需执行任何 PowerShell、CMD 或 BAT 安装/测试脚本。** `scripts/windows` 只保留给项目维护和 CI 使用。

安装日志统一写入：

```text
C:\ProgramData\MyUsbIP\InstallerLogs
```

## 运行时说明

Windows 发布使用 self-contained 单文件模式，因此 `myusbipd.exe`、`myusbip.exe` 和两个 Setup EXE 均不依赖目标机预装 .NET Runtime。v1.1.14 起启用 single-file 压缩，并将 Server/Client Setup 拆为各自只嵌入本角色 payload 与驱动依赖；备用 ZIP 不再重复包含 Setup EXE。

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

当前服务端以“**单会话独占 + 精确取消 URB + Detach 后真正归还宿主驱动**”为准：

- IMPORT 成功后由 UsbDk Redirect 独占真实 USB 设备，同一 BUSID 同时只允许一个活动 USB/IP 会话。
- USB/IP UNLINK/断线清理优先使用按 OVERLAPPED 的 `CancelIoEx` 精确取消，避免端点级 `AbortPipe/ResetPipe` 误伤同端点其他 CH340 Bulk-IN 请求。
- Detach/TCP 断开后等待挂起 URB 收尾，再执行 `UsbDk_StopRedirect`，把设备真正归还 Windows 原生驱动栈。
- StopRedirect 后等待同一物理设备在 UsbDk 原生枚举中连续稳定出现，再释放 BUSID 会话占用；释放过程未完成时不会允许新的 IMPORT 抢占，避免旧会话清理与新会话重定向竞态。
- 物理拔出后的 Redirect 快照通过 Windows SetupAPI PRESENT USB 枚举进行保守清理，不再把 UsbDk 的短 InstanceId 直接当作完整 PnP InstanceId。

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
