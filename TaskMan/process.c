/* process.c - Process management */
#include "taskman.h"

static wchar_t *PM_GetProcessFullPath(DWORD pid) {
    static wchar_t pathBuf[MAX_PATH];
    HANDLE hProc;
    DWORD size = MAX_PATH;
    pathBuf[0] = 0;
    hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (!hProc) return pathBuf;
    QueryFullProcessImageNameW(hProc, 0, pathBuf, &size);
    CloseHandle(hProc);
    return pathBuf;
}

static SIZE_T PM_GetProcessMemoryKB(DWORD pid) {
    SIZE_T memKB = 0;
    HANDLE hProc = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, FALSE, pid);
    if (hProc) {
        PROCESS_MEMORY_COUNTERS pmc;
        memset(&pmc, 0, sizeof(pmc));
        if (GetProcessMemoryInfo(hProc, &pmc, sizeof(pmc)))
            memKB = pmc.WorkingSetSize / 1024;
        CloseHandle(hProc);
    }
    return memKB;
}

HICON PM_GetProcessIcon(const wchar_t *pathOrName) {
    SHFILEINFOW sfi;
    DWORD_PTR res;
    if (!pathOrName || !pathOrName[0]) return NULL;
    memset(&sfi, 0, sizeof(sfi));
    res = SHGetFileInfoW(pathOrName, FILE_ATTRIBUTE_NORMAL, &sfi,
        sizeof(sfi), SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES);
    return (res && sfi.hIcon) ? sfi.hIcon : NULL;
}

BOOL PM_IsSystemProcess(DWORD pid) {
    static const wchar_t *criticals[] = {
        L"csrss.exe", L"winlogon.exe", L"services.exe", L"lsass.exe",
        L"smss.exe", L"wininit.exe", L"explorer.exe"
    };
    wchar_t *fullPath;
    const wchar_t *fileName;
    int i;

    if (pid == 0 || pid == 4) return TRUE;

    fullPath = PM_GetProcessFullPath(pid);
    if (!fullPath[0]) return TRUE;

    fileName = PathFindFileNameW(fullPath);

    for (i = 0; i < (int)(sizeof(criticals)/sizeof(criticals[0])); i++) {
        if (lstrcmpiW(fileName, criticals[i]) == 0)
            return TRUE;
    }
    return FALSE;
}

void PM_EnumerateProcesses(void) {
    HANDLE hSnap;
    PROCESSENTRY32W pe32;
    ProcessInfo pi;
    wchar_t *fullPath;
    wchar_t version[TM_MAX_VERSION];

    DYNARRAY_FREE(g_processes, g_processCount, g_processCap);

    hSnap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (hSnap == INVALID_HANDLE_VALUE) return;

    memset(&pe32, 0, sizeof(pe32));
    pe32.dwSize = sizeof(pe32);

    if (Process32FirstW(hSnap, &pe32)) {
        do {
            memset(&pi, 0, sizeof(pi));
            pi.pid = pe32.th32ProcessID;
            lstrcpynW(pi.exeName, pe32.szExeFile, TM_MAX_NAME);

            fullPath = PM_GetProcessFullPath(pi.pid);
            lstrcpynW(pi.fullPath, fullPath, TM_MAX_PATH_BUF);
            pi.workingSetKB = PM_GetProcessMemoryKB(pi.pid);
            pi.cpuUsage = 0.0;
            pi.parentPid = pe32.th32ParentProcessID;
            pi.threadCount = pe32.cntThreads;

            /* Get handle count */
            {
                HANDLE hTmp = OpenProcess(PROCESS_QUERY_INFORMATION, FALSE, pi.pid);
                if (hTmp) {
                    GetProcessHandleCount(hTmp, &pi.handleCount);
                    CloseHandle(hTmp);
                }
            }

            if (pi.fullPath[0]) {
                pi.hIcon = PM_GetProcessIcon(pi.fullPath);
                FI_GetFileVersionDetails(pi.fullPath, pi.description, pi.company, version);
                pi.verified = FI_IsFileSigned(pi.fullPath);
            } else {
                pi.hIcon = PM_GetProcessIcon(pi.exeName);
            }

            DYNARRAY_GROW(g_processes, g_processCount, g_processCap, ProcessInfo);
            g_processes[g_processCount++] = pi;
        } while (Process32NextW(hSnap, &pe32));
    }
    CloseHandle(hSnap);
}

