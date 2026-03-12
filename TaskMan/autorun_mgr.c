/* autorun_mgr.c - Autorun management (enable/disable/remove/export) + force removal */
#include "taskman.h"

/* ========================================================================= */
/* INTERNAL HELPERS                                                          */
/* ========================================================================= */

static BOOL EnablePrivilege(const wchar_t *privName) {
    HANDLE hToken = NULL;
    TOKEN_PRIVILEGES tp;
    BOOL result;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, &hToken))
        return FALSE;
    memset(&tp, 0, sizeof(tp));
    tp.PrivilegeCount = 1;
    tp.Privileges[0].Attributes = SE_PRIVILEGE_ENABLED;
    if (!LookupPrivilegeValueW(NULL, privName, &tp.Privileges[0].Luid)) {
        CloseHandle(hToken);
        return FALSE;
    }
    result = AdjustTokenPrivileges(hToken, FALSE, &tp, sizeof(tp), NULL, NULL);
    CloseHandle(hToken);
    return (result && GetLastError() != ERROR_NOT_ALL_ASSIGNED);
}

static BOOL SetRegistryValue(HKEY hRoot, const wchar_t *keyPath,
                             const wchar_t *valueName, const wchar_t *value) {
    HKEY hKey = NULL;
    LONG lr;
    lr = RegOpenKeyExW(hRoot, keyPath, 0, KEY_SET_VALUE, &hKey);
    if (lr != ERROR_SUCCESS) return FALSE;
    lr = RegSetValueExW(hKey, valueName, 0, REG_SZ,
        (const BYTE*)value, (DWORD)((lstrlenW(value) + 1) * sizeof(wchar_t)));
    RegCloseKey(hKey);
    return (lr == ERROR_SUCCESS);
}

static BOOL DeleteRegistryValue(HKEY hRoot, const wchar_t *keyPath,
                                const wchar_t *valueName) {
    HKEY hKey = NULL;
    LONG lr;
    lr = RegOpenKeyExW(hRoot, keyPath, 0, KEY_SET_VALUE, &hKey);
    if (lr != ERROR_SUCCESS) return FALSE;
    lr = RegDeleteValueW(hKey, valueName);
    RegCloseKey(hKey);
    return (lr == ERROR_SUCCESS);
}

static BOOL DeleteRegistryValueEx(HKEY hRoot, const wchar_t *keyPath,
                                  const wchar_t *valueName) {
    HKEY hKey = NULL;
    LONG lr;
    if (DeleteRegistryValue(hRoot, keyPath, valueName))
        return TRUE;
    EnablePrivilege(SE_BACKUP_NAME);
    EnablePrivilege(SE_RESTORE_NAME);
    EnablePrivilege(SE_TAKE_OWNERSHIP_NAME);
    lr = RegOpenKeyExW(hRoot, keyPath, 0, KEY_SET_VALUE | KEY_QUERY_VALUE | DELETE | WRITE_DAC, &hKey);
    if (lr != ERROR_SUCCESS) return FALSE;
    lr = RegDeleteValueW(hKey, valueName);
    RegCloseKey(hKey);
    return (lr == ERROR_SUCCESS);
}

static BOOL DisableRegistryValue(HKEY hRoot, const wchar_t *keyPath,
                                 const wchar_t *valueName) {
    HKEY hKey = NULL;
    LONG lr;
    wchar_t buffer[2048];
    DWORD bufferSize = sizeof(buffer);
    DWORD valueType;
    wchar_t disabledName[TM_MAX_NAME];

    lr = RegOpenKeyExW(hRoot, keyPath, 0, KEY_QUERY_VALUE | KEY_SET_VALUE, &hKey);
    if (lr != ERROR_SUCCESS) return FALSE;
    lr = RegQueryValueExW(hKey, valueName, NULL, &valueType, (BYTE*)buffer, &bufferSize);
    if (lr == ERROR_SUCCESS) {
        RegDeleteValueW(hKey, valueName);
        wsprintfW(disabledName, L"DISABLED_%s", valueName);
        lr = RegSetValueExW(hKey, disabledName, 0, valueType, (const BYTE*)buffer, bufferSize);
    }
    RegCloseKey(hKey);
    return (lr == ERROR_SUCCESS);
}

