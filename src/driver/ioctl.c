/*
 * KernelFlirt - IOCTL dispatcher
 * ioctl.c - Routes IOCTLs to handler functions
 */

#include <ntddk.h>
#include "../../include/kf_shared.h"
#include "ntundoc.h"
#include "debughook.h"
#include "ntqsi_hook.h"

/* ntifs.h declarations needed for alloc/free in target process */
NTSYSAPI NTSTATUS NTAPI ZwAllocateVirtualMemory(
    HANDLE ProcessHandle, PVOID *BaseAddress, ULONG_PTR ZeroBits,
    PSIZE_T RegionSize, ULONG AllocationType, ULONG Protect);
NTSYSAPI NTSTATUS NTAPI ZwFreeVirtualMemory(
    HANDLE ProcessHandle, PVOID *BaseAddress, PSIZE_T RegionSize, ULONG FreeType);

/* Forward declarations for handlers (implemented in other files) */
extern NTSTATUS KfReadMemory(PIRP Irp, PIO_STACK_LOCATION IoStack);
extern NTSTATUS KfWriteMemory(PIRP Irp, PIO_STACK_LOCATION IoStack);
extern NTSTATUS KfSetBreakpoint(PIRP Irp, PIO_STACK_LOCATION IoStack);
extern NTSTATUS KfRemoveBreakpoint(PIRP Irp, PIO_STACK_LOCATION IoStack);
extern NTSTATUS KfSingleStep(PIRP Irp, PIO_STACK_LOCATION IoStack);
extern NTSTATUS KfProtectMemory(PIRP Irp, PIO_STACK_LOCATION IoStack);
extern NTSTATUS KfReadRegisters(PIRP Irp, PIO_STACK_LOCATION IoStack);
extern NTSTATUS KfWriteRegisters(PIRP Irp, PIO_STACK_LOCATION IoStack);
extern NTSTATUS KfWriteRip(PIRP Irp, PIO_STACK_LOCATION IoStack);
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

    case IOCTL_KF_PROTECT_MEMORY:
        status = KfProtectMemory(Irp, ioStack);
        break;

    case IOCTL_KF_READ_REGISTERS:
        status = KfReadRegisters(Irp, ioStack);
        break;

    case IOCTL_KF_WRITE_REGISTERS:
        status = KfWriteRegisters(Irp, ioStack);
        break;

    case IOCTL_KF_WRITE_RIP:
        status = KfWriteRip(Irp, ioStack);
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

    case IOCTL_KF_INSTALL_NTQSI_HOOK:
        status = KfInstallNtQsiHook();
        Irp->IoStatus.Information = 0;
        break;

    case IOCTL_KF_REMOVE_NTQSI_HOOK:
        KfRemoveNtQsiHook();
        status = STATUS_SUCCESS;
        Irp->IoStatus.Information = 0;
        break;

    case IOCTL_KF_PROBE_NTQSI:
        status = KfProbeNtQsi(Irp, ioStack);
        break;

    case IOCTL_KF_SPOOF_SHARED_DATA:
    {
        PUCHAR pInput;
        extern void KfSetSpoofSharedUserData(BOOLEAN enable);
        if (ioStack->Parameters.DeviceIoControl.InputBufferLength >= 1) {
            pInput = (PUCHAR)Irp->AssociatedIrp.SystemBuffer;
            KfSetSpoofSharedUserData(pInput[0] ? TRUE : FALSE);
            status = STATUS_SUCCESS;
        } else {
            status = STATUS_BUFFER_TOO_SMALL;
        }
        Irp->IoStatus.Information = 0;
        break;
    }

    case IOCTL_KF_ALLOC_MEMORY:
    {
        PUCHAR pIn;
        ULONG  pid2;
        ULONG64 reqSize;
        ULONG  prot;
        PEPROCESS proc2 = NULL;
        KAPC_STATE apc2;
        PVOID base2 = NULL;
        SIZE_T sz2;

        if (ioStack->Parameters.DeviceIoControl.InputBufferLength < 16 ||
            ioStack->Parameters.DeviceIoControl.OutputBufferLength < 8) {
            status = STATUS_BUFFER_TOO_SMALL;
            Irp->IoStatus.Information = 0;
            break;
        }

        pIn     = (PUCHAR)Irp->AssociatedIrp.SystemBuffer;
        pid2    = *(PULONG)(pIn);
        reqSize = *(PULONG64)(pIn + 4);
        prot    = *(PULONG)(pIn + 12);

        status = PsLookupProcessByProcessId((HANDLE)(ULONG_PTR)pid2, &proc2);
        if (NT_SUCCESS(status)) {
            sz2 = (SIZE_T)reqSize;
            KeStackAttachProcess(proc2, &apc2);
            status = ZwAllocateVirtualMemory(
                ZwCurrentProcess(), &base2, 0, &sz2,
                MEM_COMMIT | MEM_RESERVE, prot);
            KeUnstackDetachProcess(&apc2);

            if (NT_SUCCESS(status)) {
                *(PULONG64)Irp->AssociatedIrp.SystemBuffer = (ULONG64)base2;
                Irp->IoStatus.Information = 8;
            } else {
                Irp->IoStatus.Information = 0;
            }
            ObDereferenceObject(proc2);
        } else {
            Irp->IoStatus.Information = 0;
        }
        break;
    }

    case IOCTL_KF_FREE_MEMORY:
    {
        PUCHAR pIn3;
        ULONG  pid3;
        PEPROCESS proc3 = NULL;
        KAPC_STATE apc3;
        PVOID base3;
        SIZE_T sz3 = 0;

        if (ioStack->Parameters.DeviceIoControl.InputBufferLength < 12) {
            status = STATUS_BUFFER_TOO_SMALL;
            Irp->IoStatus.Information = 0;
            break;
        }

        pIn3  = (PUCHAR)Irp->AssociatedIrp.SystemBuffer;
        pid3  = *(PULONG)(pIn3);
        base3 = (PVOID)*(PULONG64)(pIn3 + 4);

        status = PsLookupProcessByProcessId((HANDLE)(ULONG_PTR)pid3, &proc3);
        if (NT_SUCCESS(status)) {
            KeStackAttachProcess(proc3, &apc3);
            ZwFreeVirtualMemory(ZwCurrentProcess(), &base3, &sz3, MEM_RELEASE);
            KeUnstackDetachProcess(&apc3);
            ObDereferenceObject(proc3);
            status = STATUS_SUCCESS;
        }
        Irp->IoStatus.Information = 0;
        break;
    }

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
        KfDebugHookDeactivate();  /* deactivate, don't remove — hook stays safe */
        status = STATUS_SUCCESS;
        Irp->IoStatus.Information = 0;
        break;

    case IOCTL_KF_RESET:
        DbgPrint("[KernelFlirt] RESET: deactivating hook, removing BPs\n");
        KfRemoveNtQsiHook();
        KfRemoveAllBreakpoints();
        KfDebugHookDeactivate();  /* PID=invalid, wake threads, cancel WAIT IRP — hook stays */
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

    case IOCTL_KF_SET_TARGET_PID:
    {
        ULONG pid = 0xFFFFFFFF;  /* default = no target */
        if (ioStack->Parameters.DeviceIoControl.InputBufferLength >= sizeof(ULONG))
            pid = *(PULONG)Irp->AssociatedIrp.SystemBuffer;
        KfSetTargetPid(pid);
        status = STATUS_SUCCESS;
        Irp->IoStatus.Information = 0;
        break;
    }

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
