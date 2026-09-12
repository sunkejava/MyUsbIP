# MyUsbIP v1.1.0

本版本正式切换 Windows 推荐架构为：

- 服务端：UsbDk + MyUsbIP.NativeServer
- 客户端：usbip-win VHCI
- 网络协议：标准 USB/IP / TCP 3240

## 主要变化

- 服务端不再依赖 usbipd-win / usbipd.exe。
- 新增 UsbDk 原生设备枚举、重定向和 USB 传输桥接。
- 新增标准 USB/IP DEVLIST / IMPORT / SUBMIT / UNLINK 数据路径。
- 支持 Control / Bulk / Interrupt 主路径及多 URB 并发。
- 客户端独立使用 usbip-win VHCI。
- 新增 Windows 服务端/客户端一键部署、测试和依赖管理脚本。
- 新增固定版本依赖清单与 SHA256 校验机制。
- 自动生成 Server/Client Windows Bundle 和 checksums。

## 发布产物

- MyUsbIP-Server-win-x64.zip
- MyUsbIP-Client-win-x64.zip
- myusbip-linux-x64.zip
- checksums.sha256

## 首轮硬件验证目标

1. 096E:0303
2. 096E:0312
3. 标准 CCID UKey
4. FT ePass3000GM
5. CH340
6. U 盘
7. 多 Hub / 多 UKey 长时间稳定性

> 本版本完成软件链路集成和部署体系，正式生产前仍应完成目标 UKey/Hub 的实机稳定性验证。
