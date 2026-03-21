/*
 * AntiDebug Test — No-Import Edition
 * Zero #include, zero IAT entries. All functions resolved via PEB hash walk.
 * Build (x64, MSVC):
 *   cl /O2 /GS- /Zi antidebug_noimport.c /link /ENTRY:entry /NODEFAULTLIB /SUBSYSTEM:CONSOLE /DEBUG
 */

/* ═══════════════════════════════════════════════════════════════════════
 * PRIMITIVE TYPES (no headers)
 * ═══════════════════════════════════════════════════════════════════════ */
typedef unsigned char       BYTE;
typedef unsigned short      WORD;
typedef unsigned int        DWORD;
typedef unsigned long long  QWORD;
typedef long long           INT64;
typedef int                 BOOL;
typedef long                LONG;
typedef long                NTSTATUS;
typedef void               *HANDLE;
typedef void               *PVOID;
typedef DWORD              *PDWORD;
typedef QWORD               ULONG_PTR;
typedef unsigned short      WCHAR;

#define NULL    ((void*)0)
#define TRUE    1
#define FALSE   0
#define NT_SUCCESS(s)   ((s) >= 0)
#define INVALID_HANDLE  ((HANDLE)(QWORD)0xDEADBEEF)

/* Console */
#define STD_OUTPUT_HANDLE   ((DWORD)-11)

/* Console colors */
#define FG_RED      0x0004 | 0x0008   /* FOREGROUND_RED | FOREGROUND_INTENSITY */
#define FG_GREEN    0x0002 | 0x0008   /* FOREGROUND_GREEN | FOREGROUND_INTENSITY */
#define FG_YELLOW   0x0002 | 0x0004 | 0x0008
#define FG_WHITE    0x0001 | 0x0002 | 0x0004 | 0x0008
#define FG_CYAN     0x0001 | 0x0002 | 0x0008
#define FG_DEFAULT  0x0007  /* grey */

typedef struct {
    short X, Y;
} COORD;
typedef struct {
    short Left, Top, Right, Bottom;
} SMALL_RECT;
typedef struct {
    COORD      dwSize;
    COORD      dwCursorPosition;
    WORD       wAttributes;
    SMALL_RECT srWindow;
    COORD      dwMaximumWindowSize;
} CONSOLE_SCREEN_BUFFER_INFO;

/* CONTEXT flags */
#define CONTEXT_AMD64               0x00100000
#define CONTEXT_CONTROL_F           (CONTEXT_AMD64 | 0x01)
#define CONTEXT_DEBUG_REGISTERS_F   (CONTEXT_AMD64 | 0x10)

/* Minimal CONTEXT — only the fields we care about */
#pragma pack(push, 16)
typedef struct _CONTEXT {
    QWORD _pad0[6];        /* P1-P6 Home */
    DWORD ContextFlags;     /* +0x30 */
    DWORD MxCsr;            /* +0x34 */
    /* Segment + flags */
    WORD SegCs, SegDs, SegEs, SegFs, SegGs, SegSs;  /* +0x38 */
    DWORD EFlags;           /* +0x44 */
    /* Debug registers */
    QWORD Dr0;              /* +0x48 */
    QWORD Dr1;              /* +0x50 */
    QWORD Dr2;              /* +0x58 */
    QWORD Dr3;              /* +0x60 */
    QWORD Dr6;              /* +0x68 */
    QWORD Dr7;              /* +0x70 */
    /* We don't need the rest but must reserve space for GetThreadContext */
    BYTE _tail[1232 - 0x78];
} CONTEXT;
#pragma pack(pop)

typedef struct { INT64 QuadPart; } LARGE_INTEGER;

/* ═══════════════════════════════════════════════════════════════════════
 * COMPILER INTRINSICS (no CRT needed)
 * ═══════════════════════════════════════════════════════════════════════ */
unsigned __int64 __readgsqword(unsigned long);
unsigned __int64 __rdtsc(void);
#pragma intrinsic(__readgsqword)
#pragma intrinsic(__rdtsc)

/* ═══════════════════════════════════════════════════════════════════════
 * HASH FUNCTION — djb2
 * ═══════════════════════════════════════════════════════════════════════ */
