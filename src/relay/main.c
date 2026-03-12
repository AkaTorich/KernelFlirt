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

static HANDLE g_hDeviceCmd = INVALID_HANDLE_VALUE;  /* CMD channel handle */
static HANDLE g_hDeviceDbg = INVALID_HANDLE_VALUE;  /* DBG channel handle */

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

/* Structs KF_DRIVE_ENTRY, KF_DIR_ENTRY, KF_CREATE_PROCESS_OUT come from kf_shared.h */

static BOOL HandleListDrives(BYTE **ppOut, DWORD *pOutSize)
{
    DWORD mask = GetLogicalDrives();
    if (!mask) return FALSE;

    /* Count drives */
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

        /* Try to get volume label */
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

    /* Input is a null-terminated wide string (path) */
    WCHAR *path = (WCHAR *)inputBuf;
    /* Ensure null-terminated */
    int maxChars = inputSize / sizeof(WCHAR);
    path[maxChars - 1] = L'\0';

    /* Build search pattern: path\* */
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

    /* First pass: count entries (skip . and ..) */
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
        entries[count].FileSize = ((ULONGLONG)fd.nFileSizeHigh << 32) | fd.nFileSizeLow;
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

static BOOL HandleCreateProcess(BYTE *inputBuf, DWORD inputSize, BYTE **ppOut, DWORD *pOutSize)
{
    if (!inputBuf || inputSize < 4) return FALSE;

    WCHAR *exePath = (WCHAR *)inputBuf;
    int maxChars = inputSize / sizeof(WCHAR);
    exePath[maxChars - 1] = L'\0';

    STARTUPINFOW si;
    PROCESS_INFORMATION pi;
    memset(&si, 0, sizeof(si));
    si.cb = sizeof(si);
    memset(&pi, 0, sizeof(pi));

    /* Create process suspended so debugger can set breakpoints first */
    BOOL ok = CreateProcessW(
        exePath, NULL, NULL, NULL, FALSE,
        CREATE_SUSPENDED,
        NULL, NULL, &si, &pi);

    if (!ok) {
        printf("[relay] CreateProcess failed: %lu\n", GetLastError());
        return FALSE;
    }

    printf("[relay] Created process PID=%lu TID=%lu (suspended)\n", pi.dwProcessId, pi.dwThreadId);

    /* Query PEB to get ImageBaseAddress (valid even while suspended) */
    ULONG64 imageBase = 0;
    {
        typedef LONG (NTAPI *NtQueryInformationProcess_t)(
            HANDLE, ULONG, PVOID, ULONG, PULONG);
        /* x64 PROCESS_BASIC_INFORMATION layout:
         *   NTSTATUS  ExitStatus       (4 + 4 padding = offset 0)
         *   PPEB      PebBaseAddress   (8 bytes        = offset 8)
         *   ULONG_PTR AffinityMask     (8 bytes        = offset 16)
         *   LONG      BasePriority     (4 + 4 padding  = offset 24)
         *   ULONG_PTR UniqueProcessId  (8 bytes        = offset 32)
         *   ULONG_PTR InheritedFrom    (8 bytes        = offset 40)
         *   Total: 48 bytes */
        typedef struct {
            LONG      ExitStatus;
            LONG      Pad0;
            PVOID     PebBaseAddress;
            ULONG_PTR AffinityMask;
            LONG      BasePriority;
            LONG      Pad1;
            ULONG_PTR UniqueProcessId;
            ULONG_PTR InheritedFromUniqueProcessId;
        } PBI;

        NtQueryInformationProcess_t pNtQIP = (NtQueryInformationProcess_t)
            GetProcAddress(GetModuleHandleA("ntdll.dll"), "NtQueryInformationProcess");
        if (pNtQIP) {
            PBI pbi = {0};
            ULONG retLen = 0;
            LONG ntStatus = pNtQIP(pi.hProcess, 0 /*ProcessBasicInformation*/, &pbi, sizeof(pbi), &retLen);
            printf("[relay] NtQIP status=0x%08lX retLen=%lu sizeof(PBI)=%zu\n",
                   (unsigned long)ntStatus, retLen, sizeof(pbi));
            if (ntStatus == 0) {
                printf("[relay] PEB @ %p\n", pbi.PebBaseAddress);
                /* PEB+0x10 = ImageBaseAddress on x64 */
                SIZE_T bytesRead = 0;
                BOOL readOk = ReadProcessMemory(pi.hProcess, (BYTE *)pbi.PebBaseAddress + 0x10,
                                  &imageBase, sizeof(imageBase), &bytesRead);
                printf("[relay] ReadProcessMemory(PEB+0x10) ok=%d read=%zu ImageBase=0x%llX\n",
                       readOk, bytesRead, imageBase);
            }
        } else {
            printf("[relay] NtQueryInformationProcess not found!\n");
        }
    }

    KF_CREATE_PROCESS_OUT *out = (KF_CREATE_PROCESS_OUT *)calloc(1, sizeof(KF_CREATE_PROCESS_OUT));
    if (!out) {
        TerminateProcess(pi.hProcess, 1);
        CloseHandle(pi.hProcess);
        CloseHandle(pi.hThread);
        return FALSE;
    }

    out->ProcessId = pi.dwProcessId;
    out->ThreadId = pi.dwThreadId;
    out->ImageBase = imageBase;

    /* Don't close process/thread handles — we want the process to stay alive */
    CloseHandle(pi.hProcess);
    CloseHandle(pi.hThread);

    *ppOut = (BYTE *)out;
    *pOutSize = sizeof(KF_CREATE_PROCESS_OUT);
    return TRUE;
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
    default:
        return FALSE;
    }
}

/* ── Per-request work item for thread pool ── */

