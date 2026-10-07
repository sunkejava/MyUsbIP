# CH340 二次 attach：原因、修复与复测

## 已确认的 v1.1.17 日志

| 时间 | 服务端事件 | 结论 |
| --- | --- | --- |
| 10:41:17–10:41:19 | 首次 Redirect 成功 | 第一次捕获可以完成 |
| 10:46:37 | StopRedirect 开始 | 客户端 detach 返回只代表本地分离 |
| 10:46:47 | recovery-failed，stable=false，保存的叶子 PnP 节点不存在 | 父节点 CM_Reenumerate_DevNode 返回成功，但设备没有恢复 |
| 10:46:48 | native.session.released | 原实现误将失败恢复视为释放完成，文字还错误声称保留 Redirect |
| 10:47:08–10:49:26 | 第二次 StartRedirect，最后 Win32 1167 | 旧身份进入 UsbDk 捕获等待，之后又执行恢复等待 |

软件层已确认的问题是恢复失败没有传播、旧原生快照和过弱的身份检查可以继续授权捕获。日志不能单独证明 CH341SER、UsbDk filter、USB Hub 固件中究竟哪一层导致叶子节点消失。此次修复阻断该错误路径，并提供已验证目标端口的恢复手段；不以加长客户端超时作为修复。

## v1.1.18 行为

捕获前读取新的 UsbDk 快照，解析唯一的当前完整 PnP InstanceId。CH340 必须在连续三次检查中保持完整身份、原生 BUSID/短身份一致，且 Configuration Manager 报告 DN_STARTED、没有 DN_HAS_PROBLEM。缺失或歧义时拒绝调用 StartRedirect。

捕获前还保存父 Hub 的设备接口路径、CM_DRP_ADDRESS 和驱动键，并用 Hub 对应端口的驱动键核对地址。StopRedirect 必须返回成功，否则保留失败记录并禁止复用句柄。结束会话后首先定向重启叶子设备栈，必要时重新枚举父节点；等待约 8 秒仍未就绪时，尝试循环已保存并核实的单个物理端口，再等待约 8 秒。恢复等待需要完整 PnP 身份及新的 UsbDk 枚举连续稳定。

端口恢复前再次检查当前驱动键。驱动键变化时拒绝操作；查键失败也不会直接当成空端口，只有独立 Hub 连接状态查询确认 NoDeviceConnected 才允许恢复原端口。没有保存可靠的端口信息时拒绝猜测。原生 Windows/UsbDk 调用本身是同步调用，这些准备阶段等待预算不能强制中断内核调用。

恢复失败记录 usbdk.ch340.release.recovery-failed 和 native.session.release.failed，不会再记录成功释放。新 attach 仍独立检查当前设备状态，待宿主驱动实际恢复后可重试。恢复导致 BUSID 变化时必须重新 list 获取 BUSID。

DEVLIST 繁忙时读取缓存并做物理存在性检查。原生枚举、描述符查询、Redirect 建立/释放和非活动句柄清理由同一控制平面门保护，避免查询插入正在恢复的 UsbDk 控制队列。速度直接使用已有快照，取消额外枚举。

## 实机验收

1. 升级服务端到 v1.1.18，确认正在运行的是新 Daemon；可使用 Server 在线或离线安装器。既有配置继续保留。
2. 若升级前 CH340 已经在设备管理器中消失，先重新插拔该 CH340，使宿主系统恢复正常枚举，再开始测试。这也使新版本能够在首次捕获前保存已验证端口。
3. client list 获取当前 BUSID；首次 attach 后确认客户端 COM 端口可打开，串口收发正常。
4. detach，观察服务端 usbdk.release.stopped → usbdk.ch340.release.recovered → native.session.released。客户端 detach 命令完成不等于服务端清理完成。
5. 再次 list/attach，确认第二次能进入 usbdk.redirect.ready，串口收发正常；连续重复至少 10 次，并在恢复期间检查其他设备 list/status 仍可响应。
6. 若失败，保留服务端从首次 usbdk.redirect.start 到第二次失败的完整 JSONL，以及客户端对应日志。重点查看 hasVerifiedRecoveryPort、recoveryConnectionIndex、stable、recovery 和完整 PnP 身份；结合设备管理器问题码判断进一步的驱动/Hub 原因。

自动测试覆盖恢复策略、失败传播、取消、唯一身份、PnP 状态与端口保护，没有执行真实 Hub IOCTL 或物理 CH340。Windows/Linux 编译和发布验证不能替代上述实机验收。
