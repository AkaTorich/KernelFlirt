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

/* CALL-site patch (primary approach) — patch BOTH paths in KdTrap */
static PUCHAR   g_CallSite                 = NULL;   /* &E8 byte for select=origValue path */
static INT32    g_OrigCallDisp             = 0;       /* Original rel32 */
static PUCHAR   g_CallSite2                = NULL;   /* &E8 byte for the OTHER path */
static INT32    g_OrigCallDisp2            = 0;       /* Original rel32 for other path */

/* Inline hook on primary function (KdpStub or KdpTrap) */
static PUCHAR   g_Trampoline              = NULL;
static UCHAR    g_OrigEntryBytes[16];
static BOOLEAN  g_UsedInlineHook          = FALSE;

/* Original KdpTrap as callable function pointer */
static PKDEBUG_ROUTINE g_OrigKdpTrap      = NULL;

/* KiDebugRoutine function pointer (in ntoskrnl .data section) */
static PKDEBUG_ROUTINE *g_pKiDebugRoutine  = NULL;
static PKDEBUG_ROUTINE  g_OrigKiDebugRoutine = NULL;

static BOOLEAN          g_HookInstalled   = FALSE;
static ULONG            g_TargetPid       = 0;

/* Diagnostic counters (no DbgPrint in handler — it may reset KdDebuggerEnabled!) */
static volatile LONG    g_HookCallCount        = 0;
static volatile LONG    g_HookBpHitCount       = 0;
static volatile LONG    g_HookBpNotFoundCount  = 0;
static volatile LONG    g_HookStepCount        = 0;
static volatile LONG    g_HookTargetCallCount  = 0;  /* calls where isTarget=TRUE */
static volatile ULONG64 g_LastTargetAddr       = 0;  /* last exception addr from target */
static volatile ULONG   g_LastTargetCode       = 0;  /* last exception code from target */
static volatile ULONG   g_LastNonTargetPid     = 0;  /* last non-target PID seen */

/* Debug event state */
static KF_DEBUG_EVENT   g_DebugEvent;
static BOOLEAN          g_EventPending   = FALSE;
static PIRP             g_WaitIrp        = NULL;
static KSPIN_LOCK       g_DbgLock;
static KEVENT           g_ContinueEvent;
static BOOLEAN          g_ThreadBlocked  = FALSE;

/* Continue mode (set by ContinueDebugEvent IOCTL before signaling) */
static ULONG            g_ContinueMode  = KF_CONTINUE_RUN;

/* Pending RIP/RSP override (for IAT tracing — applied to ContextRecord after wake) */
static ULONG            g_ContinueFlags = 0;
static ULONG64          g_ContinueNewRip = 0;
static ULONG64          g_ContinueNewRsp = 0;

/* Step-past state for target process */
static BOOLEAN          g_StepPastPending = FALSE;
static ULONG64          g_StepPastAddr    = 0;
static BOOLEAN          g_StepPastAutoRun = TRUE;

