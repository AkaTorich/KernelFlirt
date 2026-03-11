/*
 * KernelFlirt - KdTrap Hook (v2)
 * debughook.c - Intercepts kernel debug traps (#DB/#BP) via KdTrap patching
 *
 * Architecture (KdTrap-based):
 *   On modern Windows 10/11, KiDispatchException calls KdTrap for debug
 *   exceptions.  KdTrap checks KdpDebugRoutineSelect:
 *     == 0 -> calls KdpStub (returns FALSE, no debugger)
 *     == 1 -> calls KdpTrap (kernel debug handler)
 *
 *   We:
 *     1. Pattern-scan ntoskrnl .text for KdTrap
 *     2. Extract KdpDebugRoutineSelect from CMP in KdTrap, set it to 1
 *     3. Patch the CALL KdpTrap displacement -> our handler
 *     4. Save original KdpTrap address for pass-through
 *
 *   This avoids modifying the KiDebugRoutine pointer (NULL on Win11 24H2)
 *   and instead hooks at the KdTrap dispatch level.
 *
 * KD-style step-past:
 *   The hook handles SW breakpoint step-past INTERNALLY:
 *   For target process INT3:
 *     1. Look up BP in breakpoint table, get original byte
 *     2. Report event to UI, block thread
 *     3. On ContinueDebugEvent(STEP_PAST):
 *        - Restore original byte via MDL
 *        - Set TF in ContextRecord
 *        - Return TRUE -> thread executes 1 original instruction
 *     4. On SingleStep (re-arm):
 *        - Write 0xCC back via MDL
 *        - If mode was STEP_PAST (Run): return TRUE silently
 *        - If mode was STEP_INTO: report SingleStep to UI, block
 *
 *   For non-target process INT3 (shared page):
 *     1. Restore original byte, set TF, mark re-arm
 *     2. Return TRUE -> thread runs 1 instruction
 *     3. On SingleStep: write 0xCC back, clear TF, return TRUE (transparent)
 */

#include <ntddk.h>
#include <ntimage.h>
#include <intrin.h>
#include "debughook.h"
#include "ntundoc.h"
#include "../../include/kf_shared.h"

/* ------------------------------------------------------------------ */
/* KdpTrap/KdTrap function pointer type                                */
/* ------------------------------------------------------------------ */

typedef BOOLEAN (*PKDEBUG_ROUTINE)(
    IN PVOID                TrapFrame,
    IN PVOID                ExceptionFrame,
    IN PEXCEPTION_RECORD    ExceptionRecord,
    IN PCONTEXT             ContextRecord,
    IN KPROCESSOR_MODE      PreviousMode,
    IN BOOLEAN              SecondChance
);

/* ------------------------------------------------------------------ */
/* Helpers from breakpoint.c                                           */
/* ------------------------------------------------------------------ */

extern BOOLEAN KfFindSwBpOrigByte(ULONG64 address, ULONG pid, PUCHAR origByte);
extern BOOLEAN KfFindAnySwBpOrigByte(ULONG64 address, PUCHAR origByte);

/* ------------------------------------------------------------------ */
/* Globals                                                             */
/* ------------------------------------------------------------------ */

/* KdTrap hook state */
static PUCHAR   g_KdTrap                   = NULL;
static PUCHAR   g_KdpTrap                  = NULL;
static PULONG   g_pKdpDebugRoutineSelect   = NULL;
static ULONG    g_OrigSelectValue          = 0;

/* KdDebuggerEnabled / KdDebuggerNotPresent — must be set so
   KiDispatchException actually calls KdTrap for user-mode exceptions */
static PBOOLEAN g_pKdDebuggerEnabled       = NULL;
static PBOOLEAN g_pKdDebuggerNotPresent    = NULL;
static BOOLEAN  g_OrigKdDebuggerEnabled    = FALSE;
static BOOLEAN  g_OrigKdDebuggerNotPresent = TRUE;

/* CALL-site patch (primary approach) */
static PUCHAR   g_CallSite                 = NULL;   /* &E8 byte in KdTrap */
static INT32    g_OrigCallDisp             = 0;       /* Original rel32 */

/* Inline hook fallback (when distance > 2GB) */
static PUCHAR   g_Trampoline              = NULL;
static UCHAR    g_OrigEntryBytes[16];
static BOOLEAN  g_UsedInlineHook          = FALSE;

/* Original KdpTrap as callable function pointer */
static PKDEBUG_ROUTINE g_OrigKdpTrap      = NULL;

static BOOLEAN          g_HookInstalled   = FALSE;
static ULONG            g_TargetPid       = 0;

/* Debug event state */
static KF_DEBUG_EVENT   g_DebugEvent;
static BOOLEAN          g_EventPending   = FALSE;
static PIRP             g_WaitIrp        = NULL;
static KSPIN_LOCK       g_DbgLock;
static KEVENT           g_ContinueEvent;
static BOOLEAN          g_ThreadBlocked  = FALSE;

/* Continue mode (set by ContinueDebugEvent IOCTL before signaling) */
static ULONG            g_ContinueMode  = KF_CONTINUE_RUN;

/* Step-past state for target process */
static BOOLEAN          g_StepPastPending = FALSE;
static ULONG64          g_StepPastAddr    = 0;
static BOOLEAN          g_StepPastAutoRun = TRUE;

/* Transparent step-past for non-target processes */
#define MAX_TRANSPARENT  16
static struct {
    ULONG   Tid;
    ULONG64 Addr;
    BOOLEAN Active;
} g_Transparent[MAX_TRANSPARENT];

/* Forward declarations */
static BOOLEAN KfDebugHandler(
    IN PVOID TrapFrame, IN PVOID ExceptionFrame,
    IN PEXCEPTION_RECORD ExceptionRecord, IN PCONTEXT ContextRecord,
    IN KPROCESSOR_MODE PreviousMode, IN BOOLEAN SecondChance);
static void KfCancelWaitIrp(PDEVICE_OBJECT DeviceObject, PIRP Irp);

/* ------------------------------------------------------------------ */
/* MDL-based byte write (works in current process context)             */
/* ------------------------------------------------------------------ */