static BOOL MoveFileToDisabled(const wchar_t *filePath) {
    wchar_t disPath[TM_MAX_PATH_BUF];
    wsprintfW(disPath, L"%s.disabled", filePath);
    return MoveFileW(filePath, disPath);
}

static BOOL RestoreFileFromDisabled(const wchar_t *filePath) {
    wchar_t disPath[TM_MAX_PATH_BUF];
    wsprintfW(disPath, L"%s.disabled", filePath);
    return MoveFileW(disPath, filePath);
}

static BOOL TakeFileOwnership(const wchar_t *filePath) {
    HANDLE hToken = NULL;
    DWORD tokenInfoLen = 0;
    BYTE *tokenInfo;
    PTOKEN_USER pTokenUser;
    EXPLICIT_ACCESSW ea;
    PACL pACL = NULL;
    DWORD result;

    EnablePrivilege(SE_TAKE_OWNERSHIP_NAME);
    EnablePrivilege(SE_SECURITY_NAME);

    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &hToken))
        return FALSE;
    GetTokenInformation(hToken, TokenUser, NULL, 0, &tokenInfoLen);
    tokenInfo = (BYTE*)HeapAlloc(GetProcessHeap(), 0, tokenInfoLen);
    if (!tokenInfo) { CloseHandle(hToken); return FALSE; }
    if (!GetTokenInformation(hToken, TokenUser, tokenInfo, tokenInfoLen, &tokenInfoLen)) {
        HeapFree(GetProcessHeap(), 0, tokenInfo);
        CloseHandle(hToken);
        return FALSE;
    }
    pTokenUser = (PTOKEN_USER)tokenInfo;
    CloseHandle(hToken);

    result = SetNamedSecurityInfoW((LPWSTR)filePath, SE_FILE_OBJECT,
        OWNER_SECURITY_INFORMATION, pTokenUser->User.Sid, NULL, NULL, NULL);
    if (result != ERROR_SUCCESS) {
        HeapFree(GetProcessHeap(), 0, tokenInfo);
        return FALSE;
    }

    memset(&ea, 0, sizeof(ea));
    ea.grfAccessPermissions = GENERIC_ALL;
    ea.grfAccessMode = GRANT_ACCESS;
    ea.grfInheritance = NO_INHERITANCE;
    ea.Trustee.TrusteeForm = TRUSTEE_IS_SID;
    ea.Trustee.ptstrName = (LPWSTR)pTokenUser->User.Sid;

    result = SetEntriesInAclW(1, &ea, NULL, &pACL);
    if (result == ERROR_SUCCESS) {
        result = SetNamedSecurityInfoW((LPWSTR)filePath, SE_FILE_OBJECT,
            DACL_SECURITY_INFORMATION, NULL, NULL, pACL, NULL);
        LocalFree(pACL);
    }
    HeapFree(GetProcessHeap(), 0, tokenInfo);
    return (result == ERROR_SUCCESS);
}

static BOOL MoveFileForDeletion(const wchar_t *filePath) {
    wchar_t tempPath[MAX_PATH], tempFile[MAX_PATH];
    if (GetTempPathW(MAX_PATH, tempPath) == 0) return FALSE;
    if (GetTempFileNameW(tempPath, L"DEL", 0, tempFile) == 0) return FALSE;
    DeleteFileW(tempFile);
    if (!MoveFileW(filePath, tempFile)) return FALSE;
    return MoveFileExW(tempFile, NULL, MOVEFILE_DELAY_UNTIL_REBOOT);
}

static BOOL DeleteFileExtended(const wchar_t *filePath) {
    DWORD error, attributes;
    if (DeleteFileW(filePath)) return TRUE;
    error = GetLastError();
    if (error == ERROR_ACCESS_DENIED) {
        attributes = GetFileAttributesW(filePath);
        if (attributes != INVALID_FILE_ATTRIBUTES) {
            attributes &= ~(FILE_ATTRIBUTE_READONLY | FILE_ATTRIBUTE_HIDDEN | FILE_ATTRIBUTE_SYSTEM);
            SetFileAttributesW(filePath, attributes);
            if (DeleteFileW(filePath)) return TRUE;
        }
        if (TakeFileOwnership(filePath)) {
            if (DeleteFileW(filePath)) return TRUE;
        }
    }
    if (error == ERROR_ACCESS_DENIED || error == ERROR_SHARING_VIOLATION)
        return MoveFileForDeletion(filePath);
    return FALSE;
}