/* Fast trace mode: step internally without reporting until RIP exits range */
static volatile BOOLEAN g_TraceActive     = FALSE;
static ULONG64          g_TraceRangeBase  = 0;
static ULONG64          g_TraceRangeEnd   = 0;
static ULONG            g_TraceMaxSteps   = 0;
static volatile ULONG   g_TraceStepCount  = 0;

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
    KPROCESSOR_MODE lockMode = (address >= 0xFFFF800000000000ULL) ? KernelMode : UserMode;

    mdl = IoAllocateMdl((PVOID)(ULONG_PTR)address, 1, FALSE, FALSE, NULL);
    if (!mdl) return STATUS_INSUFFICIENT_RESOURCES;

    __try {
        MmProbeAndLockPages(mdl, lockMode, IoReadAccess);

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

NTSTATUS KfPatchBytes(PVOID dest, const void *src, SIZE_T size)
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

/* SystemModuleInformation structures (same as kmodules.c) */
#define KF_SystemModuleInformation 11

typedef struct _KF_PROCESS_MODULE_INFORMATION {
    HANDLE  Section;
    PVOID   MappedBase;
    PVOID   ImageBase;
    ULONG   ImageSize;
    ULONG   Flags;
    USHORT  LoadOrderIndex;
    USHORT  InitOrderIndex;
    USHORT  LoadCount;
    USHORT  OffsetToFileName;
    UCHAR   FullPathName[256];
} KF_PROCESS_MODULE_INFORMATION;

typedef struct _KF_PROCESS_MODULES {
    ULONG   NumberOfModules;
    KF_PROCESS_MODULE_INFORMATION Modules[1];
} KF_PROCESS_MODULES;

static PVOID KfFindNtoskrnlBase(void)
{
    NTSTATUS status;
    PVOID buffer;
    ULONG bufferSize = 0x10000;
    ULONG returnLength = 0;
    PVOID base = NULL;

    buffer = ExAllocatePoolWithTag(NonPagedPool, bufferSize, 'bNkK');
    if (!buffer) return NULL;

    status = ZwQuerySystemInformation(KF_SystemModuleInformation,
                                      buffer, bufferSize, &returnLength);
    if (status == STATUS_INFO_LENGTH_MISMATCH) {
        ExFreePoolWithTag(buffer, 'bNkK');
        bufferSize = returnLength + 0x1000;
        buffer = ExAllocatePoolWithTag(NonPagedPool, bufferSize, 'bNkK');
        if (!buffer) return NULL;
        status = ZwQuerySystemInformation(KF_SystemModuleInformation,
                                          buffer, bufferSize, &returnLength);
    }

    if (NT_SUCCESS(status)) {
        KF_PROCESS_MODULES *modules = (KF_PROCESS_MODULES *)buffer;
        if (modules->NumberOfModules > 0) {
            base = modules->Modules[0].ImageBase;
            DbgPrint("[KernelFlirt] ntoskrnl base: %p (%s)\n",
                     base, modules->Modules[0].FullPathName);
        }
    } else {
        DbgPrint("[KernelFlirt] ZwQuerySystemInformation failed: 0x%08X\n", status);
    }

    ExFreePoolWithTag(buffer, 'bNkK');
    return base;
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
    DbgPrint("[KernelFlirt] Total sections: %u\n", nt->FileHeader.NumberOfSections);
    for (i = 0; i < nt->FileHeader.NumberOfSections; i++) {
        if (!MmIsAddressValid(&sec[i])) break;
        DbgPrint("[KernelFlirt] Section[%u] %.8s VA=0x%X Size=0x%X Char=0x%08X\n",
                 i, sec[i].Name, sec[i].VirtualAddress,
                 sec[i].Misc.VirtualSize, sec[i].Characteristics);
        if (!(sec[i].Characteristics & IMAGE_SCN_MEM_EXECUTE)) continue;

        textBase = ntBase + sec[i].VirtualAddress;
        textSize = sec[i].Misc.VirtualSize;
        DbgPrint("[KernelFlirt] Scanning executable section %.8s: %p, size=0x%X\n",
                 sec[i].Name, textBase, textSize);

        if (textSize < 32) continue;

        for (off = 0; off < textSize - 32; off++) {
            PUCHAR p = textBase + off;

            if (!MmIsAddressValid(p)) {
                ULONG_PTR nextPage = ((ULONG_PTR)p + 0x1000) & ~(ULONG_PTR)0xFFF;
                ULONG skip = (ULONG)(nextPage - (ULONG_PTR)textBase);
                if (skip > off)
                    off = skip - 1;
                continue;
            }

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
                DbgPrint("[KernelFlirt] KdTrap found at %p (%.8s+0x%X)\n",
                         p, sec[i].Name, off);
                return p;
            }
        }
    }

    DbgPrint("[KernelFlirt] KdTrap pattern not found in any code section\n");
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

static PUCHAR KfFindKdpTrapCallSite(PUCHAR kdTrap, PUCHAR *outKdpTrap, ULONG selectValue)
{
    int i;
    int callIndex = 0;
    /* select=0 → first CALL (KdpStub at ~+0x1D)
       select=1 → second CALL (KdpTrap at ~+0x28) */
    int targetCall = (selectValue != 0) ? 1 : 0;

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

            DbgPrint("[KernelFlirt] CALL[%d] at KdTrap+0x%X -> %p\n", callIndex, i, target);

            if (callIndex == targetCall) {
                DbgPrint("[KernelFlirt] Using CALL[%d] (select=%u path)\n", callIndex, selectValue);
                if (outKdpTrap) *outKdpTrap = target;
                return kdTrap + i;
            }
            callIndex++;
        }
    }

    DbgPrint("[KernelFlirt] Target CALL not found in KdTrap (select=%u)\n", selectValue);
    return NULL;
}

/* ------------------------------------------------------------------ */
/* Find KiDebugRoutine: a function pointer in ntoskrnl that              */
/* KiDispatchException calls for user-mode debug exceptions.             */
/* It holds the address of KdpStub (no debugger) or KdpTrap (debugger). */
/*                                                                       */
/* Strategy 1: Scan ALL data sections for a QWORD == KdpStub address.    */
/* Strategy 2: Scan code sections for MOV reg,[rip+disp32] where the     */
/*   resolved pointer location holds KdpStub's address. This catches     */
/*   references even when KiDebugRoutine is in an unexpected section.     */
/* ------------------------------------------------------------------ */

static PKDEBUG_ROUTINE * KfFindKiDebugRoutineInData(
    PUCHAR ntBase, PIMAGE_NT_HEADERS64 nt, ULONG_PTR target)
{
    PIMAGE_SECTION_HEADER sec = IMAGE_FIRST_SECTION(nt);
    USHORT i;

    for (i = 0; i < nt->FileHeader.NumberOfSections; i++) {
        PUCHAR secBase;
        ULONG secSize, off;

        if (!MmIsAddressValid(&sec[i])) break;

        /* Skip EXECUTABLE sections — code uses rel32, not raw QWORDs */
        if (sec[i].Characteristics & IMAGE_SCN_MEM_EXECUTE) continue;

        secBase = ntBase + sec[i].VirtualAddress;
        secSize = sec[i].Misc.VirtualSize;
        if (secSize < 8) continue;

        DbgPrint("[KernelFlirt] Scanning section %.8s (%p, 0x%X, char=0x%08X) "
                 "for KiDebugRoutine\n",
                 sec[i].Name, secBase, secSize, sec[i].Characteristics);

        for (off = 0; off <= secSize - 8; off += 8) {
            PUCHAR p = secBase + off;
            if (!MmIsAddressValid(p)) {
                ULONG_PTR nextPage = ((ULONG_PTR)p + 0x1000) & ~(ULONG_PTR)0xFFF;
                off = (ULONG)(nextPage - (ULONG_PTR)secBase);
                if (off > 0) off -= 8;
                continue;
            }
            if (*(ULONG_PTR *)p == target) {
                DbgPrint("[KernelFlirt] Found KiDebugRoutine candidate at %p "
                         "(section %.8s+0x%X, value=%p)\n",
                         p, sec[i].Name, off, (PVOID)target);
                return (PKDEBUG_ROUTINE *)p;
            }
        }
    }
    return NULL;
}

