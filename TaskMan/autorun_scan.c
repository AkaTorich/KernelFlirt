/* autorun_scan.c - Autorun scanning from all sources */
#include "taskman.h"

/* Local dynamic array for building results */
typedef struct { AutorunInfo *items; int count; int cap; } ARArr;

static void ARArr_Add(ARArr *a, const AutorunInfo *item) {
    DYNARRAY_GROW(a->items, a->count, a->cap, AutorunInfo);
    a->items[a->count++] = *item;
}

const wchar_t *AS_GetSourceDescription(int source) {
    switch (source) {
        case ARSRC_RegistryRunHKCU: return L"HKCU Run";
        case ARSRC_RegistryRunHKLM: return L"HKLM Run";
        case ARSRC_RegistryRunOnceHKCU: return L"HKCU RunOnce";
        case ARSRC_RegistryRunOnceHKLM: return L"HKLM RunOnce";
        case ARSRC_RegistryRunServicesHKCU: return L"HKCU RunServices";
        case ARSRC_RegistryRunServicesHKLM: return L"HKLM RunServices";
        case ARSRC_RegistryRunServicesOnceHKCU: return L"HKCU RunServicesOnce";
        case ARSRC_RegistryRunServicesOnceHKLM: return L"HKLM RunServicesOnce";
        case ARSRC_RegistryPoliciesRunHKCU: return L"HKCU Policies Run";
        case ARSRC_RegistryPoliciesRunHKLM: return L"HKLM Policies Run";
        case ARSRC_RegistryWinlogonUserinit: return L"Winlogon Userinit";
        case ARSRC_RegistryWinlogonShell: return L"Winlogon Shell";
        case ARSRC_RegistryWinlogonVMApplet: return L"Winlogon VMApplet";
        case ARSRC_RegistryWinlogonTaskman: return L"Winlogon Taskman";
        case ARSRC_RegistryWinlogonSystem: return L"Winlogon System";
        case ARSRC_RegistryActiveSetup: return L"Active Setup";
        case ARSRC_RegistrySessionManagerBootExecute: return L"Session Manager BootExecute";
        case ARSRC_RegistrySessionManagerSetupExecute: return L"Session Manager SetupExecute";
        case ARSRC_RegistryAppInitDLLs: return L"AppInit DLLs";
        case ARSRC_RegistryImageFileExecutionOptions: return L"Image File Execution Options";
        case ARSRC_RegistryShellServiceObjectDelayLoad: return L"Shell Service Objects";
        case ARSRC_RegistryShellExtensions: return L"Shell Extensions";
        case ARSRC_RegistryContextMenuHandlers: return L"Context Menu Handlers";
        case ARSRC_RegistryBrowserHelperObjects: return L"Browser Helper Objects";
        case ARSRC_RegistryIEToolbar: return L"IE Toolbar";
        case ARSRC_RegistryIEExtensions: return L"IE Extensions";
        case ARSRC_RegistryFontDrivers: return L"Font Drivers";
        case ARSRC_RegistryKnownDLLs: return L"Known DLLs";
        case ARSRC_RegistryPrintMonitors: return L"Print Monitors";
        case ARSRC_RegistryNetworkProviders: return L"Network Providers";
        case ARSRC_RegistryLSAProviders: return L"LSA Providers";
        case ARSRC_RegistryWinsockProviders: return L"Winsock Providers";
        case ARSRC_RegistryCodecs: return L"Codecs";
        case ARSRC_RegistryDirectShowFilters: return L"DirectShow Filters";
        case ARSRC_StartupFolderUser: return L"User Startup Folder";
        case ARSRC_StartupFolderCommon: return L"Common Startup Folder";
        case ARSRC_WindowsService: return L"Windows Service";
        case ARSRC_ScheduledTask: return L"Scheduled Task";
        case ARSRC_SystemProcess: return L"System Process";
        case ARSRC_WMIEventConsumer: return L"WMI Event Consumer";
        default: return L"Unknown";
    }
}

