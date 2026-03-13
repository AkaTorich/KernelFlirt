#include <ntddk.h>

static UNICODE_STRING g_DeviceName = RTL_CONSTANT_STRING(L"\\Device\\KfTestDriver");
static UNICODE_STRING g_SymLink   = RTL_CONSTANT_STRING(L"\\DosDevices\\KfTestDriver");
static PDEVICE_OBJECT g_DeviceObj = NULL;

static NTSTATUS TestDispatch(PDEVICE_OBJECT DeviceObject, PIRP Irp)
{
    UNREFERENCED_PARAMETER(DeviceObject);
    Irp->IoStatus.Status      = STATUS_SUCCESS;
    Irp->IoStatus.Information  = 0;
    IoCompleteRequest(Irp, IO_NO_INCREMENT);
    return STATUS_SUCCESS;
}

static VOID TestUnload(PDRIVER_OBJECT DriverObject)
{
    UNREFERENCED_PARAMETER(DriverObject);
    DbgPrint("[KfTest] Unloading driver\n");
    IoDeleteSymbolicLink(&g_SymLink);
    if (g_DeviceObj)
        IoDeleteDevice(g_DeviceObj);
}

NTSTATUS DriverEntry(PDRIVER_OBJECT DriverObject, PUNICODE_STRING RegistryPath)
{
    NTSTATUS status;

    UNREFERENCED_PARAMETER(RegistryPath);

    DbgPrint("[KfTest] DriverEntry called!\n");
    DbgPrint("[KfTest] DriverObject = %p\n", DriverObject);

    DriverObject->DriverUnload = TestUnload;
    DriverObject->MajorFunction[IRP_MJ_CREATE] = TestDispatch;
    DriverObject->MajorFunction[IRP_MJ_CLOSE]  = TestDispatch;

    status = IoCreateDevice(
        DriverObject,
        0,
        &g_DeviceName,
        FILE_DEVICE_UNKNOWN,
        FILE_DEVICE_SECURE_OPEN,
        FALSE,
        &g_DeviceObj);

    if (!NT_SUCCESS(status)) {
        DbgPrint("[KfTest] IoCreateDevice failed: 0x%08X\n", status);
        return status;
    }

    status = IoCreateSymbolicLink(&g_SymLink, &g_DeviceName);
    if (!NT_SUCCESS(status)) {
        DbgPrint("[KfTest] IoCreateSymbolicLink failed: 0x%08X\n", status);
        IoDeleteDevice(g_DeviceObj);
        g_DeviceObj = NULL;
        return status;
    }

    DbgPrint("[KfTest] Driver loaded successfully!\n");
    return STATUS_SUCCESS;
}