static PKDEBUG_ROUTINE * KfFindKiDebugRoutineInCode(
    PUCHAR ntBase, PIMAGE_NT_HEADERS64 nt, ULONG_PTR target)
{
    /*
     * Scan code sections for pattern: 48 8B XX [disp32]
     * where XX = {05,0D,15,1D,25,2D,35,3D} (MOV reg, [rip+disp32])
     * and the resolved address ([rip+7+disp32]) holds a QWORD == target.
     *
     * Also look for: FF 15 [disp32] (CALL [rip+disp32])
     * where resolved address holds target.
     */
    PIMAGE_SECTION_HEADER sec = IMAGE_FIRST_SECTION(nt);
    USHORT i;

    for (i = 0; i < nt->FileHeader.NumberOfSections; i++) {
        PUCHAR secBase;
        ULONG secSize, off;

        if (!MmIsAddressValid(&sec[i])) break;
        if (!(sec[i].Characteristics & IMAGE_SCN_MEM_EXECUTE)) continue;

        secBase = ntBase + sec[i].VirtualAddress;
        secSize = sec[i].Misc.VirtualSize;
        if (secSize < 16) continue;

        DbgPrint("[KernelFlirt] Code-scanning section %.8s (%p, 0x%X) "
                 "for KiDebugRoutine refs\n",
                 sec[i].Name, secBase, secSize);

        for (off = 0; off < secSize - 8; off++) {
            PUCHAR p = secBase + off;
            INT32 disp;
            PULONG_PTR candidate;

            if (!MmIsAddressValid(p)) {
                ULONG_PTR nextPage = ((ULONG_PTR)p + 0x1000) & ~(ULONG_PTR)0xFFF;
                off = (ULONG)(nextPage - (ULONG_PTR)secBase);
                if (off > 0) off--;
                continue;
            }
            if (!MmIsAddressValid(p + 7)) continue;

            /* Pattern 1: 48 8B [05|0D|15|1D|25|2D|35|3D] [disp32]
             * = REX.W MOV reg, [rip+disp32]  (7 bytes total) */
            if (p[0] == 0x48 && p[1] == 0x8B &&
                (p[2] & 0xC7) == 0x05 /* ModRM: mod=00, r/m=101 (RIP-relative) */) {
                disp = *(INT32 *)(p + 3);
                candidate = (PULONG_PTR)(p + 7 + disp);
                if (MmIsAddressValid(candidate) && *candidate == target) {
                    DbgPrint("[KernelFlirt] Found KiDebugRoutine via MOV at code %p "
                             "-> var at %p (value=%p)\n",
                             p, candidate, (PVOID)target);
                    return (PKDEBUG_ROUTINE *)candidate;
                }
            }

            /* Pattern 2: FF 15 [disp32] = CALL [rip+disp32] (6 bytes) */
            if (p[0] == 0xFF && p[1] == 0x15) {
                disp = *(INT32 *)(p + 2);
                candidate = (PULONG_PTR)(p + 6 + disp);
                if (MmIsAddressValid(candidate) && *candidate == target) {
                    DbgPrint("[KernelFlirt] Found KiDebugRoutine via CALL [rip] at code %p "
                             "-> var at %p (value=%p)\n",
                             p, candidate, (PVOID)target);
                    return (PKDEBUG_ROUTINE *)candidate;
                }
            }
        }
    }
    return NULL;
}

