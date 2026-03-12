/* taskman.h - Unified header for TaskMan Enhanced (pure C, no CRT) */
#ifndef TASKMAN_H
#define TASKMAN_H

#include <windows.h>
#include <commctrl.h>
#include <tlhelp32.h>
#include <psapi.h>
#include <shellapi.h>
#include <shlobj.h>
#include <shlwapi.h>
#include <winsvc.h>
#include <wintrust.h>
#include <softpub.h>
#include <aclapi.h>
#include <accctrl.h>
#include <commdlg.h>
#include <windowsx.h>
#include <iphlpapi.h>

#pragma comment(lib, "iphlpapi.lib")

#pragma comment(lib, "kernel32.lib")
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "gdi32.lib")
#pragma comment(lib, "comctl32.lib")
#pragma comment(lib, "psapi.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "advapi32.lib")
#pragma comment(lib, "shlwapi.lib")
#pragma comment(lib, "version.lib")
#pragma comment(lib, "wintrust.lib")
#pragma comment(lib, "crypt32.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "oleaut32.lib")
#pragma comment(lib, "comdlg32.lib")
#pragma comment(lib, "taskschd.lib")
#pragma comment(lib, "uxtheme.lib")

#include <uxtheme.h>

/* ========================================================================= */
/* BUFFER SIZE CONSTANTS                                                     */
/* ========================================================================= */
#define TM_MAX_NAME      260
#define TM_MAX_PATH_BUF  1024
#define TM_MAX_DESC      512
#define TM_MAX_ARGS      1024
#define TM_MAX_VERSION   64
#define TM_MAX_REGKEY    512
#define TM_MAX_SOURCE    128
#define TM_MAX_TRIGGER   256
#define TM_MAX_FMT       128

/* ========================================================================= */
/* WINDOW / UI CONSTANTS                                                     */
/* ========================================================================= */
#define WIN_WIDTH   1200
#define WIN_HEIGHT  700
#define PANEL_WIDTH 200

/* Control IDs - Processes */
#define ID_LISTVIEW       1001
#define ID_REFRESH        1002
#define ID_TERMINATE      1003
#define ID_TERMINATE_ALL  1004
#define ID_SEARCH_GOOGLE  1005
#define ID_OPENFOLDER     1006
#define ID_OPEN_AUTORUNS  1007
#define ID_ABOUT          1008
#define ID_TASK_SCHEDULER 1009
#define ID_SUSPEND        1010
#define ID_RESUME         1011
#define ID_DLL_LIST       1012
#define ID_PROCESS_TREE   1013
#define TIMER_REFRESH     1
#define ID_PROC_FILTER        1020
#define ID_PROC_FILTER_LABEL  1021
#define ID_DARK_TOGGLE        1022

/* Control IDs - Autoruns */
#define ID_AR_LIST        2001
#define ID_AR_REFRESH     2002
#define ID_AR_REMOVE      2003
#define ID_AR_OPENFOLDER  2004
#define ID_AR_EXPORT      2005
#define ID_AR_ENABLE      2006
#define ID_AR_DISABLE     2007
#define ID_AR_PROPERTIES  2008
#define ID_AR_FORCE_DELETE 2009
#define ID_AR_FILTER        2010
#define ID_AR_SOURCE_COMBO  2011
#define ID_AR_CHK_ENABLED   2012
#define ID_AR_CHK_UNSIGNED  2013
#define ID_AR_UNDO          2014
#define ID_AR_FILTER_LABEL  2015
#define ID_AR_SOURCE_LABEL  2016

/* Undo action types */
#define UNDO_ACTION_NONE         0
#define UNDO_ACTION_DELETE       1
#define UNDO_ACTION_DISABLE      2
#define UNDO_ACTION_FORCE_DELETE 3
#define UNDO_MAX_ENTRIES         16

/* Control IDs - Task Scheduler */
#define ID_TS_LIST        3001
#define ID_TS_REFRESH     3002
#define ID_TS_CREATE      3003
#define ID_TS_DELETE      3004
#define ID_TS_ENABLE      3005
#define ID_TS_DISABLE     3006
#define ID_TS_RUN         3007
#define ID_TS_STOP        3008
#define ID_TS_PROPERTIES  3009
#define ID_TS_EXPORT      3010
#define ID_TS_IMPORT      3011

/* Create Task Dialog Controls */
#define ID_CTD_NAME           4001
#define ID_CTD_EXECUTABLE     4002
#define ID_CTD_BROWSE         4003
#define ID_CTD_ARGUMENTS      4004
#define ID_CTD_DESCRIPTION    4005
#define ID_CTD_TRIGGER_COMBO  4006
#define ID_CTD_DATE           4007
#define ID_CTD_TIME           4008
#define ID_CTD_ADMIN          4009
#define ID_CTD_OK             4010
#define ID_CTD_CANCEL         4011

