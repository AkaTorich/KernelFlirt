/*
 * AntiDebug Test Program
 * Runs anti-debug checks, shows MessageBox for EVERY check (passed or detected).
 * Never crashes, never hangs.
 * Build: cl /O2 /Zi /D_CRT_SECURE_NO_WARNINGS antidebug_test.c /link /DEBUG user32.lib kernel32.lib
 */

#include <windows.h>
#include <stdio.h>
#include <intrin.h>

/* ── ntdll imports ── */
typedef long NTSTATUS;
#define STATUS_SUCCESS ((NTSTATUS)0x00000000)
#define NT_SUCCESS(s) ((s) >= 0)

typedef struct _PROCESS_BASIC_INFORMATION {
    NTSTATUS  ExitStatus;
    void     *PebBaseAddress;
    ULONG_PTR AffinityMask;
    LONG      BasePriority;
    ULONG_PTR UniqueProcessId;
    ULONG_PTR InheritedFromUniqueProcessId;
} PROCESS_BASIC_INFORMATION;

typedef NTSTATUS (NTAPI *PFN_NtQueryInformationProcess)(
    HANDLE, ULONG, PVOID, ULONG, PULONG);
typedef NTSTATUS (NTAPI *PFN_NtQuerySystemInformation)(
    ULONG, PVOID, ULONG, PULONG);
typedef NTSTATUS (NTAPI *PFN_NtQueryObject)(
    HANDLE, ULONG, PVOID, ULONG, PULONG);

static PFN_NtQueryInformationProcess pNtQueryInformationProcess;
static PFN_NtQuerySystemInformation  pNtQuerySystemInformation;
static PFN_NtQueryObject             pNtQueryObject;

static int g_totalChecks = 0;
static int g_detected    = 0;

static void Report(const char *method, BOOL detected)
{
    g_totalChecks++;
    if (detected) {
        g_detected++;
        MessageBoxA(NULL, method, "DETECTED", MB_OK | MB_ICONWARNING);
    } else {
        MessageBoxA(NULL, method, "PASSED", MB_OK | MB_ICONINFORMATION);
    }
}

static void InitNtdll(void)
{
    HMODULE ntdll = GetModuleHandleA("ntdll.dll");
    if (!ntdll) return;
    pNtQueryInformationProcess = (PFN_NtQueryInformationProcess)
        GetProcAddress(ntdll, "NtQueryInformationProcess");
    pNtQuerySystemInformation = (PFN_NtQuerySystemInformation)
        GetProcAddress(ntdll, "NtQuerySystemInformation");
    pNtQueryObject = (PFN_NtQueryObject)
        GetProcAddress(ntdll, "NtQueryObject");
}

/* ── 1. IsDebuggerPresent ── */
static void Check_IsDebuggerPresent(void)
{
    __try {
        Report("1. IsDebuggerPresent", IsDebuggerPresent());
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        Report("1. IsDebuggerPresent (EXCEPTION)", FALSE);
    }
}

/* ── 2. PEB.BeingDebugged direct read ── */
static void Check_PEB_BeingDebugged_Direct(void)
{
    __try {
#ifdef _WIN64
        unsigned char val = *(unsigned char*)(__readgsqword(0x60) + 2);
#else
        unsigned char val = *(unsigned char*)(__readfsdword(0x30) + 2);
#endif
        Report("2. PEB.BeingDebugged (direct TEB read)", val != 0);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        Report("2. PEB.BeingDebugged (EXCEPTION)", FALSE);
    }
}

/* ── 3. PEB.NtGlobalFlag ── */
static void Check_NtGlobalFlag(void)
{
    __try {
#ifdef _WIN64
        DWORD flags = *(DWORD*)(__readgsqword(0x60) + 0xBC);
#else
        DWORD flags = *(DWORD*)(__readfsdword(0x30) + 0x68);
#endif
        BOOL detected = (flags & 0x70) != 0;
        char msg[128];
        sprintf(msg, "3. PEB.NtGlobalFlag = 0x%X", flags);
        Report(msg, detected);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        Report("3. PEB.NtGlobalFlag (EXCEPTION)", FALSE);
    }
}

/* ── 4. Heap Flags ── */
static void Check_HeapFlags(void)
{
    __try {
#ifdef _WIN64
        void *peb = (void*)__readgsqword(0x60);
        void *heap = *(void**)((char*)peb + 0x30);
        DWORD flags      = *(DWORD*)((char*)heap + 0x70);
        DWORD forceFlags  = *(DWORD*)((char*)heap + 0x74);
#else
        void *peb = (void*)__readfsdword(0x30);
        void *heap = *(void**)((char*)peb + 0x18);
        DWORD flags      = *(DWORD*)((char*)heap + 0x40);
        DWORD forceFlags  = *(DWORD*)((char*)heap + 0x44);
#endif
        char msg[128];
        sprintf(msg, "4. Heap.Flags=0x%X, ForceFlags=0x%X", flags, forceFlags);
        Report(msg, flags != 2 || forceFlags != 0);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        Report("4. Heap Flags (EXCEPTION)", FALSE);
    }
}

