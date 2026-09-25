# MyUsbIP v1.1.14

本版本重点是稳定性审计、安装链路加固与 Release 体积优化。Windows 客户端继续使用 **usbip-win2 0.9.8.0**，没有回退客户端驱动。

## 稳定性修复

- 修复 UsbDk EndSession 与新 IMPORT 之间的 BUSID 抢占竞态：StopRedirect 和宿主驱动稳定回绑完成前，旧会话始终保持设备占用。
- 保留 v1.1.12 精确单 URB CancelIoEx 与 v1.1.13 Detach 后 StopRedirect + 稳定回绑策略。
- Win32Exception 日志记录 NativeErrorCode/Hex/原生错误消息，UsbDk/Windows API 故障不再只看到通用 HResult。

## Windows 安装器

- 已健康安装 UsbDk 时跳过重复 MSI，降低升级过程对宿主 USB/PnP 的扰动。
- UsbDk/usbip-win2 返回“需要重启”时明确中止后续启动与自检。
- 防火墙和 Server 自检跟随 NativeServer.Port。
- 外部安装命令增加超时保护。
- 成功安装后清理临时 SetupCache。
- UsbDk 与 usbip-win2 SHA256 均固定在仓库并由 CI 强制验证。

## 体积优化

- CLI、Daemon、Setup 启用 .NET single-file compression。
- Server Setup 仅嵌 Server payload + UsbDk；Client Setup 仅嵌 Client payload + usbip-win2。
- 备用 Server/Client ZIP 不再重复包含独立 Setup EXE。
- 每个备用 ZIP 只包含对应角色所需的离线驱动依赖。
- CI 输出各 Release 资产实际字节数，便于后续持续控制包体积。

## 验证范围

CI 必须通过 Windows/Linux Build、Smoke Tests、self-contained single-file publish、Windows 双角色 Setup、Bundle 和正式 Release。CH340/UKey 的物理硬件时序仍以目标 Windows 实机回归为最终依据。