static BOOL EnableDisableService(const wchar_t *serviceName, BOOL enable) {
    SC_HANDLE hSCM, hService;
    BOOL result;
    hSCM = OpenSCManagerW(NULL, NULL, SC_MANAGER_CONNECT);
    if (!hSCM) return FALSE;
    hService = OpenServiceW(hSCM, serviceName, SERVICE_CHANGE_CONFIG);
    if (!hService) { CloseServiceHandle(hSCM); return FALSE; }
    result = ChangeServiceConfigW(hService, SERVICE_NO_CHANGE,
        enable ? SERVICE_AUTO_START : SERVICE_DISABLED,
        SERVICE_NO_CHANGE, NULL, NULL, NULL, NULL, NULL, NULL, NULL);
    CloseServiceHandle(hService);
    CloseServiceHandle(hSCM);
    return result;
}

static BOOL RemoveServiceAutorun(const wchar_t *serviceName) {
    SC_HANDLE hSCM, hService;
    SERVICE_STATUS status;
    BOOL result;
    hSCM = OpenSCManagerW(NULL, NULL, SC_MANAGER_ALL_ACCESS);
    if (!hSCM) return FALSE;
    hService = OpenServiceW(hSCM, serviceName, SERVICE_STOP | SERVICE_QUERY_STATUS | DELETE);
    if (!hService) { CloseServiceHandle(hSCM); return FALSE; }
    if (QueryServiceStatus(hService, &status)) {
        if (status.dwCurrentState != SERVICE_STOPPED) {
            ControlService(hService, SERVICE_CONTROL_STOP, &status);
            Sleep(1000);
        }
    }
    result = DeleteService(hService);
    CloseServiceHandle(hService);
    CloseServiceHandle(hSCM);
    return result;
}

static BOOL TerminateServiceProcess(const wchar_t *serviceName) {
    SC_HANDLE hSCM, hService;
    SERVICE_STATUS_PROCESS ssp;
    DWORD dwBytesNeeded;
    hSCM = OpenSCManagerW(NULL, NULL, SC_MANAGER_CONNECT);
    if (!hSCM) return FALSE;
    hService = OpenServiceW(hSCM, serviceName, SERVICE_QUERY_STATUS);
    if (!hService) { CloseServiceHandle(hSCM); return FALSE; }
    if (QueryServiceStatusEx(hService, SC_STATUS_PROCESS_INFO,
                             (LPBYTE)&ssp, sizeof(ssp), &dwBytesNeeded)) {
        if (ssp.dwProcessId != 0) {
            HANDLE hProcess = OpenProcess(PROCESS_TERMINATE, FALSE, ssp.dwProcessId);
            if (hProcess) { TerminateProcess(hProcess, 0); CloseHandle(hProcess); }
        }
    }
    CloseServiceHandle(hService);
    CloseServiceHandle(hSCM);
    return TRUE;
}

static BOOL ForceRemoveService(const wchar_t *serviceName) {
    SC_HANDLE hSCM, hService;
    SERVICE_STATUS status;
    BOOL result;
    hSCM = OpenSCManagerW(NULL, NULL, SC_MANAGER_ALL_ACCESS);
    if (!hSCM) return FALSE;
    hService = OpenServiceW(hSCM, serviceName, SERVICE_ALL_ACCESS);
    if (!hService) { CloseServiceHandle(hSCM); return FALSE; }
    if (QueryServiceStatus(hService, &status)) {
        if (status.dwCurrentState != SERVICE_STOPPED) {
            ControlService(hService, SERVICE_CONTROL_STOP, &status);
            Sleep(2000);
            if (QueryServiceStatus(hService, &status) && status.dwCurrentState != SERVICE_STOPPED) {
                TerminateServiceProcess(serviceName);
                Sleep(1000);
            }
        }
    }
    result = DeleteService(hService);
    CloseServiceHandle(hService);
    CloseServiceHandle(hSCM);
    return result;
}

