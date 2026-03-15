/*
 * KernelFlirt - IOCTL dispatcher
 * ioctl.c - Routes IOCTLs to handler functions
 */

#include <ntddk.h>
#include "../../include/kf_shared.h"
#include "debughook.h"

/* Forward declarations for handlers (implemented in other files) */
extern NTSTATUS KfReadMemory(PIRP Irp, PIO_STACK_LOCATION IoStack);
extern NTSTATUS KfWriteMemory(PIRP Irp, PIO_STACK_LOCATION IoStack);
extern NTSTATUS KfSetBreakpoint(PIRP Irp, PIO_STACK_LOCATION IoStack);
extern NTSTATUS KfRemoveBreakpoint(PIRP Irp, PIO_STACK_LOCATION IoStack);
extern NTSTATUS KfSingleStep(PIRP Irp, PIO_STACK_LOCATION IoStack);
extern NTSTATUS KfReadRegisters(PIRP Irp, PIO_STACK_LOCATION IoStack);
extern NTSTATUS KfWriteRegisters(PIRP Irp, PIO_STACK_LOCATION IoStack);
extern NTSTATUS KfEnumModules(PIRP Irp, PIO_STACK_LOCATION IoStack);
extern NTSTATUS KfEnumKernelModules(PIRP Irp, PIO_STACK_LOCATION IoStack);
extern NTSTATUS KfEnumThreads(PIRP Irp, PIO_STACK_LOCATION IoStack);
extern NTSTATUS KfSuspendThread(PIRP Irp, PIO_STACK_LOCATION IoStack);
extern NTSTATUS KfResumeThread(PIRP Irp, PIO_STACK_LOCATION IoStack);
extern NTSTATUS KfEnumProcesses(PIRP Irp, PIO_STACK_LOCATION IoStack);
extern NTSTATUS KfGetPebAddress(PIRP Irp, PIO_STACK_LOCATION IoStack);
extern NTSTATUS KfClearDebugPort(PIRP Irp, PIO_STACK_LOCATION IoStack);
extern NTSTATUS KfClearThreadHide(PIRP Irp, PIO_STACK_LOCATION IoStack);
extern void KfRemoveAllBreakpoints(void);

