/*
 * KernelFlirt - NtQuerySystemInformation hook
 * ntqsi_hook.h - Spoofs SystemKernelDebuggerInformation (class 0x23)
 */

#ifndef KF_NTQSI_HOOK_H
#define KF_NTQSI_HOOK_H

#include <ntddk.h>

/* Install inline hook on NtQuerySystemInformation */
NTSTATUS KfInstallNtQsiHook(void);

/* Remove the hook and restore original bytes (trampoline kept alive) */
void KfRemoveNtQsiHook(void);

/* Full cleanup — call from DriverUnload only */
void KfNtQsiCleanup(void);

/* Check if hook is active */
BOOLEAN KfIsNtQsiHookActive(void);

/* Probe: find address, dump bytes, decode — no hooking (safe) */
NTSTATUS KfProbeNtQsi(PIRP Irp, PIO_STACK_LOCATION IoStack);

#endif /* KF_NTQSI_HOOK_H */