static BOOL RemoveScheduledTask(const wchar_t *taskName) {
    wchar_t command[TM_MAX_PATH_BUF];
    STARTUPINFOW si;
    PROCESS_INFORMATION pi;
    DWORD exitCode;
    int attempt;
    wchar_t paths[2][TM_MAX_PATH_BUF];
    int pathCount = 0;

    if (taskName[0] == L'\\') {
        lstrcpynW(paths[pathCount++], taskName, TM_MAX_PATH_BUF);
    } else {
        lstrcpynW(paths[pathCount++], taskName, TM_MAX_PATH_BUF);
        wsprintfW(paths[pathCount++], L"\\%s", taskName);
    }

    for (attempt = 0; attempt < pathCount; attempt++) {
        wsprintfW(command, L"schtasks /delete /tn \"%s\" /f", paths[attempt]);
        memset(&si, 0, sizeof(si));
        si.cb = sizeof(si);
        si.dwFlags = STARTF_USESHOWWINDOW;
        si.wShowWindow = SW_HIDE;
        memset(&pi, 0, sizeof(pi));
        if (CreateProcessW(NULL, command, NULL, NULL, FALSE, CREATE_NO_WINDOW, NULL, NULL, &si, &pi)) {
            WaitForSingleObject(pi.hProcess, 5000);
            GetExitCodeProcess(pi.hProcess, &exitCode);
            CloseHandle(pi.hProcess);
            CloseHandle(pi.hThread);
            if (exitCode == 0) return TRUE;
        }
    }
    return FALSE;
}

static void FormatFileTime(const FILETIME *ft, wchar_t *buf, int bufSize) {
    SYSTEMTIME st;
    if (ft->dwLowDateTime == 0 && ft->dwHighDateTime == 0) {
        lstrcpynW(buf, L"Unknown", bufSize);
        return;
    }
    if (!FileTimeToSystemTime(ft, &st)) {
        lstrcpynW(buf, L"Invalid", bufSize);
        return;
    }
    wsprintfW(buf, L"%d-%02d-%02d %02d:%02d:%02d",
              st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond);
}

static BOOL CanModifyAutorun(const AutorunInfo *ar) {
    if (ar->source == ARSRC_SystemProcess) return FALSE;
    return TRUE;
}

/* ========================================================================= */
/* PUBLIC API                                                                */
/* ========================================================================= */