static NTSTATUS KfWriteByteInContext(ULONG64 address, UCHAR byte)
{
    PMDL    mdl = NULL;
    PVOID   mapped = NULL;
    NTSTATUS status = STATUS_SUCCESS;

    mdl = IoAllocateMdl((PVOID)(ULONG_PTR)address, 1, FALSE, FALSE, NULL);
    if (!mdl) return STATUS_INSUFFICIENT_RESOURCES;

    __try {
        MmProbeAndLockPages(mdl, UserMode, IoReadAccess);

        mapped = MmMapLockedPagesSpecifyCache(
            mdl, KernelMode, MmNonCached, NULL, FALSE, NormalPagePriority);
        if (!mapped) {
            MmUnlockPages(mdl);
            IoFreeMdl(mdl);
            return STATUS_INSUFFICIENT_RESOURCES;
        }

        status = MmProtectMdlSystemAddress(mdl, PAGE_READWRITE);
        if (NT_SUCCESS(status)) {
            *(UCHAR *)mapped = byte;
        }

        MmUnmapLockedPages(mapped, mdl);
        MmUnlockPages(mdl);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        status = GetExceptionCode();
        __try {
            if (mapped) MmUnmapLockedPages(mapped, mdl);
            MmUnlockPages(mdl);
        } __except (EXCEPTION_EXECUTE_HANDLER) { /* ignore */ }
    }

    IoFreeMdl(mdl);
    return status;
}

/* ------------------------------------------------------------------ */
/* MDL-remap kernel code patching (Blackbone / kdmapper style)         */
/*                                                                     */
/* Standard approach used by virtually all kernel hooking tools:        */
/*   1. IoAllocateMdl for the target kernel VA                         */
/*   2. MmBuildMdlForNonPagedPool (kernel code is non-paged)           */
/*   3. MmMapLockedPagesSpecifyCache -> new writable VA mapping        */
/*   4. MmProtectMdlSystemAddress(PAGE_EXECUTE_READWRITE)              */
/*   5. Write through the new mapping                                  */
/*   6. Unmap + free                                                   */
/*                                                                     */
/* This works because it creates a SECOND virtual mapping to the same  */
/* physical page, with writable permissions. The original mapping       */
/* stays read-only. No PTE/CR0 manipulation needed.                    */
/* ------------------------------------------------------------------ */

