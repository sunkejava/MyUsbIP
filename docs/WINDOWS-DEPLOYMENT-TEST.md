# MyUsbIP Windows 服务端 / 客户端部署与测试流程

本文针对当前推荐架构：

```text
Windows 服务端
真实 USB / UKey
  -> UsbDk
  -> UsbDkHelper.dll
  -> MyUsbIP.UsbDk
  -> MyUsbIP.NativeServer
  -> TCP 3240 / USB-IP

Windows 客户端
  -> usbip.exe
  -> usbip-win VHCI
  -> Windows PnP
  -> 设备厂商驱动
```

## 1. 推荐系统范围

建议生产测试范围：

- Windows 10 x64 22H2
- Windows 11 x64
- 服务端与客户端均使用管理员权限安装驱动
- MyUsbIP 发布使用 win-x64 自包含版本
- 服务端 TCP 3240 只允许可信局域网/VPN 网段访问

不建议将 Windows 7/8 作为第一阶段生产目标。UsbDk 本身兼容较老 Windows，但当前 MyUsbIP、.NET 10、usbip-win VHCI 的整体兼容性和驱动签名部署成本都更适合 Windows 10/11。

---

# 2. 依赖文件管理原则

项目使用：

```text
config/dependencies.windows.json
scripts/windows/Install-Dependencies.ps1
```

依赖清单记录：

- 固定版本
- 固定文件名
- 上游项目地址
- 固定二进制下载地址
- SHA256
- 安装/解压目录
- 安装后必须存在的文件

## 2.1 为什么不直接下载 latest

生产现场禁止：

```text
https://github.com/.../releases/latest/...
```

原因：

1. 上游发布新版本后内容可能变化。
2. 驱动版本升级可能引入 BSOD、PnP、签名或兼容性变化。
3. 同一套 MyUsbIP 版本必须能够复现完全相同的依赖环境。
4. 方便出现故障时快速回退。

因此每个 MyUsbIP Release 应绑定一份固定依赖清单。

## 2.2 推荐目录

部署包建议：

```text
MyUsbIP-1.0.x-win-x64\
├─ Server\
│  ├─ myusbipd.exe
│  ├─ appsettings.json
│  └─ *.dll
├─ Client\
│  ├─ myusbip.exe
│  ├─ myusbipd.exe
│  └─ *.dll
├─ Dependencies\
│  ├─ Server\
│  │  └─ UsbDk_1.0.22_x64.msi
│  └─ Client\
│     └─ usbip-win-vhci-x64\
│        ├─ usbip.exe
│        ├─ usbip_vhci*.sys
│        ├─ usbip_vhci*.inf
│        ├─ usbip_vhci*.cat
│        └─ certificate files when required
├─ config\
│  └─ dependencies.windows.json
└─ scripts\windows\
```

生产环境优先直接随 MyUsbIP Release 一起发布已验证的依赖文件，而不是现场联网下载。

---

# 3. 获取 UsbDk 服务端依赖

官方项目：

```text
https://github.com/daynix/UsbDk
```

UsbDk 安装器会安装 UsbDk 驱动、运行库，并把 UsbDk 注册到 USB Class UpperFilters。

建议固定一个已经完成 UKey 实测的 x64 MSI 版本，例如项目清单当前预留：

```text
UsbDk_1.0.22_x64.msi
```

实际发布前必须：

1. 从可信官方来源下载。
2. 计算 SHA256：

```powershell
Get-FileHash .\UsbDk_1.0.22_x64.msi -Algorithm SHA256
```

3. 把下载地址和哈希写入：

```text
config/dependencies.windows.json
```

4. 将同一份 MSI 缓存到内部文件服务器或直接跟随 MyUsbIP Release 发布。

UsbDk 官方文档说明安装过程中会创建 `usbdk.sys` 服务、注册 USB Class UpperFilters，并触发 USB 设备重新枚举。因此安装 UsbDk 时不应有重要 USB 业务正在运行。

安装：

```cmd
msiexec /i UsbDk_1.0.22_x64.msi /l*v usbdk-install.log
```

静默安装：

```cmd
msiexec /i UsbDk_1.0.22_x64.msi /qn /norestart /l*v usbdk-install.log
```

验证：

```cmd
sc query usbdk
```

```cmd
reg query HKLM\SYSTEM\CurrentControlSet\Control\Class\{36fc9e60-c465-11cf-8056-444553540000} /v UpperFilters
```

