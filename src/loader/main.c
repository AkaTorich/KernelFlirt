/*
 * KernelFlirt - Driver Loader
 * main.c - CLI entry point for load/unload/status
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <windows.h>

/* Defined in service.c */
int KfLoadDriver(const char *driverPath, const char *serviceName);
int KfUnloadDriver(const char *serviceName);
int KfQueryStatus(const char *serviceName);

/* Defined in vmdetect.c */
void KfDetectVM(void);
int  KfCheckTestSigning(void);

static void PrintUsage(void)
{
    printf("KernelFlirt Driver Loader v1.0\n");
    printf("Usage:\n");
    printf("  KfLoader load   [--path <driver.sys>] [--name <ServiceName>]\n");
    printf("  KfLoader unload [--name <ServiceName>]\n");
    printf("  KfLoader status [--name <ServiceName>]\n");
    printf("  KfLoader info\n");
    printf("\nDefaults:\n");
    printf("  path: KernelFlirt.sys (same directory as loader)\n");
    printf("  name: KernelFlirt\n");
}

static void GetDefaultDriverPath(char *buffer, size_t bufferSize)
{
    char modulePath[MAX_PATH];
    char *lastSlash;

    GetModuleFileNameA(NULL, modulePath, MAX_PATH);
    lastSlash = strrchr(modulePath, '\\');
    if (lastSlash) {
        *(lastSlash + 1) = '\0';
    }
    snprintf(buffer, bufferSize, "%sKernelFlirt.sys", modulePath);
}

int main(int argc, char *argv[])
{
    const char *command     = NULL;
    const char *driverPath  = NULL;
    const char *serviceName = "KernelFlirt";
    char        defaultPath[MAX_PATH];
    int         i;

    if (argc < 2) {
        PrintUsage();
        return 1;
    }

    command = argv[1];

    /* Parse optional arguments */
    for (i = 2; i < argc; i++) {
        if (strcmp(argv[i], "--path") == 0 && i + 1 < argc) {
            driverPath = argv[++i];
        } else if (strcmp(argv[i], "--name") == 0 && i + 1 < argc) {
            serviceName = argv[++i];
        }
    }

    if (_stricmp(command, "load") == 0) {
        if (!driverPath) {
            GetDefaultDriverPath(defaultPath, sizeof(defaultPath));
            driverPath = defaultPath;
        }

        printf("[*] Loading driver: %s\n", driverPath);
        printf("[*] Service name:   %s\n", serviceName);

        /* Check environment */
        KfDetectVM();
        if (!KfCheckTestSigning()) {
            printf("[!] WARNING: Test signing may not be enabled.\n");
            printf("[!] Run: bcdedit /set testsigning on\n");
        }

        return KfLoadDriver(driverPath, serviceName);

    } else if (_stricmp(command, "unload") == 0) {
        printf("[*] Unloading driver: %s\n", serviceName);
        return KfUnloadDriver(serviceName);

    } else if (_stricmp(command, "status") == 0) {
        return KfQueryStatus(serviceName);

    } else if (_stricmp(command, "info") == 0) {
        KfDetectVM();
        KfCheckTestSigning();
        return 0;

    } else {
        printf("[!] Unknown command: %s\n", command);
        PrintUsage();
        return 1;
    }
}
