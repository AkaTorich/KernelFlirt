/*
 * KernelFlirt - Breakpoint management
 * breakpoint.c - Software (INT3), Hardware (DR0-3), and Memory (PAGE_GUARD) breakpoints
 *
 * Hardware breakpoint DR7 layout (per slot, 4 bits):
 *   Bits 16-17: Condition (00=execute, 01=write, 10=I/O, 11=read/write)
 *   Bits 18-19: Length    (00=1, 01=2, 10=8, 11=4)
 *   Repeat for DR1 at 20-23, DR2 at 24-27, DR3 at 28-31
 *   Bits 0-7: L0,G0,L1,G1,L2,G2,L3,G3 (local/global enable)
 */

#include <ntddk.h>
#include "ntundoc.h"
#include "../../include/kf_shared.h"

/* KTRAP_FRAME offsets for debug registers */
#define KTHREAD_TRAPFRAME_OFFSET  0x90
#define TF_DR0      0xD8
#define TF_DR1      0xE0
#define TF_DR2      0xE8
#define TF_DR3      0xF0
#define TF_DR6      0xF8
#define TF_DR7      0x100

#define TF_READ64(base, off)  (*(ULONG64 *)((UCHAR *)(base) + (off)))
#define TF_WRITE64(base, off, val) (*(ULONG64 *)((UCHAR *)(base) + (off)) = (val))

/* Helper: get TrapFrame pointer from ETHREAD */
static PVOID KfGetTrapFrame(PETHREAD thread)
{
    PVOID pKThread = (PVOID)thread;
    return *(PVOID *)((UCHAR *)pKThread + KTHREAD_TRAPFRAME_OFFSET);
}

/* Breakpoint tracking */
#define KF_MAX_BREAKPOINTS  256

typedef struct _KF_BP_ENTRY {
    BOOLEAN     Active;
    ULONG       Handle;
    ULONG       ProcessId;
    ULONG       ThreadId;       /* 0 for process-wide SW bp */
    ULONG64     Address;
    ULONG       Type;           /* KF_BP_* */
    ULONG       HwSlot;         /* 0-3 for HW breakpoints */
    UCHAR       OrigByte;       /* Original byte for SW breakpoints */
    ULONG       OrigProtect;    /* Original page protection for memory BP */
    ULONG64     PageBase;       /* Page-aligned base for memory BP */
    SIZE_T      PageSize;       /* Region size for memory BP */
} KF_BP_ENTRY;

static KF_BP_ENTRY g_Breakpoints[KF_MAX_BREAKPOINTS];
static ULONG       g_NextHandle = 1;
static KSPIN_LOCK  g_BpLock;
static BOOLEAN     g_BpInitialized = FALSE;

static void KfBpInit(void)
{
    if (!g_BpInitialized) {
        KeInitializeSpinLock(&g_BpLock);
        RtlZeroMemory(g_Breakpoints, sizeof(g_Breakpoints));
        g_BpInitialized = TRUE;
    }
}

/* Find a free slot in the BP array */
static ULONG KfFindFreeSlot(void)
{
    ULONG i;
    for (i = 0; i < KF_MAX_BREAKPOINTS; i++) {
        if (!g_Breakpoints[i].Active)
            return i;
    }
    return (ULONG)-1;
}

/* ================================================================== */
/* Safe memory write via MDL (bypasses page protection)                */
/* ================================================================== */

