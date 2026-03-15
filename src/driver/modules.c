/*
 * KernelFlirt - Module enumeration
 * modules.c - Walk PEB->Ldr to enumerate loaded modules
 *
 * Uses explicit-offset structs for PEB/LDR to avoid
 * layout mismatches across Windows builds (x64).
 */

#include <ntddk.h>
#include "ntundoc.h"
#include "../../include/kf_shared.h"

/*
 * PEB_LDR_DATA — x64 offsets:
 *   0x00  Length          (ULONG, 4)
 *   0x04  Initialized     (BOOLEAN, 1) + pad(3)
 *   0x08  SsHandle        (PVOID, 8)
 *   0x10  InLoadOrderModuleList (LIST_ENTRY, 16)
 */
typedef struct _PEB_LDR_DATA_KF {
    ULONG       Length;                     /* 0x00 */
    UCHAR       _pad0[4];                   /* 0x04 (Initialized + pad) */
    PVOID       SsHandle;                   /* 0x08 */
    LIST_ENTRY  InLoadOrderModuleList;      /* 0x10 */
} PEB_LDR_DATA_KF, *PPEB_LDR_DATA_KF;

/*
 * PEB (minimal) — x64 offsets:
 *   0x00  InheritedAddressSpace  (1)
 *   0x01  ReadImageFileExecOpts  (1)
 *   0x02  BeingDebugged          (1)
 *   0x03  BitField               (1)
 *   0x04  padding                (4)
 *   0x08  Mutant                 (PVOID, 8)
 *   0x10  ImageBaseAddress       (PVOID, 8)
 *   0x18  Ldr                    (PPEB_LDR_DATA, 8)
 */
typedef struct _PEB_KF {
    UCHAR               _pad0[0x18];    /* skip to Ldr */
    PPEB_LDR_DATA_KF    Ldr;            /* 0x18 */
} PEB_KF, *PPEB_KF;

/*
 * LDR_DATA_TABLE_ENTRY — x64 offsets:
 *   0x00  InLoadOrderLinks        (LIST_ENTRY, 16)
 *   0x10  InMemoryOrderLinks      (LIST_ENTRY, 16)
 *   0x20  InInitializationOrderLinks (LIST_ENTRY, 16)
 *   0x30  DllBase                 (PVOID, 8)
 *   0x38  EntryPoint              (PVOID, 8)
 *   0x40  SizeOfImage             (ULONG, 4) + pad(4)
 *   0x48  FullDllName             (UNICODE_STRING, 16)
 *   0x58  BaseDllName             (UNICODE_STRING, 16)
 */
typedef struct _LDR_ENTRY_KF {
    LIST_ENTRY      InLoadOrderLinks;           /* 0x00 */
    UCHAR           _pad0[16];                  /* 0x10 (InMemoryOrderLinks) */
    UCHAR           _pad1[16];                  /* 0x20 (InInitializationOrderLinks) */
    PVOID           DllBase;                    /* 0x30 */
    PVOID           EntryPoint;                 /* 0x38 */
    ULONG           SizeOfImage;                /* 0x40 */
    UCHAR           _pad2[4];                   /* 0x44 (alignment) */
    UNICODE_STRING  FullDllName;                /* 0x48 */
    UNICODE_STRING  BaseDllName;                /* 0x58 */
} LDR_ENTRY_KF, *PLDR_ENTRY_KF;

