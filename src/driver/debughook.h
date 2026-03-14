/*
 * KernelFlirt - Debug Hook (KdTrap-based)
 * debughook.h - Declarations for kernel debug trap interception
 */

#ifndef KF_DEBUGHOOK_H
#define KF_DEBUGHOOK_H

#include <ntddk.h>

/* Initialize debug hook subsystem */
NTSTATUS KfDebugHookInit(void);

/* Install the KdTrap hook */
NTSTATUS KfInstallDebugHook(void);

/* Remove the hook and restore original handler */
void KfRemoveDebugHook(void);

/* Cleanup (call from DriverUnload) */
void KfDebugHookCleanup(void);

/* IOCTL handlers */
NTSTATUS KfWaitDebugEvent(PIRP Irp, PIO_STACK_LOCATION IoStack);
NTSTATUS KfContinueDebugEvent(PIRP Irp, PIO_STACK_LOCATION IoStack);
NTSTATUS KfGetHookStats(PIRP Irp, PIO_STACK_LOCATION IoStack);

/* Set target PID filter (0 = catch all processes) */
void KfSetTargetPid(ULONG pid);

/* Check if hook is active */
BOOLEAN KfIsDebugHookActive(void);

/* Re-assert KdDebuggerEnabled=TRUE (call before expecting debug events) */
void KfReassertDebugFlags(void);

#endif /* KF_DEBUGHOOK_H */