static NTSTATUS KfWriteProcessMemory(PEPROCESS process, ULONG64 address, PVOID buffer, SIZE_T size)
{
    KAPC_STATE  apcState;
    PMDL        mdl = NULL;
    PVOID       mapped = NULL;
    NTSTATUS    status = STATUS_SUCCESS;
    PVOID       targetAddr = (PVOID)address;
    ULONG       oldProtect = 0;
    SIZE_T      regionSize = size;

    KeStackAttachProcess(process, &apcState);

    __try {
        /*
         * Step 1: Change protection to RW to trigger copy-on-write.
         * This ensures we write to a PRIVATE copy of the page,
         * not the shared physical page used by other processes.
         */
        status = ZwProtectVirtualMemory(
            ZwCurrentProcess(), &targetAddr, &regionSize,
            PAGE_EXECUTE_READWRITE, &oldProtect);

        if (!NT_SUCCESS(status)) {
            /*
             * ZwProtectVirtualMemory failed (e.g. VAD not found for this range).
             * Fall back to MDL-based write (will affect shared page).
             */
            mdl = IoAllocateMdl((PVOID)address, (ULONG)size, FALSE, FALSE, NULL);
            if (mdl == NULL) {
                status = STATUS_INSUFFICIENT_RESOURCES;
                __leave;
            }

            MmProbeAndLockPages(mdl, UserMode, IoReadAccess);

            mapped = MmMapLockedPagesSpecifyCache(
                mdl, KernelMode, MmNonCached, NULL, FALSE, NormalPagePriority);
            if (mapped == NULL) {
                status = STATUS_INSUFFICIENT_RESOURCES;
                __leave;
            }

            status = MmProtectMdlSystemAddress(mdl, PAGE_READWRITE);
            if (NT_SUCCESS(status)) {
                RtlCopyMemory(mapped, buffer, size);
            }
            __leave;
        }

        /*
         * Step 2: Direct write. We are KeStackAttach'd to the target process
         * and the page is now RWX (CoW created a private copy). Write directly.
         */
        ProbeForWrite((PVOID)(ULONG_PTR)address, size, 1);
        RtlCopyMemory((PVOID)(ULONG_PTR)address, buffer, size);
        status = STATUS_SUCCESS;

        /* Step 3: Restore original protection */
        targetAddr = (PVOID)address;
        regionSize = size;
        ZwProtectVirtualMemory(
            ZwCurrentProcess(), &targetAddr, &regionSize,
            oldProtect, &oldProtect);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        status = GetExceptionCode();
    }

    if (mapped) MmUnmapLockedPages(mapped, mdl);
    if (mdl) {
        __try { MmUnlockPages(mdl); } __except(EXCEPTION_EXECUTE_HANDLER) {}
        IoFreeMdl(mdl);
    }

    KeUnstackDetachProcess(&apcState);
    return status;
}

static NTSTATUS KfReadProcessByte(PEPROCESS process, ULONG64 address, PUCHAR outByte)
{
    SIZE_T bytes = 0;
    return MmCopyVirtualMemory(
        process, (PVOID)address,
        PsGetCurrentProcess(), outByte,
        1, KernelMode, &bytes);
}

/* ================================================================== */
/* Software Breakpoint (INT3)                                          */
/* ================================================================== */

