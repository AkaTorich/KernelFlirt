/*
 * KernelFlirt - Service management
 * service.c - Load/Unload driver via SCM API
 */

#include <stdio.h>
#include <windows.h>

int KfLoadDriver(const char *driverPath, const char *serviceName)
{
    SC_HANDLE   scManager = NULL;
    SC_HANDLE   scService = NULL;
    char        fullPath[MAX_PATH];
    char        destPath[MAX_PATH];
    char        winDir[MAX_PATH];
    DWORD       pathLen;
    int         result = 0;

    /* Get full path of source .sys */
    pathLen = GetFullPathNameA(driverPath, MAX_PATH, fullPath, NULL);
    if (pathLen == 0 || pathLen >= MAX_PATH) {
        printf("[!] Failed to resolve driver path: %lu\n", GetLastError());
        return 1;
    }

    /* Check file exists */
    if (GetFileAttributesA(fullPath) == INVALID_FILE_ATTRIBUTES) {
        printf("[!] Driver file not found: %s\n", fullPath);
        return 1;
    }

    /* Copy .sys to System32\drivers\ */
    GetWindowsDirectoryA(winDir, MAX_PATH);
    _snprintf(destPath, MAX_PATH, "%s\\System32\\drivers\\%s.sys",
              winDir, serviceName);
    destPath[MAX_PATH - 1] = '\0';

    if (!CopyFileA(fullPath, destPath, FALSE)) {
        printf("[!] Failed to copy driver to %s: %lu\n", destPath, GetLastError());
        return 1;
    }
    printf("[+] Copied to %s\n", destPath);

    /* Use the destination path for SCM registration */
    _snprintf(fullPath, MAX_PATH, "%s", destPath);
    fullPath[MAX_PATH - 1] = '\0';

    /* Open SCM */
    scManager = OpenSCManagerA(NULL, NULL, SC_MANAGER_CREATE_SERVICE);
    if (!scManager) {
        printf("[!] OpenSCManager failed: %lu (run as Administrator)\n", GetLastError());
        return 1;
    }

    /* Try to create the service */
    scService = CreateServiceA(
        scManager,
        serviceName,
        serviceName,
        SERVICE_ALL_ACCESS,
        SERVICE_KERNEL_DRIVER,
        SERVICE_DEMAND_START,
        SERVICE_ERROR_NORMAL,
        fullPath,
        NULL, NULL, NULL, NULL, NULL
    );

    if (!scService) {
        DWORD err = GetLastError();
        if (err == ERROR_SERVICE_EXISTS) {
            printf("[*] Service already exists, opening...\n");
            scService = OpenServiceA(scManager, serviceName, SERVICE_ALL_ACCESS);
            if (!scService) {
                printf("[!] OpenService failed: %lu\n", GetLastError());
                CloseServiceHandle(scManager);
                return 1;
            }
        } else {
            printf("[!] CreateService failed: %lu\n", err);
            CloseServiceHandle(scManager);
            return 1;
        }
    } else {
        printf("[+] Service created successfully\n");
    }

    /* Start the service */
    if (!StartServiceA(scService, 0, NULL)) {
        DWORD err = GetLastError();
        if (err == ERROR_SERVICE_ALREADY_RUNNING) {
            printf("[*] Driver is already running\n");
        } else {
            printf("[!] StartService failed: %lu\n", err);
            result = 1;
        }
    } else {
        printf("[+] Driver loaded successfully\n");
    }

    CloseServiceHandle(scService);
    CloseServiceHandle(scManager);
    return result;
}

