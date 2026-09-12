using System.Buffers.Binary;
using System.Text;
using MyUsbIP.Abstractions;
using MyUsbIP.Protocol;
using MyUsbIP.Runtime;

var failures = new List<string>();

await TestOperationHeaderAsync();
await TestBusIdAsync();
await TestDeviceWireMetadataAsync();
await TestDevListInterfacesAsync();
await TestDeviceStatusProtocolAsync();
await TestChineseJsonLogAsync();
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

async Task TestDevListInterfacesAsync()
{
    var device = new UsbIpDeviceInfo
    {
        BusId = "0000002F-00000004",
        Path = "USB\\VID_1A86&PID_7523\\4",
        BusNumber = 47,
        DeviceNumber = 4,
        Speed = 2,
        VendorId = 0x1A86,
        ProductId = 0x7523,
        DeviceClass = 0xFF,
        DeviceSubClass = 0x01,
        DeviceProtocol = 0x02,
        ConfigurationValue = 1,
        ConfigurationCount = 1,
        InterfaceCount = 1,
        State = UsbIpDeviceState.Available,
    };

    await using var stream = new MemoryStream();
    await UsbIpWire.WriteDevListReplyAsync(stream, new[] { device });
    var bytes = stream.ToArray();
    var expectedLength = UsbIpCodec.OperationHeaderSize + 4 + UsbIpWire.DeviceWireSize + UsbIpWire.InterfaceWireSize;
    Assert(bytes.Length == expectedLength, "DEVLIST 未按 bNumInterfaces 写入 usb_interface 记录");

    var interfaceOffset = UsbIpCodec.OperationHeaderSize + 4 + UsbIpWire.DeviceWireSize;
    Assert(bytes[interfaceOffset] == device.DeviceClass, "DEVLIST interface class 写入错误");
    Assert(bytes[interfaceOffset + 1] == device.DeviceSubClass, "DEVLIST interface subclass 写入错误");
    Assert(bytes[interfaceOffset + 2] == device.DeviceProtocol, "DEVLIST interface protocol 写入错误");
    Assert(bytes[interfaceOffset + 3] == 0, "DEVLIST interface padding 写入错误");
}

async Task TestDeviceStatusProtocolAsync()
{
    var expected = new UsbIpDeviceInfo
    {
        BusId = "0000002F-00000004",
        VendorId = 0x1A86,
        ProductId = 0x7523,
        Manufacturer = "QinHeng Electronics",
        Product = "CH340 serial converter",
        SerialNumber = "CH340-001",
        State = UsbIpDeviceState.Attached,
        ClientAddress = "192.168.3.101:52000",
        SessionId = "session-001",
        ConnectedAt = new DateTimeOffset(2026, 9, 13, 1, 1, 15, TimeSpan.FromHours(8)),
        BusNumber = 47,
        DeviceNumber = 4,
        Speed = 2,
        UsbVersion = 0x0200,
        DeviceVersion = 0x0264,
        DeviceClass = 0,
        DeviceSubClass = 0,
        DeviceProtocol = 0,
        ConfigurationValue = 1,
        ConfigurationCount = 1,
        InterfaceCount = 1,
    };

    await using var stream = new MemoryStream();
    await UsbDeviceStatusProtocol.WriteReplyAsync(stream, new[] { expected });
    stream.Position = 0;
    var actual = await UsbDeviceStatusProtocol.ReadReplyAsync(stream);
    Assert(actual.Count == 1, "设备状态扩展协议返回数量错误");
    if (actual.Count == 1)
    {
        Assert(actual[0].BusId == expected.BusId, "设备状态扩展协议 BusId 丢失");
        Assert(actual[0].ClientAddress == expected.ClientAddress, "设备状态扩展协议连接客户端丢失");
        Assert(actual[0].SessionId == expected.SessionId, "设备状态扩展协议 SessionId 丢失");
        Assert(actual[0].Manufacturer == expected.Manufacturer, "设备状态扩展协议厂商信息丢失");
        Assert(actual[0].SerialNumber == expected.SerialNumber, "设备状态扩展协议序列号丢失");
    }
}

async Task TestChineseJsonLogAsync()
{
    var path = Path.Combine(Path.GetTempPath(), $"myusbip-smoke-{Guid.NewGuid():N}.jsonl");
    try
    {
        await using (var sink = new JsonLinesUsbIpEventSink(path))
        {
            await sink.WriteAsync(new UsbIpEvent(DateTimeOffset.Now, "smoke.chinese", "Information", null,
                "0000002F-00000004", "192.168.3.100", "中文日志可以直接阅读：设备连接成功"));
        }

        var text = await File.ReadAllTextAsync(path, Encoding.UTF8);
        Assert(text.Contains("中文日志可以直接阅读", StringComparison.Ordinal), "JSONL 中文被编码为 Unicode 转义");
        Assert(!text.Contains("\\u4e2d\\u6587", StringComparison.OrdinalIgnoreCase), "JSONL 不应包含中文 Unicode 转义");
    }
    finally
    {
        try { File.Delete(path); } catch { }
    }
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