/* ── 5. CheckRemoteDebuggerPresent ── */
static void Check_RemoteDebuggerPresent(void)
{
    __try {
        BOOL present = FALSE;
        CheckRemoteDebuggerPresent(GetCurrentProcess(), &present);
        Report("5. CheckRemoteDebuggerPresent", present);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        Report("5. CheckRemoteDebuggerPresent (EXCEPTION)", FALSE);
    }
}

/* ── 6. NtQueryInformationProcess - DebugPort (0x07) ── */
static void Check_DebugPort(void)
{
    __try {
        if (!pNtQueryInformationProcess) { Report("6. DebugPort (no ntdll)", FALSE); return; }
        ULONG_PTR debugPort = 0;
        NTSTATUS st = pNtQueryInformationProcess(
            GetCurrentProcess(), 7, &debugPort, sizeof(debugPort), NULL);
        Report("6. NtQueryInformationProcess(DebugPort)", NT_SUCCESS(st) && debugPort != 0);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        Report("6. DebugPort (EXCEPTION)", FALSE);
    }
}

/* ── 7. NtQueryInformationProcess - DebugObjectHandle (0x1E) ── */
static void Check_DebugObjectHandle(void)
{
    __try {
        if (!pNtQueryInformationProcess) { Report("7. DebugObjectHandle (no ntdll)", FALSE); return; }
        HANDLE debugObj = NULL;
        NTSTATUS st = pNtQueryInformationProcess(
            GetCurrentProcess(), 0x1E, &debugObj, sizeof(debugObj), NULL);
        Report("7. NtQueryInformationProcess(DebugObjectHandle)",
               NT_SUCCESS(st) && debugObj != NULL);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        Report("7. DebugObjectHandle (EXCEPTION)", FALSE);
    }
}

/* ── 8. NtQueryInformationProcess - DebugFlags (0x1F) ── */
static void Check_DebugFlags(void)
{
    __try {
        if (!pNtQueryInformationProcess) { Report("8. DebugFlags (no ntdll)", FALSE); return; }
        ULONG debugFlags = 1;
        NTSTATUS st = pNtQueryInformationProcess(
            GetCurrentProcess(), 0x1F, &debugFlags, sizeof(debugFlags), NULL);
        Report("8. NtQueryInformationProcess(DebugFlags=0)", NT_SUCCESS(st) && debugFlags == 0);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        Report("8. DebugFlags (EXCEPTION)", FALSE);
    }
}

/* ── 9. SystemKernelDebuggerInformation (0x23) ── */
static void Check_SystemKernelDebugger(void)
{
    __try {
        if (!pNtQuerySystemInformation) { Report("9. KernelDebugger (no ntdll)", FALSE); return; }
        struct { BOOLEAN DebuggerEnabled; BOOLEAN DebuggerNotPresent; } info = {0};
        NTSTATUS st = pNtQuerySystemInformation(0x23, &info, sizeof(info), NULL);
        Report("9. SystemKernelDebuggerInformation",
               NT_SUCCESS(st) && info.DebuggerEnabled && !info.DebuggerNotPresent);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        Report("9. KernelDebugger (EXCEPTION)", FALSE);
    }
}

/* ── 10. CloseHandle invalid handle exception ── */
static void Check_CloseHandle_Exception(void)
{
    BOOL detected = FALSE;
    __try {
        CloseHandle((HANDLE)(ULONG_PTR)0xDEADBEEF);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        detected = TRUE;
    }
    Report("10. CloseHandle(invalid) -> exception", detected);
}