BOOL PM_TerminateProcessById(DWORD pid) {
    HANDLE hProc;
    BOOL result;
    if (PM_IsSystemProcess(pid)) return FALSE;
    hProc = OpenProcess(PROCESS_TERMINATE, FALSE, pid);
    if (!hProc) return FALSE;
    result = TerminateProcess(hProc, 0);
    CloseHandle(hProc);
    return result;
}

int PM_GetProcessesByName(const wchar_t *name, DWORD *outPids, int maxPids) {
    HANDLE hSnap;
    PROCESSENTRY32W pe32;
    int count = 0;

    hSnap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (hSnap == INVALID_HANDLE_VALUE) return 0;

    memset(&pe32, 0, sizeof(pe32));
    pe32.dwSize = sizeof(pe32);

    if (Process32FirstW(hSnap, &pe32)) {
        do {
            if (lstrcmpiW(pe32.szExeFile, name) == 0 && count < maxPids) {
                outPids[count++] = pe32.th32ProcessID;
            }
        } while (Process32NextW(hSnap, &pe32));
    }
    CloseHandle(hSnap);
    return count;
}

int PM_TerminateProcessesByName(const wchar_t *name) {
    DWORD pids[4096];
    int count, i, success = 0;
    count = PM_GetProcessesByName(name, pids, 4096);
    for (i = 0; i < count; i++) {
        if (!PM_IsSystemProcess(pids[i])) {
            if (PM_TerminateProcessById(pids[i]))
                success++;
        }
    }
    return success;
}

/* ========================================================================= */
/* CPU USAGE                                                                 */
/* ========================================================================= */

static ULONGLONG FT_ToU64(FILETIME ft) {
    return ((ULONGLONG)ft.dwHighDateTime << 32) | ft.dwLowDateTime;
}

static int s_numCPUs = 0;

void PM_BuildCpuSnapshot(void) {
    int i;
    FILETIME sysTime;

    if (s_numCPUs == 0) {
        SYSTEM_INFO si;
        GetSystemInfo(&si);
        s_numCPUs = (int)si.dwNumberOfProcessors;
        if (s_numCPUs < 1) s_numCPUs = 1;
    }

    DYNARRAY_FREE(g_cpuSnap, g_cpuSnapCount, g_cpuSnapCap);
    GetSystemTimeAsFileTime(&sysTime);

    for (i = 0; i < g_processCount; i++) {
        HANDLE hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, g_processes[i].pid);
        if (hProc) {
            FILETIME creation, exitT, kernel, user;
            if (GetProcessTimes(hProc, &creation, &exitT, &kernel, &user)) {
                CpuSnapshot snap;
                snap.pid = g_processes[i].pid;
                snap.kernelTime = FT_ToU64(kernel);
                snap.userTime = FT_ToU64(user);
                snap.snapshotTime = FT_ToU64(sysTime);
                DYNARRAY_GROW(g_cpuSnap, g_cpuSnapCount, g_cpuSnapCap, CpuSnapshot);
                g_cpuSnap[g_cpuSnapCount++] = snap;
            }
            CloseHandle(hProc);
        }
    }
}

void PM_ComputeCpuUsage(void) {
    int i, j;
    FILETIME sysTime;

    if (g_cpuSnapCount == 0) return;

    GetSystemTimeAsFileTime(&sysTime);

    for (i = 0; i < g_processCount; i++) {
        HANDLE hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, g_processes[i].pid);
        if (!hProc) continue;
        {
            FILETIME creation, exitT, kernel, user;
            if (GetProcessTimes(hProc, &creation, &exitT, &kernel, &user)) {
                ULONGLONG newProc = FT_ToU64(kernel) + FT_ToU64(user);
                /* Find in snapshot */
                for (j = 0; j < g_cpuSnapCount; j++) {
                    if (g_cpuSnap[j].pid == g_processes[i].pid) {
                        ULONGLONG oldProc = g_cpuSnap[j].kernelTime + g_cpuSnap[j].userTime;
                        ULONGLONG deltaProc = newProc - oldProc;
                        ULONGLONG deltaSys = FT_ToU64(sysTime) - g_cpuSnap[j].snapshotTime;
                        if (deltaSys > 0) {
                            g_processes[i].cpuUsage = ((double)deltaProc / (double)deltaSys) * 100.0 / s_numCPUs;
                            if (g_processes[i].cpuUsage > 100.0) g_processes[i].cpuUsage = 100.0;
                            if (g_processes[i].cpuUsage < 0.0) g_processes[i].cpuUsage = 0.0;
                        }
                        break;
                    }
                }
            }
        }
        CloseHandle(hProc);
    }
}

