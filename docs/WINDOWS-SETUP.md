# NativeAOT Windows 安装器

安装器面向 Windows 10/11 x64。Setup 使用 .NET 10 NativeAOT，目标机器无需 .NET Runtime。CLI/Daemon 仍使用未经 trimming 的 self-contained 单文件发布；可行性审计见 [TRIMMING-AUDIT.md](TRIMMING-AUDIT.md)。

## 选择安装方式

| 资产 | 用途 |
| --- | --- |
| `MyUsbIP-Server-Setup.exe` / `MyUsbIP-Client-Setup.exe` | 小型在线安装器，只嵌入当前 Release 的版本、下载地址、ZIP 大小及 SHA256 |
| `MyUsbIP-Server-Offline-Setup.exe` / `MyUsbIP-Client-Offline-Setup.exe` | 独立离线安装器，内置同一版本完整 ZIP，现场无须联网 |
| `MyUsbIP-Server-win-x64.zip` / `MyUsbIP-Client-win-x64.zip` | 完整程序、配置及固定版本驱动包，可与在线 EXE 配合离线安装 |

双击对应角色 EXE，接受管理员提权。默认 `auto` 模式优先读取旁边的同名 ZIP，其次使用内置 ZIP，否则下载清单指定的版本。显式 `--package` 或已找到但损坏的本地包会直接报错，不自动切换网络来源。仅有 ZIP 时需要配合同一 Release 的安装器，不能直接双击 ZIP 安装。

```cmd
MyUsbIP-Client-Setup.exe --mode online
MyUsbIP-Client-Offline-Setup.exe --mode offline
MyUsbIP-Client-Setup.exe --mode offline --package "D:\Packages\MyUsbIP-Client-win-x64.zip"
MyUsbIP-Server-Setup.exe --mode offline --package "D:\Packages\MyUsbIP-Server-win-x64.zip" --quiet
MyUsbIP-Client-Setup.exe --package "D:\Packages\MyUsbIP-Client-win-x64.zip" --verify-only --quiet
```

`offline` 模式不创建 HTTP 客户端，不发出网络请求。`--quiet` 禁用按键等待，不跳过 UAC 或系统管理员权限。`--verify-only` 只下载/验证/解包，不停止旧程序，不调用驱动安装器，不改防火墙或 PATH。`--timeout-seconds` 是整个在线下载的总超时，默认 300 秒（1～3600）。Ctrl+C 可以取消下载/准备阶段；安装系统驱动阶段由原有系统安装器完成。

## 校验及升级流程

1. 从 EXE 内置清单读取固定版本 HTTPS URL、SHA256、大小、角色和架构。
2. 将本地/内置/下载数据流写入唯一临时目录的 `.partial` 文件；限制大小，不整包读入内存。在线下载仅允许 HTTPS，最多 5 次重定向。
3. 校验包大小及 SHA256，通过后重命名并解包。解包限制文件数及总大小，拒绝路径越界、符号链接、Windows 保留名称、重复路径和 ADS。
4. 校验包内驱动 SHA256、安装类型、程序入口及配置文件。全部通过后才停止旧版本。相同角色的实际安装使用全局互斥锁。
5. 继续原有 UsbDk/usbip-win2 健康检查及安装、保留用户配置、程序部署、服务端防火墙和 SYSTEM 开机任务、客户端 PATH、升级迁移及自检。
6. 成功或失败后清理临时文件；保留 `C:\ProgramData\MyUsbIP\InstallerLogs` 日志。

校验失败不会停止旧版本；开始覆盖部署后的驱动安装/系统设置失败仍可能需要修复安装，不提供完整系统事务回滚。驱动要求重启时保留原有停止后续步骤的行为。SHA256 绑定到可信的安装器 EXE；应从可信 Release 下载 EXE，当前没有新增代码签名证书。

| 退出码 | 含义 |
| --- | --- |
| 0 | 安装成功或验证成功，`--help` 也返回 0 |
| 1 | 包缺失、校验失败、安装或自检错误 |
| 2 | 命令行参数无效 |
| 3 | 下载/准备取消或下载超时 |
| 10 | 非 Windows 系统 |

