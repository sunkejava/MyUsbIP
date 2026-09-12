#include <ntddk.h>
#include <wdf.h>
#include "../shared/myusbip_ioctl.h"

DRIVER_INITIALIZE DriverEntry;
EVT_WDF_DRIVER_DEVICE_ADD MyUsbIpVhciEvtDeviceAdd;
EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL MyUsbIpVhciEvtIoDeviceControl;

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

    WdfDeviceInitSetDeviceType(DeviceInit, FILE_DEVICE_UNKNOWN);
    WdfDeviceInitSetExclusive(DeviceInit, FALSE);
    NTSTATUS status = WdfDeviceInitAssignName(DeviceInit, &deviceName);
    if (!NT_SUCCESS(status)) return status;

    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_OBJECT_ATTRIBUTES_INIT(&attributes);
    WDFDEVICE device;
    status = WdfDeviceCreate(&DeviceInit, &attributes, &device);
    if (!NT_SUCCESS(status)) return status;

    status = WdfDeviceCreateSymbolicLink(device, &symbolicLink);
    if (!NT_SUCCESS(status)) return status;

    WDF_IO_QUEUE_CONFIG queueConfig;
    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&queueConfig, WdfIoQueueDispatchParallel);
    queueConfig.EvtIoDeviceControl = MyUsbIpVhciEvtIoDeviceControl;

    WDFQUEUE queue;
    return WdfIoQueueCreate(device, &queueConfig, WDF_NO_OBJECT_ATTRIBUTES, &queue);
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
        case IOCTL_MYUSBIP_VHCI_REMOVE_PORT:
        case IOCTL_MYUSBIP_VHCI_GET_PENDING_URB:
        case IOCTL_MYUSBIP_VHCI_COMPLETE_URB:
        case IOCTL_MYUSBIP_VHCI_RESET_PORT:
            // 端口/PDO/URB 数据路径在 VhciBus/VhciPdo/UsbUrbQueue 模块实现。
            status = STATUS_NOT_IMPLEMENTED;
            break;
        default:
            status = STATUS_INVALID_DEVICE_REQUEST;
            break;
    }

    WdfRequestCompleteWithInformation(Request, status, information);
}