static DWORD djb2_a(const char *s)
{
    DWORD h = 5381;
    while (*s)
        h = ((h << 5) + h) + (BYTE)*s++;
    return h;
}

static DWORD djb2_w_lower(const WCHAR *s, int len)
{
    DWORD h = 5381;
    for (int i = 0; i < len; i++) {
        BYTE c = (BYTE)s[i];
        if (c >= 'A' && c <= 'Z') c += 32;
        h = ((h << 5) + h) + c;
    }
    return h;
}

/* ═══════════════════════════════════════════════════════════════════════
 * MODULE HASHES
 * ═══════════════════════════════════════════════════════════════════════ */
#define H_KERNEL32                  0x7040EE75u  /* kernel32.dll */
#define H_NTDLL                     0x22D3B5EDu  /* ntdll.dll    */
/* user32.dll no longer needed — console only */

/* ═══════════════════════════════════════════════════════════════════════
 * FUNCTION HASHES
 * ═══════════════════════════════════════════════════════════════════════ */
/* LoadLibraryA no longer needed */
#define H_IsDebuggerPresent         0xE6A24847u
#define H_CheckRemoteDebuggerPresent 0x06638C15u
#define H_GetCurrentProcess         0xCA8D7527u
#define H_GetCurrentThread          0xE03908C0u
#define H_GetThreadContext          0xEBA2CFC2u
#define H_CloseHandle               0x3870CA07u
#define H_GetTickCount64            0x614DB023u
#define H_QueryPerformanceCounter   0xDB4E150Du
#define H_QueryPerformanceFrequency 0x40D0207Fu
/* MessageBoxA removed — console only */
#define H_NtQueryInformationProcess 0xD034FC62u
#define H_NtQuerySystemInformation  0xEE4F73A8u
#define H_GetStdHandle              0xF178843Cu
#define H_WriteConsoleA             0xEE4211A4u
#define H_SetConsoleTextAttribute   0x4A3A951Du

/* ═══════════════════════════════════════════════════════════════════════
 * PEB WALKING — find module base by hash
 * ═══════════════════════════════════════════════════════════════════════
 *
 * PEB layout (x64):
 *   gs:[0x60]        -> PEB
 *   PEB + 0x18       -> PEB_LDR_DATA
 *   LDR + 0x20       -> InMemoryOrderModuleList (LIST_ENTRY)
 *
 * Each list entry (at InMemoryOrderLinks offset 0x10 in LDR_DATA_TABLE_ENTRY):
 *   entry + 0x20     -> DllBase
 *   entry + 0x48     -> BaseDllName.Length  (WORD)
 *   entry + 0x4A     -> BaseDllName.MaxLen  (WORD)
 *   entry + 0x50     -> BaseDllName.Buffer  (WCHAR*)
 */
static BYTE *get_module(DWORD hash)
{
    BYTE *peb = (BYTE *)__readgsqword(0x60);
    BYTE *ldr = *(BYTE **)(peb + 0x18);
    BYTE *head = ldr + 0x20;               /* &InMemoryOrderModuleList */
    BYTE *node = *(BYTE **)head;            /* first Flink */

    while (node != head) {
        BYTE  *base   = *(BYTE **)(node + 0x20);
        WORD   namLen = *(WORD  *)(node + 0x48);   /* BaseDllName.Length (bytes) */
        WCHAR *namBuf = *(WCHAR**)(node + 0x50);   /* BaseDllName.Buffer */

        if (base && namBuf && namLen > 0) {
            DWORD h = djb2_w_lower(namBuf, namLen / 2);
            if (h == hash)
                return base;
        }
        node = *(BYTE **)node;  /* Flink */
    }
    return NULL;
}

/* ═══════════════════════════════════════════════════════════════════════
 * PE EXPORT TABLE — find function by hash
 * ═══════════════════════════════════════════════════════════════════════
 *
 * DOS header + 0x3C   -> e_lfanew
 * PE + 0x88           -> ExportDirectory RVA  (first data directory, x64)
 * Export dir + 0x18   -> NumberOfNames
 * Export dir + 0x1C   -> AddressOfFunctions  RVA
 * Export dir + 0x20   -> AddressOfNames      RVA
 * Export dir + 0x24   -> AddressOfNameOrdinals RVA
 */
