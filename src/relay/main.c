/*
 * KernelFlirt - TCP Relay Agent
 * main.c - Runs on the VM, proxies IOCTL calls from the network to the local driver.
 *
 * Architecture:
 *   The relay accepts TWO TCP connections from the UI:
 *     1. CMD channel  (first connection)  - normal IOCTLs (read memory, enum threads, etc.)
 *     2. DBG channel  (second connection) - debug event IOCTLs (WAIT_DEBUG_EVENT, CONTINUE)
 *
 *   Each channel dispatches every incoming request to a thread pool worker,
 *   so a blocking IOCTL (e.g. WAIT_DEBUG_EVENT) never blocks other requests
 *   on the same channel.  Responses are serialized via a per-channel
 *   CRITICAL_SECTION so they arrive in the same order as requests.
 *
 * Wire protocol (little-endian, same on both channels):
 *   Request:  [uint32 ioctl_code][uint32 input_size][byte[input_size] input_data]
 *   Response: [uint32 success(1/0)][uint32 win32_error][uint32 output_size][byte[output_size] output_data]
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <winioctl.h>
#include "../../include/kf_shared.h"

#pragma comment(lib, "ws2_32.lib")
#pragma comment(lib, "shlwapi.lib")

#define KF_RELAY_PORT       31337
#define KF_MAX_BUFFER       (4 * 1024 * 1024)  /* 4MB max IOCTL buffer */
#define KF_DEVICE_PATH      "\\\\.\\KernelFlirt"

/* Pseudo-IOCTL codes handled by relay (must match kf_shared.h) */
#define KF_PSEUDO_LIST_DRIVES     CTL_CODE(0x00008000, 0x900, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define KF_PSEUDO_LIST_DIRECTORY  CTL_CODE(0x00008000, 0x901, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define KF_PSEUDO_CREATE_PROCESS  CTL_CODE(0x00008000, 0x902, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define KF_PSEUDO_LOAD_DRIVER     CTL_CODE(0x00008000, 0x903, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define KF_PSEUDO_UNLOAD_DRIVER   CTL_CODE(0x00008000, 0x904, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define KF_PSEUDO_START_DRIVER    CTL_CODE(0x00008000, 0x905, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define KF_PSEUDO_READ_FILE       CTL_CODE(0x00008000, 0x906, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define KF_PSEUDO_WRITE_FILE      CTL_CODE(0x00008000, 0x907, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define KF_PSEUDO_DELETE_PATH     CTL_CODE(0x00008000, 0x908, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define KF_PSEUDO_CREATE_DIR      CTL_CODE(0x00008000, 0x909, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define KF_PSEUDO_RENAME_PATH     CTL_CODE(0x00008000, 0x90A, METHOD_BUFFERED, FILE_ANY_ACCESS)

static HANDLE g_hDeviceCmd = INVALID_HANDLE_VALUE;  /* CMD channel handle */
static HANDLE g_hDeviceDbg = INVALID_HANDLE_VALUE;  /* DBG channel handle */

/* Track last created process so we can report exit code */
static HANDLE g_hChildProcess = NULL;
static DWORD  g_dwChildPid    = 0;
static DWORD WINAPI ChildExitWatcher(LPVOID param);

static HANDLE OpenOneHandle(const char *label)
{
    HANDLE h = CreateFileA(
        KF_DEVICE_PATH,
        GENERIC_READ | GENERIC_WRITE,
        FILE_SHARE_READ | FILE_SHARE_WRITE,
        NULL, OPEN_EXISTING, 0, NULL);

    if (h == INVALID_HANDLE_VALUE) {
        printf("[!] Failed to open %s for %s: %lu\n", KF_DEVICE_PATH, label, GetLastError());
    } else {
        printf("[+] Driver handle opened (%s)\n", label);
    }
    return h;
}

static BOOL OpenDriver(void)
{
    g_hDeviceCmd = OpenOneHandle("cmd");
    if (g_hDeviceCmd == INVALID_HANDLE_VALUE) return FALSE;

    g_hDeviceDbg = OpenOneHandle("dbg");
    if (g_hDeviceDbg == INVALID_HANDLE_VALUE) {
        CloseHandle(g_hDeviceCmd);
        g_hDeviceCmd = INVALID_HANDLE_VALUE;
        return FALSE;
    }
    return TRUE;
}

static void CloseDriver(void)
{
    if (g_hDeviceCmd != INVALID_HANDLE_VALUE) {
        CloseHandle(g_hDeviceCmd);
        g_hDeviceCmd = INVALID_HANDLE_VALUE;
    }
    if (g_hDeviceDbg != INVALID_HANDLE_VALUE) {
        CloseHandle(g_hDeviceDbg);
        g_hDeviceDbg = INVALID_HANDLE_VALUE;
    }
}

/* Send IOCTL_KF_RESET to driver — removes all BPs, hooks, unblocks threads */
static void ResetDriver(void)
{
    if (g_hDeviceCmd != INVALID_HANDLE_VALUE) {
        DWORD bytesReturned = 0;
        BOOL ok = DeviceIoControl(g_hDeviceCmd, IOCTL_KF_RESET,
                                   NULL, 0, NULL, 0, &bytesReturned, NULL);
        printf("[*] Driver reset: %s\n", ok ? "OK" : "FAILED");
    }
}

/* Read exactly n bytes from socket. Returns FALSE on error/disconnect. */
static BOOL RecvAll(SOCKET s, void *buf, int len)
{
    char *p = (char *)buf;
    int remaining = len;
    while (remaining > 0) {
        int n = recv(s, p, remaining, 0);
        if (n <= 0) return FALSE;
        p += n;
        remaining -= n;
    }
    return TRUE;
}

/* Send exactly n bytes. Returns FALSE on error. */
static BOOL SendAll(SOCKET s, const void *buf, int len)
{
    const char *p = (const char *)buf;
    int remaining = len;
    while (remaining > 0) {
        int n = send(s, p, remaining, 0);
        if (n <= 0) return FALSE;
        p += n;
        remaining -= n;
    }
    return TRUE;
}

/* ── Relay-handled pseudo-IOCTLs ── */

static BOOL HandleListDrives(BYTE **ppOut, DWORD *pOutSize)
{
    DWORD mask = GetLogicalDrives();
    if (!mask) return FALSE;

    int count = 0;
    for (int i = 0; i < 26; i++)
        if (mask & (1 << i)) count++;

    DWORD totalSize = (DWORD)(count * sizeof(KF_DRIVE_ENTRY));
    KF_DRIVE_ENTRY *entries = (KF_DRIVE_ENTRY *)calloc(count, sizeof(KF_DRIVE_ENTRY));
    if (!entries) return FALSE;

    int idx = 0;
    for (int i = 0; i < 26; i++) {
        if (!(mask & (1 << i))) continue;

        entries[idx].Letter = 'A' + (char)i;
        entries[idx].Padding[0] = 0;
        entries[idx].Padding[1] = 0;
        entries[idx].Padding[2] = 0;

        WCHAR root[4] = { L'A' + i, L':', L'\\', 0 };
        entries[idx].DriveType = GetDriveTypeW(root);

        WCHAR label[64] = {0};
        GetVolumeInformationW(root, label, 64, NULL, NULL, NULL, NULL, 0);
        wcsncpy(entries[idx].Label, label, 63);

        idx++;
    }

    *ppOut = (BYTE *)entries;
    *pOutSize = totalSize;
    return TRUE;
}