/*
 * WoW64 (32-bit) PEB/LDR structures:
 *
 * LIST_ENTRY32: { Flink: ULONG, Blink: ULONG }
 * UNICODE_STRING32: { Length: USHORT, MaximumLength: USHORT, Buffer: ULONG }
 *
 * PEB32 — offsets:
 *   0x0C  Ldr (ULONG ptr to PEB_LDR_DATA32)
 *
 * PEB_LDR_DATA32 — offsets:
 *   0x0C  InLoadOrderModuleList (LIST_ENTRY32)
 *
 * LDR_DATA_TABLE_ENTRY32 — offsets:
 *   0x00  InLoadOrderLinks     (LIST_ENTRY32, 8)
 *   0x08  InMemoryOrderLinks   (LIST_ENTRY32, 8)
 *   0x10  InInitOrderLinks     (LIST_ENTRY32, 8)
 *   0x18  DllBase              (ULONG, 4)
 *   0x1C  EntryPoint           (ULONG, 4)
 *   0x20  SizeOfImage          (ULONG, 4)
 *   0x24  FullDllName          (UNICODE_STRING32, 8)
 *   0x2C  BaseDllName          (UNICODE_STRING32, 8)
 */

#pragma pack(push, 4)
typedef struct _LIST_ENTRY32_KF {
    ULONG   Flink;
    ULONG   Blink;
} LIST_ENTRY32_KF;

typedef struct _UNICODE_STRING32_KF {
    USHORT  Length;
    USHORT  MaximumLength;
    ULONG   Buffer;         /* 32-bit pointer */
} UNICODE_STRING32_KF;

typedef struct _PEB_LDR_DATA32_KF {
    UCHAR           _pad0[0x0C];            /* skip to InLoadOrderModuleList */
    LIST_ENTRY32_KF InLoadOrderModuleList;  /* 0x0C */
} PEB_LDR_DATA32_KF, *PPEB_LDR_DATA32_KF;

typedef struct _PEB32_KF {
    UCHAR   _pad0[0x0C];   /* skip to Ldr */
    ULONG   Ldr;            /* 0x0C — 32-bit pointer to PEB_LDR_DATA32 */
} PEB32_KF, *PPEB32_KF;

typedef struct _LDR_ENTRY32_KF {
    LIST_ENTRY32_KF     InLoadOrderLinks;       /* 0x00 */
    LIST_ENTRY32_KF     InMemoryOrderLinks;     /* 0x08 */
    LIST_ENTRY32_KF     InInitOrderLinks;       /* 0x10 */
    ULONG               DllBase;                /* 0x18 */
    ULONG               EntryPoint;             /* 0x1C */
    ULONG               SizeOfImage;            /* 0x20 */
    UNICODE_STRING32_KF FullDllName;            /* 0x24 */
    UNICODE_STRING32_KF BaseDllName;            /* 0x2C */
} LDR_ENTRY32_KF;
#pragma pack(pop)