还应确认：

```text
C:\Program Files\UsbDk Runtime Library\UsbDkHelper.dll
```

存在。

---

# 4. 获取 usbip-win VHCI 客户端依赖

上游项目示例：

```text
https://github.com/cezanne/usbip-win
```

usbip-win 客户端核心需要：

```text
usbip.exe
VHCI .sys
VHCI .inf
VHCI .cat
证书文件（若该版本是测试签名）
```

项目存在 WDM 与 UDE 两类 VHCI 实现。不要在一批生产机器上混装多个不同版本。

建议选定一套后固定：

```text
MyUsbIP 1.0.x
  -> usbip-win commit/release X
  -> VHCI WDM 或 UDE 固定一种
```

如果上游包是测试签名驱动，通常需要：

```cmd
bcdedit /set TESTSIGNING ON
```

随后重启。

测试证书需安装到 Local Computer：

```text
Trusted Root Certification Authorities
Trusted Publishers
```

生产环境如果无法接受 TESTSIGNING，则必须改用正式签名的 VHCI 构建，不能依赖测试签名包。

安装 VHCI 的常见方式：

```cmd
usbip.exe install
```

部分版本支持：

```cmd
usbip.exe install -u
```

安装 UDE，或：

```cmd
usbip.exe install -w
```

安装 WDM。

具体参数必须以实际固定版本为准，不要跨版本混用安装命令。

---

# 5. 自动下载、缓存和 SHA256 校验

先维护：

```text
config/dependencies.windows.json
```

填写：

```json
{
  "downloadUrl": "固定版本真实下载地址",
  "sha256": "对应文件的 SHA256"
}
```

仅下载服务端依赖：

```powershell
.\scripts\windows\Install-Dependencies.ps1 -Role Server
```

下载并安装服务端依赖：

```powershell
.\scripts\windows\Install-Dependencies.ps1 -Role Server -Install
```

客户端下载并解压：

```powershell
.\scripts\windows\Install-Dependencies.ps1 -Role Client -Install
```

强制重新下载：

```powershell
.\scripts\windows\Install-Dependencies.ps1 -Role All -Install -ForceDownload
```

脚本规则：

- 下载前要求 manifest 中存在明确 URL。
- 必须存在 SHA256。
- 下载后强制校验。
- 哈希不一致立即删除文件并退出。
- 已下载文件优先使用本地缓存。
- 不允许自动追踪 latest。

---

# 6. 服务端部署

假设安装目录：

```text
C:\Program Files\MyUsbIP\
```

## 6.1 安装 UsbDk

以管理员权限执行：

```powershell
.\scripts\windows\Install-Dependencies.ps1 -Role Server -Install
```

如果提示重启，先重启机器。

## 6.2 部署 MyUsbIP Server

将 Windows win-x64 发布产物复制到：

```text
C:\Program Files\MyUsbIP\
```

确保至少包含：

```text
myusbipd.exe
appsettings.json
MyUsbIP.Abstractions.dll
MyUsbIP.Protocol.dll
MyUsbIP.Server.dll
MyUsbIP.NativeServer.dll
MyUsbIP.UsbDk.dll
```

如果系统无法从 UsbDk Runtime Library 搜索到 `UsbDkHelper.dll`，可以将经过校验的同版本 `UsbDkHelper.dll` 放到 `myusbipd.exe` 同目录。

## 6.3 服务端配置

推荐：

```json
{
  "BackendMode": "UsbDkUsbipWin",
  "LogDirectory": "logs",
  "CommandTimeoutSeconds": 15,
  "MonitorIntervalSeconds": 2,
  "NativeServer": {
    "Enabled": true,
    "ListenAddress": "0.0.0.0",
    "Port": 3240
  },
  "AutoShareRules": [],
  "ManagedConnections": [],
  "Reconnect": {
    "Enabled": true,
    "CheckIntervalSeconds": 5,
    "RetryDelaySeconds": 3,
    "MaxConsecutiveFailures": 0
  }
}
```

## 6.4 防火墙

仅测试：

```cmd
netsh advfirewall firewall add rule name="MyUsbIP USB-IP 3240" dir=in action=allow protocol=TCP localport=3240
```

生产环境应该限定客户端网段，例如仅允许业务 VLAN/VPN 地址。

## 6.5 启动

首次联调建议前台运行：