static BOOL HandleListDirectory(BYTE *inputBuf, DWORD inputSize, BYTE **ppOut, DWORD *pOutSize)
{
    if (!inputBuf || inputSize < 4) return FALSE;

    WCHAR *path = (WCHAR *)inputBuf;
    int maxChars = inputSize / sizeof(WCHAR);
    path[maxChars - 1] = L'\0';

    WCHAR searchPath[MAX_PATH + 4];
    wcsncpy(searchPath, path, MAX_PATH);
    searchPath[MAX_PATH - 1] = L'\0';
    size_t len = wcslen(searchPath);
    if (len > 0 && searchPath[len - 1] != L'\\')
        wcscat(searchPath, L"\\");
    wcscat(searchPath, L"*");

    WIN32_FIND_DATAW fd;
    HANDLE hFind = FindFirstFileW(searchPath, &fd);
    if (hFind == INVALID_HANDLE_VALUE) return FALSE;

    int count = 0;
    KF_DIR_ENTRY *entries = NULL;
    DWORD capacity = 256;
    entries = (KF_DIR_ENTRY *)calloc(capacity, sizeof(KF_DIR_ENTRY));
    if (!entries) { FindClose(hFind); return FALSE; }

    do {
        if (wcscmp(fd.cFileName, L".") == 0) continue;
        if (wcscmp(fd.cFileName, L"..") == 0) continue;

        if ((DWORD)count >= capacity) {
            capacity *= 2;
            KF_DIR_ENTRY *tmp = (KF_DIR_ENTRY *)realloc(entries, capacity * sizeof(KF_DIR_ENTRY));
            if (!tmp) { free(entries); FindClose(hFind); return FALSE; }
            entries = tmp;
        }

        memset(&entries[count], 0, sizeof(KF_DIR_ENTRY));
        entries[count].IsDirectory = (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) ? 1 : 0;
        entries[count].Attributes = fd.dwFileAttributes;
        entries[count].FileSize = ((ULONGLONG)fd.nFileSizeHigh << 32) | fd.nFileSizeLow;
        entries[count].LastWriteTime = ((ULONGLONG)fd.ftLastWriteTime.dwHighDateTime << 32)
                                     | fd.ftLastWriteTime.dwLowDateTime;
        wcsncpy(entries[count].Name, fd.cFileName, 259);
        count++;
    } while (FindNextFileW(hFind, &fd));

    FindClose(hFind);

    if (count == 0) {
        free(entries);
        *ppOut = NULL;
        *pOutSize = 0;
        return TRUE;
    }

    *ppOut = (BYTE *)entries;
    *pOutSize = (DWORD)(count * sizeof(KF_DIR_ENTRY));
    return TRUE;
}

/* Background thread: waits for child process to exit and prints exit code */
static DWORD WINAPI ChildExitWatcher(LPVOID param)
{
    (void)param;
    HANDLE h = g_hChildProcess;
    DWORD pid = g_dwChildPid;
    if (!h) return 0;

    WaitForSingleObject(h, INFINITE);

    DWORD exitCode = 0;
    GetExitCodeProcess(h, &exitCode);
    printf("[dbg] Process %lu exited with code: %lu (0x%lX)\n", pid, exitCode, exitCode);
    return 0;
}

static BOOL HandleCreateProcess(BYTE *inputBuf, DWORD inputSize, BYTE **ppOut, DWORD *pOutSize)
{
    if (!inputBuf || inputSize < 4) return FALSE;

    WCHAR *exePath = (WCHAR *)inputBuf;
    int maxChars = inputSize / sizeof(WCHAR);
    exePath[maxChars - 1] = L'\0';

    STARTUPINFOW si = {0};
    PROCESS_INFORMATION pi = {0};
    si.cb = sizeof(si);

    /* Disable Application Compatibility shims (aclayers.dll) for the child.
       aclayers hooks HeapAlloc/HeapSize/HeapReAlloc which breaks IAT resolution. */
    SetEnvironmentVariableW(L"__COMPAT_LAYER", L"");

    BOOL ok = CreateProcessW(
        exePath, NULL, NULL, NULL, FALSE,
        CREATE_SUSPENDED,
        NULL, NULL, &si, &pi);

    /* Restore: remove __COMPAT_LAYER from our own environment */
    SetEnvironmentVariableW(L"__COMPAT_LAYER", NULL);

    if (!ok) {
        printf("[relay] CreateProcess failed: %lu\n", GetLastError());
        return FALSE;
    }

    printf("[relay] Created PID=%lu TID=%lu (suspended)\n",
           pi.dwProcessId, pi.dwThreadId);

    /* Query ImageBase from PEB */
    ULONG64 imageBase = 0;
    {
        typedef LONG (NTAPI *NtQIP_t)(HANDLE, ULONG, PVOID, ULONG, PULONG);
        typedef struct {
            LONG Stat; LONG P0; PVOID Peb; ULONG_PTR Aff;
            LONG Prio; LONG P1; ULONG_PTR Pid; ULONG_PTR Inh;
        } PBI;
        NtQIP_t pNtQIP = (NtQIP_t)GetProcAddress(GetModuleHandleA("ntdll.dll"),
                                                    "NtQueryInformationProcess");
        if (pNtQIP) {
            PBI pbi = {0}; ULONG rl = 0;
            if (pNtQIP(pi.hProcess, 0, &pbi, sizeof(pbi), &rl) == 0) {
                SIZE_T br = 0;
                ReadProcessMemory(pi.hProcess, (BYTE *)pbi.Peb + 0x10,
                                  &imageBase, sizeof(imageBase), &br);
                printf("[relay] ImageBase = 0x%llX\n", imageBase);
            }
        }
    }

    /* Close previous child handle if any */
    if (g_hChildProcess) {
        CloseHandle(g_hChildProcess);
        g_hChildProcess = NULL;
    }
    /* Keep process handle to monitor exit */
    g_hChildProcess = pi.hProcess;
    g_dwChildPid    = pi.dwProcessId;
    CloseHandle(pi.hThread);

    /* Background thread to watch for process exit */
    CreateThread(NULL, 0, ChildExitWatcher, NULL, 0, NULL);

    KF_CREATE_PROCESS_OUT *out = (KF_CREATE_PROCESS_OUT *)calloc(1, sizeof(KF_CREATE_PROCESS_OUT));
    if (!out) return FALSE;
    out->ProcessId = pi.dwProcessId;
    out->ThreadId  = pi.dwThreadId;
    out->ImageBase = imageBase;

    *ppOut = (BYTE *)out;
    *pOutSize = sizeof(KF_CREATE_PROCESS_OUT);
    return TRUE;
}