NTSTATUS
KfEnumModules(
    _In_ PIRP               Irp,
    _In_ PIO_STACK_LOCATION  IoStack
)
{
    PKF_ENUM_MODULES_IN input;
    PKF_MODULE_ENTRY    outputEntries;
    PEPROCESS           process = NULL;
    KAPC_STATE          apcState;
    NTSTATUS            status;
    ULONG               maxEntries;
    ULONG               count = 0;
    ULONG               targetPid;

    if (IoStack->Parameters.DeviceIoControl.InputBufferLength < sizeof(KF_ENUM_MODULES_IN)) {
        Irp->IoStatus.Information = 0;
        return STATUS_BUFFER_TOO_SMALL;
    }

    input = (PKF_ENUM_MODULES_IN)Irp->AssociatedIrp.SystemBuffer;
    targetPid = input->ProcessId;  /* save before output overwrites buffer */

    outputEntries = (PKF_MODULE_ENTRY)Irp->AssociatedIrp.SystemBuffer;
    maxEntries = IoStack->Parameters.DeviceIoControl.OutputBufferLength / sizeof(KF_MODULE_ENTRY);

    if (maxEntries == 0) {
        Irp->IoStatus.Information = 0;
        return STATUS_BUFFER_TOO_SMALL;
    }

    status = PsLookupProcessByProcessId((HANDLE)(ULONG_PTR)targetPid, &process);
    if (!NT_SUCCESS(status)) {
        DbgPrint("[KernelFlirt] PsLookupProcessByProcessId(%u) failed: 0x%08X\n", targetPid, status);
        Irp->IoStatus.Information = 0;
        return status;
    }

    KeStackAttachProcess(process, &apcState);

    __try {
        PPEB_KF peb = (PPEB_KF)PsGetProcessPeb(process);
        if (peb && peb->Ldr) {
            PLIST_ENTRY head = &peb->Ldr->InLoadOrderModuleList;
            PLIST_ENTRY entry = head->Flink;

            while (entry != head && count < maxEntries) {
                PLDR_ENTRY_KF ldrEntry =
                    CONTAINING_RECORD(entry, LDR_ENTRY_KF, InLoadOrderLinks);

                RtlZeroMemory(&outputEntries[count], sizeof(KF_MODULE_ENTRY));

                outputEntries[count].BaseAddress = (ULONG64)ldrEntry->DllBase;
                outputEntries[count].Size        = ldrEntry->SizeOfImage;

                if (ldrEntry->BaseDllName.Length > 0 && ldrEntry->BaseDllName.Buffer) {
                    USHORT copyLen = ldrEntry->BaseDllName.Length;
                    if (copyLen > (KF_MAX_MODULE_NAME - 1) * sizeof(WCHAR))
                        copyLen = (KF_MAX_MODULE_NAME - 1) * sizeof(WCHAR);
                    RtlCopyMemory(outputEntries[count].Name,
                                  ldrEntry->BaseDllName.Buffer,
                                  copyLen);
                }

                count++;
                entry = entry->Flink;
            }
        } else {
            DbgPrint("[KernelFlirt] PEB or Ldr is NULL for PID %u\n", targetPid);
        }

        /*
         * WoW64 PEB32: enumerate 32-bit modules loaded via WoW64 subsystem.
         * These include kernel32.dll, user32.dll, etc. that the 32-bit EXE uses.
         */
        {
            PVOID               peb32Raw;
            PPEB32_KF           peb32;
            PPEB_LDR_DATA32_KF  ldr32;
            ULONG               headAddr32;
            ULONG               curAddr32;

            peb32Raw = PsGetProcessWow64Process(process);
            if (peb32Raw) {
                peb32 = (PPEB32_KF)peb32Raw;
                if (peb32->Ldr) {
                    ldr32 = (PPEB_LDR_DATA32_KF)(ULONG_PTR)peb32->Ldr;
                    headAddr32 = (ULONG)((ULONG_PTR)&ldr32->InLoadOrderModuleList);
                    curAddr32  = ldr32->InLoadOrderModuleList.Flink;

                    while (curAddr32 != headAddr32 && count < maxEntries && curAddr32 != 0) {
                        LDR_ENTRY32_KF *ldr32Entry;
                        ULONG           ii;
                        BOOLEAN         duplicate;

                        ldr32Entry = (LDR_ENTRY32_KF *)(ULONG_PTR)curAddr32;
                        duplicate = FALSE;

                        for (ii = 0; ii < count; ii++) {
                            if (outputEntries[ii].BaseAddress == (ULONG64)ldr32Entry->DllBase) {
                                duplicate = TRUE;
                                break;
                            }
                        }

                        if (!duplicate) {
                            USHORT copyLen;
                            RtlZeroMemory(&outputEntries[count], sizeof(KF_MODULE_ENTRY));
                            outputEntries[count].BaseAddress = (ULONG64)ldr32Entry->DllBase;
                            outputEntries[count].Size        = ldr32Entry->SizeOfImage;

                            if (ldr32Entry->BaseDllName.Length > 0 && ldr32Entry->BaseDllName.Buffer) {
                                copyLen = ldr32Entry->BaseDllName.Length;
                                if (copyLen > (KF_MAX_MODULE_NAME - 1) * sizeof(WCHAR))
                                    copyLen = (KF_MAX_MODULE_NAME - 1) * sizeof(WCHAR);
                                RtlCopyMemory(outputEntries[count].Name,
                                              (PVOID)(ULONG_PTR)ldr32Entry->BaseDllName.Buffer,
                                              copyLen);
                            }
                            count++;
                        }

                        curAddr32 = ldr32Entry->InLoadOrderLinks.Flink;
                    }
                }
            }
        }

        status = STATUS_SUCCESS;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        DbgPrint("[KernelFlirt] Exception 0x%08X in KfEnumModules\n", GetExceptionCode());
        status = GetExceptionCode();
        count = 0;
    }

    KeUnstackDetachProcess(&apcState);
    ObDereferenceObject(process);

    Irp->IoStatus.Information = count * sizeof(KF_MODULE_ENTRY);
    return status;
}