/* ========================================================================= */
/* LIGHTWEIGHT STATS REFRESH (for timer - no icons/signatures)               */
/* ========================================================================= */

void PM_RefreshProcessStats(void) {
    int i;
    for (i = 0; i < g_processCount; i++) {
        ProcessInfo *p = &g_processes[i];
        HANDLE hProc;

        p->workingSetKB = PM_GetProcessMemoryKB(p->pid);

        hProc = OpenProcess(PROCESS_QUERY_INFORMATION, FALSE, p->pid);
        if (hProc) {
            GetProcessHandleCount(hProc, &p->handleCount);
            CloseHandle(hProc);
        }
    }
    /* Thread count requires re-snapshot */
    {
        HANDLE hSnap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (hSnap != INVALID_HANDLE_VALUE) {
            PROCESSENTRY32W pe32;
            memset(&pe32, 0, sizeof(pe32));
            pe32.dwSize = sizeof(pe32);
            if (Process32FirstW(hSnap, &pe32)) {
                do {
                    for (i = 0; i < g_processCount; i++) {
                        if (g_processes[i].pid == pe32.th32ProcessID) {
                            g_processes[i].threadCount = pe32.cntThreads;
                            break;
                        }
                    }
                } while (Process32NextW(hSnap, &pe32));
            }
            CloseHandle(hSnap);
        }
    }
}

/* ========================================================================= */
/* NETWORK STATS                                                             */
/* ========================================================================= */

