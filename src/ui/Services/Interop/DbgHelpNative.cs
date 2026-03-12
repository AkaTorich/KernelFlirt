using System.Runtime.InteropServices;

namespace KernelFlirt.UI.Services.Interop;

/// <summary>
/// P/Invoke wrappers for dbghelp.dll symbol resolution.
/// </summary>
internal static class DbgHelpNative
{
    private const string Dll = "dbghelp.dll";

    // SymSetOptions flags
    public const uint SYMOPT_DEFERRED_LOADS = 0x00000004;
    public const uint SYMOPT_UNDNAME        = 0x00000002;
    public const uint SYMOPT_LOAD_LINES     = 0x00000010;
    public const uint SYMOPT_DEBUG          = 0x80000000;
    public const uint SYMOPT_FAVOR_COMPRESSED = 0x00800000;

    [DllImport(Dll, SetLastError = true)]
    public static extern uint SymSetOptions(uint SymOptions);

    [DllImport(Dll, SetLastError = true)]
    public static extern uint SymGetOptions();

    [DllImport(Dll, SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SymInitializeW(
        IntPtr hProcess,
        [MarshalAs(UnmanagedType.LPWStr)] string? UserSearchPath,
        [MarshalAs(UnmanagedType.Bool)] bool fInvadeProcess);

    [DllImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SymCleanup(IntPtr hProcess);

    [DllImport(Dll, SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern ulong SymLoadModuleExW(
        IntPtr hProcess,
        IntPtr hFile,
        [MarshalAs(UnmanagedType.LPWStr)] string? ImageName,
        [MarshalAs(UnmanagedType.LPWStr)] string? ModuleName,
        ulong BaseOfDll,
        uint DllSize,
        IntPtr Data,  // MODLOAD_DATA*
        uint Flags);

    [DllImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SymUnloadModule64(IntPtr hProcess, ulong BaseOfDll);

    [DllImport(Dll, SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SymFromAddrW(
        IntPtr hProcess,
        ulong Address,
        out ulong Displacement,
        IntPtr Symbol); // SYMBOL_INFOW*

    [DllImport(Dll, SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SymSetSearchPathW(
        IntPtr hProcess,
        [MarshalAs(UnmanagedType.LPWStr)] string SearchPath);

    [DllImport(Dll, SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SymGetSearchPathW(
        IntPtr hProcess,
        IntPtr SearchPath,
        int SearchPathLength);

    // SymRegisterCallbackW64 for capturing debug output
    public const uint CBA_DEBUG_INFO = 0x10000000;

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    public delegate bool SymRegisterCallbackProc64(
        IntPtr hProcess,
        uint ActionCode,
        ulong CallbackData,
        ulong UserContext);

    [DllImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SymRegisterCallbackW64(
        IntPtr hProcess,
        SymRegisterCallbackProc64 CallbackFunction,
        ulong UserContext);

    // SYMBOL_INFOW structure layout (x64, default pack(8)):
    // Offset 0:  SizeOfStruct (ULONG, 4)
    // Offset 4:  TypeIndex (ULONG, 4)
    // Offset 8:  Reserved[2] (ULONG64 x2, 16)
    // Offset 24: Index (ULONG, 4)
    // Offset 28: Size (ULONG, 4)
    // Offset 32: ModBase (ULONG64, 8)
    // Offset 40: Flags (ULONG, 4)
    // [4 bytes padding for Value alignment]
    // Offset 48: Value (ULONG64, 8)
    // Offset 56: Address (ULONG64, 8)
    // Offset 64: Register (ULONG, 4)
    // Offset 68: Scope (ULONG, 4)
    // Offset 72: Tag (ULONG, 4)
    // Offset 76: NameLen (ULONG, 4)
    // Offset 80: MaxNameLen (ULONG, 4)
    // Offset 84: Name[1] (WCHAR, 2)
    // sizeof(SYMBOL_INFOW) = 88 (padded to 8-byte alignment)
    public const int SYMBOL_INFO_SIZE = 88; // sizeof(SYMBOL_INFOW)
    public const int SYMBOL_INFO_NAME_OFFSET = 84; // offset of Name field
    public const int MAX_SYM_NAME = 512;

    public static IntPtr AllocSymbolInfo()
    {
        int totalSize = SYMBOL_INFO_NAME_OFFSET + MAX_SYM_NAME * 2; // struct up to Name + name buffer
        var ptr = Marshal.AllocHGlobal(totalSize);
        unsafe
        {
            new Span<byte>((void*)ptr, totalSize).Clear();
        }
        Marshal.WriteInt32(ptr, 0, SYMBOL_INFO_SIZE);  // SizeOfStruct at offset 0
        Marshal.WriteInt32(ptr, 80, MAX_SYM_NAME);     // MaxNameLen at offset 80
        return ptr;
    }

    public static (string Name, ulong Address, uint Size) ReadSymbolInfo(IntPtr ptr)
    {
        uint size = (uint)Marshal.ReadInt32(ptr, 28);           // Size at offset 28
        ulong address = (ulong)Marshal.ReadInt64(ptr, 56);      // Address at offset 56
        int nameLen = Marshal.ReadInt32(ptr, 76);                // NameLen at offset 76
        string name = Marshal.PtrToStringUni(ptr + SYMBOL_INFO_NAME_OFFSET, nameLen) ?? "";
        return (name, address, size);
    }

    public static void FreeSymbolInfo(IntPtr ptr)
    {
        Marshal.FreeHGlobal(ptr);
    }

    [DllImport(Dll, SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SymFromNameW(
        IntPtr hProcess,
        [MarshalAs(UnmanagedType.LPWStr)] string Name,
        IntPtr Symbol); // SYMBOL_INFOW*

    // SymFindFileInPathW — searches symbol path (including symbol servers) for a PDB
    public const uint SSRVOPT_GUIDPTR = 0x0008;

    [DllImport(Dll, SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SymFindFileInPathW(
        IntPtr hProcess,
        [MarshalAs(UnmanagedType.LPWStr)] string? SearchPath,
        [MarshalAs(UnmanagedType.LPWStr)] string FileName,
        IntPtr id,       // pointer to GUID
        uint two,        // age
        uint three,      // unused, pass 0
        uint flags,      // SSRVOPT_GUIDPTR
        IntPtr FoundFile, // WCHAR[MAX_PATH] buffer
        IntPtr callback,  // PFINDFILEINPATHCALLBACKW, can be null
        IntPtr context);
}