/* ──────────────────────────────────────────────────────────────────────────
 * KfGetPebAddress - Return PEB address for a given process
 * ────────────────────────────────────────────────────────────────────────── */
NTSTATUS
KfGetPebAddress(
    _In_ PIRP               Irp,
    _In_ PIO_STACK_LOCATION  IoStack
)
{
    PKF_GET_PEB_IN  input;
    PKF_GET_PEB_OUT output;
    PEPROCESS       process = NULL;
    NTSTATUS        status;
    ULONG           targetPid;

    if (IoStack->Parameters.DeviceIoControl.InputBufferLength < sizeof(KF_GET_PEB_IN)) {
        Irp->IoStatus.Information = 0;
        return STATUS_BUFFER_TOO_SMALL;
    }

    if (IoStack->Parameters.DeviceIoControl.OutputBufferLength < sizeof(KF_GET_PEB_OUT)) {
        Irp->IoStatus.Information = 0;
        return STATUS_BUFFER_TOO_SMALL;
    }

    input = (PKF_GET_PEB_IN)Irp->AssociatedIrp.SystemBuffer;
    targetPid = input->ProcessId;

    output = (PKF_GET_PEB_OUT)Irp->AssociatedIrp.SystemBuffer;

    status = PsLookupProcessByProcessId((HANDLE)(ULONG_PTR)targetPid, &process);
    if (!NT_SUCCESS(status)) {
        Irp->IoStatus.Information = 0;
        return status;
    }

    output->PebAddress   = (ULONG64)PsGetProcessPeb(process);
    output->Peb32Address = (ULONG64)PsGetProcessWow64Process(process);

    ObDereferenceObject(process);

    Irp->IoStatus.Information = sizeof(KF_GET_PEB_OUT);
    return STATUS_SUCCESS;
}

/* ──────────────────────────────────────────────────────────────────────────
 * FindDebugPortOffset - Extract EPROCESS.DebugPort offset from
 * PsGetProcessDebugPort function bytes.
 * PsGetProcessDebugPort is: mov rax,[rcx+XX]; ret
 * ────────────────────────────────────────────────────────────────────────── */
static ULONG FindDebugPortOffset(void)
{
    UNICODE_STRING funcName = RTL_CONSTANT_STRING(L"PsGetProcessDebugPort");
    PUCHAR func = (PUCHAR)MmGetSystemRoutineAddress(&funcName);
    ULONG i;

    if (!func) return 0;

    for (i = 0; i < 32; i++) {
        /* mov rax, [rcx+disp32]: 48 8B 81 XX XX XX XX */
        if (func[i] == 0x48 && func[i+1] == 0x8B && func[i+2] == 0x81) {
            return *(ULONG*)(func + i + 3);
        }
        /* mov rax, [rcx+disp8]: 48 8B 41 XX */
        if (func[i] == 0x48 && func[i+1] == 0x8B && func[i+2] == 0x41) {
            return (ULONG)func[i + 3];
        }
    }
    return 0;
}

/* ──────────────────────────────────────────────────────────────────────────
 * FindCrossThreadFlagsOffset - Extract ETHREAD.CrossThreadFlags offset from
 * PsIsThreadTerminating: test byte ptr [rcx+XX], 1
 * ────────────────────────────────────────────────────────────────────────── */
