/* main.c - Entry point, globals, UI, window procedures (pure C, no CRT) */
#include "taskman.h"
#include "resource.h"

/* ========================================================================= */
/* GLOBAL VARIABLE DEFINITIONS                                               */
/* ========================================================================= */

HINSTANCE g_hInst = NULL;
const char *g_szMainClass = "TaskManagerEnhancedClass";
const char *g_szAutorunClass = "AutorunsEnhancedClass";
const char *g_szTaskSchedulerClass = "TaskSchedulerEnhancedClass";
const char *g_szProcessTreeClass = "ProcessTreeClass";

HWND g_hMainWnd = NULL;
HWND g_hListView = NULL;
HWND g_hStatusBar = NULL;
HWND g_hAutorunWnd = NULL;
HWND g_hArListView = NULL;
HWND g_hArStatusBar = NULL;
HWND g_hTaskSchedulerWnd = NULL;
HWND g_hTsListView = NULL;
HWND g_hTsStatusBar = NULL;

ProcessInfo *g_processes = NULL;
int g_processCount = 0;
int g_processCap = 0;

AutorunInfo *g_autoruns = NULL;
int g_autorunCount = 0;
int g_autorunCap = 0;

ScheduledTaskInfo *g_tasks = NULL;
int g_taskCount = 0;
int g_taskCap = 0;

BOOL g_procSortAsc[PROC_COL_COUNT] = {TRUE, TRUE, TRUE, TRUE, TRUE, TRUE, TRUE, TRUE, TRUE, TRUE, TRUE};

CpuSnapshot *g_cpuSnap = NULL;
int g_cpuSnapCount = 0;
int g_cpuSnapCap = 0;
int  g_procSortColumn = PROC_COL_NAME;
BOOL g_arSortAsc[7] = {TRUE, TRUE, TRUE, TRUE, TRUE, TRUE, TRUE};
int  g_arSortColumn = AR_COL_NAME;
BOOL g_tsSortAsc[7] = {TRUE, TRUE, TRUE, TRUE, TRUE, TRUE, TRUE};
int  g_tsSortColumn = TS_COL_NAME;

wchar_t g_arFilter[TM_MAX_NAME] = {0};
BOOL g_showOnlyEnabled = FALSE;
BOOL g_showOnlyUnsigned = FALSE;
int g_arSourceFilter = 0;
AutorunUndoEntry g_arUndoStack[UNDO_MAX_ENTRIES];
int g_arUndoCount = 0;

wchar_t g_procFilter[TM_MAX_NAME] = {0};
BOOL g_darkTheme = FALSE;
HBRUSH g_hDarkBgBrush = NULL;
HBRUSH g_hDarkEditBrush = NULL;
BOOL g_trayActive = FALSE;
NOTIFYICONDATAW g_nid;
const char *g_szCreateTaskDlgClass = "CreateTaskDlgClass";
const char *g_szGraphClass = "GraphWindowClass";
const char *g_szNetDetailClass = "NetDetailClass";
ULONGLONG g_prevIdleTime = 0, g_prevKernelTime = 0, g_prevUserTime = 0;
double g_systemCpuPercent = 0.0;
HFONT g_hFont = NULL;
int g_currentDpi = 96;
int g_langId = 0;
wchar_t g_logFilePath[MAX_PATH] = {0};
double g_cpuHistory[GRAPH_HISTORY_SIZE] = {0};
double g_ramHistory[GRAPH_HISTORY_SIZE] = {0};
int g_graphIndex = 0;
HWND g_hGraphWnd = NULL;

CRITICAL_SECTION g_dataLock;

/* ========================================================================= */
/* FORWARD DECLARATIONS                                                      */
/* ========================================================================= */

static LRESULT CALLBACK MainWndProc(HWND, UINT, WPARAM, LPARAM);
static LRESULT CALLBACK AutorunsWndProc(HWND, UINT, WPARAM, LPARAM);
static LRESULT CALLBACK TaskSchedulerWndProc(HWND, UINT, WPARAM, LPARAM);
static LRESULT CALLBACK ProcessTreeWndProc(HWND, UINT, WPARAM, LPARAM);
static LRESULT CALLBACK CreateTaskDlgProc(HWND, UINT, WPARAM, LPARAM);
static LRESULT CALLBACK GraphWndProc(HWND, UINT, WPARAM, LPARAM);
static LRESULT CALLBACK NetDetailWndProc(HWND, UINT, WPARAM, LPARAM);

static void CreateProcessListView(HWND hWnd);
static void CreateAutorunListView(HWND hWnd);
static void CreateTaskSchedulerListView(HWND hWnd);
static void RefreshProcessList(void);
static void RefreshAutorunList(void);
static void RefreshTaskSchedulerList(void);

/* ========================================================================= */
/* URL ENCODING HELPER                                                       */
/* ========================================================================= */

static void UrlEncode(const wchar_t *src, wchar_t *dst, int dstChars) {
    int pos = 0;
    dst[0] = 0;
    while (*src && pos < dstChars - 4) {
        switch (*src) {
            case L' ':  dst[pos++]=L'%'; dst[pos++]=L'2'; dst[pos++]=L'0'; break;
            case L'"':  dst[pos++]=L'%'; dst[pos++]=L'2'; dst[pos++]=L'2'; break;
            case L'&':  dst[pos++]=L'%'; dst[pos++]=L'2'; dst[pos++]=L'6'; break;
            case L'=':  dst[pos++]=L'%'; dst[pos++]=L'3'; dst[pos++]=L'D'; break;
            case L'?':  dst[pos++]=L'%'; dst[pos++]=L'3'; dst[pos++]=L'F'; break;
            case L'#':  dst[pos++]=L'%'; dst[pos++]=L'2'; dst[pos++]=L'3'; break;
            case L'+':  dst[pos++]=L'%'; dst[pos++]=L'2'; dst[pos++]=L'B'; break;
            default:    dst[pos++] = *src; break;
        }
        src++;
    }
    dst[pos] = 0;
}

/* ========================================================================= */
/* LOGGING                                                                   */
/* ========================================================================= */

void TM_LogAction(const wchar_t *action, const wchar_t *detail) {
    HANDLE hFile;
    SYSTEMTIME st;
    wchar_t line[512];
    char utf8[1024];
    int utf8Len;
    DWORD written;

    if (!g_logFilePath[0]) {
        wchar_t appData[MAX_PATH];
        if (SHGetFolderPathW(NULL, CSIDL_APPDATA, NULL, 0, appData) != S_OK) return;
        tm_wcscat_s(appData, MAX_PATH, L"\\TaskMan");
        CreateDirectoryW(appData, NULL);
        lstrcpynW(g_logFilePath, appData, MAX_PATH);
        tm_wcscat_s(g_logFilePath, MAX_PATH, L"\\taskman.log");
    }

    hFile = CreateFileW(g_logFilePath, GENERIC_WRITE, FILE_SHARE_READ, NULL,
                        OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (hFile == INVALID_HANDLE_VALUE) return;
    SetFilePointer(hFile, 0, NULL, FILE_END);

    GetLocalTime(&st);
    wsprintfW(line, L"[%04d-%02d-%02d %02d:%02d:%02d] %s: %s\r\n",
              st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond,
              action, detail ? detail : L"");

    utf8Len = WideCharToMultiByte(CP_UTF8, 0, line, -1, utf8, sizeof(utf8), NULL, NULL);
    if (utf8Len > 1) WriteFile(hFile, utf8, (DWORD)(utf8Len - 1), &written, NULL);
    CloseHandle(hFile);
}

/* ========================================================================= */
/* PRIVILEGES                                                                */
/* ========================================================================= */

static BOOL IsRunningAsAdmin(void) {
    HANDLE hToken;
    TOKEN_ELEVATION elev;
    DWORD size = sizeof(elev);
    BOOL result = FALSE;
    if (OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &hToken)) {
        if (GetTokenInformation(hToken, TokenElevation, &elev, sizeof(elev), &size))
            result = elev.TokenIsElevated;
        CloseHandle(hToken);
    }
    return result;
}

static void RestartAsAdmin(void) {
    wchar_t selfPath[MAX_PATH];
    GetModuleFileNameW(NULL, selfPath, MAX_PATH);
    ShellExecuteW(NULL, L"runas", selfPath, NULL, NULL, SW_SHOWNORMAL);
    ExitProcess(0);
}

/* ========================================================================= */
/* DPI SCALING                                                               */
/* ========================================================================= */

static HFONT CreateScaledFont(int dpi) {
    return CreateFontW(-MulDiv(9, dpi, 72), 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE,
        DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, DEFAULT_QUALITY,
        DEFAULT_PITCH | FF_DONTCARE, L"Segoe UI");
}

static BOOL CALLBACK SetFontCallback(HWND hChild, LPARAM lParam) {
    SendMessageW(hChild, WM_SETFONT, (WPARAM)lParam, TRUE);
    return TRUE;
}

static void ApplyDpiFont(HWND hWnd) {
    if (g_hFont) {
        EnumChildWindows(hWnd, SetFontCallback, (LPARAM)g_hFont);
    }
}

/* ========================================================================= */
/* MULTILINGUAL                                                              */
/* ========================================================================= */

static void LoadStr(UINT id, wchar_t *buf, int bufLen) {
    /* Load string for current language */
    LANGID langId = g_langId == 1 ? MAKELANGID(LANG_RUSSIAN, SUBLANG_DEFAULT)
                                  : MAKELANGID(LANG_ENGLISH, SUBLANG_ENGLISH_US);
    /* SetThreadUILanguage requires Win7+ */
    {
        typedef LANGID (WINAPI *PFN_SetThreadUILanguage)(LANGID);
        static PFN_SetThreadUILanguage pfn = NULL;
        static BOOL tried = FALSE;
        if (!tried) {
            HMODULE hK = GetModuleHandleW(L"kernel32.dll");
            if (hK) pfn = (PFN_SetThreadUILanguage)GetProcAddress(hK, "SetThreadUILanguage");
            tried = TRUE;
        }
        if (pfn) pfn(langId);
    }
    buf[0] = 0;
    LoadStringW(g_hInst, id, buf, bufLen);
}

static void RefreshAllButtonText(HWND hWnd) {
    wchar_t buf[128];
    struct { int id; UINT strId; } map[] = {
        {ID_REFRESH, IDS_REFRESH}, {ID_TERMINATE, IDS_TERMINATE}, {ID_TERMINATE_ALL, IDS_TERMINATE_ALL},
        {ID_SUSPEND, IDS_SUSPEND}, {ID_RESUME, IDS_RESUME}, {ID_DLL_LIST, IDS_DLL_LIST},
        {ID_PROCESS_TREE, IDS_PROCESS_TREE}, {ID_SEARCH_GOOGLE, IDS_SEARCH_GOOGLE},
        {ID_OPENFOLDER, IDS_OPEN_FOLDER}, {ID_OPEN_AUTORUNS, IDS_AUTORUNS},
        {ID_TASK_SCHEDULER, IDS_TASK_SCHEDULER}, {ID_ABOUT, IDS_ABOUT},
        {ID_DARK_TOGGLE, IDS_DARK_THEME}, {ID_SHOW_GRAPHS, IDS_GRAPHS},
        {ID_NET_DETAIL, IDS_NET_DETAIL}, {ID_RESTART_ADMIN, IDS_RUN_AS_ADMIN},
    };
    int i;
    for (i = 0; i < (int)(sizeof(map)/sizeof(map[0])); i++) {
        HWND hBtn = GetDlgItem(hWnd, map[i].id);
        if (hBtn) { LoadStr(map[i].strId, buf, 128); SetWindowTextW(hBtn, buf); }
    }
    /* Filter label */
    {
        HWND hLbl = GetDlgItem(hWnd, ID_PROC_FILTER_LABEL);
        if (hLbl) { LoadStr(IDS_FILTER, buf, 128); SetWindowTextW(hLbl, buf); }
    }
    /* Lang toggle shows opposite language name */
    {
        HWND hLang = GetDlgItem(hWnd, ID_LANG_TOGGLE);
        if (hLang) { LoadStr(IDS_LANG_TOGGLE, buf, 128); SetWindowTextW(hLang, buf); }
    }
}

static int GetWindowDpi(HWND hWnd) {
    typedef UINT (WINAPI *PFN_GetDpiForWindow)(HWND);
    static PFN_GetDpiForWindow pfn = NULL;
    static BOOL tried = FALSE;
    if (!tried) {
        HMODULE hUser = GetModuleHandleW(L"user32.dll");
        if (hUser) pfn = (PFN_GetDpiForWindow)GetProcAddress(hUser, "GetDpiForWindow");
        tried = TRUE;
    }
    if (pfn) return (int)pfn(hWnd);
    return 96;
}

/* ========================================================================= */
/* POPULATE LIST VIEWS (called from sorting.c)                               */
/* ========================================================================= */

void PopulateProcessListView(void) {
    HIMAGELIST hImgList;
    LVITEMW lvItem;
    int i, insertIdx;
    wchar_t buf[64];

    if (!g_hListView) return;
    ListView_DeleteAllItems(g_hListView);

    /* Build image list only for matching processes */
    hImgList = ImageList_Create(16, 16, ILC_COLOR32 | ILC_MASK, g_processCount, 1);
    for (i = 0; i < g_processCount; i++) {
        HICON hIcon;
        if (g_procFilter[0] && !StrStrIW(g_processes[i].exeName, g_procFilter)
                            && !StrStrIW(g_processes[i].fullPath, g_procFilter))
            continue;
        hIcon = g_processes[i].hIcon;
        if (!hIcon) hIcon = LoadIconW(NULL, IDI_APPLICATION);
        ImageList_AddIcon(hImgList, hIcon);
    }
    ListView_SetImageList(g_hListView, hImgList, LVSIL_SMALL);

    memset(&lvItem, 0, sizeof(lvItem));
    lvItem.mask = LVIF_TEXT | LVIF_IMAGE;
    insertIdx = 0;

    for (i = 0; i < g_processCount; i++) {
        ProcessInfo *p = &g_processes[i];
        int idx;
        LVITEMW lvSub;

        if (g_procFilter[0] && !StrStrIW(p->exeName, g_procFilter)
                            && !StrStrIW(p->fullPath, g_procFilter))
            continue;

        lvItem.iItem = insertIdx;
        lvItem.iSubItem = PROC_COL_NAME;
        lvItem.iImage = insertIdx;
        lvItem.pszText = p->exeName;
        idx = ListView_InsertItem(g_hListView, &lvItem);

        memset(&lvSub, 0, sizeof(lvSub));
        lvSub.mask = LVIF_TEXT;
        lvSub.iItem = idx;

        wsprintfW(buf, L"%u", p->pid);
        lvSub.iSubItem = PROC_COL_PID;
        lvSub.pszText = buf;
        ListView_SetItem(g_hListView, &lvSub);

        wsprintfW(buf, L"%u", (unsigned)p->workingSetKB);
        lvSub.iSubItem = PROC_COL_MEMORY;
        lvSub.pszText = buf;
        ListView_SetItem(g_hListView, &lvSub);

        tm_format_double1(buf, 64, p->cpuUsage);
        lvSub.iSubItem = PROC_COL_CPU;
        lvSub.pszText = buf;
        ListView_SetItem(g_hListView, &lvSub);

        lvSub.iSubItem = PROC_COL_PATH;
        lvSub.pszText = p->fullPath;
        ListView_SetItem(g_hListView, &lvSub);

        wsprintfW(buf, L"T:%d U:%d", p->tcpConnections, p->udpConnections);
        lvSub.iSubItem = PROC_COL_NETWORK;
        lvSub.pszText = buf;
        ListView_SetItem(g_hListView, &lvSub);

        wsprintfW(buf, L"%u", p->threadCount);
        lvSub.iSubItem = PROC_COL_THREADS;
        lvSub.pszText = buf;
        ListView_SetItem(g_hListView, &lvSub);

        wsprintfW(buf, L"%u", p->handleCount);
        lvSub.iSubItem = PROC_COL_HANDLES;
        lvSub.pszText = buf;
        ListView_SetItem(g_hListView, &lvSub);

        tm_format_double1(buf, 64, p->gpuUsage);
        lvSub.iSubItem = PROC_COL_GPU;
        lvSub.pszText = buf;
        ListView_SetItem(g_hListView, &lvSub);

        /* Disk I/O rates - format as KB/s */
        {
            DWORD rdKB = (DWORD)(p->ioReadRate / 1024.0);
            DWORD wrKB = (DWORD)(p->ioWriteRate / 1024.0);
            wsprintfW(buf, L"%u KB/s", rdKB);
            lvSub.iSubItem = PROC_COL_DISK_READ;
            lvSub.pszText = buf;
            ListView_SetItem(g_hListView, &lvSub);

            wsprintfW(buf, L"%u KB/s", wrKB);
            lvSub.iSubItem = PROC_COL_DISK_WRITE;
            lvSub.pszText = buf;
            ListView_SetItem(g_hListView, &lvSub);
        }

        insertIdx++;
    }
}

/* ========================================================================= */
/* SOURCE CATEGORY FILTER                                                    */
/* ========================================================================= */

static BOOL SourceMatchesCategory(int source, int category) {
    switch (category) {
    case 0: return TRUE;                                      /* All */
    case 1: return (source >= 0 && source <= 9);              /* Registry Run Keys */
    case 2: return (source >= 10 && source <= 14);            /* Winlogon */
    case 3: return (source >= 15 && source <= 19);            /* System Keys */
    case 4: return (source >= 20 && source <= 25);            /* Shell & Browser */
    case 5: return (source >= 26 && source <= 33);            /* Drivers & Codecs */
    case 6: return (source >= 34 && source <= 35);            /* Startup Folders */
    case 7: return (source == 36);                            /* Services */
    case 8: return (source == 37);                            /* Scheduled Tasks */
    case 9: return (source >= 38);                            /* Other */
    default: return TRUE;
    }
}

/* ========================================================================= */
/* UNDO SYSTEM                                                               */
/* ========================================================================= */

static void AR_PushUndo(const AutorunInfo *ar, int action) {
    if (g_arUndoCount < UNDO_MAX_ENTRIES)
        g_arUndoCount++;
    /* Shift stack down to make room at index 0 */
    if (g_arUndoCount > 1) {
        int i;
        for (i = g_arUndoCount - 1; i > 0; i--)
            g_arUndoStack[i] = g_arUndoStack[i - 1];
    }
    memcpy(&g_arUndoStack[0].snapshot, ar, sizeof(AutorunInfo));
    g_arUndoStack[0].action = action;
}