static void *get_proc(BYTE *base, DWORD hash)
{
    if (!base) return NULL;

    DWORD pe_off   = *(DWORD *)(base + 0x3C);
    BYTE *pe       = base + pe_off;
    DWORD exp_rva  = *(DWORD *)(pe + 0x88);
    if (exp_rva == 0) return NULL;

    BYTE *exp      = base + exp_rva;
    DWORD nNames   = *(DWORD *)(exp + 0x18);
    DWORD *funcs   = (DWORD *)(base + *(DWORD *)(exp + 0x1C));
    DWORD *names   = (DWORD *)(base + *(DWORD *)(exp + 0x20));
    WORD  *ords    = (WORD  *)(base + *(DWORD *)(exp + 0x24));

    for (DWORD i = 0; i < nNames; i++) {
        const char *name = (const char *)(base + names[i]);
        if (djb2_a(name) == hash)
            return (void *)(base + funcs[ords[i]]);
    }
    return NULL;
}

/* ═══════════════════════════════════════════════════════════════════════
 * RESOLVED FUNCTION POINTERS
 * ═══════════════════════════════════════════════════════════════════════ */
typedef HANDLE (__stdcall *fn_GetCurrentProcess)(void);
typedef HANDLE (__stdcall *fn_GetCurrentThread)(void);
typedef BOOL   (__stdcall *fn_GetThreadContext)(HANDLE, CONTEXT*);
typedef BOOL   (__stdcall *fn_IsDebuggerPresent)(void);
typedef BOOL   (__stdcall *fn_CheckRemoteDebuggerPresent)(HANDLE, BOOL*);
typedef BOOL   (__stdcall *fn_CloseHandle)(HANDLE);
typedef QWORD  (__stdcall *fn_GetTickCount64)(void);
typedef BOOL   (__stdcall *fn_QueryPerformanceCounter)(LARGE_INTEGER*);
typedef BOOL   (__stdcall *fn_QueryPerformanceFrequency)(LARGE_INTEGER*);
typedef NTSTATUS (__stdcall *fn_NtQueryInformationProcess)(HANDLE, DWORD, PVOID, DWORD, PDWORD);
typedef NTSTATUS (__stdcall *fn_NtQuerySystemInformation)(DWORD, PVOID, DWORD, PDWORD);
typedef HANDLE (__stdcall *fn_GetStdHandle)(DWORD);
typedef BOOL   (__stdcall *fn_WriteConsoleA)(HANDLE, const void*, DWORD, PDWORD, PVOID);
typedef BOOL   (__stdcall *fn_SetConsoleTextAttribute)(HANDLE, WORD);

static fn_GetCurrentProcess             pGetCurrentProcess;
static fn_GetCurrentThread              pGetCurrentThread;
static fn_GetThreadContext              pGetThreadContext;
static fn_IsDebuggerPresent             pIsDebuggerPresent;
static fn_CheckRemoteDebuggerPresent    pCheckRemoteDebuggerPresent;
static fn_CloseHandle                   pCloseHandle;
static fn_GetTickCount64                pGetTickCount64;
static fn_QueryPerformanceCounter       pQueryPerformanceCounter;
static fn_QueryPerformanceFrequency     pQueryPerformanceFrequency;
static fn_NtQueryInformationProcess     pNtQueryInformationProcess;
static fn_NtQuerySystemInformation      pNtQuerySystemInformation;
static fn_GetStdHandle                  pGetStdHandle;
static fn_WriteConsoleA                 pWriteConsoleA;
static fn_SetConsoleTextAttribute       pSetConsoleTextAttribute;
static HANDLE                           hStdOut;

/* ═══════════════════════════════════════════════════════════════════════
 * HELPERS (no CRT)
 * ═══════════════════════════════════════════════════════════════════════ */
/* Must be named 'memset' — compiler emits calls to it for struct zeroing */
#pragma function(memset)
void *memset(void *dst, int val, unsigned __int64 size)
{
    BYTE *p = (BYTE *)dst;
    while (size--) *p++ = (BYTE)val;
    return dst;
}

