# MyUsbIP NativeWindows 架构

## 目标

NativeWindows 模式的最终目标是让 Windows 服务端和 Windows 客户端都不再安装 `usbipd-win`、`usbip-win` 或 VirtualHere。

最终链路：

```text
服务端真实 USB 设备
  -> MyUsbIP.Exporter.sys
  -> WindowsExporterTransport
  -> UsbIpNativeServer (TCP 3240)
  -> 网络
  -> WindowsNativeClientBackend
  -> MyUsbIP.Vhci.sys (UdeCx)
  -> Windows USB Stack / PnP
  -> UKey/CCID/CH340 原厂驱动
```

## 为什么服务端也需要驱动

Windows 下真实 USB 设备通常已经被 CCID、WUDF、串口、存储等功能驱动占用。仅使用 .NET 服务无法完整代理这些设备的 URB，同时又保持设备原驱动关系。因此服务端采用选择性 KMDF Exporter/Filter 驱动，而不是要求所有设备改绑 WinUSB。

Exporter 必须按设备实例选择性安装/启用，不允许把不必要的过滤器全局挂到所有 USB 设备，避免扩大系统稳定性风险。

## 客户端为什么使用 UdeCx

MyUsbIP.Vhci 不从零实现整个 USB Host Controller/Hub 总线，而使用 Windows UdeCx（USB Device Emulation Class Extension）。UdeCx 负责虚拟 USB Host Controller 和虚拟 USB Device 的系统集成，MyUsbIP 驱动负责描述符、端点队列和 URB 转发。

目标正式支持：

- Windows 10 x64
- Windows 11 x64
- Windows Server 2016+（驱动签名要求与桌面版不同，正式支持需完成 HLK 路线）
- KMDF 1.15+

UdeCx 不支持模拟外部 USB Hub，因此 MyUsbIP NativeWindows 的目标是共享远端叶子设备，而不是把远端 Hub 本身映射为本地 Hub。服务端真实设备可以正常位于多级物理 Hub 后面。

## 网络协议

数据面保持 USB/IP：

- DEVLIST
- IMPORT
- CMD_SUBMIT / RET_SUBMIT
- CMD_UNLINK / RET_UNLINK

MyUsbIP 增加一个私有描述符预取控制请求 `0x80F0/0x00F0`。原因是 UdeCx 创建虚拟设备时必须先拥有完整 Device / Configuration / BOS / String 描述符，而标准 USB/IP IMPORT 响应不携带完整描述符。

## 内核 ABI

用户态和驱动共享 `drivers/windows/shared/myusbip_ioctl.h`，ABI 版本当前为 `0x00010000`。

Exporter IOCTL：

- GET_VERSION
- LIST_DEVICES
- SHARE / UNSHARE
- SUBMIT_URB / CANCEL_URB
- GET_COMPLETION
- GET_DESCRIPTORS

VHCI IOCTL：

- GET_VERSION
- CREATE_PORT / REMOVE_PORT
- GET_PENDING_URB / COMPLETE_URB
- RESET_PORT

所有边界必须校验版本、长度、设备标识、最大传输大小和取消状态。

## 当前实现状态

### 已完成

- .NET NativeServer 网络框架
- DEVLIST / IMPORT / SUBMIT / UNLINK 编解码
- 描述符预取扩展
- Exporter/VHCI 用户态 IOCTL ABI
- WindowsNativeServerBackend
- WindowsExporterTransport
- WindowsNativeClientBackend
- UdeCx VHCI KMDF 驱动入口
- Exporter KMDF 驱动入口
- INF、WDK vcxproj、驱动解决方案骨架
- Daemon `BackendMode=NativeWindows` 切换入口

### 驱动层仍必须完成并实机验证

Exporter：

1. 按设备实例注册过滤上下文与稳定 BusId。
2. 获取真实 Device/Configuration/BOS/String 描述符。
3. 实现共享/独占状态。
4. 把用户态 SUBMIT 转成真实 USB URB 并提交到下层 USB 栈。
5. 维护 Sequence -> WDFREQUEST/URB 映射并支持取消。
6. 处理设备拔出、端口复位、PnP Stop/Remove、睡眠恢复。

VHCI/UdeCx：

1. CREATE_PORT 根据远端描述符创建 UDECXUSBDEVICE。
2. 为 EP0 与配置描述符中的每个端点创建 UDECXUSBENDPOINT。
3. 每个端点绑定 WDF Queue。
4. 在 `EvtIoInternalDeviceControl` 处理 `IOCTL_INTERNAL_USB_SUBMIT_URB`。
5. 把 URB 投递给 GET_PENDING_URB 等待队列。
6. COMPLETE_URB 找回原始 WDFREQUEST、写回数据并完成请求。
7. 完整实现 Cancel/Purge/Reset/PlugOutAndDelete。
8. 支持多个并行 in-flight URB，而不是单 URB 串行隧道。

在以上内核数据路径完成并经过真实硬件测试前，`NativeWindows` 只能视为开发模式，不应宣称已经能生产替代 VirtualHere。

## 首批硬件验收顺序

建议按复杂度逐步打通：

1. 简单 USB HID / 测试设备
2. CH340 1A86:7523
3. 标准 CCID
4. 096E:0303 / 096E:0312 UKey
5. FT ePass 系列
6. USB Mass Storage
7. 高并发多 UKey + 多级 Hub

每个阶段记录枚举、描述符、控制传输、Bulk/Interrupt、取消、拔插、重连、长稳运行结果。