static NTSTATUS KfPatchBytes(PVOID dest, const void *src, SIZE_T size)
{
    PMDL    mdl = NULL;
    PVOID   mapped = NULL;
    NTSTATUS status;
    SIZE_T  i;

    DbgPrint("[KernelFlirt] KfPatchBytes(MDL): dest=%p size=%llu\n", dest, (ULONG64)size);

    if (!MmIsAddressValid(dest) || !MmIsAddressValid((PUCHAR)dest + size - 1)) {
        DbgPrint("[KernelFlirt] KfPatchBytes: dest pages not valid\n");
        return STATUS_ACCESS_VIOLATION;
    }

    /* Allocate MDL for the target region */
    mdl = IoAllocateMdl(dest, (ULONG)size, FALSE, FALSE, NULL);
    if (!mdl) {
        DbgPrint("[KernelFlirt] KfPatchBytes: IoAllocateMdl failed\n");
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    /* Lock pages — use KernelMode (not BuildForNonPagedPool, which
       bugchecks on image section pages that aren't from NonPagedPool) */
    __try {
        MmProbeAndLockPages(mdl, KernelMode, IoReadAccess);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        status = GetExceptionCode();
        DbgPrint("[KernelFlirt] KfPatchBytes: MmProbeAndLockPages failed 0x%08X\n", status);
        IoFreeMdl(mdl);
        return status;
    }

    /* Map locked pages to a new VA */
    mapped = MmMapLockedPagesSpecifyCache(
        mdl, KernelMode, MmCached, NULL, FALSE, NormalPagePriority);
    if (!mapped) {
        DbgPrint("[KernelFlirt] KfPatchBytes: MmMapLockedPages failed\n");
        MmUnlockPages(mdl);
        IoFreeMdl(mdl);
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    DbgPrint("[KernelFlirt] KfPatchBytes: mapped %p -> %p\n", dest, mapped);

    /* Make the new mapping writable */
    status = MmProtectMdlSystemAddress(mdl, PAGE_EXECUTE_READWRITE);
    if (!NT_SUCCESS(status)) {
        DbgPrint("[KernelFlirt] KfPatchBytes: MmProtectMdlSystemAddress failed 0x%08X\n", status);
        MmUnmapLockedPages(mapped, mdl);
        MmUnlockPages(mdl);
        IoFreeMdl(mdl);
        return status;
    }

    /* Write through the new mapping */
    RtlCopyMemory(mapped, src, size);

    /* Unmap, unlock, free */
    MmUnmapLockedPages(mapped, mdl);
    MmUnlockPages(mdl);
    IoFreeMdl(mdl);

    /* Verify by reading original VA */
    {
        BOOLEAN match = TRUE;
        for (i = 0; i < size; i++) {
            if (((volatile UCHAR *)dest)[i] != ((const UCHAR *)src)[i]) {
                match = FALSE;
                break;
            }
        }
        if (match) {
            DbgPrint("[KernelFlirt] KfPatchBytes(MDL): VERIFIED %llu bytes at %p OK\n",
                     (ULONG64)size, dest);
        } else {
            DbgPrint("[KernelFlirt] KfPatchBytes(MDL): WRITE FAILED — readback mismatch at byte %llu! (got 0x%02X, expected 0x%02X)\n",
                     (ULONG64)i, ((UCHAR *)dest)[i], ((const UCHAR *)src)[i]);
            return STATUS_UNSUCCESSFUL;
        }
    }
    return STATUS_SUCCESS;
}

/* ------------------------------------------------------------------ */
/* Find ntoskrnl base by scanning backward from a known export         */
/* ------------------------------------------------------------------ */

static PVOID KfFindNtoskrnlBase(void)
{
    UNICODE_STRING name;
    PUCHAR addr;
    int i;

    RtlInitUnicodeString(&name, L"RtlInitUnicodeString");
    addr = (PUCHAR)MmGetSystemRoutineAddress(&name);
    if (!addr) {
        DbgPrint("[KernelFlirt] RtlInitUnicodeString not resolved\n");
        return NULL;
    }

    /* Align to page boundary and scan backward for MZ header */
    addr = (PUCHAR)((ULONG_PTR)addr & ~(ULONG_PTR)0xFFF);
    for (i = 0; i < 0x2000; i++, addr -= 0x1000) {
        if (!MmIsAddressValid(addr))
            continue;
        if (addr[0] == 'M' && addr[1] == 'Z') {
            DbgPrint("[KernelFlirt] ntoskrnl base: %p\n", addr);
            return addr;
        }
    }

    DbgPrint("[KernelFlirt] ntoskrnl base not found\n");
    return NULL;
}

/* ------------------------------------------------------------------ */
/* Pattern scan for KdTrap in ntoskrnl .text section                   */
/*                                                                     */
/* KdTrap layout on Win10/Win11:                                       */
/*   48 83 EC 38                      sub rsp, 38h                     */
/*   83 3D [disp32] 00               cmp [KdpDebugRoutineSelect], 0   */
/*   8A 44 24 68                      mov al, [rsp+68h]  (SecondChance)*/
/*   88 44 24 28                      mov [rsp+28h], al                */
/*   8A 44 24 60                      mov al, [rsp+60h]  (PrevMode)   */
/*   88 44 24 20                      mov [rsp+20h], al                */
/*   74 XX / 0F 84 ...               jz stub_path                     */
/*   E8 [disp32]                      call KdpTrap                     */
/* ------------------------------------------------------------------ */

static PUCHAR KfPatternScanKdTrap(PUCHAR ntBase)
{
    PIMAGE_DOS_HEADER dos;
    PIMAGE_NT_HEADERS64 nt;
    PIMAGE_SECTION_HEADER sec;
    PUCHAR textBase = NULL;
    ULONG textSize = 0;
    ULONG off;
    USHORT i;

    static const UCHAR prefix[4] = { 0x48, 0x83, 0xEC, 0x38 };
    static const UCHAR suffix[16] = {
        0x8A, 0x44, 0x24, 0x68, 0x88, 0x44, 0x24, 0x28,
        0x8A, 0x44, 0x24, 0x60, 0x88, 0x44, 0x24, 0x20
    };

    dos = (PIMAGE_DOS_HEADER)ntBase;
    if (!MmIsAddressValid(dos)) {
        DbgPrint("[KernelFlirt] DOS header at %p is not valid\n", ntBase);
        return NULL;
    }
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) {
        DbgPrint("[KernelFlirt] Invalid DOS signature at %p\n", ntBase);
        return NULL;
    }

    nt = (PIMAGE_NT_HEADERS64)(ntBase + dos->e_lfanew);
    if (!MmIsAddressValid(nt)) {
        DbgPrint("[KernelFlirt] NT header at %p is not valid\n", nt);
        return NULL;
    }
    if (nt->Signature != IMAGE_NT_SIGNATURE) {
        DbgPrint("[KernelFlirt] Invalid NT signature\n");
        return NULL;
    }

    sec = IMAGE_FIRST_SECTION(nt);
    for (i = 0; i < nt->FileHeader.NumberOfSections; i++) {
        if (!MmIsAddressValid(&sec[i])) break;
        if (sec[i].Characteristics & IMAGE_SCN_CNT_CODE) {
            textBase = ntBase + sec[i].VirtualAddress;
            textSize = sec[i].Misc.VirtualSize;
            DbgPrint("[KernelFlirt] Code section: %p, size=0x%X\n", textBase, textSize);
            break;
        }
    }

    if (!textBase || textSize < 32) {
        DbgPrint("[KernelFlirt] Code section not found\n");
        return NULL;
    }

    for (off = 0; off < textSize - 32; off++) {
        PUCHAR p = textBase + off;

        /*
         * MmIsAddressValid check: __try/__except does NOT catch page faults
         * on kernel addresses — they cause immediate bugcheck 0x50.
         * We must validate each page boundary before reading.
         */
        if (!MmIsAddressValid(p)) {
            /* Skip to next page boundary */
            ULONG_PTR nextPage = ((ULONG_PTR)p + 0x1000) & ~(ULONG_PTR)0xFFF;
            ULONG skip = (ULONG)(nextPage - (ULONG_PTR)textBase);
            if (skip > off)
                off = skip - 1;
            continue;
        }

        /* Also check the end of our match range (27 bytes ahead) */
        if (!MmIsAddressValid(p + 26)) {
            ULONG_PTR nextPage = ((ULONG_PTR)(p + 26) + 0x1000) & ~(ULONG_PTR)0xFFF;
            ULONG skip = (ULONG)(nextPage - (ULONG_PTR)textBase);
            if (skip > off)
                off = skip - 1;
            continue;
        }

        /* Match prefix: 48 83 EC 38 */
        if (RtlCompareMemory(p, prefix, 4) != 4) continue;

        /* Match suffix at offset 11 (after 7-byte CMP instruction) */
        if (RtlCompareMemory(p + 11, suffix, 16) != 16) continue;

        /* Verify CMP dword ptr [rip+disp32], 0 at offset 4 */
        if (p[4] == 0x83 && p[5] == 0x3D && p[10] == 0x00) {
            DbgPrint("[KernelFlirt] KdTrap found at %p (.text+0x%X)\n", p, off);
            return p;
        }
    }

    DbgPrint("[KernelFlirt] KdTrap pattern not found in .text\n");
    return NULL;
}

/* ------------------------------------------------------------------ */
/* Extract KdpDebugRoutineSelect from CMP in KdTrap                    */
/* CMP dword ptr [rip+disp32], 0 at KdTrap+4:                         */
/*   83 3D [disp32] 00 (7 bytes total)                                 */
/*   Address = KdTrap + 4 + 7 + disp32 = KdTrap + 11 + disp32         */
/* ------------------------------------------------------------------ */

static PULONG KfExtractKdpDebugRoutineSelect(PUCHAR kdTrap)
{
    INT32 disp;
    PULONG target;

    if (!MmIsAddressValid(kdTrap + 6) || !MmIsAddressValid(kdTrap + 9)) {
        DbgPrint("[KernelFlirt] CMP disp at %p is not valid\n", kdTrap + 6);
        return NULL;
    }
    disp = *(INT32 *)(kdTrap + 6);
    target = (PULONG)(kdTrap + 11 + disp);

    if (!MmIsAddressValid(target)) {
        DbgPrint("[KernelFlirt] KdpDebugRoutineSelect at %p is not valid\n", target);
        return NULL;
    }
    DbgPrint("[KernelFlirt] KdpDebugRoutineSelect at %p, value=%u\n", target, *target);
    return target;
}

/* ------------------------------------------------------------------ */
/* Find the CALL rel32 to KdpTrap inside KdTrap                       */
/* Scan from offset 27 (after full matched pattern) for first E8       */
/* ------------------------------------------------------------------ */

static PUCHAR KfFindKdpTrapCallSite(PUCHAR kdTrap, PUCHAR *outKdpTrap)
{
    int i;

    for (i = 27; i < 96; i++) {
        if (!MmIsAddressValid(kdTrap + i))
            break;

        if (kdTrap[i] == 0xE8) {
            INT32 disp;
            PUCHAR target;

            if (!MmIsAddressValid(kdTrap + i + 4))
                break;

            disp = *(INT32 *)(kdTrap + i + 1);
            target = kdTrap + i + 5 + disp;

            /* Sanity: target should be a kernel address */
            if ((ULONG_PTR)target < 0xFFFF800000000000ULL)
                continue;

            DbgPrint("[KernelFlirt] CALL at KdTrap+0x%X -> %p (KdpTrap)\n", i, target);
            if (outKdpTrap) *outKdpTrap = target;
            return kdTrap + i;  /* Address of the E8 byte */
        }
    }

    DbgPrint("[KernelFlirt] KdpTrap CALL not found in KdTrap\n");
    return NULL;
}

/* ------------------------------------------------------------------ */
/* Fill KF_DEBUG_EVENT from exception context                          */
/* ------------------------------------------------------------------ */

static void KfFillDebugEvent(
    PKF_DEBUG_EVENT         evt,
    PEXCEPTION_RECORD       ExceptionRecord,
    PCONTEXT                ContextRecord,
    KPROCESSOR_MODE         PreviousMode)
{
    RtlZeroMemory(evt, sizeof(*evt));

    if (ExceptionRecord->ExceptionCode == STATUS_GUARD_PAGE_VIOLATION) {
        evt->Type = KF_DBG_MEMORY_BP;
    } else if (ExceptionRecord->ExceptionCode == STATUS_BREAKPOINT) {
        evt->Type = KF_DBG_BREAKPOINT;
    } else if (ExceptionRecord->ExceptionCode == STATUS_SINGLE_STEP) {
        if (ContextRecord->Dr6 & 0x0F) {
            ULONG64 dr6 = ContextRecord->Dr6;
            ULONG64 dr7 = ContextRecord->Dr7;
            int slot;
            BOOLEAN isWatchpoint = FALSE;
            for (slot = 0; slot < 4; slot++) {
                if (dr6 & ((ULONG64)1 << slot)) {
                    ULONG condBits = (ULONG)((dr7 >> (16 + slot * 4)) & 0x3);
                    if (condBits != 0) isWatchpoint = TRUE;
                    break;
                }
            }
            evt->Type = isWatchpoint ? KF_DBG_HW_WATCHPOINT : KF_DBG_HW_BREAKPOINT;
        } else {
            evt->Type = KF_DBG_SINGLE_STEP;
        }
    } else {
        evt->Type = KF_DBG_BREAKPOINT;
    }

    evt->ProcessId = (ULONG)(ULONG_PTR)PsGetCurrentProcessId();
    evt->ThreadId  = (ULONG)(ULONG_PTR)PsGetCurrentThreadId();
    evt->Address   = ContextRecord->Rip;
    evt->PreviousMode = (ULONG)PreviousMode;

    evt->Registers.Rax    = ContextRecord->Rax;
    evt->Registers.Rbx    = ContextRecord->Rbx;
    evt->Registers.Rcx    = ContextRecord->Rcx;
    evt->Registers.Rdx    = ContextRecord->Rdx;
    evt->Registers.Rsi    = ContextRecord->Rsi;
    evt->Registers.Rdi    = ContextRecord->Rdi;
    evt->Registers.Rbp    = ContextRecord->Rbp;
    evt->Registers.Rsp    = ContextRecord->Rsp;
    evt->Registers.R8     = ContextRecord->R8;
    evt->Registers.R9     = ContextRecord->R9;
    evt->Registers.R10    = ContextRecord->R10;
    evt->Registers.R11    = ContextRecord->R11;
    evt->Registers.R12    = ContextRecord->R12;
    evt->Registers.R13    = ContextRecord->R13;
    evt->Registers.R14    = ContextRecord->R14;
    evt->Registers.R15    = ContextRecord->R15;
    evt->Registers.Rip    = ContextRecord->Rip;
    evt->Registers.Rflags = ContextRecord->EFlags;
    evt->Registers.Cs     = ContextRecord->SegCs;
    evt->Registers.Ds     = ContextRecord->SegDs;
    evt->Registers.Es     = ContextRecord->SegEs;
    evt->Registers.Fs     = ContextRecord->SegFs;
    evt->Registers.Gs     = ContextRecord->SegGs;
    evt->Registers.Ss     = ContextRecord->SegSs;
    evt->Registers.Dr0    = ContextRecord->Dr0;
    evt->Registers.Dr1    = ContextRecord->Dr1;
    evt->Registers.Dr2    = ContextRecord->Dr2;
    evt->Registers.Dr3    = ContextRecord->Dr3;
    evt->Registers.Dr6    = ContextRecord->Dr6;
    evt->Registers.Dr7    = ContextRecord->Dr7;
}

/* ------------------------------------------------------------------ */
/* Complete a pending WAIT IRP with debug event data                   */
/* ------------------------------------------------------------------ */

static void KfCompleteWaitIrp(PIRP Irp, PKF_DEBUG_EVENT event)
{
    PKF_DEBUG_EVENT outBuf;
    outBuf = (PKF_DEBUG_EVENT)Irp->AssociatedIrp.SystemBuffer;
    RtlCopyMemory(outBuf, event, sizeof(KF_DEBUG_EVENT));
    Irp->IoStatus.Information = sizeof(KF_DEBUG_EVENT);
    Irp->IoStatus.Status      = STATUS_SUCCESS;
    IoCompleteRequest(Irp, IO_NO_INCREMENT);
}

/* ------------------------------------------------------------------ */
/* Report event + block thread (shared logic)                          */
/* ------------------------------------------------------------------ */

static void KfReportAndBlock(
    PEXCEPTION_RECORD ExceptionRecord,
    PCONTEXT          ContextRecord,
    KPROCESSOR_MODE   PreviousMode)
{
    KIRQL   oldIrql;
    PIRP    irpToComplete = NULL;

    KeAcquireSpinLock(&g_DbgLock, &oldIrql);

    KfFillDebugEvent(&g_DebugEvent, ExceptionRecord, ContextRecord, PreviousMode);

    if (g_WaitIrp != NULL) {
        irpToComplete = g_WaitIrp;
        g_WaitIrp = NULL;
        IoSetCancelRoutine(irpToComplete, NULL);
        g_EventPending = FALSE;
    } else {
        g_EventPending = TRUE;
    }

    g_ThreadBlocked = TRUE;
    KeClearEvent(&g_ContinueEvent);

    KeReleaseSpinLock(&g_DbgLock, oldIrql);

    if (irpToComplete)
        KfCompleteWaitIrp(irpToComplete, &g_DebugEvent);

    /* Block until ContinueDebugEvent */
    if (KeGetCurrentIrql() <= APC_LEVEL) {
        DbgPrint("[KernelFlirt] Blocking TID %u\n",
                 (ULONG)(ULONG_PTR)PsGetCurrentThreadId());

        KeWaitForSingleObject(&g_ContinueEvent, Executive,
                              KernelMode, FALSE, NULL);

        DbgPrint("[KernelFlirt] TID %u resumed (mode=%u)\n",
                 (ULONG)(ULONG_PTR)PsGetCurrentThreadId(), g_ContinueMode);
    } else {
        DbgPrint("[KernelFlirt] Cannot block at IRQL %d\n", KeGetCurrentIrql());
    }

    KeAcquireSpinLock(&g_DbgLock, &oldIrql);
    g_ThreadBlocked = FALSE;
    KeReleaseSpinLock(&g_DbgLock, oldIrql);
}

/* ------------------------------------------------------------------ */
/* Debug handler - called instead of KdpTrap                           */
/* Same signature as KdpTrap (6 params, returns BOOLEAN)               */
/* ------------------------------------------------------------------ */

static BOOLEAN KfDebugHandler(
    IN PVOID                TrapFrame,
    IN PVOID                ExceptionFrame,
    IN PEXCEPTION_RECORD    ExceptionRecord,
    IN PCONTEXT             ContextRecord,
    IN KPROCESSOR_MODE      PreviousMode,
    IN BOOLEAN              SecondChance)
{
    ULONG currentPid = (ULONG)(ULONG_PTR)PsGetCurrentProcessId();
    ULONG currentTid = (ULONG)(ULONG_PTR)PsGetCurrentThreadId();
    BOOLEAN isTarget = (g_TargetPid == 0 || currentPid == g_TargetPid);

    UNREFERENCED_PARAMETER(TrapFrame);
    UNREFERENCED_PARAMETER(ExceptionFrame);

    DbgPrint("[KernelFlirt] HOOK CALLED: code=0x%08X pid=%u tid=%u addr=%p mode=%d\n",
             ExceptionRecord->ExceptionCode, currentPid, currentTid,
             (PVOID)ExceptionRecord->ExceptionAddress, PreviousMode);

    /* Only handle BP, SingleStep, GuardPage */
    if (ExceptionRecord->ExceptionCode != STATUS_BREAKPOINT &&
        ExceptionRecord->ExceptionCode != STATUS_SINGLE_STEP &&
        ExceptionRecord->ExceptionCode != STATUS_GUARD_PAGE_VIOLATION) {
        return FALSE;  /* Not handled — let normal exception dispatch continue */
    }

    if (SecondChance)
        return FALSE;

    /* ============================================================== */
    /*  SINGLE STEP - check if this is a re-arm after step-past        */
    /* ============================================================== */
    if (ExceptionRecord->ExceptionCode == STATUS_SINGLE_STEP) {

        /* --- Target process: re-arm after step-past --- */
        if (isTarget && g_StepPastPending && currentTid != 0) {
            NTSTATUS st;
            g_StepPastPending = FALSE;

            /* Re-arm the INT3 */
            st = KfWriteByteInContext(g_StepPastAddr, 0xCC);
            DbgPrint("[KernelFlirt] Re-armed 0xCC at %p (status=0x%08X)\n",
                     (PVOID)g_StepPastAddr, st);

            /* Clear TF so thread doesn't keep stepping */
            ContextRecord->EFlags &= ~0x100UL;

            if (g_StepPastAutoRun) {
                /* Run mode: silently continue, don't report */
                return TRUE;
            }

            /* StepIn mode: report SingleStep event to UI */
            DbgPrint("[KernelFlirt] StepIn: reporting SingleStep at %p\n",
                     (PVOID)ContextRecord->Rip);
            KfReportAndBlock(ExceptionRecord, ContextRecord, PreviousMode);

            /* After UI continues from step display, just resume */
            return TRUE;
        }

        /* --- Non-target process: re-arm after transparent step --- */
        {
            int i;
            for (i = 0; i < MAX_TRANSPARENT; i++) {
                if (g_Transparent[i].Active && g_Transparent[i].Tid == currentTid) {
                    NTSTATUS st;
                    ULONG64 addr = g_Transparent[i].Addr;
                    g_Transparent[i].Active = FALSE;

                    /* Re-arm INT3 */
                    st = KfWriteByteInContext(addr, 0xCC);
                    DbgPrint("[KernelFlirt] Transparent re-arm at %p for TID %u (0x%08X)\n",
                             (PVOID)addr, currentTid, st);

                    /* Clear TF */
                    ContextRecord->EFlags &= ~0x100UL;
                    return TRUE;
                }
            }
        }

        /* Not a step-past SingleStep, fall through to normal handling */
        if (!isTarget)
            return FALSE;

        /* Target SingleStep (user-requested or HW BP) */
        goto report_to_ui;
    }

    /* ============================================================== */
    /*  BREAKPOINT (INT3)                                              */
    /* ============================================================== */
    if (ExceptionRecord->ExceptionCode == STATUS_BREAKPOINT) {
        ULONG64 bpAddr = (ULONG64)ExceptionRecord->ExceptionAddress;
        UCHAR   origByte = 0;

        /*
         * Adjust RIP back to BP address.
         * On x64, KiBreakpointTrap already decrements Rip in TrapFrame,
         * so ContextRecord->Rip should already be at the INT3 addr.
         * Set it explicitly to be safe.
         */
        ContextRecord->Rip = bpAddr;

        /* --- Target process --- */
        if (isTarget) {
            if (!KfFindSwBpOrigByte(bpAddr, currentPid, &origByte)) {
                /* Not our BP (compiler int3 padding, etc.) */
                DbgPrint("[KernelFlirt] Target INT3 at %p not in BP table, skipping\n",
                         (PVOID)bpAddr);
                ContextRecord->Rip = bpAddr + 1;
                return TRUE;
            }

            DbgPrint("[KernelFlirt] Target BP hit at %p (orig=0x%02X)\n",
                     (PVOID)bpAddr, origByte);

            /* Report to UI and block */
            KfReportAndBlock(ExceptionRecord, ContextRecord, PreviousMode);

            /*
             * Back from UI continue. Check continue mode:
             *   STEP_PAST / STEP_INTO: restore byte, set TF, prepare re-arm
             *   RUN: just resume (for non-SW-BP events)
             */
            if (g_ContinueMode == KF_CONTINUE_STEP_PAST ||
                g_ContinueMode == KF_CONTINUE_STEP_INTO) {
                NTSTATUS st;

                /* Restore original byte */
                st = KfWriteByteInContext(bpAddr, origByte);
                DbgPrint("[KernelFlirt] Restored 0x%02X at %p (status=0x%08X)\n",
                         origByte, (PVOID)bpAddr, st);

                /* Set TF for single step */
                ContextRecord->EFlags |= 0x100UL;

                /* Prepare for re-arm on next SingleStep */
                g_StepPastPending = TRUE;
                g_StepPastAddr    = bpAddr;
                g_StepPastAutoRun = (g_ContinueMode == KF_CONTINUE_STEP_PAST);
            }

            return TRUE;
        }

        /* --- Non-target process hit our INT3 (shared page) --- */
        if (KfFindAnySwBpOrigByte(bpAddr, &origByte)) {
            NTSTATUS st;
            int slot = -1, i;

            DbgPrint("[KernelFlirt] Non-target PID %u TID %u hit BP at %p\n",
                     currentPid, currentTid, (PVOID)bpAddr);

            /* Restore original byte so thread can execute */
            st = KfWriteByteInContext(bpAddr, origByte);

            /* Set TF for single step */
            ContextRecord->EFlags |= 0x100UL;

            /* Find a free transparent step slot */
            for (i = 0; i < MAX_TRANSPARENT; i++) {
                if (!g_Transparent[i].Active) { slot = i; break; }
            }
            if (slot >= 0) {
                g_Transparent[slot].Active = TRUE;
                g_Transparent[slot].Tid    = currentTid;
                g_Transparent[slot].Addr   = bpAddr;
            } else {
                DbgPrint("[KernelFlirt] WARNING: no transparent step slot!\n");
            }

            return TRUE;
        }

        /* Not our BP — not handled */
        return FALSE;
    }

    /* ============================================================== */
    /*  GUARD PAGE / other — only for target                           */
    /* ============================================================== */
    if (!isTarget)
        return FALSE;

report_to_ui:
    DbgPrint("[KernelFlirt] Event: code=0x%08X addr=%p PID=%u TID=%u\n",
             ExceptionRecord->ExceptionCode,
             (PVOID)ContextRecord->Rip,
             currentPid, currentTid);

    KfReportAndBlock(ExceptionRecord, ContextRecord, PreviousMode);
    return TRUE;
}

/* ------------------------------------------------------------------ */
/* IRP cancel routine                                                  */
/* ------------------------------------------------------------------ */

static void KfCancelWaitIrp(PDEVICE_OBJECT DeviceObject, PIRP Irp)
{
    KIRQL oldIrql;
    UNREFERENCED_PARAMETER(DeviceObject);

    IoReleaseCancelSpinLock(Irp->CancelIrql);

    KeAcquireSpinLock(&g_DbgLock, &oldIrql);
    if (g_WaitIrp == Irp)
        g_WaitIrp = NULL;
    KeReleaseSpinLock(&g_DbgLock, oldIrql);

    Irp->IoStatus.Status      = STATUS_CANCELLED;
    Irp->IoStatus.Information = 0;
    IoCompleteRequest(Irp, IO_NO_INCREMENT);
}

/* ------------------------------------------------------------------ */
/* Public API                                                          */
/* ------------------------------------------------------------------ */

NTSTATUS KfDebugHookInit(void)
{
    KeInitializeSpinLock(&g_DbgLock);
    KeInitializeEvent(&g_ContinueEvent, SynchronizationEvent, FALSE);
    g_HookInstalled   = FALSE;
    g_EventPending    = FALSE;
    g_WaitIrp         = NULL;
    g_ThreadBlocked   = FALSE;
    g_StepPastPending = FALSE;
    g_ContinueMode    = KF_CONTINUE_RUN;

    RtlZeroMemory(&g_DebugEvent, sizeof(g_DebugEvent));
    RtlZeroMemory(g_Transparent, sizeof(g_Transparent));

    return STATUS_SUCCESS;
}

/*
 * Helper: dump N bytes at an address for diagnostics
 */
static void KfDumpBytes(const char *label, PUCHAR addr, int count)
{
    int i;
    DbgPrint("[KernelFlirt] %s (%d bytes at %p):\n", label, count, addr);
    for (i = 0; i < count; i += 8) {
        int remaining = count - i;
        if (!MmIsAddressValid(addr + i)) {
            DbgPrint("[KernelFlirt]   +%02X: (invalid page)\n", i);
            break;
        }
        if (remaining >= 8 && MmIsAddressValid(addr + i + 7))
            DbgPrint("[KernelFlirt]   +%02X: %02X %02X %02X %02X %02X %02X %02X %02X\n",
                     i, addr[i], addr[i+1], addr[i+2], addr[i+3],
                     addr[i+4], addr[i+5], addr[i+6], addr[i+7]);
        else if (remaining >= 4 && MmIsAddressValid(addr + i + 3))
            DbgPrint("[KernelFlirt]   +%02X: %02X %02X %02X %02X\n",
                     i, addr[i], addr[i+1], addr[i+2], addr[i+3]);
    }
}

NTSTATUS KfInstallDebugHook(void)
{
    PVOID ntBase;
    PUCHAR callSite;
    PUCHAR kdpTrap;
    PULONG pSelect;
    INT64  delta;

    if (g_HookInstalled)
        return STATUS_SUCCESS;

    DbgPrint("[KernelFlirt] === InstallDebugHook START ===\n");

    /* Step 1: Find ntoskrnl base */
    ntBase = KfFindNtoskrnlBase();
    if (!ntBase) {
        DbgPrint("[KernelFlirt] FAIL: ntoskrnl base not found\n");
        return STATUS_NOT_FOUND;
    }

    /* Step 2: Pattern scan for KdTrap */
    g_KdTrap = KfPatternScanKdTrap((PUCHAR)ntBase);
    if (!g_KdTrap) {
        DbgPrint("[KernelFlirt] FAIL: KdTrap not found\n");
        return STATUS_NOT_FOUND;
    }

    /* Dump first 64 bytes of KdTrap for verification */
    KfDumpBytes("KdTrap[0..31]", g_KdTrap, 32);
    KfDumpBytes("KdTrap[32..63]", g_KdTrap + 32, 32);

    /* Step 3: Extract KdpDebugRoutineSelect */
    pSelect = KfExtractKdpDebugRoutineSelect(g_KdTrap);
    if (!pSelect) {
        DbgPrint("[KernelFlirt] FAIL: KdpDebugRoutineSelect not found\n");
        return STATUS_NOT_FOUND;
    }
    g_pKdpDebugRoutineSelect = pSelect;
    g_OrigSelectValue = *pSelect;

    /* Step 4: Find CALL KdpTrap inside KdTrap */
    callSite = KfFindKdpTrapCallSite(g_KdTrap, &kdpTrap);
    if (!callSite) {
        DbgPrint("[KernelFlirt] FAIL: KdpTrap call site not found\n");
        return STATUS_NOT_FOUND;
    }
    g_CallSite = callSite;
    g_KdpTrap  = kdpTrap;
    g_OrigCallDisp = *(INT32 *)(callSite + 1);
    g_OrigKdpTrap  = (PKDEBUG_ROUTINE)kdpTrap;

    DbgPrint("[KernelFlirt] KdTrap=%p  KdpTrap=%p  Select@%p=%u\n",
             g_KdTrap, g_KdpTrap, g_pKdpDebugRoutineSelect, g_OrigSelectValue);

    /* Dump first 16 bytes of KdpTrap target */
    KfDumpBytes("KdpTrap[0..15]", kdpTrap, 16);

    /* Step 5: Calculate delta */
    delta = (INT64)((PUCHAR)KfDebugHandler - (callSite + 5));
    DbgPrint("[KernelFlirt] Handler=%p  CallSite=%p  delta=0x%llX  fits_rel32=%d\n",
             (PVOID)KfDebugHandler, callSite, delta,
             (delta >= -2147483648LL && delta <= 2147483647LL) ? 1 : 0);

    /* Step 6: Patch the CALL displacement to redirect to our handler */
    if (delta >= -2147483648LL && delta <= 2147483647LL) {
        /* rel32 patch — just overwrite the 4-byte displacement */
        INT32 newDisp = (INT32)delta;
        NTSTATUS st;

        DbgPrint("[KernelFlirt] Patching CALL rel32 at %p: old=0x%08X new=0x%08X\n",
                 callSite + 1, g_OrigCallDisp, newDisp);

        st = KfPatchBytes(callSite + 1, &newDisp, 4);
        if (!NT_SUCCESS(st)) {
            DbgPrint("[KernelFlirt] FAIL: KfPatchBytes returned 0x%08X\n", st);
            return st;
        }

        /* Verify patch took effect by reading back */
        if (MmIsAddressValid(callSite) && MmIsAddressValid(callSite + 4)) {
            INT32 readBack = *(INT32 *)(callSite + 1);
            PUCHAR resolvedTarget = callSite + 5 + readBack;
            DbgPrint("[KernelFlirt] VERIFY: CALL disp readback=0x%08X -> target=%p (expected=%p)\n",
                     readBack, resolvedTarget, (PVOID)KfDebugHandler);
            if (resolvedTarget == (PUCHAR)KfDebugHandler)
                DbgPrint("[KernelFlirt] VERIFY: PATCH OK!\n");
            else
                DbgPrint("[KernelFlirt] VERIFY: PATCH MISMATCH! readback target != handler\n");
        }

        g_UsedInlineHook = FALSE;
    } else {
        /* Distance > 2GB — use 14-byte inline hook at KdpTrap entry */
        UCHAR jmpStub[14];
        NTSTATUS st;

        DbgPrint("[KernelFlirt] Delta too large for rel32, using inline hook\n");

        /* Allocate trampoline: original 14 bytes + JMP back */
        g_Trampoline = (PUCHAR)ExAllocatePoolWithTag(
            NonPagedPool, 14 + 14, 'KfTr');
        if (!g_Trampoline) {
            DbgPrint("[KernelFlirt] FAIL: Cannot allocate trampoline\n");
            return STATUS_INSUFFICIENT_RESOURCES;
        }

        /* Save original entry bytes */
        RtlCopyMemory(g_OrigEntryBytes, kdpTrap, 14);

        /* Build trampoline: original bytes + absolute JMP to kdpTrap+14 */
        RtlCopyMemory(g_Trampoline, kdpTrap, 14);
        g_Trampoline[14] = 0xFF;
        g_Trampoline[15] = 0x25;
        *(ULONG *)(g_Trampoline + 16) = 0;
        *(ULONG_PTR *)(g_Trampoline + 20) = (ULONG_PTR)(kdpTrap + 14);

        /* Build JMP stub: FF 25 00 00 00 00 [handler addr] */
        jmpStub[0] = 0xFF;
        jmpStub[1] = 0x25;
        *(ULONG *)(jmpStub + 2) = 0;
        *(ULONG_PTR *)(jmpStub + 6) = (ULONG_PTR)KfDebugHandler;

        st = KfPatchBytes(kdpTrap, jmpStub, 14);
        if (!NT_SUCCESS(st)) {
            ExFreePoolWithTag(g_Trampoline, 'KfTr');
            g_Trampoline = NULL;
            DbgPrint("[KernelFlirt] FAIL: inline hook patch returned 0x%08X\n", st);
            return st;
        }

        g_OrigKdpTrap = (PKDEBUG_ROUTINE)g_Trampoline;
        g_UsedInlineHook = TRUE;
    }

    /*
     * DO NOT set KdpDebugRoutineSelect to 1!
     *
     * KdTrap layout:
     *   CMP [KdpDebugRoutineSelect], 0
     *   JNZ  +0x28                     ← if select!=0, jumps to CALL KdpTrap
     *   CALL KdpStub  (+0x1D)          ← if select==0, calls KdpStub (our patched call)
     *   ...
     *   CALL KdpTrap  (+0x28)          ← the real handler (unpatched)
     *
     * We patched the CALL at +0x1D (KdpStub path).
     * If we set select=1, JNZ skips our patch and calls the real KdpTrap → crash.
     * By keeping select=0, the fall-through path hits our patched CALL.
     */
    _mm_mfence();
    DbgPrint("[KernelFlirt] KdpDebugRoutineSelect left at %u (using KdpStub path)\n",
             *g_pKdpDebugRoutineSelect);

    /*
     * Step 8: Set KdDebuggerEnabled=TRUE, KdDebuggerNotPresent=FALSE
     * Without these, KiDispatchException skips KdTrap entirely for
     * user-mode exceptions, so our hook is never called.
     */
    {
        UNICODE_STRING symName;

        RtlInitUnicodeString(&symName, L"KdDebuggerEnabled");
        g_pKdDebuggerEnabled = (PBOOLEAN)MmGetSystemRoutineAddress(&symName);

        RtlInitUnicodeString(&symName, L"KdDebuggerNotPresent");
        g_pKdDebuggerNotPresent = (PBOOLEAN)MmGetSystemRoutineAddress(&symName);

        if (g_pKdDebuggerEnabled) {
            g_OrigKdDebuggerEnabled = *g_pKdDebuggerEnabled;
            *g_pKdDebuggerEnabled = TRUE;
            DbgPrint("[KernelFlirt] KdDebuggerEnabled: %u -> TRUE\n", g_OrigKdDebuggerEnabled);
        } else {
            DbgPrint("[KernelFlirt] WARNING: KdDebuggerEnabled not found\n");
        }

        if (g_pKdDebuggerNotPresent) {
            g_OrigKdDebuggerNotPresent = *g_pKdDebuggerNotPresent;
            *g_pKdDebuggerNotPresent = FALSE;
            DbgPrint("[KernelFlirt] KdDebuggerNotPresent: %u -> FALSE\n", g_OrigKdDebuggerNotPresent);
        } else {
            DbgPrint("[KernelFlirt] WARNING: KdDebuggerNotPresent not found\n");
        }
    }

    /* Final verification: dump patched KdTrap and check all flags */
    KfDumpBytes("KdTrap AFTER patch", g_KdTrap, 48);

    if (g_pKdDebuggerEnabled)
        DbgPrint("[KernelFlirt] VERIFY: KdDebuggerEnabled = %u (expect 1)\n", *g_pKdDebuggerEnabled);
    if (g_pKdDebuggerNotPresent)
        DbgPrint("[KernelFlirt] VERIFY: KdDebuggerNotPresent = %u (expect 0)\n", *g_pKdDebuggerNotPresent);
    if (g_pKdpDebugRoutineSelect)
        DbgPrint("[KernelFlirt] VERIFY: KdpDebugRoutineSelect = %u (expect 0)\n", *g_pKdpDebugRoutineSelect);

    g_HookInstalled = TRUE;
    DbgPrint("[KernelFlirt] === InstallDebugHook COMPLETE ===\n");
    return STATUS_SUCCESS;
}

void KfRemoveDebugHook(void)
{
    KIRQL oldIrql;

    if (!g_HookInstalled)
        return;

    /*
     * Restore KdDebuggerEnabled / KdDebuggerNotPresent FIRST,
     * so KiDispatchException stops calling KdTrap.
     */
    if (g_pKdDebuggerEnabled) {
        *g_pKdDebuggerEnabled = g_OrigKdDebuggerEnabled;
        DbgPrint("[KernelFlirt] KdDebuggerEnabled restored to %u\n", g_OrigKdDebuggerEnabled);
    }
    if (g_pKdDebuggerNotPresent) {
        *g_pKdDebuggerNotPresent = g_OrigKdDebuggerNotPresent;
        DbgPrint("[KernelFlirt] KdDebuggerNotPresent restored to %u\n", g_OrigKdDebuggerNotPresent);
    }

    /* Small delay to let any in-flight calls through our handler finish */
    {
        LARGE_INTEGER delay;
        delay.QuadPart = -10 * 1000 * 50;  /* 50ms */
        KeDelayExecutionThread(KernelMode, FALSE, &delay);
    }

    /* Now restore the code */
    if (g_UsedInlineHook) {
        KfPatchBytes(g_KdpTrap, g_OrigEntryBytes, 14);
        DbgPrint("[KernelFlirt] KdpTrap entry restored\n");

        if (g_Trampoline) {
            ExFreePoolWithTag(g_Trampoline, 'KfTr');
            g_Trampoline = NULL;
        }
    } else if (g_CallSite) {
        KfPatchBytes(g_CallSite + 1, &g_OrigCallDisp, 4);
        DbgPrint("[KernelFlirt] CALL displacement restored at %p\n", g_CallSite);
    }

    g_HookInstalled  = FALSE;
    g_UsedInlineHook = FALSE;

    /* Wake any blocked thread */
    KeAcquireSpinLock(&g_DbgLock, &oldIrql);
    if (g_ThreadBlocked)
        KeSetEvent(&g_ContinueEvent, 0, FALSE);
    KeReleaseSpinLock(&g_DbgLock, oldIrql);

    DbgPrint("[KernelFlirt] Debug hook removed\n");
}

void KfDebugHookCleanup(void)
{
    KIRQL oldIrql;

    KfRemoveDebugHook();

    KeAcquireSpinLock(&g_DbgLock, &oldIrql);
    if (g_WaitIrp) {
        PIRP irp = g_WaitIrp;
        g_WaitIrp = NULL;
        KeReleaseSpinLock(&g_DbgLock, oldIrql);

        IoSetCancelRoutine(irp, NULL);
        irp->IoStatus.Status      = STATUS_CANCELLED;
        irp->IoStatus.Information = 0;
        IoCompleteRequest(irp, IO_NO_INCREMENT);
    } else {
        KeReleaseSpinLock(&g_DbgLock, oldIrql);
    }
}

void KfSetTargetPid(ULONG pid)
{
    g_TargetPid = pid;
    DbgPrint("[KernelFlirt] Target PID = %u\n", pid);
}

BOOLEAN KfIsDebugHookActive(void)
{
    return g_HookInstalled;
}

/* ------------------------------------------------------------------ */
/* IOCTL: WAIT_DEBUG_EVENT                                             */
/* ------------------------------------------------------------------ */

NTSTATUS KfWaitDebugEvent(PIRP Irp, PIO_STACK_LOCATION IoStack)
{
    KIRQL oldIrql;

    if (IoStack->Parameters.DeviceIoControl.OutputBufferLength < sizeof(KF_DEBUG_EVENT)) {
        Irp->IoStatus.Information = 0;
        Irp->IoStatus.Status = STATUS_BUFFER_TOO_SMALL;
        IoCompleteRequest(Irp, IO_NO_INCREMENT);
        return STATUS_BUFFER_TOO_SMALL;
    }

    if (!g_HookInstalled) {
        Irp->IoStatus.Information = 0;
        Irp->IoStatus.Status = STATUS_DEVICE_NOT_READY;
        IoCompleteRequest(Irp, IO_NO_INCREMENT);
        return STATUS_DEVICE_NOT_READY;
    }

    KeAcquireSpinLock(&g_DbgLock, &oldIrql);

    if (g_EventPending) {
        g_EventPending = FALSE;
        KeReleaseSpinLock(&g_DbgLock, oldIrql);
        KfCompleteWaitIrp(Irp, &g_DebugEvent);
        return STATUS_SUCCESS;
    }

    if (g_WaitIrp != NULL) {
        KeReleaseSpinLock(&g_DbgLock, oldIrql);
        Irp->IoStatus.Information = 0;
        Irp->IoStatus.Status = STATUS_DEVICE_BUSY;
        IoCompleteRequest(Irp, IO_NO_INCREMENT);
        return STATUS_DEVICE_BUSY;
    }

    IoMarkIrpPending(Irp);
    IoSetCancelRoutine(Irp, KfCancelWaitIrp);
    g_WaitIrp = Irp;

    KeReleaseSpinLock(&g_DbgLock, oldIrql);
    return STATUS_PENDING;
}

/* ------------------------------------------------------------------ */
/* IOCTL: CONTINUE_DEBUG_EVENT                                         */
/* ------------------------------------------------------------------ */

NTSTATUS KfContinueDebugEvent(PIRP Irp, PIO_STACK_LOCATION IoStack)
{
    ULONG mode = KF_CONTINUE_RUN;

    if (IoStack->Parameters.DeviceIoControl.InputBufferLength >= sizeof(KF_CONTINUE_IN)) {
        PKF_CONTINUE_IN input = (PKF_CONTINUE_IN)Irp->AssociatedIrp.SystemBuffer;
        mode = input->Mode;
    }

    if (!g_HookInstalled) {
        Irp->IoStatus.Information = 0;
        Irp->IoStatus.Status = STATUS_DEVICE_NOT_READY;
        IoCompleteRequest(Irp, IO_NO_INCREMENT);
        return STATUS_DEVICE_NOT_READY;
    }

    /* Set mode BEFORE signaling (handler reads it after wake) */
    g_ContinueMode = mode;

    KeSetEvent(&g_ContinueEvent, 0, FALSE);

    DbgPrint("[KernelFlirt] Continue (mode=%u)\n", mode);

    Irp->IoStatus.Information = 0;
    Irp->IoStatus.Status = STATUS_SUCCESS;
    IoCompleteRequest(Irp, IO_NO_INCREMENT);
    return STATUS_SUCCESS;
}
