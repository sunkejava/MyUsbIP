using System.Runtime.InteropServices;
using System.Text;

namespace MyUsbIP.UsbDk;

/// <summary>
/// CH340/USB 设备在 UsbDk StopRedirect 后的 Windows PnP 恢复辅助。
/// DICS_PROPCHANGE 会让 Windows 停止并重新启动目标设备栈；若目标设备暂时不可见，
/// 则退化为对上级 USB Hub/总线节点执行重新枚举。
/// </summary>
internal static class WindowsDeviceRecovery
{
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfAllClasses = 0x00000004;
    private const int ErrorNoMoreItems = 259;
    private const uint DifPropertyChange = 0x12;
    private const uint DicsPropChange = 0x00000003;
    private const uint DicsFlagGlobal = 0x00000001;
    private const uint CrSuccess = 0;
    private static readonly nint InvalidHandleValue = new(-1);

    public sealed record Identity(string FullInstanceId, string? ParentInstanceId);

    public static Identity? ResolveIdentity(string? deviceId, string? instanceId)
    {
        if (!OperatingSystem.IsWindows()) return null;

        var present = WindowsDevicePresence.EnumeratePresentUsbInstanceIds();
        var fullInstanceId = WindowsDevicePresence.FindPresentInstanceId(deviceId, instanceId, present);
        if (string.IsNullOrWhiteSpace(fullInstanceId)) return null;

        string? parentInstanceId = null;
        if (CM_Locate_DevNodeW(out var devInst, fullInstanceId, 0) == CrSuccess &&
            CM_Get_Parent(out var parentDevInst, devInst, 0) == CrSuccess)
        {
            parentInstanceId = GetDeviceInstanceId(parentDevInst);
        }

        return new Identity(fullInstanceId, parentInstanceId);
    }

    /// <summary>
    /// 首选 DIF_PROPERTYCHANGE/DICS_PROPCHANGE 重启目标设备。
    /// 如果设备节点暂时不存在，则重新枚举已经保存的父 USB 节点。
    /// </summary>
    public static bool TryRestart(Identity identity, out string detail)
    {
        ArgumentNullException.ThrowIfNull(identity);

        if (!OperatingSystem.IsWindows())
        {
            detail = "当前系统不是 Windows";
            return false;
        }

        if (TryPropertyChangeRestart(identity.FullInstanceId, out var propertyDetail))
        {
            detail = propertyDetail;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(identity.ParentInstanceId) &&
            TryReenumerate(identity.ParentInstanceId, out var parentDetail))
        {
            detail = $"{propertyDetail}; fallback={parentDetail}";
            return true;
        }

        detail = string.IsNullOrWhiteSpace(identity.ParentInstanceId)
            ? propertyDetail
            : $"{propertyDetail}; fallback=父节点重新枚举失败";
        return false;
    }

    public static bool TryReenumerateParent(Identity identity, out string detail)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (string.IsNullOrWhiteSpace(identity.ParentInstanceId))
        {
            detail = "父 USB 节点 InstanceId 不可用";
            return false;
        }

