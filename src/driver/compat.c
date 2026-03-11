/*
 * KernelFlirt - Compatibility stubs
 * compat.c - Stubs for CRT symbols required by VS 2022 v143 toolset
 *            that are not exported by ntoskrnl on older Windows 10 builds.
 *
 * These symbols are referenced by compiler-generated code (/GS, CFG, XFG)
 * but may not exist in ntoskrnl.exe on builds < 22000.
 */

#include <ntddk.h>

/* CPU feature detection — called by CRT init, not needed in kernel driver */
void __cpu_features_init(void) { }

/* Optimized memset variants — fall back to standard memset */
void *__memset_repmovs(void *dest, int c, unsigned __int64 count)
{
    return memset(dest, c, (size_t)count);
}

/* memset query — returns 0 to use default memset path */
int __memset_query(void)
{
    return 0;
}

/* XFG (eXtended Flow Guard) dispatch — just call through, no XFG in our driver */
void _guard_xfg_dispatch_icall_nop(void) { }