#define my_memset(d,v,s) memset((d),(v),(s))

static int my_strlen(const char *s)
{
    int n = 0;
    while (*s++) n++;
    return n;
}

static void my_strcpy(char *dst, const char *src)
{
    while ((*dst++ = *src++));
}

static void my_itoa(char *buf, int val)
{
    char tmp[16];
    int neg = 0, i = 0;
    if (val < 0) { neg = 1; val = -val; }
    if (val == 0) { tmp[i++] = '0'; }
    else { while (val) { tmp[i++] = '0' + (val % 10); val /= 10; } }
    int j = 0;
    if (neg) buf[j++] = '-';
    while (i > 0) buf[j++] = tmp[--i];
    buf[j] = 0;
}

static void my_itoa_hex(char *buf, QWORD val)
{
    const char *hex = "0123456789ABCDEF";
    char tmp[20];
    int i = 0;
    if (val == 0) { tmp[i++] = '0'; }
    else { while (val) { tmp[i++] = hex[val & 0xF]; val >>= 4; } }
    int j = 0;
    buf[j++] = '0'; buf[j++] = 'x';
    while (i > 0) buf[j++] = tmp[--i];
    buf[j] = 0;
}

/* Append src to dst */
static void my_strcat(char *dst, const char *src)
{
    dst += my_strlen(dst);
    my_strcpy(dst, src);
}

/* ═══════════════════════════════════════════════════════════════════════
 * RESOLVE ALL APIS
 * ═══════════════════════════════════════════════════════════════════════ */
static BOOL resolve_all(void)
{
    BYTE *k32   = get_module(H_KERNEL32);
    BYTE *ntdll = get_module(H_NTDLL);

    if (!k32 || !ntdll) return FALSE;

    pGetCurrentProcess          = (fn_GetCurrentProcess)         get_proc(k32, H_GetCurrentProcess);
    pGetCurrentThread           = (fn_GetCurrentThread)          get_proc(k32, H_GetCurrentThread);
    pGetThreadContext           = (fn_GetThreadContext)           get_proc(k32, H_GetThreadContext);
    pIsDebuggerPresent          = (fn_IsDebuggerPresent)         get_proc(k32, H_IsDebuggerPresent);
    pCheckRemoteDebuggerPresent = (fn_CheckRemoteDebuggerPresent)get_proc(k32, H_CheckRemoteDebuggerPresent);
    pCloseHandle                = (fn_CloseHandle)               get_proc(k32, H_CloseHandle);
    pGetTickCount64             = (fn_GetTickCount64)            get_proc(k32, H_GetTickCount64);
    pQueryPerformanceCounter    = (fn_QueryPerformanceCounter)   get_proc(k32, H_QueryPerformanceCounter);
    pQueryPerformanceFrequency  = (fn_QueryPerformanceFrequency) get_proc(k32, H_QueryPerformanceFrequency);
    pNtQueryInformationProcess  = (fn_NtQueryInformationProcess) get_proc(ntdll, H_NtQueryInformationProcess);
    pNtQuerySystemInformation   = (fn_NtQuerySystemInformation)  get_proc(ntdll, H_NtQuerySystemInformation);
    pGetStdHandle               = (fn_GetStdHandle)              get_proc(k32, H_GetStdHandle);
    pWriteConsoleA              = (fn_WriteConsoleA)             get_proc(k32, H_WriteConsoleA);
    pSetConsoleTextAttribute    = (fn_SetConsoleTextAttribute)   get_proc(k32, H_SetConsoleTextAttribute);

    if (pGetStdHandle)
        hStdOut = pGetStdHandle(STD_OUTPUT_HANDLE);

    return pGetCurrentProcess && pGetCurrentThread && pWriteConsoleA;
}

/* ═══════════════════════════════════════════════════════════════════════
 * CONSOLE OUTPUT
 * ═══════════════════════════════════════════════════════════════════════ */
static void con_write(const char *s)
{
    if (!pWriteConsoleA || !hStdOut) return;
    DWORD written;
    pWriteConsoleA(hStdOut, s, (DWORD)my_strlen(s), &written, NULL);
}

