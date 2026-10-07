using System.Text.Json;
using System.Text.Json.Serialization;
using MyUsbIP.Abstractions;

namespace MyUsbIP.Protocol;

// 保持状态扩展的 camelCase、数值枚举、只读属性及接口列表线上格式。
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(IReadOnlyList<UsbIpDeviceInfo>))]
[JsonSerializable(typeof(List<UsbIpDeviceInfo>))]
internal partial class DeviceStatusJsonContext : JsonSerializerContext;
