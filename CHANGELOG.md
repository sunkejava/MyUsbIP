# Changelog

## 1.1.0

Windows 主链路切换为 **UsbDk 服务端 + MyUsbIP 原生 USB/IP Server + usbip-win VHCI 客户端**，目标是在不自研/自签 Windows 内核驱动的前提下完成可部署、可诊断、可验证的 USB 网络共享方案。

### Windows 服务端

- 新增 `MyUsbIP.UsbDk`，直接通过 `UsbDkHelper.dll` 枚举和重定向真实 USB 设备。
- 服务端不再依赖 `usbipd-win / usbipd.exe`。
- `MyUsbIP.NativeServer` 直接实现标准 USB/IP DEVLIST / IMPORT / SUBMIT / UNLINK 数据路径。
- 支持 Control、Bulk、Interrupt 传输主路径。
- 支持多个 URB 并发在途和串行化安全回包，避免 CCID Interrupt 长轮询阻塞其他请求。
- 支持 UsbDk Abort/Reset Pipe 及会话释放。

### Windows 客户端

- 新增独立 `UsbipWinVhciClientBackend`。
- 客户端使用 usbip-win `usbip.exe + VHCI`。
- 服务端/客户端平台依赖完全拆分，不再复用包含 `usbipd.exe` 语义的旧 Windows backend。
- 支持远端列表、Attach、Detach、端口解析和诊断。

### 部署与发布

- 新增 Windows 服务端/客户端完整部署与测试文档。
- 新增固定版本依赖清单 `config/dependencies.windows.json`。
- 固定 UsbDk `1.0.22 x64` 官方 Release 下载地址。
- 固定 usbip-win `0.3.6-dev` 官方 Release 下载地址。
- 新增依赖下载、缓存、SHA256 校验脚本。
- 新增服务端一键安装脚本。
- 新增客户端一键安装脚本，支持 UDE/WDM VHCI。
- 新增 Server/Client Bundle 自动打包。
- GitHub Release 自动生成：
  - `MyUsbIP-Server-win-x64.zip`
  - `MyUsbIP-Client-win-x64.zip`
  - `myusbip-linux-x64.zip`
  - `checksums.sha256`
- 增加 `release/vX.Y.Z` 分支发布入口。

### 当前验证边界

v1.1.0 为 Windows UsbDk + usbip-win 路线的首个正式集成版本。正式生产前仍需要针对目标硬件完成实机验证，优先顺序：

1. 096E:0303
2. 096E:0312
3. 标准 CCID UKey
4. FT ePass3000GM
5. CH340
6. U 盘
7. 多设备/多 Hub 长时间压力测试

Isochronous USB/IP packet descriptors 尚未作为本版本重点验证范围，因此摄像头、USB 声卡等实时流设备暂不列为生产支持目标。

## 1.0.0

首个可交付版本，目标是提供 VirtualHere 的标准 USB/IP 替代方案。

### 核心能力

- .NET 10 服务端/客户端统一类库。
- Windows/Linux 双平台后端。
- USB/IP 设备发现、共享、取消共享、远程枚举、Attach、Detach。
- 跨平台设备热插拔监控。
- VID/PID/序列号/BusId 前缀自动共享规则。
- 受管客户端连接、状态记录、远端设备恢复后的自动重新挂载。
- TraceId、Activity、Metrics、JSON Lines 结构化日志。
- 服务端/客户端健康检查。
- `myusbip` CLI 管理工具。
- `myusbipd` 跨平台守护进程。
- Windows/Linux GitHub Actions 构建及发布产物。

### 平台依赖

- Windows 服务端：usbipd-win 驱动/服务。
- Windows 客户端：usbip-win/VHCI 或兼容虚拟 USB Host Controller。
- Linux：usbip-tools、usbip_host、vhci_hcd。

平台依赖均被隔离在 `MyUsbIP.Platform`，上层 API 不绑定具体驱动实现。