## 维护者构建

需要 .NET 10 SDK，以及 Windows Visual Studio C++ Desktop 工作负载和 Windows SDK。以下在 PowerShell 7 中执行；最终用户只运行 EXE。

```powershell
dotnet publish tools/MyUsbIP.Cli/MyUsbIP.Cli.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o artifacts/win-x64/cli
dotnet publish apps/MyUsbIP.Daemon/MyUsbIP.Daemon.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o artifacts/win-x64/daemon
./scripts/windows/Install-Dependencies.ps1 -Role All
./scripts/windows/Build-WindowsBundles.ps1 -ReleaseTag v1.1.18
./scripts/windows/Build-WindowsSetups.ps1
./scripts/windows/Test-WindowsSetups.ps1
```

顺序必须是 **发布业务程序 → 验证驱动缓存 → 生成 ZIP 及清单 → 发布四个 NativeAOT EXE → 验证 → 上传同批 Release 资产**。每次 Setup publish 前 clean，避免离线资源污染在线产物。工程直接 publish 必须提供 `SetupRole=server|client`、`SetupMode=online|offline`，并存在生成的清单；缺失时构建失败，不能输出不可用的安装器。

在线 EXE 不读取远端可变清单，也不使用 latest URL。不要覆盖已发布 ZIP 而保留旧 EXE：ZIP 的构建哈希已绑定到对应 EXE，资产必须成套发布。普通 main/PR 构建的在线 URL 只有在对应 Release 正式上传后才可用；CI 会验证本地 ZIP 及内置离线载荷。CI 设置在线 EXE 20 MiB 体积上限，实际体积由 Windows NativeAOT publish 的输出报告。

Windows CI 不安装真实驱动，使用 `--verify-only --quiet` 覆盖旁边 ZIP、指定路径、内置离线包、缺包、损坏包和参数错误。驱动安装、SYSTEM 启动任务及 USB 设备行为仍需在真实 Windows 机器验证。

## 本次验证结果

2026-10-07 Windows/Linux CI 完整通过并正式发布 [v1.1.18](https://github.com/sunkejava/MyUsbIP/releases/tag/v1.1.18)（[发布流水线](https://github.com/sunkejava/MyUsbIP/actions/runs/37575289403)）。Windows NativeAOT 安装器实际输出：

| 资产 | 体积 |
| --- | ---: |
| Server / Client 在线 EXE | 5,928,448 bytes（5.65 MiB） |
| Server 离线 EXE | 44,694,528 bytes（42.62 MiB） |
| Client 离线 EXE | 63,906,816 bytes（60.95 MiB） |

真实 PE 的管理员权限清单、NativeAOT EXE 参数、本地 ZIP、内置 ZIP、缺包、SHA256 损坏包和 `--verify-only` 均通过验证；下载引擎通过固定 v1.1.15 Release 的真实 HTTPS/CDN 下载、SHA256 及解包集成测试。v1.1.18 的四个安装器、两个 Windows ZIP、Linux ZIP 和 SHA256 文件已成套上传，在线安装器固定的正式下载地址已生效。此处体积会随后续发布的业务程序和驱动包变化。

v1.1.17 修复 v1.1.16 配置源生成对缺省 `init` 属性覆盖初始化值的回归。旧版 `clientsettings.json` 没有 `UsbipWinPath` 时重新使用 `usbip.exe`，连接超时和接收模式默认值也恢复；Daemon 部分嵌套配置同步修复。安装器继续保留已有配置，无需删除或重写配置文件；Windows/Linux CI 直接验证生产配置类和旧配置运行行为。

v1.1.18 补充 CH340 服务端严格恢复与二次捕获检查；8 个正式 Release 文件下载后大小及 GitHub SHA256 匹配，6 个 Windows 安装资产也与 checksums.sha256 一致。两个在线 EXE 内嵌的 1.1.18 版本、正式 URL、ZIP 大小及 SHA256 均与对应已发布 ZIP 一致。Linux 发布归档中的 CLI 无参数帮助启动成功。CH340 的最终硬件验收见 [二次连接排查](CH340-REATTACH.md)。
