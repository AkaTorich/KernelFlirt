/* nocrt.c - CRT intrinsic replacements for no-CRT build */

/* The compiler may generate calls to memset/memcpy/memcmp for struct
   operations even with /NODEFAULTLIB. We provide minimal implementations. */

#include <stddef.h>  /* size_t */

int _fltused = 0;  /* Required when using floating-point (double) without CRT */

/* x86 no-CRT: conversion intrinsics not available without CRT.
   These must use __declspec(naked) + inline asm to avoid the compiler
   generating recursive calls to the very functions we're implementing. */
#if defined(_M_IX86)

double __cdecl _ultod3(unsigned __int64 val) {
    return (double)val;
}

__declspec(naked) long __cdecl _dtol3(double val) {
    __asm {
        fld     QWORD PTR [esp+4]
        push    eax
        fistp   DWORD PTR [esp]
        pop     eax
        ret
    }
}

__declspec(naked) unsigned long __cdecl _dtoui3(double val) {
    __asm {
        fld     QWORD PTR [esp+4]
        push    eax
        fistp   DWORD PTR [esp]
        pop     eax
        ret
    }
}

__declspec(naked) unsigned __int64 __cdecl _dtoul3(double val) {
    __asm {
        fld     QWORD PTR [esp+4]
        sub     esp, 8
        fistp   QWORD PTR [esp]
        pop     eax
        pop     edx
        ret
    }
}

#endif


#pragma function(memset)
void * __cdecl memset(void *dst, int val, size_t count) {
    unsigned char *p = (unsigned char *)dst;
    while (count--) *p++ = (unsigned char)val;
    return dst;
}

#pragma function(memcpy)
void * __cdecl memcpy(void *dst, const void *src, size_t count) {
    unsigned char *d = (unsigned char *)dst;
    const unsigned char *s = (const unsigned char *)src;
    while (count--) *d++ = *s++;
    return dst;
}

#pragma function(memcmp)
int __cdecl memcmp(const void *s1, const void *s2, size_t count) {
    const unsigned char *p1 = (const unsigned char *)s1;
    const unsigned char *p2 = (const unsigned char *)s2;
    while (count--) {
        if (*p1 != *p2) return (int)*p1 - (int)*p2;
        p1++; p2++;
    }
    return 0;
}
