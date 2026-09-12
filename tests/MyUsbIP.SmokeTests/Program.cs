using System.Buffers.Binary;
using MyUsbIP.Abstractions;
using MyUsbIP.Protocol;
using MyUsbIP.Runtime;

var failures = new List<string>();

await TestOperationHeaderAsync();
await TestBusIdAsync();
await TestDeviceWireMetadataAsync();
TestAutoShareRule();
await TestLeaseManagerAsync();

if (failures.Count == 0)
{
    Console.WriteLine("MyUsbIP smoke tests: PASS");
    return 0;
}

foreach (var failure in failures) Console.Error.WriteLine(failure);
return 1;

async Task TestOperationHeaderAsync()
{
    await using var stream = new MemoryStream();
    var expected = new UsbIpOperationHeader(UsbIpProtocolConstants.Version, UsbIpProtocolConstants.OpReqImport, 0);
    await UsbIpCodec.WriteOperationHeaderAsync(stream, expected);
    stream.Position = 0;
    var actual = await UsbIpCodec.ReadOperationHeaderAsync(stream);
    Assert(expected == actual, "OP_COMMON 大端序编解码失败");
}

async Task TestBusIdAsync()
{
    await using var stream = new MemoryStream();
    await UsbIpCodec.WriteBusIdAsync(stream, "2-3.1");
    Assert(stream.Length == UsbIpCodec.BusIdFieldSize, "BusId 定长字段长度错误");
    stream.Position = 0;
    var actual = await UsbIpCodec.ReadBusIdAsync(stream);
    Assert(actual == "2-3.1", "BusId 编解码失败");
}

async Task TestDeviceWireMetadataAsync()
{
    var device = new UsbIpDeviceInfo
    {
        BusId = "00000008-00000004",
        InstanceId = "USB\\VID_1A86&PID_7523\\4",
        Path = "USB\\VID_1A86&PID_7523\\4",
        BusNumber = 8,
        DeviceNumber = 4,
        Speed = 2,
        VendorId = 0x1A86,
        ProductId = 0x7523,
        State = UsbIpDeviceState.Available,
    };

    await using var stream = new MemoryStream();
    await UsbIpWire.WriteDeviceAsync(stream, device);
    var bytes = stream.ToArray();
    Assert(bytes.Length == UsbIpWire.DeviceWireSize, "USB/IP 设备结构长度错误");
    Assert(BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(288, 4)) == 8, "USB/IP busnum 写入错误");
    Assert(BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(292, 4)) == 4, "USB/IP devnum 写入错误");
    Assert(BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(296, 4)) == 2, "USB/IP speed 写入错误");
}

void TestAutoShareRule()
{
    var device = new UsbIpDeviceInfo
    {
        BusId = "2-3",
        VendorId = 0x096E,
        ProductId = 0x0303,
        SerialNumber = "ABC001",
        Product = "USB TOKEN",
        State = UsbIpDeviceState.Available,
    };

    var match = new UsbIpAutoShareRule { VendorId = 0x096E, ProductId = 0x0303 };
    var notMatch = new UsbIpAutoShareRule { VendorId = 0x1A86, ProductId = 0x7523 };
    Assert(match.IsMatch(device), "自动共享规则应匹配但未匹配");
    Assert(!notMatch.IsMatch(device), "自动共享规则不应匹配但发生匹配");
}

async Task TestLeaseManagerAsync()
{
    var leases = new UsbIpDeviceLeaseManager();
    var first = await leases.AcquireAsync("2-3", "client-a", TimeSpan.FromMinutes(1));
    var second = await leases.AcquireAsync("2-3", "client-b", TimeSpan.FromMinutes(1));
    Assert(first.Success && first.Lease is not null, "第一个客户端应成功获取设备租约");
    Assert(!second.Success, "设备被占用时第二个客户端不应获取租约");

    if (first.Lease is not null)
    {
        Assert(await leases.ReleaseAsync(first.Lease.LeaseId), "租约释放失败");
        var third = await leases.AcquireAsync("2-3", "client-b", TimeSpan.FromMinutes(1));
        Assert(third.Success, "释放后第二个客户端应能够获取租约");
    }
}

void Assert(bool condition, string message)
{
    if (!condition) failures.Add($"[FAIL] {message}");
}
