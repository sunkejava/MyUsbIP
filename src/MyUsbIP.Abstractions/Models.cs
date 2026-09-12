namespace MyUsbIP.Abstractions;

/// <summary>
/// USB/IP 远程设备信息。
/// 该模型刻意不暴露任何具体平台驱动类型，保证 Windows/Linux 使用同一套业务代码。
/// </summary>
public sealed record UsbIpDeviceInfo
{
    public required string BusId { get; init; }
    public string? InstanceId { get; init; }
    public ushort VendorId { get; init; }
    public ushort ProductId { get; init; }
    public string? Manufacturer { get; init; }
    public string? Product { get; init; }
    public string? SerialNumber { get; init; }
    public UsbIpDeviceState State { get; init; }

    /// <summary>当前占用该远端设备的客户端地址/IP。设备空闲时为空。</summary>
    public string? ClientAddress { get; init; }

    /// <summary>当前 USB/IP IMPORT 会话标识，便于服务端/客户端日志交叉定位。</summary>
    public string? SessionId { get; init; }

    /// <summary>当前客户端建立 IMPORT 会话的时间。</summary>
    public DateTimeOffset? ConnectedAt { get; init; }

    /// <summary>USB/IP 线协议中的设备路径。为空时回退到 InstanceId/BusId。</summary>
    public string? Path { get; init; }

    /// <summary>USB/IP 线协议中的远端总线号。</summary>
    public uint BusNumber { get; init; }

    /// <summary>USB/IP 线协议中的远端设备号。</summary>
    public uint DeviceNumber { get; init; }

    /// <summary>
    /// 标准 USB/IP usb_device_speed 值：0 Unknown、1 Low、2 Full、3 High、4 Wireless、5 Super、6 SuperPlus。
    /// </summary>
    public uint Speed { get; init; } = 2;

    /// <summary>USB Device Descriptor 的 bcdUSB。</summary>
    public ushort UsbVersion { get; init; }

    /// <summary>USB Device Descriptor 的 bcdDevice。</summary>
    public ushort DeviceVersion { get; init; }

    public byte DeviceClass { get; init; }
    public byte DeviceSubClass { get; init; }
    public byte DeviceProtocol { get; init; }
    public byte ConfigurationValue { get; init; } = 1;
    public byte ConfigurationCount { get; init; } = 1;
    public byte InterfaceCount { get; init; }

    public string VidPid => $"{VendorId:X4}:{ProductId:X4}";
}

/// <summary>设备当前状态。</summary>
public enum UsbIpDeviceState
{
    Unknown = 0,
    Available = 1,
    Shared = 2,
    Attached = 3,
    Busy = 4,
    Error = 5,
}

/// <summary>远程挂载结果。</summary>
public sealed record UsbIpAttachResult(bool Success, string BusId, int? Port, string? Message)
{
    public static UsbIpAttachResult Failed(string busId, string message) => new(false, busId, null, message);
}

/// <summary>平台后端能力。</summary>
public sealed record UsbIpBackendCapabilities(
    bool CanList,
    bool CanShare,
    bool CanAttach,
    bool CanDetach,
    string BackendName,
    string? BackendVersion = null);