/* ── Driver load/unload via SCM ── */

/* Background thread: calls StartServiceA (may block if driver hits INT3) */
typedef struct _START_DRIVER_CTX {
    char serviceName[64];
} START_DRIVER_CTX;

static DWORD WINAPI StartDriverThread(LPVOID param)
{
    START_DRIVER_CTX *ctx = (START_DRIVER_CTX *)param;
    SC_HANDLE scm = OpenSCManagerA(NULL, NULL, SC_MANAGER_CONNECT);
    if (scm) {
        SC_HANDLE svc = OpenServiceA(scm, ctx->serviceName, SERVICE_START);
        if (svc) {
            if (!StartServiceA(svc, 0, NULL)) {
                DWORD err = GetLastError();
                if (err != ERROR_SERVICE_ALREADY_RUNNING)
                    printf("[relay] StartService('%s') failed: %lu\n", ctx->serviceName, err);
            } else {
                printf("[relay] Driver '%s' started (DriverEntry returned)\n", ctx->serviceName);
            }
            CloseServiceHandle(svc);
        } else {
            printf("[relay] OpenService('%s') failed: %lu\n", ctx->serviceName, GetLastError());
        }
        CloseServiceHandle(scm);
    }
    free(ctx);
    return 0;
}

/* Read PE AddressOfEntryPoint RVA from a file */
static ULONG ReadPeEntryRva(const char *filePath)
{
    HANDLE hFile = CreateFileA(filePath, GENERIC_READ, FILE_SHARE_READ,
                                NULL, OPEN_EXISTING, 0, NULL);
    if (hFile == INVALID_HANDLE_VALUE) return 0;

    DWORD br = 0;
    USHORT dosMagic = 0;
    ReadFile(hFile, &dosMagic, 2, &br, NULL);
    if (dosMagic != 0x5A4D) { CloseHandle(hFile); return 0; }

    SetFilePointer(hFile, 0x3C, NULL, FILE_BEGIN);
    ULONG peOffset = 0;
    ReadFile(hFile, &peOffset, 4, &br, NULL);

    SetFilePointer(hFile, peOffset, NULL, FILE_BEGIN);
    ULONG peSig = 0;
    ReadFile(hFile, &peSig, 4, &br, NULL);
    if (peSig != 0x00004550) { CloseHandle(hFile); return 0; }

    /* AddressOfEntryPoint at OptionalHeader+16 = peOffset+24+16 */
    SetFilePointer(hFile, peOffset + 24 + 16, NULL, FILE_BEGIN);
    ULONG entryRva = 0;
    ReadFile(hFile, &entryRva, 4, &br, NULL);

    CloseHandle(hFile);
    return entryRva;
}

/* Map RVA to file offset using section headers */
static ULONG RvaToFileOffset(const char *filePath, ULONG rva)
{
    HANDLE hFile = CreateFileA(filePath, GENERIC_READ, FILE_SHARE_READ,
                                NULL, OPEN_EXISTING, 0, NULL);
    if (hFile == INVALID_HANDLE_VALUE) return 0;

    DWORD br = 0;
    SetFilePointer(hFile, 0x3C, NULL, FILE_BEGIN);
    ULONG peOffset = 0;
    ReadFile(hFile, &peOffset, 4, &br, NULL);

    SetFilePointer(hFile, peOffset + 6, NULL, FILE_BEGIN);
    USHORT numSections = 0;
    ReadFile(hFile, &numSections, 2, &br, NULL);

    SetFilePointer(hFile, peOffset + 20, NULL, FILE_BEGIN);
    USHORT optHdrSize = 0;
    ReadFile(hFile, &optHdrSize, 2, &br, NULL);

    ULONG sectionStart = peOffset + 24 + optHdrSize;
    ULONG result = 0;

    for (USHORT i = 0; i < numSections; i++) {
        ULONG secOff = sectionStart + i * 40;
        ULONG secVirtSize, secVA, secRawSize, secRawPtr;

        SetFilePointer(hFile, secOff + 8, NULL, FILE_BEGIN);
        ReadFile(hFile, &secVirtSize, 4, &br, NULL);
        ReadFile(hFile, &secVA, 4, &br, NULL);
        ReadFile(hFile, &secRawSize, 4, &br, NULL);
        ReadFile(hFile, &secRawPtr, 4, &br, NULL);

        if (rva >= secVA && rva < secVA + secVirtSize) {
            result = secRawPtr + (rva - secVA);
            break;
        }
    }

    CloseHandle(hFile);
    return result;
}

/* Patch a single byte in a file at given offset */
static BOOL PatchFileByteAt(const char *filePath, ULONG fileOffset,
                             UCHAR newByte, UCHAR *pOrigByte)
{
    HANDLE hFile = CreateFileA(filePath, GENERIC_READ | GENERIC_WRITE,
                                0, NULL, OPEN_EXISTING, 0, NULL);
    if (hFile == INVALID_HANDLE_VALUE) return FALSE;

    DWORD br = 0;
    SetFilePointer(hFile, fileOffset, NULL, FILE_BEGIN);
    UCHAR orig = 0;
    ReadFile(hFile, &orig, 1, &br, NULL);

    SetFilePointer(hFile, fileOffset, NULL, FILE_BEGIN);
    WriteFile(hFile, &newByte, 1, &br, NULL);
    FlushFileBuffers(hFile);
    CloseHandle(hFile);

    if (pOrigByte) *pOrigByte = orig;
    return TRUE;
}

/* Re-sign a driver file using PowerShell + self-signed test cert.
   Requires testsigning enabled on the VM. */
static BOOL TestSignDriver(const char *filePath)
{
    /* PowerShell one-liner:
       - Get or create a test cert named "KernelFlirt Test"
       - Sign the file with it */
    char cmd[1024];
    _snprintf(cmd, sizeof(cmd) - 1,
        "powershell -NoProfile -ExecutionPolicy Bypass -Command \""
        "$cert = Get-ChildItem Cert:\\CurrentUser\\My -CodeSigningCert | "
            "Where-Object { $_.Subject -eq 'CN=KernelFlirt Test' } | "
            "Select-Object -First 1; "
        "if (-not $cert) { "
            "$cert = New-SelfSignedCertificate -Type CodeSigningCert "
                "-Subject 'CN=KernelFlirt Test' "
                "-CertStoreLocation Cert:\\CurrentUser\\My "
                "-NotAfter (Get-Date).AddYears(10) "
        "}; "
        "Set-AuthenticodeSignature -FilePath '%s' -Certificate $cert"
        "\"", filePath);
    cmd[sizeof(cmd) - 1] = '\0';

    printf("[relay] Signing: %s\n", filePath);
    int rc = system(cmd);
    printf("[relay] Sign result: %d\n", rc);
    return (rc == 0);
}