        return TryReenumerate(identity.ParentInstanceId, out detail);
    }

    private static bool TryPropertyChangeRestart(string fullInstanceId, out string detail)
    {
        var infoSet = SetupDiGetClassDevsW(0, "USB", 0, DigcfPresent | DigcfAllClasses);
        if (infoSet == 0 || infoSet == InvalidHandleValue)
        {
            detail = $"SetupDiGetClassDevsW 失败，Win32={Marshal.GetLastWin32Error()}";
            return false;
        }

        try
        {
            for (uint index = 0; ; index++)
            {
                var info = new SpDevInfoData { CbSize = (uint)Marshal.SizeOf<SpDevInfoData>() };
                if (!SetupDiEnumDeviceInfo(infoSet, index, ref info))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == ErrorNoMoreItems) break;
                    continue;
                }

                var candidate = GetDeviceInstanceId(infoSet, ref info);
                if (!string.Equals(candidate, fullInstanceId, StringComparison.OrdinalIgnoreCase))
                    continue;

                var change = new SpPropChangeParams
                {
                    ClassInstallHeader = new SpClassInstallHeader
                    {
                        CbSize = (uint)Marshal.SizeOf<SpClassInstallHeader>(),
                        InstallFunction = DifPropertyChange,
                    },
                    StateChange = DicsPropChange,
                    Scope = DicsFlagGlobal,
                    HwProfile = 0,
                };

                if (!SetupDiSetClassInstallParamsW(
                        infoSet,
                        ref info,
                        ref change,
                        (uint)Marshal.SizeOf<SpPropChangeParams>()))
                {
                    detail = $"SetupDiSetClassInstallParamsW(DICS_PROPCHANGE) 失败，Win32={Marshal.GetLastWin32Error()}，InstanceId={fullInstanceId}";
                    return false;
                }

                if (!SetupDiCallClassInstaller(DifPropertyChange, infoSet, ref info))
                {
                    detail = $"SetupDiCallClassInstaller(DIF_PROPERTYCHANGE) 失败，Win32={Marshal.GetLastWin32Error()}，InstanceId={fullInstanceId}";
                    return false;
                }

                detail = $"DIF_PROPERTYCHANGE(DICS_PROPCHANGE) 成功，InstanceId={fullInstanceId}";
                return true;
            }

            detail = $"当前 PRESENT USB 节点中未找到 {fullInstanceId}";
            return false;
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(infoSet);
        }
    }

    private static bool TryReenumerate(string instanceId, out string detail)
    {
        var locate = CM_Locate_DevNodeW(out var devInst, instanceId, 0);
        if (locate != CrSuccess)
        {
            detail = $"CM_Locate_DevNodeW({instanceId}) 失败，CONFIGRET=0x{locate:X8}";
            return false;
        }

        var result = CM_Reenumerate_DevNode(devInst, 0);
        detail = $"CM_Reenumerate_DevNode({instanceId})=0x{result:X8}";
        return result == CrSuccess;
    }

    private static string? GetDeviceInstanceId(nint infoSet, ref SpDevInfoData info)
    {
        var buffer = new StringBuilder(512);
        if (SetupDiGetDeviceInstanceIdW(infoSet, ref info, buffer, (uint)buffer.Capacity, out var required))
            return buffer.ToString();

        if (required <= buffer.Capacity) return null;
        buffer = new StringBuilder(checked((int)required + 1));
        return SetupDiGetDeviceInstanceIdW(infoSet, ref info, buffer, (uint)buffer.Capacity, out _)
            ? buffer.ToString()
            : null;
    }

    private static string? GetDeviceInstanceId(uint devInst)
    {
        if (CM_Get_Device_ID_Size(out var length, devInst, 0) != CrSuccess) return null;
        var buffer = new StringBuilder(checked((int)length + 1));
        return CM_Get_Device_IDW(devInst, buffer, (uint)buffer.Capacity, 0) == CrSuccess
            ? buffer.ToString()
            : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public uint CbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public nuint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpClassInstallHeader
    {
        public uint CbSize;
        public uint InstallFunction;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpPropChangeParams
    {
        public SpClassInstallHeader ClassInstallHeader;
        public uint StateChange;
        public uint Scope;
        public uint HwProfile;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SetupDiGetClassDevsW(
        nint classGuid,
        string? enumerator,
        nint hwndParent,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(
        nint deviceInfoSet,
        uint memberIndex,
        ref SpDevInfoData deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceIdW(
        nint deviceInfoSet,
        ref SpDevInfoData deviceInfoData,
        StringBuilder deviceInstanceId,
        uint deviceInstanceIdSize,
        out uint requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiSetClassInstallParamsW(
        nint deviceInfoSet,
        ref SpDevInfoData deviceInfoData,
        ref SpPropChangeParams classInstallParams,
        uint classInstallParamsSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiCallClassInstaller(
        uint installFunction,
        nint deviceInfoSet,
        ref SpDevInfoData deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(nint deviceInfoSet);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Parent(out uint parentDevInst, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_IDW(uint devInst, StringBuilder buffer, uint bufferLength, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Device_ID_Size(out uint length, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Reenumerate_DevNode(uint devInst, uint flags);
}