NTSTATUS
KfDispatchIoctl(
    _In_ PDEVICE_OBJECT DeviceObject,
    _In_ PIRP           Irp
)
{
    NTSTATUS            status;
    PIO_STACK_LOCATION  ioStack;
    ULONG               ioctl;

    UNREFERENCED_PARAMETER(DeviceObject);

    ioStack = IoGetCurrentIrpStackLocation(Irp);
    ioctl   = ioStack->Parameters.DeviceIoControl.IoControlCode;

    /*
     * Re-assert KdDebuggerEnabled=TRUE on every IOCTL when hook is active.
     * DbgPrint calls in IOCTL handlers (ReadMemory, SetBreakpoint, etc.)
     * go through the KD transport which may detect no real debugger and
     * reset KdDebuggerEnabled=FALSE. Without this, KiDispatchException
     * skips calling our hook for user-mode exceptions (INT3 from debuggee).
     */
    KfReassertDebugFlags();

    switch (ioctl) {

    case IOCTL_KF_PING: {
        PKF_PING_OUT pingOut;

        if (ioStack->Parameters.DeviceIoControl.OutputBufferLength < sizeof(KF_PING_OUT)) {
            status = STATUS_BUFFER_TOO_SMALL;
            Irp->IoStatus.Information = 0;
            break;
        }

        pingOut = (PKF_PING_OUT)Irp->AssociatedIrp.SystemBuffer;
        pingOut->Version = KF_VERSION;
        pingOut->Magic   = KF_MAGIC;

        Irp->IoStatus.Information = sizeof(KF_PING_OUT);
        status = STATUS_SUCCESS;
        break;
    }

    case IOCTL_KF_READ_MEMORY:
        status = KfReadMemory(Irp, ioStack);
        break;

    case IOCTL_KF_WRITE_MEMORY:
        status = KfWriteMemory(Irp, ioStack);
        break;

    case IOCTL_KF_SET_BREAKPOINT:
        status = KfSetBreakpoint(Irp, ioStack);
        break;

    case IOCTL_KF_REMOVE_BREAKPOINT:
        status = KfRemoveBreakpoint(Irp, ioStack);
        break;

    case IOCTL_KF_SINGLE_STEP:
        status = KfSingleStep(Irp, ioStack);
        break;

    case IOCTL_KF_READ_REGISTERS:
        status = KfReadRegisters(Irp, ioStack);
        break;

    case IOCTL_KF_WRITE_REGISTERS:
        status = KfWriteRegisters(Irp, ioStack);
        break;

    case IOCTL_KF_ENUM_MODULES:
        status = KfEnumModules(Irp, ioStack);
        break;

    case IOCTL_KF_ENUM_KERNEL_MODULES:
        status = KfEnumKernelModules(Irp, ioStack);
        break;

    case IOCTL_KF_ENUM_THREADS:
        status = KfEnumThreads(Irp, ioStack);
        break;

    case IOCTL_KF_SUSPEND_THREAD:
        status = KfSuspendThread(Irp, ioStack);
        break;

    case IOCTL_KF_RESUME_THREAD:
        status = KfResumeThread(Irp, ioStack);
        break;

    case IOCTL_KF_ENUM_PROCESSES:
        status = KfEnumProcesses(Irp, ioStack);
        break;

    case IOCTL_KF_GET_PEB_ADDRESS:
        status = KfGetPebAddress(Irp, ioStack);
        break;

    case IOCTL_KF_CLEAR_DEBUG_PORT:
        status = KfClearDebugPort(Irp, ioStack);
        break;

    case IOCTL_KF_CLEAR_THREAD_HIDE:
        status = KfClearThreadHide(Irp, ioStack);
        break;

    case IOCTL_KF_INSTALL_HOOK:
    {
        /* Optional: pass target PID in input buffer */
        ULONG targetPid = 0;
        if (ioStack->Parameters.DeviceIoControl.InputBufferLength >= sizeof(ULONG)) {
            targetPid = *(PULONG)Irp->AssociatedIrp.SystemBuffer;
        }
        KfSetTargetPid(targetPid);
        status = KfInstallDebugHook();
        Irp->IoStatus.Information = 0;
        break;
    }

    case IOCTL_KF_REMOVE_HOOK:
        KfRemoveDebugHook();
        status = STATUS_SUCCESS;
        Irp->IoStatus.Information = 0;
        break;

    case IOCTL_KF_RESET:
        DbgPrint("[KernelFlirt] RESET: removing all breakpoints and hook\n");
        KfRemoveAllBreakpoints();
        KfDebugHookCleanup();   /* removes hook AND cancels pending WAIT IRP */
        KfSetTargetPid(0);
        status = STATUS_SUCCESS;
        Irp->IoStatus.Information = 0;
        break;

    case IOCTL_KF_WAIT_DEBUG_EVENT:
        /* This handler manages its own IRP completion (may pend) */
        return KfWaitDebugEvent(Irp, ioStack);

    case IOCTL_KF_CONTINUE_DEBUG_EVENT:
        /* This handler manages its own IRP completion */
        return KfContinueDebugEvent(Irp, ioStack);

    case IOCTL_KF_GET_HOOK_STATS:
        return KfGetHookStats(Irp, ioStack);

    default:
        DbgPrint("[KernelFlirt] Unknown IOCTL: 0x%08X\n", ioctl);
        status = STATUS_INVALID_DEVICE_REQUEST;
        Irp->IoStatus.Information = 0;
        break;
    }

    Irp->IoStatus.Status = status;
    IoCompleteRequest(Irp, IO_NO_INCREMENT);

    return status;
}