static BOOL HandleLoadDriver(BYTE *inputBuf, DWORD inputSize, BYTE **ppOut, DWORD *pOutSize)
{
    if (!inputBuf || inputSize < 4) return FALSE;

    /* Input: wide path to .sys file on VM */
    WCHAR *sysPath = (WCHAR *)inputBuf;
    int maxChars = inputSize / sizeof(WCHAR);
    sysPath[maxChars - 1] = L'\0';

    printf("[relay] LoadDriver: %ls\n", sysPath);

    /* Extract service name from filename (strip path and .sys extension) */
    char serviceName[64] = {0};
    {
        WCHAR *lastSlash = wcsrchr(sysPath, L'\\');
        WCHAR *fname = lastSlash ? lastSlash + 1 : sysPath;
        char ansiName[128] = {0};
        WideCharToMultiByte(CP_ACP, 0, fname, -1, ansiName, sizeof(ansiName) - 1, NULL, NULL);
        char *dot = strrchr(ansiName, '.');
        if (dot) *dot = '\0';
        _snprintf(serviceName, sizeof(serviceName) - 1, "%s", ansiName);
    }

    if (serviceName[0] == '\0') {
        printf("[relay] Could not extract service name\n");
        return FALSE;
    }

    printf("[relay] Service name: %s\n", serviceName);

    /* Stop and delete any existing service with the same name BEFORE copying.
       The old driver may still be loaded, locking the .sys file in System32. */
    {
        SC_HANDLE scmPre = OpenSCManagerA(NULL, NULL, SC_MANAGER_CONNECT);
        if (scmPre) {
            SC_HANDLE svcPre = OpenServiceA(scmPre, serviceName, SERVICE_ALL_ACCESS);
            if (svcPre) {
                SERVICE_STATUS ss;
                printf("[relay] Stopping existing service '%s'...\n", serviceName);
                ControlService(svcPre, SERVICE_CONTROL_STOP, &ss);
                /* Wait for the driver to actually stop (file unlock) */
                for (int i = 0; i < 20; i++) {
                    Sleep(100);
                    if (QueryServiceStatus(svcPre, &ss) && ss.dwCurrentState == SERVICE_STOPPED)
                        break;
                }
                DeleteService(svcPre);
                CloseServiceHandle(svcPre);
                printf("[relay] Old service stopped and deleted\n");
                Sleep(200); /* extra delay for file release */
            }
            CloseServiceHandle(scmPre);
        }
    }

    /* Copy .sys to System32\drivers\ */
    char winDir[MAX_PATH];
    char destPath[MAX_PATH];
    GetWindowsDirectoryA(winDir, MAX_PATH);
    _snprintf(destPath, MAX_PATH, "%s\\System32\\drivers\\%s.sys", winDir, serviceName);
    destPath[MAX_PATH - 1] = '\0';

    char ansiSrcPath[MAX_PATH] = {0};
    WideCharToMultiByte(CP_ACP, 0, sysPath, -1, ansiSrcPath, MAX_PATH - 1, NULL, NULL);

    if (!CopyFileA(ansiSrcPath, destPath, FALSE)) {
        printf("[relay] CopyFile to %s failed: %lu\n", destPath, GetLastError());
        return FALSE;
    }
    printf("[relay] Copied to %s\n", destPath);

    /* Read PE entry point RVA and patch to INT3 */
    ULONG entryRva = ReadPeEntryRva(destPath);
    UCHAR originalByte = 0;

    if (entryRva != 0) {
        ULONG entryFileOffset = RvaToFileOffset(destPath, entryRva);
        if (entryFileOffset != 0) {
            PatchFileByteAt(destPath, entryFileOffset, 0xCC, &originalByte);
            printf("[relay] Patched entry RVA=0x%lX fileOff=0x%lX: 0x%02X -> 0xCC\n",
                   entryRva, entryFileOffset, originalByte);

            /* Re-sign the patched driver with a test certificate */
            if (!TestSignDriver(destPath)) {
                printf("[relay] Warning: test signing failed, driver may not load\n");
            }
        } else {
            printf("[relay] Could not map entry RVA to file offset\n");
            entryRva = 0; /* signal failure */
        }
    } else {
        printf("[relay] PE entry point RVA is 0\n");
    }

    /* Create SCM service (stop+delete old one if exists) */
    SC_HANDLE scm = OpenSCManagerA(NULL, NULL, SC_MANAGER_CREATE_SERVICE);
    if (!scm) {
        printf("[relay] OpenSCManager failed: %lu\n", GetLastError());
        return FALSE;
    }

    SC_HANDLE svc = CreateServiceA(scm, serviceName, serviceName,
                                    SERVICE_ALL_ACCESS, SERVICE_KERNEL_DRIVER,
                                    SERVICE_DEMAND_START, SERVICE_ERROR_NORMAL,
                                    destPath, NULL, NULL, NULL, NULL, NULL);
    if (!svc) {
        DWORD err = GetLastError();
        if (err == ERROR_SERVICE_EXISTS) {
            printf("[relay] Service exists, removing old...\n");
            svc = OpenServiceA(scm, serviceName, SERVICE_ALL_ACCESS);
            if (svc) {
                SERVICE_STATUS ss;
                ControlService(svc, SERVICE_CONTROL_STOP, &ss);
                Sleep(200);
                DeleteService(svc);
                CloseServiceHandle(svc);
            }
            svc = CreateServiceA(scm, serviceName, serviceName,
                                  SERVICE_ALL_ACCESS, SERVICE_KERNEL_DRIVER,
                                  SERVICE_DEMAND_START, SERVICE_ERROR_NORMAL,
                                  destPath, NULL, NULL, NULL, NULL, NULL);
            if (!svc) {
                printf("[relay] CreateService retry failed: %lu\n", GetLastError());
                CloseServiceHandle(scm);
                return FALSE;
            }
        } else {
            printf("[relay] CreateService failed: %lu\n", err);
            CloseServiceHandle(scm);
            return FALSE;
        }
    }

    printf("[relay] Service created: %s\n", serviceName);
    CloseServiceHandle(svc);
    CloseServiceHandle(scm);

    /* NOTE: Service is NOT started here. UI must call START_DRIVER after
       installing the debug hook, so INT3 at DriverEntry is caught. */

    /* Build output */
    KF_LOAD_DRIVER_OUT *out = (KF_LOAD_DRIVER_OUT *)calloc(1, sizeof(KF_LOAD_DRIVER_OUT));
    if (!out) return FALSE;

    strncpy(out->ServiceName, serviceName, KF_MAX_SERVICE_NAME - 1);
    out->EntryPointRva = entryRva;
    out->OriginalByte = originalByte;

    *ppOut = (BYTE *)out;
    *pOutSize = sizeof(KF_LOAD_DRIVER_OUT);
    return TRUE;
}