/* System Tray */
#define WM_TRAYICON           (WM_APP + 1)
#define ID_TRAY_SHOW          5001
#define ID_TRAY_EXIT          5002
#define HOTKEY_SHOW           1

/* New features v3.1 */
#define ID_RESTART_ADMIN      6001
#define ID_SHOW_GRAPHS        6002
#define ID_NET_DETAIL         6003
#define ID_LANG_TOGGLE        6004
#define TIMER_GRAPH           3
#define GRAPH_HISTORY_SIZE    60

/* ========================================================================= */
/* COLUMN ENUMS                                                              */
/* ========================================================================= */
#define PROC_COL_NAME       0
#define PROC_COL_PID        1
#define PROC_COL_MEMORY     2
#define PROC_COL_CPU        3
#define PROC_COL_PATH       4
#define PROC_COL_NETWORK    5
#define PROC_COL_THREADS    6
#define PROC_COL_HANDLES    7
#define PROC_COL_GPU        8
#define PROC_COL_DISK_READ  9
#define PROC_COL_DISK_WRITE 10
#define PROC_COL_COUNT      11

#define AR_COL_ENABLED   0
#define AR_COL_NAME      1
#define AR_COL_DESCRIPTION 2
#define AR_COL_COMPANY   3
#define AR_COL_PATH      4
#define AR_COL_SOURCE    5
#define AR_COL_VERIFIED  6

#define TS_COL_NAME      0
#define TS_COL_STATUS    1
#define TS_COL_TRIGGER   2
#define TS_COL_LAST_RUN  3
#define TS_COL_NEXT_RUN  4
#define TS_COL_AUTHOR    5
#define TS_COL_PATH      6

/* ========================================================================= */
/* TASK STATE CONSTANTS (matches TASK_STATE enum from taskschd.h)             */
/* ========================================================================= */
#define TM_TASK_STATE_UNKNOWN   0
#define TM_TASK_STATE_DISABLED  1
#define TM_TASK_STATE_QUEUED    2
#define TM_TASK_STATE_READY     3
#define TM_TASK_STATE_RUNNING   4

/* ========================================================================= */
/* AUTORUN SOURCE CONSTANTS (replaces enum class AutorunSource)               */
/* ========================================================================= */
#define ARSRC_RegistryRunHKCU                  0
#define ARSRC_RegistryRunHKLM                  1
#define ARSRC_RegistryRunOnceHKCU              2
#define ARSRC_RegistryRunOnceHKLM              3
#define ARSRC_RegistryRunServicesHKCU           4
#define ARSRC_RegistryRunServicesHKLM           5
#define ARSRC_RegistryRunServicesOnceHKCU       6
#define ARSRC_RegistryRunServicesOnceHKLM       7
#define ARSRC_RegistryPoliciesRunHKCU           8
#define ARSRC_RegistryPoliciesRunHKLM           9
#define ARSRC_RegistryWinlogonUserinit         10
#define ARSRC_RegistryWinlogonShell            11
#define ARSRC_RegistryWinlogonVMApplet         12
#define ARSRC_RegistryWinlogonTaskman          13
#define ARSRC_RegistryWinlogonSystem           14
#define ARSRC_RegistryActiveSetup              15
#define ARSRC_RegistrySessionManagerBootExecute 16
#define ARSRC_RegistrySessionManagerSetupExecute 17
#define ARSRC_RegistryAppInitDLLs              18
#define ARSRC_RegistryImageFileExecutionOptions 19
#define ARSRC_RegistryShellServiceObjectDelayLoad 20
#define ARSRC_RegistryShellExtensions          21
#define ARSRC_RegistryContextMenuHandlers      22
#define ARSRC_RegistryBrowserHelperObjects     23
#define ARSRC_RegistryIEToolbar                24
#define ARSRC_RegistryIEExtensions             25
#define ARSRC_RegistryFontDrivers              26
#define ARSRC_RegistryKnownDLLs                27
#define ARSRC_RegistryPrintMonitors            28
#define ARSRC_RegistryNetworkProviders         29
#define ARSRC_RegistryLSAProviders             30
#define ARSRC_RegistryWinsockProviders         31
#define ARSRC_RegistryCodecs                   32
#define ARSRC_RegistryDirectShowFilters        33
#define ARSRC_StartupFolderUser                34
#define ARSRC_StartupFolderCommon              35
#define ARSRC_WindowsService                   36
#define ARSRC_ScheduledTask                    37
#define ARSRC_SystemProcess                    38
#define ARSRC_WMIEventConsumer                 39