static ULONG FindCrossThreadFlagsOffset(void)
{
    UNICODE_STRING funcName = RTL_CONSTANT_STRING(L"PsIsThreadTerminating");
    PUCHAR func = (PUCHAR)MmGetSystemRoutineAddress(&funcName);
    ULONG i;

    if (!func) {
        DbgPrint("[KernelFlirt] PsIsThreadTerminating not found\n");
        return 0;
    }

    DbgPrint("[KernelFlirt] PsIsThreadTerminating at %p: "
             "%02X %02X %02X %02X %02X %02X %02X %02X "
             "%02X %02X %02X %02X %02X %02X %02X %02X\n",
             func,
             func[0],func[1],func[2],func[3],func[4],func[5],func[6],func[7],
             func[8],func[9],func[10],func[11],func[12],func[13],func[14],func[15]);

    for (i = 0; i < 32; i++) {
        /* test byte ptr [rcx+disp32], imm8: F6 81 XX XX XX XX 01 */
        if (func[i] == 0xF6 && func[i+1] == 0x81 && func[i+6] == 0x01) {
            DbgPrint("[KernelFlirt] CrossThreadFlags = 0x%X (test byte disp32)\n", *(ULONG*)(func+i+2));
            return *(ULONG*)(func + i + 2);
        }
        /* test byte ptr [rcx+disp8], imm8: F6 41 XX 01 */
        if (func[i] == 0xF6 && func[i+1] == 0x41 && func[i+3] == 0x01) {
            DbgPrint("[KernelFlirt] CrossThreadFlags = 0x%X (test byte disp8)\n", (ULONG)func[i+2]);
            return (ULONG)func[i + 2];
        }
        /* test dword ptr [rcx+disp32], imm32: F7 81 XX XX XX XX 01 00 00 00 */
        if (func[i] == 0xF7 && func[i+1] == 0x81 && *(ULONG*)(func+i+6) == 1) {
            DbgPrint("[KernelFlirt] CrossThreadFlags = 0x%X (test dword disp32)\n", *(ULONG*)(func+i+2));
            return *(ULONG*)(func + i + 2);
        }
        /* test dword ptr [rcx+disp8], imm32: F7 41 XX 01 00 00 00 */
        if (func[i] == 0xF7 && func[i+1] == 0x41 && *(ULONG*)(func+i+3) == 1) {
            DbgPrint("[KernelFlirt] CrossThreadFlags = 0x%X (test dword disp8)\n", (ULONG)func[i+2]);
            return (ULONG)func[i + 2];
        }
        /* bt dword ptr [rcx+disp8], 0: 0F BA 61 XX 00 */
        if (func[i] == 0x0F && func[i+1] == 0xBA && func[i+2] == 0x61 && func[i+4] == 0x00) {
            DbgPrint("[KernelFlirt] CrossThreadFlags = 0x%X (bt disp8)\n", (ULONG)func[i+3]);
            return (ULONG)func[i + 3];
        }
        /* bt dword ptr [rcx+disp32], 0: 0F BA A1 XX XX XX XX 00 */
        if (func[i] == 0x0F && func[i+1] == 0xBA && func[i+2] == 0xA1 && func[i+7] == 0x00) {
            DbgPrint("[KernelFlirt] CrossThreadFlags = 0x%X (bt disp32)\n", *(ULONG*)(func+i+3));
            return *(ULONG*)(func + i + 3);
        }
        /* mov eax, [rcx+disp8] + and/test: 8B 41 XX ... */
        if (func[i] == 0x8B && func[i+1] == 0x41) {
            ULONG j = i + 3;
            if ((func[j] == 0x83 && func[j+1] == 0xE0 && func[j+2] == 0x01) ||
                (func[j] == 0xA8 && func[j+1] == 0x01) ||
                (func[j] == 0x24 && func[j+1] == 0x01)) {  /* and al, 1 */
                DbgPrint("[KernelFlirt] CrossThreadFlags = 0x%X (mov+and/test disp8)\n", (ULONG)func[i+2]);
                return (ULONG)func[i + 2];
            }
        }
        /* mov eax, [rcx+disp32] + and/test: 8B 81 XX XX XX XX ... */
        if (func[i] == 0x8B && func[i+1] == 0x81) {
            ULONG j = i + 6;
            if ((func[j] == 0x83 && func[j+1] == 0xE0 && func[j+2] == 0x01) ||
                (func[j] == 0xA8 && func[j+1] == 0x01) ||
                (func[j] == 0x24 && func[j+1] == 0x01)) {  /* and al, 1 */
                DbgPrint("[KernelFlirt] CrossThreadFlags = 0x%X (mov+and/test disp32)\n", *(ULONG*)(func+i+2));
                return *(ULONG*)(func + i + 2);
            }
        }
    }

    DbgPrint("[KernelFlirt] CrossThreadFlags: no pattern matched in PsIsThreadTerminating\n");
    return 0;
}