static NTSTATUS KfSetSwBreakpoint(PKF_SET_BP_IN input, PULONG outHandle)
{
    PEPROCESS   process = NULL;
    NTSTATUS    status;
    KIRQL       oldIrql;
    ULONG       slot;
    UCHAR       int3 = 0xCC;
    UCHAR       origByte = 0;

    status = PsLookupProcessByProcessId((HANDLE)(ULONG_PTR)input->ProcessId, &process);
    if (!NT_SUCCESS(status))
        return status;

    /* Read original byte */
    __try {
        status = KfReadProcessByte(process, input->Address, &origByte);
    } __except(EXCEPTION_EXECUTE_HANDLER) {
        status = GetExceptionCode();
    }

    if (!NT_SUCCESS(status)) {
        ObDereferenceObject(process);
        return status;
    }

    /* Write INT3 via MDL (bypasses PAGE_EXECUTE_READ) */
    status = KfWriteProcessMemory(process, input->Address, &int3, 1);

    ObDereferenceObject(process);

    if (!NT_SUCCESS(status))
        return status;

    /* Record breakpoint */
    KfBpInit();
    KeAcquireSpinLock(&g_BpLock, &oldIrql);

    slot = KfFindFreeSlot();
    if (slot == (ULONG)-1) {
        KeReleaseSpinLock(&g_BpLock, oldIrql);
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    g_Breakpoints[slot].Active    = TRUE;
    g_Breakpoints[slot].Handle    = g_NextHandle++;
    g_Breakpoints[slot].ProcessId = input->ProcessId;
    g_Breakpoints[slot].ThreadId  = input->ThreadId;
    g_Breakpoints[slot].Address   = input->Address;
    g_Breakpoints[slot].Type      = KF_BP_SOFTWARE;
    g_Breakpoints[slot].OrigByte  = origByte;

    *outHandle = g_Breakpoints[slot].Handle;

    KeReleaseSpinLock(&g_BpLock, oldIrql);
    return STATUS_SUCCESS;
}

/* ================================================================== */
/* Hardware Breakpoint (DR0-3)                                         */
/* Supports: Execute, Write, Read/Write conditions                     */
/* ================================================================== */

/*
 * DR7 condition encoding:
 *   00 = execute    (KF_BP_HARDWARE)
 *   01 = write      (KF_BP_HW_WRITE)
 *   11 = read/write (KF_BP_HW_READWRITE)
 *
 * DR7 length encoding:
 *   00 = 1 byte
 *   01 = 2 bytes
 *   10 = 8 bytes (x64 only)
 *   11 = 4 bytes
 */
static ULONG64 KfEncodeDr7Condition(ULONG bpType)
{
    switch (bpType) {
        case KF_BP_HARDWARE:     return 0;  /* 00 = execute */
        case KF_BP_HW_WRITE:    return 1;  /* 01 = write only */
        case KF_BP_HW_READWRITE: return 3;  /* 11 = read/write */
        default:                 return 0;
    }
}

static ULONG64 KfEncodeDr7Length(ULONG length)
{
    switch (length) {
        case 1:  return 0;  /* 00 */
        case 2:  return 1;  /* 01 */
        case 8:  return 2;  /* 10 */
        case 4:  return 3;  /* 11 */
        default: return 0;  /* default 1 byte */
    }
}

static NTSTATUS KfSetHwBreakpoint(PKF_SET_BP_IN input, PULONG outHandle)
{
    PETHREAD    thread = NULL;
    NTSTATUS    status;
    KIRQL       oldIrql;
    ULONG       slot;
    ULONG       hwSlot = (ULONG)-1;
    ULONG       i;
    ULONG64     condition;
    ULONG64     lenBits;
    PVOID       pTrapFrame;
    ULONG64     dr7;

    status = PsLookupThreadByThreadId((HANDLE)(ULONG_PTR)input->ThreadId, &thread);
    if (!NT_SUCCESS(status))
        return status;

    __try {
        pTrapFrame = KfGetTrapFrame(thread);
        if (pTrapFrame == NULL) {
            ObDereferenceObject(thread);
            return STATUS_UNSUCCESSFUL;
        }

        /* Read current DR7 */
        dr7 = TF_READ64(pTrapFrame, TF_DR7);

        /* Find a free DR slot */
        for (i = 0; i < 4; i++) {
            if (!(dr7 & (1ULL << (i * 2)))) {
                hwSlot = i;
                break;
            }
        }

        if (hwSlot == (ULONG)-1) {
            ObDereferenceObject(thread);
            return STATUS_INSUFFICIENT_RESOURCES;
        }

        /* Set the DR address in KTRAP_FRAME */
        switch (hwSlot) {
            case 0: TF_WRITE64(pTrapFrame, TF_DR0, input->Address); break;
            case 1: TF_WRITE64(pTrapFrame, TF_DR1, input->Address); break;
            case 2: TF_WRITE64(pTrapFrame, TF_DR2, input->Address); break;
            case 3: TF_WRITE64(pTrapFrame, TF_DR3, input->Address); break;
        }

        /* Enable local breakpoint (L0-L3 bit) */
        dr7 |= (1ULL << (hwSlot * 2));

        /* Set condition and length in DR7 */
        condition = KfEncodeDr7Condition(input->Type);
        lenBits   = KfEncodeDr7Length(input->Length);

        {
            ULONG condOffset = 16 + hwSlot * 4;
            dr7 &= ~(0xFULL << condOffset);
            dr7 |= (condition << condOffset);
            dr7 |= (lenBits << (condOffset + 2));
        }

        TF_WRITE64(pTrapFrame, TF_DR7, dr7);

        DbgPrint("[KernelFlirt] HW BP slot=%u addr=%p type=%u len=%u DR7=0x%llX\n",
                 hwSlot, (PVOID)input->Address, input->Type, input->Length, dr7);

        status = STATUS_SUCCESS;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        status = GetExceptionCode();
    }

    ObDereferenceObject(thread);

    if (!NT_SUCCESS(status))
        return status;

    /* Record breakpoint */
    KfBpInit();
    KeAcquireSpinLock(&g_BpLock, &oldIrql);

    slot = KfFindFreeSlot();
    if (slot == (ULONG)-1) {
        KeReleaseSpinLock(&g_BpLock, oldIrql);
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    g_Breakpoints[slot].Active    = TRUE;
    g_Breakpoints[slot].Handle    = g_NextHandle++;
    g_Breakpoints[slot].ProcessId = input->ProcessId;
    g_Breakpoints[slot].ThreadId  = input->ThreadId;
    g_Breakpoints[slot].Address   = input->Address;
    g_Breakpoints[slot].Type      = input->Type;
    g_Breakpoints[slot].HwSlot    = hwSlot;

    *outHandle = g_Breakpoints[slot].Handle;

    KeReleaseSpinLock(&g_BpLock, oldIrql);
    return STATUS_SUCCESS;
}

/* ================================================================== */
/* Memory Breakpoint (PAGE_GUARD)                                      */
/* ================================================================== */

/*
 * Memory breakpoints work by setting PAGE_GUARD on the target page.
 * When the page is accessed, STATUS_GUARD_PAGE_VIOLATION is raised.
 * Our KiDebugRoutine hook catches this and reports the event.
 *
 * Flow:
 *   1. Open target process handle
 *   2. ZwProtectVirtualMemory to add PAGE_GUARD
 *   3. On guard violation -> report event
 *   4. On remove -> restore original protection
 */

static NTSTATUS KfOpenProcessHandle(ULONG pid, PHANDLE procHandle)
{
    OBJECT_ATTRIBUTES oa;
    CLIENT_ID cid;

    InitializeObjectAttributes(&oa, NULL, 0, NULL, NULL);
    cid.UniqueProcess = (HANDLE)(ULONG_PTR)pid;
    cid.UniqueThread  = NULL;

    return ZwOpenProcess(procHandle, PROCESS_ALL_ACCESS, &oa, &cid);
}

static NTSTATUS KfSetMemoryBreakpoint(PKF_SET_BP_IN input, PULONG outHandle)
{
    NTSTATUS    status;
    HANDLE      procHandle = NULL;
    PVOID       baseAddr;
    SIZE_T      regionSize;
    ULONG       oldProtect = 0;
    ULONG       newProtect;
    KIRQL       oldIrql;
    ULONG       slot;

    /* Page-align the address */
    baseAddr   = (PVOID)(input->Address & ~0xFFFULL);
    regionSize = 0x1000;  /* One page */

    /* Open target process */
    status = KfOpenProcessHandle(input->ProcessId, &procHandle);
    if (!NT_SUCCESS(status)) {
        DbgPrint("[KernelFlirt] MemBP: Failed to open PID %u: 0x%08X\n",
                 input->ProcessId, status);
        return status;
    }

    /* Query current protection first by attempting a protect-and-revert */
    /* Set PAGE_GUARD on the page */
    /* We need to know the current protection to add PAGE_GUARD to it */
    /* First try: protect with PAGE_EXECUTE_READWRITE | PAGE_GUARD */
    newProtect = PAGE_EXECUTE_READWRITE | PAGE_GUARD;

    status = ZwProtectVirtualMemory(procHandle, &baseAddr, &regionSize,
                                     newProtect, &oldProtect);
    if (!NT_SUCCESS(status)) {
        /* Fallback: try PAGE_READWRITE | PAGE_GUARD */
        baseAddr   = (PVOID)(input->Address & ~0xFFFULL);
        regionSize = 0x1000;
        newProtect = PAGE_READWRITE | PAGE_GUARD;

        status = ZwProtectVirtualMemory(procHandle, &baseAddr, &regionSize,
                                         newProtect, &oldProtect);
    }

    if (!NT_SUCCESS(status)) {
        DbgPrint("[KernelFlirt] MemBP: ZwProtectVirtualMemory failed: 0x%08X\n", status);
        ZwClose(procHandle);
        return status;
    }

    DbgPrint("[KernelFlirt] MemBP: Set PAGE_GUARD on %p (old=0x%X new=0x%X)\n",
             baseAddr, oldProtect, newProtect);

    ZwClose(procHandle);

    /* Record breakpoint */
    KfBpInit();
    KeAcquireSpinLock(&g_BpLock, &oldIrql);

    slot = KfFindFreeSlot();
    if (slot == (ULONG)-1) {
        KeReleaseSpinLock(&g_BpLock, oldIrql);
        /* Try to restore - best effort */
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    g_Breakpoints[slot].Active      = TRUE;
    g_Breakpoints[slot].Handle      = g_NextHandle++;
    g_Breakpoints[slot].ProcessId   = input->ProcessId;
    g_Breakpoints[slot].ThreadId    = input->ThreadId;
    g_Breakpoints[slot].Address     = input->Address;
    g_Breakpoints[slot].Type        = KF_BP_MEMORY;
    g_Breakpoints[slot].OrigProtect = oldProtect;
    g_Breakpoints[slot].PageBase    = (ULONG64)baseAddr;
    g_Breakpoints[slot].PageSize    = regionSize;

    *outHandle = g_Breakpoints[slot].Handle;

    KeReleaseSpinLock(&g_BpLock, oldIrql);
    return STATUS_SUCCESS;
}

static NTSTATUS KfRemoveMemoryBreakpoint(ULONG idx)
{
    HANDLE  procHandle = NULL;
    NTSTATUS status;
    PVOID    baseAddr;
    SIZE_T   regionSize;
    ULONG    oldProtect;

    status = KfOpenProcessHandle(g_Breakpoints[idx].ProcessId, &procHandle);
    if (!NT_SUCCESS(status))
        return status;

    /* Restore original protection */
    baseAddr   = (PVOID)g_Breakpoints[idx].PageBase;
    regionSize = g_Breakpoints[idx].PageSize;

    status = ZwProtectVirtualMemory(procHandle, &baseAddr, &regionSize,
                                     g_Breakpoints[idx].OrigProtect, &oldProtect);

    DbgPrint("[KernelFlirt] MemBP: Restored protection on %p (0x%X)\n",
             baseAddr, g_Breakpoints[idx].OrigProtect);

    ZwClose(procHandle);
    return status;
}

/* ================================================================== */
/* IOCTL handlers                                                      */
/* ================================================================== */

NTSTATUS
KfSetBreakpoint(
    _In_ PIRP               Irp,
    _In_ PIO_STACK_LOCATION  IoStack
)
{
    PKF_SET_BP_IN   input;
    PKF_SET_BP_OUT  output;
    NTSTATUS        status;
    ULONG           handle = 0;

    if (IoStack->Parameters.DeviceIoControl.InputBufferLength < sizeof(KF_SET_BP_IN)) {
        Irp->IoStatus.Information = 0;
        return STATUS_BUFFER_TOO_SMALL;
    }
    if (IoStack->Parameters.DeviceIoControl.OutputBufferLength < sizeof(KF_SET_BP_OUT)) {
        Irp->IoStatus.Information = 0;
        return STATUS_BUFFER_TOO_SMALL;
    }

    input  = (PKF_SET_BP_IN)Irp->AssociatedIrp.SystemBuffer;
    output = (PKF_SET_BP_OUT)Irp->AssociatedIrp.SystemBuffer;

    switch (input->Type) {
    case KF_BP_SOFTWARE:
        status = KfSetSwBreakpoint(input, &handle);
        break;

    case KF_BP_HARDWARE:
    case KF_BP_HW_WRITE:
    case KF_BP_HW_READWRITE:
        status = KfSetHwBreakpoint(input, &handle);
        break;

    case KF_BP_MEMORY:
        status = KfSetMemoryBreakpoint(input, &handle);
        break;

    default:
        status = STATUS_INVALID_PARAMETER;
        break;
    }

    if (NT_SUCCESS(status)) {
        output->Handle = handle;
        Irp->IoStatus.Information = sizeof(KF_SET_BP_OUT);
    } else {
        Irp->IoStatus.Information = 0;
    }

    return status;
}

NTSTATUS
KfRemoveBreakpoint(
    _In_ PIRP               Irp,
    _In_ PIO_STACK_LOCATION  IoStack
)
{
    PKF_REMOVE_BP_IN input;
    KIRQL            oldIrql;
    NTSTATUS         status = STATUS_NOT_FOUND;
    ULONG            i;

    if (IoStack->Parameters.DeviceIoControl.InputBufferLength < sizeof(KF_REMOVE_BP_IN)) {
        Irp->IoStatus.Information = 0;
        return STATUS_BUFFER_TOO_SMALL;
    }

    input = (PKF_REMOVE_BP_IN)Irp->AssociatedIrp.SystemBuffer;

    KfBpInit();
    KeAcquireSpinLock(&g_BpLock, &oldIrql);

    for (i = 0; i < KF_MAX_BREAKPOINTS; i++) {
        if (g_Breakpoints[i].Active && g_Breakpoints[i].Handle == input->Handle) {

            if (g_Breakpoints[i].Type == KF_BP_SOFTWARE) {
                /* Restore original byte */
                PEPROCESS process = NULL;
                NTSTATUS  lookupStatus;

                KeReleaseSpinLock(&g_BpLock, oldIrql);

                lookupStatus = PsLookupProcessByProcessId(
                    (HANDLE)(ULONG_PTR)g_Breakpoints[i].ProcessId, &process);

                if (NT_SUCCESS(lookupStatus)) {
                    KfWriteProcessMemory(process, g_Breakpoints[i].Address,
                                         &g_Breakpoints[i].OrigByte, 1);
                    ObDereferenceObject(process);
                }

                KeAcquireSpinLock(&g_BpLock, &oldIrql);
            }
            else if (g_Breakpoints[i].Type == KF_BP_HARDWARE ||
                     g_Breakpoints[i].Type == KF_BP_HW_WRITE ||
                     g_Breakpoints[i].Type == KF_BP_HW_READWRITE) {
                /* Clear HW breakpoint in DR7 */
                PETHREAD thread = NULL;
                NTSTATUS lookupStatus;

                ULONG hwSlot  = g_Breakpoints[i].HwSlot;
                ULONG threadId = g_Breakpoints[i].ThreadId;

                KeReleaseSpinLock(&g_BpLock, oldIrql);

                lookupStatus = PsLookupThreadByThreadId(
                    (HANDLE)(ULONG_PTR)threadId, &thread);

                if (NT_SUCCESS(lookupStatus)) {
                    __try {
                        PVOID pTF = KfGetTrapFrame(thread);
                        if (pTF != NULL) {
                            ULONG64 dr7;
                            ULONG condOffset;

                            /* Disable local enable bit */
                            dr7 = TF_READ64(pTF, TF_DR7);
                            dr7 &= ~(1ULL << (hwSlot * 2));
                            /* Clear condition+length bits */
                            condOffset = 16 + hwSlot * 4;
                            dr7 &= ~(0xFULL << condOffset);
                            TF_WRITE64(pTF, TF_DR7, dr7);

                            /* Clear DR address */
                            switch (hwSlot) {
                                case 0: TF_WRITE64(pTF, TF_DR0, 0); break;
                                case 1: TF_WRITE64(pTF, TF_DR1, 0); break;
                                case 2: TF_WRITE64(pTF, TF_DR2, 0); break;
                                case 3: TF_WRITE64(pTF, TF_DR3, 0); break;
                            }
                        }
                    }
                    __except (EXCEPTION_EXECUTE_HANDLER) {
                        /* Best effort */
                    }
                    ObDereferenceObject(thread);
                }

                KeAcquireSpinLock(&g_BpLock, &oldIrql);
            }
            else if (g_Breakpoints[i].Type == KF_BP_MEMORY) {
                /* Restore page protection */
                KeReleaseSpinLock(&g_BpLock, oldIrql);
                KfRemoveMemoryBreakpoint(i);
                KeAcquireSpinLock(&g_BpLock, &oldIrql);
            }

            g_Breakpoints[i].Active = FALSE;
            status = STATUS_SUCCESS;
            break;
        }
    }

    KeReleaseSpinLock(&g_BpLock, oldIrql);

    Irp->IoStatus.Information = 0;
    return status;
}

/* ================================================================== */
/* Helper functions for debughook.c (KD-style step-past)               */
/* ================================================================== */

/*
 * Find a SW breakpoint at the given address for the given PID.
 * Returns the original byte in *origByte if found, FALSE otherwise.
 * Safe to call at DISPATCH_LEVEL (uses spinlock).
 */
BOOLEAN KfFindSwBpOrigByte(ULONG64 address, ULONG pid, PUCHAR origByte)
{
    KIRQL oldIrql;
    ULONG i;
    BOOLEAN found = FALSE;

    if (!g_BpInitialized) return FALSE;

    KeAcquireSpinLock(&g_BpLock, &oldIrql);
    for (i = 0; i < KF_MAX_BREAKPOINTS; i++) {
        if (g_Breakpoints[i].Active &&
            g_Breakpoints[i].Type == KF_BP_SOFTWARE &&
            g_Breakpoints[i].Address == address &&
            (g_Breakpoints[i].ProcessId == pid || g_Breakpoints[i].ProcessId == 0)) {
            *origByte = g_Breakpoints[i].OrigByte;
            found = TRUE;
            break;
        }
    }
    KeReleaseSpinLock(&g_BpLock, oldIrql);
    return found;
}

/*
 * Check if any SW breakpoint exists at the given address (any PID).
 * Used for non-target processes that hit a shared-page INT3.
 */
/*
 * Remove ALL active breakpoints (called during session reset).
 * Restores original bytes for SW BPs, clears DR for HW BPs,
 * restores page protection for memory BPs.
 * Must be called at PASSIVE_LEVEL.
 */
void KfRemoveAllBreakpoints(void)
{
    KIRQL    oldIrql;
    ULONG    i;

    if (!g_BpInitialized) return;

    for (i = 0; i < KF_MAX_BREAKPOINTS; i++) {
        KeAcquireSpinLock(&g_BpLock, &oldIrql);
        if (!g_Breakpoints[i].Active) {
            KeReleaseSpinLock(&g_BpLock, oldIrql);
            continue;
        }

        if (g_Breakpoints[i].Type == KF_BP_SOFTWARE) {
            PEPROCESS process = NULL;
            ULONG     pid   = g_Breakpoints[i].ProcessId;
            ULONG64   addr  = g_Breakpoints[i].Address;
            UCHAR     orig  = g_Breakpoints[i].OrigByte;

            KeReleaseSpinLock(&g_BpLock, oldIrql);

            if (NT_SUCCESS(PsLookupProcessByProcessId((HANDLE)(ULONG_PTR)pid, &process))) {
                KfWriteProcessMemory(process, addr, &orig, 1);
                ObDereferenceObject(process);
            }

            KeAcquireSpinLock(&g_BpLock, &oldIrql);
        }
        else if (g_Breakpoints[i].Type == KF_BP_HARDWARE ||
                 g_Breakpoints[i].Type == KF_BP_HW_WRITE ||
                 g_Breakpoints[i].Type == KF_BP_HW_READWRITE) {
            PETHREAD thread = NULL;
            ULONG    hwSlot   = g_Breakpoints[i].HwSlot;
            ULONG    threadId = g_Breakpoints[i].ThreadId;

            KeReleaseSpinLock(&g_BpLock, oldIrql);

            if (NT_SUCCESS(PsLookupThreadByThreadId((HANDLE)(ULONG_PTR)threadId, &thread))) {
                __try {
                    PVOID pTF = KfGetTrapFrame(thread);
                    if (pTF != NULL) {
                        ULONG64 dr7 = TF_READ64(pTF, TF_DR7);
                        dr7 &= ~(1ULL << (hwSlot * 2));
                        dr7 &= ~(0xFULL << (16 + hwSlot * 4));
                        TF_WRITE64(pTF, TF_DR7, dr7);
                        switch (hwSlot) {
                            case 0: TF_WRITE64(pTF, TF_DR0, 0); break;
                            case 1: TF_WRITE64(pTF, TF_DR1, 0); break;
                            case 2: TF_WRITE64(pTF, TF_DR2, 0); break;
                            case 3: TF_WRITE64(pTF, TF_DR3, 0); break;
                        }
                    }
                } __except (EXCEPTION_EXECUTE_HANDLER) { }
                ObDereferenceObject(thread);
            }

            KeAcquireSpinLock(&g_BpLock, &oldIrql);
        }
        else if (g_Breakpoints[i].Type == KF_BP_MEMORY) {
            KeReleaseSpinLock(&g_BpLock, oldIrql);
            KfRemoveMemoryBreakpoint(i);
            KeAcquireSpinLock(&g_BpLock, &oldIrql);
        }

        g_Breakpoints[i].Active = FALSE;
        KeReleaseSpinLock(&g_BpLock, oldIrql);
    }

    DbgPrint("[KernelFlirt] All breakpoints removed (reset)\n");
}

BOOLEAN KfFindAnySwBpOrigByte(ULONG64 address, PUCHAR origByte)
{
    KIRQL oldIrql;
    ULONG i;
    BOOLEAN found = FALSE;

    if (!g_BpInitialized) return FALSE;

    KeAcquireSpinLock(&g_BpLock, &oldIrql);
    for (i = 0; i < KF_MAX_BREAKPOINTS; i++) {
        if (g_Breakpoints[i].Active &&
            g_Breakpoints[i].Type == KF_BP_SOFTWARE &&
            g_Breakpoints[i].Address == address) {
            *origByte = g_Breakpoints[i].OrigByte;
            found = TRUE;
            break;
        }
    }
    KeReleaseSpinLock(&g_BpLock, oldIrql);
    return found;
}
