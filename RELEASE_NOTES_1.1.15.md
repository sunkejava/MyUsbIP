# MyUsbIP v1.1.15

本版本专门修复 CH340 (1A86:7523) 在 Windows UsbDk 服务端中“Detach 后第二次 Attach 异常，并短期拖累整个 DEVLIST”的实机问题。

## 本次日志确认的故障链

1. 第一次 CH340 IMPORT 正常成功。
2. 客户端 detach 成功，释放后 CH340 仍可连续两次正常出现在服务端 DEVLIST。
3. 第二次 IMPORT 进入 UsbDk_StartRedirect 后卡住约 120 秒，最终返回 Win32 1167 (ERROR_DEVICE_NOT_CONNECTED)。
4. StartRedirect 卡住期间 UsbDk_GetDevicesList 返回 Win32 5，导致其它客户端也暂时无法获取服务端设备列表。
5. Redirect 失败后 CH340 从服务端枚举中消失。

因此根因不是 usbip-win2 detach 命令本身，而是 CH340 从 UsbDk Redirect 归还 CH341SER 后，PnP/驱动栈虽然重新可见，但没有处于可再次被 UsbDk 捕获的稳定状态。

## 修复内容

- CH340 StopRedirect 后保存并使用完整 Windows PnP InstanceId。
- 使用 DIF_PROPERTYCHANGE + DICS_PROPCHANGE 定向停止/重启 CH340 设备栈。
- 叶子设备恢复失败时回退父 USB Hub CM_Reenumerate_DevNode。
- StopRedirect 前精确取消并等待残留 URB 回收。
- StartRedirect / StopRedirect / PnP 恢复统一串行化。
- Redirect/恢复期间 DEVLIST/status 使用缓存与 Windows PRESENT 状态，不再进入被占用的 UsbDk 控制队列。
- 缓存速度/描述符元数据，Busy 窗口不执行额外 UsbDk 描述符 IOCTL。
- 多 Hub CH340 监控优先用 BUSID，避免短 InstanceId 冲突。
- 新增完整 UsbDk Redirect/Release/Recovery JSONL 日志。

## 实机复测建议

按同一 CH340 连续执行至少 10 轮：

1. client list
2. client attach
3. 打开远程 COM 口并完成实际收发
4. client detach
5. 立即连续执行 client list（1 秒、3 秒、5 秒）
6. 再次 client attach

预期：DEVLIST 全程可响应；CH340 不从服务端永久消失；第二次及后续 Attach 不再进入约 120 秒的 UsbDk_StartRedirect 卡死。

Windows 客户端继续使用 usbip-win2 0.9.8.0，没有回退驱动版本。
