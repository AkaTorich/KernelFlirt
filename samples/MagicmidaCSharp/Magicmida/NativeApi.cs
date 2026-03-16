using System.Runtime.InteropServices;

namespace Magicmida;

/// <summary>
/// P/Invoke declarations for Windows API, NT API, and related structures.
/// </summary>
public static class NativeApi
{
    // ==================== Constants ====================

    public const uint INFINITE = 0xFFFFFFFF;
    public const uint DBG_CONTINUE = 0x00010002;
    public const uint DBG_EXCEPTION_NOT_HANDLED = 0x80010001;
    public const uint DBG_CONTROL_BREAK = 0x40010008;

    public const uint EXCEPTION_DEBUG_EVENT = 1;
    public const uint CREATE_THREAD_DEBUG_EVENT = 2;
    public const uint CREATE_PROCESS_DEBUG_EVENT = 3;
    public const uint EXIT_THREAD_DEBUG_EVENT = 4;
    public const uint EXIT_PROCESS_DEBUG_EVENT = 5;
    public const uint LOAD_DLL_DEBUG_EVENT = 6;
    public const uint UNLOAD_DLL_DEBUG_EVENT = 7;
    public const uint OUTPUT_DEBUG_STRING_EVENT = 8;
    public const uint RIP_EVENT = 9;

    public const uint EXCEPTION_ACCESS_VIOLATION = 0xC0000005;
    public const uint EXCEPTION_BREAKPOINT = 0x80000003;
    public const uint EXCEPTION_SINGLE_STEP = 0x80000004;
    public const uint EXCEPTION_DATATYPE_MISALIGNMENT = 0x80000002;

    public const uint CONTEXT_i386 = 0x00010000;
    public const uint CONTEXT_AMD64 = 0x00100000;

#if CPUX86
    public const uint CONTEXT_CONTROL = CONTEXT_i386 | 0x01;
    public const uint CONTEXT_INTEGER = CONTEXT_i386 | 0x02;
    public const uint CONTEXT_DEBUG_REGISTERS = CONTEXT_i386 | 0x10;
    public const uint CONTEXT_FULL = CONTEXT_CONTROL | CONTEXT_INTEGER | 0x04; // SEGMENTS
#else
    public const uint CONTEXT_CONTROL = CONTEXT_AMD64 | 0x01;
    public const uint CONTEXT_INTEGER = CONTEXT_AMD64 | 0x02;
    public const uint CONTEXT_DEBUG_REGISTERS = CONTEXT_AMD64 | 0x10;
    public const uint CONTEXT_FULL = CONTEXT_CONTROL | CONTEXT_INTEGER | 0x04;
#endif

    public const uint PROCESS_ALL_ACCESS = 0x001FFFFF;
    public const uint CREATE_DEFAULT_ERROR_MODE = 0x04000000;
    public const uint CREATE_NEW_CONSOLE = 0x00000010;
    public const uint NORMAL_PRIORITY_CLASS = 0x00000020;
    public const uint DEBUG_PROCESS = 0x00000001;
    public const uint DEBUG_ONLY_THIS_PROCESS = 0x00000002;

    public const uint STARTF_USESHOWWINDOW = 0x01;
    public const ushort SW_SHOW = 5;
    public const ushort SW_HIDE = 0;

    public const uint PAGE_NOACCESS = 0x01;
    public const uint PAGE_READONLY = 0x02;
    public const uint PAGE_READWRITE = 0x04;
    public const uint PAGE_EXECUTE_READWRITE = 0x40;
    public const uint MEM_COMMIT = 0x1000;
    public const uint MEM_RESERVE = 0x2000;

    public const uint FILE_SHARE_READ = 0x01;
    public const uint GENERIC_READ = 0x80000000;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    public const uint FILE_BEGIN = 0;

    public const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

    public const uint TH32CS_SNAPMODULE = 0x08;

    public const uint IMAGE_FILE_DLL = 0x2000;
    public const ushort IMAGE_FILE_MACHINE_I386 = 0x14C;
    public const ushort IMAGE_FILE_MACHINE_AMD64 = 0x8664;
    public const uint IMAGE_NT_SIGNATURE = 0x00004550;

