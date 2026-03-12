; chkstk_x86.asm - Stack probe for x86 (Win32) no-CRT builds
; The compiler generates calls to _chkstk when local variables exceed 4KB.
; This minimal implementation probes each page to commit stack memory.

.386
.MODEL FLAT
OPTION CASEMAP:NONE

_TEXT SEGMENT

PUBLIC __chkstk

__chkstk PROC
    ; eax = number of bytes to allocate on stack
    ; We need to touch each page so the guard page mechanism commits them.
    push    ecx
    lea     ecx, [esp + 8]         ; current stack pointer (past saved ecx + return addr)
    cmp     eax, 1000h
    jb      done
probe_loop:
    sub     ecx, 1000h
    test    DWORD PTR [ecx], eax   ; touch the page
    sub     eax, 1000h
    cmp     eax, 1000h
    ja      probe_loop
done:
    sub     ecx, eax
    test    DWORD PTR [ecx], eax   ; touch last page
    pop     ecx
    ret
__chkstk ENDP

_TEXT ENDS
END
