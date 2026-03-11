/*
 * KernelFlirt - Single-step execution
 * singlestep.c - Set TF (Trap Flag) in EFLAGS via KTRAP_FRAME
 *
 * Uses direct KTRAP_FRAME access (same as registers.c).
 * Works on suspended threads without PsGetContextThread/PsSetContextThread.
 */

#include <ntddk.h>
#include "ntundoc.h"
#include "../../include/kf_shared.h"

#define EFLAGS_TF               0x100   /* Trap Flag - bit 8 */
#define KTHREAD_TRAPFRAME_OFFSET 0x90
#define TF_EFLAGS               0x178

NTSTATUS
KfSingleStep(
    _In_ PIRP               Irp,
    _In_ PIO_STACK_LOCATION  IoStack
)
{
    PKF_THREAD_TARGET   input;
    PETHREAD            thread = NULL;
    NTSTATUS            status;
    PVOID               pKThread;
    PVOID               pTrapFrame;

    if (IoStack->Parameters.DeviceIoControl.InputBufferLength < sizeof(KF_THREAD_TARGET)) {
        Irp->IoStatus.Information = 0;
        return STATUS_BUFFER_TOO_SMALL;
    }

    input = (PKF_THREAD_TARGET)Irp->AssociatedIrp.SystemBuffer;

    status = PsLookupThreadByThreadId((HANDLE)(ULONG_PTR)input->ThreadId, &thread);
    if (!NT_SUCCESS(status)) {
        Irp->IoStatus.Information = 0;
        return status;
    }

    pKThread = (PVOID)thread;

    __try {
        /* Read KTHREAD.TrapFrame pointer at offset 0x90 */
        pTrapFrame = *(PVOID *)((UCHAR *)pKThread + KTHREAD_TRAPFRAME_OFFSET);

        if (pTrapFrame == NULL) {
            status = STATUS_UNSUCCESSFUL;
        } else {
            /* Set the Trap Flag in EFLAGS within KTRAP_FRAME */
            ULONG64 eflags = *(ULONG64 *)((UCHAR *)pTrapFrame + TF_EFLAGS);
            eflags |= EFLAGS_TF;
            *(ULONG64 *)((UCHAR *)pTrapFrame + TF_EFLAGS) = eflags;

            status = STATUS_SUCCESS;
            DbgPrint("KernelFlirt: SingleStep TID %u - TF set (EFLAGS=0x%llX)\n",
                     input->ThreadId, eflags);
        }
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        status = GetExceptionCode();
    }

    ObDereferenceObject(thread);
    Irp->IoStatus.Information = 0;
    return status;
}
