/*
 * KernelFlirt - NtQuerySystemInformation inline hook
 * ntqsi_hook.c - Hooks NtQuerySystemInformation to spoof class 0x23
 *                (SystemKernelDebuggerInformation)
 *
 * This allows hiding KdDebuggerEnabled/KdDebuggerNotPresent from usermode
 * while keeping them set for the KdTrap debug hook to function.
 */

#include <ntddk.h>
#include <ntimage.h>
#include "ntundoc.h"
#include "../../include/kf_shared.h"
#include "ntqsi_hook.h"

/* Import KfPatchBytes from debughook.c */
extern NTSTATUS KfPatchBytes(PVOID dest, const void *src, SIZE_T size);

/* ── Types ── */

typedef NTSTATUS (NTAPI *PFN_NtQuerySystemInformation)(
    ULONG  SystemInformationClass,
    PVOID  SystemInformation,
    ULONG  SystemInformationLength,
    PULONG ReturnLength
);

#define SYS_KERNEL_DEBUGGER_INFO  0x23

typedef struct _SYSTEM_KERNEL_DEBUGGER_INFORMATION {
    BOOLEAN DebuggerEnabled;
    BOOLEAN DebuggerNotPresent;
} SYSTEM_KERNEL_DEBUGGER_INFORMATION;

/* ── Globals ── */

static PUCHAR  g_NtQsiAddr        = NULL;    /* Address of NtQuerySystemInformation */
static PUCHAR  g_NtQsiTrampoline  = NULL;    /* Trampoline: fixed bytes + jmp back  */
static UCHAR   g_NtQsiOrigBytes[14];         /* Saved original 14 bytes            */
static BOOLEAN g_NtQsiHookActive  = FALSE;
static ULONG   g_NtQsiCopyLen     = 0;       /* Actual bytes copied to trampoline  */

static PFN_NtQuerySystemInformation g_OrigNtQsi = NULL;

/* ── Hook handler ── */

static NTSTATUS NTAPI KfNtQsiHandler(
    ULONG  SystemInformationClass,
    PVOID  SystemInformation,
    ULONG  SystemInformationLength,
    PULONG ReturnLength)
{
    NTSTATUS status;

    /* Call original via trampoline */
    status = g_OrigNtQsi(SystemInformationClass, SystemInformation,
                         SystemInformationLength, ReturnLength);

    /* Spoof class 0x23 result */
    if (NT_SUCCESS(status) &&
        SystemInformationClass == SYS_KERNEL_DEBUGGER_INFO &&
        SystemInformation != NULL &&
        SystemInformationLength >= sizeof(SYSTEM_KERNEL_DEBUGGER_INFORMATION))
    {
        __try {
            SYSTEM_KERNEL_DEBUGGER_INFORMATION *info =
                (SYSTEM_KERNEL_DEBUGGER_INFORMATION *)SystemInformation;
            info->DebuggerEnabled    = FALSE;
            info->DebuggerNotPresent = TRUE;
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            /* Buffer was usermode and faulted — ignore */
        }
    }

    return status;
}

/* ── Simple x64 instruction length decoder ──
 * Returns the length of the instruction at *p, or 0 if unknown.
 * Also sets *isRipRelative if the instruction uses RIP-relative addressing.
 * This is a minimal decoder for common x64 prologue instructions.
 */