static void con_color(WORD attr)
{
    if (pSetConsoleTextAttribute && hStdOut)
        pSetConsoleTextAttribute(hStdOut, attr);
}

/* ═══════════════════════════════════════════════════════════════════════
 * REPORTING
 * ═══════════════════════════════════════════════════════════════════════ */
static int g_total  = 0;
static int g_detect = 0;

static void Report(const char *method, BOOL detected)
{
    g_total++;
    if (detected) g_detect++;

    /* Console output with color */
    con_color(FG_WHITE);
    con_write("  ");
    con_write(method);
    con_write("  ");

    if (detected) {
        con_color(FG_RED);
        con_write("[DETECTED]\n");
    } else {
        con_color(FG_GREEN);
        con_write("[PASSED]\n");
    }
    con_color(FG_DEFAULT);
}

/* ═══════════════════════════════════════════════════════════════════════
 * ANTI-DEBUG CHECKS
 * ═══════════════════════════════════════════════════════════════════════ */

/* 1. IsDebuggerPresent */
static void Check01(void)
{
    Report("1. IsDebuggerPresent", pIsDebuggerPresent());
}

/* 2. PEB.BeingDebugged direct read */
static void Check02(void)
{
    BYTE val = *(BYTE *)(__readgsqword(0x60) + 2);
    Report("2. PEB.BeingDebugged (direct TEB read)", val != 0);
}

/* 3. PEB.NtGlobalFlag */
static void Check03(void)
{
    DWORD flags = *(DWORD *)(__readgsqword(0x60) + 0xBC);
    BOOL detected = (flags & 0x70) != 0;
    char msg[128];
    my_strcpy(msg, "3. PEB.NtGlobalFlag = ");
    my_itoa_hex(msg + my_strlen(msg), flags);
    Report(msg, detected);
}

/* 4. Heap Flags */
static void Check04(void)
{
    BYTE *peb  = (BYTE *)__readgsqword(0x60);
    BYTE *heap = *(BYTE **)(peb + 0x30);
    DWORD flags      = *(DWORD *)(heap + 0x70);
    DWORD forceFlags = *(DWORD *)(heap + 0x74);
    char msg[128];
    my_strcpy(msg, "4. Heap Flags=");
    my_itoa_hex(msg + my_strlen(msg), flags);
    my_strcat(msg, " Force=");
    my_itoa_hex(msg + my_strlen(msg), forceFlags);
    Report(msg, flags != 2 || forceFlags != 0);
}

/* 5. CheckRemoteDebuggerPresent */
static void Check05(void)
{
    if (!pCheckRemoteDebuggerPresent) { Report("5. CheckRemoteDebuggerPresent (n/a)", FALSE); return; }
    BOOL present = FALSE;
    pCheckRemoteDebuggerPresent(pGetCurrentProcess(), &present);
    Report("5. CheckRemoteDebuggerPresent", present);
}

/* 6. NtQueryInformationProcess — DebugPort (0x07) */
static void Check06(void)
{
    if (!pNtQueryInformationProcess) { Report("6. DebugPort (n/a)", FALSE); return; }
    QWORD debugPort = 0;
    NTSTATUS st = pNtQueryInformationProcess(
        pGetCurrentProcess(), 7, &debugPort, sizeof(debugPort), NULL);
    Report("6. NtQueryInformationProcess(DebugPort)", NT_SUCCESS(st) && debugPort != 0);
}

/* 7. NtQueryInformationProcess — DebugObjectHandle (0x1E) */
static void Check07(void)
{
    if (!pNtQueryInformationProcess) { Report("7. DebugObjectHandle (n/a)", FALSE); return; }
    HANDLE debugObj = NULL;
    NTSTATUS st = pNtQueryInformationProcess(
        pGetCurrentProcess(), 0x1E, &debugObj, sizeof(debugObj), NULL);
    Report("7. NtQueryInformationProcess(DebugObjectHandle)", NT_SUCCESS(st) && debugObj != NULL);
}

/* 8. NtQueryInformationProcess — DebugFlags (0x1F) */
static void Check08(void)
{
    if (!pNtQueryInformationProcess) { Report("8. DebugFlags (n/a)", FALSE); return; }
    DWORD debugFlags = 1;
    NTSTATUS st = pNtQueryInformationProcess(
        pGetCurrentProcess(), 0x1F, &debugFlags, sizeof(debugFlags), NULL);
    Report("8. NtQueryInformationProcess(DebugFlags=0)", NT_SUCCESS(st) && debugFlags == 0);
}

