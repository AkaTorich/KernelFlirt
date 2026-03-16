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

/*
 * Safe kernel memory write: uses MDL to bypass page protection (read-only .text).
 * Writes page-by-page, checking MmIsAddressValid before each page.
 * Returns number of bytes successfully written (may be partial).
 */
static SIZE_T
KfSafeKernelWrite(
    _In_  PVOID   destAddress,
    _In_  PVOID   srcBuffer,
    _In_  SIZE_T  size
)
{
    SIZE_T  written = 0;
    PUCHAR  dst = (PUCHAR)destAddress;
    PUCHAR  src = (PUCHAR)srcBuffer;

    while (written < size) {
        SIZE_T pageOffset = (ULONG_PTR)(dst + written) & (PAGE_SIZE - 1);
        SIZE_T chunkSize  = PAGE_SIZE - pageOffset;
        PMDL   mdl        = NULL;
        PVOID  mapped      = NULL;

        if (chunkSize > size - written)
            chunkSize = size - written;

        if (!MmIsAddressValid(dst + written))
            break;

        __try {
            mdl = IoAllocateMdl(dst + written, (ULONG)chunkSize, FALSE, FALSE, NULL);
            if (!mdl) break;

            MmProbeAndLockPages(mdl, KernelMode, IoReadAccess);

            mapped = MmMapLockedPagesSpecifyCache(
                mdl, KernelMode, MmNonCached, NULL, FALSE, NormalPagePriority);
            if (!mapped) {
                MmUnlockPages(mdl);
                IoFreeMdl(mdl);
                break;
            }

            if (NT_SUCCESS(MmProtectMdlSystemAddress(mdl, PAGE_READWRITE))) {
                RtlCopyMemory(mapped, src + written, chunkSize);
                written += chunkSize;
            } else {
                MmUnmapLockedPages(mapped, mdl);
                MmUnlockPages(mdl);
                IoFreeMdl(mdl);
                break;
            }

            MmUnmapLockedPages(mapped, mdl);
            MmUnlockPages(mdl);
            IoFreeMdl(mdl);
        }
        __except (EXCEPTION_EXECUTE_HANDLER) {
            if (mapped && mdl) MmUnmapLockedPages(mapped, mdl);
            if (mdl) {
                __try { MmUnlockPages(mdl); } __except(EXCEPTION_EXECUTE_HANDLER) {}
                IoFreeMdl(mdl);
            }
            break;
        }
    }

    return written;
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

    /*
     * For kernel-space addresses (PID 4): use MDL-based write
     * to bypass read-only page protection on .text sections.
     * MmCopyVirtualMemory does NOT bypass page protection and
     * causes ATTEMPTED_WRITE_TO_READONLY_MEMORY bugcheck.
     */
    if (input->ProcessId == 4 && input->Address >= KERNEL_SPACE_START) {
        bytesWritten = KfSafeKernelWrite(
            (PVOID)input->Address,
            data,
            (SIZE_T)input->Size
        );
        Irp->IoStatus.Information = bytesWritten;
        return (bytesWritten > 0) ? STATUS_SUCCESS : STATUS_PARTIAL_COPY;
    }

    /*
     * User-space: use KeStackAttachProcess + ZwProtectVirtualMemory
     * to handle code pages (PAGE_EXECUTE_READ) that MmCopyVirtualMemory
     * cannot write to. Same approach as KfWriteProcessMemory in breakpoint.c.
     */
    status = PsLookupProcessByProcessId((HANDLE)(ULONG_PTR)input->ProcessId, &process);
    if (!NT_SUCCESS(status)) {
        Irp->IoStatus.Information = 0;
        return status;
    }

    {
        KAPC_STATE  apcState;
        PVOID       targetAddr = (PVOID)input->Address;
        ULONG       oldProtect = 0;
        SIZE_T      regionSize = (SIZE_T)input->Size;

        KeStackAttachProcess(process, &apcState);

        __try {
            /* Change protection to RWX to handle code pages */
            status = ZwProtectVirtualMemory(
                ZwCurrentProcess(), &targetAddr, &regionSize,
                PAGE_EXECUTE_READWRITE, &oldProtect);

            if (!NT_SUCCESS(status)) {
                /* Protection change failed — fall back to direct copy attempt */
                ProbeForWrite((PVOID)(ULONG_PTR)input->Address, (SIZE_T)input->Size, 1);
                RtlCopyMemory((PVOID)(ULONG_PTR)input->Address, data, (SIZE_T)input->Size);
                bytesWritten = (SIZE_T)input->Size;
                status = STATUS_SUCCESS;
                __leave;
            }

            /* Write to the now-writable page */
            ProbeForWrite((PVOID)(ULONG_PTR)input->Address, (SIZE_T)input->Size, 1);
            RtlCopyMemory((PVOID)(ULONG_PTR)input->Address, data, (SIZE_T)input->Size);
            bytesWritten = (SIZE_T)input->Size;
            status = STATUS_SUCCESS;

            /* Restore original protection */
            targetAddr = (PVOID)input->Address;
            regionSize = (SIZE_T)input->Size;
            ZwProtectVirtualMemory(
                ZwCurrentProcess(), &targetAddr, &regionSize,
                oldProtect, &oldProtect);
        }
        __except (EXCEPTION_EXECUTE_HANDLER) {
            status = GetExceptionCode();
            bytesWritten = 0;
        }

        KeUnstackDetachProcess(&apcState);
    }

    ObDereferenceObject(process);

    Irp->IoStatus.Information = bytesWritten;
    return status;
}

