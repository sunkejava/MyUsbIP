using MyUsbIP.Abstractions;
using MyUsbIP.Protocol;

var failures = new List<string>();

await TestOperationHeaderAsync();
await TestBusIdAsync();
TestAutoShareRule();

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

void Assert(bool condition, string message)
{
    if (!condition) failures.Add($"[FAIL] {message}");
}