/* 9. SystemKernelDebuggerInformation (0x23) */
static void Check09(void)
{
    if (!pNtQuerySystemInformation) { Report("9. KernelDebugger (n/a)", FALSE); return; }
    struct { BYTE DebuggerEnabled; BYTE DebuggerNotPresent; } info;
    my_memset(&info, 0, sizeof(info));
    NTSTATUS st = pNtQuerySystemInformation(0x23, &info, sizeof(info), NULL);
    Report("9. SystemKernelDebuggerInformation",
           NT_SUCCESS(st) && info.DebuggerEnabled && !info.DebuggerNotPresent);
}

/* 10. CloseHandle invalid handle exception */
static void Check10(void)
{
    /* Under debugger, CloseHandle(invalid) raises EXCEPTION_INVALID_HANDLE.
       Without SEH (__try/__except needs CRT on some configs), we just call it
       and note: if the process survives, no debugger caught the exception.
       A real debugger would break here — that itself is the detection. */
    pCloseHandle(INVALID_HANDLE);
    Report("10. CloseHandle(invalid) — survived (no break)", FALSE);
}

/* 11. Hardware breakpoint detection (DR registers) */
static void Check11(void)
{
    if (!pGetThreadContext) { Report("11. HW Breakpoints (n/a)", FALSE); return; }
    CONTEXT ctx;
    my_memset(&ctx, 0, sizeof(ctx));
    ctx.ContextFlags = CONTEXT_DEBUG_REGISTERS_F;
    if (pGetThreadContext(pGetCurrentThread(), &ctx)) {
        BOOL detected = (ctx.Dr0 || ctx.Dr1 || ctx.Dr2 || ctx.Dr3);
        char msg[256];
        my_strcpy(msg, "11. DR0=");
        my_itoa_hex(msg + my_strlen(msg), ctx.Dr0);
        my_strcat(msg, " DR1=");
        my_itoa_hex(msg + my_strlen(msg), ctx.Dr1);
        my_strcat(msg, " DR2=");
        my_itoa_hex(msg + my_strlen(msg), ctx.Dr2);
        my_strcat(msg, " DR3=");
        my_itoa_hex(msg + my_strlen(msg), ctx.Dr3);
        Report(msg, detected);
    } else {
        Report("11. HW Breakpoints (GetThreadContext failed)", FALSE);
    }
}

/* 12. RDTSC timing */
static void Check12(void)
{
    QWORD t1 = __rdtsc();
    volatile int x = 0;
    for (int i = 0; i < 100; i++) x += i;
    QWORD t2 = __rdtsc();
    QWORD delta = t2 - t1;
    char msg[128];
    my_strcpy(msg, "12. RDTSC delta: ");
    my_itoa(msg + my_strlen(msg), (int)(delta & 0x7FFFFFFF));
    my_strcat(msg, " cycles");
    Report(msg, delta > 10000000);
}

/* 13. QPC timing */
static void Check13(void)
{
    if (!pQueryPerformanceCounter || !pQueryPerformanceFrequency) {
        Report("13. QPC (n/a)", FALSE); return;
    }
    LARGE_INTEGER freq, t1, t2;
    pQueryPerformanceFrequency(&freq);
    pQueryPerformanceCounter(&t1);
    volatile int x = 0;
    for (int i = 0; i < 100; i++) x += i;
    pQueryPerformanceCounter(&t2);
    INT64 diff = t2.QuadPart - t1.QuadPart;
    /* ms = diff * 1000 / freq — integer approx */
    INT64 ms = (diff * 1000) / freq.QuadPart;
    char msg[128];
    my_strcpy(msg, "13. QPC delta: ");
    my_itoa(msg + my_strlen(msg), (int)ms);
    my_strcat(msg, " ms");
    Report(msg, ms > 100);
}