static void AR_PerformUndo(HWND hParent) {
    AutorunUndoEntry *entry;
    AutorunInfo *ar;
    BOOL success = FALSE;

    if (g_arUndoCount <= 0) {
        MessageBoxW(hParent, L"Nothing to undo.", L"Undo", MB_ICONINFORMATION);
        return;
    }

    entry = &g_arUndoStack[0];
    ar = &entry->snapshot;

    switch (entry->action) {
    case UNDO_ACTION_DISABLE:
        /* Re-enable the entry */
        success = AM_EnableDisableAutorun(ar, TRUE);
        if (success)
            MessageBoxW(hParent, L"Entry re-enabled successfully.", L"Undo", MB_ICONINFORMATION);
        else
            MessageBoxW(hParent, L"Failed to re-enable entry.", L"Undo", MB_ICONERROR);
        break;

    case UNDO_ACTION_DELETE:
    case UNDO_ACTION_FORCE_DELETE:
        /* Try to restore registry value */
        if (ar->source <= 33 && ar->regKeyPath[0] && ar->regValueName[0]) {
            HKEY hKey;
            wchar_t cmdLine[TM_MAX_PATH_BUF + TM_MAX_ARGS];
            LONG res;

            /* Build the command line value from fullPath + arguments */
            lstrcpynW(cmdLine, ar->fullPath, TM_MAX_PATH_BUF + TM_MAX_ARGS);
            if (ar->arguments[0]) {
                tm_wcscat_s(cmdLine, TM_MAX_PATH_BUF + TM_MAX_ARGS, L" ");
                tm_wcscat_s(cmdLine, TM_MAX_PATH_BUF + TM_MAX_ARGS, ar->arguments);
            }

            res = RegCreateKeyExW(
                tm_is_hkcu_source(ar->source) ? HKEY_CURRENT_USER : HKEY_LOCAL_MACHINE,
                ar->regKeyPath, 0, NULL, 0, KEY_SET_VALUE, NULL, &hKey, NULL);
            if (res == ERROR_SUCCESS) {
                res = RegSetValueExW(hKey, ar->regValueName, 0, REG_SZ,
                    (const BYTE *)cmdLine, (lstrlenW(cmdLine) + 1) * sizeof(wchar_t));
                RegCloseKey(hKey);
                success = (res == ERROR_SUCCESS);
            }
            if (success)
                MessageBoxW(hParent, L"Registry entry restored. File may still be missing if it was deleted.",
                            L"Undo", MB_ICONINFORMATION);
            else
                MessageBoxW(hParent, L"Failed to restore registry entry.", L"Undo", MB_ICONERROR);
        } else if (ar->source == ARSRC_WindowsService) {
            success = AM_EnableDisableAutorun(ar, TRUE);
            if (success)
                MessageBoxW(hParent, L"Service re-enabled.", L"Undo", MB_ICONINFORMATION);
            else
                MessageBoxW(hParent, L"Failed to restore service.", L"Undo", MB_ICONERROR);
        } else {
            MessageBoxW(hParent, L"Cannot undo deletion for this source type.", L"Undo", MB_ICONWARNING);
        }
        break;

    default:
        MessageBoxW(hParent, L"Unknown undo action.", L"Undo", MB_ICONWARNING);
        break;
    }

    /* Remove the top entry from the stack */
    {
        int i;
        for (i = 0; i < g_arUndoCount - 1; i++)
            g_arUndoStack[i] = g_arUndoStack[i + 1];
        g_arUndoCount--;
    }
}

/* ========================================================================= */
/* AUTORUN FILTER                                                            */
/* ========================================================================= */

static BOOL PassesFilter(const AutorunInfo *ar) {
    if (g_arFilter[0]) {
        if (!StrStrIW(ar->name, g_arFilter) &&
            !StrStrIW(ar->description, g_arFilter) &&
            !StrStrIW(ar->company, g_arFilter))
            return FALSE;
    }
    if (g_showOnlyEnabled && !ar->enabled) return FALSE;
    if (g_showOnlyUnsigned && ar->verified) return FALSE;
    if (g_arSourceFilter > 0 && !SourceMatchesCategory(ar->source, g_arSourceFilter)) return FALSE;
    return TRUE;
}

void PopulateAutorunListView(void) {
    LVITEMW lvItem;
    int i, displayIndex = 0;

    if (!g_hArListView) return;
    ListView_DeleteAllItems(g_hArListView);

    memset(&lvItem, 0, sizeof(lvItem));
    lvItem.mask = LVIF_TEXT | LVIF_STATE | LVIF_PARAM;

    for (i = 0; i < g_autorunCount; i++) {
        AutorunInfo *ar = &g_autoruns[i];
        int idx;
        LVITEMW lvSub;

        if (!PassesFilter(ar)) continue;

        lvItem.iItem = displayIndex;
        lvItem.iSubItem = AR_COL_ENABLED;
        lvItem.stateMask = LVIS_STATEIMAGEMASK;
        lvItem.state = INDEXTOSTATEIMAGEMASK(ar->enabled ? 2 : 1);
        lvItem.pszText = (LPWSTR)L"";
        lvItem.lParam = i;
        idx = ListView_InsertItem(g_hArListView, &lvItem);

        memset(&lvSub, 0, sizeof(lvSub));
        lvSub.mask = LVIF_TEXT;
        lvSub.iItem = idx;

        lvSub.iSubItem = AR_COL_NAME;
        lvSub.pszText = ar->name;
        ListView_SetItem(g_hArListView, &lvSub);

        lvSub.iSubItem = AR_COL_DESCRIPTION;
        lvSub.pszText = ar->description;
        ListView_SetItem(g_hArListView, &lvSub);

        lvSub.iSubItem = AR_COL_COMPANY;
        lvSub.pszText = ar->company;
        ListView_SetItem(g_hArListView, &lvSub);

        lvSub.iSubItem = AR_COL_PATH;
        lvSub.pszText = ar->fullPath;
        ListView_SetItem(g_hArListView, &lvSub);

        lvSub.iSubItem = AR_COL_SOURCE;
        lvSub.pszText = ar->sourceDetails;
        ListView_SetItem(g_hArListView, &lvSub);

        lvSub.iSubItem = AR_COL_VERIFIED;
        lvSub.pszText = (LPWSTR)(ar->verified ? L"Yes" : L"No");
        ListView_SetItem(g_hArListView, &lvSub);

        displayIndex++;
    }
}

void PopulateTaskSchedulerListView(void) {
    LVITEMW lvItem;
    int i;
    wchar_t statusBuf[64], lastBuf[64], nextBuf[64];

    if (!g_hTsListView) return;
    ListView_DeleteAllItems(g_hTsListView);

    memset(&lvItem, 0, sizeof(lvItem));
    lvItem.mask = LVIF_TEXT;

    for (i = 0; i < g_taskCount; i++) {
        ScheduledTaskInfo *t = &g_tasks[i];
        int idx;
        LVITEMW lvSub;

        lvItem.iItem = i;
        lvItem.iSubItem = TS_COL_NAME;
        lvItem.pszText = t->name;
        idx = ListView_InsertItem(g_hTsListView, &lvItem);

        memset(&lvSub, 0, sizeof(lvSub));
        lvSub.mask = LVIF_TEXT;
        lvSub.iItem = idx;

        lstrcpynW(statusBuf, TS_GetTaskStateString(t->state), 64);
        if (!t->enabled) tm_wcscat_s(statusBuf, 64, L" (Disabled)");
        lvSub.iSubItem = TS_COL_STATUS;
        lvSub.pszText = statusBuf;
        ListView_SetItem(g_hTsListView, &lvSub);

        lvSub.iSubItem = TS_COL_TRIGGER;
        lvSub.pszText = t->triggerDescription;
        ListView_SetItem(g_hTsListView, &lvSub);

        TS_FormatDate(t->lastRunTime, lastBuf, 64);
        lvSub.iSubItem = TS_COL_LAST_RUN;
        lvSub.pszText = lastBuf;
        ListView_SetItem(g_hTsListView, &lvSub);

        TS_FormatDate(t->nextRunTime, nextBuf, 64);
        lvSub.iSubItem = TS_COL_NEXT_RUN;
        lvSub.pszText = nextBuf;
        ListView_SetItem(g_hTsListView, &lvSub);

        lvSub.iSubItem = TS_COL_AUTHOR;
        lvSub.pszText = t->author;
        ListView_SetItem(g_hTsListView, &lvSub);

        lvSub.iSubItem = TS_COL_PATH;
        lvSub.pszText = t->path;
        ListView_SetItem(g_hTsListView, &lvSub);
    }
}

/* ========================================================================= */
/* LIST VIEW CREATION                                                        */
/* ========================================================================= */

static void CreateProcessListView(HWND hWnd) {
    LVCOLUMNW col;
    g_hListView = CreateWindowExW(WS_EX_CLIENTEDGE, WC_LISTVIEWW, L"",
        WS_CHILD | WS_VISIBLE | LVS_REPORT | LVS_SINGLESEL,
        0, 0, 0, 0, hWnd, (HMENU)ID_LISTVIEW, g_hInst, NULL);
    ListView_SetExtendedListViewStyle(g_hListView, LVS_EX_FULLROWSELECT | LVS_EX_GRIDLINES);

    memset(&col, 0, sizeof(col));
    col.mask = LVCF_WIDTH | LVCF_TEXT | LVCF_SUBITEM;

    col.iSubItem = PROC_COL_NAME;  col.cx = 200; col.pszText = (LPWSTR)L"Process Name";
    ListView_InsertColumn(g_hListView, PROC_COL_NAME, &col);
    col.iSubItem = PROC_COL_PID;   col.cx = 80;  col.pszText = (LPWSTR)L"PID";
    ListView_InsertColumn(g_hListView, PROC_COL_PID, &col);
    col.iSubItem = PROC_COL_MEMORY;col.cx = 100; col.pszText = (LPWSTR)L"Memory (KB)";
    ListView_InsertColumn(g_hListView, PROC_COL_MEMORY, &col);
    col.iSubItem = PROC_COL_CPU;   col.cx = 80;  col.pszText = (LPWSTR)L"CPU %";
    ListView_InsertColumn(g_hListView, PROC_COL_CPU, &col);
    col.iSubItem = PROC_COL_PATH;  col.cx = 300; col.pszText = (LPWSTR)L"Path";
    ListView_InsertColumn(g_hListView, PROC_COL_PATH, &col);
    col.iSubItem = PROC_COL_NETWORK; col.cx = 100; col.pszText = (LPWSTR)L"Network";
    ListView_InsertColumn(g_hListView, PROC_COL_NETWORK, &col);
    col.iSubItem = PROC_COL_THREADS; col.cx = 70;  col.pszText = (LPWSTR)L"Threads";
    ListView_InsertColumn(g_hListView, PROC_COL_THREADS, &col);
    col.iSubItem = PROC_COL_HANDLES; col.cx = 70;  col.pszText = (LPWSTR)L"Handles";
    ListView_InsertColumn(g_hListView, PROC_COL_HANDLES, &col);
    col.iSubItem = PROC_COL_GPU;       col.cx = 70;  col.pszText = (LPWSTR)L"GPU %";
    ListView_InsertColumn(g_hListView, PROC_COL_GPU, &col);
    col.iSubItem = PROC_COL_DISK_READ; col.cx = 90;  col.pszText = (LPWSTR)L"Disk Read/s";
    ListView_InsertColumn(g_hListView, PROC_COL_DISK_READ, &col);
    col.iSubItem = PROC_COL_DISK_WRITE;col.cx = 90;  col.pszText = (LPWSTR)L"Disk Write/s";
    ListView_InsertColumn(g_hListView, PROC_COL_DISK_WRITE, &col);
}

static void CreateAutorunListView(HWND hWnd) {
    LVCOLUMNW col;
    g_hArListView = CreateWindowExW(WS_EX_CLIENTEDGE, WC_LISTVIEWW, L"",
        WS_CHILD | WS_VISIBLE | LVS_REPORT | LVS_SINGLESEL,
        0, 0, 0, 0, hWnd, (HMENU)ID_AR_LIST, g_hInst, NULL);
    ListView_SetExtendedListViewStyle(g_hArListView,
        LVS_EX_FULLROWSELECT | LVS_EX_GRIDLINES | LVS_EX_CHECKBOXES);

    memset(&col, 0, sizeof(col));
    col.mask = LVCF_WIDTH | LVCF_TEXT | LVCF_SUBITEM;

    col.iSubItem = AR_COL_ENABLED;    col.cx = 60;  col.pszText = (LPWSTR)L"Enable";
    ListView_InsertColumn(g_hArListView, AR_COL_ENABLED, &col);
    col.iSubItem = AR_COL_NAME;       col.cx = 180; col.pszText = (LPWSTR)L"Name";
    ListView_InsertColumn(g_hArListView, AR_COL_NAME, &col);
    col.iSubItem = AR_COL_DESCRIPTION;col.cx = 250; col.pszText = (LPWSTR)L"Description";
    ListView_InsertColumn(g_hArListView, AR_COL_DESCRIPTION, &col);
    col.iSubItem = AR_COL_COMPANY;    col.cx = 150; col.pszText = (LPWSTR)L"Company";
    ListView_InsertColumn(g_hArListView, AR_COL_COMPANY, &col);
    col.iSubItem = AR_COL_PATH;       col.cx = 300; col.pszText = (LPWSTR)L"Path";
    ListView_InsertColumn(g_hArListView, AR_COL_PATH, &col);
    col.iSubItem = AR_COL_SOURCE;     col.cx = 120; col.pszText = (LPWSTR)L"Source";
    ListView_InsertColumn(g_hArListView, AR_COL_SOURCE, &col);
    col.iSubItem = AR_COL_VERIFIED;   col.cx = 80;  col.pszText = (LPWSTR)L"Verified";
    ListView_InsertColumn(g_hArListView, AR_COL_VERIFIED, &col);
}

static void CreateTaskSchedulerListView(HWND hWnd) {
    LVCOLUMNW col;
    g_hTsListView = CreateWindowExW(WS_EX_CLIENTEDGE, WC_LISTVIEWW, L"",
        WS_CHILD | WS_VISIBLE | LVS_REPORT | LVS_SINGLESEL,
        0, 0, 0, 0, hWnd, (HMENU)ID_TS_LIST, g_hInst, NULL);
    ListView_SetExtendedListViewStyle(g_hTsListView, LVS_EX_FULLROWSELECT | LVS_EX_GRIDLINES);

    memset(&col, 0, sizeof(col));
    col.mask = LVCF_WIDTH | LVCF_TEXT | LVCF_SUBITEM;

    col.iSubItem = TS_COL_NAME;     col.cx = 220; col.pszText = (LPWSTR)L"Task Name";
    ListView_InsertColumn(g_hTsListView, TS_COL_NAME, &col);
    col.iSubItem = TS_COL_STATUS;   col.cx = 100; col.pszText = (LPWSTR)L"Status";
    ListView_InsertColumn(g_hTsListView, TS_COL_STATUS, &col);
    col.iSubItem = TS_COL_TRIGGER;  col.cx = 150; col.pszText = (LPWSTR)L"Trigger";
    ListView_InsertColumn(g_hTsListView, TS_COL_TRIGGER, &col);
    col.iSubItem = TS_COL_LAST_RUN; col.cx = 150; col.pszText = (LPWSTR)L"Last Run";
    ListView_InsertColumn(g_hTsListView, TS_COL_LAST_RUN, &col);
    col.iSubItem = TS_COL_NEXT_RUN; col.cx = 150; col.pszText = (LPWSTR)L"Next Run";
    ListView_InsertColumn(g_hTsListView, TS_COL_NEXT_RUN, &col);
    col.iSubItem = TS_COL_AUTHOR;   col.cx = 120; col.pszText = (LPWSTR)L"Author";
    ListView_InsertColumn(g_hTsListView, TS_COL_AUTHOR, &col);
    col.iSubItem = TS_COL_PATH;     col.cx = 300; col.pszText = (LPWSTR)L"Task Path";
    ListView_InsertColumn(g_hTsListView, TS_COL_PATH, &col);
}

/* ========================================================================= */
/* REFRESH FUNCTIONS                                                         */
/* ========================================================================= */

static void RefreshProcessList(void) {
    wchar_t status[64];
    SetCursor(LoadCursorW(NULL, IDC_WAIT));
    EnterCriticalSection(&g_dataLock);
    PM_EnumerateProcesses();
    LeaveCriticalSection(&g_dataLock);
    PopulateProcessListView();
    SetCursor(LoadCursorW(NULL, IDC_ARROW));
    if (g_hStatusBar) {
        wsprintfW(status, L"Processes: %d", g_processCount);
        SendMessageW(g_hStatusBar, SB_SETTEXTW, 0, (LPARAM)status);
    }
}

static void RefreshAutorunList(void) {
    wchar_t status[64];
    SetCursor(LoadCursorW(NULL, IDC_WAIT));
    EnterCriticalSection(&g_dataLock);
    AS_ScanAll();
    LeaveCriticalSection(&g_dataLock);
    PopulateAutorunListView();
    SetCursor(LoadCursorW(NULL, IDC_ARROW));
    if (g_hArStatusBar) {
        wsprintfW(status, L"Found autoruns: %d", g_autorunCount);
        SendMessageW(g_hArStatusBar, SB_SETTEXTW, 0, (LPARAM)status);
    }
}

static void RefreshTaskSchedulerList(void) {
    wchar_t status[64];
    SetCursor(LoadCursorW(NULL, IDC_WAIT));
    EnterCriticalSection(&g_dataLock);
    TS_EnumerateAllTasks();
    LeaveCriticalSection(&g_dataLock);
    PopulateTaskSchedulerListView();
    SetCursor(LoadCursorW(NULL, IDC_ARROW));
    if (g_hTsStatusBar) {
        wsprintfW(status, L"Tasks found: %d", g_taskCount);
        SendMessageW(g_hTsStatusBar, SB_SETTEXTW, 0, (LPARAM)status);
    }
}

/* ========================================================================= */
/* THREAD WRAPPERS                                                           */
/* ========================================================================= */

static DWORD WINAPI AutorunRefreshThread(LPVOID p) {
    (void)p;
    RefreshAutorunList();
    return 0;
}

static DWORD WINAPI TaskSchedulerRefreshThread(LPVOID p) {
    (void)p;
    RefreshTaskSchedulerList();
    return 0;
}

static void StartAutorunRefresh(void) {
    HANDLE h = CreateThread(NULL, 0, AutorunRefreshThread, NULL, 0, NULL);
    if (h) CloseHandle(h);
}

static void StartTaskSchedulerRefresh(void) {
    HANDLE h = CreateThread(NULL, 0, TaskSchedulerRefreshThread, NULL, 0, NULL);
    if (h) CloseHandle(h);
}

/* ========================================================================= */
/* SELECTION HELPERS                                                         */
/* ========================================================================= */

static DWORD GetSelectedProcessPID(void) {
    int sel = ListView_GetNextItem(g_hListView, -1, LVNI_SELECTED);
    if (sel < 0 || sel >= g_processCount) return 0;
    return g_processes[sel].pid;
}