static BOOL HandleUnloadDriver(BYTE *inputBuf, DWORD inputSize, BYTE **ppOut, DWORD *pOutSize)
{
    if (!inputBuf || inputSize < 2) return FALSE;

    /* Input: null-terminated ANSI service name */
    char *serviceName = (char *)inputBuf;
    serviceName[inputSize - 1] = '\0';

    printf("[relay] UnloadDriver: %s\n", serviceName);

    SC_HANDLE scm = OpenSCManagerA(NULL, NULL, SC_MANAGER_CONNECT);
    if (!scm) {
        printf("[relay] OpenSCManager failed: %lu\n", GetLastError());
        return FALSE;
    }

    SC_HANDLE svc = OpenServiceA(scm, serviceName, SERVICE_ALL_ACCESS);
    if (!svc) {
        printf("[relay] OpenService('%s') failed: %lu\n", serviceName, GetLastError());
        CloseServiceHandle(scm);
        return FALSE;
    }

    /* Stop */
    SERVICE_STATUS ss;
    if (!ControlService(svc, SERVICE_CONTROL_STOP, &ss)) {
        DWORD err = GetLastError();
        if (err != ERROR_SERVICE_NOT_ACTIVE)
            printf("[relay] StopService failed: %lu\n", err);
    } else {
        printf("[relay] Driver stopped\n");
    }

    /* Delete */
    if (!DeleteService(svc)) {
        printf("[relay] DeleteService failed: %lu\n", GetLastError());
    } else {
        printf("[relay] Service deleted\n");
    }

    CloseServiceHandle(svc);
    CloseServiceHandle(scm);

    /* Remove .sys from System32\drivers\ */
    char winDir[MAX_PATH];
    char destPath[MAX_PATH];
    GetWindowsDirectoryA(winDir, MAX_PATH);
    _snprintf(destPath, MAX_PATH, "%s\\System32\\drivers\\%s.sys", winDir, serviceName);
    destPath[MAX_PATH - 1] = '\0';
    DeleteFileA(destPath);

    *ppOut = NULL;
    *pOutSize = 0;
    return TRUE;
}

static BOOL HandleStartDriver(BYTE *inputBuf, DWORD inputSize, BYTE **ppOut, DWORD *pOutSize)
{
    if (!inputBuf || inputSize < 2) return FALSE;

    /* Input: ASCII service name */
    char serviceName[64] = {0};
    int len = inputSize < 63 ? inputSize : 63;
    memcpy(serviceName, inputBuf, len);
    serviceName[len] = '\0';

    printf("[relay] StartDriver: %s\n", serviceName);

    /* Start in background thread (StartService blocks until DriverEntry returns) */
    START_DRIVER_CTX *ctx = (START_DRIVER_CTX *)calloc(1, sizeof(START_DRIVER_CTX));
    if (!ctx) return FALSE;

    strncpy(ctx->serviceName, serviceName, sizeof(ctx->serviceName) - 1);
    HANDLE hThread = CreateThread(NULL, 0, StartDriverThread, ctx, 0, NULL);
    if (hThread) {
        CloseHandle(hThread);
        printf("[relay] StartService dispatched in background\n");
    } else {
        free(ctx);
        return FALSE;
    }

    *ppOut = NULL;
    *pOutSize = 0;
    return TRUE;
}

/* ── File browser operations ── */

/*
 * READ_FILE: read a chunk from a remote file.
 * Input: [wchar_path\0][uint64 offset][uint32 length]
 * Output: raw file bytes
 */
static BOOL HandleReadFile(BYTE *inputBuf, DWORD inputSize, BYTE **ppOut, DWORD *pOutSize)
{
    if (!inputBuf || inputSize < 16) return FALSE;

    WCHAR *path = (WCHAR *)inputBuf;
    int maxChars = inputSize / sizeof(WCHAR);
    /* Find null terminator to locate the trailer (offset + length) */
    int pathLen = 0;
    while (pathLen < maxChars && path[pathLen] != L'\0') pathLen++;
    if (pathLen >= maxChars) return FALSE;

    /* Trailer starts after the null terminator */
    DWORD trailerOff = (DWORD)((pathLen + 1) * sizeof(WCHAR));
    if (trailerOff + 12 > inputSize) return FALSE; /* need 8 + 4 bytes */

    ULONGLONG offset;
    ULONG chunkLen;
    memcpy(&offset,   inputBuf + trailerOff, 8);
    memcpy(&chunkLen,  inputBuf + trailerOff + 8, 4);

    /* Clamp to 4MB */
    if (chunkLen > KF_MAX_BUFFER) chunkLen = KF_MAX_BUFFER;

    HANDLE hFile = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                               NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (hFile == INVALID_HANDLE_VALUE) return FALSE;

    LARGE_INTEGER li;
    li.QuadPart = (LONGLONG)offset;
    if (!SetFilePointerEx(hFile, li, NULL, FILE_BEGIN)) {
        CloseHandle(hFile);
        return FALSE;
    }

    BYTE *buf = (BYTE *)malloc(chunkLen);
    if (!buf) { CloseHandle(hFile); return FALSE; }

    DWORD bytesRead = 0;
    if (!ReadFile(hFile, buf, chunkLen, &bytesRead, NULL)) {
        free(buf);
        CloseHandle(hFile);
        return FALSE;
    }
    CloseHandle(hFile);

    if (bytesRead == 0) {
        free(buf);
        *ppOut = NULL;
        *pOutSize = 0;
        return TRUE;
    }

    *ppOut = buf;
    *pOutSize = bytesRead;
    return TRUE;
}

/*
 * WRITE_FILE: write a chunk to a remote file.
 * Input: [wchar_path\0][uint32 flags][uint32 data_len][raw bytes]
 * flags: bit 0 = append(1) / create-or-truncate(0)
 * Output: [uint32 bytes_written]
 */
static BOOL HandleWriteFile(BYTE *inputBuf, DWORD inputSize, BYTE **ppOut, DWORD *pOutSize)
{
    if (!inputBuf || inputSize < 12) return FALSE;

    WCHAR *path = (WCHAR *)inputBuf;
    int maxChars = inputSize / sizeof(WCHAR);
    int pathLen = 0;
    while (pathLen < maxChars && path[pathLen] != L'\0') pathLen++;
    if (pathLen >= maxChars) return FALSE;

    DWORD trailerOff = (DWORD)((pathLen + 1) * sizeof(WCHAR));
    if (trailerOff + 8 > inputSize) return FALSE;

    ULONG flags, dataLen;
    memcpy(&flags,   inputBuf + trailerOff, 4);
    memcpy(&dataLen,  inputBuf + trailerOff + 4, 4);

    DWORD dataOff = trailerOff + 8;
    if (dataOff + dataLen > inputSize) return FALSE;

    BOOL append = (flags & 1) != 0;
    DWORD creationDisposition = append ? OPEN_ALWAYS : CREATE_ALWAYS;

    HANDLE hFile = CreateFileW(path, GENERIC_WRITE, 0,
                               NULL, creationDisposition, FILE_ATTRIBUTE_NORMAL, NULL);
    if (hFile == INVALID_HANDLE_VALUE) return FALSE;

    if (append) {
        LARGE_INTEGER li;
        li.QuadPart = 0;
        SetFilePointerEx(hFile, li, NULL, FILE_END);
    }

    DWORD bytesWritten = 0;
    if (!WriteFile(hFile, inputBuf + dataOff, dataLen, &bytesWritten, NULL)) {
        CloseHandle(hFile);
        return FALSE;
    }
    CloseHandle(hFile);

    ULONG *result = (ULONG *)malloc(sizeof(ULONG));
    if (!result) return FALSE;
    *result = bytesWritten;

    *ppOut = (BYTE *)result;
    *pOutSize = sizeof(ULONG);
    return TRUE;
}

