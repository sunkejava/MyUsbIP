# Changelog

## 1.1.2

在 CH340 已完成远程 Attach、串口收发实机验证后，继续修复 USB/IP 会话管理与设备元数据问题。

- 同一物理 `BUSID` 同一时刻只允许一个活动 USB/IP IMPORT 会话，避免重复 Attach 到多个 VHCI 端口后多个客户端同时向同一个 UsbDk Handle 发送 URB。
- 第二次 IMPORT 同一设备时返回标准 USB/IP 失败响应，不再直接复用现有 Redirect 句柄。
- 客户端 detach、TCP 断开或会话异常结束后自动释放会话独占锁，允许后续重新 Attach。
- SUBMIT 增加活动会话校验，阻止已结束会话继续向设备提交 URB。
- `UsbIpDeviceInfo` 增加 Path、BusNumber、DeviceNumber、Speed 等标准 USB/IP 线协议元数据。
- USB/IP DEVLIST / IMPORT 响应不再固定写死 `busnum=0/devnum=0/speed=2`，当前 UsbDk Windows BusId 可解析出真实 FilterId/Port 作为 bus/dev 标识。
- 增加 USB/IP Device Wire 元数据回归测试。
- `usbip-win` 的 `unknown host, remote port and remote busid` 还可能来自客户端本地 connection record，属于上游 usbip-win 已知行为；MyUsbIP 已修复服务端可控的 bus/dev/path 字段。

## 1.1.1

修复 Windows UsbDk 服务端 Control Transfer 返回长度计算错误。

- UsbDk `BytesTransferred` 对 Control Transfer 表示数据阶段实际长度，不包含 8 字节 Setup Packet。
- v1.1.0 错误再次减去 8，导致 `GET_DESCRIPTOR(9)` 被上报为 1 字节，客户端出现 `fetch_descriptor: too short response: actual length: 1` 并 Attach 失败。
- 修复后 Control IN 返回长度直接使用 UsbDk `BytesTransferred`，数据仍从 Setup Packet 后 8 字节位置读取。
- 该修复主要影响设备 Attach/枚举阶段，客户端 VHCI 无需因该问题变更。

## 1.1.0

Windows 主链路切换为 **UsbDk 服务端 + MyUsbIP 原生 USB/IP Server + usbip-win VHCI 客户端**，目标是在不自研/自签 Windows 内核驱动的前提下完成可部署、可诊断、可验证的 USB 网络共享方案。

### Windows 服务端

- 新增 `MyUsbIP.UsbDk`，直接通过 `UsbDkHelper.dll` 枚举和重定向真实 USB 设备。
- 服务端不再依赖 `usbipd-win / usbipd.exe`。
- `MyUsbIP.NativeServer` 直接实现标准 USB/IP DEVLIST / IMPORT / SUBMIT / UNLINK 数据路径。
- 支持 Control、Bulk、Interrupt 传输主路径。
- 支持多个 URB 并发在途和串行化安全回包，避免 CCID Interrupt 长轮询阻塞其他请求。

### Windows 客户端

- 新增独立 `UsbipWinVhciClientBackend`。
- 客户端使用 usbip-win `usbip.exe + VHCI`。
- 服务端/客户端平台依赖完全拆分。
- 支持远端列表、Attach、Detach、端口解析和诊断。

### Windows 安装与发布

- Windows CLI、Daemon、Setup 全部改为 .NET 10 `self-contained + single-file` 发布，目标机无需预装 .NET Runtime。
- 新增 `MyUsbIP.WindowsSetup` 双击安装器。
- 服务端用户只需双击 `MyUsbIP-Server-Setup.exe`。
- 客户端用户只需双击 `MyUsbIP-Client-Setup.exe`。
- 安装器自动请求管理员权限、校验离线驱动 SHA256、安装驱动、部署程序并执行内置自检。
- 服务端不再将普通 Console Daemon 通过 `sc.exe` 错误注册为 Windows Service，改为 SYSTEM 开机计划任务运行。
- 自动配置 TCP 3240 防火墙。
- 客户端自动安装 usbip-win VHCI，优先 UDE，失败时回退自动模式。
- 最终用户 Bundle 不再包含需要手工执行的 `.ps1/.cmd/.bat` 安装和测试入口。
- 安装日志统一写入 `C:\ProgramData\MyUsbIP\InstallerLogs`。
- Release 自动内置固定版本 UsbDk 与 usbip-win，并在打包阶段冻结/校验 SHA256。
- GitHub Release 支持重复执行和附件覆盖更新。
- 修复 Release Job 缺少仓库上下文导致 `fatal: not a git repository` 的问题。

### 发布产物

- `MyUsbIP-Server-win-x64.zip`
- `MyUsbIP-Client-win-x64.zip`
- `myusbip-linux-x64.zip`
- `checksums.sha256`

### 当前验证边界

正式生产前仍需要针对目标硬件完成实机验证，优先顺序：096E:0303、096E:0312、标准 CCID UKey、FT ePass3000GM、CH340、U 盘以及多 Hub 长时间压力测试。Isochronous 设备暂不作为本版本重点生产支持范围。

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