static int GetSelectedAutorunIndex(void) {
    LVITEMW lvi;
    int sel = ListView_GetNextItem(g_hArListView, -1, LVNI_SELECTED);
    if (sel < 0) return -1;
    memset(&lvi, 0, sizeof(lvi));
    lvi.iItem = sel;
    lvi.mask = LVIF_PARAM;
    if (ListView_GetItem(g_hArListView, &lvi))
        return (int)lvi.lParam;
    return -1;
}

static int GetSelectedTaskIndex(void) {
    int sel = ListView_GetNextItem(g_hTsListView, -1, LVNI_SELECTED);
    if (sel < 0 || sel >= g_taskCount) return -1;
    return sel;
}

/* ========================================================================= */
/* PROCESS ACTIONS                                                           */
/* ========================================================================= */

static void TerminateSelectedProcess(void) {
    wchar_t msg[300];
    int sel;
    DWORD pid = GetSelectedProcessPID();
    if (pid == 0) {
        MessageBoxW(g_hMainWnd, L"Please select a process to terminate.", L"Error", MB_ICONWARNING);
        return;
    }
    wsprintfW(msg, L"Are you sure you want to terminate process with PID %u?", pid);
    if (MessageBoxW(g_hMainWnd, msg, L"Confirmation", MB_YESNO | MB_ICONQUESTION) == IDYES) {
        sel = ListView_GetNextItem(g_hListView, -1, LVNI_SELECTED);
        if (PM_TerminateProcessById(pid)) {
            wsprintfW(msg, L"PID %u (%s)", pid, (sel >= 0 && sel < g_processCount) ? g_processes[sel].exeName : L"?");
            TM_LogAction(L"TERMINATE", msg);
            RefreshProcessList();
        } else {
            MessageBoxW(g_hMainWnd, L"Failed to terminate process. Insufficient privileges or process is protected.",
                        L"Error", MB_ICONERROR);
        }
    }
}

static void TerminateAllProcessesByName(void) {
    int sel, count, nonSystem, i, terminated;
    DWORD pids[4096];
    wchar_t msg[512];

    sel = ListView_GetNextItem(g_hListView, -1, LVNI_SELECTED);
    if (sel < 0 || sel >= g_processCount) {
        MessageBoxW(g_hMainWnd, L"Please select a process to terminate all instances.", L"Error", MB_ICONWARNING);
        return;
    }

    count = PM_GetProcessesByName(g_processes[sel].exeName, pids, 4096);
    if (count == 0) {
        MessageBoxW(g_hMainWnd, L"No processes found with this name.", L"Information", MB_ICONINFORMATION);
        return;
    }

    nonSystem = 0;
    for (i = 0; i < count; i++) {
        if (!PM_IsSystemProcess(pids[i])) nonSystem++;
    }
    if (nonSystem == 0) {
        MessageBoxW(g_hMainWnd, L"All processes with this name are system processes and cannot be terminated.",
                    L"Warning", MB_ICONWARNING);
        return;
    }

    wsprintfW(msg, L"Are you sure you want to terminate ALL %d process(es) with name '%s'?\n\n"
              L"Warning: This will terminate ALL instances of this process!",
              nonSystem, g_processes[sel].exeName);
    if (MessageBoxW(g_hMainWnd, msg, L"Confirm Termination of All Processes",
                    MB_YESNO | MB_ICONEXCLAMATION | MB_DEFBUTTON2) == IDYES) {
        terminated = PM_TerminateProcessesByName(g_processes[sel].exeName);
        { wchar_t logMsg[300]; wsprintfW(logMsg, L"%s (%d killed)", g_processes[sel].exeName, terminated); TM_LogAction(L"TERMINATE_ALL", logMsg); }
        RefreshProcessList();
        wsprintfW(msg, L"Successfully terminated %d process(es) with name '%s'.",
                  terminated, g_processes[sel].exeName);
        if (terminated < nonSystem)
            tm_wcscat_s(msg, 512, L"\n\nSome processes could not be terminated due to insufficient privileges or protection.");
        MessageBoxW(g_hMainWnd, msg, L"Operation Complete", MB_ICONINFORMATION);
    }
}

static void SearchProcessInGoogle(void) {
    int sel;
    wchar_t query[512], encoded[1024], url[1024];

    sel = ListView_GetNextItem(g_hListView, -1, LVNI_SELECTED);
    if (sel < 0 || sel >= g_processCount) {
        MessageBoxW(g_hMainWnd, L"Please select a process to search for information.", L"Error", MB_ICONWARNING);
        return;
    }

    if (g_processes[sel].company[0] && lstrcmpiW(g_processes[sel].company, L"Unknown") != 0) {
        wsprintfW(query, L"\"%s\" \"%s\" process what is",
                  g_processes[sel].exeName, g_processes[sel].company);
    } else {
        wsprintfW(query, L"\"%s\" process what is safe malware", g_processes[sel].exeName);
    }

    UrlEncode(query, encoded, 1024);
    lstrcpynW(url, L"https://www.google.com/search?q=", 1024);
    tm_wcscat_s(url, 1024, encoded);

    ShellExecuteW(NULL, L"open", url, NULL, NULL, SW_SHOWNORMAL);
}

static void OpenSelectedProcessFolder(void) {
    int sel;
    wchar_t param[TM_MAX_PATH_BUF + 32];

    sel = ListView_GetNextItem(g_hListView, -1, LVNI_SELECTED);
    if (sel < 0 || sel >= g_processCount) {
        MessageBoxW(g_hMainWnd, L"Please select a process.", L"Error", MB_ICONWARNING);
        return;
    }
    if (!g_processes[sel].fullPath[0]) {
        MessageBoxW(g_hMainWnd, L"Could not determine process file path.", L"Error", MB_ICONERROR);
        return;
    }
    wsprintfW(param, L"/select,\"%s\"", g_processes[sel].fullPath);
    ShellExecuteW(NULL, L"open", L"explorer.exe", param, NULL, SW_SHOWNORMAL);
}

/* ========================================================================= */
/* TASK SCHEDULER ACTIONS                                                    */
/* ========================================================================= */

/* ShowCreateTaskDialog removed - replaced by ShowCreateTaskDialogEx */

static void DeleteSelectedTask(HWND hParent) {
    int index;
    wchar_t msg[512];

    index = GetSelectedTaskIndex();
    if (index < 0) {
        MessageBoxW(hParent, L"Please select a task to delete.", L"Information", MB_ICONINFORMATION);
        return;
    }

    if (TS_IsTaskSystem(g_tasks[index].path)) {
        if (MessageBoxW(hParent,
                L"This appears to be a system task. Deleting it may affect system functionality.\n\n"
                L"Are you sure you want to delete this task?",
                L"Warning - System Task",
                MB_YESNO | MB_ICONWARNING | MB_DEFBUTTON2) != IDYES)
            return;
    }

    wsprintfW(msg, L"Are you sure you want to delete task '%s'?", g_tasks[index].name);
    if (MessageBoxW(hParent, msg, L"Confirm Deletion", MB_YESNO | MB_ICONQUESTION) == IDYES) {
        if (TS_DeleteTask(g_tasks[index].path)) {
            TM_LogAction(L"TS_DELETE", g_tasks[index].name);
            MessageBoxW(hParent, L"Task deleted successfully!", L"Success", MB_ICONINFORMATION);
            RefreshTaskSchedulerList();
        } else {
            MessageBoxW(hParent, L"Failed to delete task. Make sure you have administrator privileges.",
                        L"Error", MB_ICONERROR);
        }
    }
}

static void EnableDisableSelectedTask(HWND hParent, BOOL enable) {
    int index = GetSelectedTaskIndex();
    if (index < 0) {
        MessageBoxW(hParent, L"Please select a task.", L"Information", MB_ICONINFORMATION);
        return;
    }
    if (TS_EnableTask(g_tasks[index].path, enable)) {
        TM_LogAction(enable ? L"TS_ENABLE" : L"TS_DISABLE", g_tasks[index].name);
        MessageBoxW(hParent, enable ? L"Task enabled successfully!" : L"Task disabled successfully!",
                    L"Success", MB_ICONINFORMATION);
        RefreshTaskSchedulerList();
    } else {
        MessageBoxW(hParent, L"Failed to change task state. Make sure you have administrator privileges.",
                    L"Error", MB_ICONERROR);
    }
}

static void RunSelectedTask(HWND hParent) {
    int index = GetSelectedTaskIndex();
    if (index < 0) {
        MessageBoxW(hParent, L"Please select a task to run.", L"Information", MB_ICONINFORMATION);
        return;
    }
    if (TS_RunTask(g_tasks[index].path)) {
        TM_LogAction(L"TS_RUN", g_tasks[index].name);
        MessageBoxW(hParent, L"Task started successfully!", L"Success", MB_ICONINFORMATION);
        Sleep(1000);
        RefreshTaskSchedulerList();
    } else {
        MessageBoxW(hParent, L"Failed to run task. Make sure the task is enabled and you have sufficient privileges.",
                    L"Error", MB_ICONERROR);
    }
}

static void StopSelectedTask(HWND hParent) {
    int index = GetSelectedTaskIndex();
    if (index < 0) {
        MessageBoxW(hParent, L"Please select a task to stop.", L"Information", MB_ICONINFORMATION);
        return;
    }
    if (g_tasks[index].state != TM_TASK_STATE_RUNNING) {
        MessageBoxW(hParent, L"The selected task is not currently running.", L"Information", MB_ICONINFORMATION);
        return;
    }
    if (TS_StopTask(g_tasks[index].path)) {
        TM_LogAction(L"TS_STOP", g_tasks[index].name);
        MessageBoxW(hParent, L"Task stopped successfully!", L"Success", MB_ICONINFORMATION);
        RefreshTaskSchedulerList();
    } else {
        MessageBoxW(hParent, L"Failed to stop task.", L"Error", MB_ICONERROR);
    }
}

static void ShowSelectedTaskProperties(HWND hParent) {
    int index;
    wchar_t buf[2048], lastBuf[64], nextBuf[64];

    index = GetSelectedTaskIndex();
    if (index < 0) {
        MessageBoxW(hParent, L"Please select a task to view properties.", L"Information", MB_ICONINFORMATION);
        return;
    }

    TS_FormatDate(g_tasks[index].lastRunTime, lastBuf, 64);
    TS_FormatDate(g_tasks[index].nextRunTime, nextBuf, 64);

    buf[0] = 0;
    lstrcpynW(buf, L"Task Properties\r\n\r\n", 2048);
    tm_wcscat_s(buf, 2048, L"Name: "); tm_wcscat_s(buf, 2048, g_tasks[index].name); tm_wcscat_s(buf, 2048, L"\r\n");
    tm_wcscat_s(buf, 2048, L"Path: "); tm_wcscat_s(buf, 2048, g_tasks[index].path); tm_wcscat_s(buf, 2048, L"\r\n");
    tm_wcscat_s(buf, 2048, L"Author: "); tm_wcscat_s(buf, 2048, g_tasks[index].author); tm_wcscat_s(buf, 2048, L"\r\n");
    tm_wcscat_s(buf, 2048, L"Description: "); tm_wcscat_s(buf, 2048, g_tasks[index].description); tm_wcscat_s(buf, 2048, L"\r\n");
    tm_wcscat_s(buf, 2048, L"Executable: "); tm_wcscat_s(buf, 2048, g_tasks[index].executable); tm_wcscat_s(buf, 2048, L"\r\n");
    if (g_tasks[index].arguments[0]) {
        tm_wcscat_s(buf, 2048, L"Arguments: "); tm_wcscat_s(buf, 2048, g_tasks[index].arguments); tm_wcscat_s(buf, 2048, L"\r\n");
    }
    if (g_tasks[index].workingDirectory[0]) {
        tm_wcscat_s(buf, 2048, L"Working Directory: "); tm_wcscat_s(buf, 2048, g_tasks[index].workingDirectory); tm_wcscat_s(buf, 2048, L"\r\n");
    }
    tm_wcscat_s(buf, 2048, L"Status: "); tm_wcscat_s(buf, 2048, TS_GetTaskStateString(g_tasks[index].state)); tm_wcscat_s(buf, 2048, L"\r\n");
    tm_wcscat_s(buf, 2048, L"Enabled: "); tm_wcscat_s(buf, 2048, g_tasks[index].enabled ? L"Yes" : L"No"); tm_wcscat_s(buf, 2048, L"\r\n");
    tm_wcscat_s(buf, 2048, L"Hidden: "); tm_wcscat_s(buf, 2048, g_tasks[index].hidden ? L"Yes" : L"No"); tm_wcscat_s(buf, 2048, L"\r\n");
    {
        wchar_t trigBuf[64];
        wsprintfW(trigBuf, L"Triggers: %d (", g_tasks[index].triggerCount);
        tm_wcscat_s(buf, 2048, trigBuf);
        tm_wcscat_s(buf, 2048, g_tasks[index].triggerDescription);
        tm_wcscat_s(buf, 2048, L")\r\n");
    }
    tm_wcscat_s(buf, 2048, L"Last Run: "); tm_wcscat_s(buf, 2048, lastBuf); tm_wcscat_s(buf, 2048, L"\r\n");
    tm_wcscat_s(buf, 2048, L"Next Run: "); tm_wcscat_s(buf, 2048, nextBuf);

    MessageBoxW(hParent, buf, L"Task Properties", MB_ICONINFORMATION);
}

/* ========================================================================= */
/* HELPER FUNCTIONS - TRAY, THEME, COLUMNS, SYSTEM STATS                     */
/* ========================================================================= */

static void AddTrayIcon(HWND hWnd) {
    memset(&g_nid, 0, sizeof(g_nid));
    g_nid.cbSize = sizeof(NOTIFYICONDATAW);
    g_nid.hWnd = hWnd;
    g_nid.uID = 1;
    g_nid.uFlags = NIF_ICON | NIF_MESSAGE | NIF_TIP;
    g_nid.uCallbackMessage = WM_TRAYICON;
    g_nid.hIcon = LoadIconA(g_hInst, MAKEINTRESOURCEA(101));
    if (!g_nid.hIcon) g_nid.hIcon = LoadIconW(NULL, IDI_APPLICATION);
    lstrcpynW(g_nid.szTip, L"TaskMan Enhanced", 64);
    Shell_NotifyIconW(NIM_ADD, &g_nid);
    g_trayActive = TRUE;
}

static void RemoveTrayIcon(void) {
    if (g_trayActive) {
        Shell_NotifyIconW(NIM_DELETE, &g_nid);
        g_trayActive = FALSE;
    }
}

static void ApplyDarkTheme(HWND hWnd) {
    HWND hChild;
    if (g_darkTheme) {
        if (!g_hDarkBgBrush) g_hDarkBgBrush = CreateSolidBrush(RGB(30, 30, 30));
        if (!g_hDarkEditBrush) g_hDarkEditBrush = CreateSolidBrush(RGB(45, 45, 45));
    }
    /* Apply to all listviews in this window */
    hChild = GetWindow(hWnd, GW_CHILD);
    while (hChild) {
        wchar_t cls[64];
        GetClassNameW(hChild, cls, 64);
        if (lstrcmpiW(cls, WC_LISTVIEWW) == 0) {
            SetWindowTheme(hChild, g_darkTheme ? L"DarkMode_Explorer" : L"Explorer", NULL);
            ListView_SetBkColor(hChild, g_darkTheme ? RGB(30, 30, 30) : RGB(255, 255, 255));
            ListView_SetTextBkColor(hChild, g_darkTheme ? RGB(30, 30, 30) : RGB(255, 255, 255));
            ListView_SetTextColor(hChild, g_darkTheme ? RGB(220, 220, 220) : RGB(0, 0, 0));
        }
        hChild = GetWindow(hChild, GW_HWNDNEXT);
    }
    /* Update window background */
    SetClassLongPtrW(hWnd, GCLP_HBRBACKGROUND,
        (LONG_PTR)(g_darkTheme ? g_hDarkBgBrush : (HBRUSH)(COLOR_WINDOW + 1)));
    InvalidateRect(hWnd, NULL, TRUE);
}

static void SaveColumnWidths(const wchar_t *subKey, HWND hListView, int colCount) {
    HKEY hKey;
    if (!hListView) return;
    if (RegCreateKeyExW(HKEY_CURRENT_USER, L"Software\\TaskMan", 0, NULL, 0,
                        KEY_SET_VALUE, NULL, &hKey, NULL) == ERROR_SUCCESS) {
        int i;
        for (i = 0; i < colCount; i++) {
            wchar_t valName[64];
            DWORD width = (DWORD)ListView_GetColumnWidth(hListView, i);
            wsprintfW(valName, L"%s_Col%d", subKey, i);
            RegSetValueExW(hKey, valName, 0, REG_DWORD, (BYTE*)&width, sizeof(DWORD));
        }
        RegCloseKey(hKey);
    }
}

static void LoadColumnWidths(const wchar_t *subKey, HWND hListView, int colCount) {
    HKEY hKey;
    if (!hListView) return;
    if (RegOpenKeyExW(HKEY_CURRENT_USER, L"Software\\TaskMan", 0, KEY_READ, &hKey) == ERROR_SUCCESS) {
        int i;
        for (i = 0; i < colCount; i++) {
            wchar_t valName[64];
            DWORD width, size = sizeof(DWORD);
            wsprintfW(valName, L"%s_Col%d", subKey, i);
            if (RegQueryValueExW(hKey, valName, NULL, NULL, (BYTE*)&width, &size) == ERROR_SUCCESS && width > 0)
                ListView_SetColumnWidth(hListView, i, (int)width);
        }
        RegCloseKey(hKey);
    }
}

static void UpdateSystemStats(void) {
    FILETIME idleTime, kernelTime, userTime;
    if (GetSystemTimes(&idleTime, &kernelTime, &userTime)) {
        ULONGLONG idle   = ((ULONGLONG)idleTime.dwHighDateTime << 32) | idleTime.dwLowDateTime;
        ULONGLONG kernel = ((ULONGLONG)kernelTime.dwHighDateTime << 32) | kernelTime.dwLowDateTime;
        ULONGLONG user   = ((ULONGLONG)userTime.dwHighDateTime << 32) | userTime.dwLowDateTime;
        if (g_prevKernelTime != 0) {
            ULONGLONG dIdle = idle - g_prevIdleTime;
            ULONGLONG dKernel = kernel - g_prevKernelTime;
            ULONGLONG dUser = user - g_prevUserTime;
            ULONGLONG total = dKernel + dUser;
            if (total > 0)
                g_systemCpuPercent = (1.0 - (double)dIdle / (double)total) * 100.0;
        }
        g_prevIdleTime = idle;
        g_prevKernelTime = kernel;
        g_prevUserTime = user;
    }
}

