# 设备租约与权限接入

USB/IP 数据面最终通常只能让一个客户端独占使用某个物理 USB 设备。为了避免多个业务客户端同时抢占同一 UKey，MyUsbIP v1.0 提供 `IUsbIpDeviceLeaseManager`。

## 基本用法

```csharp
var leaseManager = new UsbIpDeviceLeaseManager(eventSink);

var result = await leaseManager.AcquireAsync(
    busId: "2-3",
    clientId: "pc-001",
    ttl: TimeSpan.FromMinutes(5),
    userId: "zhangsan",
    purpose: "社保系统登录");

if (!result.Success)
{
    Console.WriteLine(result.Message);
    return;
}

try
{
    await client.AttachAsync(serverHost, "2-3");
    // 执行业务
}
finally
{
    await leaseManager.ReleaseAsync(result.Lease!.LeaseId);
}
```

## 续租

长时间业务处理时可以定期续租：

```csharp
await leaseManager.RenewAsync(leaseId, TimeSpan.FromMinutes(5));
```

## 多实例部署

v1.0 内置 `UsbIpDeviceLeaseManager` 是进程内实现，适合单实例 Server/控制服务。

如果服务端做集群部署，请继续实现同一个接口：

```text
IUsbIpDeviceLeaseManager
  ├─ UsbIpDeviceLeaseManager       单实例内存版
  ├─ RedisUsbIpDeviceLeaseManager  推荐的分布式版本
  └─ DbUsbIpDeviceLeaseManager     数据库版本
```

上层业务代码无需改变。

## 与权限系统结合

建议控制流程：

```text
用户登录
 -> 查询用户有权使用的 USB/UKey
 -> Acquire 租约
 -> 服务端确认设备 Shared
 -> 客户端 Attach
 -> 业务操作
 -> Detach
 -> Release 租约
```

建议权限最小粒度至少包含：

- 用户/角色
- 服务端节点
- USB 设备或设备组
- VID/PID
- 序列号
- 可使用时间段
- 最大租约时间
- 客户端机器标识/IP

租约事件会生成 `lease.acquired`、`lease.renewed`、`lease.released` 等结构化日志，方便后续审计。