typedef struct _REQUEST_ITEM {
    SOCKET      client;
    HANDLE      hDevice;
    const char *tag;
    CRITICAL_SECTION *pSendLock;  /* serialize responses on the socket */

    /* Pre-read request data (read by the reader thread under recvLock) */
    DWORD       ioctlCode;
    DWORD       inputSize;
    BYTE       *inputBuf;    /* NULL if inputSize==0 */
} REQUEST_ITEM;

static DWORD WINAPI RequestWorker(LPVOID param)
{
    REQUEST_ITEM *req = (REQUEST_ITEM *)param;
    BYTE *outputBuf = NULL;
    DWORD outputSize = 0;
    DWORD bytesReturned = 0;
    BOOL  success;
    DWORD win32Error = 0;

    /* Check if this is a relay pseudo-IOCTL (file browser, create process, etc.) */
    BYTE *pseudoOut = NULL;
    DWORD pseudoOutSize = 0;
    BOOL pseudoSuccess = FALSE;

    if (TryHandlePseudoIoctl(req->ioctlCode, req->inputBuf, req->inputSize,
                              &pseudoOut, &pseudoOutSize, &pseudoSuccess))
    {
        /* Handled locally by relay */
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

    /* Serialize the response on the socket */
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

    free(req->inputBuf);
    free(outputBuf);
    free(req);
    return 0;
}

/*
 * Channel loop: reads requests sequentially (they arrive in order on TCP),
 * but dispatches each to a thread pool worker so blocking IOCTLs don't
 * stall the channel.  Responses are serialized via sendLock.
 *
 * Returns when the socket disconnects or on error.
 */
static void ChannelLoop(SOCKET client, HANDLE hDevice, const char *tag)
{
    CRITICAL_SECTION sendLock;
    InitializeCriticalSection(&sendLock);

    for (;;) {
        DWORD ioctlCode, inputSize;
        BYTE *inputBuf = NULL;
        REQUEST_ITEM *req;

        /* Read header */
        if (!RecvAll(client, &ioctlCode, 4)) break;
        if (!RecvAll(client, &inputSize, 4))  break;

        if (inputSize > KF_MAX_BUFFER) {
            printf("[%s] Input too large: %lu\n", tag, inputSize);
            break;
        }

        /* Read input data */
        if (inputSize > 0) {
            inputBuf = (BYTE *)malloc(inputSize);
            if (!inputBuf) break;
            if (!RecvAll(client, inputBuf, inputSize)) {
                free(inputBuf);
                break;
            }
        }

        /* Build work item */
        req = (REQUEST_ITEM *)malloc(sizeof(REQUEST_ITEM));
        if (!req) {
            free(inputBuf);
            break;
        }
        req->client    = client;
        req->hDevice   = hDevice;
        req->tag       = tag;
        req->pSendLock = &sendLock;
        req->ioctlCode = ioctlCode;
        req->inputSize = inputSize;
        req->inputBuf  = inputBuf;

        /* Dispatch to thread pool */
        if (!QueueUserWorkItem(RequestWorker, req, WT_EXECUTEDEFAULT)) {
            printf("[%s] QueueUserWorkItem failed: %lu\n", tag, GetLastError());
            free(inputBuf);
            free(req);
            break;
        }
    }

    /* Wait a bit for pending workers to finish sending */
    Sleep(500);
    DeleteCriticalSection(&sendLock);
}

/* Thread procedure for the DBG channel */
static DWORD WINAPI DbgChannelThread(LPVOID param)
{
    SOCKET dbgSock = (SOCKET)(ULONG_PTR)param;
    printf("[dbg] Debug channel thread started\n");

    ChannelLoop(dbgSock, g_hDeviceDbg, "dbg");

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

    /* Disable Nagle */
    {
        BOOL opt = TRUE;
        setsockopt(s, IPPROTO_TCP, TCP_NODELAY, (char *)&opt, sizeof(opt));
    }

    inet_ntop(AF_INET, &addr.sin_addr, ipStr, sizeof(ipStr));
    printf("[+] %s channel connected: %s:%d\n", label, ipStr, ntohs(addr.sin_port));
    return s;
}

int main(int argc, char *argv[])
{
    WSADATA wsa;
    SOCKET listenSock;
    struct sockaddr_in serverAddr;
    USHORT port = KF_RELAY_PORT;
    const char *bindAddr = "0.0.0.0";

    printf("KernelFlirt TCP Relay v3.0 (threaded channels)\n");

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

    /* Open driver */
    if (!OpenDriver()) {
        printf("[!] Cannot open driver. Is it loaded?\n");
        WSACleanup();
        return 1;
    }

    /* Create listening socket */
    listenSock = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (listenSock == INVALID_SOCKET) {
        printf("[!] socket() failed: %d\n", WSAGetLastError());
        CloseDriver();
        WSACleanup();
        return 1;
    }

    /* Allow port reuse */
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
        if (cmdSock == INVALID_SOCKET) continue;

        printf("[*] Waiting for DBG channel (connection 2/2)...\n");
        dbgSock = AcceptOne(listenSock, "dbg");
        if (dbgSock == INVALID_SOCKET) {
            closesocket(cmdSock);
            continue;
        }

        printf("[+] Both channels connected — session active\n");

        /* Run DBG channel in a separate thread */
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

        /* Wait for DBG thread to finish */
        WaitForSingleObject(dbgThread, 3000);
        CloseHandle(dbgThread);

        /* Reset driver state: remove all BPs, hooks, unblock threads */
        ResetDriver();

        printf("[-] Session ended (driver reset)\n");
    }

    closesocket(listenSock);
    CloseDriver();
    WSACleanup();
    return 0;
}