/* ========================================================================= */
/* MAIN WINDOW PROCEDURE                                                     */
/* ========================================================================= */

static LRESULT CALLBACK MainWndProc(HWND hWnd, UINT message, WPARAM wParam, LPARAM lParam) {
    switch (message) {
    case WM_CREATE:
    {
        INITCOMMONCONTROLSEX icex;
        g_hMainWnd = hWnd;

        icex.dwICC = ICC_LISTVIEW_CLASSES | ICC_BAR_CLASSES;
        icex.dwSize = sizeof(icex);
        InitCommonControlsEx(&icex);

        CreateProcessListView(hWnd);

        g_hStatusBar = CreateWindowExW(0, STATUSCLASSNAMEW, L"",
            WS_CHILD | WS_VISIBLE | SBARS_SIZEGRIP,
            0, 0, 0, 0, hWnd, NULL, g_hInst, NULL);

        /* Process filter controls */
        CreateWindowExW(0, L"STATIC", L"Filter:",
            WS_CHILD | WS_VISIBLE, 0, 0, 40, 20, hWnd, (HMENU)ID_PROC_FILTER_LABEL, g_hInst, NULL);
        CreateWindowExW(WS_EX_CLIENTEDGE, L"EDIT", L"",
            WS_CHILD | WS_VISIBLE | ES_AUTOHSCROLL, 0, 0, 150, 22, hWnd, (HMENU)ID_PROC_FILTER, g_hInst, NULL);

        CreateWindowExW(0, L"BUTTON", L"Refresh",
            WS_CHILD | WS_VISIBLE | BS_DEFPUSHBUTTON,
            0, 0, 150, 25, hWnd, (HMENU)ID_REFRESH, g_hInst, NULL);
        CreateWindowExW(0, L"BUTTON", L"Terminate Process",
            WS_CHILD | WS_VISIBLE, 0, 0, 150, 25, hWnd, (HMENU)ID_TERMINATE, g_hInst, NULL);
        CreateWindowExW(0, L"BUTTON", L"Terminate All",
            WS_CHILD | WS_VISIBLE, 0, 0, 150, 25, hWnd, (HMENU)ID_TERMINATE_ALL, g_hInst, NULL);
        CreateWindowExW(0, L"BUTTON", L"Suspend",
            WS_CHILD | WS_VISIBLE, 0, 0, 150, 25, hWnd, (HMENU)ID_SUSPEND, g_hInst, NULL);
        CreateWindowExW(0, L"BUTTON", L"Resume",
            WS_CHILD | WS_VISIBLE, 0, 0, 150, 25, hWnd, (HMENU)ID_RESUME, g_hInst, NULL);
        CreateWindowExW(0, L"BUTTON", L"DLL List",
            WS_CHILD | WS_VISIBLE, 0, 0, 150, 25, hWnd, (HMENU)ID_DLL_LIST, g_hInst, NULL);
        CreateWindowExW(0, L"BUTTON", L"Process Tree",
            WS_CHILD | WS_VISIBLE, 0, 0, 150, 25, hWnd, (HMENU)ID_PROCESS_TREE, g_hInst, NULL);
        CreateWindowExW(0, L"BUTTON", L"Search Google",
            WS_CHILD | WS_VISIBLE, 0, 0, 150, 25, hWnd, (HMENU)ID_SEARCH_GOOGLE, g_hInst, NULL);
        CreateWindowExW(0, L"BUTTON", L"Open Folder",
            WS_CHILD | WS_VISIBLE, 0, 0, 150, 25, hWnd, (HMENU)ID_OPENFOLDER, g_hInst, NULL);
        CreateWindowExW(0, L"BUTTON", L"Autoruns",
            WS_CHILD | WS_VISIBLE, 0, 0, 150, 25, hWnd, (HMENU)ID_OPEN_AUTORUNS, g_hInst, NULL);
        CreateWindowExW(0, L"BUTTON", L"Task Scheduler",
            WS_CHILD | WS_VISIBLE, 0, 0, 150, 25, hWnd, (HMENU)ID_TASK_SCHEDULER, g_hInst, NULL);
        CreateWindowExW(0, L"BUTTON", L"About",
            WS_CHILD | WS_VISIBLE, 0, 0, 150, 25, hWnd, (HMENU)ID_ABOUT, g_hInst, NULL);
        CreateWindowExW(0, L"BUTTON", L"Dark Theme",
            WS_CHILD | WS_VISIBLE, 0, 0, 150, 25, hWnd, (HMENU)ID_DARK_TOGGLE, g_hInst, NULL);
        CreateWindowExW(0, L"BUTTON", L"Graphs",
            WS_CHILD | WS_VISIBLE, 0, 0, 150, 25, hWnd, (HMENU)ID_SHOW_GRAPHS, g_hInst, NULL);
        CreateWindowExW(0, L"BUTTON", L"Network Detail",
            WS_CHILD | WS_VISIBLE, 0, 0, 150, 25, hWnd, (HMENU)ID_NET_DETAIL, g_hInst, NULL);
        if (!IsRunningAsAdmin()) {
            HWND hAdminBtn = CreateWindowExW(0, L"BUTTON", L"Run as Admin",
                WS_CHILD | WS_VISIBLE, 0, 0, 150, 25, hWnd, (HMENU)ID_RESTART_ADMIN, g_hInst, NULL);
            if (hAdminBtn) SendMessageW(hAdminBtn, BCM_SETSHIELD, 0, TRUE);
        }
        CreateWindowExW(0, L"BUTTON", L"RU",
            WS_CHILD | WS_VISIBLE, 0, 0, 150, 25, hWnd, (HMENU)ID_LANG_TOGGLE, g_hInst, NULL);

        /* Load language preference from registry */
        {
            HKEY hKey;
            if (RegOpenKeyExW(HKEY_CURRENT_USER, L"Software\\TaskMan", 0, KEY_READ, &hKey) == ERROR_SUCCESS) {
                DWORD val, sz = sizeof(DWORD);
                if (RegQueryValueExW(hKey, L"Language", NULL, NULL, (BYTE*)&val, &sz) == ERROR_SUCCESS)
                    g_langId = (int)val;
                RegCloseKey(hKey);
            }
            if (g_langId) RefreshAllButtonText(hWnd);
        }

        /* Load dark theme preference from registry */
        {
            HKEY hKey;
            if (RegOpenKeyExW(HKEY_CURRENT_USER, L"Software\\TaskMan", 0, KEY_READ, &hKey) == ERROR_SUCCESS) {
                DWORD val, sz = sizeof(DWORD);
                if (RegQueryValueExW(hKey, L"DarkTheme", NULL, NULL, (BYTE*)&val, &sz) == ERROR_SUCCESS)
                    g_darkTheme = (BOOL)val;
                RegCloseKey(hKey);
            }
        }

        RegisterHotKey(hWnd, HOTKEY_SHOW, MOD_CONTROL | MOD_SHIFT, 'T');

        /* DPI scaling */
        g_currentDpi = GetWindowDpi(hWnd);
        if (g_hFont) DeleteObject(g_hFont);
        g_hFont = CreateScaledFont(g_currentDpi);
        ApplyDpiFont(hWnd);

        LoadColumnWidths(L"Proc", g_hListView, PROC_COL_COUNT);
        if (g_darkTheme) ApplyDarkTheme(hWnd);

        RefreshProcessList();
        PM_BuildCpuSnapshot();
        PM_GatherNetworkStats();
        SetTimer(hWnd, TIMER_REFRESH, 2000, NULL);
    }
    break;

    case WM_SIZE:
    {
        if (wParam == SIZE_MINIMIZED) {
            /* Minimize to system tray */
            AddTrayIcon(hWnd);
            ShowWindow(hWnd, SW_HIDE);
        } else {
            RECT rc;
            int w, h, statusHeight, listW, listH, panelX, btnW, curY;
            HWND hBtn;

            GetClientRect(hWnd, &rc);
            w = rc.right - rc.left;
            h = rc.bottom - rc.top;
            statusHeight = 25;

            MoveWindow(g_hStatusBar, 0, h - statusHeight, w, statusHeight, TRUE);

            listW = w - PANEL_WIDTH - 20;
            listH = h - statusHeight - 20;
            if (listW < 50) listW = 50;
            if (listH < 50) listH = 50;
            MoveWindow(g_hListView, 10, 10, listW, listH, TRUE);

            panelX = 10 + listW + 10;
            btnW = PANEL_WIDTH - 20;
            if (btnW < 50) btnW = 50;
            curY = 10;

            /* Process filter at top of panel */
            hBtn = GetDlgItem(hWnd, ID_PROC_FILTER_LABEL);
            if (hBtn) MoveWindow(hBtn, panelX, curY + 2, 40, 20, TRUE);
            hBtn = GetDlgItem(hWnd, ID_PROC_FILTER);
            if (hBtn) MoveWindow(hBtn, panelX + 42, curY, btnW - 42, 22, TRUE);
            curY += 30;

            hBtn = GetDlgItem(hWnd, ID_REFRESH);
            if (hBtn) { MoveWindow(hBtn, panelX, curY, btnW, 25, TRUE); curY += 30; }
            hBtn = GetDlgItem(hWnd, ID_TERMINATE);
            if (hBtn) { MoveWindow(hBtn, panelX, curY, btnW, 25, TRUE); curY += 30; }
            hBtn = GetDlgItem(hWnd, ID_TERMINATE_ALL);
            if (hBtn) { MoveWindow(hBtn, panelX, curY, btnW, 25, TRUE); curY += 30; }
            hBtn = GetDlgItem(hWnd, ID_SUSPEND);
            if (hBtn) { MoveWindow(hBtn, panelX, curY, btnW, 25, TRUE); curY += 30; }
            hBtn = GetDlgItem(hWnd, ID_RESUME);
            if (hBtn) { MoveWindow(hBtn, panelX, curY, btnW, 25, TRUE); curY += 30; }
            hBtn = GetDlgItem(hWnd, ID_DLL_LIST);
            if (hBtn) { MoveWindow(hBtn, panelX, curY, btnW, 25, TRUE); curY += 30; }
            hBtn = GetDlgItem(hWnd, ID_PROCESS_TREE);
            if (hBtn) { MoveWindow(hBtn, panelX, curY, btnW, 25, TRUE); curY += 30; }
            hBtn = GetDlgItem(hWnd, ID_SEARCH_GOOGLE);
            if (hBtn) { MoveWindow(hBtn, panelX, curY, btnW, 25, TRUE); curY += 30; }
            hBtn = GetDlgItem(hWnd, ID_OPENFOLDER);
            if (hBtn) { MoveWindow(hBtn, panelX, curY, btnW, 25, TRUE); curY += 30; }
            hBtn = GetDlgItem(hWnd, ID_OPEN_AUTORUNS);
            if (hBtn) { MoveWindow(hBtn, panelX, curY, btnW, 25, TRUE); curY += 30; }
            hBtn = GetDlgItem(hWnd, ID_TASK_SCHEDULER);
            if (hBtn) { MoveWindow(hBtn, panelX, curY, btnW, 25, TRUE); curY += 30; }
            hBtn = GetDlgItem(hWnd, ID_ABOUT);
            if (hBtn) { MoveWindow(hBtn, panelX, curY, btnW, 25, TRUE); curY += 30; }
            hBtn = GetDlgItem(hWnd, ID_DARK_TOGGLE);
            if (hBtn) { MoveWindow(hBtn, panelX, curY, btnW, 25, TRUE); curY += 30; }
            hBtn = GetDlgItem(hWnd, ID_SHOW_GRAPHS);
            if (hBtn) { MoveWindow(hBtn, panelX, curY, btnW, 25, TRUE); curY += 30; }
            hBtn = GetDlgItem(hWnd, ID_NET_DETAIL);
            if (hBtn) { MoveWindow(hBtn, panelX, curY, btnW, 25, TRUE); curY += 30; }
            hBtn = GetDlgItem(hWnd, ID_RESTART_ADMIN);
            if (hBtn) { MoveWindow(hBtn, panelX, curY, btnW, 25, TRUE); curY += 30; }
            hBtn = GetDlgItem(hWnd, ID_LANG_TOGGLE);
            if (hBtn) { MoveWindow(hBtn, panelX, curY, btnW, 25, TRUE); }
        }
    }
    break;

    case WM_NOTIFY:
    {
        LPNMHDR pnmh = (LPNMHDR)lParam;
        if (pnmh->hwndFrom == g_hListView && pnmh->code == LVN_COLUMNCLICK) {
            LPNMLISTVIEW pnmlv = (LPNMLISTVIEW)lParam;
            OnProcessColumnClick(pnmlv->iSubItem);
        }
        if (pnmh->hwndFrom == g_hListView && pnmh->code == NM_CUSTOMDRAW && g_darkTheme) {
            LPNMLVCUSTOMDRAW lpcd = (LPNMLVCUSTOMDRAW)lParam;
            switch (lpcd->nmcd.dwDrawStage) {
            case CDDS_PREPAINT: return CDRF_NOTIFYITEMDRAW;
            case CDDS_ITEMPREPAINT:
                lpcd->clrText = RGB(220, 220, 220);
                lpcd->clrTextBk = RGB(30, 30, 30);
                return CDRF_DODEFAULT;
            }
        }
    }
    break;

    case WM_TIMER:
        if (wParam == TIMER_REFRESH) {
            EnterCriticalSection(&g_dataLock);
            PM_RefreshProcessStats();
            PM_ComputeCpuUsage();
            PM_BuildCpuSnapshot();
            PM_GatherNetworkStats();
            PM_GatherDiskIO();
            PM_GatherGpuStats();
            LeaveCriticalSection(&g_dataLock);
            PopulateProcessListView();
            if (g_hStatusBar) {
                MEMORYSTATUSEX memInfo;
                wchar_t s0[64], s1[64], s2[128], s3[64];
                wchar_t cpuStr[16];
                int parts[4];
                DWORD usedMB, totalMB;

                UpdateSystemStats();
                memInfo.dwLength = sizeof(memInfo);
                GlobalMemoryStatusEx(&memInfo);

                wsprintfW(s0, L" Processes: %d", g_processCount);
                tm_format_double1(cpuStr, 16, g_systemCpuPercent);
                wsprintfW(s1, L" CPU: %s%%", cpuStr);
                usedMB = (DWORD)((memInfo.ullTotalPhys - memInfo.ullAvailPhys) / (1024*1024));
                totalMB = (DWORD)(memInfo.ullTotalPhys / (1024*1024));
                wsprintfW(s2, L" RAM: %u / %u MB (%u%%)", usedMB, totalMB, memInfo.dwMemoryLoad);
                wsprintfW(s3, L" %s", IsRunningAsAdmin() ? L"(Admin)" : L"(User)");

                parts[0] = 150; parts[1] = 300; parts[2] = 550; parts[3] = -1;
                SendMessageW(g_hStatusBar, SB_SETPARTS, 4, (LPARAM)parts);
                SendMessageW(g_hStatusBar, SB_SETTEXTW, 0, (LPARAM)s0);
                SendMessageW(g_hStatusBar, SB_SETTEXTW, 1, (LPARAM)s1);
                SendMessageW(g_hStatusBar, SB_SETTEXTW, 2, (LPARAM)s2);
                SendMessageW(g_hStatusBar, SB_SETTEXTW, 3, (LPARAM)s3);

                /* Push history for graphs */
                g_cpuHistory[g_graphIndex] = g_systemCpuPercent;
                g_ramHistory[g_graphIndex] = (double)memInfo.dwMemoryLoad;
                g_graphIndex = (g_graphIndex + 1) % GRAPH_HISTORY_SIZE;
                if (g_hGraphWnd) InvalidateRect(g_hGraphWnd, NULL, FALSE);
            }
        }
        break;

    case WM_COMMAND:
    {
        switch (LOWORD(wParam)) {
        case ID_REFRESH:        RefreshProcessList(); PM_BuildCpuSnapshot(); PM_GatherNetworkStats(); break;
        case ID_TERMINATE:      TerminateSelectedProcess(); break;
        case ID_TERMINATE_ALL:  TerminateAllProcessesByName(); break;
        case ID_SEARCH_GOOGLE:  SearchProcessInGoogle(); break;
        case ID_OPENFOLDER:     OpenSelectedProcessFolder(); break;

        case ID_SUSPEND:
        {
            DWORD pid = GetSelectedProcessPID();
            if (pid == 0) { MessageBoxW(hWnd, L"Please select a process.", L"Error", MB_ICONWARNING); }
            else if (PM_IsSystemProcess(pid)) { MessageBoxW(hWnd, L"Cannot suspend a system process.", L"Error", MB_ICONWARNING); }
            else if (PM_SuspendProcess(pid)) { wchar_t logM[64]; wsprintfW(logM, L"PID %u", pid); TM_LogAction(L"SUSPEND", logM); MessageBoxW(hWnd, L"Process suspended.", L"Success", MB_ICONINFORMATION); }
            else { MessageBoxW(hWnd, L"Failed to suspend process.", L"Error", MB_ICONERROR); }
        }
        break;

        case ID_RESUME:
        {
            DWORD pid = GetSelectedProcessPID();
            if (pid == 0) { MessageBoxW(hWnd, L"Please select a process.", L"Error", MB_ICONWARNING); }
            else if (PM_ResumeProcess(pid)) { wchar_t logM[64]; wsprintfW(logM, L"PID %u", pid); TM_LogAction(L"RESUME", logM); MessageBoxW(hWnd, L"Process resumed.", L"Success", MB_ICONINFORMATION); }
            else { MessageBoxW(hWnd, L"Failed to resume process.", L"Error", MB_ICONERROR); }
        }
        break;

        case ID_DLL_LIST:
        {
            DWORD pid = GetSelectedProcessPID();
            if (pid == 0) MessageBoxW(hWnd, L"Please select a process.", L"Error", MB_ICONWARNING);
            else PM_EnumProcessDlls(pid, hWnd);
        }
        break;

        case ID_PROCESS_TREE:
            PM_ShowProcessTree(hWnd);
            break;

        case ID_OPEN_AUTORUNS:
            if (!g_hAutorunWnd) {
                if (!CreateWindowExA(0, g_szAutorunClass, "Autoruns Enhanced",
                        WS_OVERLAPPEDWINDOW | WS_VISIBLE,
                        CW_USEDEFAULT, CW_USEDEFAULT, 1200, 600,
                        NULL, NULL, g_hInst, NULL))
                    MessageBoxA(NULL, "Failed to create Autoruns window!", "Error", MB_ICONERROR);
            } else {
                SetForegroundWindow(g_hAutorunWnd);
            }
            break;

        case ID_TASK_SCHEDULER:
            if (!g_hTaskSchedulerWnd) {
                if (!CreateWindowExA(0, g_szTaskSchedulerClass, "Task Scheduler Enhanced",
                        WS_OVERLAPPEDWINDOW | WS_VISIBLE,
                        CW_USEDEFAULT, CW_USEDEFAULT, 1400, 700,
                        NULL, NULL, g_hInst, NULL))
                    MessageBoxA(NULL, "Failed to create Task Scheduler window!", "Error", MB_ICONERROR);
            } else {
                SetForegroundWindow(g_hTaskSchedulerWnd);
            }
            break;

        case ID_ABOUT:
            MessageBoxW(hWnd,
                L"TaskMan Enhanced v3.1\r\n\r\n"
                L"Advanced Task Manager with 50+ autorun locations\r\n"
                L"Part of PsyShoutTools\r\n\r\n"
                L"Features:\r\n"
                L"\x2022 CPU, GPU, Disk I/O monitoring\r\n"
                L"\x2022 Network connections detail\r\n"
                L"\x2022 CPU/RAM graphs (GDI)\r\n"
                L"\x2022 Process tree / DLL list\r\n"
                L"\x2022 Suspend/Resume processes\r\n"
                L"\x2022 Dark theme / DPI scaling\r\n"
                L"\x2022 Multilingual (EN/RU)\r\n"
                L"\x2022 Action audit logging\r\n"
                L"\x2022 System tray + hotkey\r\n"
                L"\x2022 Task Scheduler + XML\r\n"
                L"\x2022 Autorun scanning\r\n"
                L"\x2022 Admin self-elevation\r\n"
                L"\x2022 x86 + x64 support\r\n\r\n"
                L"\x00A9 2025 PsyShoutTools",
                L"About", MB_ICONINFORMATION);
            break;

        case ID_PROC_FILTER:
            if (HIWORD(wParam) == EN_CHANGE) {
                GetWindowTextW(GetDlgItem(hWnd, ID_PROC_FILTER), g_procFilter, TM_MAX_NAME);
                PopulateProcessListView();
            }
            break;

        case ID_DARK_TOGGLE:
            g_darkTheme = !g_darkTheme;
            ApplyDarkTheme(hWnd);
            if (g_hAutorunWnd) ApplyDarkTheme(g_hAutorunWnd);
            if (g_hTaskSchedulerWnd) ApplyDarkTheme(g_hTaskSchedulerWnd);
            /* Save preference */
            {
                HKEY hKey;
                if (RegCreateKeyExW(HKEY_CURRENT_USER, L"Software\\TaskMan", 0, NULL, 0,
                                    KEY_SET_VALUE, NULL, &hKey, NULL) == ERROR_SUCCESS) {
                    DWORD val = g_darkTheme ? 1 : 0;
                    RegSetValueExW(hKey, L"DarkTheme", 0, REG_DWORD, (BYTE*)&val, sizeof(DWORD));
                    RegCloseKey(hKey);
                }
            }
            break;

        case ID_TRAY_SHOW:
            ShowWindow(hWnd, SW_RESTORE);
            SetForegroundWindow(hWnd);
            RemoveTrayIcon();
            break;

        case ID_TRAY_EXIT:
            RemoveTrayIcon();
            DestroyWindow(hWnd);
            break;

        case ID_SHOW_GRAPHS:
            if (!g_hGraphWnd) {
                if (!CreateWindowExA(0, g_szGraphClass, "CPU / RAM Graphs",
                        WS_OVERLAPPEDWINDOW | WS_VISIBLE,
                        CW_USEDEFAULT, CW_USEDEFAULT, 600, 400,
                        NULL, NULL, g_hInst, NULL))
                    MessageBoxA(NULL, "Failed to create graph window!", "Error", MB_ICONERROR);
            } else {
                SetForegroundWindow(g_hGraphWnd);
            }
            break;

        case ID_NET_DETAIL:
        {
            DWORD pid = GetSelectedProcessPID();
            if (pid == 0) {
                MessageBoxW(hWnd, L"Please select a process.", L"Error", MB_ICONWARNING);
            } else {
                /* Store PID in window name for NetDetail */
                char title[128];
                wsprintfA(title, "Network Connections - PID %u", pid);
                CreateWindowExA(0, g_szNetDetailClass, title,
                    WS_OVERLAPPEDWINDOW | WS_VISIBLE,
                    CW_USEDEFAULT, CW_USEDEFAULT, 700, 400,
                    NULL, NULL, g_hInst, (LPVOID)(DWORD_PTR)pid);
            }
        }
        break;

        case ID_RESTART_ADMIN:
            RestartAsAdmin();
            break;

        case ID_LANG_TOGGLE:
            g_langId = g_langId ? 0 : 1;
            RefreshAllButtonText(hWnd);
            /* Save preference */
            {
                HKEY hKey;
                if (RegCreateKeyExW(HKEY_CURRENT_USER, L"Software\\TaskMan", 0, NULL, 0,
                                    KEY_SET_VALUE, NULL, &hKey, NULL) == ERROR_SUCCESS) {
                    DWORD val = (DWORD)g_langId;
                    RegSetValueExW(hKey, L"Language", 0, REG_DWORD, (BYTE*)&val, sizeof(DWORD));
                    RegCloseKey(hKey);
                }
            }
            break;
        }
    }
    break;

    case WM_HOTKEY:
        if (wParam == HOTKEY_SHOW) {
            if (IsIconic(hWnd) || !IsWindowVisible(hWnd)) {
                ShowWindow(hWnd, SW_RESTORE);
                if (g_trayActive) RemoveTrayIcon();
            }
            SetForegroundWindow(hWnd);
        }
        break;

    case WM_TRAYICON:
        if (lParam == WM_LBUTTONDBLCLK) {
            ShowWindow(hWnd, SW_RESTORE);
            SetForegroundWindow(hWnd);
            RemoveTrayIcon();
        } else if (lParam == WM_RBUTTONUP) {
            POINT pt;
            HMENU hMenu = CreatePopupMenu();
            GetCursorPos(&pt);
            AppendMenuW(hMenu, MF_STRING, ID_TRAY_SHOW, L"Show TaskMan");
            AppendMenuW(hMenu, MF_SEPARATOR, 0, NULL);
            AppendMenuW(hMenu, MF_STRING, ID_TRAY_EXIT, L"Exit");
            SetForegroundWindow(hWnd);
            TrackPopupMenu(hMenu, TPM_RIGHTBUTTON, pt.x, pt.y, 0, hWnd, NULL);
            DestroyMenu(hMenu);
        }
        break;

    case WM_CONTEXTMENU:
    {
        HWND hTarget = (HWND)wParam;
        if (hTarget == g_hListView) {
            int x = GET_X_LPARAM(lParam), y = GET_Y_LPARAM(lParam);
            HMENU hMenu = CreatePopupMenu();
            if (x == -1 && y == -1) {
                RECT rc; int sel = ListView_GetNextItem(g_hListView, -1, LVNI_SELECTED);
                if (sel >= 0) { ListView_GetItemRect(g_hListView, sel, &rc, LVIR_BOUNDS); }
                else { GetClientRect(g_hListView, &rc); }
                x = rc.left; y = rc.bottom;
                ClientToScreen(g_hListView, (POINT*)&x);
            }
            AppendMenuW(hMenu, MF_STRING, ID_REFRESH, L"Refresh");
            AppendMenuW(hMenu, MF_SEPARATOR, 0, NULL);
            AppendMenuW(hMenu, MF_STRING, ID_TERMINATE, L"Terminate Process");
            AppendMenuW(hMenu, MF_STRING, ID_TERMINATE_ALL, L"Terminate All by Name");
            AppendMenuW(hMenu, MF_SEPARATOR, 0, NULL);
            AppendMenuW(hMenu, MF_STRING, ID_SUSPEND, L"Suspend");
            AppendMenuW(hMenu, MF_STRING, ID_RESUME, L"Resume");
            AppendMenuW(hMenu, MF_SEPARATOR, 0, NULL);
            AppendMenuW(hMenu, MF_STRING, ID_DLL_LIST, L"DLL List");
            AppendMenuW(hMenu, MF_STRING, ID_OPENFOLDER, L"Open Folder");
            AppendMenuW(hMenu, MF_STRING, ID_SEARCH_GOOGLE, L"Search Google");
            TrackPopupMenu(hMenu, TPM_RIGHTBUTTON, x, y, 0, hWnd, NULL);
            DestroyMenu(hMenu);
        }
    }
    break;

    case WM_CTLCOLORSTATIC:
    case WM_CTLCOLOREDIT:
    case WM_CTLCOLORBTN:
        if (g_darkTheme) {
            HDC hdc = (HDC)wParam;
            SetTextColor(hdc, RGB(220, 220, 220));
            SetBkColor(hdc, RGB(45, 45, 45));
            return (LRESULT)g_hDarkEditBrush;
        }
        break;

    case WM_ERASEBKGND:
        if (g_darkTheme) {
            RECT rc;
            GetClientRect(hWnd, &rc);
            FillRect((HDC)wParam, &rc, g_hDarkBgBrush);
            return 1;
        }
        break;

    case WM_DPICHANGED:
    {
        RECT *prc = (RECT*)lParam;
        g_currentDpi = HIWORD(wParam);
        if (g_hFont) DeleteObject(g_hFont);
        g_hFont = CreateScaledFont(g_currentDpi);
        ApplyDpiFont(hWnd);
        SetWindowPos(hWnd, NULL, prc->left, prc->top,
                     prc->right - prc->left, prc->bottom - prc->top,
                     SWP_NOZORDER | SWP_NOACTIVATE);
    }
    break;

    case WM_DESTROY:
        SaveColumnWidths(L"Proc", g_hListView, PROC_COL_COUNT);
        KillTimer(hWnd, TIMER_REFRESH);
        UnregisterHotKey(hWnd, HOTKEY_SHOW);
        RemoveTrayIcon();
        if (g_hFont) { DeleteObject(g_hFont); g_hFont = NULL; }
        if (g_hDarkBgBrush) { DeleteObject(g_hDarkBgBrush); g_hDarkBgBrush = NULL; }
        if (g_hDarkEditBrush) { DeleteObject(g_hDarkEditBrush); g_hDarkEditBrush = NULL; }
        PostQuitMessage(0);
        break;

    default:
        return DefWindowProcA(hWnd, message, wParam, lParam);
    }
    return 0;
}

