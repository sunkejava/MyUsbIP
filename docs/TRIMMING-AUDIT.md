# CLI / Daemon trimming 可行性审计

审计日期：2026-10-07；SDK：.NET 10.0.100；实际运行环境：Linux x64。

## 结论

**目前不能把 CLI / Daemon 的正式发行版改为 trimming 或 NativeAOT。** 两者共享的 JSON Lines 事件接收器仍依赖反射发现匿名事件信封及 `IReadOnlyDictionary<string, object?>` 中的任意运行时类型。保留该兼容契约，不压制 IL 警告，不使用 linker 全程序集保留来掩盖问题。

本次已将 CLI 配置、Daemon 配置和设备状态扩展协议改为 `JsonSerializerContext` 源生成；正式 CLI / Daemon 仍沿用普通未裁剪发布。安装器的 NativeAOT 目标独立于此结论。

## 编译器实测

使用全局 `EnableTrimAnalyzer=true`、`EnableAotAnalyzer=true`，重新编译根项目及全部项目引用，按“文件/行号/警告代码”去重（MSBuild 结尾摘要会重复警告）。

| 对象 | 修改前 IL2026 | 修改前 IL3050 | 修改后 IL2026 | 修改后 IL3050 |
| --- | ---: | ---: | ---: | ---: |
| CLI 及全部依赖 | 4 | 4 | 1 | 1 |
| Daemon 及全部依赖 | 4 | 4 | 1 | 1 |

原告警分别来自 `EventSinks.cs:63` 的事件 JSON（2 个）、`UsbDeviceStatusProtocol.cs:36/63` 的协议 JSON（4 个）、对应 `Program.cs` 的配置 JSON（2 个）。修复后只剩 `EventSinks.cs:63` 的 IL2026、IL3050。未发现其他源码 IL 告警；现有 CS1591 XML 文档告警仍保留，不把“0 个新错误”误写成“0 个总告警”。

CLI 的 Linux 裁剪 publish 又在 `EventSinks.cs` 报告 IL2026，证明编译器检查与 linker 检查结果一致。Windows P/Invoke/UsbDk/原生 USB 路径在源码审计范围内，但 Linux 运行不能覆盖实际 Windows 驱动行为。

## 已做的兼容改造

- CLI / Daemon 配置保持大小写不敏感、默认值、init/required 成员及原嵌套模型；未改变 JSON 字段名或读取入口。
- 状态扩展保持 Web 默认规则、camelCase、数值枚举、null、只读 `vidPid`、接口列表、原始中文及 4 MiB 数据上限；源生成显式覆盖 `IReadOnlyList<UsbIpDeviceInfo>` 写出与 `List<UsbIpDeviceInfo>` 读入。
- JSON Lines 接收器保持现有异常摘要、Win32 native error 字段及任意 object 值的行为。仅注册 `object` 无法保证字典内实际运行时类型的完整元数据，因此没有采用该“看似告警消失”的改法。

## 运行验证

| 验证项 | 实测结果 |
| --- | --- |
| 现有 `MyUsbIP.SmokeTests` | PASS，覆盖状态协议往返和中文/Win32 异常日志 |
| 未裁剪 Linux CLI | PASS；大写配置键、非默认超时、中文 usbIdsPath、事件 arguments 数组与字段一致 |
| 裁剪 Linux CLI，关闭文件日志 | PASS；配置加载及帮助/无效命令出口正常，退出码 2 |
| 裁剪 Linux CLI，打开文件日志 | 复现阻断；CLI 仍退出 2，但 JSON Lines 文件为空，事件被静默丢弃 |
| 未裁剪 Linux Daemon | PASS；大写及嵌套配置加载，`daemon.started` JSON 正确；验证后由审计终止进程 |
| 裁剪 Linux Daemon | 复现阻断；可以启动，但文件中的启动/诊断事件全部静默丢失 |
| Windows USB 枚举、IMPORT、URB、attach/detach、服务运行 | 未验证，需要 Windows 实机及真实驱动/设备 |

`关闭日志`只是定位条件，不是正式修复或推荐运行方式。实际主程序通过 `CompositeUsbIpEventSink` 写事件，该接收器会捕获文件日志序列化异常。因此裁剪版可能帮助、启动和健康检查看起来正常，但文件日志被静默丢弃；必须核对输出 JSON 内容，不能只检查进程退出码。

### 发行目录体积实测

Linux x64、Release、自包含（不是单文件）、.NET 10.0.0 runtime；统计整个 publish 目录，包括 DLL、native runtime、配置及调试符号。以下为审计实验产物，不作为可用裁剪发行包：

| 项目 | 未裁剪字节 | 裁剪字节 | 未裁剪 MiB | 裁剪 MiB |
| --- | ---: | ---: | ---: | ---: |
| CLI | 83,133,844 | 25,860,080 | 79.28 | 24.66 |
| Daemon | 83,368,511 | 26,054,831 | 79.51 | 24.85 |

Windows 体积须以对应平台审计产物为准。本机托管环境禁止 Unix socket，ILLink 的独立 MSBuild task host 创建命名管道失败；为完成 Linux 实测，仅在 `/tmp` 写了 `UsingTask Override=true` 的临时 targets，把两个 linker tasks 改为同进程执行。没有修改仓库 SDK/ILLink 配置，也没有压制 linker 的真实 IL2026；正常 Windows/Linux CI 直接运行审计脚本即可。

## 可复现命令

需要 .NET 10 SDK、PowerShell 7。审计过程不共享/挂载 USB；Daemon runtime smoke 禁用原生监听、自动共享及受管连接，启动后终止。

```powershell
# 编译源码及所有引用，保留真实 IL 告警
./scripts/Audit-Trimming.ps1 -RuntimeIdentifier win-x64

# 发行目录体积对照，以及 config/event 的 runtime smoke
./scripts/Audit-Trimming.ps1 -RuntimeIdentifier win-x64 -Publish -Smoke

# Linux 主机
./scripts/Audit-Trimming.ps1 -RuntimeIdentifier linux-x64 -Publish -Smoke
```

`-DotNetPath` 可以传入 SDK 的绝对 dotnet 路径；`-OutputDirectory` 指定输出目录，默认 `artifacts/trimming-audit`。`-Smoke` 隐含发布，必须选择与运行主机一致的 RID。输出包含 analyzer 日志、唯一 IL 警告列表、完整 publish 日志、summary.json、运行结果及现有 smoke harness 输出。

`-p:MyUsbIpTrimmingAudit=true` 是 CLI / Daemon 项目自身的 opt-in analyzer 开关；要覆盖全部依赖，必须像脚本一样同时传入全局 `EnableTrimAnalyzer` / `EnableAotAnalyzer`。该开关不设置 `PublishTrimmed` / `PublishAot`。

脚本把已知 reflection blocker 的复现作为审计断言，并不将该失败标为产品运行通过。若后续改造事件类型契约，必须同时更新该断言，加入未知类型/嵌套数组/字典和异常的验证，不能只删除断言。

## 后续投入建议

1. 先定义事件属性允许的类型集合及扩展约定，再选择强类型事件 DTO、明确的源生成元数据或专用 writer；任何选择都要兼容现有日志字段，并明确未知类型如何处理。
2. 在 Windows 上开启全局 analyzer 与真实 linker，再验证 UsbDk、NativeWindows、usbip-win、Windows 服务，以及带真实硬件的重连/断开/并发 URB。
3. 只有完整运行验证通过才调整正式 CLI / Daemon 发布方式；安装器可以先单独使用 NativeAOT。
