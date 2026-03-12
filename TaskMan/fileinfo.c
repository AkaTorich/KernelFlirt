/* fileinfo.c - File version info and digital signature verification */
#include "taskman.h"

BOOL FI_GetFileVersionDetails(const wchar_t *filePath,
                              wchar_t *desc, wchar_t *company, wchar_t *version)
{
    DWORD dwHandle = 0;
    DWORD dwSize;
    BYTE *buffer;
    LPVOID lpBuf = NULL;
    UINT uLen = 0;

    if (!PathFileExistsW(filePath))
        return FALSE;

    dwSize = GetFileVersionInfoSizeW(filePath, &dwHandle);
    if (dwSize == 0)
        return FALSE;

    buffer = (BYTE*)HeapAlloc(GetProcessHeap(), 0, dwSize);
    if (!buffer) return FALSE;

    if (!GetFileVersionInfoW(filePath, 0, dwSize, buffer)) {
        HeapFree(GetProcessHeap(), 0, buffer);
        return FALSE;
    }

    if (desc) {
        desc[0] = 0;
        if (VerQueryValueW(buffer, L"\\StringFileInfo\\040904b0\\FileDescription", &lpBuf, &uLen) && uLen > 0)
            lstrcpynW(desc, (LPCWSTR)lpBuf, TM_MAX_DESC);
    }
    if (company) {
        company[0] = 0;
        if (VerQueryValueW(buffer, L"\\StringFileInfo\\040904b0\\CompanyName", &lpBuf, &uLen) && uLen > 0)
            lstrcpynW(company, (LPCWSTR)lpBuf, TM_MAX_NAME);
    }
    if (version) {
        version[0] = 0;
        if (VerQueryValueW(buffer, L"\\StringFileInfo\\040904b0\\FileVersion", &lpBuf, &uLen) && uLen > 0)
            lstrcpynW(version, (LPCWSTR)lpBuf, TM_MAX_VERSION);
    }

    HeapFree(GetProcessHeap(), 0, buffer);
    return TRUE;
}

BOOL FI_IsFileSigned(const wchar_t *filePath) {
    WINTRUST_FILE_INFO FileData;
    GUID WVTPolicyGUID = WINTRUST_ACTION_GENERIC_VERIFY_V2;
    WINTRUST_DATA WinTrustData;
    LONG lStatus;

    if (!PathFileExistsW(filePath))
        return FALSE;

    memset(&FileData, 0, sizeof(FileData));
    FileData.cbStruct = sizeof(WINTRUST_FILE_INFO);
    FileData.pcwszFilePath = filePath;
    FileData.hFile = NULL;
    FileData.pgKnownSubject = NULL;

    memset(&WinTrustData, 0, sizeof(WinTrustData));
    WinTrustData.cbStruct = sizeof(WinTrustData);
    WinTrustData.pPolicyCallbackData = NULL;
    WinTrustData.pSIPClientData = NULL;
    WinTrustData.dwUIChoice = WTD_UI_NONE;
    WinTrustData.fdwRevocationChecks = WTD_REVOKE_NONE;
    WinTrustData.dwUnionChoice = WTD_CHOICE_FILE;
    WinTrustData.dwStateAction = WTD_STATEACTION_VERIFY;
    WinTrustData.hWVTStateData = NULL;
    WinTrustData.pwszURLReference = NULL;
    WinTrustData.dwUIContext = 0;
    WinTrustData.pFile = &FileData;

    lStatus = WinVerifyTrust(NULL, &WVTPolicyGUID, &WinTrustData);

    WinTrustData.dwStateAction = WTD_STATEACTION_CLOSE;
    WinVerifyTrust(NULL, &WVTPolicyGUID, &WinTrustData);

    return (lStatus == ERROR_SUCCESS);
}

DWORD FI_GetFileSizeByPath(const wchar_t *filePath) {
    WIN32_FIND_DATAW findData;
    HANDLE hFind = FindFirstFileW(filePath, &findData);
    if (hFind == INVALID_HANDLE_VALUE) return 0;
    FindClose(hFind);
    return findData.nFileSizeLow;
}

FILETIME FI_GetFileModifiedTime(const wchar_t *filePath) {
    FILETIME ft;
    WIN32_FIND_DATAW findData;
    HANDLE hFind;
    memset(&ft, 0, sizeof(ft));
    hFind = FindFirstFileW(filePath, &findData);
    if (hFind != INVALID_HANDLE_VALUE) {
        ft = findData.ftLastWriteTime;
        FindClose(hFind);
    }
    return ft;
}

HICON FI_GetFileIcon(const wchar_t *filePath) {
    SHFILEINFOW sfi;
    DWORD_PTR res;
    if (!filePath || !filePath[0]) return NULL;
    memset(&sfi, 0, sizeof(sfi));
    res = SHGetFileInfoW(filePath, FILE_ATTRIBUTE_NORMAL, &sfi,
        sizeof(sfi), SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES);
    return (res && sfi.hIcon) ? sfi.hIcon : NULL;
}
