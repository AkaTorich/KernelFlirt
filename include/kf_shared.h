#ifndef KF_SHARED_H
#define KF_SHARED_H

/*
 * KernelFlirt - Shared IOCTL definitions
 * Used by both the kernel driver and usermode components.
 */

#define KF_DEVICE_NAME      L"\\Device\\KernelFlirt"
#define KF_SYMLINK_NAME     L"\\DosDevices\\KernelFlirt"
#define KF_USERMODE_PATH    "\\\\.\\KernelFlirt"
#define KF_SERVICE_NAME     "KernelFlirt"

/* Device type for CTL_CODE */
#define KF_DEVICE_TYPE      0x00008000  /* custom device type */

/* IOCTL codes - METHOD_BUFFERED, FILE_ANY_ACCESS */
#define IOCTL_KF_READ_MEMORY        CTL_CODE(KF_DEVICE_TYPE, 0x800, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_KF_WRITE_MEMORY       CTL_CODE(KF_DEVICE_TYPE, 0x801, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_KF_SET_BREAKPOINT     CTL_CODE(KF_DEVICE_TYPE, 0x802, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_KF_REMOVE_BREAKPOINT  CTL_CODE(KF_DEVICE_TYPE, 0x803, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_KF_SINGLE_STEP        CTL_CODE(KF_DEVICE_TYPE, 0x804, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_KF_READ_REGISTERS     CTL_CODE(KF_DEVICE_TYPE, 0x810, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_KF_WRITE_REGISTERS    CTL_CODE(KF_DEVICE_TYPE, 0x811, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_KF_ENUM_MODULES       CTL_CODE(KF_DEVICE_TYPE, 0x820, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_KF_ENUM_KERNEL_MODULES CTL_CODE(KF_DEVICE_TYPE, 0x821, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_KF_ENUM_THREADS       CTL_CODE(KF_DEVICE_TYPE, 0x830, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_KF_SUSPEND_THREAD     CTL_CODE(KF_DEVICE_TYPE, 0x831, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_KF_RESUME_THREAD      CTL_CODE(KF_DEVICE_TYPE, 0x832, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_KF_ENUM_PROCESSES     CTL_CODE(KF_DEVICE_TYPE, 0x835, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_KF_INSTALL_HOOK       CTL_CODE(KF_DEVICE_TYPE, 0x840, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_KF_REMOVE_HOOK        CTL_CODE(KF_DEVICE_TYPE, 0x841, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_KF_WAIT_DEBUG_EVENT   CTL_CODE(KF_DEVICE_TYPE, 0x842, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_KF_CONTINUE_DEBUG_EVENT CTL_CODE(KF_DEVICE_TYPE, 0x843, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_KF_GET_HOOK_STATS     CTL_CODE(KF_DEVICE_TYPE, 0x844, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_KF_SET_TARGET_PID     CTL_CODE(KF_DEVICE_TYPE, 0x845, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_KF_GET_PEB_ADDRESS    CTL_CODE(KF_DEVICE_TYPE, 0x836, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_KF_CLEAR_DEBUG_PORT   CTL_CODE(KF_DEVICE_TYPE, 0x837, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_KF_CLEAR_THREAD_HIDE  CTL_CODE(KF_DEVICE_TYPE, 0x838, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_KF_INSTALL_NTQSI_HOOK CTL_CODE(KF_DEVICE_TYPE, 0x850, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_KF_REMOVE_NTQSI_HOOK  CTL_CODE(KF_DEVICE_TYPE, 0x851, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_KF_PROBE_NTQSI        CTL_CODE(KF_DEVICE_TYPE, 0x852, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_KF_PROTECT_MEMORY     CTL_CODE(KF_DEVICE_TYPE, 0x805, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_KF_WRITE_RIP          CTL_CODE(KF_DEVICE_TYPE, 0x812, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_KF_RESET              CTL_CODE(KF_DEVICE_TYPE, 0x8FE, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_KF_PING               CTL_CODE(KF_DEVICE_TYPE, 0x8FF, METHOD_BUFFERED, FILE_ANY_ACCESS)

/* ---- Shared structures ---- */

#pragma pack(push, 1)