void PM_GatherNetworkStats(void) {
    DWORD tcpSize = 0, udpSize = 0;
    PMIB_TCPTABLE_OWNER_PID tcpTable = NULL;
    PMIB_UDPTABLE_OWNER_PID udpTable = NULL;
    DWORD i;
    int j;

    for (j = 0; j < g_processCount; j++) {
        g_processes[j].tcpConnections = 0;
        g_processes[j].udpConnections = 0;
    }

    /* TCP */
    GetExtendedTcpTable(NULL, &tcpSize, FALSE, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
    if (tcpSize > 0) {
        tcpTable = (PMIB_TCPTABLE_OWNER_PID)HeapAlloc(GetProcessHeap(), 0, tcpSize);
        if (tcpTable) {
            if (GetExtendedTcpTable(tcpTable, &tcpSize, FALSE, AF_INET,
                                     TCP_TABLE_OWNER_PID_ALL, 0) == NO_ERROR) {
                for (i = 0; i < tcpTable->dwNumEntries; i++) {
                    DWORD pid = tcpTable->table[i].dwOwningPid;
                    for (j = 0; j < g_processCount; j++) {
                        if (g_processes[j].pid == pid) {
                            g_processes[j].tcpConnections++;
                            break;
                        }
                    }
                }
            }
            HeapFree(GetProcessHeap(), 0, tcpTable);
        }
    }

    /* UDP */
    GetExtendedUdpTable(NULL, &udpSize, FALSE, AF_INET, UDP_TABLE_OWNER_PID, 0);
    if (udpSize > 0) {
        udpTable = (PMIB_UDPTABLE_OWNER_PID)HeapAlloc(GetProcessHeap(), 0, udpSize);
        if (udpTable) {
            if (GetExtendedUdpTable(udpTable, &udpSize, FALSE, AF_INET,
                                     UDP_TABLE_OWNER_PID, 0) == NO_ERROR) {
                for (i = 0; i < udpTable->dwNumEntries; i++) {
                    DWORD pid = udpTable->table[i].dwOwningPid;
                    for (j = 0; j < g_processCount; j++) {
                        if (g_processes[j].pid == pid) {
                            g_processes[j].udpConnections++;
                            break;
                        }
                    }
                }
            }
            HeapFree(GetProcessHeap(), 0, udpTable);
        }
    }
}

/* ========================================================================= */
/* SUSPEND / RESUME                                                          */
/* ========================================================================= */

typedef LONG (NTAPI *pfnNtSuspendProcess)(HANDLE);
typedef LONG (NTAPI *pfnNtResumeProcess)(HANDLE);

BOOL PM_SuspendProcess(DWORD pid) {
    HMODULE hNtdll;
    pfnNtSuspendProcess fn;
    HANDLE hProc;
    LONG status;

    if (PM_IsSystemProcess(pid)) return FALSE;
    hNtdll = GetModuleHandleW(L"ntdll.dll");
    if (!hNtdll) return FALSE;
    fn = (pfnNtSuspendProcess)GetProcAddress(hNtdll, "NtSuspendProcess");
    if (!fn) return FALSE;
    hProc = OpenProcess(PROCESS_SUSPEND_RESUME, FALSE, pid);
    if (!hProc) return FALSE;
    status = fn(hProc);
    CloseHandle(hProc);
    return (status == 0);
}

BOOL PM_ResumeProcess(DWORD pid) {
    HMODULE hNtdll;
    pfnNtResumeProcess fn;
    HANDLE hProc;
    LONG status;

    if (PM_IsSystemProcess(pid)) return FALSE;
    hNtdll = GetModuleHandleW(L"ntdll.dll");
    if (!hNtdll) return FALSE;
    fn = (pfnNtResumeProcess)GetProcAddress(hNtdll, "NtResumeProcess");
    if (!fn) return FALSE;
    hProc = OpenProcess(PROCESS_SUSPEND_RESUME, FALSE, pid);
    if (!hProc) return FALSE;
    status = fn(hProc);
    CloseHandle(hProc);
    return (status == 0);
}

/* ========================================================================= */
/* DLL LIST                                                                  */
/* ========================================================================= */

void PM_EnumProcessDlls(DWORD pid, HWND hParent) {
    HANDLE hProc;
    HMODULE hMods[1024];
    DWORD cbNeeded;
    int modCount, i, bufSize;
    wchar_t modName[MAX_PATH];
    wchar_t title[64];
    wchar_t *bigBuf;

    hProc = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, FALSE, pid);
    if (!hProc) {
        MessageBoxW(hParent, L"Cannot open process. Try running as administrator.", L"Error", MB_ICONERROR);
        return;
    }

    if (!EnumProcessModules(hProc, hMods, sizeof(hMods), &cbNeeded)) {
        CloseHandle(hProc);
        MessageBoxW(hParent, L"Cannot enumerate modules. Try running as administrator.", L"Error", MB_ICONERROR);
        return;
    }

    modCount = (int)(cbNeeded / sizeof(HMODULE));
    if (modCount > 1024) modCount = 1024;

    bufSize = (modCount + 1) * (MAX_PATH + 2);
    bigBuf = (wchar_t*)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, (SIZE_T)bufSize * sizeof(wchar_t));
    if (!bigBuf) { CloseHandle(hProc); return; }

    for (i = 0; i < modCount; i++) {
        modName[0] = 0;
        if (GetModuleFileNameExW(hProc, hMods[i], modName, MAX_PATH)) {
            tm_wcscat_s(bigBuf, bufSize, modName);
            tm_wcscat_s(bigBuf, bufSize, L"\r\n");
        }
    }
    CloseHandle(hProc);

    wsprintfW(title, L"DLLs for PID %u (%d modules)", pid, modCount);
    MessageBoxW(hParent, bigBuf, title, MB_OK | MB_ICONINFORMATION);
    HeapFree(GetProcessHeap(), 0, bigBuf);
}

/* ========================================================================= */
/* PROCESS TREE                                                              */
/* ========================================================================= */

void PM_ShowProcessTree(HWND hParent) {
    if (!CreateWindowExA(0, g_szProcessTreeClass, "Process Tree",
            WS_OVERLAPPEDWINDOW | WS_VISIBLE,
            CW_USEDEFAULT, CW_USEDEFAULT, 600, 500,
            NULL, NULL, g_hInst, NULL))
        MessageBoxA(NULL, "Failed to create Process Tree window!", "Error", MB_ICONERROR);
    (void)hParent;
}

/* ========================================================================= */
/* DISK I/O                                                                  */
/* ========================================================================= */

