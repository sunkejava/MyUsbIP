# Changelog

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