/* IOCTL_KF_READ_MEMORY input */
typedef struct _KF_READ_MEMORY_IN {
    ULONG   ProcessId;
    ULONG64 Address;
    ULONG   Size;
} KF_READ_MEMORY_IN, *PKF_READ_MEMORY_IN;

/* IOCTL_KF_WRITE_MEMORY input (data follows the struct) */
typedef struct _KF_WRITE_MEMORY_IN {
    ULONG   ProcessId;
    ULONG64 Address;
    ULONG   Size;
    /* UCHAR Data[Size] follows */
} KF_WRITE_MEMORY_IN, *PKF_WRITE_MEMORY_IN;

/* IOCTL_KF_SET_BREAKPOINT input */
#define KF_BP_SOFTWARE      0   /* INT3 software breakpoint */
#define KF_BP_HARDWARE      1   /* HW execute breakpoint (DR0-3) */
#define KF_BP_HW_WRITE      2   /* HW write watchpoint (DR0-3, condition=01) */
#define KF_BP_HW_READWRITE  3   /* HW read/write watchpoint (DR0-3, condition=11) */
#define KF_BP_MEMORY        4   /* Memory breakpoint (PAGE_GUARD) */

typedef struct _KF_SET_BP_IN {
    ULONG   ProcessId;
    ULONG   ThreadId;
    ULONG64 Address;
    ULONG   Type;       /* KF_BP_* */
    ULONG   Length;     /* 1, 2, 4, 8 for HW bp; page size for memory bp */
} KF_SET_BP_IN, *PKF_SET_BP_IN;

/* IOCTL_KF_SET_BREAKPOINT output */
typedef struct _KF_SET_BP_OUT {
    ULONG   Handle;
} KF_SET_BP_OUT, *PKF_SET_BP_OUT;

/* IOCTL_KF_REMOVE_BREAKPOINT input */
typedef struct _KF_REMOVE_BP_IN {
    ULONG   Handle;
} KF_REMOVE_BP_IN, *PKF_REMOVE_BP_IN;

/* IOCTL_KF_PROTECT_MEMORY input */
typedef struct _KF_PROTECT_MEMORY_IN {
    ULONG   ProcessId;
    ULONG64 Address;
    ULONG   Size;
    ULONG   NewProtection;  /* PAGE_NOACCESS, PAGE_EXECUTE_READ, etc. */
} KF_PROTECT_MEMORY_IN, *PKF_PROTECT_MEMORY_IN;

/* IOCTL_KF_PROTECT_MEMORY output */
typedef struct _KF_PROTECT_MEMORY_OUT {
    ULONG   OldProtection;
} KF_PROTECT_MEMORY_OUT, *PKF_PROTECT_MEMORY_OUT;

/* IOCTL_KF_SINGLE_STEP / READ_REGISTERS / WRITE_REGISTERS input */
typedef struct _KF_THREAD_TARGET {
    ULONG   ProcessId;
    ULONG   ThreadId;
} KF_THREAD_TARGET, *PKF_THREAD_TARGET;

/* IOCTL_KF_READ_REGISTERS output */
typedef struct _KF_REGISTERS {
    ULONG64 Rax, Rbx, Rcx, Rdx;
    ULONG64 Rsi, Rdi, Rbp, Rsp;
    ULONG64 R8, R9, R10, R11;
    ULONG64 R12, R13, R14, R15;
    ULONG64 Rip;
    ULONG64 Rflags;
    USHORT  Cs, Ds, Es, Fs, Gs, Ss;
    ULONG64 Dr0, Dr1, Dr2, Dr3, Dr6, Dr7;
} KF_REGISTERS, *PKF_REGISTERS;

/* IOCTL_KF_WRITE_REGISTERS input */
typedef struct _KF_WRITE_REGISTERS_IN {
    KF_THREAD_TARGET Target;
    KF_REGISTERS     Registers;
} KF_WRITE_REGISTERS_IN, *PKF_WRITE_REGISTERS_IN;

/* IOCTL_KF_WRITE_RIP input — modifies ONLY RIP (and optionally RSP) in trap frame.
 * Safe for IAT tracing — does NOT touch R12-R15, segments, or debug registers. */
typedef struct _KF_WRITE_RIP_IN {
    ULONG   ThreadId;
    ULONG   Flags;      /* bit 0: also write RSP */
    ULONG64 NewRip;
    ULONG64 NewRsp;     /* only written if Flags & 1 */
} KF_WRITE_RIP_IN, *PKF_WRITE_RIP_IN;

