#include <ntddk.h>
#include <wdf.h>
#include "../shared/myusbip_ioctl.h"

DRIVER_INITIALIZE DriverEntry;
EVT_WDF_DRIVER_DEVICE_ADD MyUsbIpEvtDeviceAdd;
EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL MyUsbIpEvtIoDeviceControl;

static NTSTATUS MyUsbIpCreateControlDevice(_In_ WDFDRIVER Driver);

NTSTATUS DriverEntry(_In_ PDRIVER_OBJECT DriverObject, _In_ PUNICODE_STRING RegistryPath)
{
    WDF_DRIVER_CONFIG config;
    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_OBJECT_ATTRIBUTES_INIT(&attributes);
    WDF_DRIVER_CONFIG_INIT(&config, MyUsbIpEvtDeviceAdd);

    NTSTATUS status = WdfDriverCreate(DriverObject, RegistryPath, &attributes, &config, WDF_NO_HANDLE);
    if (!NT_SUCCESS(status)) return status;

    return MyUsbIpCreateControlDevice(WdfGetDriver());
}

NTSTATUS MyUsbIpEvtDeviceAdd(_In_ WDFDRIVER Driver, _Inout_ PWDFDEVICE_INIT DeviceInit)
{
    UNREFERENCED_PARAMETER(Driver);

    // Exporter 作为 USB 设备过滤驱动参与 PnP 栈。
    // 当前阶段先建立安全的过滤设备对象，后续在 EvtIoInternalDeviceControl 中拦截/镜像 URB。
    WdfFdoInitSetFilter(DeviceInit);

    WDFDEVICE device;
    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_OBJECT_ATTRIBUTES_INIT(&attributes);
    return WdfDeviceCreate(&DeviceInit, &attributes, &device);
}

static NTSTATUS MyUsbIpCreateControlDevice(_In_ WDFDRIVER Driver)
{
    DECLARE_CONST_UNICODE_STRING(deviceName, L"\\Device\\MyUsbIPExporter");
    DECLARE_CONST_UNICODE_STRING(symbolicLink, L"\\DosDevices\\MyUsbIPExporter");

    PWDFDEVICE_INIT init = WdfControlDeviceInitAllocate(Driver, &SDDL_DEVOBJ_SYS_ALL_ADM_RWX_WORLD_RW_RES_R);
    if (init == NULL) return STATUS_INSUFFICIENT_RESOURCES;

    NTSTATUS status = WdfDeviceInitAssignName(init, &deviceName);
    if (!NT_SUCCESS(status)) {
        WdfDeviceInitFree(init);
        return status;
    }

    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_OBJECT_ATTRIBUTES_INIT(&attributes);
    WDFDEVICE device;
    status = WdfDeviceCreate(&init, &attributes, &device);
    if (!NT_SUCCESS(status)) return status;

    status = WdfDeviceCreateSymbolicLink(device, &symbolicLink);
    if (!NT_SUCCESS(status)) return status;

    WDF_IO_QUEUE_CONFIG queueConfig;
    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&queueConfig, WdfIoQueueDispatchParallel);
    queueConfig.EvtIoDeviceControl = MyUsbIpEvtIoDeviceControl;

    WDFQUEUE queue;
    status = WdfIoQueueCreate(device, &queueConfig, WDF_NO_OBJECT_ATTRIBUTES, &queue);
    if (!NT_SUCCESS(status)) return status;

    WdfControlFinishInitializing(device);
    return STATUS_SUCCESS;
}

VOID MyUsbIpEvtIoDeviceControl(
    _In_ WDFQUEUE Queue,
    _In_ WDFREQUEST Request,
    _In_ size_t OutputBufferLength,
    _In_ size_t InputBufferLength,
    _In_ ULONG IoControlCode)
{
    UNREFERENCED_PARAMETER(Queue);
    UNREFERENCED_PARAMETER(InputBufferLength);

    NTSTATUS status = STATUS_INVALID_DEVICE_REQUEST;
    size_t information = 0;

    switch (IoControlCode)
    {
        case IOCTL_MYUSBIP_EXPORTER_GET_VERSION:
        {
            PULONG version = NULL;
            status = WdfRequestRetrieveOutputBuffer(Request, sizeof(ULONG), (PVOID*)&version, NULL);
            if (NT_SUCCESS(status)) {
                *version = MYUSBIP_API_VERSION;
                information = sizeof(ULONG);
            }
            break;
        }
        case IOCTL_MYUSBIP_EXPORTER_LIST_DEVICES:
        {
            // 先返回合法空列表，后续由过滤设备上下文注册表填充真实 USB PDO。
            PMYUSBIP_DEVICE_LIST_HEADER header = NULL;
            status = WdfRequestRetrieveOutputBuffer(Request, sizeof(MYUSBIP_DEVICE_LIST_HEADER), (PVOID*)&header, NULL);
            if (NT_SUCCESS(status)) {
                header->ApiVersion = MYUSBIP_API_VERSION;
                header->Count = 0;
                information = sizeof(MYUSBIP_DEVICE_LIST_HEADER);
            }
            break;
        }
        case IOCTL_MYUSBIP_EXPORTER_SHARE:
        case IOCTL_MYUSBIP_EXPORTER_UNSHARE:
        case IOCTL_MYUSBIP_EXPORTER_SUBMIT_URB:
        case IOCTL_MYUSBIP_EXPORTER_CANCEL_URB:
        case IOCTL_MYUSBIP_EXPORTER_GET_COMPLETION:
            // ABI 已固定；真实 URB 数据路径在 ExportDevice/UsbUrbQueue 模块继续实现。
            status = STATUS_NOT_IMPLEMENTED;
            break;
        default:
            status = STATUS_INVALID_DEVICE_REQUEST;
            break;
    }

    UNREFERENCED_PARAMETER(OutputBufferLength);
    WdfRequestCompleteWithInformation(Request, status, information);
}