/* ========================================================================= */
/* STRUCTURES                                                                */
/* ========================================================================= */

typedef struct {
    DWORD pid;
    wchar_t exeName[TM_MAX_NAME];
    wchar_t fullPath[TM_MAX_PATH_BUF];
    wchar_t description[TM_MAX_DESC];
    wchar_t company[TM_MAX_NAME];
    SIZE_T workingSetKB;
    double cpuUsage;
    HICON hIcon;
    BOOL verified;
    DWORD parentPid;
    int tcpConnections;
    int udpConnections;
    DWORD threadCount;
    DWORD handleCount;
    double gpuUsage;
    ULONGLONG ioReadBytes;
    ULONGLONG ioWriteBytes;
    double ioReadRate;
    double ioWriteRate;
} ProcessInfo;

typedef struct {
    DWORD pid;
    ULONGLONG kernelTime;
    ULONGLONG userTime;
    ULONGLONG snapshotTime;
} CpuSnapshot;

typedef struct {
    wchar_t name[TM_MAX_NAME];
    wchar_t fullPath[TM_MAX_PATH_BUF];
    wchar_t description[TM_MAX_DESC];
    wchar_t company[TM_MAX_NAME];
    wchar_t version[TM_MAX_VERSION];
    wchar_t arguments[TM_MAX_ARGS];
    int source;
    wchar_t sourceDetails[TM_MAX_SOURCE];
    wchar_t regKeyPath[TM_MAX_REGKEY];
    wchar_t regValueName[TM_MAX_NAME];
    wchar_t filePath[TM_MAX_PATH_BUF];
    BOOL enabled;
    BOOL verified;
    DWORD processId;
    FILETIME lastModified;
    DWORD fileSize;
} AutorunInfo;

typedef struct {
    AutorunInfo snapshot;
    int action;
} AutorunUndoEntry;

typedef struct {
    wchar_t name[TM_MAX_NAME];
    wchar_t path[TM_MAX_PATH_BUF];
    wchar_t description[TM_MAX_DESC];
    wchar_t author[TM_MAX_NAME];
    wchar_t executable[TM_MAX_PATH_BUF];
    wchar_t arguments[TM_MAX_ARGS];
    wchar_t workingDirectory[TM_MAX_PATH_BUF];
    BOOL enabled;
    BOOL hidden;
    int state;
    double lastRunTime;
    double nextRunTime;
    int triggerCount;
    wchar_t triggerDescription[TM_MAX_TRIGGER];
} ScheduledTaskInfo;

typedef struct {
    wchar_t name[TM_MAX_NAME];
    wchar_t executable[TM_MAX_PATH_BUF];
    wchar_t arguments[TM_MAX_ARGS];
    wchar_t description[TM_MAX_DESC];
    int triggerType;      /* 0=Once,1=Daily,2=Weekly,3=AtLogon,4=AtStartup */
    SYSTEMTIME schedTime;
    BOOL runAsAdmin;
    BOOL confirmed;
} CreateTaskParams;

/* ========================================================================= */
/* DYNAMIC ARRAY HELPER                                                      */
/* ========================================================================= */
#define DYNARRAY_GROW(arr, cnt, cap, type) do { \
    if ((cnt) >= (cap)) { \
        int _nc = (cap) == 0 ? 32 : (cap) * 2; \
        type *_ni = (type*)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, \
                                     (SIZE_T)_nc * sizeof(type)); \
        if ((arr) && (cnt) > 0) { \
            memcpy(_ni, (arr), (SIZE_T)(cnt) * sizeof(type)); \
            HeapFree(GetProcessHeap(), 0, (arr)); \
        } \
        (arr) = _ni; (cap) = _nc; \
    } \
} while(0)

#define DYNARRAY_FREE(arr, cnt, cap) do { \
    if (arr) { HeapFree(GetProcessHeap(), 0, (arr)); (arr) = NULL; } \
    (cnt) = 0; (cap) = 0; \
} while(0)

/* ========================================================================= */
/* GLOBAL VARIABLES (defined in main.c)                                      */
/* ========================================================================= */
extern HINSTANCE g_hInst;
extern const char *g_szMainClass;
extern const char *g_szAutorunClass;
extern const char *g_szTaskSchedulerClass;
extern const char *g_szProcessTreeClass;

extern HWND g_hMainWnd;
extern HWND g_hListView;
extern HWND g_hStatusBar;
extern HWND g_hAutorunWnd;
extern HWND g_hArListView;
extern HWND g_hArStatusBar;
extern HWND g_hTaskSchedulerWnd;
extern HWND g_hTsListView;
extern HWND g_hTsStatusBar;