#define KF_WRIP_SET_RSP  0x01

/* IOCTL_KF_ENUM_MODULES input */
typedef struct _KF_ENUM_MODULES_IN {
    ULONG   ProcessId;
} KF_ENUM_MODULES_IN, *PKF_ENUM_MODULES_IN;

/* Module info entry */
#define KF_MAX_MODULE_NAME  256

typedef struct _KF_MODULE_ENTRY {
    ULONG64 BaseAddress;
    ULONG   Size;
    WCHAR   Name[KF_MAX_MODULE_NAME];
} KF_MODULE_ENTRY, *PKF_MODULE_ENTRY;

/* IOCTL_KF_ENUM_KERNEL_MODULES output - no input needed */
#define KF_MAX_KMOD_NAME 256

typedef struct _KF_KERNEL_MODULE_ENTRY {
    ULONG64 BaseAddress;
    ULONG   Size;
    USHORT  LoadOrderIndex;
    CHAR    Name[KF_MAX_KMOD_NAME];  /* ANSI, kernel module paths are ANSI */
} KF_KERNEL_MODULE_ENTRY, *PKF_KERNEL_MODULE_ENTRY;

/* IOCTL_KF_ENUM_THREADS input */
typedef struct _KF_ENUM_THREADS_IN {
    ULONG   ProcessId;
} KF_ENUM_THREADS_IN, *PKF_ENUM_THREADS_IN;

/* Thread info entry */
typedef struct _KF_THREAD_ENTRY {
    ULONG   ThreadId;
    ULONG64 StartAddress;
    ULONG   State;          /* running, waiting, etc. */
    ULONG   Priority;
} KF_THREAD_ENTRY, *PKF_THREAD_ENTRY;

/* IOCTL_KF_SUSPEND/RESUME_THREAD input */
typedef struct _KF_THREAD_OP_IN {
    ULONG   ThreadId;
} KF_THREAD_OP_IN, *PKF_THREAD_OP_IN;

/* IOCTL_KF_ENUM_PROCESSES output — no input needed */
#define KF_MAX_PROCESS_NAME 260

typedef struct _KF_PROCESS_ENTRY {
    ULONG   ProcessId;
    ULONG   SessionId;
    ULONG64 PeakVirtualSize;
    WCHAR   Name[KF_MAX_PROCESS_NAME];
} KF_PROCESS_ENTRY, *PKF_PROCESS_ENTRY;

/* IOCTL_KF_GET_PEB_ADDRESS input/output */
typedef struct _KF_GET_PEB_IN {
    ULONG   ProcessId;
} KF_GET_PEB_IN, *PKF_GET_PEB_IN;

typedef struct _KF_GET_PEB_OUT {
    ULONG64 PebAddress;
    ULONG64 Peb32Address;   /* WoW64 PEB, 0 if native x64 */
} KF_GET_PEB_OUT, *PKF_GET_PEB_OUT;

/* IOCTL_KF_CLEAR_DEBUG_PORT input */
typedef struct _KF_CLEAR_DEBUG_PORT_IN {
    ULONG   ProcessId;
} KF_CLEAR_DEBUG_PORT_IN, *PKF_CLEAR_DEBUG_PORT_IN;

/* IOCTL_KF_CLEAR_THREAD_HIDE input */
typedef struct _KF_CLEAR_THREAD_HIDE_IN {
    ULONG   ProcessId;      /* Clear HideFromDebugger on all threads of this process */
} KF_CLEAR_THREAD_HIDE_IN, *PKF_CLEAR_THREAD_HIDE_IN;

/* Debug event types */
#define KF_DBG_BREAKPOINT       1   /* INT3 software breakpoint */
#define KF_DBG_SINGLE_STEP      2   /* TF single step */
#define KF_DBG_HW_BREAKPOINT    3   /* DR0-3 hardware breakpoint (execute) */
#define KF_DBG_HW_WATCHPOINT    4   /* DR0-3 write/RW watchpoint */
#define KF_DBG_MEMORY_BP        5   /* PAGE_GUARD memory breakpoint */
#define KF_DBG_ACCESS_VIOLATION 6   /* STATUS_ACCESS_VIOLATION (anti-debug kill?) */