static ULONG DecodeInsnLength(const UCHAR *p, BOOLEAN *isRipRelative)
{
    const UCHAR *start = p;
    UCHAR rex = 0;
    UCHAR opcode;
    BOOLEAN hasModRM = FALSE;
    BOOLEAN hasOpSize = FALSE;  /* 66h prefix */

    *isRipRelative = FALSE;

    /* Legacy prefixes (can appear in any order, multiple allowed) */
    for (;;) {
        if (*p == 0x66) { hasOpSize = TRUE; p++; }      /* operand-size override */
        else if (*p == 0x67) { p++; }                     /* address-size override */
        else if (*p == 0xF0 || *p == 0xF2 || *p == 0xF3) { p++; } /* LOCK/REPNE/REP */
        else if (*p == 0x2E || *p == 0x36 || *p == 0x3E ||
                 *p == 0x26 || *p == 0x64 || *p == 0x65) { p++; } /* segment overrides */
        else break;
    }

    /* REX prefix (0x40-0x4F) — must come after legacy prefixes */
    if ((*p & 0xF0) == 0x40) {
        rex = *p++;
    }

    opcode = *p++;

    /* Two-byte opcode (0F xx) */
    if (opcode == 0x0F) {
        opcode = *p++;
        /* Common 2-byte opcodes with ModRM */
        hasModRM = TRUE;
        /* 0F 1F = NOP with ModRM, 0F B6/B7/BE/BF = MOVZX/MOVSX */
        /* Most 0F opcodes have ModRM */
    }
    /* PUSH r64 (50-57) */
    else if (opcode >= 0x50 && opcode <= 0x5F) {
        return (ULONG)(p - start);
    }
    /* RET (C3) */
    else if (opcode == 0xC3) {
        return (ULONG)(p - start);
    }
    /* NOP (90) */
    else if (opcode == 0x90) {
        return (ULONG)(p - start);
    }
    /* MOV r/m, r  or  MOV r, r/m  (0x88-0x8B) */
    else if (opcode >= 0x88 && opcode <= 0x8B) {
        hasModRM = TRUE;
    }
    /* LEA (0x8D) */
    else if (opcode == 0x8D) {
        hasModRM = TRUE;
    }
    /* SUB/ADD/CMP r/m, imm8 (0x83) */
    else if (opcode == 0x83) {
        hasModRM = TRUE;
        /* + imm8 after ModRM+SIB+disp */
    }
    /* SUB/ADD/CMP r/m, imm32 (0x81) */
    else if (opcode == 0x81) {
        hasModRM = TRUE;
        /* + imm32 after ModRM+SIB+disp */
    }
    /* XOR/TEST etc r, r/m  (0x31, 0x33, 0x85, 0x29, 0x2B, 0x39, 0x3B, 0x09, 0x0B, 0x21, 0x23) */
    else if (opcode == 0x31 || opcode == 0x33 || opcode == 0x85 ||
             opcode == 0x29 || opcode == 0x2B || opcode == 0x39 ||
             opcode == 0x3B || opcode == 0x09 || opcode == 0x0B ||
             opcode == 0x21 || opcode == 0x23) {
        hasModRM = TRUE;
    }
    /* MOV r, imm (0xB8-0xBF) -- 32-bit or 64-bit immediate */
    else if (opcode >= 0xB8 && opcode <= 0xBF) {
        if (rex & 0x08) /* REX.W */
            return (ULONG)(p - start) + 8;
        return (ULONG)(p - start) + 4;
    }
    /* MOV r8, imm8 (0xB0-0xB7) */
    else if (opcode >= 0xB0 && opcode <= 0xB7) {
        return (ULONG)(p - start) + 1;
    }
    /* INT 3 (CC) */
    else if (opcode == 0xCC) {
        return (ULONG)(p - start);
    }
    else {
        /* Unknown opcode */
        return 0;
    }

    if (hasModRM) {
        UCHAR modrm = *p++;
        UCHAR mod = (modrm >> 6) & 3;
        UCHAR rm  = modrm & 7;

        /* RIP-relative: mod=00, rm=101 */
        if (mod == 0 && rm == 5) {
            *isRipRelative = TRUE;
            p += 4; /* disp32 */
        }
        else if (mod == 0 && rm == 4) {
            p++; /* SIB byte */
        }
        else if (mod == 1) {
            if (rm == 4) p++; /* SIB */
            p++; /* disp8 */
        }
        else if (mod == 2) {
            if (rm == 4) p++; /* SIB */
            p += 4; /* disp32 */
        }
        /* mod == 3: register only, no displacement */

        /* Check for immediate operand */
        if (opcode == 0x83) p += 1; /* imm8 */
        if (opcode == 0x81) p += 4; /* imm32 */
    }

    return (ULONG)(p - start);
}