static void ParseCommandLine(AutorunInfo *ai) {
    wchar_t cmdLine[TM_MAX_PATH_BUF];
    wchar_t *endQuote, *sp;
    lstrcpynW(cmdLine, ai->fullPath, TM_MAX_PATH_BUF);
    if (!cmdLine[0]) return;

    if (cmdLine[0] == L'"') {
        endQuote = tm_wcschr(cmdLine + 1, L'"');
        if (endQuote) {
            *endQuote = 0;
            lstrcpynW(ai->fullPath, cmdLine + 1, TM_MAX_PATH_BUF);
            if (*(endQuote + 1) == L' ')
                lstrcpynW(ai->arguments, endQuote + 2, TM_MAX_ARGS);
        }
    } else {
        sp = tm_wcschr(cmdLine, L' ');
        if (sp) {
            *sp = 0;
            lstrcpynW(ai->fullPath, cmdLine, TM_MAX_PATH_BUF);
            lstrcpynW(ai->arguments, sp + 1, TM_MAX_ARGS);
        }
    }
}

static void FillAutorunInfo(AutorunInfo *ai) {
    if (ai->fullPath[0]) {
        FI_GetFileVersionDetails(ai->fullPath, ai->description, ai->company, ai->version);
        ai->verified = FI_IsFileSigned(ai->fullPath);
        ai->fileSize = FI_GetFileSizeByPath(ai->fullPath);
        ai->lastModified = FI_GetFileModifiedTime(ai->fullPath);
    }
}

static BOOL ReadRegistryRunItems(HKEY hRoot, const wchar_t *subKey,
                                 int source, ARArr *out) {
    HKEY hKey = NULL;
    REGSAM access[] = { KEY_READ, KEY_READ | KEY_WOW64_64KEY, KEY_READ | KEY_WOW64_32KEY };
    LONG lr = ERROR_ACCESS_DENIED;
    DWORD index = 0;
    int i, itemsFound = 0;

    for (i = 0; i < 3; i++) {
        lr = RegOpenKeyExW(hRoot, subKey, 0, access[i], &hKey);
        if (lr == ERROR_SUCCESS) break;
    }

    if (lr != ERROR_SUCCESS) {
        if (source == ARSRC_RegistryRunHKLM || source == ARSRC_RegistryRunHKCU ||
            source == ARSRC_RegistryRunOnceHKLM || source == ARSRC_RegistryRunOnceHKCU) {
            AutorunInfo errorAi;
            const wchar_t *rootName = (hRoot == HKEY_LOCAL_MACHINE) ? L"HKLM" : L"HKCU";
            const wchar_t *errorType;
            memset(&errorAi, 0, sizeof(errorAi));
            switch (lr) {
                case ERROR_FILE_NOT_FOUND: errorType = L"Key does not exist"; break;
                case ERROR_ACCESS_DENIED: errorType = L"Access denied - need admin rights"; break;
                default: errorType = L"Unknown error"; break;
            }
            wsprintfW(errorAi.name, L"[DIAGNOSTIC] %s", errorType);
            wsprintfW(errorAi.description, L"%s\\%s", rootName, subKey);
            errorAi.source = source;
            errorAi.enabled = FALSE;
            errorAi.verified = FALSE;
            wsprintfW(errorAi.sourceDetails, L"%s (Error: %s)", AS_GetSourceDescription(source), errorType);
            ARArr_Add(out, &errorAi);
        }
        return FALSE;
    }

    while (TRUE) {
        wchar_t valName[256];
        DWORD valNameSize = 255;
        DWORD valType = 0;
        BYTE valData[2048];
        DWORD valDataSize = 2047;
        LONG rc;
        AutorunInfo ai;

        rc = RegEnumValueW(hKey, index, valName, &valNameSize,
            NULL, &valType, valData, &valDataSize);
        if (rc == ERROR_NO_MORE_ITEMS) break;

        if (rc == ERROR_SUCCESS && (valType == REG_SZ || valType == REG_EXPAND_SZ)) {
            memset(&ai, 0, sizeof(ai));
            lstrcpynW(ai.name, valName, TM_MAX_NAME);
            lstrcpynW(ai.fullPath, (wchar_t*)valData, TM_MAX_PATH_BUF);
            ai.source = source;
            lstrcpynW(ai.regKeyPath, subKey, TM_MAX_REGKEY);
            lstrcpynW(ai.regValueName, valName, TM_MAX_NAME);
            ai.enabled = TRUE;
            lstrcpynW(ai.sourceDetails, AS_GetSourceDescription(source), TM_MAX_SOURCE);
            ParseCommandLine(&ai);
            FillAutorunInfo(&ai);
            ARArr_Add(out, &ai);
            itemsFound++;
        }
        index++;
    }

    if (itemsFound == 0 && (source == ARSRC_RegistryRunHKLM || source == ARSRC_RegistryRunHKCU ||
                            source == ARSRC_RegistryRunOnceHKLM || source == ARSRC_RegistryRunOnceHKCU)) {
        AutorunInfo emptyAi;
        const wchar_t *rootName = (hRoot == HKEY_LOCAL_MACHINE) ? L"HKLM" : L"HKCU";
        memset(&emptyAi, 0, sizeof(emptyAi));
        lstrcpynW(emptyAi.name, L"[INFO] No autorun entries found", TM_MAX_NAME);
        wsprintfW(emptyAi.description, L"Registry key is accessible but empty: %s\\%s", rootName, subKey);
        emptyAi.source = source;
        emptyAi.enabled = TRUE;
        emptyAi.verified = TRUE;
        wsprintfW(emptyAi.sourceDetails, L"%s (Empty but accessible)", AS_GetSourceDescription(source));
        ARArr_Add(out, &emptyAi);
    }

    RegCloseKey(hKey);
    return itemsFound > 0;
}