/* IOCTL_KF_CONTINUE_DEBUG_EVENT input (optional) */
#define KF_CONTINUE_RUN             0   /* Just resume (no SW BP hit, or HW BP) */
#define KF_CONTINUE_STEP_PAST       1   /* Step past SW BP, then auto-continue (F9/Run) */
#define KF_CONTINUE_STEP_INTO       2   /* Step past SW BP, then report SingleStep (F7) */
#define KF_CONTINUE_HANDLED         3   /* AV was handled by plugin: return TRUE + set TF (single-step) */
#define KF_CONTINUE_TRACE           4   /* Fast trace: step internally while RIP in [TraceRangeBase,TraceRangeEnd), report when exit */

typedef struct _KF_CONTINUE_IN {
    ULONG   Mode;       /* KF_CONTINUE_* */
    ULONG   Flags;      /* bit 0: apply NewRip, bit 1: apply NewRsp */
    ULONG64 NewRip;     /* set RIP in ContextRecord before resume (if Flags & 1) */
    ULONG64 NewRsp;     /* set RSP in ContextRecord before resume (if Flags & 2) */
    ULONG64 TraceRangeBase;  /* For KF_CONTINUE_TRACE: keep stepping while RIP >= Base */
    ULONG64 TraceRangeEnd;   /* For KF_CONTINUE_TRACE: keep stepping while RIP < End */
    ULONG   TraceMaxSteps;   /* For KF_CONTINUE_TRACE: max steps (0 = 500000 default) */
    ULONG   TraceReserved;   /* Alignment padding */
} KF_CONTINUE_IN, *PKF_CONTINUE_IN;

#define KF_CONT_SET_RIP  0x01
#define KF_CONT_SET_RSP  0x02

/* IOCTL_KF_WAIT_DEBUG_EVENT output */
typedef struct _KF_DEBUG_EVENT {
    ULONG           Type;           /* KF_DBG_* */
    ULONG           ProcessId;
    ULONG           ThreadId;
    ULONG64         Address;        /* Exception address (RIP) */
    ULONG           PreviousMode;   /* 0=KernelMode, 1=UserMode */
    ULONG           ExceptionCode;  /* NTSTATUS exception code (e.g. 0xC0000005 for AV) */
    ULONG64         FaultAddress;   /* For AV: the target address that was accessed */
    ULONG           AccessType;     /* For AV: 0=read, 1=write, 8=execute (ExceptionInformation[0]) */
    ULONG           Reserved0;      /* Alignment padding */
    KF_REGISTERS    Registers;      /* Full register context */
} KF_DEBUG_EVENT, *PKF_DEBUG_EVENT;

/* IOCTL_KF_GET_HOOK_STATS output */
typedef struct _KF_HOOK_STATS_OUT {
    ULONG   HookCallCount;       /* Total times KfDebugHandler was called */
    ULONG   BpHitCount;          /* BPs found in table and reported */
    ULONG   BpNotFoundCount;     /* BPs not in table (skipped) */
    ULONG   StepCount;           /* Single-step events reported */
    UCHAR   KdDebuggerEnabled;   /* Current value */
    UCHAR   KdDebuggerNotPresent;/* Current value */
    UCHAR   Reserved[2];
    ULONG   TargetCallCount;     /* Calls with isTarget=TRUE */
    ULONG64 LastTargetAddr;      /* Last exception addr from target */
    ULONG   LastTargetCode;      /* Last exception code from target */
    ULONG   LastNonTargetPid;    /* Last non-target PID */
    ULONG64 KiDebugRoutineAddr;  /* Address of KiDebugRoutine in ntoskrnl (0=not found) */
    ULONG64 KiDebugRoutineOrig;  /* Original value before redirect */
    ULONG64 KiDebugRoutineNow;   /* Current value */
    ULONG64 HookedFuncAddr;      /* Address of inline-hooked function (KdpStub) */
    ULONG64 KdTrapAddr;          /* Address of KdTrap */
} KF_HOOK_STATS_OUT, *PKF_HOOK_STATS_OUT;

/* IOCTL_KF_PING output */
typedef struct _KF_PING_OUT {
    ULONG   Version;
    ULONG   Magic;          /* 0x4B464C54 = 'KFLT' */
} KF_PING_OUT, *PKF_PING_OUT;