```cmd
cd /d "C:\Program Files\MyUsbIP"
myusbipd.exe appsettings.json
```

观察：

```text
logs\myusbip-yyyyMMdd.jsonl
```

确认服务监听：

```powershell
Get-NetTCPConnection -State Listen -LocalPort 3240
```

## 6.6 服务端自动自检

```powershell
.\scripts\windows\Test-Server.ps1 -MyUsbIpDir "C:\Program Files\MyUsbIP"
```

检查内容：

- 管理员权限
- UsbDk 服务状态
- UpperFilters
- UsbDkHelper.dll
- MyUsbIP 文件
- TCP 3240
- 防火墙规则

---

# 7. 客户端部署

## 7.1 安装 usbip-win VHCI

将固定版本 VHCI 文件复制到：

```text
C:\Program Files\MyUsbIP\usbip-win\
```

如果使用测试签名：

1. 安装对应证书到 Local Computer。
2. 开启 TESTSIGNING。
3. 重启。
4. 管理员 CMD/PowerShell 安装 VHCI。

例如：

```cmd
usbip.exe install
```

## 7.2 验证驱动

设备管理器中应出现 USB/IP VHCI 对应虚拟控制器/根设备。

命令检查：

```powershell
Get-CimInstance Win32_SystemDriver | Where-Object {
    $_.Name -match 'usbip|vhci' -or $_.DisplayName -match 'USB/IP|VHCI'
}
```

## 7.3 网络连通测试

```powershell
Test-NetConnection 192.168.1.100 -Port 3240
```

必须：

```text
TcpTestSucceeded : True
```

## 7.4 获取服务端设备

```cmd
usbip.exe list -r 192.168.1.100
```

预期能看到服务端 UsbDk 枚举并导出的设备，例如：

```text
096e:0303
096e:0312
1a86:7523
```

以及对应 BusId。

## 7.5 Attach

```cmd
usbip.exe attach -r 192.168.1.100 -b <BusId>
```

然后：

```cmd
usbip.exe port
```

确认设备已绑定到某个 VHCI Port。

## 7.6 Windows PnP 检查

Attach 后检查：

1. 设备管理器是否新增设备。
2. VID/PID 是否与服务端真实设备一致。
3. 原厂设备驱动是否成功加载。
4. 是否出现黄色叹号。
5. 智能卡设备是否进入 Smart Card Reader/USB CCID 类。
6. CH340 是否正确生成 COM 口。

客户端仍需要安装对应厂商驱动。VHCI 只负责把远端 USB 虚拟成本地 USB，并不能代替 UKey/CH340 厂商驱动。

## 7.7 客户端自动自检

仅检查：

```powershell
.\scripts\windows\Test-Client.ps1 -ServerHost 192.168.1.100
```

同时 Attach 指定设备：

```powershell
.\scripts\windows\Test-Client.ps1 `
    -ServerHost 192.168.1.100 `
    -BusId <BusId>
```

---

# 8. 第一轮正式联调测试顺序

建议不要一开始直接测试大量 Hub/UKey。

## 阶段 1：单设备

依次测试：

```text
普通 U 盘
CH340
096E:0303
096E:0312
FT ePass
```

每个设备执行：

```text
服务端插入
 -> UsbDk 枚举
 -> MyUsbIP list 可见
 -> 客户端 usbip list 可见
 -> attach
 -> Windows PnP 完成
 -> 业务软件使用
 -> detach
 -> 服务端恢复本地状态
```

每一步记录耗时。

## 阶段 2：重复连接

单设备循环：

```text
attach
业务请求
业务退出
detach
```

至少 100 次。

观察：

- VHCI Port 是否泄漏
- UsbDk Redirect 是否释放
- 服务端设备能否再次连接
- PnP 是否产生幽灵设备
- COM 号是否变化
- CCID Reader 名称是否异常

## 阶段 3：异常测试

连接期间测试：

```text
拔设备
Hub 端口断电
服务端重启 MyUsbIP
客户端结束 usbip.exe
网络断开 5~30 秒
客户端重启
服务端重启
```

要求：

- 不蓝屏
- 不死锁
- 不永久占用 UsbDk Redirect
- 再次 Attach 能恢复

## 阶段 4：多设备

先：

```text
2 台
4 台
7 台
```

再逐步增加到：

```text
14 / 28 / 49
```

不要直接 49 台同时上电。

重点观察：