    public const int IMAGE_DIRECTORY_ENTRY_EXPORT = 0;
    public const int IMAGE_DIRECTORY_ENTRY_IMPORT = 1;
    public const int IMAGE_DIRECTORY_ENTRY_IAT = 12;
    public const int IMAGE_DIRECTORY_ENTRY_TLS = 9;

    public const uint IMAGE_SCN_MEM_READ = 0x40000000;
    public const uint IMAGE_SCN_MEM_WRITE = 0x80000000;
    public const uint IMAGE_SCN_MEM_EXECUTE = 0x20000000;
    public const uint IMAGE_SCN_CNT_INITIALIZED_DATA = 0x00000040;

    public const uint IMAGE_ORDINAL_FLAG32 = 0x80000000;
    public const ulong IMAGE_ORDINAL_FLAG64 = 0x8000000000000000;
#if CPUX86
    public const nuint IMAGE_ORDINAL_FLAG = (nuint)IMAGE_ORDINAL_FLAG32;
#else
    public static readonly nuint IMAGE_ORDINAL_FLAG = unchecked((nuint)IMAGE_ORDINAL_FLAG64);
#endif

    public const int STATUS_SUCCESS = 0;
    public const int STATUS_PORT_NOT_SET = unchecked((int)0xC0000353);

    // ==================== Structures ====================