/* ──────────────────────────────────────────────────────────────────────────
 * KfClearDebugPort - Zero EPROCESS.DebugPort to hide from
 * NtQueryInformationProcess(DebugPort/DebugObjectHandle/DebugFlags),
 * CheckRemoteDebuggerPresent, and NtClose invalid handle exception.
 * ────────────────────────────────────────────────────────────────────────── */
NTSTATUS
KfClearDebugPort(
    _In_ PIRP               Irp,
    _In_ PIO_STACK_LOCATION  IoStack
)
{
    PKF_CLEAR_DEBUG_PORT_IN input;
    PEPROCESS               process = NULL;
    NTSTATUS                status;
    ULONG                   offset;

    if (IoStack->Parameters.DeviceIoControl.InputBufferLength < sizeof(KF_CLEAR_DEBUG_PORT_IN)) {
        Irp->IoStatus.Information = 0;
        return STATUS_BUFFER_TOO_SMALL;
    }

    input = (PKF_CLEAR_DEBUG_PORT_IN)Irp->AssociatedIrp.SystemBuffer;

    offset = FindDebugPortOffset();
    if (offset == 0) {
        DbgPrint("[KernelFlirt] ClearDebugPort: failed to find DebugPort offset\n");
        Irp->IoStatus.Information = 0;
        return STATUS_NOT_SUPPORTED;
    }

    status = PsLookupProcessByProcessId((HANDLE)(ULONG_PTR)input->ProcessId, &process);
    if (!NT_SUCCESS(status)) {
        Irp->IoStatus.Information = 0;
        return status;
    }

    /* Zero the DebugPort pointer */
    InterlockedExchangePointer((PVOID*)((PUCHAR)process + offset), NULL);
    DbgPrint("[KernelFlirt] ClearDebugPort: zeroed DebugPort at EPROCESS+0x%X for PID %u\n",
             offset, input->ProcessId);

    ObDereferenceObject(process);

    Irp->IoStatus.Information = 0;
    return STATUS_SUCCESS;
}

/* ──────────────────────────────────────────────────────────────────────────
 * KfClearThreadHide - Clear HideFromDebugger flag in CrossThreadFlags
 * for all threads of a process.
 *
 * Uses same SPI layout as threads.c (SPI_HDR + thread array at +0x100).
 * ────────────────────────────────────────────────────────────────────────── */

#define CLEAR_SPI  5  /* SystemProcessInformation */

/* Minimal process entry header — same layout as threads.c */
typedef struct _CTH_SPI_HDR {
    ULONG   NextEntryOffset;    /* 0x00 */
    ULONG   NumberOfThreads;    /* 0x04 */
    UCHAR   _pad1[72];          /* 0x08 -> 0x50 */
    HANDLE  UniqueProcessId;    /* 0x50 */
} CTH_SPI_HDR;

/* Minimal thread entry — only need ClientId.UniqueThread at 0x30 */
typedef struct _CTH_STI {
    UCHAR   _pad0[0x30];
    HANDLE  UniqueThread;       /* 0x30 */
    UCHAR   _pad1[0x18];       /* 0x38 -> 0x50 */
} CTH_STI;

