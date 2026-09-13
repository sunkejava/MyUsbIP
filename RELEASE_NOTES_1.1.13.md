# MyUsbIP v1.1.13

- 修复客户端释放 USB/IP 会话后服务端设备偶发无法重新枚举的问题。
- 释放会话时不再调用 UsbDk_ResetDevice，避免端口复位触发 PnP/驱动栈重建。
- USB/IP detach 后真正调用 UsbDk_StopRedirect，把设备归还 Windows 原生驱动栈。
- StopRedirect 后等待同一物理设备连续稳定枚举三次，再认为宿主驱动回绑完成，降低 CH340/USB 串口设备立即二次 Redirect 失败概率。
- 保留 v1.1.12 的单 URB CancelIoEx 精确取消，避免 CH340 Bulk-IN 接收随机丢包。