/*
 * DELETE_PATH: delete a file or empty directory.
 * Input: wchar null-terminated path
 * Output: none
 */
static BOOL HandleDeletePath(BYTE *inputBuf, DWORD inputSize, BYTE **ppOut, DWORD *pOutSize)
{
    if (!inputBuf || inputSize < 4) return FALSE;

    WCHAR *path = (WCHAR *)inputBuf;
    int maxChars = inputSize / sizeof(WCHAR);
    path[maxChars - 1] = L'\0';

    DWORD attrs = GetFileAttributesW(path);
    if (attrs == INVALID_FILE_ATTRIBUTES) return FALSE;

    BOOL ok;
    if (attrs & FILE_ATTRIBUTE_DIRECTORY)
        ok = RemoveDirectoryW(path);
    else
        ok = DeleteFileW(path);

    *ppOut = NULL;
    *pOutSize = 0;
    return ok;
}

/*
 * CREATE_DIR: create a new directory.
 * Input: wchar null-terminated path
 * Output: none
 */
static BOOL HandleCreateDir(BYTE *inputBuf, DWORD inputSize, BYTE **ppOut, DWORD *pOutSize)
{
    if (!inputBuf || inputSize < 4) return FALSE;

    WCHAR *path = (WCHAR *)inputBuf;
    int maxChars = inputSize / sizeof(WCHAR);
    path[maxChars - 1] = L'\0';

    BOOL ok = CreateDirectoryW(path, NULL);

    *ppOut = NULL;
    *pOutSize = 0;
    return ok;
}

/*
 * RENAME_PATH: rename/move a file or directory.
 * Input: [wchar oldPath\0][wchar newPath\0]
 * Output: none
 */
static BOOL HandleRenamePath(BYTE *inputBuf, DWORD inputSize, BYTE **ppOut, DWORD *pOutSize)
{
    if (!inputBuf || inputSize < 8) return FALSE;

    WCHAR *oldPath = (WCHAR *)inputBuf;
    int maxChars = inputSize / sizeof(WCHAR);

    /* Find end of first string */
    int oldLen = 0;
    while (oldLen < maxChars && oldPath[oldLen] != L'\0') oldLen++;
    if (oldLen >= maxChars) return FALSE;

    WCHAR *newPath = oldPath + oldLen + 1;
    int remaining = maxChars - (oldLen + 1);
    if (remaining <= 0) return FALSE;
    newPath[remaining - 1] = L'\0';

    BOOL ok = MoveFileW(oldPath, newPath);

    *ppOut = NULL;
    *pOutSize = 0;
    return ok;
}

/*
 * Check if this IOCTL is a relay pseudo-IOCTL.
 * If so, handle it locally and return TRUE; caller should send response.
 * On return: *ppOut (malloc'd, caller frees), *pOutSize, *pSuccess.
 */
static BOOL TryHandlePseudoIoctl(DWORD ioctlCode, BYTE *inputBuf, DWORD inputSize,
                                  BYTE **ppOut, DWORD *pOutSize, BOOL *pSuccess)
{
    switch (ioctlCode) {
    case KF_PSEUDO_LIST_DRIVES:
        *pSuccess = HandleListDrives(ppOut, pOutSize);
        return TRUE;
    case KF_PSEUDO_LIST_DIRECTORY:
        *pSuccess = HandleListDirectory(inputBuf, inputSize, ppOut, pOutSize);
        return TRUE;
    case KF_PSEUDO_CREATE_PROCESS:
        *pSuccess = HandleCreateProcess(inputBuf, inputSize, ppOut, pOutSize);
        return TRUE;
    case KF_PSEUDO_LOAD_DRIVER:
        *pSuccess = HandleLoadDriver(inputBuf, inputSize, ppOut, pOutSize);
        return TRUE;
    case KF_PSEUDO_UNLOAD_DRIVER:
        *pSuccess = HandleUnloadDriver(inputBuf, inputSize, ppOut, pOutSize);
        return TRUE;
    case KF_PSEUDO_START_DRIVER:
        *pSuccess = HandleStartDriver(inputBuf, inputSize, ppOut, pOutSize);
        return TRUE;
    case KF_PSEUDO_READ_FILE:
        *pSuccess = HandleReadFile(inputBuf, inputSize, ppOut, pOutSize);
        return TRUE;
    case KF_PSEUDO_WRITE_FILE:
        *pSuccess = HandleWriteFile(inputBuf, inputSize, ppOut, pOutSize);
        return TRUE;
    case KF_PSEUDO_DELETE_PATH:
        *pSuccess = HandleDeletePath(inputBuf, inputSize, ppOut, pOutSize);
        return TRUE;
    case KF_PSEUDO_CREATE_DIR:
        *pSuccess = HandleCreateDir(inputBuf, inputSize, ppOut, pOutSize);
        return TRUE;
    case KF_PSEUDO_RENAME_PATH:
        *pSuccess = HandleRenamePath(inputBuf, inputSize, ppOut, pOutSize);
        return TRUE;
    default:
        break;
    }

    return FALSE;  /* Not a pseudo-IOCTL — forward to driver */
}

/* ── Per-request work item for thread pool ── */

typedef struct _REQUEST_ITEM {
    SOCKET      client;
    HANDLE      hDevice;
    const char *tag;
    CRITICAL_SECTION *pSendLock;
    volatile LONG    *pActiveWorkers;
    volatile LONG    *pShutdown;

    DWORD       ioctlCode;
    DWORD       inputSize;
    BYTE       *inputBuf;
} REQUEST_ITEM;