static PKDEBUG_ROUTINE * KfFindKiDebugRoutine(PUCHAR ntBase, PVOID kdpStubAddr)
{
    PIMAGE_DOS_HEADER dos = (PIMAGE_DOS_HEADER)ntBase;
    PIMAGE_NT_HEADERS64 nt;
    ULONG_PTR target = (ULONG_PTR)kdpStubAddr;
    PKDEBUG_ROUTINE *result;

    if (!MmIsAddressValid(dos) || dos->e_magic != IMAGE_DOS_SIGNATURE)
        return NULL;
    nt = (PIMAGE_NT_HEADERS64)(ntBase + dos->e_lfanew);
    if (!MmIsAddressValid(nt) || nt->Signature != IMAGE_NT_SIGNATURE)
        return NULL;

    /* Strategy 1: scan data sections for raw QWORD == KdpStub */
    result = KfFindKiDebugRoutineInData(ntBase, nt, target);
    if (result) return result;

    /* Strategy 2: scan code sections for MOV/CALL [rip+disp32] refs */
    DbgPrint("[KernelFlirt] Data scan failed, trying code reference scan...\n");
    result = KfFindKiDebugRoutineInCode(ntBase, nt, target);
    if (result) return result;

    DbgPrint("[KernelFlirt] KiDebugRoutine not found by any method\n");
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
        ULONG64 dr6 = ContextRecord->Dr6;
        /*
         * DR6 bit 14 (BS) = single-step trap (TF was set).
         * DR6 bits 0-3 = hardware breakpoint match on DR0-DR3.
         * The CPU does NOT clear DR6 automatically, so stale bits
         * from previous HW BP events can persist. Check BS first
         * to correctly identify TF-triggered single steps.
         */
        if (dr6 & (1ULL << 14)) {
            /* TF-triggered single step — clear BS and stale HW bits */
            evt->Type = KF_DBG_SINGLE_STEP;
            ContextRecord->Dr6 = 0;
        } else if (dr6 & 0x0F) {
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
            /* Clear DR6 to prevent stale bits on next exception */
            ContextRecord->Dr6 = 0;
        } else {
            evt->Type = KF_DBG_SINGLE_STEP;
            ContextRecord->Dr6 = 0;
        }
    } else if (ExceptionRecord->ExceptionCode == STATUS_ACCESS_VIOLATION) {
        evt->Type = KF_DBG_ACCESS_VIOLATION;
    } else {
        evt->Type = KF_DBG_BREAKPOINT;
    }

    evt->ProcessId = (ULONG)(ULONG_PTR)PsGetCurrentProcessId();
    evt->ThreadId  = (ULONG)(ULONG_PTR)PsGetCurrentThreadId();
    evt->Address   = ContextRecord->Rip;
    evt->PreviousMode = (ULONG)PreviousMode;
    evt->ExceptionCode = ExceptionRecord->ExceptionCode;
    evt->FaultAddress  = (ExceptionRecord->ExceptionCode == STATUS_ACCESS_VIOLATION &&
                          ExceptionRecord->NumberParameters >= 2)
                         ? (ULONG64)ExceptionRecord->ExceptionInformation[1] : 0;
    evt->AccessType    = (ExceptionRecord->ExceptionCode == STATUS_ACCESS_VIOLATION &&
                          ExceptionRecord->NumberParameters >= 1)
                         ? (ULONG)ExceptionRecord->ExceptionInformation[0] : 0;

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
    PVOID             TrapFrame,
    PEXCEPTION_RECORD ExceptionRecord,
    PCONTEXT          ContextRecord,
    KPROCESSOR_MODE   PreviousMode)
{
    KIRQL   oldIrql;
    PIRP    irpToComplete = NULL;

    /*
     * Sync ContextRecord->Rip back to TrapFrame so that ReadRegisters
     * (which reads KTHREAD->TrapFrame directly) sees the adjusted RIP
     * while the thread is blocked in KeWaitForSingleObject below.
     */
    if (TrapFrame != NULL) {
        *(ULONG64 *)((UCHAR *)TrapFrame + 0x168) = ContextRecord->Rip;
        *(ULONG64 *)((UCHAR *)TrapFrame + 0x178) = ContextRecord->EFlags;
    }

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
        KeWaitForSingleObject(&g_ContinueEvent, Executive,
                              KernelMode, FALSE, NULL);
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
    ULONG64 excAddr = (ULONG64)ExceptionRecord->ExceptionAddress;
    /* Treat as target: matching PID, any PID if g_TargetPid==0, or kernel-space address */
    BOOLEAN isTarget = (g_TargetPid == 0 || currentPid == g_TargetPid
                        || excAddr >= 0xFFFF800000000000ULL);
    /* For AV: strict PID match only (kernel AVs happen all the time) */
    BOOLEAN isTargetAv = (g_TargetPid != 0 && currentPid == g_TargetPid
                          && excAddr < 0xFFFF800000000000ULL);

    UNREFERENCED_PARAMETER(ExceptionFrame);

    /*
     * NO DbgPrint here! DbgPrint calls KD transport which may detect
     * no debugger connected and reset KdDebuggerEnabled=FALSE,
     * preventing subsequent INT3 from reaching our handler.
     */
    InterlockedIncrement(&g_HookCallCount);
    if (isTarget) {
        InterlockedIncrement(&g_HookTargetCallCount);
        g_LastTargetAddr = excAddr;
        g_LastTargetCode = ExceptionRecord->ExceptionCode;
    } else {
        g_LastNonTargetPid = currentPid;
    }

    /* Only handle BP, SingleStep, GuardPage, and AV for target process (user-mode) */
    if (ExceptionRecord->ExceptionCode != STATUS_BREAKPOINT &&
        ExceptionRecord->ExceptionCode != STATUS_SINGLE_STEP &&
        ExceptionRecord->ExceptionCode != STATUS_GUARD_PAGE_VIOLATION &&
        !(ExceptionRecord->ExceptionCode == STATUS_ACCESS_VIOLATION && isTargetAv && !SecondChance)) {
        return FALSE;  /* Not handled — let normal exception dispatch continue */
    }

    if (SecondChance)
        return FALSE;

    /* ============================================================== */
    /*  ACCESS VIOLATION — report to UI, then pass to app's SEH.      */
    /*  User sees the AV in the debugger (can inspect state), but     */
    /*  when they press Run, we return FALSE so the app's SEH handler */
    /*  receives the exception. This is correct for protectors like   */
    /*  Themida (VM uses intentional AV with SEH) AND for real crashes */
    /*  (user sees the crash, app gets the unhandled exception).       */
    /* ============================================================== */
    if (ExceptionRecord->ExceptionCode == STATUS_ACCESS_VIOLATION && isTargetAv) {
        KfReportAndBlock(TrapFrame, ExceptionRecord, ContextRecord, PreviousMode);

        /* Apply pending RIP/RSP override */
        if (g_ContinueFlags & KF_CONT_SET_RIP) ContextRecord->Rip = g_ContinueNewRip;
        if (g_ContinueFlags & KF_CONT_SET_RSP) ContextRecord->Rsp = g_ContinueNewRsp;
        g_ContinueFlags = 0;

        if (g_ContinueMode == KF_CONTINUE_HANDLED) {
            /* Plugin handled the AV (e.g. PAGE_NOACCESS guard):
             * Set TF for single-step, return TRUE to suppress AV from reaching SEH */
            ContextRecord->EFlags |= 0x100UL;
            return TRUE;
        }
        /* Default: return FALSE to pass AV to app's SEH */
        return FALSE;
    }

    /* ============================================================== */
    /*  SINGLE STEP - check if this is a re-arm after step-past        */
    /* ============================================================== */
    if (ExceptionRecord->ExceptionCode == STATUS_SINGLE_STEP) {

        /* --- Target process: re-arm after step-past --- */
        if (isTarget && g_StepPastPending && currentTid != 0) {
            UCHAR dummyOrig;
            g_StepPastPending = FALSE;

            /* Only re-arm if BP still exists in table (user may have removed it) */
            if (KfFindSwBpOrigByte(g_StepPastAddr, currentPid, &dummyOrig)) {
                KfWriteByteInContext(g_StepPastAddr, 0xCC);
            }

            /* Clear TF so thread doesn't keep stepping */
            ContextRecord->EFlags &= ~0x100UL;

            if (g_StepPastAutoRun) {
                /* Run mode: silently continue, don't report */
                return TRUE;
            }

            /* StepIn mode: report SingleStep event to UI */
            InterlockedIncrement(&g_HookStepCount);
            KfReportAndBlock(TrapFrame, ExceptionRecord, ContextRecord, PreviousMode);

            /* Apply pending RIP/RSP override (for IAT tracing) */
            if (g_ContinueFlags & KF_CONT_SET_RIP) ContextRecord->Rip = g_ContinueNewRip;
            if (g_ContinueFlags & KF_CONT_SET_RSP) ContextRecord->Rsp = g_ContinueNewRsp;
            g_ContinueFlags = 0;

            /* After UI continues: check if another step was requested */
            if (g_ContinueMode == KF_CONTINUE_STEP_INTO) {
                ContextRecord->EFlags |= 0x100UL; /* Set TF */
            }
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

                    /* Clear TF */
                    ContextRecord->EFlags &= ~0x100UL;
                    return TRUE;
                }
            }
        }

        /* Not a step-past SingleStep, fall through to normal handling */
        if (!isTarget)
            return FALSE;

        /* Fast trace mode: keep stepping internally while RIP is in trace range */
        if (g_TraceActive) {
            InterlockedIncrement(&g_TraceStepCount);
            if (excAddr >= g_TraceRangeBase && excAddr < g_TraceRangeEnd &&
                g_TraceStepCount < g_TraceMaxSteps) {
                ContextRecord->EFlags |= 0x100UL; /* Set TF — step again */
                return TRUE;  /* Don't report to UI, keep stepping */
            }
            g_TraceActive = FALSE;
            /* Fall through to report final RIP to UI */
        }

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
                /* Not in our BP table. For kernel addresses this may be a
                   file-patched INT3 (e.g. DriverEntry debug break) — report
                   to UI so the debugger can handle it. For user-mode addresses
                   return FALSE so normal exception dispatch continues:
                   the app's SEH handler will receive STATUS_BREAKPOINT.
                   This is critical for protectors like Themida that use INT3
                   with SEH as anti-debug checks — they expect the SEH to fire. */
                InterlockedIncrement(&g_HookBpNotFoundCount);
                if (bpAddr >= 0xFFFF800000000000ULL) {
                    goto report_to_ui;
                }
                return FALSE;
            }

            InterlockedIncrement(&g_HookBpHitCount);

            /* Report to UI and block */
            KfReportAndBlock(TrapFrame, ExceptionRecord, ContextRecord, PreviousMode);

            /*
             * Back from UI continue. Check continue mode:
             *   STEP_PAST / STEP_INTO: restore byte, set TF, prepare re-arm
             *   RUN: just resume (for non-SW-BP events)
             *
             * Note: BP may have been removed by UI while we were blocked.
             * Check if it still exists before doing step-past.
             */
            if (g_ContinueMode == KF_CONTINUE_STEP_PAST ||
                g_ContinueMode == KF_CONTINUE_STEP_INTO) {
                UCHAR currentOrig;
                BOOLEAN bpStillExists = KfFindSwBpOrigByte(bpAddr, currentPid, &currentOrig);

                if (bpStillExists) {
                    NTSTATUS st;

                    /* Restore original byte so CPU can execute it */
                    st = KfWriteByteInContext(bpAddr, currentOrig);

                    /* Prepare for re-arm on next SingleStep */
                    g_StepPastPending = TRUE;
                    g_StepPastAddr    = bpAddr;
                    g_StepPastAutoRun = (g_ContinueMode == KF_CONTINUE_STEP_PAST);
                }

                /* Always set TF for single step (even if BP was removed) */
                ContextRecord->EFlags |= 0x100UL;
            }

            return TRUE;
        }

        /* --- Non-target process hit our INT3 (shared page) --- */
        if (KfFindAnySwBpOrigByte(bpAddr, &origByte)) {
            NTSTATUS st;
            int slot = -1, i;

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
    KfReportAndBlock(TrapFrame, ExceptionRecord, ContextRecord, PreviousMode);

    /* Apply pending RIP/RSP override (for IAT tracing) */
    if (g_ContinueFlags & KF_CONT_SET_RIP) {
        ContextRecord->Rip = g_ContinueNewRip;
    }
    if (g_ContinueFlags & KF_CONT_SET_RSP) {
        ContextRecord->Rsp = g_ContinueNewRsp;
    }
    g_ContinueFlags = 0;

    /* After UI continues: set TF if step was requested */
    if (g_ContinueMode == KF_CONTINUE_STEP_INTO) {
        ContextRecord->EFlags |= 0x100UL;
    }
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
    g_TraceActive     = FALSE;

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
    PUCHAR otherTarget = NULL;

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

    /* Step 4: Find BOTH CALL sites inside KdTrap.
     * KdTrap has two paths:
     *   select=0 → CALL[0] (KdpStub, ~+0x1D)
     *   select=1 → CALL[1] (KdpTrap, ~+0x28)
     * We MUST patch BOTH because KdpDebugRoutineSelect can change
     * after we set KdDebuggerEnabled=TRUE.
     */
    callSite = KfFindKdpTrapCallSite(g_KdTrap, &kdpTrap, g_OrigSelectValue);
    if (!callSite) {
        DbgPrint("[KernelFlirt] FAIL: primary call site not found\n");
        return STATUS_NOT_FOUND;
    }
    g_CallSite = callSite;
    g_KdpTrap  = kdpTrap;
    g_OrigCallDisp = *(INT32 *)(callSite + 1);
    g_OrigKdpTrap  = (PKDEBUG_ROUTINE)kdpTrap;

    /* Find the OTHER call site (the alternate select path) */
    {
        ULONG otherSelect = (g_OrigSelectValue != 0) ? 0 : 1;
        g_CallSite2 = KfFindKdpTrapCallSite(g_KdTrap, &otherTarget, otherSelect);
        if (g_CallSite2) {
            g_OrigCallDisp2 = *(INT32 *)(g_CallSite2 + 1);
            DbgPrint("[KernelFlirt] Found BOTH call sites: select=%u at %p -> %p, select=%u at %p -> %p\n",
                     g_OrigSelectValue, callSite, kdpTrap, otherSelect, g_CallSite2, otherTarget);
        } else {
            DbgPrint("[KernelFlirt] WARNING: only found one call site (select=%u path)\n",
                     g_OrigSelectValue);
        }
    }

    DbgPrint("[KernelFlirt] KdTrap=%p  KdpTrap=%p  Select@%p=%u\n",
             g_KdTrap, g_KdpTrap, g_pKdpDebugRoutineSelect, g_OrigSelectValue);

    /* Dump first 16 bytes of KdpTrap target */
    KfDumpBytes("KdpTrap[0..15]", kdpTrap, 16);

    /*
     * Step 5+6: INLINE HOOK on KdpStub/KdpTrap directly.
     *
     * KiDispatchException calls KiDebugRoutine (function pointer) for
     * user-mode exceptions. KiDebugRoutine points to KdpStub (select=0)
     * or KdpTrap (select=1). Patching the CALL inside KdTrap only
     * intercepts calls routed through KdTrap, NOT calls via KiDebugRoutine.
     *
     * By inline-hooking the target function itself, we intercept ALL calls
     * regardless of call path.
     */
    {
        UCHAR jmpStub[14];
        NTSTATUS st;

        DbgPrint("[KernelFlirt] Installing inline hook on %p (KdpStub/KdpTrap)\n", kdpTrap);

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

        /* Verify: first 2 bytes should be FF 25 */
        if (MmIsAddressValid(kdpTrap) && kdpTrap[0] == 0xFF && kdpTrap[1] == 0x25) {
            DbgPrint("[KernelFlirt] VERIFY inline hook: OK (FF 25 at %p)\n", kdpTrap);
        } else {
            DbgPrint("[KernelFlirt] VERIFY inline hook: MISMATCH at %p!\n", kdpTrap);
        }

        g_OrigKdpTrap = (PKDEBUG_ROUTINE)g_Trampoline;
        g_UsedInlineHook = TRUE;
    }

    /*
     * Step 5c: Find and patch KiDebugRoutine function pointer.
     *
     * KiDispatchException calls KiDebugRoutine (not KdTrap!) for
     * user-mode exceptions. On some builds it may skip the call when
     * KiDebugRoutine still points to KdpStub (optimization: KdpStub
     * always returns FALSE). By changing the pointer to our handler,
     * we ensure KiDispatchException actually calls us.
     */
    g_pKiDebugRoutine = KfFindKiDebugRoutine((PUCHAR)ntBase, (PVOID)kdpTrap);
    if (g_pKiDebugRoutine) {
        g_OrigKiDebugRoutine = *g_pKiDebugRoutine;
        DbgPrint("[KernelFlirt] KiDebugRoutine at %p, current value=%p (KdpStub=%p)\n",
                 g_pKiDebugRoutine, (PVOID)g_OrigKiDebugRoutine, kdpTrap);

        /* Patch KiDebugRoutine to point directly to our handler */
        {
            ULONG_PTR newVal = (ULONG_PTR)KfDebugHandler;
            NTSTATUS st2 = KfPatchBytes(g_pKiDebugRoutine, &newVal, sizeof(newVal));
            if (NT_SUCCESS(st2)) {
                DbgPrint("[KernelFlirt] KiDebugRoutine patched: %p -> %p\n",
                         (PVOID)g_OrigKiDebugRoutine, KfDebugHandler);
            } else {
                DbgPrint("[KernelFlirt] WARNING: KiDebugRoutine patch failed: 0x%08X\n", st2);
            }
        }
    } else {
        DbgPrint("[KernelFlirt] WARNING: KiDebugRoutine not found — "
                 "user-mode INT3 may not be caught\n");
    }

    /* Resolve KdDebuggerEnabled/NotPresent */
    {
        UNICODE_STRING symName;
        RtlInitUnicodeString(&symName, L"KdDebuggerEnabled");
        g_pKdDebuggerEnabled = (PBOOLEAN)MmGetSystemRoutineAddress(&symName);
        RtlInitUnicodeString(&symName, L"KdDebuggerNotPresent");
        g_pKdDebuggerNotPresent = (PBOOLEAN)MmGetSystemRoutineAddress(&symName);
        DbgPrint("[KernelFlirt] KdDebuggerEnabled at %p, KdDebuggerNotPresent at %p\n",
                 g_pKdDebuggerEnabled, g_pKdDebuggerNotPresent);
    }

    _mm_mfence();
    DbgPrint("[KernelFlirt] KdpDebugRoutineSelect = %u (hooking select=%u path)\n",
             *g_pKdpDebugRoutineSelect, g_OrigSelectValue);

    /*
     * Step 8: Set KdDebuggerEnabled=TRUE, KdDebuggerNotPresent=FALSE
     * Without these, KiDispatchException skips KdTrap entirely for
     * user-mode exceptions, so our hook is never called.
     * (g_pKdDebuggerEnabled/NotPresent already resolved in Step 5b)
     */
    {
        NTSTATUS stFlag;

        if (g_pKdDebuggerEnabled) {
            g_OrigKdDebuggerEnabled = *g_pKdDebuggerEnabled;
            if (!g_OrigKdDebuggerEnabled) {
                BOOLEAN val = TRUE;
                stFlag = KfPatchBytes(g_pKdDebuggerEnabled, &val, sizeof(val));
                if (!NT_SUCCESS(stFlag))
                    DbgPrint("[KernelFlirt] WARNING: KdDebuggerEnabled patch failed: 0x%08X\n", stFlag);
            }
            DbgPrint("[KernelFlirt] KdDebuggerEnabled: %u -> %u\n",
                     g_OrigKdDebuggerEnabled, *g_pKdDebuggerEnabled);
        } else {
            DbgPrint("[KernelFlirt] WARNING: KdDebuggerEnabled not found\n");
        }

        if (g_pKdDebuggerNotPresent) {
            g_OrigKdDebuggerNotPresent = *g_pKdDebuggerNotPresent;
            if (g_OrigKdDebuggerNotPresent) {
                BOOLEAN val = FALSE;
                stFlag = KfPatchBytes(g_pKdDebuggerNotPresent, &val, sizeof(val));
                if (!NT_SUCCESS(stFlag))
                    DbgPrint("[KernelFlirt] WARNING: KdDebuggerNotPresent patch failed: 0x%08X\n", stFlag);
            }
            DbgPrint("[KernelFlirt] KdDebuggerNotPresent: %u -> %u\n",
                     g_OrigKdDebuggerNotPresent, *g_pKdDebuggerNotPresent);
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
        DbgPrint("[KernelFlirt] VERIFY: KdpDebugRoutineSelect = %u (unchanged)\n", *g_pKdpDebugRoutineSelect);

    g_HookInstalled = TRUE;
    DbgPrint("[KernelFlirt] === InstallDebugHook COMPLETE ===\n");

    /*
     * Re-assert KdDebuggerEnabled=TRUE as the VERY LAST step.
     * DbgPrint calls above go through the KD transport which may detect
     * no real debugger connected and reset KdDebuggerEnabled=FALSE.
     * We must ensure the flag is TRUE when we return so that
     * KiDispatchException will route INT3 exceptions to our hook.
     */
    if (g_pKdDebuggerEnabled && *g_pKdDebuggerEnabled != TRUE) {
        BOOLEAN val = TRUE;
        KfPatchBytes(g_pKdDebuggerEnabled, &val, sizeof(BOOLEAN));
    }
    if (g_pKdDebuggerNotPresent && *g_pKdDebuggerNotPresent != FALSE) {
        BOOLEAN val = FALSE;
        KfPatchBytes(g_pKdDebuggerNotPresent, &val, sizeof(BOOLEAN));
    }

    return STATUS_SUCCESS;
}

void KfRemoveDebugHook(void)
{
    KIRQL oldIrql;

    if (!g_HookInstalled)
        return;

    /*
     * Restore KiDebugRoutine FIRST, then KdDebuggerEnabled,
     * so KiDispatchException stops calling our handler.
     */
    if (g_pKiDebugRoutine && g_OrigKiDebugRoutine) {
        ULONG_PTR origVal = (ULONG_PTR)g_OrigKiDebugRoutine;
        KfPatchBytes(g_pKiDebugRoutine, &origVal, sizeof(origVal));
        DbgPrint("[KernelFlirt] KiDebugRoutine restored to %p\n", (PVOID)g_OrigKiDebugRoutine);
        g_pKiDebugRoutine = NULL;
        g_OrigKiDebugRoutine = NULL;
    }

    if (g_pKdDebuggerEnabled) {
        KfPatchBytes(g_pKdDebuggerEnabled, &g_OrigKdDebuggerEnabled, sizeof(BOOLEAN));
        DbgPrint("[KernelFlirt] KdDebuggerEnabled restored to %u\n", g_OrigKdDebuggerEnabled);
    }
    if (g_pKdDebuggerNotPresent) {
        KfPatchBytes(g_pKdDebuggerNotPresent, &g_OrigKdDebuggerNotPresent, sizeof(BOOLEAN));
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
        /* Restore primary hook */
        KfPatchBytes(g_KdpTrap, g_OrigEntryBytes, 14);
        DbgPrint("[KernelFlirt] KdpTrap entry restored\n");

        if (g_Trampoline) {
            ExFreePoolWithTag(g_Trampoline, 'KfTr');
            g_Trampoline = NULL;
        }

    } else if (g_CallSite) {
        KfPatchBytes(g_CallSite + 1, &g_OrigCallDisp, 4);
        DbgPrint("[KernelFlirt] CALL displacement restored at %p\n", g_CallSite);

        if (g_CallSite2) {
            KfPatchBytes(g_CallSite2 + 1, &g_OrigCallDisp2, 4);
            DbgPrint("[KernelFlirt] CALL displacement 2 restored at %p\n", g_CallSite2);
        }
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

void KfDebugHookDeactivate(void)
{
    KIRQL oldIrql;

    /* Set PID to invalid — hook returns FALSE for everything */
    g_TargetPid = 0xFFFFFFFF;
    DbgPrint("[KernelFlirt] Hook deactivated (PID=0xFFFFFFFF)\n");

    /* Wake any blocked thread */
    KeAcquireSpinLock(&g_DbgLock, &oldIrql);
    if (g_ThreadBlocked)
        KeSetEvent(&g_ContinueEvent, 0, FALSE);
    KeReleaseSpinLock(&g_DbgLock, oldIrql);

    /* Cancel pending WAIT IRP */
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

void KfReassertDebugFlags(void)
{
    if (!g_HookInstalled)
        return;
    if (g_pKdDebuggerEnabled && *g_pKdDebuggerEnabled != TRUE) {
        BOOLEAN val = TRUE;
        KfPatchBytes(g_pKdDebuggerEnabled, &val, sizeof(BOOLEAN));
    }
    if (g_pKdDebuggerNotPresent && *g_pKdDebuggerNotPresent != FALSE) {
        BOOLEAN val = FALSE;
        KfPatchBytes(g_pKdDebuggerNotPresent, &val, sizeof(BOOLEAN));
    }
}

NTSTATUS KfGetHookStats(PIRP Irp, PIO_STACK_LOCATION IoStack)
{
    PKF_HOOK_STATS_OUT out;

    if (IoStack->Parameters.DeviceIoControl.OutputBufferLength < sizeof(KF_HOOK_STATS_OUT)) {
        Irp->IoStatus.Information = 0;
        Irp->IoStatus.Status = STATUS_BUFFER_TOO_SMALL;
        IoCompleteRequest(Irp, IO_NO_INCREMENT);
        return STATUS_BUFFER_TOO_SMALL;
    }

    out = (PKF_HOOK_STATS_OUT)Irp->AssociatedIrp.SystemBuffer;
    out->HookCallCount     = (ULONG)g_HookCallCount;
    out->BpHitCount        = (ULONG)g_HookBpHitCount;
    out->BpNotFoundCount   = (ULONG)g_HookBpNotFoundCount;
    out->StepCount         = (ULONG)g_HookStepCount;
    out->KdDebuggerEnabled    = g_pKdDebuggerEnabled ? *g_pKdDebuggerEnabled : 0xFF;
    out->KdDebuggerNotPresent = g_pKdDebuggerNotPresent ? *g_pKdDebuggerNotPresent : 0xFF;
    out->Reserved[0] = 0;
    out->Reserved[1] = 0;
    out->TargetCallCount  = (ULONG)g_HookTargetCallCount;
    out->LastTargetAddr   = g_LastTargetAddr;
    out->LastTargetCode   = g_LastTargetCode;
    out->LastNonTargetPid = g_LastNonTargetPid;
    out->KiDebugRoutineAddr = (ULONG64)g_pKiDebugRoutine;
    out->KiDebugRoutineOrig = (ULONG64)g_OrigKiDebugRoutine;
    out->KiDebugRoutineNow  = g_pKiDebugRoutine ? (ULONG64)*g_pKiDebugRoutine : 0;
    out->HookedFuncAddr = (ULONG64)g_KdpTrap;
    out->KdTrapAddr     = (ULONG64)g_KdTrap;

    Irp->IoStatus.Information = sizeof(KF_HOOK_STATS_OUT);
    Irp->IoStatus.Status = STATUS_SUCCESS;
    IoCompleteRequest(Irp, IO_NO_INCREMENT);
    return STATUS_SUCCESS;
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

    /*
     * Re-assert KdDebuggerEnabled=TRUE before waiting.
     * Between InstallDebugHook and this point, other IOCTLs (ReadMemory,
     * SetBreakpoint) may have used DbgPrint which resets the flag.
     * Without this, KiDispatchException skips KdTrap and INT3 goes
     * unhandled — especially visible with WoW64 processes whose
     * loader takes longer to reach the entry point.
     */
    if (g_pKdDebuggerEnabled && *g_pKdDebuggerEnabled != TRUE) {
        BOOLEAN val = TRUE;
        KfPatchBytes(g_pKdDebuggerEnabled, &val, sizeof(BOOLEAN));
    }
    if (g_pKdDebuggerNotPresent && *g_pKdDebuggerNotPresent != FALSE) {
        BOOLEAN val = FALSE;
        KfPatchBytes(g_pKdDebuggerNotPresent, &val, sizeof(BOOLEAN));
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

    /*
     * Re-assert KdDebuggerEnabled=TRUE before waking the thread.
     * DbgPrint (or other KD calls) may have reset it to FALSE,
     * which would prevent KiDispatchException from calling KdTrap
     * for the next INT3.
     */
    if (g_pKdDebuggerEnabled && *g_pKdDebuggerEnabled != TRUE) {
        BOOLEAN val = TRUE;
        KfPatchBytes(g_pKdDebuggerEnabled, &val, sizeof(BOOLEAN));
    }
    if (g_pKdDebuggerNotPresent && *g_pKdDebuggerNotPresent != FALSE) {
        BOOLEAN val = FALSE;
        KfPatchBytes(g_pKdDebuggerNotPresent, &val, sizeof(BOOLEAN));
    }

    /* Set mode and pending RIP/RSP BEFORE signaling (handler reads after wake) */
    if (mode == KF_CONTINUE_TRACE) {
        /* Fast trace mode: set up range and switch to STEP_INTO for first step */
        g_ContinueMode = KF_CONTINUE_STEP_INTO;
    } else {
        g_ContinueMode = mode;
    }

    if (IoStack->Parameters.DeviceIoControl.InputBufferLength >= sizeof(KF_CONTINUE_IN)) {
        PKF_CONTINUE_IN input = (PKF_CONTINUE_IN)Irp->AssociatedIrp.SystemBuffer;
        g_ContinueFlags  = input->Flags;
        g_ContinueNewRip = input->NewRip;
        g_ContinueNewRsp = input->NewRsp;

        if (mode == KF_CONTINUE_TRACE) {
            g_TraceRangeBase = input->TraceRangeBase;
            g_TraceRangeEnd  = input->TraceRangeEnd;
            g_TraceMaxSteps  = input->TraceMaxSteps;
            if (g_TraceMaxSteps == 0) g_TraceMaxSteps = 500000;
            g_TraceStepCount = 0;
            g_TraceActive    = TRUE;
        }
    } else {
        g_ContinueFlags = 0;
    }

    KeSetEvent(&g_ContinueEvent, 0, FALSE);

    Irp->IoStatus.Information = 0;
    Irp->IoStatus.Status = STATUS_SUCCESS;
    IoCompleteRequest(Irp, IO_NO_INCREMENT);
    return STATUS_SUCCESS;
}