/* ========================================================================= */
/* AUTORUNS WINDOW PROCEDURE                                                 */
/* ========================================================================= */

static LRESULT CALLBACK AutorunsWndProc(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    switch (msg) {
    case WM_CREATE:
    {
        g_hAutorunWnd = hWnd;
        CreateAutorunListView(hWnd);

        g_hArStatusBar = CreateWindowExW(0, STATUSCLASSNAMEW, L"",
            WS_CHILD | WS_VISIBLE | SBARS_SIZEGRIP,
            0, 0, 0, 0, hWnd, NULL, g_hInst, NULL);

        /* Filter controls */
        {
            HWND hCombo;
            static const wchar_t *cats[] = {
                L"All Sources", L"Registry Run Keys", L"Winlogon", L"System Keys",
                L"Shell & Browser", L"Drivers & Codecs", L"Startup Folders",
                L"Services", L"Scheduled Tasks", L"Other"
            };
            int ci;

            CreateWindowExW(0, L"STATIC", L"Filter:",
                WS_CHILD | WS_VISIBLE, 0, 0, 40, 20, hWnd, (HMENU)ID_AR_FILTER_LABEL, g_hInst, NULL);
            CreateWindowExW(WS_EX_CLIENTEDGE, L"EDIT", L"",
                WS_CHILD | WS_VISIBLE | ES_AUTOHSCROLL, 0, 0, 200, 22, hWnd, (HMENU)ID_AR_FILTER, g_hInst, NULL);
            CreateWindowExW(0, L"STATIC", L"Source:",
                WS_CHILD | WS_VISIBLE, 0, 0, 50, 20, hWnd, (HMENU)ID_AR_SOURCE_LABEL, g_hInst, NULL);
            hCombo = CreateWindowExW(0, L"COMBOBOX", L"",
                WS_CHILD | WS_VISIBLE | CBS_DROPDOWNLIST | WS_VSCROLL,
                0, 0, 180, 300, hWnd, (HMENU)ID_AR_SOURCE_COMBO, g_hInst, NULL);
            for (ci = 0; ci < 10; ci++)
                SendMessageW(hCombo, CB_ADDSTRING, 0, (LPARAM)cats[ci]);
            SendMessageW(hCombo, CB_SETCURSEL, 0, 0);

            CreateWindowExW(0, L"BUTTON", L"Only Enabled",
                WS_CHILD | WS_VISIBLE | BS_AUTOCHECKBOX,
                0, 0, 120, 20, hWnd, (HMENU)ID_AR_CHK_ENABLED, g_hInst, NULL);
            CreateWindowExW(0, L"BUTTON", L"Only Unsigned",
                WS_CHILD | WS_VISIBLE | BS_AUTOCHECKBOX,
                0, 0, 130, 20, hWnd, (HMENU)ID_AR_CHK_UNSIGNED, g_hInst, NULL);
        }

        /* Action buttons */
        CreateWindowExW(0, L"BUTTON", L"Refresh",
            WS_CHILD | WS_VISIBLE | BS_DEFPUSHBUTTON,
            0, 0, 100, 30, hWnd, (HMENU)ID_AR_REFRESH, g_hInst, NULL);
        CreateWindowExW(0, L"BUTTON", L"Delete",
            WS_CHILD | WS_VISIBLE, 0, 0, 100, 30, hWnd, (HMENU)ID_AR_REMOVE, g_hInst, NULL);
        CreateWindowExW(0, L"BUTTON", L"Open Folder",
            WS_CHILD | WS_VISIBLE, 0, 0, 120, 30, hWnd, (HMENU)ID_AR_OPENFOLDER, g_hInst, NULL);
        CreateWindowExW(0, L"BUTTON", L"Export CSV",
            WS_CHILD | WS_VISIBLE, 0, 0, 100, 30, hWnd, (HMENU)ID_AR_EXPORT, g_hInst, NULL);
        CreateWindowExW(0, L"BUTTON", L"Enable",
            WS_CHILD | WS_VISIBLE, 0, 0, 100, 30, hWnd, (HMENU)ID_AR_ENABLE, g_hInst, NULL);
        CreateWindowExW(0, L"BUTTON", L"Disable",
            WS_CHILD | WS_VISIBLE, 0, 0, 100, 30, hWnd, (HMENU)ID_AR_DISABLE, g_hInst, NULL);
        CreateWindowExW(0, L"BUTTON", L"Properties",
            WS_CHILD | WS_VISIBLE, 0, 0, 100, 30, hWnd, (HMENU)ID_AR_PROPERTIES, g_hInst, NULL);
        CreateWindowExW(0, L"BUTTON", L"Force Delete",
            WS_CHILD | WS_VISIBLE, 0, 0, 100, 30, hWnd, (HMENU)ID_AR_FORCE_DELETE, g_hInst, NULL);
        CreateWindowExW(0, L"BUTTON", L"Undo",
            WS_CHILD | WS_VISIBLE, 0, 0, 100, 30, hWnd, (HMENU)ID_AR_UNDO, g_hInst, NULL);

        LoadColumnWidths(L"AR", g_hArListView, 7);
        if (g_darkTheme) ApplyDarkTheme(hWnd);
        StartAutorunRefresh();
    }
    break;

    case WM_SIZE:
    {
        if (wParam != SIZE_MINIMIZED) {
            RECT rc;
            int w, h, statusHeight, listW, listH, filterY, btnY, btnSpacing, btnWidth, btnHeight, totalBtnWidth, startX;
            HWND hCtl;

            GetClientRect(hWnd, &rc);
            w = rc.right - rc.left;
            h = rc.bottom - rc.top;
            statusHeight = 25;

            MoveWindow(g_hArStatusBar, 0, h - statusHeight, w, statusHeight, TRUE);

            listW = w - 20; listH = h - statusHeight - 140;
            if (listW < 50) listW = 50;
            if (listH < 50) listH = 50;
            MoveWindow(g_hArListView, 10, 10, listW, listH, TRUE);

            /* Filter bar */
            filterY = 10 + listH + 8;
            hCtl = GetDlgItem(hWnd, ID_AR_FILTER_LABEL);
            if (hCtl) MoveWindow(hCtl, 10, filterY + 2, 40, 20, TRUE);
            hCtl = GetDlgItem(hWnd, ID_AR_FILTER);
            if (hCtl) MoveWindow(hCtl, 52, filterY, 200, 22, TRUE);
            hCtl = GetDlgItem(hWnd, ID_AR_SOURCE_LABEL);
            if (hCtl) MoveWindow(hCtl, 265, filterY + 2, 50, 20, TRUE);
            hCtl = GetDlgItem(hWnd, ID_AR_SOURCE_COMBO);
            if (hCtl) MoveWindow(hCtl, 318, filterY, 180, 300, TRUE);

            hCtl = GetDlgItem(hWnd, ID_AR_CHK_ENABLED);
            if (hCtl) MoveWindow(hCtl, 520, filterY + 2, 120, 20, TRUE);
            hCtl = GetDlgItem(hWnd, ID_AR_CHK_UNSIGNED);
            if (hCtl) MoveWindow(hCtl, 650, filterY + 2, 130, 20, TRUE);

            /* Buttons row */
            btnY = filterY + 30;
            btnSpacing = 8; btnWidth = 85; btnHeight = 28;
            totalBtnWidth = 9 * btnWidth + 8 * btnSpacing;
            startX = (w - totalBtnWidth) / 2;
            if (startX < 10) startX = 10;

            hCtl = GetDlgItem(hWnd, ID_AR_REFRESH);
            if (hCtl) { MoveWindow(hCtl, startX, btnY, btnWidth, btnHeight, TRUE); startX += btnWidth + btnSpacing; }
            hCtl = GetDlgItem(hWnd, ID_AR_ENABLE);
            if (hCtl) { MoveWindow(hCtl, startX, btnY, btnWidth, btnHeight, TRUE); startX += btnWidth + btnSpacing; }
            hCtl = GetDlgItem(hWnd, ID_AR_DISABLE);
            if (hCtl) { MoveWindow(hCtl, startX, btnY, btnWidth, btnHeight, TRUE); startX += btnWidth + btnSpacing; }
            hCtl = GetDlgItem(hWnd, ID_AR_REMOVE);
            if (hCtl) { MoveWindow(hCtl, startX, btnY, btnWidth, btnHeight, TRUE); startX += btnWidth + btnSpacing; }
            hCtl = GetDlgItem(hWnd, ID_AR_PROPERTIES);
            if (hCtl) { MoveWindow(hCtl, startX, btnY, btnWidth, btnHeight, TRUE); startX += btnWidth + btnSpacing; }
            hCtl = GetDlgItem(hWnd, ID_AR_FORCE_DELETE);
            if (hCtl) { MoveWindow(hCtl, startX, btnY, btnWidth + 10, btnHeight, TRUE); startX += btnWidth + 10 + btnSpacing; }
            hCtl = GetDlgItem(hWnd, ID_AR_OPENFOLDER);
            if (hCtl) { MoveWindow(hCtl, startX, btnY, btnWidth + 10, btnHeight, TRUE); startX += btnWidth + 10 + btnSpacing; }
            hCtl = GetDlgItem(hWnd, ID_AR_EXPORT);
            if (hCtl) { MoveWindow(hCtl, startX, btnY, btnWidth, btnHeight, TRUE); startX += btnWidth + btnSpacing; }
            hCtl = GetDlgItem(hWnd, ID_AR_UNDO);
            if (hCtl) { MoveWindow(hCtl, startX, btnY, btnWidth, btnHeight, TRUE); }
        }
    }
    break;

    case WM_NOTIFY:
    {
        LPNMHDR pnmh = (LPNMHDR)lParam;
        if (pnmh->hwndFrom == g_hArListView && pnmh->code == LVN_COLUMNCLICK) {
            LPNMLISTVIEW pnmlv = (LPNMLISTVIEW)lParam;
            OnAutorunColumnClick(pnmlv->iSubItem);
        }
        if (pnmh->hwndFrom == g_hArListView && pnmh->code == NM_CUSTOMDRAW && g_darkTheme) {
            LPNMLVCUSTOMDRAW lpcd = (LPNMLVCUSTOMDRAW)lParam;
            switch (lpcd->nmcd.dwDrawStage) {
            case CDDS_PREPAINT: return CDRF_NOTIFYITEMDRAW;
            case CDDS_ITEMPREPAINT:
                lpcd->clrText = RGB(220, 220, 220);
                lpcd->clrTextBk = RGB(30, 30, 30);
                return CDRF_DODEFAULT;
            }
        }
    }
    break;

    case WM_CONTEXTMENU:
    {
        HWND hTarget = (HWND)wParam;
        if (hTarget == g_hArListView) {
            int x = GET_X_LPARAM(lParam), y = GET_Y_LPARAM(lParam);
            HMENU hMenu = CreatePopupMenu();
            if (x == -1 && y == -1) {
                RECT rc; int sel = ListView_GetNextItem(g_hArListView, -1, LVNI_SELECTED);
                if (sel >= 0) { ListView_GetItemRect(g_hArListView, sel, &rc, LVIR_BOUNDS); }
                else { GetClientRect(g_hArListView, &rc); }
                x = rc.left; y = rc.bottom;
                ClientToScreen(g_hArListView, (POINT*)&x);
            }
            AppendMenuW(hMenu, MF_STRING, ID_AR_REFRESH, L"Refresh");
            AppendMenuW(hMenu, MF_SEPARATOR, 0, NULL);
            AppendMenuW(hMenu, MF_STRING, ID_AR_ENABLE, L"Enable");
            AppendMenuW(hMenu, MF_STRING, ID_AR_DISABLE, L"Disable");
            AppendMenuW(hMenu, MF_STRING, ID_AR_REMOVE, L"Delete");
            AppendMenuW(hMenu, MF_STRING, ID_AR_FORCE_DELETE, L"Force Delete");
            AppendMenuW(hMenu, MF_SEPARATOR, 0, NULL);
            AppendMenuW(hMenu, MF_STRING, ID_AR_PROPERTIES, L"Properties");
            AppendMenuW(hMenu, MF_STRING, ID_AR_OPENFOLDER, L"Open Folder");
            AppendMenuW(hMenu, MF_STRING, ID_AR_EXPORT, L"Export CSV");
            AppendMenuW(hMenu, MF_SEPARATOR, 0, NULL);
            AppendMenuW(hMenu, MF_STRING, ID_AR_UNDO, L"Undo");
            TrackPopupMenu(hMenu, TPM_RIGHTBUTTON, x, y, 0, hWnd, NULL);
            DestroyMenu(hMenu);
        }
    }
    break;

    case WM_CTLCOLORSTATIC:
    case WM_CTLCOLOREDIT:
    case WM_CTLCOLORBTN:
        if (g_darkTheme) {
            HDC hdc = (HDC)wParam;
            SetTextColor(hdc, RGB(220, 220, 220));
            SetBkColor(hdc, RGB(45, 45, 45));
            return (LRESULT)g_hDarkEditBrush;
        }
        break;

    case WM_ERASEBKGND:
        if (g_darkTheme) {
            RECT rc;
            GetClientRect(hWnd, &rc);
            FillRect((HDC)wParam, &rc, g_hDarkBgBrush);
            return 1;
        }
        break;

    case WM_COMMAND:
    {
        switch (LOWORD(wParam)) {
        case ID_AR_REFRESH:
            StartAutorunRefresh();
            break;

        case ID_AR_FILTER:
            if (HIWORD(wParam) == EN_CHANGE) {
                GetWindowTextW(GetDlgItem(hWnd, ID_AR_FILTER), g_arFilter, TM_MAX_NAME);
                PopulateAutorunListView();
            }
            break;

        case ID_AR_SOURCE_COMBO:
            if (HIWORD(wParam) == CBN_SELCHANGE) {
                g_arSourceFilter = (int)SendMessageW(GetDlgItem(hWnd, ID_AR_SOURCE_COMBO), CB_GETCURSEL, 0, 0);
                PopulateAutorunListView();
            }
            break;

        case ID_AR_CHK_ENABLED:
            g_showOnlyEnabled = (SendMessageW(GetDlgItem(hWnd, ID_AR_CHK_ENABLED), BM_GETCHECK, 0, 0) == BST_CHECKED);
            PopulateAutorunListView();
            break;

        case ID_AR_CHK_UNSIGNED:
            g_showOnlyUnsigned = (SendMessageW(GetDlgItem(hWnd, ID_AR_CHK_UNSIGNED), BM_GETCHECK, 0, 0) == BST_CHECKED);
            PopulateAutorunListView();
            break;

        case ID_AR_UNDO:
            AR_PerformUndo(hWnd);
            StartAutorunRefresh();
            break;

        case ID_AR_EXPORT:
            AM_ExportToCSV(hWnd);
            break;

        case ID_AR_PROPERTIES:
        {
            int index = GetSelectedAutorunIndex();
            if (index >= 0 && index < g_autorunCount)
                AM_ShowProperties(hWnd, &g_autoruns[index]);
            else
                MessageBoxW(hWnd, L"Please select an autorun entry.", L"Information", MB_ICONINFORMATION);
        }
        break;

        case ID_AR_ENABLE:
        case ID_AR_DISABLE:
        {
            int index = GetSelectedAutorunIndex();
            if (index >= 0 && index < g_autorunCount) {
                BOOL enable = (LOWORD(wParam) == ID_AR_ENABLE);
                if (!enable) AR_PushUndo(&g_autoruns[index], UNDO_ACTION_DISABLE);
                if (AM_EnableDisableAutorun(&g_autoruns[index], enable)) {
                    TM_LogAction(enable ? L"AR_ENABLE" : L"AR_DISABLE", g_autoruns[index].name);
                    g_autoruns[index].enabled = enable;
                    PopulateAutorunListView();
                } else {
                    MessageBoxW(hWnd, L"Failed to change autorun state. Insufficient privileges.",
                                L"Error", MB_ICONERROR);
                }
            } else {
                MessageBoxW(hWnd, L"Please select an autorun entry.", L"Information", MB_ICONINFORMATION);
            }
        }
        break;

        case ID_AR_REMOVE:
        {
            int index = GetSelectedAutorunIndex();
            if (index >= 0 && index < g_autorunCount) {
                wchar_t msg[512];
                wsprintfW(msg, L"Are you sure you want to delete autorun \"%s\"?", g_autoruns[index].name);
                if (MessageBoxW(hWnd, msg, L"Confirm Deletion", MB_YESNO | MB_ICONQUESTION) == IDYES) {
                    AR_PushUndo(&g_autoruns[index], UNDO_ACTION_DELETE);
                    if (AM_RemoveAutorun(&g_autoruns[index])) {
                        TM_LogAction(L"AR_REMOVE", g_autoruns[index].name);
                        RefreshAutorunList();
                        MessageBoxW(hWnd, L"Autorun entry deleted successfully!", L"Success", MB_ICONINFORMATION);
                    } else {
                        MessageBoxW(hWnd,
                            L"Failed to remove autorun entry.\r\n\r\n"
                            L"Possible reasons:\r\n"
                            L"\x2022 Protected system entry\r\n"
                            L"\x2022 File is in use by another process\r\n"
                            L"\x2022 Registry key is protected\r\n"
                            L"\x2022 Service cannot be stopped\r\n\r\n"
                            L"Try:\r\n"
                            L"\x2022 Disable the entry instead of deleting\r\n"
                            L"\x2022 Restart and try again\r\n"
                            L"\x2022 Check if the process is running",
                            L"Deletion Failed", MB_ICONWARNING);
                    }
                }
            } else {
                MessageBoxW(hWnd, L"Please select an autorun entry.", L"Information", MB_ICONINFORMATION);
            }
        }
        break;

        case ID_AR_FORCE_DELETE:
        {
            int index = GetSelectedAutorunIndex();
            if (index >= 0 && index < g_autorunCount) {
                wchar_t msg[512];
                wsprintfW(msg,
                    L"FORCE DELETE WARNING!\r\n\r\n"
                    L"This will attempt to forcefully remove:\r\n"
                    L"\"%s\"\r\n\r\n"
                    L"This action may:\r\n"
                    L"\x2022 Mark files for deletion on next reboot\r\n"
                    L"\x2022 Modify system security settings\r\n"
                    L"\x2022 Stop and delete system services\r\n\r\n"
                    L"Are you ABSOLUTELY SURE?", g_autoruns[index].name);
                if (MessageBoxW(hWnd, msg, L"FORCE DELETE - DANGER!",
                        MB_YESNO | MB_ICONEXCLAMATION | MB_DEFBUTTON2) == IDYES) {
                    AR_PushUndo(&g_autoruns[index], UNDO_ACTION_FORCE_DELETE);
                    if (AM_ForceRemoveAutorun(&g_autoruns[index])) {
                        TM_LogAction(L"AR_FORCE_DELETE", g_autoruns[index].name);
                        RefreshAutorunList();
                        MessageBoxW(hWnd, L"Force deletion completed. Some changes may require a reboot.",
                                    L"Force Delete Complete", MB_ICONINFORMATION);
                    } else {
                        MessageBoxW(hWnd, L"Force deletion failed. The entry may be too deeply protected by the system.",
                                    L"Force Delete Failed", MB_ICONERROR);
                    }
                }
            } else {
                MessageBoxW(hWnd, L"Please select an autorun entry.", L"Information", MB_ICONINFORMATION);
            }
        }
        break;

        case ID_AR_OPENFOLDER:
        {
            int index = GetSelectedAutorunIndex();
            if (index >= 0 && index < g_autorunCount)
                AM_OpenFileLocation(hWnd, &g_autoruns[index]);
            else
                MessageBoxW(hWnd, L"Please select an autorun entry.", L"Information", MB_ICONINFORMATION);
        }
        break;
        }
    }
    break;

    case WM_DESTROY:
        SaveColumnWidths(L"AR", g_hArListView, 7);
        g_hAutorunWnd = NULL;
        break;

    default:
        return DefWindowProcA(hWnd, msg, wParam, lParam);
    }
    return 0;
}