static DWORD WINAPI RequestWorker(LPVOID param)
{
    REQUEST_ITEM *req = (REQUEST_ITEM *)param;
    BYTE *outputBuf = NULL;
    DWORD outputSize = 0;
    DWORD bytesReturned = 0;
    BOOL  success;
    DWORD win32Error = 0;

    /* Check if this is a relay pseudo-IOCTL */
    BYTE *pseudoOut = NULL;
    DWORD pseudoOutSize = 0;
    BOOL pseudoSuccess = FALSE;

    if (TryHandlePseudoIoctl(req->ioctlCode, req->inputBuf, req->inputSize,
                              &pseudoOut, &pseudoOutSize, &pseudoSuccess))
    {
        success = pseudoSuccess;
        outputBuf = pseudoOut;
        bytesReturned = pseudoOutSize;
        if (!success)
            win32Error = GetLastError();
    }
    else
    {
        /* Forward to driver via DeviceIoControl */
        outputSize = KF_MAX_BUFFER;
        outputBuf = (BYTE *)malloc(outputSize);
        if (!outputBuf) {
            free(req->inputBuf);
            free(req);
            return 1;
        }

        success = DeviceIoControl(
            req->hDevice,
            req->ioctlCode,
            req->inputBuf, req->inputSize,
            outputBuf, outputSize,
            &bytesReturned, NULL);

        if (!success)
            win32Error = GetLastError();
    }

    /* Serialize the response on the socket — skip if channel is shutting down */
    if (!*req->pShutdown) {
        EnterCriticalSection(req->pSendLock);
        {
            DWORD successFlag = success ? 1 : 0;
            DWORD outLen = success ? bytesReturned : 0;

            SendAll(req->client, &successFlag, 4);
            SendAll(req->client, &win32Error, 4);
            SendAll(req->client, &outLen, 4);
            if (outLen > 0)
                SendAll(req->client, outputBuf, outLen);
        }
        LeaveCriticalSection(req->pSendLock);
    }

    free(req->inputBuf);
    free(outputBuf);
    InterlockedDecrement(req->pActiveWorkers);
    free(req);
    return 0;
}

/*
 * Channel loop: reads requests sequentially (they arrive in order on TCP),
 * but dispatches each to a thread pool worker so blocking IOCTLs don't
 * stall the channel.  Responses are serialized via sendLock.
 */
static void ChannelLoop(SOCKET client, HANDLE hDevice, const char *tag)
{
    CRITICAL_SECTION sendLock;
    volatile LONG activeWorkers = 0;
    volatile LONG shutdown = 0;
    InitializeCriticalSection(&sendLock);

    for (;;) {
        DWORD ioctlCode, inputSize;
        BYTE *inputBuf = NULL;
        REQUEST_ITEM *req;

        if (!RecvAll(client, &ioctlCode, 4)) break;
        if (!RecvAll(client, &inputSize, 4))  break;

        if (inputSize > KF_MAX_BUFFER) {
            printf("[%s] Input too large: %lu\n", tag, inputSize);
            break;
        }

        if (inputSize > 0) {
            inputBuf = (BYTE *)malloc(inputSize);
            if (!inputBuf) break;
            if (!RecvAll(client, inputBuf, inputSize)) {
                free(inputBuf);
                break;
            }
        }

        req = (REQUEST_ITEM *)malloc(sizeof(REQUEST_ITEM));
        if (!req) {
            free(inputBuf);
            break;
        }
        req->client        = client;
        req->hDevice       = hDevice;
        req->tag           = tag;
        req->pSendLock     = &sendLock;
        req->pActiveWorkers = &activeWorkers;
        req->pShutdown     = &shutdown;
        req->ioctlCode     = ioctlCode;
        req->inputSize     = inputSize;
        req->inputBuf      = inputBuf;

        InterlockedIncrement(&activeWorkers);
        if (!QueueUserWorkItem(RequestWorker, req, WT_EXECUTEDEFAULT)) {
            printf("[%s] QueueUserWorkItem failed: %lu\n", tag, GetLastError());
            InterlockedDecrement(&activeWorkers);
            free(inputBuf);
            free(req);
            break;
        }
    }

    /* Signal workers to skip SendAll (socket is about to close) */
    InterlockedExchange(&shutdown, 1);

    /* Wait for all thread pool workers to finish (up to 5s) */
    for (int i = 0; i < 50 && activeWorkers > 0; i++)
        Sleep(100);

    if (activeWorkers > 0)
        printf("[%s] Warning: %ld worker(s) still active after 5s\n", tag, activeWorkers);

    DeleteCriticalSection(&sendLock);
}

/*
 * DBG channel: SYNCHRONOUS loop — one request at a time, no thread pool.
 * This prevents stale workers from holding pending IOCTLs or corrupting
 * the response stream.  WAIT_DEBUG_EVENT blocks here until the driver
 * completes the IRP (or cancels it), then the response goes back on TCP.
 */
static DWORD WINAPI DbgChannelThread(LPVOID param)
{
    SOCKET dbgSock = (SOCKET)(ULONG_PTR)param;
    printf("[dbg] Debug channel thread started (synchronous mode)\n");

    for (;;) {
        DWORD ioctlCode, inputSize;
        BYTE *inputBuf = NULL;

        if (!RecvAll(dbgSock, &ioctlCode, 4)) break;
        if (!RecvAll(dbgSock, &inputSize, 4)) break;

        if (inputSize > KF_MAX_BUFFER) {
            printf("[dbg] Input too large: %lu\n", inputSize);
            break;
        }

        if (inputSize > 0) {
            inputBuf = (BYTE *)malloc(inputSize);
            if (!inputBuf) break;
            if (!RecvAll(dbgSock, inputBuf, inputSize)) {
                free(inputBuf);
                break;
            }
        }

        /* Execute IOCTL synchronously — blocks for WAIT_DEBUG_EVENT */
        DWORD outputSize = KF_MAX_BUFFER;
        BYTE *outputBuf = (BYTE *)malloc(outputSize);
        if (!outputBuf) { free(inputBuf); break; }

        DWORD bytesReturned = 0;
        BOOL success = DeviceIoControl(
            g_hDeviceDbg,
            ioctlCode,
            inputBuf, inputSize,
            outputBuf, outputSize,
            &bytesReturned, NULL);

        DWORD win32Error = success ? 0 : GetLastError();

        /* Send response directly — no sendLock needed (single-threaded) */
        {
            DWORD successFlag = success ? 1 : 0;
            DWORD outLen = success ? bytesReturned : 0;

            if (!SendAll(dbgSock, &successFlag, 4) ||
                !SendAll(dbgSock, &win32Error, 4) ||
                !SendAll(dbgSock, &outLen, 4) ||
                (outLen > 0 && !SendAll(dbgSock, outputBuf, outLen)))
            {
                free(inputBuf);
                free(outputBuf);
                break;
            }
        }

        free(inputBuf);
        free(outputBuf);
    }

    printf("[dbg] Debug channel disconnected\n");
    closesocket(dbgSock);
    return 0;
}

static SOCKET AcceptOne(SOCKET listenSock, const char *label)
{
    struct sockaddr_in addr;
    int addrLen = sizeof(addr);
    char ipStr[INET_ADDRSTRLEN];

    SOCKET s = accept(listenSock, (struct sockaddr *)&addr, &addrLen);
    if (s == INVALID_SOCKET) {
        printf("[!] accept(%s) failed: %d\n", label, WSAGetLastError());
        return INVALID_SOCKET;
    }

    {
        BOOL opt = TRUE;
        setsockopt(s, IPPROTO_TCP, TCP_NODELAY, (char *)&opt, sizeof(opt));
    }

    inet_ntop(AF_INET, &addr.sin_addr, ipStr, sizeof(ipStr));
    printf("[+] %s channel connected: %s:%d\n", label, ipStr, ntohs(addr.sin_port));
    return s;
}

