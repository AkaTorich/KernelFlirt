/*
 * KernelFlirt - VM Detection
 * vmdetect.c - Detect hypervisor and test signing status
 */

#include <stdio.h>
#include <string.h>
#include <windows.h>
#include <intrin.h>

void KfDetectVM(void)
{
    int     cpuInfo[4] = {0};
    char    vendor[13]  = {0};

    /* Check CPUID leaf 1, bit 31 of ECX = hypervisor present */
    __cpuid(cpuInfo, 1);
    if (!(cpuInfo[2] & (1 << 31))) {
        printf("[*] Hypervisor: not detected (bare metal)\n");
        return;
    }

    /* Get hypervisor vendor string from leaf 0x40000000 */
    __cpuid(cpuInfo, 0x40000000);
    memcpy(vendor + 0, &cpuInfo[1], 4);  /* EBX */
    memcpy(vendor + 4, &cpuInfo[2], 4);  /* ECX */
    memcpy(vendor + 8, &cpuInfo[3], 4);  /* EDX */

    printf("[*] Hypervisor: detected\n");
    printf("[*] Vendor:     %s", vendor);

    if (strncmp(vendor, "VMwareVMware", 12) == 0) {
        printf(" (VMware)\n");
    } else if (strncmp(vendor, "VBoxVBoxVBox", 12) == 0) {
        printf(" (VirtualBox)\n");
    } else if (strncmp(vendor, "Microsoft Hv", 12) == 0) {
        printf(" (Hyper-V)\n");
    } else if (strncmp(vendor, "KVMKVMKVM", 9) == 0) {
        printf(" (KVM)\n");
    } else if (strncmp(vendor, "XenVMMXenVMM", 12) == 0) {
        printf(" (Xen)\n");
    } else {
        printf(" (Unknown)\n");
    }
}

int KfCheckTestSigning(void)
{
    /*
     * Check BCD test signing status.
     * We use NtQuerySystemInformation with SystemCodeIntegrityInformation.
     * Alternatively, read the registry or call bcdedit.
     */
    HMODULE ntdll;
    typedef LONG (WINAPI *NtQuerySystemInformation_t)(
        ULONG SystemInformationClass,
        PVOID SystemInformation,
        ULONG SystemInformationLength,
        PULONG ReturnLength
    );

    #define SystemCodeIntegrityInformation 103

    #pragma pack(push, 1)
    typedef struct _SYSTEM_CODEINTEGRITY_INFORMATION {
        ULONG Length;
        ULONG CodeIntegrityOptions;
    } SYSTEM_CODEINTEGRITY_INFORMATION;
    #pragma pack(pop)

    #define CODEINTEGRITY_OPTION_TESTSIGN 0x02

    NtQuerySystemInformation_t pNtQuerySystemInformation;
    SYSTEM_CODEINTEGRITY_INFORMATION ciInfo;
    LONG status;

    ntdll = GetModuleHandleA("ntdll.dll");
    if (!ntdll) {
        printf("[?] Cannot check test signing (ntdll not found)\n");
        return 0;
    }

    pNtQuerySystemInformation = (NtQuerySystemInformation_t)
        GetProcAddress(ntdll, "NtQuerySystemInformation");

    if (!pNtQuerySystemInformation) {
        printf("[?] Cannot check test signing (API not found)\n");
        return 0;
    }

    ciInfo.Length = sizeof(ciInfo);
    ciInfo.CodeIntegrityOptions = 0;

    status = pNtQuerySystemInformation(
        SystemCodeIntegrityInformation,
        &ciInfo,
        sizeof(ciInfo),
        NULL
    );

    if (status != 0) {
        printf("[?] Cannot check test signing (status: 0x%08lX)\n", (unsigned long)status);
        return 0;
    }

    if (ciInfo.CodeIntegrityOptions & CODEINTEGRITY_OPTION_TESTSIGN) {
        printf("[+] Test signing: ENABLED\n");
        return 1;
    } else {
        printf("[-] Test signing: DISABLED\n");
        return 0;
    }
}