    [StructLayout(LayoutKind.Sequential)]
    public struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX, dwY, dwXSize, dwYSize;
        public int dwXCountChars, dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_BASIC_INFORMATION
    {
        public uint ExitStatus;
        public IntPtr PebBaseAddress;
        public nuint AffinityMask;
        public uint BasePriority;
        public nuint UniqueProcessId;
        public nuint InheritedFromUniqueProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EXCEPTION_RECORD
    {
        public uint ExceptionCode;
        public uint ExceptionFlags;
        public IntPtr ExceptionRecord;
        public IntPtr ExceptionAddress;
        public uint NumberParameters;
        // EXCEPTION_MAXIMUM_PARAMETERS = 15
        public nuint ExceptionInformation0;
        public nuint ExceptionInformation1;
        public nuint ExceptionInformation2;
        public nuint ExceptionInformation3;
        public nuint ExceptionInformation4;
        public nuint ExceptionInformation5;
        public nuint ExceptionInformation6;
        public nuint ExceptionInformation7;
        public nuint ExceptionInformation8;
        public nuint ExceptionInformation9;
        public nuint ExceptionInformation10;
        public nuint ExceptionInformation11;
        public nuint ExceptionInformation12;
        public nuint ExceptionInformation13;
        public nuint ExceptionInformation14;

        public nuint GetExceptionInformation(int index)
        {
            return index switch
            {
                0 => ExceptionInformation0,
                1 => ExceptionInformation1,
                _ => 0,
            };
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EXCEPTION_DEBUG_INFO
    {
        public EXCEPTION_RECORD ExceptionRecord;
        public uint dwFirstChance;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CREATE_THREAD_DEBUG_INFO
    {
        public IntPtr hThread;
        public IntPtr lpThreadLocalBase;
        public IntPtr lpStartAddress;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CREATE_PROCESS_DEBUG_INFO
    {
        public IntPtr hFile;
        public IntPtr hProcess;
        public IntPtr hThread;
        public IntPtr lpBaseOfImage;
        public uint dwDebugInfoFileOffset;
        public uint nDebugInfoSize;
        public IntPtr lpThreadLocalBase;
        public IntPtr lpStartAddress;
        public IntPtr lpImageName;
        public ushort fUnicode;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EXIT_THREAD_DEBUG_INFO
    {
        public uint dwExitCode;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EXIT_PROCESS_DEBUG_INFO
    {
        public uint dwExitCode;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LOAD_DLL_DEBUG_INFO
    {
        public IntPtr hFile;
        public IntPtr lpBaseOfDll;
        public uint dwDebugInfoFileOffset;
        public uint nDebugInfoSize;
        public IntPtr lpImageName;
        public ushort fUnicode;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct OUTPUT_DEBUG_STRING_INFO
    {
        public IntPtr lpDebugStringData;
        public ushort fUnicode;
        public ushort nDebugStringLength;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct DEBUG_EVENT
    {
        [FieldOffset(0)] public uint dwDebugEventCode;
        [FieldOffset(4)] public uint dwProcessId;
        [FieldOffset(8)] public uint dwThreadId;

#if CPUX86
        [FieldOffset(12)] public EXCEPTION_DEBUG_INFO Exception;
        [FieldOffset(12)] public CREATE_THREAD_DEBUG_INFO CreateThread;
        [FieldOffset(12)] public CREATE_PROCESS_DEBUG_INFO CreateProcessInfo;
        [FieldOffset(12)] public EXIT_THREAD_DEBUG_INFO ExitThread;
        [FieldOffset(12)] public EXIT_PROCESS_DEBUG_INFO ExitProcess;
        [FieldOffset(12)] public LOAD_DLL_DEBUG_INFO LoadDll;
        [FieldOffset(12)] public OUTPUT_DEBUG_STRING_INFO DebugString;
#else
        [FieldOffset(16)] public EXCEPTION_DEBUG_INFO Exception;
        [FieldOffset(16)] public CREATE_THREAD_DEBUG_INFO CreateThread;
        [FieldOffset(16)] public CREATE_PROCESS_DEBUG_INFO CreateProcessInfo;
        [FieldOffset(16)] public EXIT_THREAD_DEBUG_INFO ExitThread;
        [FieldOffset(16)] public EXIT_PROCESS_DEBUG_INFO ExitProcess;
        [FieldOffset(16)] public LOAD_DLL_DEBUG_INFO LoadDll;
        [FieldOffset(16)] public OUTPUT_DEBUG_STRING_INFO DebugString;
#endif
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct IMAGE_DOS_HEADER
    {
        public ushort e_magic;
        public ushort e_cblp;
        public ushort e_cp;
        public ushort e_crlc;
        public ushort e_cparhdr;
        public ushort e_minalloc;
        public ushort e_maxalloc;
        public ushort e_ss;
        public ushort e_sp;
        public ushort e_csum;
        public ushort e_ip;
        public ushort e_cs;
        public ushort e_lfarlc;
        public ushort e_ovno;
        public ulong e_res;
        public ushort e_oemid;
        public ushort e_oeminfo;
        public unsafe fixed ushort e_res2[10];
        public int e_lfanew;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct IMAGE_FILE_HEADER
    {
        public ushort Machine;
        public ushort NumberOfSections;
        public uint TimeDateStamp;
        public uint PointerToSymbolTable;
        public uint NumberOfSymbols;
        public ushort SizeOfOptionalHeader;
        public ushort Characteristics;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct IMAGE_DATA_DIRECTORY
    {
        public uint VirtualAddress;
        public uint Size;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct IMAGE_OPTIONAL_HEADER32
    {
        public ushort Magic;
        public byte MajorLinkerVersion;
        public byte MinorLinkerVersion;
        public uint SizeOfCode;
        public uint SizeOfInitializedData;
        public uint SizeOfUninitializedData;
        public uint AddressOfEntryPoint;
        public uint BaseOfCode;
        public uint BaseOfData;
        public uint ImageBase;
        public uint SectionAlignment;
        public uint FileAlignment;
        public ushort MajorOperatingSystemVersion;
        public ushort MinorOperatingSystemVersion;
        public ushort MajorImageVersion;
        public ushort MinorImageVersion;
        public ushort MajorSubsystemVersion;
        public ushort MinorSubsystemVersion;
        public uint Win32VersionValue;
        public uint SizeOfImage;
        public uint SizeOfHeaders;
        public uint CheckSum;
        public ushort Subsystem;
        public ushort DllCharacteristics;
        public uint SizeOfStackReserve;
        public uint SizeOfStackCommit;
        public uint SizeOfHeapReserve;
        public uint SizeOfHeapCommit;
        public uint LoaderFlags;
        public uint NumberOfRvaAndSizes;
        // 16 data directories inline
        public IMAGE_DATA_DIRECTORY DataDirectory0;
        public IMAGE_DATA_DIRECTORY DataDirectory1;
        public IMAGE_DATA_DIRECTORY DataDirectory2;
        public IMAGE_DATA_DIRECTORY DataDirectory3;
        public IMAGE_DATA_DIRECTORY DataDirectory4;
        public IMAGE_DATA_DIRECTORY DataDirectory5;
        public IMAGE_DATA_DIRECTORY DataDirectory6;
        public IMAGE_DATA_DIRECTORY DataDirectory7;
        public IMAGE_DATA_DIRECTORY DataDirectory8;
        public IMAGE_DATA_DIRECTORY DataDirectory9;
        public IMAGE_DATA_DIRECTORY DataDirectory10;
        public IMAGE_DATA_DIRECTORY DataDirectory11;
        public IMAGE_DATA_DIRECTORY DataDirectory12;
        public IMAGE_DATA_DIRECTORY DataDirectory13;
        public IMAGE_DATA_DIRECTORY DataDirectory14;
        public IMAGE_DATA_DIRECTORY DataDirectory15;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct IMAGE_OPTIONAL_HEADER64
    {
        public ushort Magic;
        public byte MajorLinkerVersion;
        public byte MinorLinkerVersion;
        public uint SizeOfCode;
        public uint SizeOfInitializedData;
        public uint SizeOfUninitializedData;
        public uint AddressOfEntryPoint;
        public uint BaseOfCode;
        public ulong ImageBase;
        public uint SectionAlignment;
        public uint FileAlignment;
        public ushort MajorOperatingSystemVersion;
        public ushort MinorOperatingSystemVersion;
        public ushort MajorImageVersion;
        public ushort MinorImageVersion;
        public ushort MajorSubsystemVersion;
        public ushort MinorSubsystemVersion;
        public uint Win32VersionValue;
        public uint SizeOfImage;
        public uint SizeOfHeaders;
        public uint CheckSum;
        public ushort Subsystem;
        public ushort DllCharacteristics;
        public ulong SizeOfStackReserve;
        public ulong SizeOfStackCommit;
        public ulong SizeOfHeapReserve;
        public ulong SizeOfHeapCommit;
        public uint LoaderFlags;
        public uint NumberOfRvaAndSizes;
        public IMAGE_DATA_DIRECTORY DataDirectory0;
        public IMAGE_DATA_DIRECTORY DataDirectory1;
        public IMAGE_DATA_DIRECTORY DataDirectory2;
        public IMAGE_DATA_DIRECTORY DataDirectory3;
        public IMAGE_DATA_DIRECTORY DataDirectory4;
        public IMAGE_DATA_DIRECTORY DataDirectory5;
        public IMAGE_DATA_DIRECTORY DataDirectory6;
        public IMAGE_DATA_DIRECTORY DataDirectory7;
        public IMAGE_DATA_DIRECTORY DataDirectory8;
        public IMAGE_DATA_DIRECTORY DataDirectory9;
        public IMAGE_DATA_DIRECTORY DataDirectory10;
        public IMAGE_DATA_DIRECTORY DataDirectory11;
        public IMAGE_DATA_DIRECTORY DataDirectory12;
        public IMAGE_DATA_DIRECTORY DataDirectory13;
        public IMAGE_DATA_DIRECTORY DataDirectory14;
        public IMAGE_DATA_DIRECTORY DataDirectory15;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct IMAGE_NT_HEADERS32
    {
        public uint Signature;
        public IMAGE_FILE_HEADER FileHeader;
        public IMAGE_OPTIONAL_HEADER32 OptionalHeader;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct IMAGE_NT_HEADERS64
    {
        public uint Signature;
        public IMAGE_FILE_HEADER FileHeader;
        public IMAGE_OPTIONAL_HEADER64 OptionalHeader;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct IMAGE_SECTION_HEADER
    {
        public fixed byte Name[8];
        public uint VirtualSize; // Misc.VirtualSize union
        public uint VirtualAddress;
        public uint SizeOfRawData;
        public uint PointerToRawData;
        public uint PointerToRelocations;
        public uint PointerToLinenumbers;
        public ushort NumberOfRelocations;
        public ushort NumberOfLinenumbers;
        public uint Characteristics;

        public string GetName()
        {
            fixed (byte* p = Name)
                return System.Text.Encoding.ASCII.GetString(p, 8).TrimEnd('\0');
        }

        public void SetName(string name)
        {
            fixed (byte* p = Name)
            {
                for (int i = 0; i < 8; i++)
                    p[i] = i < name.Length ? (byte)name[i] : (byte)0;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct IMAGE_EXPORT_DIRECTORY
    {
        public uint Characteristics;
        public uint TimeDateStamp;
        public ushort MajorVersion;
        public ushort MinorVersion;
        public uint Name;
        public uint Base;
        public uint NumberOfFunctions;
        public uint NumberOfNames;
        public uint AddressOfFunctions;
        public uint AddressOfNames;
        public uint AddressOfNameOrdinals;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct IMAGE_IMPORT_DESCRIPTOR
    {
        public uint OriginalFirstThunk;
        public uint TimeDateStamp;
        public uint ForwarderChain;
        public uint Name;
        public uint FirstThunk;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct IMAGE_TLS_DIRECTORY32
    {
        public uint StartAddressOfRawData;
        public uint EndAddressOfRawData;
        public uint AddressOfIndex;
        public uint AddressOfCallBacks;
        public uint SizeOfZeroFill;
        public uint Characteristics;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct IMAGE_TLS_DIRECTORY64
    {
        public ulong StartAddressOfRawData;
        public ulong EndAddressOfRawData;
        public ulong AddressOfIndex;
        public ulong AddressOfCallBacks;
        public uint SizeOfZeroFill;
        public uint Characteristics;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct MODULEENTRY32
    {
        public uint dwSize;
        public uint th32ModuleID;
        public uint th32ProcessID;
        public uint GlbcntUsage;
        public uint ProccntUsage;
        public IntPtr modBaseAddr;
        public uint modBaseSize;
        public IntPtr hModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct OSVERSIONINFOW
    {
        public uint dwOSVersionInfoSize;
        public uint dwMajorVersion;
        public uint dwMinorVersion;
        public uint dwBuildNumber;
        public uint dwPlatformId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szCSDVersion;
    }

#if CPUX86
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct CONTEXT
    {
        public uint ContextFlags;
        public uint Dr0, Dr1, Dr2, Dr3, Dr6, Dr7;
        public fixed byte FloatSave[112];
        public uint SegGs, SegFs, SegEs, SegDs;
        public uint Edi, Esi, Ebx, Edx, Ecx, Eax;
        public uint Ebp;
        public uint Eip;
        public uint SegCs;
        public uint EFlags;
        public uint Esp;
        public uint SegSs;
        public fixed byte ExtendedRegisters[512];

        public nuint IP { get => Eip; set => Eip = (uint)value; }
        public nuint SP { get => Esp; set => Esp = (uint)value; }
    }
#else
    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    public unsafe struct CONTEXT
    {
        public ulong P1Home, P2Home, P3Home, P4Home, P5Home, P6Home;
        public uint ContextFlags;
        public uint MxCsr;
        public ushort SegCs, SegDs, SegEs, SegFs, SegGs, SegSs;
        public uint EFlags;
        public ulong Dr0, Dr1, Dr2, Dr3, Dr6, Dr7;
        public ulong Rax, Rcx, Rdx, Rbx, Rsp, Rbp, Rsi, Rdi;
        public ulong R8, R9, R10, R11, R12, R13, R14, R15;
        public ulong Rip;
        public fixed byte FltSave[512];
        public fixed byte VectorRegister[26 * 16]; // 26 x M128A
        public ulong VectorControl;
        public ulong DebugControl;
        public ulong LastBranchToRip;
        public ulong LastBranchFromRip;
        public ulong LastExceptionToRip;
        public ulong LastExceptionFromRip;

        public nuint IP { get => (nuint)Rip; set => Rip = value; }
        public nuint SP { get => (nuint)Rsp; set => Rsp = value; }
    }
#endif

    // ==================== Kernel32 ====================

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CreateProcess(
        string? lpApplicationName, string lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        bool bInheritHandles, uint dwCreationFlags,
        IntPtr lpEnvironment, string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WaitForDebugEvent(out DEBUG_EVENT lpDebugEvent, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ContinueDebugEvent(uint dwProcessId, uint dwThreadId, uint dwContinueStatus);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DebugActiveProcess(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DebugActiveProcessStop(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetThreadContext(IntPtr hThread, IntPtr lpContext);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetThreadContext(IntPtr hThread, IntPtr lpContext);

    // Aligned wrappers — x64 CONTEXT requires 16-byte alignment
    public static unsafe bool GetThreadContext(IntPtr hThread, ref CONTEXT ctx)
    {
        int size = sizeof(CONTEXT);
        IntPtr mem = Marshal.AllocHGlobal(size + 16);
        try
        {
            IntPtr aligned = (IntPtr)(((long)mem + 15) & ~15L);
            // Zero the memory and set ContextFlags
            for (int i = 0; i < size; i++) ((byte*)aligned)[i] = 0;
            *(uint*)((byte*)aligned + ContextFlagsOffset) = ctx.ContextFlags;
            bool result = GetThreadContext(hThread, aligned);
            if (result)
                ctx = *(CONTEXT*)aligned;
            return result;
        }
        finally { Marshal.FreeHGlobal(mem); }
    }

    public static unsafe bool SetThreadContext(IntPtr hThread, ref CONTEXT ctx)
    {
        int size = sizeof(CONTEXT);
        IntPtr mem = Marshal.AllocHGlobal(size + 16);
        try
        {
            IntPtr aligned = (IntPtr)(((long)mem + 15) & ~15L);
            *(CONTEXT*)aligned = ctx;
            return SetThreadContext(hThread, aligned);
        }
        finally { Marshal.FreeHGlobal(mem); }
    }

#if CPUX86
    private const int ContextFlagsOffset = 0; // ContextFlags is first field in x86 CONTEXT
#else
    private const int ContextFlagsOffset = 48; // After P1Home..P6Home (6 * 8 = 48)
#endif

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, IntPtr lpBuffer, nuint nSize, out nuint lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, IntPtr lpBuffer, nuint nSize, out nuint lpNumberOfBytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool VirtualProtectEx(IntPtr hProcess, IntPtr lpAddress, nuint dwSize, uint flNewProtect, out uint lpflOldProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, nuint dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern nuint VirtualQueryEx(IntPtr hProcess, IntPtr lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, nuint dwLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool FlushInstructionCache(IntPtr hProcess, IntPtr lpBaseAddress, nuint dwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenThread(uint dwDesiredAccess, bool bInheritHandle, uint dwThreadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint SuspendThread(IntPtr hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint ResumeThread(IntPtr hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GetModuleHandleA(string lpModuleName);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool FreeLibrary(IntPtr hModule);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    public static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool IsWow64Process(IntPtr hProcess, out int Wow64Process);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern bool Module32First(IntPtr hSnapshot, ref MODULEENTRY32 lpme);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern bool Module32Next(IntPtr hSnapshot, ref MODULEENTRY32 lpme);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadFile(IntPtr hFile, IntPtr lpBuffer, uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint SetFilePointer(IntPtr hFile, int lDistanceToMove, IntPtr lpDistanceToMoveHigh, uint dwMoveMethod);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CopyFile(string lpExistingFileName, string lpNewFileName, bool bFailIfExists);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool DeleteFile(string lpFileName);

    [DllImport("kernel32.dll")]
    public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    // ==================== NTDLL ====================

    [DllImport("ntdll.dll")]
    public static extern int NtQueryInformationProcess(IntPtr ProcessHandle, uint ProcessInformationClass,
        IntPtr ProcessInformation, uint ProcessInformationLength, IntPtr ReturnLength);

    [DllImport("ntdll.dll")]
    public static extern int NtQueryInformationThread(IntPtr ThreadHandle, uint ThreadInformationClass,
        IntPtr ThreadInformation, uint ThreadInformationLength, IntPtr ReturnLength);

    [DllImport("ntdll.dll")]
    public static extern int RtlGetVersion(ref OSVERSIONINFOW lpVersionInformation);

    // ==================== Shell32 ====================

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    public static extern IntPtr ShellExecute(IntPtr hwnd, string lpOperation, string lpFile, string lpParameters, string? lpDirectory, int nShowCmd);

    // ==================== Helpers ====================

    public static unsafe ref IMAGE_DATA_DIRECTORY GetDataDirectory(ref IMAGE_OPTIONAL_HEADER32 oh, int index)
    {
        fixed (IMAGE_DATA_DIRECTORY* p = &oh.DataDirectory0)
            return ref p[index];
    }

    public static unsafe ref IMAGE_DATA_DIRECTORY GetDataDirectory(ref IMAGE_OPTIONAL_HEADER64 oh, int index)
    {
        fixed (IMAGE_DATA_DIRECTORY* p = &oh.DataDirectory0)
            return ref p[index];
    }
}