static LONG WINAPI CrashHandler(EXCEPTION_POINTERS *ep)
{
    printf("[!] CRASH: code=0x%08lX addr=%p\n",
           ep->ExceptionRecord->ExceptionCode,
           ep->ExceptionRecord->ExceptionAddress);
    printf("[!] Relay will NOT exit — restarting session loop\n");
    fflush(stdout);
    /* Return CONTINUE to let SEH in main loop handle it */
    return EXCEPTION_CONTINUE_SEARCH;
}

int main(int argc, char *argv[])
{
    WSADATA wsa;
    SOCKET listenSock;
    struct sockaddr_in serverAddr;
    USHORT port = KF_RELAY_PORT;
    const char *bindAddr = "0.0.0.0";

    SetUnhandledExceptionFilter(CrashHandler);
    printf("KernelFlirt TCP Relay v3.0 (kernel driver mode)\n");

    /* Parse args */
    for (int i = 1; i < argc; i++) {
        if (strcmp(argv[i], "--port") == 0 && i + 1 < argc) {
            port = (USHORT)atoi(argv[++i]);
        } else if (strcmp(argv[i], "--bind") == 0 && i + 1 < argc) {
            bindAddr = argv[++i];
        } else if (strcmp(argv[i], "--help") == 0 || strcmp(argv[i], "-h") == 0) {
            printf("Usage: KfRelay [--port <port>] [--bind <addr>]\n");
            printf("  Default port: %d\n", KF_RELAY_PORT);
            printf("  Default bind: 0.0.0.0\n");
            return 0;
        }
    }

    /* Init Winsock */
    if (WSAStartup(MAKEWORD(2, 2), &wsa) != 0) {
        printf("[!] WSAStartup failed: %d\n", WSAGetLastError());
        return 1;
    }

    /* Try to open driver (non-fatal — will retry when client connects) */
    if (!OpenDriver()) {
        printf("[!] Driver not available yet — will retry on client connect\n");
    }

    /* Create listening socket */
    listenSock = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (listenSock == INVALID_SOCKET) {
        printf("[!] socket() failed: %d\n", WSAGetLastError());
        CloseDriver();
        WSACleanup();
        return 1;
    }

    {
        BOOL opt = TRUE;
        setsockopt(listenSock, SOL_SOCKET, SO_REUSEADDR, (char *)&opt, sizeof(opt));
    }

    memset(&serverAddr, 0, sizeof(serverAddr));
    serverAddr.sin_family = AF_INET;
    serverAddr.sin_port = htons(port);
    inet_pton(AF_INET, bindAddr, &serverAddr.sin_addr);

    if (bind(listenSock, (struct sockaddr *)&serverAddr, sizeof(serverAddr)) == SOCKET_ERROR) {
        printf("[!] bind() failed: %d\n", WSAGetLastError());
        closesocket(listenSock);
        CloseDriver();
        WSACleanup();
        return 1;
    }

    if (listen(listenSock, 2) == SOCKET_ERROR) {
        printf("[!] listen() failed: %d\n", WSAGetLastError());
        closesocket(listenSock);
        CloseDriver();
        WSACleanup();
        return 1;
    }

    printf("[+] Listening on %s:%d\n", bindAddr, port);

    /* Session loop — one pair of clients at a time */
    for (;;) {
        SOCKET cmdSock, dbgSock;
        HANDLE dbgThread;

        printf("[*] Waiting for CMD channel (connection 1/2)...\n");
        cmdSock = AcceptOne(listenSock, "cmd");
        if (cmdSock == INVALID_SOCKET) {
            /* If listen socket is broken, wait a bit and try to recover */
            Sleep(1000);
            continue;
        }

        printf("[*] Waiting for DBG channel (connection 2/2)...\n");
        dbgSock = AcceptOne(listenSock, "dbg");
        if (dbgSock == INVALID_SOCKET) {
            closesocket(cmdSock);
            continue;
        }

        /* (Re-)open driver handles if needed — covers both initial failure
           and driver-reloaded-between-sessions scenarios */
        if (g_hDeviceCmd == INVALID_HANDLE_VALUE ||
            g_hDeviceDbg == INVALID_HANDLE_VALUE) {
            CloseDriver();
            if (!OpenDriver()) {
                printf("[!] Driver not available, closing session\n");
                closesocket(cmdSock);
                closesocket(dbgSock);
                continue;
            }
        }

        printf("[+] Both channels connected — session active\n");

        __try {

        dbgThread = CreateThread(NULL, 0, DbgChannelThread, (LPVOID)(ULONG_PTR)dbgSock, 0, NULL);
        if (!dbgThread) {
            printf("[!] CreateThread failed: %lu\n", GetLastError());
            closesocket(cmdSock);
            closesocket(dbgSock);
            continue;
        }

        /* CMD channel runs on main thread */
        ChannelLoop(cmdSock, g_hDeviceCmd, "cmd");

        printf("[-] CMD channel disconnected\n");
        closesocket(cmdSock);

        /* Cancel any blocking IOCTL (e.g. WAIT_DEBUG_EVENT) on the DBG channel.
           CancelIoEx cancels all pending I/O on the device handle (covers thread pool workers).
           CancelSynchronousIo cancels blocking I/O on the DBG thread itself. */
        if (g_hDeviceDbg != INVALID_HANDLE_VALUE)
            CancelIoEx(g_hDeviceDbg, NULL);
        CancelSynchronousIo(dbgThread);

        /* Wait longer — TerminateThread is unsafe (corrupts heap/locks, causes crashes).
           If the thread is still stuck after 10s, just leak it and move on. */
        if (WaitForSingleObject(dbgThread, 10000) == WAIT_TIMEOUT) {
            printf("[!] DBG thread did not exit in 10s — leaking thread handle\n");
        } else {
            CloseHandle(dbgThread);
        }

        /* Reset driver state: remove all BPs, hooks, unblock threads */
        ResetDriver();

        /* Re-open driver handles — the driver may have been reloaded
           between sessions, making old handles stale. */
        CloseDriver();
        if (!OpenDriver()) {
            printf("[!] Driver not available after session end. "
                   "Will retry on next connection.\n");
        }

        printf("[-] Session ended (driver reset)\n");

        } __except(EXCEPTION_EXECUTE_HANDLER) {
            printf("[!] Session crashed (exception 0x%08lX) — recovering\n",
                   GetExceptionCode());
            /* Best-effort cleanup */
            CloseDriver();
            if (!OpenDriver())
                printf("[!] Driver unavailable after crash — will retry\n");
        }
    }

    closesocket(listenSock);
    CloseDriver();
    WSACleanup();
    return 0;
}