#define KF_VERSION  0x00010000  /* 1.0.0 */
#define KF_MAGIC    0x4B464C54  /* 'KFLT' */

/* ---- Relay pseudo-IOCTLs (handled by relay, NOT forwarded to driver) ---- */
/* These use function codes 0x900+ to avoid collision with driver IOCTLs */
#define IOCTL_KF_LIST_DRIVES        CTL_CODE(KF_DEVICE_TYPE, 0x900, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_KF_LIST_DIRECTORY     CTL_CODE(KF_DEVICE_TYPE, 0x901, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_KF_CREATE_PROCESS     CTL_CODE(KF_DEVICE_TYPE, 0x902, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_KF_LOAD_DRIVER        CTL_CODE(KF_DEVICE_TYPE, 0x903, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_KF_UNLOAD_DRIVER      CTL_CODE(KF_DEVICE_TYPE, 0x904, METHOD_BUFFERED, FILE_ANY_ACCESS)

/* IOCTL_KF_LIST_DRIVES output: array of KF_DRIVE_ENTRY */
#define KF_MAX_DRIVE_LABEL 64

typedef struct _KF_DRIVE_ENTRY {
    CHAR    Letter;         /* e.g. 'C' */
    CHAR    Padding[3];
    ULONG   DriveType;      /* DRIVE_FIXED, DRIVE_REMOTE, etc. */
    WCHAR   Label[KF_MAX_DRIVE_LABEL];
} KF_DRIVE_ENTRY, *PKF_DRIVE_ENTRY;

/* IOCTL_KF_LIST_DIRECTORY input: null-terminated wide path */
/* IOCTL_KF_LIST_DIRECTORY output: array of KF_DIR_ENTRY */
#define KF_MAX_FILENAME 260

typedef struct _KF_DIR_ENTRY {
    ULONG   IsDirectory;    /* 1 = directory, 0 = file */
    ULONG64 FileSize;
    WCHAR   Name[KF_MAX_FILENAME];
} KF_DIR_ENTRY, *PKF_DIR_ENTRY;

/* IOCTL_KF_CREATE_PROCESS input: null-terminated wide exe path */
/* IOCTL_KF_CREATE_PROCESS output: KF_CREATE_PROCESS_OUT */
typedef struct _KF_CREATE_PROCESS_OUT {
    ULONG   ProcessId;
    ULONG   ThreadId;
    ULONG64 ImageBase;      /* PEB.ImageBaseAddress — valid even while suspended */
} KF_CREATE_PROCESS_OUT, *PKF_CREATE_PROCESS_OUT;

/* IOCTL_KF_LOAD_DRIVER input: null-terminated wide .sys path on VM */
/* IOCTL_KF_LOAD_DRIVER output: KF_LOAD_DRIVER_OUT */
#define KF_MAX_SERVICE_NAME 64

typedef struct _KF_LOAD_DRIVER_OUT {
    CHAR    ServiceName[KF_MAX_SERVICE_NAME]; /* ANSI service name for unload */
    ULONG   EntryPointRva;                    /* AddressOfEntryPoint from PE header */
    UCHAR   OriginalByte;                     /* Original byte at entry point (patched to 0xCC) */
    UCHAR   Reserved[3];
} KF_LOAD_DRIVER_OUT, *PKF_LOAD_DRIVER_OUT;

/* IOCTL_KF_UNLOAD_DRIVER input: null-terminated ANSI service name */

/* IOCTL_KF_PROBE_NTQSI output — probe without hooking */
typedef struct _KF_PROBE_NTQSI_OUT {
    ULONG64 Address;            /* NtQuerySystemInformation address (0 if not found) */
    UCHAR   Bytes[32];          /* First 32 bytes at the address */
    ULONG   Status;             /* 0=ok, 1=not found, 2=decode error */
    ULONG   DecodedLen;         /* Total decoded instruction bytes (for 14+ hook) */
    ULONG   NumInsns;           /* Number of decoded instructions */
    UCHAR   HasRipRelative;     /* 1 if any instruction is RIP-relative */
    UCHAR   Reserved[3];
} KF_PROBE_NTQSI_OUT, *PKF_PROBE_NTQSI_OUT;

#pragma pack(pop)

#endif /* KF_SHARED_H */
