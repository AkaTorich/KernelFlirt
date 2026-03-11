/*
 * KernelFlirt - Thread operations
 * threads.c - Enumerate, suspend, resume threads
 *
 * Uses explicit byte-offset structs (like process.c) to avoid
 * layout mismatches across Windows builds.
 */

#include <ntddk.h>
#include "ntundoc.h"
#include "../../include/kf_shared.h"

#define SystemProcessInformation 5

/*
 * Minimal process-info header — only fields we read.
 * Offsets verified for x64 Windows 10/11.
 */
typedef struct _SPI_HDR {
    ULONG   NextEntryOffset;    /* 0x00 */
    ULONG   NumberOfThreads;    /* 0x04 */
    UCHAR   _pad1[72];          /* 0x08 -> 0x50  (skip to UniqueProcessId) */
    HANDLE  UniqueProcessId;    /* 0x50 */
} SPI_HDR;

/* Thread array starts at offset 0x100 from the process entry on x64 */
#define SPI_THREADS_OFFSET  0x100

/*
 * Minimal per-thread entry — 0x50 bytes each on x64.
 *   0x00  KernelTime      (8)
 *   0x08  UserTime        (8)
 *   0x10  CreateTime      (8)
 *   0x18  WaitTime        (4) + pad(4)
 *   0x20  StartAddress    (8)
 *   0x28  ClientId        (16)  — UniqueProcess(8) + UniqueThread(8)
 *   0x38  Priority        (4)
 *   0x3C  BasePriority    (4)
 *   0x40  ContextSwitches (4)
 *   0x44  ThreadState     (4)
 *   0x48  WaitReason      (4) + pad(4)
 */
typedef struct _STI_MINIMAL {
    UCHAR   _pad0[0x20];        /* skip to StartAddress */
    PVOID   StartAddress;       /* 0x20 */
    HANDLE  UniqueProcess;      /* 0x28 */
    HANDLE  UniqueThread;       /* 0x30 */
    LONG    Priority;           /* 0x38 */
    LONG    BasePriority;       /* 0x3C */
    ULONG   ContextSwitches;    /* 0x40 */
    ULONG   ThreadState;        /* 0x44 */
    ULONG   WaitReason;         /* 0x48 */
    UCHAR   _pad1[4];           /* 0x4C -> 0x50 */
} STI_MINIMAL;

/* sizeof(STI_MINIMAL) must be 0x50 */
C_ASSERT(sizeof(STI_MINIMAL) == 0x50);

