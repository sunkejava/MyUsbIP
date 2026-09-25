# Changelog

## 1.1.15

本版本针对 Windows + CH340 (1A86:7523) 实机日志中“首次连接正常、Detach 后再次 Attach 卡住约 120 秒、期间整个 DEVLIST 短暂不可用、随后服务端串口设备消失”的问题进行专项修复。

- 根据 2026-09-25 实机日志确认：Detach 成功后 CH340 仍能正常出现在 DEVLIST；真正故障发生于第二次 UsbDk_StartRedirect，最终返回 Win32 1167 (ERROR_DEVICE_NOT_CONNECTED)。
- 修复 CH340 StopRedirect 后仅“看见设备”但驱动栈尚未适合再次捕获的问题：保存完整 PnP InstanceId 与父 USB 节点，释放后使用 DIF_PROPERTYCHANGE / DICS_PROPCHANGE 定向重启 CH340 设备栈。
- 若目标 CH340 叶子 DevNode 暂时不可用，自动回退到父 USB Hub 的 CM_Reenumerate_DevNode，让总线重新枚举子设备。
- StopRedirect 前先精确取消并等待在途 OVERLAPPED URB 回收，减少 CH341SER/UsbDk filter 在旧 I/O 尚未退出时切换驱动栈。
- StartRedirect、StopRedirect、CH340 PnP 恢复与稳定确认统一串行化到 UsbDk 控制平面，避免不同请求同时向 UsbDk 控制设备提交冲突 IOCTL。
- 新增 UsbDk 最近成功枚举快照；Redirect/PnP 恢复进行中，DEVLIST 与设备状态查询改用缓存 + Windows PRESENT 校验，避免单个 CH340 StartRedirect 阻塞时拖垮整个服务端列表。
- UsbDk 控制平面 Busy 时不再额外调用 GetDevicesList/GetConfigurationDescriptor 获取速度和描述符，而使用已缓存元数据。
- 修复多 Hub/多 CH340 场景中 UsbDk 短 InstanceId（例如 "4"）重复导致设备监控 Dictionary 键冲突的问题，监控身份优先使用 BUSID。
- Server JSONL 新增 usbdk.redirect.*、usbdk.release.*、usbdk.ch340.release.*、usbdk.list.cache-fallback 等恢复事件，后续实机回归可直接定位具体阶段。
- Windows/Linux Build、Smoke Tests、self-contained publish、Windows Setup 与 Bundle 构建通过后发布；物理 CH340 的最终时序仍需目标机器复测。

## 1.1.14

本版本对 v1.1.13 进行完整稳定性、安装/发布链路与产物体积审计，不回退 usbip-win2 0.9.8.0，也不改动已验证的标准 USB/IP 主数据协议。

- 修复 UsbDk 会话释放竞态：BUSID 会话占用现在保持到 StopRedirect 与宿主驱动稳定回绑全部完成，避免旧会话清理过程中新的 IMPORT 抢占后又被旧 EndSession 关闭 Redirect。
- JSONL 异常日志新增 Win32Exception NativeErrorCode、十六进制错误码与原生错误消息；UsbDk_StartRedirect/CancelIoEx 等失败可直接定位真实 Win32 错误。
- Windows Server 安装器检测到 UsbDk 驱动与 Runtime Library 健康时跳过重复 MSI 安装，减少覆盖升级导致整机 USB/PnP 重枚举的扰动。
- UsbDk 与 usbip-win2 安装返回 3010（需要重启）时不再继续启动/自检，明确要求重启后重新运行安装器。
- Server 防火墙与启动自检读取实际 NativeServer.Port，不再固定写死 TCP 3240。
- 外部驱动/系统命令增加超时与进程终止保护，避免安装器因 msiexec/第三方安装程序异常永久卡死。
- 安装成功后清理 ProgramData 下的临时 SetupCache，避免长期残留完整 payload/驱动副本。
- Windows 依赖完整性改为仓库预先固定 SHA256：UsbDk 1.0.22 x64 = 91f6f695e1e13c656024e6d3b55620bf08d8835ef05ee0496935ba6bb62466a5；usbip-win2 0.9.8.0 x64 保持 81f426741f7ee2ed991febe24a22daca8400b6ae2f171054e3fb404897e15d39。CI 发布时只下载并验证，不再现场生成 hash。
- Windows CLI、Daemon 与 Setup 启用 .NET single-file 压缩。
- Server/Client Setup 改为分别构建：Server 只嵌 daemon + UsbDk，Client 只嵌 CLI + usbip-win2，不再把两端 payload 与两个驱动重复塞进同一个 EXE。
- Windows 备用 ZIP 不再重复包含 Setup EXE，并且只携带对应角色的离线驱动依赖，显著降低 Release 资产体积。
- CI 增加发布产物体积报告及 Win32 原生错误码 JSONL 回归测试。
- README 同步到当前 StopRedirect + 精确 CancelIoEx + 稳定回绑生命周期。