static void ReadStartupFolder(const wchar_t *folderPath, int source, ARArr *out) {
    wchar_t searchPath[TM_MAX_PATH_BUF];
    WIN32_FIND_DATAW fdata;
    HANDLE hFind;

    wsprintfW(searchPath, L"%s\\*.*", folderPath);
    hFind = FindFirstFileW(searchPath, &fdata);
    if (hFind == INVALID_HANDLE_VALUE) return;

    do {
        if (!(fdata.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)) {
            if (lstrcmpW(fdata.cFileName, L".") != 0 && lstrcmpW(fdata.cFileName, L"..") != 0) {
                AutorunInfo ai;
                memset(&ai, 0, sizeof(ai));
                lstrcpynW(ai.name, fdata.cFileName, TM_MAX_NAME);
                wsprintfW(ai.fullPath, L"%s\\%s", folderPath, fdata.cFileName);
                ai.source = source;
                ai.enabled = TRUE;
                lstrcpynW(ai.sourceDetails, AS_GetSourceDescription(source), TM_MAX_SOURCE);
                ai.fileSize = fdata.nFileSizeLow;
                ai.lastModified = fdata.ftLastWriteTime;
                FillAutorunInfo(&ai);
                ARArr_Add(out, &ai);
            }
        }
    } while (FindNextFileW(hFind, &fdata));
    FindClose(hFind);
}

static void ReadStartupFolders(ARArr *out) {
    wchar_t path[MAX_PATH];
    if (SHGetSpecialFolderPathW(NULL, path, CSIDL_STARTUP, FALSE))
        ReadStartupFolder(path, ARSRC_StartupFolderUser, out);
    if (SHGetSpecialFolderPathW(NULL, path, CSIDL_COMMON_STARTUP, FALSE))
        ReadStartupFolder(path, ARSRC_StartupFolderCommon, out);
}

