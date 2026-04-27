/*
 * KernelFlirt - Process enumeration
 * process.c - Enumerate running processes via SystemProcessInformation
 *
 * Modelled after kmodules.c which works reliably.
 */

#include <ntddk.h>
#include "ntundoc.h"
#include "../../include/kf_shared.h"

#define SystemProcessInformation 5

/* Minimal struct — only the fields we actually read.
 * Using UCHAR padding instead of individual fields to match
 * the exact offsets on x64 Windows 10/11. */
typedef struct _SPI_MINIMAL {
    ULONG           NextEntryOffset;    /* 0x00 */
    ULONG           NumberOfThreads;    /* 0x04 */
    UCHAR           _pad1[48];          /* 0x08 — skip to ImageName at 0x38 */
    UNICODE_STRING  ImageName;          /* 0x38 (16 bytes on x64) */
    LONG            BasePriority;       /* 0x48 */
    UCHAR           _pad2[4];           /* 0x4C — alignment for HANDLE */
    HANDLE          UniqueProcessId;    /* 0x50 */
    HANDLE          InheritedFrom;      /* 0x58 */
    ULONG           HandleCount;        /* 0x60 */
    ULONG           SessionId;          /* 0x64 */
    ULONG_PTR       UniqueProcessKey;   /* 0x68 */
    SIZE_T          PeakVirtualSize;    /* 0x70 */
} SPI_MINIMAL, *PSPI_MINIMAL;

NTSTATUS
KfEnumProcesses(
    _In_ PIRP               Irp,
    _In_ PIO_STACK_LOCATION  IoStack
)
{
    PKF_PROCESS_ENTRY   outputEntries;
    NTSTATUS            status;
    ULONG               maxEntries;
    ULONG               count = 0;
    PVOID               buffer = NULL;
    ULONG               bufferSize = 0x40000;  /* 256KB initial */
    ULONG               returnLength = 0;

    outputEntries = (PKF_PROCESS_ENTRY)Irp->AssociatedIrp.SystemBuffer;
    maxEntries = IoStack->Parameters.DeviceIoControl.OutputBufferLength / sizeof(KF_PROCESS_ENTRY);

    if (maxEntries == 0) {
        Irp->IoStatus.Information = 0;
        return STATUS_BUFFER_TOO_SMALL;
    }

    /* Allocate buffer — same pattern as kmodules.c */
    buffer = ExAllocatePoolWithTag(NonPagedPool, bufferSize, 'ePkK');
    if (!buffer) {
        Irp->IoStatus.Information = 0;
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    status = ZwQuerySystemInformation(SystemProcessInformation, buffer, bufferSize, &returnLength);
    if (status == STATUS_INFO_LENGTH_MISMATCH) {
        /* Лимит: 64 МБ — больше, чем когда-либо может занять SystemProcessInformation. */
        if (returnLength == 0 || returnLength > 0x4000000UL) {
            ExFreePoolWithTag(buffer, 'ePkK');
            Irp->IoStatus.Information = 0;
            return STATUS_INSUFFICIENT_RESOURCES;
        }
        ExFreePoolWithTag(buffer, 'ePkK');
        bufferSize = returnLength + 0x10000;
        buffer = ExAllocatePoolWithTag(NonPagedPool, bufferSize, 'ePkK');
        if (!buffer) {
            Irp->IoStatus.Information = 0;
            return STATUS_INSUFFICIENT_RESOURCES;
        }
        status = ZwQuerySystemInformation(SystemProcessInformation, buffer, bufferSize, &returnLength);
    }

    if (!NT_SUCCESS(status)) {
        ExFreePoolWithTag(buffer, 'ePkK');
        Irp->IoStatus.Information = 0;
        return status;
    }

    /* Walk process entries */
    {
        PSPI_MINIMAL proc = (PSPI_MINIMAL)buffer;

        __try {
            for (;;) {
                if (count >= maxEntries)
                    break;

                RtlZeroMemory(&outputEntries[count], sizeof(KF_PROCESS_ENTRY));

                outputEntries[count].ProcessId = (ULONG)(ULONG_PTR)proc->UniqueProcessId;
                outputEntries[count].SessionId = proc->SessionId;
                outputEntries[count].PeakVirtualSize = (ULONG64)proc->PeakVirtualSize;

                /* Copy process name */
                if (proc->ImageName.Buffer != NULL && proc->ImageName.Length > 0) {
                    ULONG copyLen = proc->ImageName.Length;
                    if (copyLen > (KF_MAX_PROCESS_NAME - 1) * sizeof(WCHAR))
                        copyLen = (KF_MAX_PROCESS_NAME - 1) * sizeof(WCHAR);
                    RtlCopyMemory(outputEntries[count].Name,
                                  proc->ImageName.Buffer,
                                  copyLen);
                }

                count++;

                if (proc->NextEntryOffset == 0)
                    break;

                proc = (PSPI_MINIMAL)((UCHAR *)proc + proc->NextEntryOffset);
            }
        }
        __except (EXCEPTION_EXECUTE_HANDLER) {
            DbgPrint("[KernelFlirt] Exception 0x%08X in KfEnumProcesses\n",
                     GetExceptionCode());
        }
    }

    ExFreePoolWithTag(buffer, 'ePkK');

    Irp->IoStatus.Information = count * sizeof(KF_PROCESS_ENTRY);
    return STATUS_SUCCESS;
}