/* ── 11. Hardware breakpoint detection (DR registers) ── */
static void Check_HardwareBreakpoints(void)
{
    __try {
        CONTEXT ctx;
        memset(&ctx, 0, sizeof(ctx));
        ctx.ContextFlags = CONTEXT_DEBUG_REGISTERS;
        if (GetThreadContext(GetCurrentThread(), &ctx)) {
            BOOL detected = (ctx.Dr0 != 0 || ctx.Dr1 != 0 || ctx.Dr2 != 0 || ctx.Dr3 != 0);
            char msg[256];
            sprintf(msg, "11. HW Breakpoints: DR0=%llX DR1=%llX DR2=%llX DR3=%llX",
                    (unsigned long long)ctx.Dr0, (unsigned long long)ctx.Dr1,
                    (unsigned long long)ctx.Dr2, (unsigned long long)ctx.Dr3);
            Report(msg, detected);
        } else {
            Report("11. HW Breakpoints (GetThreadContext failed)", FALSE);
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        Report("11. HW Breakpoints (EXCEPTION)", FALSE);
    }
}

/* ── 12. RDTSC timing ── */
static void Check_Timing_RDTSC(void)
{
    __try {
        unsigned __int64 t1 = __rdtsc();
        volatile int x = 0;
        for (int i = 0; i < 100; i++) x += i;
        unsigned __int64 t2 = __rdtsc();
        unsigned __int64 delta = t2 - t1;
        char msg[128];
        sprintf(msg, "12. RDTSC timing: %llu cycles", (unsigned long long)delta);
        Report(msg, delta > 10000000);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        Report("12. RDTSC (EXCEPTION)", FALSE);
    }
}

/* ── 13. QPC timing ── */
static void Check_Timing_QPC(void)
{
    __try {
        LARGE_INTEGER freq, t1, t2;
        QueryPerformanceFrequency(&freq);
        QueryPerformanceCounter(&t1);
        volatile int x = 0;
        for (int i = 0; i < 100; i++) x += i;
        QueryPerformanceCounter(&t2);
        double ms = (double)(t2.QuadPart - t1.QuadPart) / freq.QuadPart * 1000.0;
        char msg[128];
        sprintf(msg, "13. QPC timing: %.3f ms", ms);
        Report(msg, ms > 100.0);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        Report("13. QPC (EXCEPTION)", FALSE);
    }
}

/* ── 14. GetTickCount64 timing ── */
static void Check_Timing_GetTickCount(void)
{
    __try {
        ULONGLONG t1 = GetTickCount64();
        volatile int x = 0;
        for (int i = 0; i < 100; i++) x += i;
        ULONGLONG t2 = GetTickCount64();
        ULONGLONG delta = t2 - t1;
        char msg[128];
        sprintf(msg, "14. GetTickCount64 delta: %llu ms", (unsigned long long)delta);
        Report(msg, delta > 100);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        Report("14. GetTickCount64 (EXCEPTION)", FALSE);
    }
}

/* ── 15. Software breakpoint scan (0xCC) ── */
static void Check_SoftwareBreakpoints(void)
{
    __try {
        unsigned char *func = (unsigned char*)&Check_IsDebuggerPresent;
        BOOL detected = FALSE;
        for (int i = 0; i < 64; i++) {
            if (func[i] == 0xCC) {
                detected = TRUE;
                break;
            }
        }
        Report("15. Software breakpoint scan (0xCC in code)", detected);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        Report("15. SW breakpoint scan (EXCEPTION)", FALSE);
    }
}

/* ── 16. Trap Flag (EFLAGS.TF) ── */
static void Check_TrapFlag(void)
{
    __try {
        CONTEXT ctx;
        memset(&ctx, 0, sizeof(ctx));
        ctx.ContextFlags = CONTEXT_CONTROL;
        if (GetThreadContext(GetCurrentThread(), &ctx)) {
            BOOL detected = (ctx.EFlags & 0x100) != 0;
            Report("16. Trap Flag (EFLAGS.TF)", detected);
        } else {
            Report("16. Trap Flag (GetThreadContext failed)", FALSE);
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        Report("16. Trap Flag (EXCEPTION)", FALSE);
    }
}

/* ═══════════════════════════════════════════════════════════════════════
 * MAIN
 * ═══════════════════════════════════════════════════════════════════════ */
int WINAPI WinMain(HINSTANCE hInst, HINSTANCE hPrev, LPSTR cmdLine, int nShow)
{
    (void)hInst; (void)hPrev; (void)cmdLine; (void)nShow;

    InitNtdll();

    MessageBoxA(NULL,
        "Anti-Debug Test Program\n\n"
        "16 anti-debugging checks.\n"
        "MessageBox for EVERY check (DETECTED or PASSED).\n\n"
        "Click OK to start.",
        "AntiDebug Test", MB_OK | MB_ICONINFORMATION);

    /* PEB-based */
    Check_IsDebuggerPresent();          /* 1  */
    Check_PEB_BeingDebugged_Direct();   /* 2  */
    Check_NtGlobalFlag();               /* 3  */
    Check_HeapFlags();                  /* 4  */

    /* API-based */
    Check_RemoteDebuggerPresent();      /* 5  */
    Check_DebugPort();                  /* 6  */
    Check_DebugObjectHandle();          /* 7  */
    Check_DebugFlags();                 /* 8  */
    Check_SystemKernelDebugger();       /* 9  */

    /* Exception-based */
    Check_CloseHandle_Exception();      /* 10 */

    /* Context */
    Check_HardwareBreakpoints();        /* 11 */

    /* Timing */
    Check_Timing_RDTSC();              /* 12 */
    Check_Timing_QPC();                /* 13 */
    Check_Timing_GetTickCount();       /* 14 */

    /* Code integrity */
    Check_SoftwareBreakpoints();       /* 15 */

    /* Flags */
    Check_TrapFlag();                  /* 16 */

    /* Summary */
    char summary[256];
    sprintf(summary,
        "Anti-Debug Test Complete!\n\n"
        "Total checks: %d\n"
        "Detected: %d\n"
        "Passed: %d\n\n"
        "%s",
        g_totalChecks, g_detected, g_totalChecks - g_detected,
        g_detected > 0 ? "DEBUGGER WAS DETECTED!" : "No debugger detected.");

    MessageBoxA(NULL, summary, "Results",
        g_detected > 0 ? MB_OK | MB_ICONWARNING : MB_OK | MB_ICONINFORMATION);

    return 0;
}
