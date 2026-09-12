using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MyUsbIP.WindowsNative;

/// <summary>
/// 访问 MyUsbIP 自研内核驱动设备接口。
/// 统一封装 CreateFile/DeviceIoControl，避免业务层直接处理 Win32 句柄。
/// </summary>
public sealed class KernelDeviceClient : IDisposable
{
    private readonly SafeFileHandle handle;

    public KernelDeviceClient(string devicePath)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("KernelDeviceClient 仅支持 Windows。");
        handle = CreateFileW(
            devicePath,
            0x80000000u | 0x40000000u,
            0x00000001u | 0x00000002u,
            IntPtr.Zero,
            3,
            0x00000080u | 0x40000000u,
            IntPtr.Zero);

        if (handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"无法打开驱动设备接口：{devicePath}");
    }

    public byte[] Ioctl(uint controlCode, ReadOnlySpan<byte> input, int outputBufferSize = 0)
    {
        var inputBytes = input.ToArray();
        var output = outputBufferSize > 0 ? new byte[outputBufferSize] : Array.Empty<byte>();
        if (!DeviceIoControl(handle, controlCode, inputBytes, inputBytes.Length, output, output.Length, out var returned, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"DeviceIoControl 失败，IOCTL=0x{controlCode:X8}");

        if (returned == output.Length) return output;
        return output.AsSpan(0, checked((int)returned)).ToArray();
    }

    public uint GetVersion(uint ioctl)
    {
        var data = Ioctl(ioctl, ReadOnlySpan<byte>.Empty, sizeof(uint));
        if (data.Length < sizeof(uint)) throw new InvalidDataException("驱动版本返回长度不足。 ");
        return BitConverter.ToUInt32(data, 0);
    }

    public void Dispose() => handle.Dispose();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        byte[] lpInBuffer,
        int nInBufferSize,
        byte[] lpOutBuffer,
        int nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);
}