int KfUnloadDriver(const char *serviceName)
{
    SC_HANDLE       scManager = NULL;
    SC_HANDLE       scService = NULL;
    SERVICE_STATUS  svcStatus;
    int             result = 0;

    scManager = OpenSCManagerA(NULL, NULL, SC_MANAGER_CONNECT);
    if (!scManager) {
        printf("[!] OpenSCManager failed: %lu\n", GetLastError());
        return 1;
    }

    scService = OpenServiceA(scManager, serviceName, SERVICE_ALL_ACCESS);
    if (!scService) {
        printf("[!] OpenService failed: %lu\n", GetLastError());
        CloseServiceHandle(scManager);
        return 1;
    }

    /* Stop the service */
    if (!ControlService(scService, SERVICE_CONTROL_STOP, &svcStatus)) {
        DWORD err = GetLastError();
        if (err == ERROR_SERVICE_NOT_ACTIVE) {
            printf("[*] Driver is already stopped\n");
        } else {
            printf("[!] ControlService(STOP) failed: %lu\n", err);
            result = 1;
        }
    } else {
        printf("[+] Driver stopped\n");
    }

    /* Delete the service */
    if (!DeleteService(scService)) {
        DWORD err = GetLastError();
        if (err == ERROR_SERVICE_MARKED_FOR_DELETE) {
            printf("[*] Service already marked for deletion\n");
        } else {
            printf("[!] DeleteService failed: %lu\n", err);
            result = 1;
        }
    } else {
        printf("[+] Service deleted\n");
    }

    CloseServiceHandle(scService);
    CloseServiceHandle(scManager);

    /* Remove .sys from System32\drivers\ */
    {
        char winDir[MAX_PATH];
        char destPath[MAX_PATH];
        GetWindowsDirectoryA(winDir, MAX_PATH);
        _snprintf(destPath, MAX_PATH, "%s\\System32\\drivers\\%s.sys",
                  winDir, serviceName);
        destPath[MAX_PATH - 1] = '\0';

        if (DeleteFileA(destPath)) {
            printf("[+] Removed %s\n", destPath);
        }
    }

    return result;
}

int KfQueryStatus(const char *serviceName)
{
    SC_HANDLE               scManager = NULL;
    SC_HANDLE               scService = NULL;
    SERVICE_STATUS_PROCESS  svcStatus;
    DWORD                   bytesNeeded;

    scManager = OpenSCManagerA(NULL, NULL, SC_MANAGER_CONNECT);
    if (!scManager) {
        printf("[!] OpenSCManager failed: %lu\n", GetLastError());
        return 1;
    }

    scService = OpenServiceA(scManager, serviceName, SERVICE_QUERY_STATUS);
    if (!scService) {
        DWORD err = GetLastError();
        if (err == ERROR_SERVICE_DOES_NOT_EXIST) {
            printf("[*] Service '%s' does not exist\n", serviceName);
        } else {
            printf("[!] OpenService failed: %lu\n", err);
        }
        CloseServiceHandle(scManager);
        return 1;
    }

    if (!QueryServiceStatusEx(scService, SC_STATUS_PROCESS_INFO,
            (LPBYTE)&svcStatus, sizeof(svcStatus), &bytesNeeded)) {
        printf("[!] QueryServiceStatusEx failed: %lu\n", GetLastError());
        CloseServiceHandle(scService);
        CloseServiceHandle(scManager);
        return 1;
    }

    printf("[*] Service: %s\n", serviceName);
    printf("[*] State:   ");

    switch (svcStatus.dwCurrentState) {
        case SERVICE_STOPPED:          printf("STOPPED\n"); break;
        case SERVICE_START_PENDING:    printf("START_PENDING\n"); break;
        case SERVICE_STOP_PENDING:     printf("STOP_PENDING\n"); break;
        case SERVICE_RUNNING:          printf("RUNNING\n"); break;
        case SERVICE_CONTINUE_PENDING: printf("CONTINUE_PENDING\n"); break;
        case SERVICE_PAUSE_PENDING:    printf("PAUSE_PENDING\n"); break;
        case SERVICE_PAUSED:           printf("PAUSED\n"); break;
        default:                       printf("UNKNOWN (%lu)\n", svcStatus.dwCurrentState); break;
    }

    printf("[*] PID:     %lu\n", svcStatus.dwProcessId);

    CloseServiceHandle(scService);
    CloseServiceHandle(scManager);
    return 0;
}