void AM_ExportToCSV(HWND hWnd) {
    OPENFILENAMEW ofn;
    wchar_t szFile[260];
    HANDLE hFile;
    DWORD written;
    int i;
    char header[] = "Name,Description,Company,Version,Path,Arguments,Source,Enabled,Verified,FileSize,LastModified\r\n";

    lstrcpynW(szFile, L"autoruns_export.csv", 260);
    memset(&ofn, 0, sizeof(ofn));
    ofn.lStructSize = sizeof(ofn);
    ofn.hwndOwner = hWnd;
    ofn.lpstrFile = szFile;
    ofn.nMaxFile = 260;
    ofn.lpstrFilter = L"CSV Files\0*.csv\0All Files\0*.*\0";
    ofn.nFilterIndex = 1;
    ofn.Flags = OFN_PATHMUSTEXIST | OFN_OVERWRITEPROMPT;
    ofn.lpstrDefExt = L"csv";

    if (!GetSaveFileNameW(&ofn)) return;

    hFile = CreateFileW(szFile, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (hFile == INVALID_HANDLE_VALUE) {
        MessageBoxW(hWnd, L"Failed to create export file!", L"Error", MB_ICONERROR);
        return;
    }

    WriteFile(hFile, header, lstrlenA(header), &written, NULL);

    for (i = 0; i < g_autorunCount; i++) {
        const AutorunInfo *ar = &g_autoruns[i];
        wchar_t line[4096];
        char utf8[8192];
        int len;
        SYSTEMTIME st;
        wchar_t dateStr[64];

        if (ar->lastModified.dwLowDateTime || ar->lastModified.dwHighDateTime) {
            if (FileTimeToSystemTime(&ar->lastModified, &st))
                wsprintfW(dateStr, L"%d-%02d-%02d %02d:%02d:%02d",
                    st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond);
            else
                lstrcpynW(dateStr, L"Unknown", 64);
        } else {
            lstrcpynW(dateStr, L"Unknown", 64);
        }

        wsprintfW(line, L"\"%s\",\"%s\",\"%s\",\"%s\",\"%s\",\"%s\",\"%s\",%s,%s,%u,%s\r\n",
            ar->name, ar->description, ar->company, ar->version,
            ar->fullPath, ar->arguments, ar->sourceDetails,
            ar->enabled ? L"Yes" : L"No",
            ar->verified ? L"Yes" : L"No",
            ar->fileSize, dateStr);

        len = WideCharToMultiByte(CP_UTF8, 0, line, -1, utf8, sizeof(utf8), NULL, NULL);
        if (len > 1) WriteFile(hFile, utf8, (DWORD)(len - 1), &written, NULL);
    }
    CloseHandle(hFile);
    MessageBoxW(hWnd, L"Export completed successfully!", L"Export", MB_ICONINFORMATION);
}

void AM_ShowProperties(HWND hWnd, const AutorunInfo *ar) {
    wchar_t buf[4096];
    wchar_t tmp[512];
    wchar_t ftStr[64];

    buf[0] = 0;
    lstrcatW(buf, L"Autorun Properties\n\n");
    wsprintfW(tmp, L"Name: %s\n", ar->name); lstrcatW(buf, tmp);
    wsprintfW(tmp, L"Description: %s\n", ar->description); lstrcatW(buf, tmp);
    wsprintfW(tmp, L"Company: %s\n", ar->company); lstrcatW(buf, tmp);
    wsprintfW(tmp, L"Version: %s\n", ar->version); lstrcatW(buf, tmp);
    wsprintfW(tmp, L"Path: %s\n", ar->fullPath); lstrcatW(buf, tmp);
    if (ar->arguments[0]) {
        wsprintfW(tmp, L"Arguments: %s\n", ar->arguments); lstrcatW(buf, tmp);
    }
    wsprintfW(tmp, L"Source: %s\n", ar->sourceDetails); lstrcatW(buf, tmp);
    wsprintfW(tmp, L"Enabled: %s\n", ar->enabled ? L"Yes" : L"No"); lstrcatW(buf, tmp);
    wsprintfW(tmp, L"Digitally Signed: %s\n", ar->verified ? L"Yes" : L"No"); lstrcatW(buf, tmp);
    wsprintfW(tmp, L"File Size: %u bytes\n", ar->fileSize); lstrcatW(buf, tmp);
    if (ar->processId > 0) {
        wsprintfW(tmp, L"Process ID: %u\n", ar->processId); lstrcatW(buf, tmp);
    }
    FormatFileTime(&ar->lastModified, ftStr, 64);
    wsprintfW(tmp, L"Last Modified: %s\n", ftStr); lstrcatW(buf, tmp);
    if (ar->regKeyPath[0]) {
        lstrcatW(buf, L"\nRegistry:\n");
        wsprintfW(tmp, L"Key: %s\n", ar->regKeyPath); lstrcatW(buf, tmp);
        wsprintfW(tmp, L"Value: %s\n", ar->regValueName); lstrcatW(buf, tmp);
    }
    MessageBoxW(hWnd, buf, L"Properties", MB_ICONINFORMATION);
}

BOOL AM_EnableDisableAutorun(const AutorunInfo *ar, BOOL enable) {
    HKEY hRoot;
    if (!CanModifyAutorun(ar)) return FALSE;

    switch (ar->source) {
        case ARSRC_RegistryRunHKCU: case ARSRC_RegistryRunHKLM:
        case ARSRC_RegistryRunOnceHKCU: case ARSRC_RegistryRunOnceHKLM:
        case ARSRC_RegistryRunServicesHKCU: case ARSRC_RegistryRunServicesHKLM:
        case ARSRC_RegistryRunServicesOnceHKCU: case ARSRC_RegistryRunServicesOnceHKLM:
        case ARSRC_RegistryPoliciesRunHKCU: case ARSRC_RegistryPoliciesRunHKLM:
        {
            wchar_t fullCmd[TM_MAX_PATH_BUF];
            hRoot = tm_is_hkcu_source(ar->source) ? HKEY_CURRENT_USER : HKEY_LOCAL_MACHINE;
            if (enable) {
                wsprintfW(fullCmd, L"%s %s", ar->fullPath, ar->arguments);
                return SetRegistryValue(hRoot, ar->regKeyPath, ar->regValueName, fullCmd);
            } else {
                return DisableRegistryValue(hRoot, ar->regKeyPath, ar->regValueName);
            }
        }
        case ARSRC_StartupFolderUser: case ARSRC_StartupFolderCommon:
            return enable ? RestoreFileFromDisabled(ar->fullPath) : MoveFileToDisabled(ar->fullPath);
        case ARSRC_WindowsService:
            return EnableDisableService(ar->name, enable);
        default:
            return FALSE;
    }
}

BOOL AM_RemoveAutorun(const AutorunInfo *ar) {
    HKEY hRoot;
    if (!CanModifyAutorun(ar)) return FALSE;

    switch (ar->source) {
        case ARSRC_RegistryRunHKCU: case ARSRC_RegistryRunHKLM:
        case ARSRC_RegistryRunOnceHKCU: case ARSRC_RegistryRunOnceHKLM:
        case ARSRC_RegistryRunServicesHKCU: case ARSRC_RegistryRunServicesHKLM:
        case ARSRC_RegistryRunServicesOnceHKCU: case ARSRC_RegistryRunServicesOnceHKLM:
        case ARSRC_RegistryPoliciesRunHKCU: case ARSRC_RegistryPoliciesRunHKLM:
            hRoot = tm_is_hkcu_source(ar->source) ? HKEY_CURRENT_USER : HKEY_LOCAL_MACHINE;
            return DeleteRegistryValueEx(hRoot, ar->regKeyPath, ar->regValueName);
        case ARSRC_StartupFolderUser: case ARSRC_StartupFolderCommon:
            return DeleteFileExtended(ar->fullPath);
        case ARSRC_WindowsService:
            return RemoveServiceAutorun(ar->name);
        case ARSRC_ScheduledTask:
            return RemoveScheduledTask(ar->name);
        default:
            return FALSE;
    }
}

BOOL AM_ForceRemoveAutorun(const AutorunInfo *ar) {
    HKEY hRoot;
    EnablePrivilege(SE_BACKUP_NAME);
    EnablePrivilege(SE_RESTORE_NAME);
    EnablePrivilege(SE_TAKE_OWNERSHIP_NAME);
    EnablePrivilege(SE_SECURITY_NAME);
    EnablePrivilege(SE_DEBUG_NAME);

    switch (ar->source) {
        case ARSRC_RegistryRunHKCU: case ARSRC_RegistryRunHKLM:
        case ARSRC_RegistryRunOnceHKCU: case ARSRC_RegistryRunOnceHKLM:
        case ARSRC_RegistryRunServicesHKCU: case ARSRC_RegistryRunServicesHKLM:
        case ARSRC_RegistryRunServicesOnceHKCU: case ARSRC_RegistryRunServicesOnceHKLM:
        case ARSRC_RegistryPoliciesRunHKCU: case ARSRC_RegistryPoliciesRunHKLM:
            hRoot = tm_is_hkcu_source(ar->source) ? HKEY_CURRENT_USER : HKEY_LOCAL_MACHINE;
            if (DeleteRegistryValueEx(hRoot, ar->regKeyPath, ar->regValueName)) return TRUE;
            return DisableRegistryValue(hRoot, ar->regKeyPath, ar->regValueName);
        case ARSRC_StartupFolderUser: case ARSRC_StartupFolderCommon:
            return DeleteFileExtended(ar->fullPath);
        case ARSRC_WindowsService:
            return ForceRemoveService(ar->name);
        case ARSRC_ScheduledTask:
            return RemoveScheduledTask(ar->name);
        default:
            return FALSE;
    }
}

void AM_OpenFileLocation(HWND hWnd, const AutorunInfo *ar) {
    wchar_t param[TM_MAX_PATH_BUF];
    if (!ar->fullPath[0]) {
        MessageBoxW(hWnd, L"File path not determined!", L"Error", MB_ICONWARNING);
        return;
    }
    wsprintfW(param, L"/select,\"%s\"", ar->fullPath);
    if ((INT_PTR)ShellExecuteW(NULL, L"open", L"explorer.exe", param, NULL, SW_SHOWNORMAL) <= 32)
        MessageBoxW(hWnd, L"Failed to open folder!", L"Error", MB_ICONERROR);
}
