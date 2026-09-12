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
    public string? ClientAddress { get; init; }

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