## 1.1.13

- Detach/TCP 断开后不再长期保留旧 Redirect Handle，执行 UsbDk_StopRedirect 将设备归还 Windows 原生驱动。
- 不再在释放会话时调用 UsbDk_ResetDevice，避免 CH340 等设备因端口复位触发额外 PnP/驱动栈重建。
- StopRedirect 后等待同一物理设备连续稳定枚举三次，再认为宿主驱动回绑完成。
- 保留 v1.1.12 的精确单 URB CancelIoEx 取消策略。

## 1.1.12

- UsbDk Pending Transfer 增加 OVERLAPPED 精确跟踪。
- USB/IP UNLINK/断线取消改为 CancelIoEx(systemHandle, overlapped)，不再用端点级 AbortPipe/ResetPipe 取消单个请求。
- 避免 CH340 同一 Bulk-IN 端点存在多个并发读时，一个 UNLINK 误取消其他读请求导致随机丢反馈。

## 1.1.6

针对 usbip-win2 0.9.8.0 最新正式版进行完整兼容性复核，并修复严格 DEVLIST 解析、命令输出编码、接口元数据和客户端参数适配问题。

- 确认截至 2026-09-13，`vadimgrn/usbip-win2` 最新正式 Release 为 `0.9.8.0`；客户端依赖继续固定官方 `USBip-0.9.8.0-x64.exe`，SHA256=`81f426741f7ee2ed991febe24a22daca8400b6ae2f171054e3fb404897e15d39`。
- 服务端继续实现标准 USB/IP protocol v1.1.1（0x0111），无需安装 usbip-win2；usbip-win2 仅作为 Windows 客户端 UDE/VHCI。
- 修复 DEVLIST 标准协议：每个 `usb_device` 后按 `bNumInterfaces` 写入 4 字节 `usb_interface` 记录，解决 usbip-win2 `list -r` 因继续读取 interface 遇到 EOF 而 ExitCode=1 的问题。
- 服务端从真实 Configuration Descriptor 提取逐接口 Class/SubClass/Protocol，并写入 DEVLIST，不再仅用设备级 class 作为占位。
- MyUsbIP 设备模型增加逐接口元数据，设备状态扩展协议与 CLI 同步展示接口信息。
- 外部 `usbip.exe` stdout/stderr 强制按 UTF-8 解码，修复中文 Windows 下 `操作成功完成` 被显示为 `鎿嶄綔鎴愬姛瀹屾垚` 等乱码。
- Windows 客户端适配 usbip-win2 0.9.8.0 的 `--receive-mode=zero-copy|low-latency`；新增 `ReceiveMode` 配置，默认 `zero-copy`。
- 自定义 USB/IP TCP 端口改用 usbip-win2 明确的全局参数 `--tcp-port`，避免和 attach 子命令的 `-t/--terse` 短参数产生歧义。
- `AttachTimeoutSeconds` 与 `CommandTimeoutSeconds` 改为真正独立 Runner：attach 默认 120 秒，list/port/detach 默认 15 秒。
- Attach 继续使用 `--once`，兼容 usbip-win2 0.9.8.0 UDE 驱动确认成功后返回端口；同时兼容上游当前输出中的 `succesfully/successfully attached to port N` 两种拼写。
- usbip-win2 0.9.8.0 Release 中已知的 `stop_attach_attempts` 指定 location 问题不影响 MyUsbIP，因为当前链路不使用 `attach --stop`。
- 增加 DEVLIST interface 真实字节与设备状态逐接口元数据回归测试；Windows/Linux Build、Smoke tests、Windows Setup 与 Bundle 构建通过后发布。

