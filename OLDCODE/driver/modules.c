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
