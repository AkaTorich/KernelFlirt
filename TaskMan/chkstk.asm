; chkstk.asm - Stack probe for x64 no-CRT builds
; The compiler generates calls to __chkstk when local variables exceed 4KB.
; This minimal implementation just probes each page to commit stack memory.

_TEXT SEGMENT

PUBLIC __chkstk

__chkstk PROC
    ; rax = number of bytes to allocate on stack
    ; We need to touch each page so the guard page mechanism commits them.
    push    rcx
    push    rax
    cmp     rax, 1000h
    lea     rcx, [rsp + 18h]        ; current stack pointer (past saved rcx, rax, return addr)
    jb      done
probe_loop:
    sub     rcx, 1000h
    test    DWORD PTR [rcx], eax    ; touch the page
    sub     rax, 1000h
    cmp     rax, 1000h
    ja      probe_loop
done:
    sub     rcx, rax
    test    DWORD PTR [rcx], eax    ; touch last page
    pop     rax
    pop     rcx
    ret
__chkstk ENDP

_TEXT ENDS
END