- UsbDk 枚举耗时
- DEVLIST 耗时
- 同 VID/PID BusId 唯一性
- URB 并发
- Hub 带宽
- Hub 供电
- Windows PnP 枚举稳定性

---

# 9. UKey 专项测试

针对 096E:0303 / 0312：

测试：

```text
Windows 设备管理器识别
WUDF/CCID 驱动加载
智能卡服务
证书读取
PIN 输入
登录
签名
连续签名
长时间空闲后再次调用
```

重点关注：

- Interrupt IN 长轮询
- Bulk IN/OUT
- Control Transfer
- USB/IP UNLINK
- 设备 reset

针对你之前 VirtualHere 出现的“设备管理器多个 Reader、VH 只显示一个”的场景，在 MyUsbIP 下应重点验证每个 UsbDk `FilterId-Port` BusId 是否唯一，以及同 VID/PID 多设备能否分别 Attach。

---

# 10. CH340 专项测试

Attach 后：

```text
设备管理器 -> COMx
```

确认：

- COM 口生成正常
- 9600/8/N/1 通信正常
- 连续发送响应正常
- detach/attach 后 COM 号是否稳定
- Hub 断电重上电后是否恢复

建议跑至少 24h 串口压力测试。

---

# 11. 故障定位顺序

出现“客户端无法使用远端设备”时固定按以下顺序排查：

```text
1. 服务端物理 USB 是否枚举
2. UsbDk 是否识别
3. UsbDk StartRedirect 是否成功
4. MyUsbIP 是否监听 3240
5. 客户端 TCP 3240 是否可达
6. usbip.exe list 是否可见设备
7. usbip.exe attach 是否成功
8. usbip.exe port 是否存在连接
9. VHCI 是否产生本地 USB
10. Windows PnP 是否识别
11. 原厂驱动是否加载
12. 业务应用是否工作
```

禁止跳过前面节点直接重启整台机器。

---

# 12. UsbDk 安装失败处理

生成 MSI 日志：

```cmd
msiexec /i UsbDk_1.0.22_x64.msi /l*v usbdk.log
```

确认服务：

```cmd
sc query usbdk
```

确认注册表：

```cmd
reg query HKLM\SYSTEM\CurrentControlSet\Control\Class\{36fc9e60-c465-11cf-8056-444553540000} /v UpperFilters
```

必要时可以解包 MSI：

```cmd
msiexec /a UsbDk_1.0.22_x64.msi /qb /l*v unpack.log TARGETDIR=C:\Temp\UsbDk
```

再使用官方 UsbDkController 做人工诊断。

---

# 13. 客户端 VHCI 失败处理

检查：

```cmd
usbip.exe port
```

设备管理器查看：

```text
USB/IP VHCI
Unknown USB Device
Device cannot start Code 10
Code 52 driver signature failure
```

如果是 Code 52：

优先判断驱动签名/证书/TESTSIGNING 状态，不要把问题误判成 USB/IP 协议异常。

查看：

```cmd
bcdedit /enum
```

确认：

```text
testsigning Yes
```

仅测试签名驱动需要此配置。

---

# 14. 依赖升级规则

任何 UsbDk 或 usbip-win 升级都必须单独走兼容测试，不允许直接覆盖生产版本。

推荐版本关系表：

```text
MyUsbIP 版本
UsbDk 版本 + SHA256
usbip-win commit/release + SHA256
Windows 版本
测试设备列表
测试结果
```

升级步骤：

```text
新版本依赖
 -> 单机测试
 -> 096E/FT/CH340 回归
 -> 100 次 attach/detach
 -> 24h 稳定性
 -> 小批量部署
 -> 正式推广
```

---

# 15. 推荐最终发布策略

生产上最稳定的方式不是让安装脚本临时访问 GitHub，而是：

```text
GitHub Actions 构建 MyUsbIP
 -> 固定依赖版本
 -> 校验 SHA256
 -> 打包 Server Bundle
 -> 打包 Client Bundle
 -> 上传 MyUsbIP Release
```

建议最后形成：

```text
MyUsbIP-Server-win-x64.zip
MyUsbIP-Client-win-x64.zip
MyUsbIP-Dependencies-win-x64.zip
checksums.sha256
```

服务端安装包中直接包含经过验证的 UsbDk 安装文件，客户端安装包中直接包含经过验证的 usbip-win VHCI 文件。

这样现场部署不依赖 GitHub 网络，也不会发生上游 latest 漂移。