static void ReadWindowsServices(ARArr *out) {
    SC_HANDLE hSCM;
    DWORD dwBytesNeeded = 0, dwServicesReturned = 0, dwResumeHandle = 0;
    BYTE *buffer;

    hSCM = OpenSCManagerW(NULL, NULL, SC_MANAGER_ENUMERATE_SERVICE);
    if (!hSCM) return;

    EnumServicesStatusExW(hSCM, SC_ENUM_PROCESS_INFO, SERVICE_WIN32, SERVICE_STATE_ALL,
        NULL, 0, &dwBytesNeeded, &dwServicesReturned, &dwResumeHandle, NULL);

    if (dwBytesNeeded > 0) {
        LPENUM_SERVICE_STATUS_PROCESSW pServices;
        DWORD i;
        buffer = (BYTE*)HeapAlloc(GetProcessHeap(), 0, dwBytesNeeded);
        if (!buffer) { CloseServiceHandle(hSCM); return; }
        pServices = (LPENUM_SERVICE_STATUS_PROCESSW)buffer;

        if (EnumServicesStatusExW(hSCM, SC_ENUM_PROCESS_INFO, SERVICE_WIN32, SERVICE_STATE_ALL,
            buffer, dwBytesNeeded, &dwBytesNeeded, &dwServicesReturned, &dwResumeHandle, NULL)) {

            for (i = 0; i < dwServicesReturned; i++) {
                SC_HANDLE hService = OpenServiceW(hSCM, pServices[i].lpServiceName, SERVICE_QUERY_CONFIG);
                if (hService) {
                    DWORD cfgBytes = 0;
                    QueryServiceConfigW(hService, NULL, 0, &cfgBytes);
                    if (cfgBytes > 0) {
                        BYTE *cfgBuf = (BYTE*)HeapAlloc(GetProcessHeap(), 0, cfgBytes);
                        if (cfgBuf) {
                            LPQUERY_SERVICE_CONFIGW pCfg = (LPQUERY_SERVICE_CONFIGW)cfgBuf;
                            if (QueryServiceConfigW(hService, pCfg, cfgBytes, &cfgBytes)) {
                                if (pCfg->dwStartType == SERVICE_AUTO_START || pCfg->dwStartType == SERVICE_BOOT_START) {
                                    AutorunInfo ai;
                                    memset(&ai, 0, sizeof(ai));
                                    lstrcpynW(ai.name, pServices[i].lpServiceName, TM_MAX_NAME);
                                    if (pServices[i].lpDisplayName)
                                        lstrcpynW(ai.description, pServices[i].lpDisplayName, TM_MAX_DESC);
                                    if (pCfg->lpBinaryPathName)
                                        lstrcpynW(ai.fullPath, pCfg->lpBinaryPathName, TM_MAX_PATH_BUF);
                                    ai.source = ARSRC_WindowsService;
                                    ai.enabled = (pServices[i].ServiceStatusProcess.dwCurrentState != SERVICE_STOPPED);
                                    ai.processId = pServices[i].ServiceStatusProcess.dwProcessId;
                                    lstrcpynW(ai.sourceDetails, AS_GetSourceDescription(ai.source), TM_MAX_SOURCE);
                                    ParseCommandLine(&ai);
                                    FillAutorunInfo(&ai);
                                    ARArr_Add(out, &ai);
                                }
                            }
                            HeapFree(GetProcessHeap(), 0, cfgBuf);
                        }
                    }
                    CloseServiceHandle(hService);
                }
            }
        }
        HeapFree(GetProcessHeap(), 0, buffer);
    }
    CloseServiceHandle(hSCM);
}

static void ReadScheduledTasks(ARArr *out) {
    wchar_t windowsPath[MAX_PATH];
    wchar_t tasksPath[TM_MAX_PATH_BUF];
    wchar_t searchPath[TM_MAX_PATH_BUF];
    WIN32_FIND_DATAW fdata;
    HANDLE hFind;

    if (!GetWindowsDirectoryW(windowsPath, MAX_PATH)) return;
    wsprintfW(tasksPath, L"%s\\System32\\Tasks", windowsPath);
    wsprintfW(searchPath, L"%s\\*.*", tasksPath);

    hFind = FindFirstFileW(searchPath, &fdata);
    if (hFind == INVALID_HANDLE_VALUE) return;

    do {
        if (!(fdata.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)) {
            if (lstrcmpW(fdata.cFileName, L".") != 0 && lstrcmpW(fdata.cFileName, L"..") != 0) {
                AutorunInfo ai;
                memset(&ai, 0, sizeof(ai));
                lstrcpynW(ai.name, fdata.cFileName, TM_MAX_NAME);
                wsprintfW(ai.fullPath, L"%s\\%s", tasksPath, fdata.cFileName);
                ai.source = ARSRC_ScheduledTask;
                ai.enabled = TRUE;
                lstrcpynW(ai.sourceDetails, AS_GetSourceDescription(ai.source), TM_MAX_SOURCE);
                ai.fileSize = fdata.nFileSizeLow;
                ai.lastModified = fdata.ftLastWriteTime;
                ARArr_Add(out, &ai);
            }
        }
    } while (FindNextFileW(hFind, &fdata));
    FindClose(hFind);
}