/* ========================================================================= */
/* TASK SCHEDULER WINDOW PROCEDURE                                           */
/* ========================================================================= */

/* ========================================================================= */
/* CREATE TASK DIALOG                                                        */
/* ========================================================================= */

static LRESULT CALLBACK CreateTaskDlgProc(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    switch (msg) {
    case WM_CREATE:
    {
        CreateTaskParams *p = (CreateTaskParams*)((CREATESTRUCTW*)lParam)->lpCreateParams;
        HWND hCombo;
        INITCOMMONCONTROLSEX icex;
        int y = 15;
        SetWindowLongPtrW(hWnd, GWLP_USERDATA, (LONG_PTR)p);

        icex.dwSize = sizeof(icex);
        icex.dwICC = ICC_DATE_CLASSES;
        InitCommonControlsEx(&icex);

        CreateWindowExW(0, L"STATIC", L"Task Name:", WS_CHILD|WS_VISIBLE, 15, y, 100, 20, hWnd, NULL, g_hInst, NULL);
        CreateWindowExW(WS_EX_CLIENTEDGE, L"EDIT", L"", WS_CHILD|WS_VISIBLE|ES_AUTOHSCROLL, 120, y, 340, 22, hWnd, (HMENU)ID_CTD_NAME, g_hInst, NULL);
        y += 32;

        CreateWindowExW(0, L"STATIC", L"Executable:", WS_CHILD|WS_VISIBLE, 15, y, 100, 20, hWnd, NULL, g_hInst, NULL);
        CreateWindowExW(WS_EX_CLIENTEDGE, L"EDIT", L"", WS_CHILD|WS_VISIBLE|ES_AUTOHSCROLL, 120, y, 300, 22, hWnd, (HMENU)ID_CTD_EXECUTABLE, g_hInst, NULL);
        CreateWindowExW(0, L"BUTTON", L"...", WS_CHILD|WS_VISIBLE, 425, y, 35, 22, hWnd, (HMENU)ID_CTD_BROWSE, g_hInst, NULL);
        y += 32;

        CreateWindowExW(0, L"STATIC", L"Arguments:", WS_CHILD|WS_VISIBLE, 15, y, 100, 20, hWnd, NULL, g_hInst, NULL);
        CreateWindowExW(WS_EX_CLIENTEDGE, L"EDIT", L"", WS_CHILD|WS_VISIBLE|ES_AUTOHSCROLL, 120, y, 340, 22, hWnd, (HMENU)ID_CTD_ARGUMENTS, g_hInst, NULL);
        y += 32;

        CreateWindowExW(0, L"STATIC", L"Description:", WS_CHILD|WS_VISIBLE, 15, y, 100, 20, hWnd, NULL, g_hInst, NULL);
        CreateWindowExW(WS_EX_CLIENTEDGE, L"EDIT", L"", WS_CHILD|WS_VISIBLE|ES_MULTILINE|ES_AUTOVSCROLL|WS_VSCROLL, 120, y, 340, 60, hWnd, (HMENU)ID_CTD_DESCRIPTION, g_hInst, NULL);
        y += 70;

        CreateWindowExW(0, L"STATIC", L"Trigger:", WS_CHILD|WS_VISIBLE, 15, y, 100, 20, hWnd, NULL, g_hInst, NULL);
        hCombo = CreateWindowExW(0, L"COMBOBOX", L"", WS_CHILD|WS_VISIBLE|CBS_DROPDOWNLIST|WS_VSCROLL,
            120, y, 200, 200, hWnd, (HMENU)ID_CTD_TRIGGER_COMBO, g_hInst, NULL);
        SendMessageW(hCombo, CB_ADDSTRING, 0, (LPARAM)L"Once");
        SendMessageW(hCombo, CB_ADDSTRING, 0, (LPARAM)L"Daily");
        SendMessageW(hCombo, CB_ADDSTRING, 0, (LPARAM)L"Weekly");
        SendMessageW(hCombo, CB_ADDSTRING, 0, (LPARAM)L"At Logon");
        SendMessageW(hCombo, CB_ADDSTRING, 0, (LPARAM)L"At Startup");
        SendMessageW(hCombo, CB_SETCURSEL, 0, 0);
        y += 32;

        CreateWindowExW(0, L"STATIC", L"Date:", WS_CHILD|WS_VISIBLE, 15, y, 100, 20, hWnd, (HMENU)6001, g_hInst, NULL);
        CreateWindowExW(0, DATETIMEPICK_CLASSW, L"", WS_CHILD|WS_VISIBLE|DTS_SHORTDATECENTURYFORMAT,
            120, y, 160, 24, hWnd, (HMENU)ID_CTD_DATE, g_hInst, NULL);
        CreateWindowExW(0, L"STATIC", L"Time:", WS_CHILD|WS_VISIBLE, 295, y, 40, 20, hWnd, (HMENU)6002, g_hInst, NULL);
        CreateWindowExW(0, DATETIMEPICK_CLASSW, L"", WS_CHILD|WS_VISIBLE|DTS_TIMEFORMAT,
            340, y, 120, 24, hWnd, (HMENU)ID_CTD_TIME, g_hInst, NULL);
        y += 35;

        CreateWindowExW(0, L"BUTTON", L"Run as Administrator", WS_CHILD|WS_VISIBLE|BS_AUTOCHECKBOX,
            120, y, 200, 20, hWnd, (HMENU)ID_CTD_ADMIN, g_hInst, NULL);
        y += 35;

        CreateWindowExW(0, L"BUTTON", L"OK", WS_CHILD|WS_VISIBLE|BS_DEFPUSHBUTTON,
            120, y, 100, 30, hWnd, (HMENU)ID_CTD_OK, g_hInst, NULL);
        CreateWindowExW(0, L"BUTTON", L"Cancel", WS_CHILD|WS_VISIBLE,
            235, y, 100, 30, hWnd, (HMENU)ID_CTD_CANCEL, g_hInst, NULL);

        if (g_darkTheme) ApplyDarkTheme(hWnd);
    }
    break;

    case WM_COMMAND:
    {
        switch (LOWORD(wParam)) {
        case ID_CTD_BROWSE:
        {
            wchar_t exe[MAX_PATH];
            OPENFILENAMEW ofn;
            exe[0] = 0;
            memset(&ofn, 0, sizeof(ofn));
            ofn.lStructSize = sizeof(ofn);
            ofn.hwndOwner = hWnd;
            ofn.lpstrFile = exe;
            ofn.nMaxFile = MAX_PATH;
            ofn.lpstrFilter = L"Executable Files\0*.exe\0All Files\0*.*\0";
            ofn.Flags = OFN_PATHMUSTEXIST | OFN_FILEMUSTEXIST;
            if (GetOpenFileNameW(&ofn))
                SetWindowTextW(GetDlgItem(hWnd, ID_CTD_EXECUTABLE), exe);
        }
        break;

        case ID_CTD_TRIGGER_COMBO:
            if (HIWORD(wParam) == CBN_SELCHANGE) {
                int sel = (int)SendMessageW(GetDlgItem(hWnd, ID_CTD_TRIGGER_COMBO), CB_GETCURSEL, 0, 0);
                BOOL showDT = (sel <= 2); /* Once, Daily, Weekly show date/time */
                ShowWindow(GetDlgItem(hWnd, ID_CTD_DATE), showDT ? SW_SHOW : SW_HIDE);
                ShowWindow(GetDlgItem(hWnd, ID_CTD_TIME), showDT ? SW_SHOW : SW_HIDE);
                ShowWindow(GetDlgItem(hWnd, 6001), showDT ? SW_SHOW : SW_HIDE);
                ShowWindow(GetDlgItem(hWnd, 6002), showDT ? SW_SHOW : SW_HIDE);
            }
            break;

        case ID_CTD_OK:
        {
            CreateTaskParams *p = (CreateTaskParams*)GetWindowLongPtrW(hWnd, GWLP_USERDATA);
            GetWindowTextW(GetDlgItem(hWnd, ID_CTD_NAME), p->name, TM_MAX_NAME);
            GetWindowTextW(GetDlgItem(hWnd, ID_CTD_EXECUTABLE), p->executable, TM_MAX_PATH_BUF);
            GetWindowTextW(GetDlgItem(hWnd, ID_CTD_ARGUMENTS), p->arguments, TM_MAX_ARGS);
            GetWindowTextW(GetDlgItem(hWnd, ID_CTD_DESCRIPTION), p->description, TM_MAX_DESC);
            p->triggerType = (int)SendMessageW(GetDlgItem(hWnd, ID_CTD_TRIGGER_COMBO), CB_GETCURSEL, 0, 0);
            p->runAsAdmin = (SendMessageW(GetDlgItem(hWnd, ID_CTD_ADMIN), BM_GETCHECK, 0, 0) == BST_CHECKED);

            /* Get date and time from pickers */
            SendMessageW(GetDlgItem(hWnd, ID_CTD_DATE), DTM_GETSYSTEMTIME, 0, (LPARAM)&p->schedTime);
            {
                SYSTEMTIME st;
                SendMessageW(GetDlgItem(hWnd, ID_CTD_TIME), DTM_GETSYSTEMTIME, 0, (LPARAM)&st);
                p->schedTime.wHour = st.wHour;
                p->schedTime.wMinute = st.wMinute;
                p->schedTime.wSecond = st.wSecond;
            }

            if (!p->name[0]) { MessageBoxW(hWnd, L"Task name is required.", L"Validation", MB_ICONWARNING); break; }
            if (!p->executable[0]) { MessageBoxW(hWnd, L"Executable is required.", L"Validation", MB_ICONWARNING); break; }

            p->confirmed = TRUE;
            DestroyWindow(hWnd);
        }
        break;

        case ID_CTD_CANCEL:
            DestroyWindow(hWnd);
            break;
        }
    }
    break;

    case WM_CTLCOLORSTATIC:
    case WM_CTLCOLOREDIT:
    case WM_CTLCOLORBTN:
        if (g_darkTheme) {
            HDC hdc = (HDC)wParam;
            SetTextColor(hdc, RGB(220, 220, 220));
            SetBkColor(hdc, RGB(45, 45, 45));
            return (LRESULT)g_hDarkEditBrush;
        }
        break;

    case WM_ERASEBKGND:
        if (g_darkTheme) {
            RECT rc;
            GetClientRect(hWnd, &rc);
            FillRect((HDC)wParam, &rc, g_hDarkBgBrush);
            return 1;
        }
        break;

    default:
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }
    return 0;
}

