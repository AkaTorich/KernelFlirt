/*
 * KernelFlirt - Kernel Driver
 * main.c - DriverEntry, DriverUnload, dispatch table
 */

#include <ntddk.h>
#include "../../include/kf_shared.h"
#include "debughook.h"

/* Forward declarations */
DRIVER_INITIALIZE DriverEntry;
DRIVER_UNLOAD     KfUnload;

_Dispatch_type_(IRP_MJ_CREATE)
_Dispatch_type_(IRP_MJ_CLOSE)
DRIVER_DISPATCH   KfCreateClose;

_Dispatch_type_(IRP_MJ_DEVICE_CONTROL)
DRIVER_DISPATCH   KfDeviceControl;

/* Defined in ioctl.c */
extern NTSTATUS KfDispatchIoctl(PDEVICE_OBJECT DeviceObject, PIRP Irp);

/* Globals */
PDEVICE_OBJECT g_DeviceObject = NULL;

NTSTATUS
DriverEntry(
    _In_ PDRIVER_OBJECT  DriverObject,
    _In_ PUNICODE_STRING RegistryPath
)
{
    NTSTATUS        status;
    UNICODE_STRING  deviceName;
    UNICODE_STRING  symlinkName;

    UNREFERENCED_PARAMETER(RegistryPath);

    DbgPrint("[KernelFlirt] DriverEntry: loading driver v%X\n", KF_VERSION);

    /* Create device object */
    RtlInitUnicodeString(&deviceName, KF_DEVICE_NAME);

    status = IoCreateDevice(
        DriverObject,
        0,                          /* DeviceExtensionSize */
        &deviceName,
        FILE_DEVICE_UNKNOWN,
        FILE_DEVICE_SECURE_OPEN,
        FALSE,                      /* Exclusive */
        &g_DeviceObject
    );

    if (!NT_SUCCESS(status)) {
        DbgPrint("[KernelFlirt] IoCreateDevice failed: 0x%08X\n", status);
        return status;
    }

    /* Create symbolic link for usermode access */
    RtlInitUnicodeString(&symlinkName, KF_SYMLINK_NAME);

    status = IoCreateSymbolicLink(&symlinkName, &deviceName);

    if (!NT_SUCCESS(status)) {
        DbgPrint("[KernelFlirt] IoCreateSymbolicLink failed: 0x%08X\n", status);
        IoDeleteDevice(g_DeviceObject);
        g_DeviceObject = NULL;
        return status;
    }

    /* Set dispatch routines */
    DriverObject->MajorFunction[IRP_MJ_CREATE]         = KfCreateClose;
    DriverObject->MajorFunction[IRP_MJ_CLOSE]          = KfCreateClose;
    DriverObject->MajorFunction[IRP_MJ_DEVICE_CONTROL] = KfDeviceControl;
    DriverObject->DriverUnload                          = KfUnload;

    /* Use direct I/O for better performance with large buffers */
    g_DeviceObject->Flags |= DO_DIRECT_IO;
    g_DeviceObject->Flags &= ~DO_DEVICE_INITIALIZING;

    /* Initialize debug hook subsystem */
    KfDebugHookInit();

    DbgPrint("[KernelFlirt] Driver loaded successfully\n");
    return STATUS_SUCCESS;
}

VOID
KfUnload(
    _In_ PDRIVER_OBJECT DriverObject
)
{
    UNICODE_STRING symlinkName;

    UNREFERENCED_PARAMETER(DriverObject);

    DbgPrint("[KernelFlirt] Unloading driver\n");

    /* Remove debug hook before destroying device */
    KfDebugHookCleanup();

    RtlInitUnicodeString(&symlinkName, KF_SYMLINK_NAME);
    IoDeleteSymbolicLink(&symlinkName);

    if (g_DeviceObject) {
        IoDeleteDevice(g_DeviceObject);
        g_DeviceObject = NULL;
    }

    DbgPrint("[KernelFlirt] Driver unloaded\n");
}

NTSTATUS
KfCreateClose(
    _In_ PDEVICE_OBJECT DeviceObject,
    _In_ PIRP           Irp
)
{
    UNREFERENCED_PARAMETER(DeviceObject);

    Irp->IoStatus.Status      = STATUS_SUCCESS;
    Irp->IoStatus.Information = 0;
    IoCompleteRequest(Irp, IO_NO_INCREMENT);

    return STATUS_SUCCESS;
}

NTSTATUS
KfDeviceControl(
    _In_ PDEVICE_OBJECT DeviceObject,
    _In_ PIRP           Irp
)
{
    return KfDispatchIoctl(DeviceObject, Irp);
}
