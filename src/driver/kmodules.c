/*
 * KernelFlirt - Kernel module enumeration
 * kmodules.c - Enumerate loaded kernel drivers via SystemModuleInformation
 */

#include <ntddk.h>
#include "ntundoc.h"
#include "../../include/kf_shared.h"

#define SystemModuleInformation 11

/* RTL_PROCESS_MODULE_INFORMATION - per-module entry returned by ZwQuerySystemInformation */
typedef struct _RTL_PROCESS_MODULE_INFORMATION {
    HANDLE  Section;
    PVOID   MappedBase;
    PVOID   ImageBase;
    ULONG   ImageSize;
    ULONG   Flags;
    USHORT  LoadOrderIndex;
    USHORT  InitOrderIndex;
    USHORT  LoadCount;
    USHORT  OffsetToFileName;
    UCHAR   FullPathName[256];
} RTL_PROCESS_MODULE_INFORMATION, *PRTL_PROCESS_MODULE_INFORMATION;

/* RTL_PROCESS_MODULES - header + array */
typedef struct _RTL_PROCESS_MODULES {
    ULONG   NumberOfModules;
    RTL_PROCESS_MODULE_INFORMATION Modules[1];
} RTL_PROCESS_MODULES, *PRTL_PROCESS_MODULES;

NTSTATUS
KfEnumKernelModules(
    _In_ PIRP               Irp,
    _In_ PIO_STACK_LOCATION  IoStack
)
{
    PKF_KERNEL_MODULE_ENTRY outputEntries;
    NTSTATUS                status;
    ULONG                   maxEntries;
    ULONG                   count = 0;
    PVOID                   buffer = NULL;
    ULONG                   bufferSize = 0x10000;  /* 64KB initial */
    ULONG                   returnLength = 0;

    outputEntries = (PKF_KERNEL_MODULE_ENTRY)Irp->AssociatedIrp.SystemBuffer;
    maxEntries = IoStack->Parameters.DeviceIoControl.OutputBufferLength / sizeof(KF_KERNEL_MODULE_ENTRY);

    if (maxEntries == 0) {
        Irp->IoStatus.Information = 0;
        return STATUS_BUFFER_TOO_SMALL;
    }

    /* Query kernel modules */
    buffer = ExAllocatePoolWithTag(NonPagedPool, bufferSize, 'mKkK');
    if (!buffer) {
        Irp->IoStatus.Information = 0;
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    status = ZwQuerySystemInformation(SystemModuleInformation, buffer, bufferSize, &returnLength);
    if (status == STATUS_INFO_LENGTH_MISMATCH) {
        /* Лимит расширения: 16 МБ хватит на тысячи модулей ядра, при этом
         * подделанный returnLength = 0xFFFFFFFF не вызовет integer overflow
         * и не закажет огромный nonpaged pool. */
        if (returnLength == 0 || returnLength > 0x1000000UL) {
            ExFreePoolWithTag(buffer, 'mKkK');
            Irp->IoStatus.Information = 0;
            return STATUS_INSUFFICIENT_RESOURCES;
        }
        ExFreePoolWithTag(buffer, 'mKkK');
        bufferSize = returnLength + 0x1000;
        buffer = ExAllocatePoolWithTag(NonPagedPool, bufferSize, 'mKkK');
        if (!buffer) {
            Irp->IoStatus.Information = 0;
            return STATUS_INSUFFICIENT_RESOURCES;
        }
        status = ZwQuerySystemInformation(SystemModuleInformation, buffer, bufferSize, &returnLength);
    }

    if (!NT_SUCCESS(status)) {
        ExFreePoolWithTag(buffer, 'mKkK');
        Irp->IoStatus.Information = 0;
        return status;
    }

    /* Copy module entries to output buffer */
    {
        PRTL_PROCESS_MODULES modules = (PRTL_PROCESS_MODULES)buffer;
        ULONG i;

        for (i = 0; i < modules->NumberOfModules && count < maxEntries; i++) {
            PRTL_PROCESS_MODULE_INFORMATION mod = &modules->Modules[i];

            outputEntries[count].BaseAddress   = (ULONG64)mod->ImageBase;
            outputEntries[count].Size          = mod->ImageSize;
            outputEntries[count].LoadOrderIndex = mod->LoadOrderIndex;

            /* Copy filename (just the name, not full path) */
            RtlZeroMemory(outputEntries[count].Name, sizeof(outputEntries[count].Name));

            /* OffsetToFileName points to the filename within FullPathName */
            const char *fileName = (const char *)&mod->FullPathName[mod->OffsetToFileName];
            ULONG nameLen = (ULONG)strlen(fileName);
            if (nameLen >= KF_MAX_KMOD_NAME)
                nameLen = KF_MAX_KMOD_NAME - 1;

            RtlCopyMemory(outputEntries[count].Name, fileName, nameLen);

            count++;
        }
    }

    ExFreePoolWithTag(buffer, 'mKkK');

    Irp->IoStatus.Information = count * sizeof(KF_KERNEL_MODULE_ENTRY);
    return STATUS_SUCCESS;
}
