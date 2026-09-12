using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MyUsbIP.UsbDk;

internal static class UsbDkNative
{
    internal const int MaxDeviceIdLen = 200;
    internal static readonly nint InvalidHandleValue = new(-1);

    [DllImport("UsbDkHelper.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UsbDk_GetDevicesList(out nint devicesArray, out uint numberDevices);

    [DllImport("UsbDkHelper.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void UsbDk_ReleaseDevicesList(nint devicesArray);

    [DllImport("UsbDkHelper.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UsbDk_GetConfigurationDescriptor(
        ref UsbDkConfigDescriptorRequest request,
        out nint descriptor,
        out uint length);

    [DllImport("UsbDkHelper.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void UsbDk_ReleaseConfigurationDescriptor(nint descriptor);

    [DllImport("UsbDkHelper.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    internal static extern nint UsbDk_StartRedirect(ref UsbDkDeviceId deviceId);

    [DllImport("UsbDkHelper.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UsbDk_StopRedirect(nint deviceHandle);

    [DllImport("UsbDkHelper.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    internal static extern UsbDkTransferResult UsbDk_ReadPipe(
        nint deviceHandle,
        ref UsbDkTransferRequest request,
        ref NativeOverlappedData overlapped);

    [DllImport("UsbDkHelper.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    internal static extern UsbDkTransferResult UsbDk_WritePipe(
        nint deviceHandle,
        ref UsbDkTransferRequest request,
        ref NativeOverlappedData overlapped);

    [DllImport("UsbDkHelper.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UsbDk_AbortPipe(nint deviceHandle, ulong pipeAddress);

    [DllImport("UsbDkHelper.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UsbDk_ResetPipe(nint deviceHandle, ulong pipeAddress);

    [DllImport("UsbDkHelper.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UsbDk_ResetDevice(nint deviceHandle);

    [DllImport("UsbDkHelper.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint UsbDk_GetRedirectorSystemHandle(nint deviceHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern nint CreateEventW(nint eventAttributes, bool manualReset, bool initialState, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetOverlappedResult(nint fileHandle, ref NativeOverlappedData overlapped, out uint bytesTransferred, bool wait);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(nint handle);

    internal static void ThrowLastWin32(string message) => throw new Win32Exception(Marshal.GetLastWin32Error(), message);
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct UsbDkDeviceId
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = UsbDkNative.MaxDeviceIdLen)]
    public string DeviceId;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = UsbDkNative.MaxDeviceIdLen)]
    public string InstanceId;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct UsbDeviceDescriptor
{
    public byte Length;
    public byte DescriptorType;
    public ushort BcdUsb;
    public byte DeviceClass;
    public byte DeviceSubClass;
    public byte DeviceProtocol;
    public byte MaxPacketSize0;
    public ushort VendorId;
    public ushort ProductId;
    public ushort BcdDevice;
    public byte ManufacturerIndex;
    public byte ProductIndex;
    public byte SerialNumberIndex;
    public byte NumberConfigurations;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct UsbDkDeviceInfoNative
{
    public UsbDkDeviceId Id;
    public ulong FilterId;
    public ulong Port;
    public ulong Speed;
    public UsbDeviceDescriptor DeviceDescriptor;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct UsbDkConfigDescriptorRequest
{
    public UsbDkDeviceId Id;
    public ulong Index;
}

[StructLayout(LayoutKind.Sequential)]
internal struct UsbDkGenericTransferResult
{
    public ulong BytesTransferred;
    public ulong UsbdStatus;
}

[StructLayout(LayoutKind.Sequential)]
internal struct UsbDkRequestTransferResult
{
    public UsbDkGenericTransferResult Generic;
    public nint IsochronousResultsArray;
}

[StructLayout(LayoutKind.Sequential)]
internal struct UsbDkTransferRequest
{
    public ulong EndpointAddress;
    public nint Buffer;
    public ulong BufferLength;
    public ulong TransferType;
    public ulong IsochronousPacketsArraySize;
    public nint IsochronousPacketsArray;
    public UsbDkRequestTransferResult Result;
}

internal enum UsbDkTransferResult
{
    Failure = 0,
    Success = 1,
    SuccessAsync = 2,
}

internal enum UsbDkTransferType : ulong
{
    Control = 0,
    Bulk = 1,
    Interrupt = 2,
    Isochronous = 3,
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeOverlappedData
{
    public nint Internal;
    public nint InternalHigh;
    public uint Offset;
    public uint OffsetHigh;
    public nint EventHandle;
}