extern ProcessInfo *g_processes;
extern int g_processCount;
extern int g_processCap;

extern AutorunInfo *g_autoruns;
extern int g_autorunCount;
extern int g_autorunCap;

extern ScheduledTaskInfo *g_tasks;
extern int g_taskCount;
extern int g_taskCap;

extern CpuSnapshot *g_cpuSnap;
extern int g_cpuSnapCount;
extern int g_cpuSnapCap;

extern BOOL g_procSortAsc[PROC_COL_COUNT];
extern int  g_procSortColumn;
extern BOOL g_arSortAsc[7];
extern int  g_arSortColumn;
extern BOOL g_tsSortAsc[7];
extern int  g_tsSortColumn;

extern wchar_t g_arFilter[TM_MAX_NAME];
extern BOOL g_showOnlyEnabled;
extern BOOL g_showOnlyUnsigned;
extern int g_arSourceFilter;
extern AutorunUndoEntry g_arUndoStack[UNDO_MAX_ENTRIES];
extern int g_arUndoCount;

extern wchar_t g_procFilter[TM_MAX_NAME];
extern BOOL g_darkTheme;
extern HBRUSH g_hDarkBgBrush;
extern HBRUSH g_hDarkEditBrush;
extern BOOL g_trayActive;
extern NOTIFYICONDATAW g_nid;
extern const char *g_szCreateTaskDlgClass;
extern const char *g_szGraphClass;
extern const char *g_szNetDetailClass;

extern HFONT g_hFont;
extern int g_currentDpi;
extern int g_langId;
extern wchar_t g_logFilePath[MAX_PATH];
extern double g_cpuHistory[GRAPH_HISTORY_SIZE];
extern double g_ramHistory[GRAPH_HISTORY_SIZE];
extern int g_graphIndex;
extern HWND g_hGraphWnd;

extern CRITICAL_SECTION g_dataLock;

/* ========================================================================= */
/* INLINE STRING HELPERS                                                     */
/* ========================================================================= */

static __inline wchar_t *tm_wcschr(const wchar_t *s, wchar_t c) {
    while (*s) { if (*s == c) return (wchar_t*)s; s++; }
    return (c == 0) ? (wchar_t*)s : NULL;
}

static __inline wchar_t *tm_wcsrchr(const wchar_t *s, wchar_t c) {
    const wchar_t *last = NULL;
    while (*s) { if (*s == c) last = s; s++; }
    return (wchar_t*)last;
}

static __inline void tm_wcscpy_s(wchar_t *dst, int dstChars, const wchar_t *src) {
    if (!src) { if (dstChars > 0) dst[0] = 0; return; }
    lstrcpynW(dst, src, dstChars);
}

static __inline void tm_wcscat_s(wchar_t *dst, int dstChars, const wchar_t *src) {
    int cur;
    if (!src) return;
    cur = lstrlenW(dst);
    if (cur < dstChars - 1)
        lstrcpynW(dst + cur, src, dstChars - cur);
}

static __inline void tm_format_double1(wchar_t *buf, int bufChars, double val) {
    int whole = (int)val;
    int frac = (int)((val - whole) * 10.0);
    if (frac < 0) frac = -frac;
    wsprintfW(buf, L"%d.%d", whole, frac);
    (void)bufChars;
}

/* Simple insertion sort */
typedef int (*tm_cmp_fn)(const void *a, const void *b);

static __inline void tm_sort(void *base, int count, int elemSize, tm_cmp_fn cmp) {
    unsigned char *arr, *temp;
    int i, j;
    if (count <= 1) return;
    arr = (unsigned char*)base;
    temp = (unsigned char*)HeapAlloc(GetProcessHeap(), 0, (SIZE_T)elemSize);
    if (!temp) return;
    for (i = 1; i < count; i++) {
        memcpy(temp, arr + (SIZE_T)i * elemSize, (SIZE_T)elemSize);
        j = i;
        while (j > 0 && cmp(arr + (SIZE_T)(j - 1) * elemSize, temp) > 0) {
            memcpy(arr + (SIZE_T)j * elemSize, arr + (SIZE_T)(j - 1) * elemSize, (SIZE_T)elemSize);
            j--;
        }
        memcpy(arr + (SIZE_T)j * elemSize, temp, (SIZE_T)elemSize);
    }
    HeapFree(GetProcessHeap(), 0, temp);
}

/* DPI scaling helper */
static __inline int ScaleDPI(int value, int dpi) { return MulDiv(value, dpi, 96); }

