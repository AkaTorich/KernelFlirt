/*
 * KernelFlirt - Memory operations
 * memory.c - Process memory read/write via MmCopyVirtualMemory
 */

#include <ntddk.h>
#include "ntundoc.h"
#include "../../include/kf_shared.h"

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

    /* Look up the target process */
    status = PsLookupProcessByProcessId((HANDLE)(ULONG_PTR)input->ProcessId, &process);
    if (!NT_SUCCESS(status)) {
        DbgPrint("[KernelFlirt] PsLookupProcessByProcessId(%u) failed: 0x%08X\n",
                 input->ProcessId, status);
        Irp->IoStatus.Information = 0;
        return status;
    }

    /* Read memory from target process into our output buffer */
    output = Irp->AssociatedIrp.SystemBuffer;

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
