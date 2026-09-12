#include <ntddk.h>
#include <wdf.h>
#include <Udecx.h>
#include "../shared/myusbip_ioctl.h"

DRIVER_INITIALIZE DriverEntry;
EVT_WDF_DRIVER_DEVICE_ADD MyUsbIpVhciEvtDeviceAdd;
EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL MyUsbIpVhciEvtIoDeviceControl;
EVT_UDECX_WDF_DEVICE_QUERY_USB_CAPABILITY MyUsbIpEvtQueryUsbCapability;

NTSTATUS DriverEntry(_In_ PDRIVER_OBJECT DriverObject, _In_ PUNICODE_STRING RegistryPath)
{
    WDF_DRIVER_CONFIG config;
    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_OBJECT_ATTRIBUTES_INIT(&attributes);
    WDF_DRIVER_CONFIG_INIT(&config, MyUsbIpVhciEvtDeviceAdd);
    return WdfDriverCreate(DriverObject, RegistryPath, &attributes, &config, WDF_NO_HANDLE);
}

NTSTATUS MyUsbIpVhciEvtDeviceAdd(_In_ WDFDRIVER Driver, _Inout_ PWDFDEVICE_INIT DeviceInit)
{
    UNREFERENCED_PARAMETER(Driver);

    DECLARE_CONST_UNICODE_STRING(deviceName, L"\\Device\\MyUsbIPVhci");
    DECLARE_CONST_UNICODE_STRING(symbolicLink, L"\\DosDevices\\MyUsbIPVhci");

    // UdeCx 要求在 WdfDeviceCreate 之前初始化 WDFDEVICE_INIT。
    NTSTATUS status = UdecxInitializeWdfDeviceInit(DeviceInit);
    if (!NT_SUCCESS(status)) return status;

    WdfDeviceInitSetDeviceType(DeviceInit, FILE_DEVICE_UNKNOWN);
    WdfDeviceInitSetExclusive(DeviceInit, FALSE);
    status = WdfDeviceInitAssignName(DeviceInit, &deviceName);
    if (!NT_SUCCESS(status)) return status;

    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_OBJECT_ATTRIBUTES_INIT(&attributes);
    WDFDEVICE device;
    status = WdfDeviceCreate(&DeviceInit, &attributes, &device);
    if (!NT_SUCCESS(status)) return status;

    status = WdfDeviceCreateSymbolicLink(device, &symbolicLink);
    if (!NT_SUCCESS(status)) return status;

    UDECX_WDF_DEVICE_CONFIG udeConfig;
    UDECX_WDF_DEVICE_CONFIG_INIT(&udeConfig, MyUsbIpEvtQueryUsbCapability);
    status = UdecxWdfDeviceAddUsbDeviceEmulation(device, &udeConfig);
    if (!NT_SUCCESS(status)) return status;

    WDF_IO_QUEUE_CONFIG queueConfig;
    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&queueConfig, WdfIoQueueDispatchSequential);
    queueConfig.EvtIoDeviceControl = MyUsbIpVhciEvtIoDeviceControl;
    queueConfig.PowerManaged = WdfFalse;

    WDFQUEUE queue;
    return WdfIoQueueCreate(device, &queueConfig, WDF_NO_OBJECT_ATTRIBUTES, &queue);
}

NTSTATUS MyUsbIpEvtQueryUsbCapability(
    _In_ WDFDEVICE UdecxWdfDevice,
    _In_ PGUID CapabilityType,
    _In_ ULONG OutputBufferLength,
    _Out_writes_bytes_to_opt_(OutputBufferLength, *ResultLength) PVOID OutputBuffer,
    _Out_ PULONG ResultLength)
{
    UNREFERENCED_PARAMETER(UdecxWdfDevice);
    UNREFERENCED_PARAMETER(CapabilityType);
    UNREFERENCED_PARAMETER(OutputBufferLength);
    UNREFERENCED_PARAMETER(OutputBuffer);
    *ResultLength = 0;
    return STATUS_NOT_SUPPORTED;
}

VOID MyUsbIpVhciEvtIoDeviceControl(
    _In_ WDFQUEUE Queue,
    _In_ WDFREQUEST Request,
    _In_ size_t OutputBufferLength,
    _In_ size_t InputBufferLength,
    _In_ ULONG IoControlCode)
{
    UNREFERENCED_PARAMETER(Queue);
    UNREFERENCED_PARAMETER(OutputBufferLength);
    UNREFERENCED_PARAMETER(InputBufferLength);

    NTSTATUS status = STATUS_INVALID_DEVICE_REQUEST;
    size_t information = 0;

    switch (IoControlCode)
    {
        case IOCTL_MYUSBIP_VHCI_GET_VERSION:
        {
            PULONG version = NULL;
            status = WdfRequestRetrieveOutputBuffer(Request, sizeof(ULONG), (PVOID*)&version, NULL);
            if (NT_SUCCESS(status)) {
                *version = MYUSBIP_API_VERSION;
                information = sizeof(ULONG);
            }
            break;
        }
        case IOCTL_MYUSBIP_VHCI_CREATE_PORT:
            // 下一步由 VhciUsbDevice.c 使用 UdecxUsbDeviceInitAllocate / AddDescriptor / Create
            // 创建远端设备对应的虚拟 USB Device，并为端点建立 UdeCx 队列。
            status = STATUS_NOT_IMPLEMENTED;
            break;
        case IOCTL_MYUSBIP_VHCI_REMOVE_PORT:
        case IOCTL_MYUSBIP_VHCI_GET_PENDING_URB:
        case IOCTL_MYUSBIP_VHCI_COMPLETE_URB:
        case IOCTL_MYUSBIP_VHCI_RESET_PORT:
            status = STATUS_NOT_IMPLEMENTED;
            break;
        default:
            status = STATUS_INVALID_DEVICE_REQUEST;
            break;
    }

    WdfRequestCompleteWithInformation(Request, status, information);
}
