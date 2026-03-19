/**
 * Test program #1: XOR-encrypted strings (no CRT)
 *
 * All strings are pre-XOR'd with a single-byte key in .data section.
 * At runtime, DecryptStringXor() decrypts them in-place.
 * No CRT linked — pure WinAPI only, so compiler cannot optimize away.
 *
 * Build: cl /Od /Zi /GS- xor_strings.c /Fe:xor_strings.exe /link /DEBUG /ENTRY:Entry /SUBSYSTEM:CONSOLE /NODEFAULTLIB kernel32.lib
 *
 * To test with StringDecryptorPlugin:
 *   1. Open xor_strings.exe in KernelFlirt
 *   2. Find DecryptStringXor — look for repeating `call` to same address
 *      (6 calls in a row, each preceded by `lea rcx` + `mov dl, 0x5A`)
 *   3. Set decrypt function = that address
 *   4. Result location = RAX (returns pointer to decrypted string)
 *   5. Encoding = ASCII
 *   6. Click Start, then F9 (Run)
 *   7. Plugin should capture all 6 decrypted strings
 */

#include <windows.h>

#define XOR_KEY 0x5A

/* ---- Encrypted strings (XOR'd with 0x5A) ---- */

/* "Hello, World!" */
static volatile char enc_hello[] = {
    0x12,0x3F,0x36,0x36,0x35,0x76,0x7A,0x0D,0x35,0x28,0x36,0x3E,0x7B, 0x00
};
/* "cmd.exe /c whoami" */
static volatile char enc_cmd[] = {
    0x39,0x37,0x3E,0x74,0x3F,0x22,0x3F,0x7A,0x75,0x39,0x7A,0x2D,0x32,0x35,0x3B,0x37,0x33, 0x00
};
/* "CreateRemoteThread" */
static volatile char enc_api1[] = {
    0x19,0x28,0x3F,0x3B,0x2E,0x3F,0x08,0x3F,0x37,0x35,0x2E,0x3F,0x0E,0x32,0x28,0x3F,0x3B,0x3E, 0x00
};
/* "VirtualAllocEx" */
static volatile char enc_api2[] = {
    0x0C,0x33,0x28,0x2E,0x2F,0x3B,0x36,0x1B,0x36,0x36,0x35,0x39,0x1F,0x22, 0x00
};
/* "HKEY_LOCAL_MACHINE\SOFTWARE\Malware" */
static volatile char enc_reg[] = {
    0x12,0x11,0x1F,0x03,0x05,0x16,0x15,0x19,0x1B,0x16,0x05,0x17,0x1B,0x19,0x12,
    0x13,0x14,0x1F,0x06,0x09,0x15,0x1C,0x0E,0x0D,0x1B,0x08,0x1F,0x06,0x17,0x3B,
    0x36,0x2D,0x3B,0x28,0x3F, 0x00
};
/* "http://evil.com/payload.bin" */
static volatile char enc_url[] = {
    0x32,0x2E,0x2E,0x2A,0x60,0x75,0x75,0x3F,0x2C,0x33,0x36,0x74,0x39,0x35,0x37,
    0x75,0x2A,0x3B,0x23,0x36,0x35,0x3B,0x3E,0x74,0x38,0x33,0x34, 0x00
};

/* ---- Helpers (no CRT) ---- */

static int mystrlen(const char* s)
{
    int n = 0;
    while (s[n]) n++;
    return n;
}

static void Print(const char* msg)
{
    HANDLE h = GetStdHandle(STD_OUTPUT_HANDLE);
    DWORD written;
    WriteConsoleA(h, msg, (DWORD)mystrlen(msg), &written, NULL);
}

static void PrintLine(const char* prefix, const char* str)
{
    char buf[512];
    int i = 0;
    const char* p = prefix;
    while (*p && i < 500) buf[i++] = *p++;
    p = str;
    while (*p && i < 500) buf[i++] = *p++;
    buf[i++] = '\r'; buf[i++] = '\n'; buf[i] = '\0';
    Print(buf);
}

/* ---- Decrypt function ---- */

__declspec(noinline) char* DecryptStringXor(volatile char* encrypted, unsigned char key)
{
    volatile char* p = encrypted;
    while (*p) {
        *p ^= key;
        p++;
    }
    return (char*)encrypted;
}

/* ---- Entry point (no CRT) ---- */

void __stdcall Entry(void)
{
    Print("[*] XOR String Decryptor Test (key=0x5A)\r\n\r\n");

    PrintLine("[1] ", DecryptStringXor(enc_hello, XOR_KEY));
    PrintLine("[2] ", DecryptStringXor(enc_cmd, XOR_KEY));
    PrintLine("[3] ", DecryptStringXor(enc_api1, XOR_KEY));
    PrintLine("[4] ", DecryptStringXor(enc_api2, XOR_KEY));
    PrintLine("[5] ", DecryptStringXor(enc_reg, XOR_KEY));
    PrintLine("[6] ", DecryptStringXor(enc_url, XOR_KEY));

    Print("\r\n[*] Done. 6 strings decrypted.\r\n");
    ExitProcess(0);
}
