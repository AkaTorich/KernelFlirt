/*
 * KernelFlirt - Undocumented NT API declarations
 * ntundoc.h - Forward declarations for exported but undocumented kernel APIs
 */

#ifndef KF_NTUNDOC_H
#define KF_NTUNDOC_H

#include <ntddk.h>

/* KAPC_STATE - defined in ntifs.h but we don't want to pull all of ntifs */
typedef struct _KAPC_STATE {
    LIST_ENTRY  ApcListHead[2];
    PEPROCESS   Process;
    union {
        UCHAR InProgressFlags;
        struct {
            BOOLEAN KernelApcInProgress;
            BOOLEAN SpecialApcInProgress;
        };
    };
    BOOLEAN KernelApcPending;
    union {
        BOOLEAN UserApcPendingAll;
        struct {
            BOOLEAN SpecialUserApcPending;
            BOOLEAN UserApcPending;
        };
    };
} KAPC_STATE, *PKAPC_STATE, *PRKAPC_STATE;

/* Process/Thread lookup */
NTKERNELAPI NTSTATUS PsLookupProcessByProcessId(
    _In_ HANDLE ProcessId,
    _Out_ PEPROCESS *Process
);

NTKERNELAPI NTSTATUS PsLookupThreadByThreadId(
    _In_ HANDLE ThreadId,
    _Out_ PETHREAD *Thread
);

/* APC attach/detach */
NTKERNELAPI VOID KeStackAttachProcess(
    _In_ PEPROCESS Process,
    _Out_ PRKAPC_STATE ApcState
);

NTKERNELAPI VOID KeUnstackDetachProcess(
    _In_ PRKAPC_STATE ApcState
);

/* PEB access */
NTKERNELAPI PVOID PsGetProcessPeb(
    _In_ PEPROCESS Process
);

/* WoW64 PEB access (returns PEB32 pointer, NULL if not WoW64) */
NTKERNELAPI PVOID PsGetProcessWow64Process(
    _In_ PEPROCESS Process
);

/* Memory copy between processes */
NTKERNELAPI NTSTATUS NTAPI MmCopyVirtualMemory(
    _In_ PEPROCESS SourceProcess,
    _In_ PVOID SourceAddress,
    _In_ PEPROCESS TargetProcess,
    _Out_ PVOID TargetAddress,
    _In_ SIZE_T BufferSize,
    _In_ KPROCESSOR_MODE PreviousMode,
    _Out_ PSIZE_T ReturnSize
);

/* Thread context */
NTSYSAPI NTSTATUS NTAPI PsGetContextThread(
    _In_ PETHREAD Thread,
    _Inout_ PCONTEXT ThreadContext,
    _In_ KPROCESSOR_MODE Mode
);

NTSYSAPI NTSTATUS NTAPI PsSetContextThread(
    _In_ PETHREAD Thread,
    _In_ PCONTEXT ThreadContext,
    _In_ KPROCESSOR_MODE Mode
);

/* ZwSuspendThread / ZwResumeThread — resolved dynamically in threads.c
   because they are not exported by ntoskrnl on all Windows 10 builds */

/* Open thread by ID */
NTSYSAPI NTSTATUS NTAPI ZwOpenThread(
    _Out_ PHANDLE ThreadHandle,
    _In_ ACCESS_MASK DesiredAccess,
    _In_ POBJECT_ATTRIBUTES ObjectAttributes,
    _In_ PCLIENT_ID ClientId
);

/* System information query */
NTSYSAPI NTSTATUS NTAPI ZwQuerySystemInformation(
    _In_ ULONG SystemInformationClass,
    _Out_ PVOID SystemInformation,
    _In_ ULONG SystemInformationLength,
    _Out_opt_ PULONG ReturnLength
);

/* Thread to process */
NTKERNELAPI PEPROCESS IoThreadToProcess(
    _In_ PETHREAD Thread
);

/* Process suspend/resume — resolved dynamically in threads.c
   because they may not be in WDK .lib */
typedef NTSTATUS (NTAPI *PFN_PsSuspendProcess)(PEPROCESS Process);
typedef NTSTATUS (NTAPI *PFN_PsResumeProcess)(PEPROCESS Process);


/* Virtual memory protection (for memory breakpoints via PAGE_GUARD) */
NTSYSAPI NTSTATUS NTAPI ZwProtectVirtualMemory(
    _In_ HANDLE ProcessHandle,
    _Inout_ PVOID *BaseAddress,
    _Inout_ PSIZE_T RegionSize,
    _In_ ULONG NewProtect,
    _Out_ PULONG OldProtect
);

/* Open process by ID */
NTSYSAPI NTSTATUS NTAPI ZwOpenProcess(
    _Out_ PHANDLE ProcessHandle,
    _In_ ACCESS_MASK DesiredAccess,
    _In_ POBJECT_ATTRIBUTES ObjectAttributes,
    _In_opt_ PCLIENT_ID ClientId
);

#endif /* KF_NTUNDOC_H */