/* ------------------------------------------------------------------ */
/* IOCTL_KF_PROTECT_MEMORY — change page protection via ZwProtectVirtualMemory */
/* ------------------------------------------------------------------ */
NTSTATUS
KfProtectMemory(
    PIRP                Irp,
    PIO_STACK_LOCATION  IoStack)
{
    PKF_PROTECT_MEMORY_IN  input;
    PKF_PROTECT_MEMORY_OUT output;
    PEPROCESS process = NULL;
    NTSTATUS  status;
    ULONG     oldProtect = 0;

    if (IoStack->Parameters.DeviceIoControl.InputBufferLength < sizeof(KF_PROTECT_MEMORY_IN)) {
        return STATUS_BUFFER_TOO_SMALL;
    }
    if (IoStack->Parameters.DeviceIoControl.OutputBufferLength < sizeof(KF_PROTECT_MEMORY_OUT)) {
        return STATUS_BUFFER_TOO_SMALL;
    }

    input  = (PKF_PROTECT_MEMORY_IN)Irp->AssociatedIrp.SystemBuffer;
    output = (PKF_PROTECT_MEMORY_OUT)Irp->AssociatedIrp.SystemBuffer;

    status = PsLookupProcessByProcessId((HANDLE)(ULONG_PTR)input->ProcessId, &process);
    if (!NT_SUCCESS(status)) {
        Irp->IoStatus.Information = 0;
        return status;
    }

    {
        KAPC_STATE  apcState;
        PVOID       baseAddr = (PVOID)input->Address;
        SIZE_T      regionSize = (SIZE_T)input->Size;

        KeStackAttachProcess(process, &apcState);

        __try {
            status = ZwProtectVirtualMemory(
                ZwCurrentProcess(), &baseAddr, &regionSize,
                input->NewProtection, &oldProtect);
        }
        __except (EXCEPTION_EXECUTE_HANDLER) {
            status = GetExceptionCode();
        }

        KeUnstackDetachProcess(&apcState);
    }

    ObDereferenceObject(process);

    if (NT_SUCCESS(status)) {
        output->OldProtection = oldProtect;
        Irp->IoStatus.Information = sizeof(KF_PROTECT_MEMORY_OUT);
    } else {
        Irp->IoStatus.Information = 0;
    }

    return status;
}