void PM_GatherDiskIO(void) {
    int i;
    for (i = 0; i < g_processCount; i++) {
        ProcessInfo *p = &g_processes[i];
        HANDLE hProc = OpenProcess(PROCESS_QUERY_INFORMATION, FALSE, p->pid);
        if (hProc) {
            IO_COUNTERS ioc;
            memset(&ioc, 0, sizeof(ioc));
            if (GetProcessIoCounters(hProc, &ioc)) {
                ULONGLONG newRead = ioc.ReadTransferCount;
                ULONGLONG newWrite = ioc.WriteTransferCount;
                if (p->ioReadBytes > 0) {
                    p->ioReadRate = (double)(newRead - p->ioReadBytes) / 2.0;
                    p->ioWriteRate = (double)(newWrite - p->ioWriteBytes) / 2.0;
                    if (p->ioReadRate < 0.0) p->ioReadRate = 0.0;
                    if (p->ioWriteRate < 0.0) p->ioWriteRate = 0.0;
                }
                p->ioReadBytes = newRead;
                p->ioWriteBytes = newWrite;
            }
            CloseHandle(hProc);
        }
    }
}

/* ========================================================================= */
/* GPU USAGE (D3DKMTQueryStatistics - Windows 10+)                           */
/* ========================================================================= */

/* Minimal structs for D3DKMT API - not available in all SDK versions */
typedef struct {
    UINT AdapterCount;
    struct {
        UINT hAdapter;
        LUID AdapterLuid;
        ULONG NumOfSources;
        BOOL bPrecisePresentRegionsPreferred;
    } Adapters[16];
} D3DKMT_ENUMADAPTERS_T;

#define D3DKMT_QS_ADAPTER    0
#define D3DKMT_QS_PROCESS    1
#define D3DKMT_QS_SEGMENT    5

/* Simplified - we just read the full result struct as raw bytes and extract what we need */
typedef LONG (WINAPI *PFN_D3DKMTEnumAdapters)(void*);
typedef LONG (WINAPI *PFN_D3DKMTQueryStatistics)(void*);

static PFN_D3DKMTEnumAdapters s_pfnEnumAdapters = NULL;
static PFN_D3DKMTQueryStatistics s_pfnQueryStatistics = NULL;
static BOOL s_gpuInitDone = FALSE;
static BOOL s_gpuAvailable = FALSE;
static UINT s_gpuAdapters[16];
static int s_gpuAdapterCount = 0;

static void GPU_Init(void) {
    HMODULE hGdi;
    s_gpuInitDone = TRUE;
    hGdi = GetModuleHandleW(L"gdi32.dll");
    if (!hGdi) return;
    s_pfnEnumAdapters = (PFN_D3DKMTEnumAdapters)GetProcAddress(hGdi, "D3DKMTEnumAdapters");
    s_pfnQueryStatistics = (PFN_D3DKMTQueryStatistics)GetProcAddress(hGdi, "D3DKMTQueryStatistics");
    if (s_pfnEnumAdapters) {
        D3DKMT_ENUMADAPTERS_T ea;
        UINT i;
        memset(&ea, 0, sizeof(ea));
        ea.AdapterCount = 16;
        if (s_pfnEnumAdapters(&ea) == 0) {
            for (i = 0; i < ea.AdapterCount && (int)i < 16; i++) {
                s_gpuAdapters[s_gpuAdapterCount++] = ea.Adapters[i].hAdapter;
            }
            if (s_gpuAdapterCount > 0 && s_pfnQueryStatistics)
                s_gpuAvailable = TRUE;
        }
    }
}

void PM_GatherGpuStats(void) {
    /* GPU usage via D3DKMT is complex and requires per-adapter/per-process
       node-level queries. For now, set to 0 - a full implementation would
       require D3DKMTQueryStatistics with PROCESS_SEGMENT type and running time
       delta calculation across all adapter nodes. */
    int i;
    if (!s_gpuInitDone) GPU_Init();
    /* Without a full D3DKMT implementation, GPU stays at 0.
       This placeholder allows the column to exist and can be filled
       in with NvAPI/ADL/D3DKMT data in a future update. */
    for (i = 0; i < g_processCount; i++) {
        g_processes[i].gpuUsage = 0.0;
    }
    (void)s_gpuAvailable;
}