/* 14. GetTickCount64 timing */
static void Check14(void)
{
    if (!pGetTickCount64) { Report("14. GetTickCount64 (n/a)", FALSE); return; }
    QWORD t1 = pGetTickCount64();
    volatile int x = 0;
    for (int i = 0; i < 100; i++) x += i;
    QWORD t2 = pGetTickCount64();
    QWORD delta = t2 - t1;
    char msg[128];
    my_strcpy(msg, "14. GetTickCount64 delta: ");
    my_itoa(msg + my_strlen(msg), (int)delta);
    my_strcat(msg, " ms");
    Report(msg, delta > 100);
}

/* 15. Software breakpoint scan (0xCC in code) */
static void Check15(void)
{
    BYTE *func = (BYTE *)&Check01;
    BOOL detected = FALSE;
    for (int i = 0; i < 64; i++) {
        if (func[i] == 0xCC) { detected = TRUE; break; }
    }
    Report("15. Software breakpoint scan (0xCC in code)", detected);
}

/* 16. Trap Flag (EFLAGS.TF) */
static void Check16(void)
{
    if (!pGetThreadContext) { Report("16. Trap Flag (n/a)", FALSE); return; }
    CONTEXT ctx;
    my_memset(&ctx, 0, sizeof(ctx));
    ctx.ContextFlags = CONTEXT_CONTROL_F;
    if (pGetThreadContext(pGetCurrentThread(), &ctx)) {
        Report("16. Trap Flag (EFLAGS.TF)", (ctx.EFlags & 0x100) != 0);
    } else {
        Report("16. Trap Flag (GetThreadContext failed)", FALSE);
    }
}

/* ═══════════════════════════════════════════════════════════════════════
 * ENTRY POINT — no CRT, no main()
 * ═══════════════════════════════════════════════════════════════════════ */
void __stdcall entry(void)
{
    if (!resolve_all()) {
        /* Can't even show MessageBox — just exit */
        /* Use NtTerminateProcess or loop forever; simplest: return */
        return;
    }

    /* Console banner */
    con_color(FG_CYAN);
    con_write("\n  ============================================\n");
    con_write("   AntiDebug Test (No-Import Edition)\n");
    con_write("   All APIs resolved by hash - zero imports\n");
    con_write("  ============================================\n\n");
    con_color(FG_DEFAULT);

    /* PEB-based */
    Check01();      /* IsDebuggerPresent        */
    Check02();      /* PEB.BeingDebugged        */
    Check03();      /* NtGlobalFlag             */
    Check04();      /* Heap Flags               */

    /* API-based */
    Check05();      /* CheckRemoteDebuggerPresent */
    Check06();      /* DebugPort                */
    Check07();      /* DebugObjectHandle        */
    Check08();      /* DebugFlags               */
    Check09();      /* SystemKernelDebugger     */

    /* Exception-based */
    Check10();      /* CloseHandle invalid      */

    /* Context */
    Check11();      /* Hardware breakpoints     */

    /* Timing */
    Check12();      /* RDTSC                    */
    Check13();      /* QPC                      */
    Check14();      /* GetTickCount64           */

    /* Code integrity */
    Check15();      /* 0xCC scan                */

    /* Flags */
    Check16();      /* Trap Flag                */

    /* Console summary */
    con_color(FG_CYAN);
    con_write("\n  ============================================\n");
    con_color(FG_WHITE);
    con_write("   Total checks: ");
    { char nb[16]; my_itoa(nb, g_total); con_write(nb); }
    con_write("\n   Detected:     ");
    con_color(g_detect > 0 ? FG_RED : FG_GREEN);
    { char nb[16]; my_itoa(nb, g_detect); con_write(nb); }
    con_color(FG_WHITE);
    con_write("\n   Passed:       ");
    con_color(FG_GREEN);
    { char nb[16]; my_itoa(nb, g_total - g_detect); con_write(nb); }
    con_write("\n");
    con_color(FG_CYAN);
    con_write("  ============================================\n");

    if (g_detect > 0) {
        con_color(FG_RED);
        con_write("\n   >>> DEBUGGER WAS DETECTED! <<<\n\n");
    } else {
        con_color(FG_GREEN);
        con_write("\n   No debugger detected.\n\n");
    }
    con_color(FG_DEFAULT);

}