/* Fixup RIP-relative displacements when copying instructions to trampoline */
static BOOLEAN FixupRipRelative(PUCHAR trampoline, PUCHAR origAddr, ULONG offset, ULONG insnLen)
{
    /* The instruction at origAddr+offset has a RIP-relative disp32.
     * Original: [RIP + disp32] where RIP = origAddr + offset + insnLen
     * Trampoline: [RIP + newDisp32] where RIP = trampoline + offset + insnLen
     *
     * Target = origAddr + offset + insnLen + disp32
     * newDisp32 = Target - (trampoline + offset + insnLen)
     */
    PUCHAR origInsn = origAddr + offset;
    PUCHAR trampInsn = trampoline + offset;
    LONG origDisp;
    LONG_PTR newDisp;
    ULONG dispOffset;

    /* Find the disp32 in the instruction - it's always the last 4 bytes
     * (unless there's an immediate after, but for RIP-relative MOV/LEA/CMP
     * in common prologues, disp32 is at the end or before imm) */

    /* For most instructions: disp32 is at insnLen-4 (no immediate)
     * For 0x83: disp32 is at insnLen-5 (before imm8)
     * For 0x81: disp32 is at insnLen-8 (before imm32) */
    UCHAR opByte = origInsn[0];
    if ((opByte & 0xF0) == 0x40) opByte = origInsn[1]; /* skip REX */
    if (opByte == 0x0F) opByte = origInsn[2]; /* skip 0F for 2-byte opcodes */

    if (opByte == 0x83) {
        dispOffset = insnLen - 5;
    } else if (opByte == 0x81) {
        dispOffset = insnLen - 8;
    } else {
        dispOffset = insnLen - 4;
    }

    origDisp = *(LONG *)(origInsn + dispOffset);
    newDisp = (LONG_PTR)(origAddr + offset + insnLen + origDisp) -
              (LONG_PTR)(trampoline + offset + insnLen);

    if (newDisp > 0x7FFFFFFF || newDisp < -0x7FFFFFFF) {
        DbgPrint("[KernelFlirt] NtQsiHook: RIP-relative fixup out of range: %lld\n", newDisp);
        return FALSE;
    }

    *(LONG *)(trampInsn + dispOffset) = (LONG)newDisp;
    return TRUE;
}

/* ── Find NtQuerySystemInformation ── */

static PUCHAR FindNtQuerySystemInformation(void)
{
    UNICODE_STRING name;
    PUCHAR addr;

    /* NtQuerySystemInformation IS exported by ntoskrnl on most Win10 builds */
    RtlInitUnicodeString(&name, L"NtQuerySystemInformation");
    addr = (PUCHAR)MmGetSystemRoutineAddress(&name);
    if (addr) {
        DbgPrint("[KernelFlirt] NtQsiHook: found at %p (MmGetSystemRoutineAddress)\n", addr);
        return addr;
    }

    DbgPrint("[KernelFlirt] NtQsiHook: MmGetSystemRoutineAddress failed, trying ZwQuerySystemInformation\n");

    /* Fallback: the Zw version is always exported.
     * On x64, Zw stubs are thin wrappers that go through KiServiceInternal.
     * We can't easily get the Nt function from the Zw stub without SSDT parsing.
     * For safety, refuse to hook if we can't find the Nt version directly. */
    RtlInitUnicodeString(&name, L"ZwQuerySystemInformation");
    addr = (PUCHAR)MmGetSystemRoutineAddress(&name);
    if (!addr) {
        DbgPrint("[KernelFlirt] NtQsiHook: ZwQuerySystemInformation also not found!\n");
        return NULL;
    }

    /* Try to find ntoskrnl base and walk PE exports */
    {
        PVOID buffer;
        ULONG bufSize = 0x20000;
        ULONG retLen = 0;
        NTSTATUS status;
        PUCHAR ntBase = NULL;

        /* Use ZwQuerySystemInformation(SystemModuleInformation) to find ntoskrnl */
        buffer = ExAllocatePoolWithTag(NonPagedPool, bufSize, 'QskK');
        if (!buffer) return NULL;

        status = ZwQuerySystemInformation(11 /* SystemModuleInformation */, buffer, bufSize, &retLen);
        if (status == STATUS_INFO_LENGTH_MISMATCH) {
            ExFreePoolWithTag(buffer, 'QskK');
            bufSize = retLen + 0x1000;
            buffer = ExAllocatePoolWithTag(NonPagedPool, bufSize, 'QskK');
            if (!buffer) return NULL;
            status = ZwQuerySystemInformation(11, buffer, bufSize, &retLen);
        }

        if (NT_SUCCESS(status)) {
            typedef struct {
                ULONG ModulesCount;
                struct {
                    PVOID Section;
                    PVOID MappedBase;
                    PVOID ImageBase;
                    ULONG ImageSize;
                    ULONG Flags;
                    USHORT LoadOrderIndex;
                    USHORT InitOrderIndex;
                    USHORT LoadCount;
                    USHORT OffsetToFileName;
                    CHAR FullPathName[256];
                } Modules[1];
            } RTL_PROCESS_MODULES;

            RTL_PROCESS_MODULES *modules = (RTL_PROCESS_MODULES *)buffer;
            if (modules->ModulesCount > 0) {
                ntBase = (PUCHAR)modules->Modules[0].ImageBase;
                DbgPrint("[KernelFlirt] NtQsiHook: ntoskrnl base at %p\n", ntBase);
            }
        }

        ExFreePoolWithTag(buffer, 'QskK');

        if (!ntBase) return NULL;

        /* Walk PE exports to find NtQuerySystemInformation */
        __try {
            PIMAGE_DOS_HEADER dos = (PIMAGE_DOS_HEADER)ntBase;
            PIMAGE_NT_HEADERS64 nt64;
            PIMAGE_EXPORT_DIRECTORY exports;
            PULONG nameRVAs, funcRVAs;
            PUSHORT ordinals;
            ULONG numNames, i;

            if (dos->e_magic != IMAGE_DOS_SIGNATURE) {
                DbgPrint("[KernelFlirt] NtQsiHook: bad MZ\n");
                return NULL;
            }

            nt64 = (PIMAGE_NT_HEADERS64)(ntBase + dos->e_lfanew);
            if (nt64->Signature != IMAGE_NT_SIGNATURE) {
                DbgPrint("[KernelFlirt] NtQsiHook: bad PE\n");
                return NULL;
            }

            if (nt64->OptionalHeader.NumberOfRvaAndSizes <= IMAGE_DIRECTORY_ENTRY_EXPORT)
                return NULL;

            exports = (PIMAGE_EXPORT_DIRECTORY)(ntBase +
                nt64->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXPORT].VirtualAddress);
            nameRVAs = (PULONG)(ntBase + exports->AddressOfNames);
            funcRVAs = (PULONG)(ntBase + exports->AddressOfFunctions);
            ordinals = (PUSHORT)(ntBase + exports->AddressOfNameOrdinals);
            numNames = exports->NumberOfNames;

            for (i = 0; i < numNames; i++) {
                const char *expName = (const char *)(ntBase + nameRVAs[i]);
                /* Compare "NtQuerySystemInformation" */
                static const char target[] = "NtQuerySystemInformation";
                ULONG j;
                BOOLEAN match = TRUE;
                for (j = 0; target[j]; j++) {
                    if (expName[j] != target[j]) { match = FALSE; break; }
                }
                if (match && expName[j] == '\0') {
                    USHORT ord = ordinals[i];
                    addr = ntBase + funcRVAs[ord];
                    DbgPrint("[KernelFlirt] NtQsiHook: found at %p (PE export)\n", addr);
                    return addr;
                }
            }
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            DbgPrint("[KernelFlirt] NtQsiHook: exception walking exports: 0x%08X\n",
                     GetExceptionCode());
        }
    }

    DbgPrint("[KernelFlirt] NtQsiHook: NtQuerySystemInformation not found\n");
    return NULL;
}