static void ShowCreateTaskDialogEx(HWND hParent) {
    CreateTaskParams params;
    HWND hDlg;
    MSG msg;

    memset(&params, 0, sizeof(params));
    GetLocalTime(&params.schedTime);

    hDlg = CreateWindowExW(WS_EX_DLGMODALFRAME, L"CreateTaskDlgClass", L"Create Scheduled Task",
        WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_VISIBLE,
        CW_USEDEFAULT, CW_USEDEFAULT, 490, 430,
        hParent, NULL, g_hInst, &params);

    if (!hDlg) {
        MessageBoxW(hParent, L"Failed to create task dialog.", L"Error", MB_ICONERROR);
        return;
    }

    EnableWindow(hParent, FALSE);
    while (IsWindow(hDlg) && GetMessageW(&msg, NULL, 0, 0) > 0) {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }
    EnableWindow(hParent, TRUE);
    SetForegroundWindow(hParent);

    if (params.confirmed) {
        if (TS_CreateTaskEx(params.name, params.executable, params.arguments,
                params.description, params.triggerType, &params.schedTime, params.runAsAdmin)) {
            TM_LogAction(L"TS_CREATE", params.name);
            MessageBoxW(hParent, L"Task created successfully!", L"Success", MB_ICONINFORMATION);
            RefreshTaskSchedulerList();
        } else {
            MessageBoxW(hParent, L"Failed to create task. Make sure you have administrator privileges.",
                        L"Error", MB_ICONERROR);
        }
    }
}

static void ExportSelectedTask(HWND hParent) {
    int index = GetSelectedTaskIndex();
    OPENFILENAMEW ofn;
    wchar_t filePath[MAX_PATH];
    if (index < 0 || index >= g_taskCount) {
        MessageBoxW(hParent, L"Please select a task.", L"Export", MB_ICONINFORMATION);
        return;
    }
    filePath[0] = 0;
    memset(&ofn, 0, sizeof(ofn));
    ofn.lStructSize = sizeof(ofn);
    ofn.hwndOwner = hParent;
    ofn.lpstrFile = filePath;
    ofn.nMaxFile = MAX_PATH;
    ofn.lpstrFilter = L"XML Files\0*.xml\0All Files\0*.*\0";
    ofn.lpstrDefExt = L"xml";
    ofn.lpstrTitle = L"Export Task as XML";
    ofn.Flags = OFN_OVERWRITEPROMPT;
    if (GetSaveFileNameW(&ofn)) {
        if (TS_ExportTaskXml(g_tasks[index].path, filePath))
            MessageBoxW(hParent, L"Task exported successfully!", L"Export", MB_ICONINFORMATION);
        else
            MessageBoxW(hParent, L"Failed to export task.", L"Error", MB_ICONERROR);
    }
}

static void ImportTaskFromFile(HWND hParent) {
    OPENFILENAMEW ofn;
    wchar_t filePath[MAX_PATH];
    filePath[0] = 0;
    memset(&ofn, 0, sizeof(ofn));
    ofn.lStructSize = sizeof(ofn);
    ofn.hwndOwner = hParent;
    ofn.lpstrFile = filePath;
    ofn.nMaxFile = MAX_PATH;
    ofn.lpstrFilter = L"XML Files\0*.xml\0All Files\0*.*\0";
    ofn.lpstrTitle = L"Import Task from XML";
    ofn.Flags = OFN_PATHMUSTEXIST | OFN_FILEMUSTEXIST;
    if (GetOpenFileNameW(&ofn)) {
        if (TS_ImportTaskXml(filePath)) {
            MessageBoxW(hParent, L"Task imported successfully!", L"Import", MB_ICONINFORMATION);
            RefreshTaskSchedulerList();
        } else {
            MessageBoxW(hParent, L"Failed to import task. Check XML format and permissions.", L"Error", MB_ICONERROR);
        }
    }
}

static LRESULT CALLBACK TaskSchedulerWndProc(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    switch (msg) {
    case WM_CREATE:
    {
        g_hTaskSchedulerWnd = hWnd;

        if (!TS_Initialize()) {
            MessageBoxW(hWnd, L"Failed to initialize Task Scheduler. The window will close.", L"Error", MB_ICONERROR);
            PostMessageW(hWnd, WM_CLOSE, 0, 0);
            return -1;
        }

        CreateTaskSchedulerListView(hWnd);

        g_hTsStatusBar = CreateWindowExW(0, STATUSCLASSNAMEW, L"",
            WS_CHILD | WS_VISIBLE | SBARS_SIZEGRIP,
            0, 0, 0, 0, hWnd, NULL, g_hInst, NULL);

        CreateWindowExW(0, L"BUTTON", L"Refresh",
            WS_CHILD | WS_VISIBLE | BS_DEFPUSHBUTTON,
            0, 0, 100, 30, hWnd, (HMENU)ID_TS_REFRESH, g_hInst, NULL);
        CreateWindowExW(0, L"BUTTON", L"Create Task",
            WS_CHILD | WS_VISIBLE, 0, 0, 100, 30, hWnd, (HMENU)ID_TS_CREATE, g_hInst, NULL);
        CreateWindowExW(0, L"BUTTON", L"Delete",
            WS_CHILD | WS_VISIBLE, 0, 0, 100, 30, hWnd, (HMENU)ID_TS_DELETE, g_hInst, NULL);
        CreateWindowExW(0, L"BUTTON", L"Enable",
            WS_CHILD | WS_VISIBLE, 0, 0, 100, 30, hWnd, (HMENU)ID_TS_ENABLE, g_hInst, NULL);
        CreateWindowExW(0, L"BUTTON", L"Disable",
            WS_CHILD | WS_VISIBLE, 0, 0, 100, 30, hWnd, (HMENU)ID_TS_DISABLE, g_hInst, NULL);
        CreateWindowExW(0, L"BUTTON", L"Run Now",
            WS_CHILD | WS_VISIBLE, 0, 0, 100, 30, hWnd, (HMENU)ID_TS_RUN, g_hInst, NULL);
        CreateWindowExW(0, L"BUTTON", L"Stop",
            WS_CHILD | WS_VISIBLE, 0, 0, 100, 30, hWnd, (HMENU)ID_TS_STOP, g_hInst, NULL);
        CreateWindowExW(0, L"BUTTON", L"Properties",
            WS_CHILD | WS_VISIBLE, 0, 0, 100, 30, hWnd, (HMENU)ID_TS_PROPERTIES, g_hInst, NULL);
        CreateWindowExW(0, L"BUTTON", L"Export XML",
            WS_CHILD | WS_VISIBLE, 0, 0, 100, 30, hWnd, (HMENU)ID_TS_EXPORT, g_hInst, NULL);
        CreateWindowExW(0, L"BUTTON", L"Import XML",
            WS_CHILD | WS_VISIBLE, 0, 0, 100, 30, hWnd, (HMENU)ID_TS_IMPORT, g_hInst, NULL);

        LoadColumnWidths(L"TS", g_hTsListView, 7);
        if (g_darkTheme) ApplyDarkTheme(hWnd);
        StartTaskSchedulerRefresh();
    }
    break;

    case WM_SIZE:
    {
        if (wParam != SIZE_MINIMIZED) {
            RECT rc;
            int w, h, statusHeight, listW, listH, btnY, btnSpacing, btnWidth, btnHeight, totalBtnWidth, startX;
            HWND hBtn;

            GetClientRect(hWnd, &rc);
            w = rc.right - rc.left;
            h = rc.bottom - rc.top;
            statusHeight = 25;

            MoveWindow(g_hTsStatusBar, 0, h - statusHeight, w, statusHeight, TRUE);

            listW = w - 20; listH = h - statusHeight - 80;
            if (listW < 50) listW = 50;
            if (listH < 50) listH = 50;
            MoveWindow(g_hTsListView, 10, 10, listW, listH, TRUE);

            btnY = listH + 20;
            btnSpacing = 8; btnWidth = 85; btnHeight = 30;
            totalBtnWidth = 10 * btnWidth + 9 * btnSpacing;
            startX = (w - totalBtnWidth) / 2;
            if (startX < 10) startX = 10;

            hBtn = GetDlgItem(hWnd, ID_TS_REFRESH);
            if (hBtn) { MoveWindow(hBtn, startX, btnY, btnWidth, btnHeight, TRUE); startX += btnWidth + btnSpacing; }
            hBtn = GetDlgItem(hWnd, ID_TS_CREATE);
            if (hBtn) { MoveWindow(hBtn, startX, btnY, btnWidth, btnHeight, TRUE); startX += btnWidth + btnSpacing; }
            hBtn = GetDlgItem(hWnd, ID_TS_DELETE);
            if (hBtn) { MoveWindow(hBtn, startX, btnY, btnWidth, btnHeight, TRUE); startX += btnWidth + btnSpacing; }
            hBtn = GetDlgItem(hWnd, ID_TS_ENABLE);
            if (hBtn) { MoveWindow(hBtn, startX, btnY, btnWidth, btnHeight, TRUE); startX += btnWidth + btnSpacing; }
            hBtn = GetDlgItem(hWnd, ID_TS_DISABLE);
            if (hBtn) { MoveWindow(hBtn, startX, btnY, btnWidth, btnHeight, TRUE); startX += btnWidth + btnSpacing; }
            hBtn = GetDlgItem(hWnd, ID_TS_RUN);
            if (hBtn) { MoveWindow(hBtn, startX, btnY, btnWidth, btnHeight, TRUE); startX += btnWidth + btnSpacing; }
            hBtn = GetDlgItem(hWnd, ID_TS_STOP);
            if (hBtn) { MoveWindow(hBtn, startX, btnY, btnWidth, btnHeight, TRUE); startX += btnWidth + btnSpacing; }
            hBtn = GetDlgItem(hWnd, ID_TS_PROPERTIES);
            if (hBtn) { MoveWindow(hBtn, startX, btnY, btnWidth, btnHeight, TRUE); startX += btnWidth + btnSpacing; }
            hBtn = GetDlgItem(hWnd, ID_TS_EXPORT);
            if (hBtn) { MoveWindow(hBtn, startX, btnY, btnWidth, btnHeight, TRUE); startX += btnWidth + btnSpacing; }
            hBtn = GetDlgItem(hWnd, ID_TS_IMPORT);
            if (hBtn) { MoveWindow(hBtn, startX, btnY, btnWidth, btnHeight, TRUE); }
        }
    }
    break;

    case WM_NOTIFY:
    {
        LPNMHDR pnmh = (LPNMHDR)lParam;
        if (pnmh->hwndFrom == g_hTsListView && pnmh->code == LVN_COLUMNCLICK) {
            LPNMLISTVIEW pnmlv = (LPNMLISTVIEW)lParam;
            OnTaskSchedulerColumnClick(pnmlv->iSubItem);
        }
        if (pnmh->hwndFrom == g_hTsListView && pnmh->code == NM_CUSTOMDRAW && g_darkTheme) {
            LPNMLVCUSTOMDRAW lpcd = (LPNMLVCUSTOMDRAW)lParam;
            switch (lpcd->nmcd.dwDrawStage) {
            case CDDS_PREPAINT: return CDRF_NOTIFYITEMDRAW;
            case CDDS_ITEMPREPAINT:
                lpcd->clrText = RGB(220, 220, 220);
                lpcd->clrTextBk = RGB(30, 30, 30);
                return CDRF_DODEFAULT;
            }
        }
    }
    break;

    case WM_CONTEXTMENU:
    {
        HWND hTarget = (HWND)wParam;
        if (hTarget == g_hTsListView) {
            int x = GET_X_LPARAM(lParam), y = GET_Y_LPARAM(lParam);
            HMENU hMenu = CreatePopupMenu();
            if (x == -1 && y == -1) {
                RECT rc; int sel = ListView_GetNextItem(g_hTsListView, -1, LVNI_SELECTED);
                if (sel >= 0) { ListView_GetItemRect(g_hTsListView, sel, &rc, LVIR_BOUNDS); }
                else { GetClientRect(g_hTsListView, &rc); }
                x = rc.left; y = rc.bottom;
                ClientToScreen(g_hTsListView, (POINT*)&x);
            }
            AppendMenuW(hMenu, MF_STRING, ID_TS_REFRESH, L"Refresh");
            AppendMenuW(hMenu, MF_SEPARATOR, 0, NULL);
            AppendMenuW(hMenu, MF_STRING, ID_TS_CREATE, L"Create Task");
            AppendMenuW(hMenu, MF_STRING, ID_TS_DELETE, L"Delete");
            AppendMenuW(hMenu, MF_SEPARATOR, 0, NULL);
            AppendMenuW(hMenu, MF_STRING, ID_TS_ENABLE, L"Enable");
            AppendMenuW(hMenu, MF_STRING, ID_TS_DISABLE, L"Disable");
            AppendMenuW(hMenu, MF_STRING, ID_TS_RUN, L"Run Now");
            AppendMenuW(hMenu, MF_STRING, ID_TS_STOP, L"Stop");
            AppendMenuW(hMenu, MF_SEPARATOR, 0, NULL);
            AppendMenuW(hMenu, MF_STRING, ID_TS_PROPERTIES, L"Properties");
            AppendMenuW(hMenu, MF_STRING, ID_TS_EXPORT, L"Export XML");
            AppendMenuW(hMenu, MF_STRING, ID_TS_IMPORT, L"Import XML");
            TrackPopupMenu(hMenu, TPM_RIGHTBUTTON, x, y, 0, hWnd, NULL);
            DestroyMenu(hMenu);
        }
    }
    break;

    case WM_CTLCOLORSTATIC:
    case WM_CTLCOLOREDIT:
    case WM_CTLCOLORBTN:
        if (g_darkTheme) {
            HDC hdc = (HDC)wParam;
            SetTextColor(hdc, RGB(220, 220, 220));
            SetBkColor(hdc, RGB(45, 45, 45));
            return (LRESULT)g_hDarkEditBrush;
        }
        break;

    case WM_ERASEBKGND:
        if (g_darkTheme) {
            RECT rc;
            GetClientRect(hWnd, &rc);
            FillRect((HDC)wParam, &rc, g_hDarkBgBrush);
            return 1;
        }
        break;

    case WM_COMMAND:
    {
        switch (LOWORD(wParam)) {
        case ID_TS_REFRESH:   StartTaskSchedulerRefresh(); break;
        case ID_TS_CREATE:    ShowCreateTaskDialogEx(hWnd); break;
        case ID_TS_DELETE:    DeleteSelectedTask(hWnd); break;
        case ID_TS_ENABLE:    EnableDisableSelectedTask(hWnd, TRUE); break;
        case ID_TS_DISABLE:   EnableDisableSelectedTask(hWnd, FALSE); break;
        case ID_TS_RUN:       RunSelectedTask(hWnd); break;
        case ID_TS_STOP:      StopSelectedTask(hWnd); break;
        case ID_TS_PROPERTIES:ShowSelectedTaskProperties(hWnd); break;
        case ID_TS_EXPORT:    ExportSelectedTask(hWnd); break;
        case ID_TS_IMPORT:    ImportTaskFromFile(hWnd); break;
        }
    }
    break;

    case WM_DESTROY:
        SaveColumnWidths(L"TS", g_hTsListView, 7);
        TS_Cleanup();
        g_hTaskSchedulerWnd = NULL;
        break;

    default:
        return DefWindowProcA(hWnd, msg, wParam, lParam);
    }
    return 0;
}

/* ========================================================================= */
/* PROCESS TREE WINDOW PROCEDURE                                             */
/* ========================================================================= */

