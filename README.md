# MyUsbIP

基于 **.NET 10 + 标准 USB/IP** 的跨平台 USB 网络共享封装，目标是为 Windows/Linux 提供一套可维护、可诊断、可自动恢复的 VirtualHere 替代方案。

> 当前版本：**v1.1.0**

## Windows 推荐架构

```text
Windows 服务端
真实 USB/UKey
  -> UsbDk
  -> MyUsbIP.UsbDk
  -> MyUsbIP.NativeServer
  -> TCP 3240

Windows 客户端
  -> usbip-win VHCI
  -> Windows USB/PnP
  -> 原厂 UKey/USB 驱动
```

## Windows 最终用户安装

Windows Release 采用 **self-contained + single-file** 发布，目标机器无需预装 .NET 10 Runtime。

### 服务端

下载并解压 `MyUsbIP-Server-win-x64.zip`，然后直接双击：

```text
MyUsbIP-Server-Setup.exe
```

安装器自动完成管理员提权、UsbDk 校验与安装、服务端部署、防火墙、SYSTEM 开机启动任务、启动和自检。

### 客户端

下载并解压 `MyUsbIP-Client-win-x64.zip`，然后直接双击：

```text
MyUsbIP-Client-Setup.exe
```

安装器自动完成管理员提权、usbip-win VHCI 安装、CLI 部署、PATH 配置和 VHCI 自检。

**最终用户无需执行任何 PowerShell、CMD 或 BAT 安装/测试脚本。** `scripts/windows` 只保留给项目维护和 CI 使用。

安装日志统一写入：

```text
C:\ProgramData\MyUsbIP\InstallerLogs
```

## 运行时说明

Windows 发布使用 self-contained 单文件模式，因此 `myusbipd.exe`、`myusbip.exe` 和两个 Setup EXE 均不依赖目标机预装 .NET Runtime。

## CLI

客户端安装完成后可使用：

```text
myusbip client list 192.168.1.100
myusbip client attach 192.168.1.100 <busid>
myusbip client detach <port>
```

服务端默认监听 TCP 3240。

## 主要能力

- Windows 服务端 UsbDk 捕获与 USB/IP 导出。
- Windows 客户端 usbip-win VHCI Attach/Detach。
- Linux USB/IP 后端。
- USB 设备发现、共享、远程枚举、Attach、Detach。
- VID/PID/序列号/BusId 自动共享规则。
- 客户端连接恢复。
- TraceId、Activity、Metrics、JSON Lines 日志。
- 连接健康检查与诊断。
- 并发 USB/IP URB 转发。
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

MyUsbIP 自身代码按仓库 LICENSE 使用。UsbDk、usbip-win 等第三方组件分别遵循其上游许可证。