/* ── Install hook ── */

NTSTATUS KfInstallNtQsiHook(void)
{
    UCHAR   jmpStub[14];
    NTSTATUS status;
    ULONG   copyLen = 0;
    ULONG   offset = 0;
    BOOLEAN hasRipRelative[8]; /* Track which instructions are RIP-relative */
    ULONG   insnLens[8];
    ULONG   numInsns = 0;
    ULONG   i;

    if (g_NtQsiHookActive) {
        DbgPrint("[KernelFlirt] NtQsiHook: already installed\n");
        return STATUS_SUCCESS;
    }

    /* Old trampoline from previous install: intentionally leak it (~32 bytes).
     * A thread could still be executing inside it even after the 500ms drain.
     * The memory will be reclaimed when the driver is unloaded. */
    g_NtQsiTrampoline = NULL;

    g_NtQsiAddr = FindNtQuerySystemInformation();
    if (!g_NtQsiAddr) {
        return STATUS_NOT_FOUND;
    }

    /* Dump first 20 bytes for debugging */
    DbgPrint("[KernelFlirt] NtQsiHook: first 20 bytes at %p:\n", g_NtQsiAddr);
    DbgPrint("[KernelFlirt]  %02X %02X %02X %02X %02X %02X %02X %02X"
             " %02X %02X %02X %02X %02X %02X %02X %02X"
             " %02X %02X %02X %02X\n",
             g_NtQsiAddr[0],  g_NtQsiAddr[1],  g_NtQsiAddr[2],  g_NtQsiAddr[3],
             g_NtQsiAddr[4],  g_NtQsiAddr[5],  g_NtQsiAddr[6],  g_NtQsiAddr[7],
             g_NtQsiAddr[8],  g_NtQsiAddr[9],  g_NtQsiAddr[10], g_NtQsiAddr[11],
             g_NtQsiAddr[12], g_NtQsiAddr[13], g_NtQsiAddr[14], g_NtQsiAddr[15],
             g_NtQsiAddr[16], g_NtQsiAddr[17], g_NtQsiAddr[18], g_NtQsiAddr[19]);

    /* Decode instructions until we have >= 14 bytes */
    while (copyLen < 14 && numInsns < 8) {
        BOOLEAN ripRel = FALSE;
        ULONG len = DecodeInsnLength(g_NtQsiAddr + copyLen, &ripRel);
        if (len == 0) {
            DbgPrint("[KernelFlirt] NtQsiHook: unknown instruction at offset %u (byte 0x%02X)\n",
                     copyLen, g_NtQsiAddr[copyLen]);
            return STATUS_NOT_SUPPORTED;
        }
        hasRipRelative[numInsns] = ripRel;
        insnLens[numInsns] = len;
        if (ripRel) {
            DbgPrint("[KernelFlirt] NtQsiHook: insn #%u at +%u len=%u is RIP-relative\n",
                     numInsns, copyLen, len);
        }
        copyLen += len;
        numInsns++;
    }

    DbgPrint("[KernelFlirt] NtQsiHook: will copy %u bytes (%u instructions)\n", copyLen, numInsns);
    g_NtQsiCopyLen = copyLen;

    /* Allocate trampoline: copied bytes + 14-byte JMP back */
    g_NtQsiTrampoline = (PUCHAR)ExAllocatePoolWithTag(
        NonPagedPool, copyLen + 14, 'KfQs');
    if (!g_NtQsiTrampoline) {
        DbgPrint("[KernelFlirt] NtQsiHook: trampoline alloc failed\n");
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    /* Save original bytes */
    RtlCopyMemory(g_NtQsiOrigBytes, g_NtQsiAddr, copyLen);

    /* Build trampoline: [copied instructions] [FF 25 00000000] [addr of NtQsi+copyLen] */
    RtlCopyMemory(g_NtQsiTrampoline, g_NtQsiAddr, copyLen);

    /* Fixup RIP-relative instructions in trampoline */
    offset = 0;
    for (i = 0; i < numInsns; i++) {
        if (hasRipRelative[i]) {
            if (!FixupRipRelative(g_NtQsiTrampoline, g_NtQsiAddr, offset, insnLens[i])) {
                DbgPrint("[KernelFlirt] NtQsiHook: RIP fixup failed at insn #%u\n", i);
                ExFreePoolWithTag(g_NtQsiTrampoline, 'KfQs');
                g_NtQsiTrampoline = NULL;
                return STATUS_NOT_SUPPORTED;
            }
        }
        offset += insnLens[i];
    }

    /* JMP back to NtQsi + copyLen */
    g_NtQsiTrampoline[copyLen]     = 0xFF;
    g_NtQsiTrampoline[copyLen + 1] = 0x25;
    *(ULONG  *)(g_NtQsiTrampoline + copyLen + 2) = 0;
    *(ULONG_PTR *)(g_NtQsiTrampoline + copyLen + 6) = (ULONG_PTR)(g_NtQsiAddr + copyLen);

    g_OrigNtQsi = (PFN_NtQuerySystemInformation)g_NtQsiTrampoline;

    /* Build 14-byte detour: FF 25 00000000 [addr of handler] */
    jmpStub[0] = 0xFF;
    jmpStub[1] = 0x25;
    *(ULONG *)(jmpStub + 2) = 0;
    *(ULONG_PTR *)(jmpStub + 6) = (ULONG_PTR)KfNtQsiHandler;

    /* Patch NtQuerySystemInformation entry point */
    status = KfPatchBytes(g_NtQsiAddr, jmpStub, 14);
    if (!NT_SUCCESS(status)) {
        DbgPrint("[KernelFlirt] NtQsiHook: patch failed 0x%08X\n", status);
        ExFreePoolWithTag(g_NtQsiTrampoline, 'KfQs');
        g_NtQsiTrampoline = NULL;
        g_OrigNtQsi = NULL;
        return status;
    }

    /* If we copied more than 14 bytes, NOP out the remaining original bytes */
    if (copyLen > 14) {
        UCHAR nops[16];
        RtlFillMemory(nops, sizeof(nops), 0x90);
        KfPatchBytes(g_NtQsiAddr + 14, nops, copyLen - 14);
    }

    /* Verify */
    if (MmIsAddressValid(g_NtQsiAddr) &&
        g_NtQsiAddr[0] == 0xFF && g_NtQsiAddr[1] == 0x25) {
        DbgPrint("[KernelFlirt] NtQsiHook: installed OK at %p\n", g_NtQsiAddr);
    } else {
        DbgPrint("[KernelFlirt] NtQsiHook: verify MISMATCH at %p\n", g_NtQsiAddr);
    }

    g_NtQsiHookActive = TRUE;
    return STATUS_SUCCESS;
}

/* ── Remove hook ── */

void KfRemoveNtQsiHook(void)
{
    LARGE_INTEGER delay;

    if (!g_NtQsiHookActive || !g_NtQsiAddr) return;

    /* Mark inactive FIRST — handler will see this and skip spoofing */
    g_NtQsiHookActive = FALSE;
    MemoryBarrier();

    /* Restore original bytes */
    KfPatchBytes(g_NtQsiAddr, g_NtQsiOrigBytes, g_NtQsiCopyLen);

    /* Wait for in-flight calls to drain through the trampoline.
     * 500ms is generous — NtQuerySystemInformation completes in <1ms. */
    delay.QuadPart = -5000000;  /* 500ms */
    KeDelayExecutionThread(KernelMode, FALSE, &delay);

    /* Do NOT free the trampoline here — a thread could still be executing
     * inside it.  The trampoline is only 32 bytes, so leaking it is fine.
     * It will be freed on next install or driver unload via KfNtQsiCleanup(). */
    g_OrigNtQsi = NULL;
    g_NtQsiAddr = NULL;
    g_NtQsiCopyLen = 0;

    DbgPrint("[KernelFlirt] NtQsiHook: removed (trampoline kept)\n");
}

/* Call from DriverUnload — restore original bytes, leak trampoline */
void KfNtQsiCleanup(void)
{
    KfRemoveNtQsiHook();
    /* Trampoline is never freed — 32 bytes leaked until reboot. Safe. */
}

BOOLEAN KfIsNtQsiHookActive(void)
{
    return g_NtQsiHookActive;
}

/* ── Probe: find address and dump bytes without hooking ── */

NTSTATUS KfProbeNtQsi(PIRP Irp, PIO_STACK_LOCATION IoStack)
{
    PKF_PROBE_NTQSI_OUT result;
    PUCHAR addr;
    ULONG copyLen = 0;
    ULONG numInsns = 0;
    BOOLEAN anyRipRel = FALSE;

    if (IoStack->Parameters.DeviceIoControl.OutputBufferLength < sizeof(KF_PROBE_NTQSI_OUT)) {
        Irp->IoStatus.Information = 0;
        return STATUS_BUFFER_TOO_SMALL;
    }

    result = (PKF_PROBE_NTQSI_OUT)Irp->AssociatedIrp.SystemBuffer;
    RtlZeroMemory(result, sizeof(*result));

    addr = FindNtQuerySystemInformation();
    if (!addr) {
        result->Status = 1; /* not found */
        Irp->IoStatus.Information = sizeof(*result);
        return STATUS_SUCCESS;
    }

    result->Address = (ULONG64)addr;

    /* Copy first 32 bytes */
    __try {
        RtlCopyMemory(result->Bytes, addr, 32);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        result->Status = 1;
        Irp->IoStatus.Information = sizeof(*result);
        return STATUS_SUCCESS;
    }

    /* Decode instructions */
    while (copyLen < 14 && numInsns < 8) {
        BOOLEAN ripRel = FALSE;
        ULONG len = DecodeInsnLength(addr + copyLen, &ripRel);
        if (len == 0) {
            result->Status = 2; /* decode error */
            result->DecodedLen = copyLen;
            result->NumInsns = numInsns;
            Irp->IoStatus.Information = sizeof(*result);
            return STATUS_SUCCESS;
        }
        if (ripRel) anyRipRel = TRUE;
        copyLen += len;
        numInsns++;
    }

    result->Status = 0;
    result->DecodedLen = copyLen;
    result->NumInsns = numInsns;
    result->HasRipRelative = anyRipRel ? 1 : 0;

    Irp->IoStatus.Information = sizeof(*result);
    return STATUS_SUCCESS;
}