/* Check if autorun source is HKCU */
static __inline BOOL tm_is_hkcu_source(int src) {
    return (src == ARSRC_RegistryRunHKCU ||
            src == ARSRC_RegistryRunOnceHKCU ||
            src == ARSRC_RegistryRunServicesHKCU ||
            src == ARSRC_RegistryRunServicesOnceHKCU ||
            src == ARSRC_RegistryPoliciesRunHKCU);
}

/* ========================================================================= */
/* MODULE FUNCTION DECLARATIONS                                              */
/* ========================================================================= */

/* process.c */
void PM_EnumerateProcesses(void);
BOOL PM_TerminateProcessById(DWORD pid);
BOOL PM_IsSystemProcess(DWORD pid);
int  PM_GetProcessesByName(const wchar_t *name, DWORD *outPids, int maxPids);
int  PM_TerminateProcessesByName(const wchar_t *name);
HICON PM_GetProcessIcon(const wchar_t *pathOrName);
void PM_BuildCpuSnapshot(void);
void PM_ComputeCpuUsage(void);
void PM_RefreshProcessStats(void);
void PM_GatherNetworkStats(void);
BOOL PM_SuspendProcess(DWORD pid);
BOOL PM_ResumeProcess(DWORD pid);
void PM_EnumProcessDlls(DWORD pid, HWND hParent);
void PM_ShowProcessTree(HWND hParent);

/* fileinfo.c */
BOOL FI_GetFileVersionDetails(const wchar_t *path, wchar_t *desc, wchar_t *company, wchar_t *version);
BOOL FI_IsFileSigned(const wchar_t *path);
DWORD FI_GetFileSizeByPath(const wchar_t *path);
FILETIME FI_GetFileModifiedTime(const wchar_t *path);
HICON FI_GetFileIcon(const wchar_t *path);

/* autorun_scan.c */
void AS_ScanAll(void);
const wchar_t *AS_GetSourceDescription(int source);

/* autorun_mgr.c */
void AM_ExportToCSV(HWND hWnd);
void AM_ShowProperties(HWND hWnd, const AutorunInfo *ar);
BOOL AM_EnableDisableAutorun(const AutorunInfo *ar, BOOL enable);
BOOL AM_RemoveAutorun(const AutorunInfo *ar);
BOOL AM_ForceRemoveAutorun(const AutorunInfo *ar);
void AM_OpenFileLocation(HWND hWnd, const AutorunInfo *ar);

/* sorting.c */
void SortProcesses(void);
void SortAutoruns(void);
void SortTaskScheduler(void);
void OnProcessColumnClick(int column);
void OnAutorunColumnClick(int column);
void OnTaskSchedulerColumnClick(int column);
void UpdateProcessColumnHeaders(void);
void UpdateAutorunColumnHeaders(void);
void UpdateTaskSchedulerColumnHeaders(void);
void UpdateProcessList(void);
void UpdateAutorunList(void);
void UpdateTaskSchedulerList(void);

/* taskschd.c */
BOOL TS_Initialize(void);
void TS_Cleanup(void);
void TS_EnumerateAllTasks(void);
BOOL TS_CreateSimpleTask(const wchar_t *taskName, const wchar_t *executable,
                         const wchar_t *arguments, const wchar_t *description,
                         BOOL runAtStartup, BOOL runAsAdmin);
BOOL TS_DeleteTask(const wchar_t *taskPath);
BOOL TS_EnableTask(const wchar_t *taskPath, BOOL enable);
BOOL TS_RunTask(const wchar_t *taskPath);
BOOL TS_StopTask(const wchar_t *taskPath);
const wchar_t *TS_GetTaskStateString(int state);
void TS_FormatDate(double date, wchar_t *buf, int bufSize);
BOOL TS_IsTaskSystem(const wchar_t *taskPath);
BOOL TS_CreateTaskEx(const wchar_t *taskName, const wchar_t *executable,
                     const wchar_t *arguments, const wchar_t *description,
                     int triggerType, const SYSTEMTIME *schedTime, BOOL runAsAdmin);
BOOL TS_ExportTaskXml(const wchar_t *taskPath, const wchar_t *filePath);
BOOL TS_ImportTaskXml(const wchar_t *filePath);

/* process.c - extended */
void PM_GatherDiskIO(void);
void PM_GatherGpuStats(void);

/* main.c - logging */
void TM_LogAction(const wchar_t *action, const wchar_t *detail);

/* main.c (called from sorting.c) */
void PopulateProcessListView(void);
void PopulateAutorunListView(void);
void PopulateTaskSchedulerListView(void);

#endif /* TASKMAN_H */
