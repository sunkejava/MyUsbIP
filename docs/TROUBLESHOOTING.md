# 排障指南

## 建议排查顺序

MyUsbIP 按完整连接链路排查：

1. 物理 USB 是否被主机操作系统识别。
2. `server list` 是否能看到设备。
3. 设备是否已 Share。
4. TCP 3240 是否从客户端可达。
5. `client list <host>` 是否能看到远端设备。
6. Attach 命令是否成功。
7. VHCI 是否创建本地虚拟端口。
8. 客户端操作系统是否完成 PnP/驱动枚举。
9. 最终业务程序是否能打开设备。

## 日志定位

默认日志为 JSON Lines，每一行均可独立解析。

重点字段：

- `Timestamp`
- `Name`
- `Level`
- `TraceId`
- `BusId`
- `Host`
- `Message`

建议先按 `TraceId` 聚合一次 Share/Attach 操作的完整日志。

## 常见事件含义

### `process.start` 后长时间没有正常结果

底层 usbip/usbipd 工具或驱动可能卡住。进程执行器到达超时后会 Kill 整个进程树并抛出 `TimeoutException`。

### `client.remote.list.failed`

优先检查：

- 服务端是否启动 USB/IP 服务。
- TCP 3240 防火墙。
- IP 路由/VPN。
- 设备是否已经 Share。

### `client.attach.failed`

优先检查：

- Windows 客户端 VHCI 驱动是否存在。
- Linux 是否加载 `vhci_hcd`。
- 设备是否已经被其他客户端占用。
- UKey 驱动是否支持被虚拟 USB 总线重新枚举。

### `device.removed` / `device.added` 高频交替

通常不是网络问题，而是服务端物理 USB 链路不稳定。重点检查 Hub 供电、线材、USB reset、过流、设备固件和主控稳定性。

## 096E:0303 / 0312 UKey

如果操作系统设备管理器中出现多个智能卡设备，但 USB/IP 只枚举部分设备：

1. 先按物理 BusId 区分设备，而不是只按 VID/PID。
2. 记录每个设备的 InstanceId/序列号（如果设备提供）。
3. 检查 Windows 智能卡/UMDF 驱动是否将多个接口聚合成单个功能设备。
4. 检查是否有设备处于端口重置失败、Code 10/22/43 等 PnP 异常状态。
5. 在 USB/IP 共享前确认设备已经完成本地枚举。

## CH340

CH340 高频掉线、COM 号变化通常意味着设备在 USB 层发生了重新枚举。MyUsbIP 自动恢复只能恢复网络映射，不能修复物理供电或 USB 控制器 reset 问题。

建议同时监控：

- USB BusId 是否变化。
- Windows InstanceId。
- COM PortName。
- Hub 端口电流。
- USB 端口 reset/重新枚举次数。

## 推荐告警

生产环境建议对以下事件告警：

- `process.end` 对应操作耗时异常升高。
- `connection.health.failed` 连续出现。
- `connection.recovery.stopped`。
- `autoshare.failed`。
- 设备 5 分钟内频繁 Added/Removed。