## 1.1.5

针对 Windows 客户端 Attach 实际成功但旧 `usbip.exe` 进程不退出、`usbip port` 显示 `unknown host / ???`、设备占用方不可见、日志中文被转义，以及客户端依赖升级问题进行修复与增强。

- Windows 客户端正式从 `cezanne/usbip-win` 切换到 `vadimgrn/usbip-win2 0.9.8.0`，使用官方 x64 安装包与 UDE/VHCI 驱动。
- `myusbip client attach` 改为调用 usbip-win2 并使用 `--once`，Attach 成功后直接解析 `successfully attached to port N`，避免设备已经挂载成功但 CLI 长时间不退出并最终超时。
- Windows 客户端未显式配置 `UsbipWinPath` 时优先调用 `C:\Program Files\USBip\usbip.exe`，避免覆盖升级后旧 CMD/PATH 仍命中遗留的 cezanne `usbip.exe`。
- 安装器升级时自动检测并卸载旧 usbip-win VHCI，清理旧客户端依赖目录，再安装/复用 usbip-win2；安装完成后执行 `usbip -V` 与 `usbip port` 自检。
- usbip-win2 的 `port` 信息由 VHCI 直接维护并输出 hostname、TCP service、busid、remote bus/dev、serial、receive mode，不再依赖旧版客户端易丢失的本地 connection record。
- 服务端 USB 设备模型补齐 `BusNumber`、`DeviceNumber`、`Speed`、`bcdUSB`、`bcdDevice`、Device Class/SubClass/Protocol、配置数量、当前配置值、接口数量等元数据。
- 新增 MyUsbIP 设备状态管理扩展协议，在不破坏标准 USB/IP DEVLIST/IMPORT/SUBMIT/UNLINK 的前提下额外查询设备完整元数据与当前连接客户端。
- 服务端记录每个活动 IMPORT 的客户端 IP、SessionId 与 ConnectedAt，并在设备列表中标记 `Attached` 及 `ClientAddress`。
- `myusbip client list <host>` 合并标准 USB/IP 设备列表与 MyUsbIP 状态扩展，显示当前设备由谁连接、连接时间、会话 ID、总线/设备号、速度、USB/设备版本、类信息、序列号和实例 ID。
- JSONL 日志使用 UTF-8 与 `UnsafeRelaxedJsonEscaping`，中文内容直接写入文件，不再转换为 `\uXXXX`，方便直接打开排查。
- 增加设备状态协议与中文日志回归测试，防止后续重构再次出现元数据或中文可读性退化。
- 修复 `MyUsbIP.Platform` 缺少 `MyUsbIP.Protocol` 项目引用导致的 CI 编译失败。
- Windows/Linux Build、Smoke tests、Windows 双击安装包构建与 Windows Bundle 构建均已通过后进入正式 v1.1.5 发布流程。

## 1.1.4

针对 Windows 客户端覆盖升级时重复安装 VHCI、`myusbip client attach` 15 秒超时，以及 CH340 Detach 后无法再次枚举/连接的问题进行修复。