void AS_ScanAll(void) {
    ARArr arr;
    memset(&arr, 0, sizeof(arr));

    /* 1. Main Run keys */
    ReadRegistryRunItems(HKEY_CURRENT_USER, L"Software\\Microsoft\\Windows\\CurrentVersion\\Run",
        ARSRC_RegistryRunHKCU, &arr);
    ReadRegistryRunItems(HKEY_LOCAL_MACHINE, L"Software\\Microsoft\\Windows\\CurrentVersion\\Run",
        ARSRC_RegistryRunHKLM, &arr);

    /* 2. RunOnce */
    ReadRegistryRunItems(HKEY_CURRENT_USER, L"Software\\Microsoft\\Windows\\CurrentVersion\\RunOnce",
        ARSRC_RegistryRunOnceHKCU, &arr);
    ReadRegistryRunItems(HKEY_LOCAL_MACHINE, L"Software\\Microsoft\\Windows\\CurrentVersion\\RunOnce",
        ARSRC_RegistryRunOnceHKLM, &arr);

    /* 3. RunServices */
    ReadRegistryRunItems(HKEY_CURRENT_USER, L"Software\\Microsoft\\Windows\\CurrentVersion\\RunServices",
        ARSRC_RegistryRunServicesHKCU, &arr);
    ReadRegistryRunItems(HKEY_LOCAL_MACHINE, L"Software\\Microsoft\\Windows\\CurrentVersion\\RunServices",
        ARSRC_RegistryRunServicesHKLM, &arr);

    /* 4. RunServicesOnce */
    ReadRegistryRunItems(HKEY_CURRENT_USER, L"Software\\Microsoft\\Windows\\CurrentVersion\\RunServicesOnce",
        ARSRC_RegistryRunServicesOnceHKCU, &arr);
    ReadRegistryRunItems(HKEY_LOCAL_MACHINE, L"Software\\Microsoft\\Windows\\CurrentVersion\\RunServicesOnce",
        ARSRC_RegistryRunServicesOnceHKLM, &arr);

    /* 5. Policies Run */
    ReadRegistryRunItems(HKEY_CURRENT_USER, L"Software\\Microsoft\\Windows\\CurrentVersion\\Policies\\Explorer\\Run",
        ARSRC_RegistryPoliciesRunHKCU, &arr);
    ReadRegistryRunItems(HKEY_LOCAL_MACHINE, L"Software\\Microsoft\\Windows\\CurrentVersion\\Policies\\Explorer\\Run",
        ARSRC_RegistryPoliciesRunHKLM, &arr);

    /* 6. Winlogon */
    ReadRegistryRunItems(HKEY_LOCAL_MACHINE, L"Software\\Microsoft\\Windows NT\\CurrentVersion\\Winlogon",
        ARSRC_RegistryWinlogonUserinit, &arr);

    /* 7. Active Setup */
    ReadRegistryRunItems(HKEY_LOCAL_MACHINE, L"Software\\Microsoft\\Active Setup\\Installed Components",
        ARSRC_RegistryActiveSetup, &arr);

    /* 8. System keys */
    ReadRegistryRunItems(HKEY_LOCAL_MACHINE, L"Software\\Microsoft\\Windows NT\\CurrentVersion\\Windows",
        ARSRC_RegistryAppInitDLLs, &arr);
    ReadRegistryRunItems(HKEY_LOCAL_MACHINE, L"System\\CurrentControlSet\\Control\\Session Manager",
        ARSRC_RegistrySessionManagerBootExecute, &arr);

    /* 9. Shell extensions */
    ReadRegistryRunItems(HKEY_LOCAL_MACHINE, L"Software\\Microsoft\\Windows\\CurrentVersion\\ShellServiceObjectDelayLoad",
        ARSRC_RegistryShellServiceObjectDelayLoad, &arr);

    /* 10. Browser Helper Objects */
    ReadRegistryRunItems(HKEY_LOCAL_MACHINE,
        L"Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Browser Helper Objects",
        ARSRC_RegistryBrowserHelperObjects, &arr);
    ReadRegistryRunItems(HKEY_CURRENT_USER,
        L"Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Browser Helper Objects",
        ARSRC_RegistryBrowserHelperObjects, &arr);

    /* 11. Startup folders */
    ReadStartupFolders(&arr);

    /* 12. Windows Services */
    ReadWindowsServices(&arr);

    /* 13. Scheduled Tasks */
    ReadScheduledTasks(&arr);

    /* Store results in globals */
    DYNARRAY_FREE(g_autoruns, g_autorunCount, g_autorunCap);
    g_autoruns = arr.items;
    g_autorunCount = arr.count;
    g_autorunCap = arr.cap;
}