C_ASSERT(sizeof(CTH_STI) == 0x50);

#define CTH_THREADS_OFFSET  0x100

NTSTATUS
KfClearThreadHide(
    _In_ PIRP               Irp,
    _In_ PIO_STACK_LOCATION  IoStack
)
{
    PKF_CLEAR_THREAD_HIDE_IN input;
    PETHREAD                 thread = NULL;
    NTSTATUS                 status;
    ULONG                    offset;
    ULONG                    cleared = 0;
    PVOID                    buffer = NULL;
    ULONG                    bufSize = 0x40000; /* 256KB */
    ULONG                    retLen = 0;

    if (IoStack->Parameters.DeviceIoControl.InputBufferLength < sizeof(KF_CLEAR_THREAD_HIDE_IN)) {
        Irp->IoStatus.Information = 0;
        return STATUS_BUFFER_TOO_SMALL;
    }

    input = (PKF_CLEAR_THREAD_HIDE_IN)Irp->AssociatedIrp.SystemBuffer;

    offset = FindCrossThreadFlagsOffset();
    if (offset == 0) {
        DbgPrint("[KernelFlirt] ClearThreadHide: failed to find CrossThreadFlags offset\n");
        Irp->IoStatus.Information = 0;
        return STATUS_NOT_SUPPORTED;
    }

    /* Enumerate threads via ZwQuerySystemInformation */
    buffer = ExAllocatePoolWithTag(NonPagedPool, bufSize, 'fKtH');
    if (!buffer) {
        Irp->IoStatus.Information = 0;
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    status = ZwQuerySystemInformation(CLEAR_SPI, buffer, bufSize, &retLen);
    if (status == STATUS_INFO_LENGTH_MISMATCH) {
        ExFreePoolWithTag(buffer, 'fKtH');
        bufSize = retLen + 0x10000;
        buffer = ExAllocatePoolWithTag(NonPagedPool, bufSize, 'fKtH');
        if (!buffer) {
            Irp->IoStatus.Information = 0;
            return STATUS_INSUFFICIENT_RESOURCES;
        }
        status = ZwQuerySystemInformation(CLEAR_SPI, buffer, bufSize, &retLen);
    }

    if (!NT_SUCCESS(status)) {
        ExFreePoolWithTag(buffer, 'fKtH');
        Irp->IoStatus.Information = 0;
        return status;
    }

    /* Walk process list, find target PID, iterate threads */
    __try {
        CTH_SPI_HDR *proc = (CTH_SPI_HDR *)buffer;
        for (;;) {
            if ((ULONG)(ULONG_PTR)proc->UniqueProcessId == input->ProcessId) {
                CTH_STI *threads = (CTH_STI *)((UCHAR *)proc + CTH_THREADS_OFFSET);
                ULONG i;
                for (i = 0; i < proc->NumberOfThreads; i++) {
                    status = PsLookupThreadByThreadId(threads[i].UniqueThread, &thread);
                    if (NT_SUCCESS(status)) {
                        PULONG flagsPtr = (PULONG)((PUCHAR)thread + offset);
                        if (*flagsPtr & 0x04) {
                            InterlockedAnd(flagsPtr, ~0x04);
                            cleared++;
                        }
                        ObDereferenceObject(thread);
                    }
                }
                break;
            }
            if (proc->NextEntryOffset == 0) break;
            proc = (CTH_SPI_HDR *)((UCHAR *)proc + proc->NextEntryOffset);
        }
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        DbgPrint("[KernelFlirt] Exception 0x%08X in KfClearThreadHide\n", GetExceptionCode());
    }

    ExFreePoolWithTag(buffer, 'fKtH');

    DbgPrint("[KernelFlirt] ClearThreadHide: cleared HideFromDebugger on %u threads for PID %u\n",
             cleared, input->ProcessId);

    Irp->IoStatus.Information = 0;
    return STATUS_SUCCESS;
}
