using System.Buffers.Binary;
using MyUsbIP.Protocol;

namespace MyUsbIP.WindowsNative;

/// <summary>
/// 控制 MyUsbIP.Vhci.sys（UdeCx 客户端驱动）。
/// 负责创建/移除虚拟 USB 设备端口，并与用户态 USB/IP 隧道交换 URB。
/// </summary>
public sealed class WindowsVhciController
{
    private readonly string devicePath;

    public WindowsVhciController(string? devicePath = null)
        => this.devicePath = devicePath ?? DriverControlProtocol.VhciDevicePath;

    public uint GetVersion()
    {
        using var device = new KernelDeviceClient(devicePath);
        return device.GetVersion(DriverControlProtocol.IoctlVhciGetVersion);
    }

    public int CreatePort(uint deviceId, string busId, UsbDescriptorSet descriptors)
    {
        using var device = new KernelDeviceClient(devicePath);
        var request = DriverControlProtocol.BuildVhciCreatePortRequest(deviceId, busId, descriptors);
        var response = device.Ioctl(DriverControlProtocol.IoctlVhciCreatePort, request, 8);
        if (response.Length < 8) throw new InvalidDataException("VHCI CREATE_PORT 响应长度不足。 ");
        var version = BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(0, 4));
        if (version != DriverControlProtocol.ApiVersion) throw new InvalidDataException("VHCI ABI 版本不匹配。 ");
        return checked((int)BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(4, 4)));
    }

    public void RemovePort(int port)
    {
        using var device = new KernelDeviceClient(devicePath);
        Span<byte> request = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(request[..4], DriverControlProtocol.ApiVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(request[4..], checked((uint)port));
        device.Ioctl(DriverControlProtocol.IoctlVhciRemovePort, request);
    }

    /// <summary>
    /// 阻塞等待 VHCI/UdeCx 产生一个待转发 URB。
    /// 驱动实现后该 IOCTL 将由手动队列挂起请求，直到 Windows USB 栈提交新的传输。
    /// </summary>
    public byte[] GetPendingUrb(int maxBufferSize = 16 * 1024 * 1024 + 128)
    {
        using var device = new KernelDeviceClient(devicePath);
        return device.Ioctl(DriverControlProtocol.IoctlVhciGetPendingUrb, ReadOnlySpan<byte>.Empty, maxBufferSize);
    }

    public void CompleteUrb(ReadOnlySpan<byte> completion)
    {
        using var device = new KernelDeviceClient(devicePath);
        device.Ioctl(DriverControlProtocol.IoctlVhciCompleteUrb, completion);
    }
}