- 客户端安装器新增 VHCI/UDE 健康检测：现有 `usbip.exe port` 正常时直接复用已安装驱动，不再每次覆盖升级都重复执行 `usbip install`；仅首次安装或驱动自检失败时才安装/修复 VHCI。
- 客户端命令超时拆分：普通 list/port/detach 默认继续使用 `CommandTimeoutSeconds=15`，Attach 使用独立 `AttachTimeoutSeconds=120`，避免服务端 UsbDk Redirect 较慢时被客户端过早终止。
- 客户端超时诊断进一步保留命令、stdout/stderr、耗时等结构化日志，便于区分 VHCI、本地 CLI 与服务端 IMPORT/Redirect 阶段。
- 修正 CH340 等 USB 串口设备的 UsbDk 生命周期策略：USB/IP Detach 后不再执行 `UsbDk_StopRedirect -> UsbDk_StartRedirect` 循环，改为保留 Redirect 句柄至服务端生命周期结束。
- USB/IP 会话结束时先无条件释放 BUSID 会话所有权，再执行 `UsbDk_ResetDevice` 清理设备状态，防止 Reset 异常导致设备永久处于 Busy。
- 第一次 IMPORT 成功创建 Redirect 后不立即 ResetDevice，减少首次枚举额外扰动；Reset 仅用于会话结束清理。
- 服务端 DEVLIST 现在合并 UsbDk 当前枚举结果与已 Redirect 设备快照；即使设备在 `UsbDk_GetDevicesList` 中暂时消失，仍可继续对客户端显示。
- 已 Redirect 设备缓存 DeviceDescriptor、ConfigurationDescriptor、Endpoint 类型及 UsbDk 标识，后续再次 Attach 不再依赖设备重新出现在 UsbDk 原生枚举列表。
- 继续保持同一 BUSID 单活动 USB/IP 会话约束，并修复会话结束后旧锁未正确释放导致 `native.import.rejected` 的场景。
- Windows/Linux Build 与 Smoke tests 已通过后进入正式 v1.1.4 发布流程。

## 1.1.3

针对 CH340 `1A86:7523` 首次远程 Attach/串口收发正常、Detach 后第二次 Attach 失败的问题，修正 UsbDk 会话清理，并完善服务端/客户端可持久化诊断日志。

- USB/IP 会话结束时不再保留旧 UsbDk Redirect/Handle；Detach、TCP 断开或异常结束后执行 `UsbDk_StopRedirect`，下一次 Attach 使用新的 Redirect/Handle，避免 CH340 endpoint/设备状态残留。
- USB/IP TCP 连接结束时主动取消所有尚未完成的 URB，触发 UsbDk `AbortPipe/ResetPipe`，避免阻塞在 `GetOverlappedResult` 的请求阻止会话释放。
- UsbDk Pending Request 从单独 `Sequence` 改为 `(BusId, Sequence)` 唯一定位，避免多个 USB/UKey 同时连接时相同 sequence 冲突并取消错设备。
- 服务端增加 IMPORT 请求、接受/拒绝、会话关闭/释放、URB 失败、UNLINK 等结构化 JSONL 日志；可选记录每个成功 URB。
- 客户端 MyUsbIP CLI 增加 Attach/Detach、`usbip.exe` ExitCode、stdout/stderr、超时等 JSONL 日志。
- 服务端默认日志目录：`C:\ProgramData\MyUsbIP\ServerLogs`；客户端默认日志目录：`C:\ProgramData\MyUsbIP\ClientLogs`。
- 日志目录、启停、保留天数可分别通过 `appsettings.json` 与 `clientsettings.json` 配置，默认保留 30 天。
- 错误日志不再直接序列化完整 `.NET Exception`，改为安全记录异常类型、消息、堆栈、HResult 与 InnerException，避免异常事件本身因 JSON 序列化失败而丢失。
- Windows Setup 覆盖升级时保留用户已经修改的 `appsettings.json` / `clientsettings.json`，不会重置日志策略。
- 注意：直接手工运行第三方 `usbip.exe` 的客户端命令无法被 MyUsbIP CLI 记录；需要完整客户端诊断链路时请使用 `myusbip client attach/detach`。

该版本的 CH340 重连修复仍需要目标 Windows + CH340 实机再次验证；构建通过不等同于硬件回归通过。

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
- Setup 内部自动完成依赖安装、驱动安装、防火墙、开机启动、PATH 和自检。
- 最终 Windows Bundle 不携带 `.ps1/.cmd/.bat`，脚本仅作为开发者/CI 工具保留。