static LRESULT CALLBACK ProcessTreeWndProc(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    switch (msg) {
    case WM_CREATE:
    {
        typedef struct { DWORD pid; HTREEITEM hItem; } PidItem;
        HWND hTree;
        TVINSERTSTRUCT tvins;
        PidItem *items;
        int i, j, itemCount;

        hTree = CreateWindowExW(WS_EX_CLIENTEDGE, WC_TREEVIEWW, L"",
            WS_CHILD | WS_VISIBLE | TVS_HASLINES | TVS_LINESATROOT | TVS_HASBUTTONS,
            0, 0, 0, 0, hWnd, (HMENU)9000, g_hInst, NULL);
        if (!hTree) break;

        EnterCriticalSection(&g_dataLock);
        items = (PidItem *)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, g_processCount * sizeof(PidItem));
        itemCount = g_processCount;

        /* First pass: insert root nodes (processes whose parent is not in our list) */
        memset(&tvins, 0, sizeof(tvins));
        tvins.hInsertAfter = TVI_LAST;
        for (i = 0; i < itemCount; i++) {
            BOOL parentFound = FALSE;
            for (j = 0; j < itemCount; j++) {
                if (j != i && g_processes[i].parentPid == g_processes[j].pid && g_processes[i].parentPid != 0) {
                    parentFound = TRUE;
                    break;
                }
            }
            if (!parentFound) {
                wchar_t label[384];
                wsprintfW(label, L"%s [PID: %u]", g_processes[i].exeName, g_processes[i].pid);
                tvins.hParent = TVI_ROOT;
                tvins.item.mask = TVIF_TEXT;
                tvins.item.pszText = label;
                items[i].pid = g_processes[i].pid;
                items[i].hItem = TreeView_InsertItem(hTree, &tvins);
            } else {
                items[i].pid = g_processes[i].pid;
                items[i].hItem = NULL;
            }
        }

        /* Second pass: insert children under their parents */
        for (i = 0; i < itemCount; i++) {
            if (items[i].hItem == NULL) {
                HTREEITEM hParent = TVI_ROOT;
                wchar_t label[384];
                for (j = 0; j < itemCount; j++) {
                    if (items[j].pid == g_processes[i].parentPid && items[j].hItem != NULL) {
                        hParent = items[j].hItem;
                        break;
                    }
                }
                wsprintfW(label, L"%s [PID: %u]", g_processes[i].exeName, g_processes[i].pid);
                tvins.hParent = hParent;
                tvins.item.mask = TVIF_TEXT;
                tvins.item.pszText = label;
                items[i].hItem = TreeView_InsertItem(hTree, &tvins);
            }
        }

        LeaveCriticalSection(&g_dataLock);
        HeapFree(GetProcessHeap(), 0, items);

        SetWindowLongPtrW(hWnd, GWLP_USERDATA, (LONG_PTR)hTree);
    }
    break;

    case WM_SIZE:
    {
        HWND hTree = (HWND)GetWindowLongPtrW(hWnd, GWLP_USERDATA);
        if (hTree) {
            RECT rc;
            GetClientRect(hWnd, &rc);
            MoveWindow(hTree, 0, 0, rc.right, rc.bottom, TRUE);
        }
    }
    break;

    case WM_DESTROY:
        break;

    default:
        return DefWindowProcA(hWnd, msg, wParam, lParam);
    }
    return 0;
}

/* ========================================================================= */
/* WINDOW CLASS REGISTRATION                                                 */
/* ========================================================================= */

/* ========================================================================= */
/* GRAPH WINDOW PROCEDURE                                                    */
/* ========================================================================= */

static LRESULT CALLBACK GraphWndProc(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    switch (msg) {
    case WM_CREATE:
        g_hGraphWnd = hWnd;
        break;

    case WM_PAINT:
    {
        PAINTSTRUCT ps;
        HDC hdc = BeginPaint(hWnd, &ps);
        RECT rc;
        int w, h, halfH, i, x, y, prevX, prevY, idx;
        HPEN hPenCpu, hPenRam, hPenGrid, hOldPen;
        HBRUSH hBgBrush;

        GetClientRect(hWnd, &rc);
        w = rc.right; h = rc.bottom;
        halfH = h / 2;

        /* Background */
        hBgBrush = CreateSolidBrush(g_darkTheme ? RGB(30,30,30) : RGB(255,255,255));
        FillRect(hdc, &rc, hBgBrush);
        DeleteObject(hBgBrush);

        /* Grid lines */
        hPenGrid = CreatePen(PS_DOT, 1, RGB(128,128,128));
        hOldPen = (HPEN)SelectObject(hdc, hPenGrid);
        for (i = 1; i <= 3; i++) {
            /* CPU section grid */
            MoveToEx(hdc, 0, halfH - (halfH * i / 4), NULL);
            LineTo(hdc, w, halfH - (halfH * i / 4));
            /* RAM section grid */
            MoveToEx(hdc, 0, h - (halfH * i / 4), NULL);
            LineTo(hdc, w, h - (halfH * i / 4));
        }
        /* Separator */
        MoveToEx(hdc, 0, halfH, NULL); LineTo(hdc, w, halfH);
        SelectObject(hdc, hOldPen);
        DeleteObject(hPenGrid);

        /* Labels */
        SetBkMode(hdc, TRANSPARENT);
        SetTextColor(hdc, g_darkTheme ? RGB(220,220,220) : RGB(0,0,0));
        TextOutW(hdc, 5, 2, L"CPU %", 5);
        TextOutW(hdc, 5, halfH + 2, L"RAM %", 5);

        /* CPU line */
        hPenCpu = CreatePen(PS_SOLID, 2, RGB(0, 200, 0));
        SelectObject(hdc, hPenCpu);
        prevX = -1; prevY = -1;
        for (i = 0; i < GRAPH_HISTORY_SIZE; i++) {
            idx = (g_graphIndex + i) % GRAPH_HISTORY_SIZE;
            x = (i * w) / GRAPH_HISTORY_SIZE;
            y = halfH - (int)(g_cpuHistory[idx] / 100.0 * (halfH - 4));
            if (y < 0) y = 0; if (y >= halfH) y = halfH - 1;
            if (prevX >= 0) { MoveToEx(hdc, prevX, prevY, NULL); LineTo(hdc, x, y); }
            prevX = x; prevY = y;
        }
        SelectObject(hdc, hOldPen);
        DeleteObject(hPenCpu);

        /* RAM line */
        hPenRam = CreatePen(PS_SOLID, 2, RGB(0, 100, 255));
        SelectObject(hdc, hPenRam);
        prevX = -1; prevY = -1;
        for (i = 0; i < GRAPH_HISTORY_SIZE; i++) {
            idx = (g_graphIndex + i) % GRAPH_HISTORY_SIZE;
            x = (i * w) / GRAPH_HISTORY_SIZE;
            y = h - (int)(g_ramHistory[idx] / 100.0 * (halfH - 4));
            if (y < halfH) y = halfH; if (y >= h) y = h - 1;
            if (prevX >= 0) { MoveToEx(hdc, prevX, prevY, NULL); LineTo(hdc, x, y); }
            prevX = x; prevY = y;
        }
        SelectObject(hdc, hOldPen);
        DeleteObject(hPenRam);

        EndPaint(hWnd, &ps);
    }
    return 0;

    case WM_ERASEBKGND:
        return 1; /* handled in WM_PAINT */

    case WM_DESTROY:
        g_hGraphWnd = NULL;
        break;

    default:
        return DefWindowProcA(hWnd, msg, wParam, lParam);
    }
    return 0;
}

/* ========================================================================= */
/* NETWORK DETAIL WINDOW PROCEDURE                                           */
/* ========================================================================= */

static const wchar_t *TcpStateStr(DWORD state) {
    switch (state) {
    case 1: return L"CLOSED";
    case 2: return L"LISTEN";
    case 3: return L"SYN_SENT";
    case 4: return L"SYN_RCVD";
    case 5: return L"ESTABLISHED";
    case 6: return L"FIN_WAIT1";
    case 7: return L"FIN_WAIT2";
    case 8: return L"CLOSE_WAIT";
    case 9: return L"CLOSING";
    case 10: return L"LAST_ACK";
    case 11: return L"TIME_WAIT";
    case 12: return L"DELETE_TCB";
    default: return L"UNKNOWN";
    }
}

static void PopulateNetDetail(HWND hListView, DWORD targetPid) {
    DWORD tcpSize = 0, udpSize = 0;
    PMIB_TCPTABLE_OWNER_PID tcpTable = NULL;
    PMIB_UDPTABLE_OWNER_PID udpTable = NULL;
    LVITEMW lvItem;
    LVITEMW lvSub;
    DWORD i;
    int row = 0;
    wchar_t buf[128];

    ListView_DeleteAllItems(hListView);
    memset(&lvItem, 0, sizeof(lvItem));
    lvItem.mask = LVIF_TEXT;
    memset(&lvSub, 0, sizeof(lvSub));
    lvSub.mask = LVIF_TEXT;

    /* TCP */
    GetExtendedTcpTable(NULL, &tcpSize, FALSE, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
    if (tcpSize > 0) {
        tcpTable = (PMIB_TCPTABLE_OWNER_PID)HeapAlloc(GetProcessHeap(), 0, tcpSize);
        if (tcpTable && GetExtendedTcpTable(tcpTable, &tcpSize, FALSE, AF_INET,
                TCP_TABLE_OWNER_PID_ALL, 0) == NO_ERROR) {
            for (i = 0; i < tcpTable->dwNumEntries; i++) {
                DWORD la, ra;
                WORD lp, rp;
                if (tcpTable->table[i].dwOwningPid != targetPid) continue;

                lvItem.iItem = row; lvItem.iSubItem = 0;
                lvItem.pszText = (LPWSTR)L"TCP";
                ListView_InsertItem(hListView, &lvItem);

                la = tcpTable->table[i].dwLocalAddr;
                lp = (WORD)((tcpTable->table[i].dwLocalPort >> 8) | (tcpTable->table[i].dwLocalPort << 8));
                wsprintfW(buf, L"%u.%u.%u.%u:%u", la&0xFF, (la>>8)&0xFF, (la>>16)&0xFF, (la>>24)&0xFF, lp);
                lvSub.iItem = row; lvSub.iSubItem = 1; lvSub.pszText = buf;
                ListView_SetItem(hListView, &lvSub);

                ra = tcpTable->table[i].dwRemoteAddr;
                rp = (WORD)((tcpTable->table[i].dwRemotePort >> 8) | (tcpTable->table[i].dwRemotePort << 8));
                wsprintfW(buf, L"%u.%u.%u.%u:%u", ra&0xFF, (ra>>8)&0xFF, (ra>>16)&0xFF, (ra>>24)&0xFF, rp);
                lvSub.iSubItem = 2; lvSub.pszText = buf;
                ListView_SetItem(hListView, &lvSub);

                lvSub.iSubItem = 3; lvSub.pszText = (LPWSTR)TcpStateStr(tcpTable->table[i].dwState);
                ListView_SetItem(hListView, &lvSub);
                row++;
            }
        }
        if (tcpTable) HeapFree(GetProcessHeap(), 0, tcpTable);
    }

    /* UDP */
    GetExtendedUdpTable(NULL, &udpSize, FALSE, AF_INET, UDP_TABLE_OWNER_PID, 0);
    if (udpSize > 0) {
        udpTable = (PMIB_UDPTABLE_OWNER_PID)HeapAlloc(GetProcessHeap(), 0, udpSize);
        if (udpTable && GetExtendedUdpTable(udpTable, &udpSize, FALSE, AF_INET,
                UDP_TABLE_OWNER_PID, 0) == NO_ERROR) {
            for (i = 0; i < udpTable->dwNumEntries; i++) {
                DWORD la;
                WORD lp;
                if (udpTable->table[i].dwOwningPid != targetPid) continue;

                lvItem.iItem = row; lvItem.iSubItem = 0;
                lvItem.pszText = (LPWSTR)L"UDP";
                ListView_InsertItem(hListView, &lvItem);

                la = udpTable->table[i].dwLocalAddr;
                lp = (WORD)((udpTable->table[i].dwLocalPort >> 8) | (udpTable->table[i].dwLocalPort << 8));
                wsprintfW(buf, L"%u.%u.%u.%u:%u", la&0xFF, (la>>8)&0xFF, (la>>16)&0xFF, (la>>24)&0xFF, lp);
                lvSub.iItem = row; lvSub.iSubItem = 1; lvSub.pszText = buf;
                ListView_SetItem(hListView, &lvSub);

                lvSub.iSubItem = 2; lvSub.pszText = (LPWSTR)L"*:*";
                ListView_SetItem(hListView, &lvSub);
                lvSub.iSubItem = 3; lvSub.pszText = (LPWSTR)L"-";
                ListView_SetItem(hListView, &lvSub);
                row++;
            }
        }
        if (udpTable) HeapFree(GetProcessHeap(), 0, udpTable);
    }
}

static LRESULT CALLBACK NetDetailWndProc(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    switch (msg) {
    case WM_CREATE:
    {
        CREATESTRUCTA *pcs = (CREATESTRUCTA*)lParam;
        DWORD pid = (DWORD)(DWORD_PTR)pcs->lpCreateParams;
        HWND hLV;
        LVCOLUMNW col;

        SetWindowLongPtrW(hWnd, GWLP_USERDATA, (LONG_PTR)pid);

        hLV = CreateWindowExW(WS_EX_CLIENTEDGE, WC_LISTVIEWW, L"",
            WS_CHILD | WS_VISIBLE | LVS_REPORT | LVS_SINGLESEL,
            0, 0, 0, 0, hWnd, (HMENU)9100, g_hInst, NULL);
        ListView_SetExtendedListViewStyle(hLV, LVS_EX_FULLROWSELECT | LVS_EX_GRIDLINES);

        memset(&col, 0, sizeof(col));
        col.mask = LVCF_WIDTH | LVCF_TEXT | LVCF_SUBITEM;
        col.iSubItem = 0; col.cx = 60;  col.pszText = (LPWSTR)L"Proto";
        ListView_InsertColumn(hLV, 0, &col);
        col.iSubItem = 1; col.cx = 200; col.pszText = (LPWSTR)L"Local Address";
        ListView_InsertColumn(hLV, 1, &col);
        col.iSubItem = 2; col.cx = 200; col.pszText = (LPWSTR)L"Remote Address";
        ListView_InsertColumn(hLV, 2, &col);
        col.iSubItem = 3; col.cx = 120; col.pszText = (LPWSTR)L"State";
        ListView_InsertColumn(hLV, 3, &col);

        PopulateNetDetail(hLV, pid);
    }
    break;

    case WM_SIZE:
    {
        HWND hLV = GetDlgItem(hWnd, 9100);
        if (hLV) {
            RECT rc; GetClientRect(hWnd, &rc);
            MoveWindow(hLV, 0, 0, rc.right, rc.bottom, TRUE);
        }
    }
    break;

    default:
        return DefWindowProcA(hWnd, msg, wParam, lParam);
    }
    return 0;
}

static BOOL RegisterWindowClasses(HINSTANCE hInstance) {
    WNDCLASSEXA wc;

    memset(&wc, 0, sizeof(wc));
    wc.cbSize = sizeof(wc);
    wc.style = CS_HREDRAW | CS_VREDRAW;
    wc.lpfnWndProc = MainWndProc;
    wc.hInstance = hInstance;
    wc.hIcon = LoadIconA(hInstance, MAKEINTRESOURCEA(101));
    wc.hCursor = LoadCursorA(NULL, (LPCSTR)IDC_ARROW);
    wc.hbrBackground = (HBRUSH)(COLOR_WINDOW + 1);
    wc.lpszClassName = g_szMainClass;
    wc.hIconSm = LoadIconA(hInstance, MAKEINTRESOURCEA(101));
    if (!RegisterClassExA(&wc)) return FALSE;

    wc.lpfnWndProc = AutorunsWndProc;
    wc.lpszClassName = g_szAutorunClass;
    if (!RegisterClassExA(&wc)) return FALSE;

    wc.lpfnWndProc = TaskSchedulerWndProc;
    wc.lpszClassName = g_szTaskSchedulerClass;
    if (!RegisterClassExA(&wc)) return FALSE;

    wc.lpfnWndProc = ProcessTreeWndProc;
    wc.lpszClassName = g_szProcessTreeClass;
    if (!RegisterClassExA(&wc)) return FALSE;

    wc.lpfnWndProc = GraphWndProc;
    wc.lpszClassName = g_szGraphClass;
    if (!RegisterClassExA(&wc)) return FALSE;

    wc.lpfnWndProc = NetDetailWndProc;
    wc.lpszClassName = g_szNetDetailClass;
    if (!RegisterClassExA(&wc)) return FALSE;

    /* Create Task Dialog (uses Unicode) */
    {
        WNDCLASSEXW wcw;
        memset(&wcw, 0, sizeof(wcw));
        wcw.cbSize = sizeof(wcw);
        wcw.style = CS_HREDRAW | CS_VREDRAW;
        wcw.lpfnWndProc = CreateTaskDlgProc;
        wcw.hInstance = hInstance;
        wcw.hCursor = LoadCursorW(NULL, IDC_ARROW);
        wcw.hbrBackground = (HBRUSH)(COLOR_WINDOW + 1);
        wcw.lpszClassName = L"CreateTaskDlgClass";
        if (!RegisterClassExW(&wcw)) return FALSE;
    }

    return TRUE;
}

/* ========================================================================= */
/* ENTRY POINT (no CRT)                                                      */
/* ========================================================================= */

void __cdecl Entry(void) {
    HWND hWnd;
    MSG msg;
    int nCmdShow;
    STARTUPINFOW si;

    g_hInst = GetModuleHandleW(NULL);

    memset(&si, 0, sizeof(si));
    si.cb = sizeof(si);
    GetStartupInfoW(&si);
    nCmdShow = (si.dwFlags & STARTF_USESHOWWINDOW) ? si.wShowWindow : SW_SHOWDEFAULT;

    InitializeCriticalSection(&g_dataLock);

    if (!RegisterWindowClasses(g_hInst)) {
        MessageBoxA(NULL, "Failed to register window classes!", "Error", MB_ICONERROR);
        ExitProcess(1);
    }

    hWnd = CreateWindowExA(0, g_szMainClass, "TaskMan Enhanced v3.1",
        WS_OVERLAPPEDWINDOW, CW_USEDEFAULT, CW_USEDEFAULT,
        WIN_WIDTH, WIN_HEIGHT, NULL, NULL, g_hInst, NULL);

    if (!hWnd) {
        MessageBoxA(NULL, "Failed to create main window!", "Error", MB_ICONERROR);
        ExitProcess(1);
    }

    ShowWindow(hWnd, nCmdShow);
    UpdateWindow(hWnd);

    while (GetMessageA(&msg, NULL, 0, 0) > 0) {
        TranslateMessage(&msg);
        DispatchMessageA(&msg);
    }

    DeleteCriticalSection(&g_dataLock);
    ExitProcess((UINT)msg.wParam);
}
