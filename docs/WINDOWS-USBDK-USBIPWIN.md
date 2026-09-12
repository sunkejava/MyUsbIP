# Windows 生产路线：UsbDk Server + usbip-win VHCI Client

## 目标

该模式用于 Windows -> Windows USB 网络共享：

```text
Windows Server
  Real USB Device
      -> UsbDk signed capture driver
      -> UsbDkHelper.dll
      -> MyUsbIP.UsbDk
      -> MyUsbIP.NativeServer (TCP 3240)
      -> network
      -> usbip.exe
      -> usbip-win VHCI
      -> Windows USB/PnP stack
      -> vendor driver / CCID / CH340 / UKey middleware
Windows Client
```

服务端不再安装 `usbipd-win`，MyUsbIP 自己实现标准 USB/IP 服务。客户端暂时不维护自研 VHCI，直接使用 usbip-win 的 VHCI 与 `usbip.exe`。

## 服务端依赖

1. Windows 10/11 x64。
2. 安装 UsbDk 驱动与 `UsbDkHelper.dll`。
3. `UsbDkHelper.dll` 必须可被 `myusbipd.exe` 加载（系统目录或程序搜索路径）。
4. 允许 TCP 3240 入站。
5. MyUsbIP Daemon 使用管理员/LocalSystem 权限运行。

推荐配置：

```json
{
  "BackendMode": "UsbDkUsbipWin",
  "UsbipWinPath": "usbip.exe",
  "NativeServer": {
    "Enabled": true,
    "ListenAddress": "0.0.0.0",
    "Port": 3240
  }
}
```

### UsbDk 工作方式

`UsbDk_GetDevicesList` 用于枚举设备；当设备被共享时，MyUsbIP 调用 `UsbDk_StartRedirect` 将真实 USB 从正常 Windows 功能驱动栈切换到 UsbDk redirector，并持续持有返回句柄。

网络请求到达后：

```text
USB/IP CMD_SUBMIT
  -> UsbDkExportTransport
  -> UsbDkDeviceManager
  -> UsbDk_ReadPipe / UsbDk_WritePipe
  -> physical USB
  -> USB_DK_TRANSFER_RESULT
  -> USB/IP RET_SUBMIT
```

当前实现支持：

- Control transfer（EP0）
- Bulk transfer
- Interrupt transfer
- Isochronous 类型识别与 UsbDk API 转发入口
- Configuration Descriptor 枚举
- Endpoint 类型解析
- Device reset / pipe abort/reset API 基础能力
- USB/IP UNLINK -> UsbDk AbortPipe/ResetPipe

### BusId

USB/IP 的 BusId 最长只有 32 字节。MyUsbIP 根据 UsbDk `FilterId` 与 `Port` 生成：

```text
XXXXXXXX-XXXXXXXX
```

例如：

```text
00000002-00000004
```

客户端应使用 `myusbip server list` / `usbip.exe list -r <server>` 返回的 BusId，不要自行推算。

## 客户端依赖

客户端安装 usbip-win VHCI，然后确保 `usbip.exe` 可执行。

建议优先使用 UDE 版 VHCI；如果目标机器/设备兼容性出现问题，再测试 WDM 版。

常见命令：

```cmd
usbip.exe list -r 192.168.1.100
usbip.exe attach -r 192.168.1.100 -b 00000002-00000004
usbip.exe port
usbip.exe detach --port=0
```

MyUsbIP 正常运行时无需业务程序直接执行这些命令，`WindowsUsbIpBackend` 会代为完成。

> usbip-win 上游目前仍将自身描述为非生产就绪项目。正式部署必须针对实际 UKey/CCID/CH340 型号进行压力、掉线、重连、休眠恢复与蓝屏测试。驱动签名状态取决于你实际采用的 usbip-win 构建/发行包，不要假设 GitHub 任意 release 都具备微软正式生产签名。

## 推荐验证设备顺序

优先从低风险设备逐步验证：

1. 普通 USB HID/鼠标（基础 Control + Interrupt）
2. U 盘（Bulk）
3. CH340（Control + Bulk）
4. 标准 CCID 智能卡
5. `096E:0303`
6. `096E:0312`
7. FT ePass3000GM / 厂商自定义 UKey

每种设备至少验证：

- 初次 Attach
- 连续通信 1 小时/24 小时
- 服务端 USB 拔插
- Hub 端口断电/重新上电
- 客户端 Detach/Attach
- TCP 瞬断
- 服务端进程重启
- 客户端进程重启
- Windows 休眠/唤醒
- 同型号多设备同时在线

## 当前已知边界

### UNLINK

UsbDk Helper 公共 API 提供 `AbortPipe`，但没有按单个 USB/IP Sequence 暴露取消接口。因此当前实现收到 `CMD_UNLINK` 时按端点 Abort，并执行 ResetPipe。这可能同时取消同一端点上其他尚未完成的请求。

后续如需精确取消，可使用 `UsbDk_GetRedirectorSystemHandle` 配合用户态保存的 OVERLAPPED 与 `CancelIoEx`，将 USB/IP Sequence 精确映射到单个 Windows I/O。

### Isochronous

当前已经识别 Isochronous Endpoint，但尚未把 USB/IP ISO packet descriptors 与 `USB_DK_ISO_TRANSFER_RESULT` 完整双向映射。UKey/CCID/CH340 通常不依赖 Isochronous；摄像头、USB 音频等设备需要完成这一层后再列为正式支持。

### 字符串/BOS 描述符

usbip-win VHCI 会通过标准 EP0 Control Request 从远端真实设备读取描述符，因此 UsbDk Server 不依赖 MyUsbIP 私有 Descriptor Prefetch 扩展。当前 `IUsbDescriptorProvider` 主要保留给实验性自研 UdeCx 客户端。

## 安全

标准 USB/IP TCP 3240 本身没有适合公网暴露的身份认证与加密机制。生产建议仅放在：

- 内网/VLAN
- WireGuard/Tailscale/ZeroTier
- IPsec/VPN
- Windows Firewall 白名单

MyUsbIP 的权限、Lease 与审计属于控制面；USB/IP 3240 保持标准数据面兼容。

## 许可证

UsbDk 上游公开代码使用 Apache License 2.0。实际安装、再分发第三方二进制时仍应保留其许可证与版权声明，并验证你采用发行包的来源与数字签名。
