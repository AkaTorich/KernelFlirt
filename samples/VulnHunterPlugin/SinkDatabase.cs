namespace VulnHunterPlugin;

/// <summary>
/// Database of dangerous sink functions grouped by risk level.
/// DestParam/SrcParam/SizeParam are x64 calling convention arg indices
/// (0=RCX, 1=RDX, 2=R8, 3=R9). SizeParam=-1 means unbounded copy.
/// </summary>
public static class SinkDatabase
{
    // Module name variants to try when resolving functions
    public static readonly string[][] ModuleVariants =
    [
        ["msvcrt", "ucrtbase", "vcruntime140", "api-ms-win-crt-string-l1-1-0"],
        ["ntdll"],
        ["kernel32", "kernelbase"],
        ["user32"],
    ];

    public static readonly SinkDef[] Sinks =
    [
        // ══════════════════════════════════════════════════════════════
        //  CRITICAL — unbounded copies, no size parameter
        // ══════════════════════════════════════════════════════════════

        // strcpy(dest, src)
        new() { Module = "msvcrt",     Function = "strcpy",     Danger = DangerLevel.Critical, DestParam = 0, SrcParam = 1, SizeParam = -1, Description = "Unbounded string copy" },
        new() { Module = "ucrtbase",   Function = "strcpy",     Danger = DangerLevel.Critical, DestParam = 0, SrcParam = 1, SizeParam = -1, Description = "Unbounded string copy" },
        new() { Module = "msvcrt",     Function = "wcscpy",     Danger = DangerLevel.Critical, DestParam = 0, SrcParam = 1, SizeParam = -1, Description = "Unbounded wide string copy" },
        new() { Module = "ucrtbase",   Function = "wcscpy",     Danger = DangerLevel.Critical, DestParam = 0, SrcParam = 1, SizeParam = -1, Description = "Unbounded wide string copy" },

        // strcat(dest, src)
        new() { Module = "msvcrt",     Function = "strcat",     Danger = DangerLevel.Critical, DestParam = 0, SrcParam = 1, SizeParam = -1, Description = "Unbounded string concat" },
        new() { Module = "ucrtbase",   Function = "strcat",     Danger = DangerLevel.Critical, DestParam = 0, SrcParam = 1, SizeParam = -1, Description = "Unbounded string concat" },
        new() { Module = "msvcrt",     Function = "wcscat",     Danger = DangerLevel.Critical, DestParam = 0, SrcParam = 1, SizeParam = -1, Description = "Unbounded wide string concat" },
        new() { Module = "ucrtbase",   Function = "wcscat",     Danger = DangerLevel.Critical, DestParam = 0, SrcParam = 1, SizeParam = -1, Description = "Unbounded wide string concat" },

        // sprintf(dest, fmt, ...)
        new() { Module = "msvcrt",     Function = "sprintf",    Danger = DangerLevel.Critical, DestParam = 0, SrcParam = 1, SizeParam = -1, Description = "Unbounded formatted print to buffer" },
        new() { Module = "ucrtbase",   Function = "sprintf",    Danger = DangerLevel.Critical, DestParam = 0, SrcParam = 1, SizeParam = -1, Description = "Unbounded formatted print to buffer" },
        new() { Module = "msvcrt",     Function = "swprintf",   Danger = DangerLevel.Critical, DestParam = 0, SrcParam = 1, SizeParam = -1, Description = "Unbounded wide formatted print" },

        // gets(buf) — no size at all
        new() { Module = "msvcrt",     Function = "gets",       Danger = DangerLevel.Critical, DestParam = 0, SrcParam = -1, SizeParam = -1, Description = "Reads line with no size limit" },
        new() { Module = "ucrtbase",   Function = "gets",       Danger = DangerLevel.Critical, DestParam = 0, SrcParam = -1, SizeParam = -1, Description = "Reads line with no size limit" },

        // Win32 lstr* — unbounded
        new() { Module = "kernel32",   Function = "lstrcpyA",   Danger = DangerLevel.Critical, DestParam = 0, SrcParam = 1, SizeParam = -1, Description = "Unbounded ANSI string copy" },
        new() { Module = "kernel32",   Function = "lstrcpyW",   Danger = DangerLevel.Critical, DestParam = 0, SrcParam = 1, SizeParam = -1, Description = "Unbounded Unicode string copy" },
        new() { Module = "kernel32",   Function = "lstrcatA",   Danger = DangerLevel.Critical, DestParam = 0, SrcParam = 1, SizeParam = -1, Description = "Unbounded ANSI string concat" },
        new() { Module = "kernel32",   Function = "lstrcatW",   Danger = DangerLevel.Critical, DestParam = 0, SrcParam = 1, SizeParam = -1, Description = "Unbounded Unicode string concat" },
        new() { Module = "user32",     Function = "wsprintfA",  Danger = DangerLevel.Critical, DestParam = 0, SrcParam = 1, SizeParam = -1, Description = "Unbounded formatted print" },
        new() { Module = "user32",     Function = "wsprintfW",  Danger = DangerLevel.Critical, DestParam = 0, SrcParam = 1, SizeParam = -1, Description = "Unbounded formatted print" },

        // ntdll Rtl* — unbounded
        new() { Module = "ntdll",      Function = "RtlCopyString", Danger = DangerLevel.Critical, DestParam = 0, SrcParam = 1, SizeParam = -1, Description = "Unbounded ANSI_STRING copy" },

        // ══════════════════════════════════════════════════════════════
        //  HIGH — size-bounded but commonly misused
        // ══════════════════════════════════════════════════════════════

        // memcpy(dest, src, size)
        new() { Module = "msvcrt",     Function = "memcpy",     Danger = DangerLevel.High, DestParam = 0, SrcParam = 1, SizeParam = 2, Description = "Memory copy with user-controlled size" },
        new() { Module = "ucrtbase",   Function = "memcpy",     Danger = DangerLevel.High, DestParam = 0, SrcParam = 1, SizeParam = 2, Description = "Memory copy with user-controlled size" },
        new() { Module = "ntdll",      Function = "memcpy",     Danger = DangerLevel.High, DestParam = 0, SrcParam = 1, SizeParam = 2, Description = "Memory copy with user-controlled size" },
        new() { Module = "msvcrt",     Function = "memmove",    Danger = DangerLevel.High, DestParam = 0, SrcParam = 1, SizeParam = 2, Description = "Memory move with user-controlled size" },
        new() { Module = "ucrtbase",   Function = "memmove",    Danger = DangerLevel.High, DestParam = 0, SrcParam = 1, SizeParam = 2, Description = "Memory move with user-controlled size" },

        // strncpy(dest, src, count)
        new() { Module = "msvcrt",     Function = "strncpy",    Danger = DangerLevel.High, DestParam = 0, SrcParam = 1, SizeParam = 2, Description = "Bounded copy — check size vs buffer" },
        new() { Module = "ucrtbase",   Function = "strncpy",    Danger = DangerLevel.High, DestParam = 0, SrcParam = 1, SizeParam = 2, Description = "Bounded copy — check size vs buffer" },
        new() { Module = "msvcrt",     Function = "wcsncpy",    Danger = DangerLevel.High, DestParam = 0, SrcParam = 1, SizeParam = 2, Description = "Bounded wide copy — check size vs buffer" },

        // snprintf(dest, size, fmt, ...)
        new() { Module = "msvcrt",     Function = "_snprintf",  Danger = DangerLevel.High, DestParam = 0, SrcParam = 2, SizeParam = 1, Description = "Bounded sprintf — check size" },
        new() { Module = "ucrtbase",   Function = "snprintf",   Danger = DangerLevel.High, DestParam = 0, SrcParam = 2, SizeParam = 1, Description = "Bounded sprintf — check size" },
        new() { Module = "ucrtbase",   Function = "_snprintf",  Danger = DangerLevel.High, DestParam = 0, SrcParam = 2, SizeParam = 1, Description = "Bounded sprintf — check size" },

        // Win32 bounded variants
        new() { Module = "kernel32",   Function = "lstrcpynA",  Danger = DangerLevel.High, DestParam = 0, SrcParam = 1, SizeParam = 2, Description = "Bounded ANSI copy — check size" },
        new() { Module = "kernel32",   Function = "lstrcpynW",  Danger = DangerLevel.High, DestParam = 0, SrcParam = 1, SizeParam = 2, Description = "Bounded Unicode copy — check size" },

        // RtlCopyMemory / RtlMoveMemory
        new() { Module = "ntdll",      Function = "RtlCopyMemory",  Danger = DangerLevel.High, DestParam = 0, SrcParam = 1, SizeParam = 2, Description = "Kernel memory copy" },
        new() { Module = "ntdll",      Function = "RtlMoveMemory",  Danger = DangerLevel.High, DestParam = 0, SrcParam = 1, SizeParam = 2, Description = "Kernel memory move" },

        // ══════════════════════════════════════════════════════════════
        //  MEDIUM — format string sinks, scanf family
        // ══════════════════════════════════════════════════════════════

        new() { Module = "msvcrt",     Function = "scanf",      Danger = DangerLevel.Medium, DestParam = -1, SrcParam = 0, SizeParam = -1, Description = "Format string input — check %s width" },
        new() { Module = "ucrtbase",   Function = "scanf",      Danger = DangerLevel.Medium, DestParam = -1, SrcParam = 0, SizeParam = -1, Description = "Format string input — check %s width" },
        new() { Module = "msvcrt",     Function = "sscanf",     Danger = DangerLevel.Medium, DestParam = -1, SrcParam = 0, SizeParam = -1, Description = "String scanf — check %s width" },
        new() { Module = "ucrtbase",   Function = "sscanf",     Danger = DangerLevel.Medium, DestParam = -1, SrcParam = 0, SizeParam = -1, Description = "String scanf — check %s width" },
        new() { Module = "msvcrt",     Function = "fscanf",     Danger = DangerLevel.Medium, DestParam = -1, SrcParam = 0, SizeParam = -1, Description = "File scanf — check %s width" },

        // printf with user-controlled format
        new() { Module = "msvcrt",     Function = "printf",     Danger = DangerLevel.Medium, DestParam = -1, SrcParam = 0, SizeParam = -1, Description = "Format string — user-controlled fmt?" },
        new() { Module = "ucrtbase",   Function = "printf",     Danger = DangerLevel.Medium, DestParam = -1, SrcParam = 0, SizeParam = -1, Description = "Format string — user-controlled fmt?" },
    ];
}
