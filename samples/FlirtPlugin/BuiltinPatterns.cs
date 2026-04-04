namespace FlirtPlugin;

/// <summary>
/// Embedded fallback FLIRT signatures for common MSVC 14.x (VS 2019/2022) x64 CRT functions.
/// Used when no .pat files are found in the plugin directory.
///
/// Patterns extracted from typical MSVC 14.29-14.39 release x64 builds.
/// Wildcards (..) mark relocatable bytes (RIP-relative offsets, absolute addresses).
/// </summary>
public static class BuiltinPatterns
{
    public static List<PatSignature> GetAll()
    {
        var list = new List<PatSignature>();

        // ── CRT Startup ─────────────────────────────────────────────────────
        Add(list, "__security_init_cookie",
            "48 89 5C 24 .. 48 89 6C 24 .. 48 89 74 24 .. 57 48 83 EC 20 65 48 8B 04 25 30 00 00 00");
        Add(list, "__security_check_cookie",
            "48 3B 0D .. .. .. .. 75 .. C3");
        Add(list, "__GSHandlerCheck",
            "48 89 5C 24 .. 48 89 6C 24 .. 48 89 74 24 .. 57 41 54 41 55 41 56 41 57 48 83 EC 20");
        Add(list, "_mainCRTStartup",
            "48 83 EC 28 E8 .. .. .. .. 48 83 C4 28 E9");
        Add(list, "mainCRTStartup",
            "48 83 EC 28 E8 .. .. .. .. 48 83 C4 28 E9");
        Add(list, "wmainCRTStartup",
            "48 83 EC 28 E8 .. .. .. .. 48 83 C4 28 E9");
        Add(list, "__scrt_common_main_seh",
            "48 89 5C 24 .. 55 56 57 41 54 41 55 41 56 41 57 48 8D AC 24");
        Add(list, "_initterm",
            "48 89 5C 24 .. 48 89 74 24 .. 57 48 83 EC 20 48 8B F1 48 8B FA 48 3B FA");
        Add(list, "_initterm_e",
            "48 89 5C 24 .. 48 89 6C 24 .. 48 89 74 24 .. 57 48 83 EC 20 33 ED 48 8B F1 48 8B FA");

        // ── Memory ──────────────────────────────────────────────────────────
        Add(list, "malloc",
            "48 83 EC 28 48 8B C1 48 85 C9 75 .. B9 01 00 00 00 48 8B C1");
        Add(list, "free",
            "48 85 C9 74 .. 48 83 EC 28 E8");
        Add(list, "calloc",
            "48 89 5C 24 .. 48 89 74 24 .. 57 48 83 EC 20 48 8B F2 48 8B F9 48 0F AF F1");
        Add(list, "realloc",
            "48 89 5C 24 .. 48 89 74 24 .. 57 48 83 EC 20 48 8B F2 48 8B D9 48 85 D2");
        Add(list, "_aligned_malloc",
            "48 89 5C 24 .. 57 48 83 EC 20 48 8B DA 48 8B F9 48 8D 4A FF");
        Add(list, "operator_new",
            "48 83 EC 28 48 8B C1 48 85 C9 75 .. B9 01 00 00 00 48 8B C1 E8");
        Add(list, "operator_delete",
            "48 85 C9 74 .. 48 83 EC 28 E8 .. .. .. .. 48 83 C4 28 C3");

        // ── String ──────────────────────────────────────────────────────────
        Add(list, "strlen",
            "48 8B C1 48 F7 D0 49 89 D0 0F 10 01");
        Add(list, "strcmp",
            "48 89 5C 24 .. 57 48 83 EC 20 48 8B FA 48 8B D9 E8 .. .. .. .. 44 0F B6 03");
        Add(list, "strncmp",
            "4C 8B D9 4D 85 C0 74 .. 0F 1F 80 00 00 00 00 41 0F B6 01");
        Add(list, "strcpy_s",
            "48 89 5C 24 .. 48 89 6C 24 .. 48 89 74 24 .. 57 48 83 EC 20 49 8B F0 48 8B EA");
        Add(list, "strcat_s",
            "48 89 5C 24 .. 48 89 6C 24 .. 48 89 74 24 .. 57 48 83 EC 20 49 8B F0 48 8B EA 48 8B D9");
        Add(list, "wcslen",
            "48 8B C1 66 0F 1F 44 00 00 66 83 38 00 48 8D 40 02 75");
        Add(list, "wcscpy_s",
            "48 89 5C 24 .. 48 89 6C 24 .. 48 89 74 24 .. 57 48 83 EC 20 49 8B F0 48 8B EA 48 8B D9");
        Add(list, "memcpy",
            "48 8B C1 4C 8B D1 4C 8B D9 49 83 F8 10 72");
        Add(list, "memmove",
            "48 89 74 24 .. 57 48 83 EC 20 49 8B F0 48 8B FA 48 8B D9 48 3B CF");
        Add(list, "memset",
            "48 8B C1 0F B6 D2 49 B8 01 01 01 01 01 01 01 01 4C 0F AF C2 49 83 F8 10");
        Add(list, "memcmp",
            "48 89 5C 24 .. 48 89 74 24 .. 57 48 83 EC 20 4C 8B C1 33 C0 4D 85 C0");

        // ── I/O ─────────────────────────────────────────────────────────────
        Add(list, "printf",
            "48 89 54 24 .. 4C 89 44 24 .. 4C 89 4C 24 .. 48 83 EC 38 48 8D 44 24 .. 48 89 44 24");
        Add(list, "puts",
            "48 83 EC 28 48 8B 0D .. .. .. .. E8");
        Add(list, "sprintf_s",
            "48 89 4C 24 .. 48 89 54 24 .. 4C 89 44 24 .. 4C 89 4C 24 .. 48 83 EC 38");
        Add(list, "fprintf",
            "48 89 54 24 .. 4C 89 44 24 .. 4C 89 4C 24 .. 48 83 EC 38");
        Add(list, "__acrt_iob_func",
            "48 83 EC 28 83 F9 03 73");
        Add(list, "_vfprintf_l",
            "48 89 5C 24 .. 48 89 6C 24 .. 48 89 74 24 .. 57 48 83 EC 30");

        // ── Exception Handling ──────────────────────────────────────────────
        Add(list, "_CxxThrowException",
            "48 89 5C 24 .. 48 89 74 24 .. 57 48 83 EC 30 48 8B F2 48 8B D9 33 D2");
        Add(list, "__CxxFrameHandler3",
            "48 89 5C 24 .. 48 89 6C 24 .. 48 89 74 24 .. 57 41 56 41 57 48 83 EC 30 33 ED");
        Add(list, "__CxxFrameHandler4",
            "48 89 5C 24 .. 48 89 6C 24 .. 48 89 74 24 .. 57 41 56 41 57 48 83 EC 30 49 8B F9");
        Add(list, "__std_exception_copy",
            "48 89 5C 24 .. 48 89 74 24 .. 57 48 83 EC 20 48 8B 31 48 8B FA 48 85 F6");
        Add(list, "__std_exception_destroy",
            "48 89 5C 24 .. 57 48 83 EC 20 48 8B F9 48 8B 19 48 85 DB 74");

        // ── RTC (Run-Time Checks) ───────────────────────────────────────────
        Add(list, "_RTC_CheckStackVars",
            "48 89 5C 24 .. 48 89 6C 24 .. 48 89 74 24 .. 57 48 83 EC 20 8B EA 48 8B F9 85 D2");
        Add(list, "_RTC_Initialize",
            "48 89 5C 24 .. 57 48 83 EC 20 48 8B 3D");

        // ── Math ────────────────────────────────────────────────────────────
        Add(list, "abs",
            "8B C1 99 33 C2 2B C2 C3");
        Add(list, "_abs64",
            "48 8B C1 48 99 48 33 C2 48 2B C2 C3");

        // ── Process/Thread ──────────────────────────────────────────────────
        Add(list, "exit",
            "48 83 EC 28 8B D1 B9 .. 00 00 00 E8");
        Add(list, "_exit",
            "48 83 EC 28 8B D1 B9 .. 00 00 00 E8");
        Add(list, "atexit",
            "48 83 EC 28 48 8D 15 .. .. .. .. E8");
        Add(list, "_cexit",
            "48 83 EC 28 E8 .. .. .. .. 48 83 C4 28 C3");

        // ── Heap ────────────────────────────────────────────────────────────
        Add(list, "_callnewh",
            "48 89 5C 24 .. 48 89 74 24 .. 57 48 83 EC 20 48 8B F9 33 F6 48 8D 0D");
        Add(list, "_errno",
            "48 83 EC 28 E8 .. .. .. .. 48 8D 48 .. C3");
        Add(list, "strerror",
            "48 83 EC 28 E8 .. .. .. .. 8B 48 .. E8");

        return list;
    }

    private static void Add(List<PatSignature> list, string name, string hexPattern)
    {
        // Convert space-separated hex to IDA-style packed hex (for CompilePattern compatibility)
        var packed = hexPattern.Replace(" ", "");
        var pattern = CompileHex(packed);
        if (pattern.Length >= 4)
            list.Add(new PatSignature { Name = name, LeadingPattern = pattern });
    }

    private static short[] CompileHex(string hex)
    {
        if (hex.Length % 2 != 0) return [];
        var result = new short[hex.Length / 2];
        for (int i = 0; i < hex.Length; i += 2)
        {
            if (hex[i] == '.' && hex[i + 1] == '.')
                result[i / 2] = -1;
            else if (byte.TryParse(hex.AsSpan(i, 2), System.Globalization.NumberStyles.HexNumber, null, out byte b))
                result[i / 2] = b;
            else
                result[i / 2] = -1;
        }
        return result;
    }
}
