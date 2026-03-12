/*
 * KernelFlirt - Memory operations
 * memory.c - Process memory read/write via MmCopyVirtualMemory
 *
 * For kernel-space addresses (PID 4, address >= 0xFFFF800000000000):
 *   Uses direct RtlCopyMemory with per-page MmIsAddressValid checks
 *   to avoid PAGE_FAULT_IN_NONPAGED_AREA bugcheck on paged-out or
 *   session-space pages.
 *
 * For user-space addresses:
 *   Uses MmCopyVirtualMemory as before.
 */

#include <ntddk.h>
#include "ntundoc.h"
#include "../../include/kf_shared.h"

/* Kernel-space boundary for x64 */
#define KERNEL_SPACE_START  0xFFFF800000000000ULL

/*
 * Safe kernel memory read: copies page-by-page, checking MmIsAddressValid
 * before each page to prevent bugcheck on unmapped/paged-out pages.
 * Returns number of bytes successfully copied (may be partial).
 */
static SIZE_T
KfSafeKernelRead(
    _In_  PVOID   sourceAddress,
    _Out_ PVOID   destBuffer,
    _In_  SIZE_T  size
)
{
    SIZE_T copied = 0;
    PUCHAR src = (PUCHAR)sourceAddress;
    PUCHAR dst = (PUCHAR)destBuffer;

    while (copied < size) {
        /* Bytes remaining until next page boundary */
        SIZE_T pageOffset = (ULONG_PTR)(src + copied) & (PAGE_SIZE - 1);
        SIZE_T chunkSize = PAGE_SIZE - pageOffset;
        if (chunkSize > size - copied)
            chunkSize = size - copied;

        /* Check if the start of this chunk is valid */
        if (!MmIsAddressValid(src + copied)) {
            /* Page not resident — zero-fill and skip */
            RtlZeroMemory(dst + copied, chunkSize);
            copied += chunkSize;
            continue;
        }

        /* Also check end of chunk (may cross into invalid page) */
        if (chunkSize > 1 && !MmIsAddressValid(src + copied + chunkSize - 1)) {
            RtlZeroMemory(dst + copied, chunkSize);
            copied += chunkSize;
            continue;
        }

        __try {
            RtlCopyMemory(dst + copied, src + copied, chunkSize);
        }
        __except (EXCEPTION_EXECUTE_HANDLER) {
            /* Exception during copy — zero-fill this chunk */
            RtlZeroMemory(dst + copied, chunkSize);
        }

        copied += chunkSize;
    }

    return copied;
}

NTSTATUS
KfReadMemory(
    _In_ PIRP               Irp,
    _In_ PIO_STACK_LOCATION  IoStack
)
{
    PKF_READ_MEMORY_IN  input;
    PVOID               output;
    PEPROCESS           process = NULL;
    SIZE_T              bytesRead = 0;
    NTSTATUS            status;

    /* Validate input buffer */
    if (IoStack->Parameters.DeviceIoControl.InputBufferLength < sizeof(KF_READ_MEMORY_IN)) {
        Irp->IoStatus.Information = 0;
        return STATUS_BUFFER_TOO_SMALL;
    }

    input = (PKF_READ_MEMORY_IN)Irp->AssociatedIrp.SystemBuffer;

    /* Validate output buffer */
    if (IoStack->Parameters.DeviceIoControl.OutputBufferLength < input->Size) {
        Irp->IoStatus.Information = 0;
        return STATUS_BUFFER_TOO_SMALL;
    }

    /* Limit read size to prevent abuse */
    if (input->Size == 0 || input->Size > 0x100000) { /* 1MB max */
        Irp->IoStatus.Information = 0;
        return STATUS_INVALID_PARAMETER;
    }

    output = Irp->AssociatedIrp.SystemBuffer;

    /*
     * For kernel-space addresses (PID 4): use safe direct read
     * to avoid PAGE_FAULT_IN_NONPAGED_AREA bugcheck.
     * MmCopyVirtualMemory can trigger unrecoverable page faults
     * on paged-out or session-space kernel pages.
     */
    if (input->ProcessId == 4 && input->Address >= KERNEL_SPACE_START) {
        bytesRead = KfSafeKernelRead(
            (PVOID)input->Address,
            output,
            (SIZE_T)input->Size
        );
        Irp->IoStatus.Information = bytesRead;
        return (bytesRead > 0) ? STATUS_SUCCESS : STATUS_PARTIAL_COPY;
    }

    /* User-space: use MmCopyVirtualMemory as before */
    status = PsLookupProcessByProcessId((HANDLE)(ULONG_PTR)input->ProcessId, &process);
    if (!NT_SUCCESS(status)) {
        DbgPrint("[KernelFlirt] PsLookupProcessByProcessId(%u) failed: 0x%08X\n",
                 input->ProcessId, status);
        Irp->IoStatus.Information = 0;
        return status;
    }

    __try {
        status = MmCopyVirtualMemory(
            process,
            (PVOID)input->Address,
            PsGetCurrentProcess(),
            output,
            (SIZE_T)input->Size,
            KernelMode,
            &bytesRead
        );
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        status = GetExceptionCode();
        bytesRead = 0;
    }

    ObDereferenceObject(process);

    Irp->IoStatus.Information = bytesRead;
    return status;
}

NTSTATUS
KfWriteMemory(
    _In_ PIRP               Irp,
    _In_ PIO_STACK_LOCATION  IoStack
)
{
    PKF_WRITE_MEMORY_IN input;
    PUCHAR              data;
    PEPROCESS           process = NULL;
    SIZE_T              bytesWritten = 0;
    NTSTATUS            status;
    ULONG               totalInputSize;

    /* Validate input buffer */
    if (IoStack->Parameters.DeviceIoControl.InputBufferLength < sizeof(KF_WRITE_MEMORY_IN)) {
        Irp->IoStatus.Information = 0;
        return STATUS_BUFFER_TOO_SMALL;
    }

    input = (PKF_WRITE_MEMORY_IN)Irp->AssociatedIrp.SystemBuffer;

    /* Validate data size */
    totalInputSize = sizeof(KF_WRITE_MEMORY_IN) + input->Size;
    if (IoStack->Parameters.DeviceIoControl.InputBufferLength < totalInputSize) {
        Irp->IoStatus.Information = 0;
        return STATUS_BUFFER_TOO_SMALL;
    }

    if (input->Size == 0 || input->Size > 0x100000) {
        Irp->IoStatus.Information = 0;
        return STATUS_INVALID_PARAMETER;
    }

    /* Data follows the header struct */
    data = (PUCHAR)input + sizeof(KF_WRITE_MEMORY_IN);

    /* Look up the target process */
    status = PsLookupProcessByProcessId((HANDLE)(ULONG_PTR)input->ProcessId, &process);
    if (!NT_SUCCESS(status)) {
        Irp->IoStatus.Information = 0;
        return status;
    }

    /* Write data to target process memory */
    __try {
        status = MmCopyVirtualMemory(
            PsGetCurrentProcess(),
            data,
            process,
            (PVOID)input->Address,
            (SIZE_T)input->Size,
            KernelMode,
            &bytesWritten
        );
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        status = GetExceptionCode();
        bytesWritten = 0;
    }

    ObDereferenceObject(process);

    Irp->IoStatus.Information = bytesWritten;
    return status;
}