NTSTATUS
KfEnumThreads(
    _In_ PIRP               Irp,
    _In_ PIO_STACK_LOCATION  IoStack
)
{
    PKF_ENUM_THREADS_IN input;
    PKF_THREAD_ENTRY    outputEntries;
    NTSTATUS            status;
    ULONG               maxEntries;
    ULONG               count = 0;
    PVOID               buffer = NULL;
    ULONG               bufferSize = 0x40000;  /* 256KB initial */
    ULONG               returnLength = 0;
    ULONG               targetPid;

    if (IoStack->Parameters.DeviceIoControl.InputBufferLength < sizeof(KF_ENUM_THREADS_IN)) {
        Irp->IoStatus.Information = 0;
        return STATUS_BUFFER_TOO_SMALL;
    }

    input = (PKF_ENUM_THREADS_IN)Irp->AssociatedIrp.SystemBuffer;
    outputEntries = (PKF_THREAD_ENTRY)Irp->AssociatedIrp.SystemBuffer;
    maxEntries = IoStack->Parameters.DeviceIoControl.OutputBufferLength / sizeof(KF_THREAD_ENTRY);

    if (maxEntries == 0) {
        Irp->IoStatus.Information = 0;
        return STATUS_BUFFER_TOO_SMALL;
    }

    /* Save PID before output overwrites the SystemBuffer */
    targetPid = input->ProcessId;

    /* Query system process information — same pattern as kmodules.c */
    buffer = ExAllocatePoolWithTag(NonPagedPool, bufferSize, 'fTkK');
    if (!buffer) {
        Irp->IoStatus.Information = 0;
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    status = ZwQuerySystemInformation(SystemProcessInformation, buffer, bufferSize, &returnLength);
    if (status == STATUS_INFO_LENGTH_MISMATCH) {
        ExFreePoolWithTag(buffer, 'fTkK');
        bufferSize = returnLength + 0x10000;
        buffer = ExAllocatePoolWithTag(NonPagedPool, bufferSize, 'fTkK');
        if (!buffer) {
            Irp->IoStatus.Information = 0;
            return STATUS_INSUFFICIENT_RESOURCES;
        }
        status = ZwQuerySystemInformation(SystemProcessInformation, buffer, bufferSize, &returnLength);
    }

    if (!NT_SUCCESS(status)) {
        ExFreePoolWithTag(buffer, 'fTkK');
        Irp->IoStatus.Information = 0;
        return status;
    }

    /* Find the target process and copy its thread entries */
    __try {
        SPI_HDR *proc = (SPI_HDR *)buffer;

        for (;;) {
            if ((ULONG)(ULONG_PTR)proc->UniqueProcessId == targetPid) {
                /* Found it — thread array is at proc + 0x100 */
                STI_MINIMAL *threads = (STI_MINIMAL *)((UCHAR *)proc + SPI_THREADS_OFFSET);
                ULONG i;

                for (i = 0; i < proc->NumberOfThreads && count < maxEntries; i++) {
                    outputEntries[count].ThreadId     = (ULONG)(ULONG_PTR)threads[i].UniqueThread;
                    outputEntries[count].StartAddress  = (ULONG64)threads[i].StartAddress;
                    outputEntries[count].State         = threads[i].ThreadState;
                    outputEntries[count].Priority      = (ULONG)threads[i].Priority;
                    count++;
                }
                break;
            }

            if (proc->NextEntryOffset == 0)
                break;

            proc = (SPI_HDR *)((UCHAR *)proc + proc->NextEntryOffset);
        }
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        DbgPrint("[KernelFlirt] Exception 0x%08X in KfEnumThreads\n", GetExceptionCode());
    }

    ExFreePoolWithTag(buffer, 'fTkK');

    Irp->IoStatus.Information = count * sizeof(KF_THREAD_ENTRY);
    return STATUS_SUCCESS;
}

/* ================================================================== */
/*  Suspend / Resume — uses PsSuspendProcess (reliable, exported)      */
/* ================================================================== */

static PFN_PsSuspendProcess g_pfnSuspend = NULL;
static PFN_PsResumeProcess  g_pfnResume  = NULL;
static BOOLEAN              g_SuspendResolved = FALSE;

static void KfResolveSuspendApis(void)
{
    UNICODE_STRING name;
    if (g_SuspendResolved) return;

    RtlInitUnicodeString(&name, L"PsSuspendProcess");
    g_pfnSuspend = (PFN_PsSuspendProcess)MmGetSystemRoutineAddress(&name);

    RtlInitUnicodeString(&name, L"PsResumeProcess");
    g_pfnResume = (PFN_PsResumeProcess)MmGetSystemRoutineAddress(&name);

    g_SuspendResolved = TRUE;
    DbgPrint("[KernelFlirt] PsSuspendProcess=%p PsResumeProcess=%p\n",
             g_pfnSuspend, g_pfnResume);
}

NTSTATUS
KfSuspendThread(
    _In_ PIRP               Irp,
    _In_ PIO_STACK_LOCATION  IoStack
)
{
    PKF_THREAD_OP_IN    input;
    PETHREAD            thread = NULL;
    PEPROCESS           process = NULL;
    NTSTATUS            status;

    if (IoStack->Parameters.DeviceIoControl.InputBufferLength < sizeof(KF_THREAD_OP_IN)) {
        Irp->IoStatus.Information = 0;
        return STATUS_BUFFER_TOO_SMALL;
    }

    input = (PKF_THREAD_OP_IN)Irp->AssociatedIrp.SystemBuffer;

    KfResolveSuspendApis();
    if (!g_pfnSuspend) {
        Irp->IoStatus.Information = 0;
        return STATUS_NOT_IMPLEMENTED;
    }

    /* Look up thread → get its process → suspend entire process */
    status = PsLookupThreadByThreadId((HANDLE)(ULONG_PTR)input->ThreadId, &thread);
    if (!NT_SUCCESS(status)) {
        DbgPrint("[KernelFlirt] PsLookupThreadByThreadId(%u) failed: 0x%08X\n",
                 input->ThreadId, status);
        Irp->IoStatus.Information = 0;
        return status;
    }

    process = IoThreadToProcess(thread);
    status = g_pfnSuspend(process);

    DbgPrint("[KernelFlirt] PsSuspendProcess for TID %u: 0x%08X\n",
             input->ThreadId, status);

    ObDereferenceObject(thread);
    Irp->IoStatus.Information = 0;
    return status;
}

NTSTATUS
KfResumeThread(
    _In_ PIRP               Irp,
    _In_ PIO_STACK_LOCATION  IoStack
)
{
    PKF_THREAD_OP_IN    input;
    PETHREAD            thread = NULL;
    PEPROCESS           process = NULL;
    NTSTATUS            status;

    if (IoStack->Parameters.DeviceIoControl.InputBufferLength < sizeof(KF_THREAD_OP_IN)) {
        Irp->IoStatus.Information = 0;
        return STATUS_BUFFER_TOO_SMALL;
    }

    input = (PKF_THREAD_OP_IN)Irp->AssociatedIrp.SystemBuffer;

    KfResolveSuspendApis();
    if (!g_pfnResume) {
        Irp->IoStatus.Information = 0;
        return STATUS_NOT_IMPLEMENTED;
    }

    status = PsLookupThreadByThreadId((HANDLE)(ULONG_PTR)input->ThreadId, &thread);
    if (!NT_SUCCESS(status)) {
        Irp->IoStatus.Information = 0;
        return status;
    }

    process = IoThreadToProcess(thread);
    status = g_pfnResume(process);

    ObDereferenceObject(thread);
    Irp->IoStatus.Information = 0;
    return status;
}
