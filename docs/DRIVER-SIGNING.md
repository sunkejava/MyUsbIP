# Windows 驱动签名与发布

`MyUsbIP.Exporter.sys` 与 `MyUsbIP.Vhci.sys` 都属于内核模式驱动。源码编译成功不等于可以直接在启用 Secure Boot 的生产 Windows 上加载。

## 开发阶段

开发机可使用 WDK 的测试签名流程验证驱动包。测试签名包只能用于开发和内部验证，不能作为正式发行包。

## 正式发布

正式发布应按 Microsoft Hardware Dev Center 当前要求执行：

1. 准备组织级 Microsoft Partner Center / Hardware Developer Program 账号。
2. 账号关联有效的 EV Code Signing Certificate。
3. 使用 Visual Studio + WDK 生成 SYS / INF / CAT 驱动包。
4. 驱动提交包使用 SHA-2 签名。
5. Windows 10/11 桌面测试场景可使用 Attestation Signing，但它不代表 Windows Certified，也不能作为零售 Windows Update 发布的完整替代方案。
6. 正式产品优先完成 HLK 测试并走 Windows Hardware Compatibility Program / Dashboard Signing。
7. Windows Server 2016+ 的设备/过滤驱动正式签名应采用 HLK 路线，不能依赖桌面版 Attestation 路线。

## MyUsbIP 的建议

第一阶段：

- 本地 Test Signing
- Windows 10/11 x64 实机
- Secure Boot 测试机按微软开发流程配置
- 完成 096E、CH340、CCID 等兼容性测试

第二阶段：

- EV 证书
- Hardware Dev Center
- Preproduction/Attestation 用于扩大内部测试
- 完整 Driver Verifier 测试

正式 v1 Native：

- HLK 测试
- Dashboard 签名
- SYS/INF/CAT 与 MyUsbIP 用户态程序统一安装包
- 安装器只为需要共享的设备配置 Exporter 过滤关系
- 自动检测驱动签名、版本、Secure Boot、重启状态

## CI 注意事项

普通 GitHub Hosted Windows Runner 只负责 .NET 构建，不能替代完整 WDK/HLK/签名环境。驱动 CI 应使用安装 Visual Studio + WDK 的专用 Windows Runner；生产签名密钥不能直接提交到仓库。

生产签名应在受控签名环境或 Partner Center 提交流程中完成，Release 工作流只消费已经经过 Microsoft 签名的驱动产物。
