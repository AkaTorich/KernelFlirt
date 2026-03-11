// DbgEngNative.cs — COM interop definitions for dbgeng.dll
// Provides the same interfaces WinDbg uses for kernel debugging via KD protocol.

using System.Runtime.InteropServices;
using System.Text;

namespace KernelFlirt.UI.Services.Interop;

// ============================================================================
// Factory
// ============================================================================

public static class DbgEng
{
    [DllImport("dbgeng.dll", PreserveSig = true)]
    public static extern int DebugCreate(
        ref Guid InterfaceId,
        [MarshalAs(UnmanagedType.IUnknown)] out object Interface);

    public static readonly Guid IID_IDebugClient5 = new("e3acb9d7-7ec2-4f0c-a0da-e81e0cbbe628");
    public static readonly Guid IID_IDebugControl4 = new("94e60ce9-9b41-4b19-9fc0-6d9eb35272b3");
    public static readonly Guid IID_IDebugDataSpaces4 = new("d98ada1f-29e9-4ef5-a6c0-e53349883212");
    public static readonly Guid IID_IDebugRegisters2 = new("1656afa9-19c6-4e3a-97e7-5dc9160cf9c4");
    public static readonly Guid IID_IDebugSymbols3 = new("f02fbecc-50ac-4f36-9ad9-c975e8f32ff8");
    public static readonly Guid IID_IDebugSystemObjects4 = new("489468e6-7d0f-4af5-87ab-25207454d553");
}

// ============================================================================
// Constants
// ============================================================================

public static class DbgConst
{
    // DEBUG_ATTACH_*
    public const uint DEBUG_ATTACH_KERNEL_CONNECTION = 0x00000000;

    // DEBUG_STATUS_*
    public const uint DEBUG_STATUS_NO_CHANGE       = 0;
    public const uint DEBUG_STATUS_GO               = 1;
    public const uint DEBUG_STATUS_GO_HANDLED       = 2;
    public const uint DEBUG_STATUS_GO_NOT_HANDLED   = 3;
    public const uint DEBUG_STATUS_STEP_OVER        = 4;
    public const uint DEBUG_STATUS_STEP_INTO        = 5;
    public const uint DEBUG_STATUS_BREAK            = 6;
    public const uint DEBUG_STATUS_NO_DEBUGGEE      = 7;
    public const uint DEBUG_STATUS_STEP_BRANCH      = 8;

    // DEBUG_BREAKPOINT_*
    public const uint DEBUG_BREAKPOINT_CODE         = 0;
    public const uint DEBUG_BREAKPOINT_DATA         = 1;
    public const uint DEBUG_BREAKPOINT_TIME         = 2;

    // DEBUG_BREAKPOINT_FLAG_*
    public const uint DEBUG_BREAKPOINT_ENABLED      = 0x00000004;
    public const uint DEBUG_BREAKPOINT_GO_ONLY      = 0x00000008;
    public const uint DEBUG_BREAKPOINT_ONE_SHOT     = 0x00000010;

    // DEBUG_BREAK_* (data breakpoint access types)
    public const uint DEBUG_BREAK_READ              = 0x00000001;
    public const uint DEBUG_BREAK_WRITE             = 0x00000002;
    public const uint DEBUG_BREAK_EXECUTE           = 0x00000004;
    public const uint DEBUG_BREAK_IO                = 0x00000008;

    // DEBUG_INTERRUPT_*
    public const uint DEBUG_INTERRUPT_ACTIVE        = 0;
    public const uint DEBUG_INTERRUPT_PASSIVE        = 1;
    public const uint DEBUG_INTERRUPT_EXIT          = 2;

    // DEBUG_WAIT_*
    public const uint INFINITE                      = 0xFFFFFFFF;

    // DEBUG_END_*
    public const uint DEBUG_END_PASSIVE             = 0;
    public const uint DEBUG_END_ACTIVE_TERMINATE    = 1;
    public const uint DEBUG_END_ACTIVE_DETACH       = 2;
    public const uint DEBUG_END_DISCONNECT          = 4;

    // DEBUG_OUTPUT_*
    public const uint DEBUG_OUTPUT_NORMAL           = 0x00000001;
    public const uint DEBUG_OUTPUT_ERROR            = 0x00000002;
    public const uint DEBUG_OUTPUT_WARNING          = 0x00000004;
    public const uint DEBUG_OUTPUT_VERBOSE          = 0x00000008;

    // DEBUG_EXECUTE_*
    public const uint DEBUG_EXECUTE_DEFAULT         = 0;
    public const uint DEBUG_EXECUTE_NOT_LOGGED      = 0x00000002;

    // DEBUG_OUTCTL_*
    public const uint DEBUG_OUTCTL_THIS_CLIENT      = 0x00000000;
    public const uint DEBUG_OUTCTL_ALL_CLIENTS      = 0x00000001;
    public const uint DEBUG_OUTCTL_IGNORE           = 0x00000004;

    // DEBUG_MODULE_*
    public const uint DEBUG_MODNAME_IMAGE           = 0;
    public const uint DEBUG_MODNAME_MODULE          = 1;
    public const uint DEBUG_MODNAME_LOADED_IMAGE    = 2;
    public const uint DEBUG_MODNAME_SYMBOL_FILE     = 3;
    public const uint DEBUG_MODNAME_MAPPED_IMAGE    = 4;

    // DEBUG_VALUE type
    public const uint DEBUG_VALUE_INT8              = 1;
    public const uint DEBUG_VALUE_INT16             = 2;
    public const uint DEBUG_VALUE_INT32             = 3;
    public const uint DEBUG_VALUE_INT64             = 4;
    public const uint DEBUG_VALUE_FLOAT32           = 5;
    public const uint DEBUG_VALUE_FLOAT64           = 6;
    public const uint DEBUG_VALUE_FLOAT80           = 7;
    public const uint DEBUG_VALUE_FLOAT128          = 8;
    public const uint DEBUG_VALUE_VECTOR64          = 9;
    public const uint DEBUG_VALUE_VECTOR128         = 10;

    // HRESULT codes
    public const int S_OK                           = 0;
    public const int S_FALSE                        = 1;
    public const int E_PENDING                      = unchecked((int)0x8000000A);
    public const int E_FAIL                         = unchecked((int)0x80004005);
    public const int E_UNEXPECTED                   = unchecked((int)0x8000FFFF);
    public const int E_NOTIMPL                      = unchecked((int)0x80004001);
}

// ============================================================================
// Structures
// ============================================================================

[StructLayout(LayoutKind.Explicit, Size = 32)]
public struct DEBUG_VALUE
{
    [FieldOffset(0)] public byte I8;
    [FieldOffset(0)] public ushort I16;
    [FieldOffset(0)] public uint I32;
    [FieldOffset(0)] public ulong I64;
    // Float fields
    [FieldOffset(0)] public float F32;
    [FieldOffset(0)] public double F64;
    // Raw bytes for vectors
    [FieldOffset(0)] public ulong RawLow;
    [FieldOffset(8)] public ulong RawHigh;
    // Type tag at offset 24
    [FieldOffset(24)] public uint Type;
}

[StructLayout(LayoutKind.Sequential)]
public struct DEBUG_MODULE_PARAMETERS
{
    public ulong Base;
    public uint Size;
    public uint TimeDateStamp;
    public uint Checksum;
    public uint Flags;
    public uint SymbolType;
    public uint ImageNameSize;
    public uint ModuleNameSize;
    public uint LoadedImageNameSize;
    public uint SymbolFileNameSize;
    public uint MappedImageNameSize;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
    public ulong[] Reserved;
}

[StructLayout(LayoutKind.Sequential)]
public struct DEBUG_REGISTER_DESCRIPTION
{
    public uint Type;
    public uint Flags;
    public uint SubregMaster;
    public uint SubregLength;
    public ulong SubregMask;
    public uint SubregShift;
    public uint Reserved0;
}

[StructLayout(LayoutKind.Sequential)]
public struct DEBUG_BREAKPOINT_PARAMETERS
{
    public ulong Offset;
    public uint Id;
    public uint BreakType;
    public uint ProcType;
    public uint Flags;
    public uint DataSize;
    public uint DataAccessType;
    public uint PassCount;
    public uint CurrentPassCount;
    public uint MatchThread;
    public uint CommandSize;
    public uint OffsetExpressionSize;
}

// ============================================================================
// COM Interfaces
// ============================================================================

// NOTE: vtable order MUST match the C++ interface exactly.
// Every method must be declared even if unused, because COM dispatch is positional.

[ComImport, Guid("e3acb9d7-7ec2-4f0c-a0da-e81e0cbbe628")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDebugClient5
{
    // IDebugClient methods (vtable slots 3-22)
    [PreserveSig] int AttachKernel(uint Flags, [MarshalAs(UnmanagedType.LPStr)] string? ConnectOptions);
    [PreserveSig] int GetKernelConnectionOptions(StringBuilder Buffer, uint BufferSize, out uint OptionsSize);
    [PreserveSig] int SetKernelConnectionOptions([MarshalAs(UnmanagedType.LPStr)] string Options);
    [PreserveSig] int StartProcessServer(uint Flags, [MarshalAs(UnmanagedType.LPStr)] string Options, IntPtr Reserved);
    [PreserveSig] int ConnectProcessServer([MarshalAs(UnmanagedType.LPStr)] string RemoteOptions, out ulong Server);
    [PreserveSig] int DisconnectProcessServer(ulong Server);
    [PreserveSig] int GetRunningProcessSystemIds(ulong Server, [Out] uint[] Ids, uint Count, out uint ActualCount);
    [PreserveSig] int GetRunningProcessSystemIdByExecutableName(ulong Server, [MarshalAs(UnmanagedType.LPStr)] string ExeName, uint Flags, out uint Id);
    [PreserveSig] int GetRunningProcessDescription(ulong Server, uint SystemId, uint Flags, StringBuilder ExeName, uint ExeNameSize, out uint ActualExeNameSize, StringBuilder Description, uint DescriptionSize, out uint ActualDescriptionSize);
    [PreserveSig] int AttachProcess(ulong Server, uint ProcessId, uint AttachFlags);
    [PreserveSig] int CreateProcess(ulong Server, [MarshalAs(UnmanagedType.LPStr)] string CommandLine, uint CreateFlags);
    [PreserveSig] int CreateProcessAndAttach(ulong Server, [MarshalAs(UnmanagedType.LPStr)] string? CommandLine, uint CreateFlags, uint ProcessId, uint AttachFlags);
    [PreserveSig] int GetProcessOptions(out uint Options);
    [PreserveSig] int AddProcessOptions(uint Options);
    [PreserveSig] int RemoveProcessOptions(uint Options);
    [PreserveSig] int SetProcessOptions(uint Options);
    [PreserveSig] int OpenDumpFile([MarshalAs(UnmanagedType.LPStr)] string DumpFile);
    [PreserveSig] int WriteDumpFile([MarshalAs(UnmanagedType.LPStr)] string DumpFile, uint Qualifier);
    [PreserveSig] int ConnectSession(uint Flags, uint HistoryLimit);
    [PreserveSig] int StartServer([MarshalAs(UnmanagedType.LPStr)] string Options);
    [PreserveSig] int OutputServers(uint OutputControl, [MarshalAs(UnmanagedType.LPStr)] string Machine, uint Flags);
    [PreserveSig] int TerminateProcesses();
    [PreserveSig] int DetachProcesses();
    [PreserveSig] int EndSession(uint Flags);
    [PreserveSig] int GetExitCode(out uint Code);
    [PreserveSig] int DispatchCallbacks(uint Timeout);
    [PreserveSig] int ExitDispatch([MarshalAs(UnmanagedType.Interface)] object Client);
    [PreserveSig] int CreateClient([MarshalAs(UnmanagedType.Interface)] out object Client);
    [PreserveSig] int GetInputCallbacks([MarshalAs(UnmanagedType.Interface)] out object Callbacks);
    [PreserveSig] int SetInputCallbacks([MarshalAs(UnmanagedType.Interface)] object? Callbacks);
    [PreserveSig] int GetOutputCallbacks([MarshalAs(UnmanagedType.Interface)] out object Callbacks);
    [PreserveSig] int SetOutputCallbacks([MarshalAs(UnmanagedType.Interface)] object? Callbacks);
    [PreserveSig] int GetOutputMask(out uint Mask);
    [PreserveSig] int SetOutputMask(uint Mask);
    [PreserveSig] int GetOtherOutputMask([MarshalAs(UnmanagedType.Interface)] object Client, out uint Mask);
    [PreserveSig] int SetOtherOutputMask([MarshalAs(UnmanagedType.Interface)] object Client, uint Mask);
    [PreserveSig] int GetOutputWidth(out uint Columns);
    [PreserveSig] int SetOutputWidth(uint Columns);
    [PreserveSig] int GetOutputLinePrefix(StringBuilder Buffer, uint BufferSize, out uint PrefixSize);
    [PreserveSig] int SetOutputLinePrefix([MarshalAs(UnmanagedType.LPStr)] string? Prefix);
    [PreserveSig] int GetIdentity(StringBuilder Buffer, uint BufferSize, out uint IdentitySize);
    [PreserveSig] int OutputIdentity(uint OutputControl, uint Flags, [MarshalAs(UnmanagedType.LPStr)] string Format);
    [PreserveSig] int GetEventCallbacks([MarshalAs(UnmanagedType.Interface)] out object Callbacks);
    [PreserveSig] int SetEventCallbacks([MarshalAs(UnmanagedType.Interface)] object? Callbacks);
    [PreserveSig] int FlushCallbacks();

    // IDebugClient2 methods
    [PreserveSig] int WriteDumpFile2([MarshalAs(UnmanagedType.LPStr)] string DumpFile, uint Qualifier, uint FormatFlags, [MarshalAs(UnmanagedType.LPStr)] string? Comment);
    [PreserveSig] int AddDumpInformationFile([MarshalAs(UnmanagedType.LPStr)] string InfoFile, uint Type);
    [PreserveSig] int EndProcessServer(ulong Server);
    [PreserveSig] int WaitForProcessServerEnd(uint Timeout);
    [PreserveSig] int IsKernelDebuggerEnabled();

    // IDebugClient3 methods
    [PreserveSig] int GetRunningProcessSystemIdByExecutableNameWide(ulong Server, [MarshalAs(UnmanagedType.LPWStr)] string ExeName, uint Flags, out uint Id);
    [PreserveSig] int GetRunningProcessDescriptionWide(ulong Server, uint SystemId, uint Flags, StringBuilder ExeName, uint ExeNameSize, out uint ActualExeNameSize, StringBuilder Description, uint DescriptionSize, out uint ActualDescriptionSize);
    [PreserveSig] int CreateProcessWide(ulong Server, [MarshalAs(UnmanagedType.LPWStr)] string CommandLine, uint CreateFlags);
    [PreserveSig] int CreateProcessAndAttachWide(ulong Server, [MarshalAs(UnmanagedType.LPWStr)] string? CommandLine, uint CreateFlags, uint ProcessId, uint AttachFlags);

    // IDebugClient4 methods
    [PreserveSig] int OpenDumpFileWide([MarshalAs(UnmanagedType.LPWStr)] string? FileName, ulong FileHandle);
    [PreserveSig] int WriteDumpFileWide([MarshalAs(UnmanagedType.LPWStr)] string? FileName, ulong FileHandle, uint Qualifier, uint FormatFlags, [MarshalAs(UnmanagedType.LPWStr)] string? Comment);
    [PreserveSig] int AddDumpInformationFileWide([MarshalAs(UnmanagedType.LPWStr)] string? FileName, ulong FileHandle, uint Type);
    [PreserveSig] int GetNumberDumpFiles(out uint Number);
    [PreserveSig] int GetDumpFile(uint Index, StringBuilder Buffer, uint BufferSize, out uint NameSize, out ulong Handle, out uint Type);
    [PreserveSig] int GetDumpFileWide(uint Index, StringBuilder Buffer, uint BufferSize, out uint NameSize, out ulong Handle, out uint Type);

    // IDebugClient5 methods
    [PreserveSig] int AttachKernelWide(uint Flags, [MarshalAs(UnmanagedType.LPWStr)] string? ConnectOptions);
    [PreserveSig] int GetKernelConnectionOptionsWide(StringBuilder Buffer, uint BufferSize, out uint OptionsSize);
    [PreserveSig] int SetKernelConnectionOptionsWide([MarshalAs(UnmanagedType.LPWStr)] string Options);
    [PreserveSig] int StartProcessServerWide(uint Flags, [MarshalAs(UnmanagedType.LPWStr)] string Options, IntPtr Reserved);
    [PreserveSig] int ConnectProcessServerWide([MarshalAs(UnmanagedType.LPWStr)] string RemoteOptions, out ulong Server);
    [PreserveSig] int StartServerWide([MarshalAs(UnmanagedType.LPWStr)] string Options);
    [PreserveSig] int OutputServersWide(uint OutputControl, [MarshalAs(UnmanagedType.LPWStr)] string Machine, uint Flags);
    [PreserveSig] int GetOutputCallbacksWide([MarshalAs(UnmanagedType.Interface)] out object Callbacks);
    [PreserveSig] int SetOutputCallbacksWide([MarshalAs(UnmanagedType.Interface)] object? Callbacks);
    [PreserveSig] int GetOutputLinePrefixWide(StringBuilder Buffer, uint BufferSize, out uint PrefixSize);
    [PreserveSig] int SetOutputLinePrefixWide([MarshalAs(UnmanagedType.LPWStr)] string? Prefix);
    [PreserveSig] int GetIdentityWide(StringBuilder Buffer, uint BufferSize, out uint IdentitySize);
    [PreserveSig] int OutputIdentityWide(uint OutputControl, uint Flags, [MarshalAs(UnmanagedType.LPWStr)] string Format);
    [PreserveSig] int GetEventCallbacksWide([MarshalAs(UnmanagedType.Interface)] out object Callbacks);
    [PreserveSig] int SetEventCallbacksWide([MarshalAs(UnmanagedType.Interface)] object? Callbacks);
    [PreserveSig] int CreateProcess2(ulong Server, [MarshalAs(UnmanagedType.LPStr)] string CommandLine, IntPtr OptionsBuffer, uint OptionsBufferSize, [MarshalAs(UnmanagedType.LPStr)] string? InitialDirectory, [MarshalAs(UnmanagedType.LPStr)] string? Environment);
    [PreserveSig] int CreateProcess2Wide(ulong Server, [MarshalAs(UnmanagedType.LPWStr)] string CommandLine, IntPtr OptionsBuffer, uint OptionsBufferSize, [MarshalAs(UnmanagedType.LPWStr)] string? InitialDirectory, [MarshalAs(UnmanagedType.LPWStr)] string? Environment);
    [PreserveSig] int CreateProcessAndAttach2(ulong Server, [MarshalAs(UnmanagedType.LPStr)] string? CommandLine, IntPtr OptionsBuffer, uint OptionsBufferSize, [MarshalAs(UnmanagedType.LPStr)] string? InitialDirectory, [MarshalAs(UnmanagedType.LPStr)] string? Environment, uint ProcessId, uint AttachFlags);
    [PreserveSig] int CreateProcessAndAttach2Wide(ulong Server, [MarshalAs(UnmanagedType.LPWStr)] string? CommandLine, IntPtr OptionsBuffer, uint OptionsBufferSize, [MarshalAs(UnmanagedType.LPWStr)] string? InitialDirectory, [MarshalAs(UnmanagedType.LPWStr)] string? Environment, uint ProcessId, uint AttachFlags);
    [PreserveSig] int PushOutputLinePrefix([MarshalAs(UnmanagedType.LPStr)] string? NewPrefix, out ulong Handle);
    [PreserveSig] int PushOutputLinePrefixWide([MarshalAs(UnmanagedType.LPWStr)] string? NewPrefix, out ulong Handle);
    [PreserveSig] int PopOutputLinePrefix(ulong Handle);
    [PreserveSig] int GetNumberInputCallbacks(out uint Count);
    [PreserveSig] int GetNumberOutputCallbacks(out uint Count);
    [PreserveSig] int GetNumberEventCallbacks(uint EventFlags, out uint Count);
    [PreserveSig] int GetQuitLockString(StringBuilder Buffer, uint BufferSize, out uint StringSize);
    [PreserveSig] int SetQuitLockString([MarshalAs(UnmanagedType.LPStr)] string String);
    [PreserveSig] int GetQuitLockStringWide(StringBuilder Buffer, uint BufferSize, out uint StringSize);
    [PreserveSig] int SetQuitLockStringWide([MarshalAs(UnmanagedType.LPWStr)] string String);
}

[ComImport, Guid("94e60ce9-9b41-4b19-9fc0-6d9eb35272b3")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDebugControl4
{
    // IDebugControl (vtable 3+)
    [PreserveSig] int GetInterrupt();
    [PreserveSig] int SetInterrupt(uint Flags);
    [PreserveSig] int GetInterruptTimeout(out uint Seconds);
    [PreserveSig] int SetInterruptTimeout(uint Seconds);
    [PreserveSig] int GetLogFile(StringBuilder Buffer, uint BufferSize, out uint FileSize, out int Append);
    [PreserveSig] int OpenLogFile([MarshalAs(UnmanagedType.LPStr)] string File, int Append);
    [PreserveSig] int CloseLogFile();
    [PreserveSig] int GetLogMask(out uint Mask);
    [PreserveSig] int SetLogMask(uint Mask);
    [PreserveSig] int Input(StringBuilder Buffer, uint BufferSize, out uint InputSize);
    [PreserveSig] int ReturnInput([MarshalAs(UnmanagedType.LPStr)] string Buffer);
    [PreserveSig] int Output(uint Mask, [MarshalAs(UnmanagedType.LPStr)] string Format);
    [PreserveSig] int OutputVaList(uint Mask, [MarshalAs(UnmanagedType.LPStr)] string Format, IntPtr Args);
    [PreserveSig] int ControlledOutput(uint OutputControl, uint Mask, [MarshalAs(UnmanagedType.LPStr)] string Format);
    [PreserveSig] int ControlledOutputVaList(uint OutputControl, uint Mask, [MarshalAs(UnmanagedType.LPStr)] string Format, IntPtr Args);
    [PreserveSig] int OutputPrompt(uint OutputControl, [MarshalAs(UnmanagedType.LPStr)] string? Format);
    [PreserveSig] int OutputPromptVaList(uint OutputControl, [MarshalAs(UnmanagedType.LPStr)] string? Format, IntPtr Args);
    [PreserveSig] int GetPromptText(StringBuilder Buffer, uint BufferSize, out uint TextSize);
    [PreserveSig] int OutputCurrentState(uint OutputControl, uint Flags);
    [PreserveSig] int OutputVersionInformation(uint OutputControl);
    [PreserveSig] int GetNotifyEventHandle(out ulong Handle);
    [PreserveSig] int SetNotifyEventHandle(ulong Handle);
    [PreserveSig] int Assemble(ulong Offset, [MarshalAs(UnmanagedType.LPStr)] string Instr, out ulong EndOffset);
    [PreserveSig] int Disassemble(ulong Offset, uint Flags, StringBuilder Buffer, uint BufferSize, out uint DisassemblySize, out ulong EndOffset);
    [PreserveSig] int GetDisassembleEffectiveOffset(out ulong Offset);
    [PreserveSig] int OutputDisassembly(uint OutputControl, ulong Offset, uint Flags, out ulong EndOffset);
    [PreserveSig] int OutputDisassemblyLines(uint OutputControl, uint PreviousLines, uint TotalLines, ulong Offset, uint Flags, out uint OffsetLine, out ulong StartOffset, out ulong EndOffset, [Out] ulong[]? LineOffsets);
    [PreserveSig] int GetNearInstruction(ulong Offset, int Delta, out ulong NearOffset);
    [PreserveSig] int GetStackTrace(ulong FrameOffset, ulong StackOffset, ulong InstructionOffset, IntPtr Frames, uint FramesSize, out uint FramesFilled);
    [PreserveSig] int GetReturnOffset(out ulong Offset);
    [PreserveSig] int OutputStackTrace(uint OutputControl, IntPtr Frames, uint FramesSize, uint Flags);
    [PreserveSig] int GetDebuggeeType(out uint Class, out uint Qualifier);
    [PreserveSig] int GetActualProcessorType(out uint Type);
    [PreserveSig] int GetExecutingProcessorType(out uint Type);
    [PreserveSig] int GetNumberPossibleExecutingProcessorTypes(out uint Number);
    [PreserveSig] int GetPossibleExecutingProcessorTypes(uint Start, uint Count, [Out] uint[] Types);
    [PreserveSig] int GetNumberProcessors(out uint Number);
    [PreserveSig] int GetSystemVersion(out uint PlatformId, out uint Major, out uint Minor, StringBuilder ServicePackString, uint ServicePackStringSize, out uint ServicePackStringUsed, out uint ServicePackNumber, StringBuilder BuildString, uint BuildStringSize, out uint BuildStringUsed);
    [PreserveSig] int GetPageSize(out uint Size);
    [PreserveSig] int IsPointer64Bit();
    [PreserveSig] int ReadBugCheckData(out uint Code, out ulong Arg1, out ulong Arg2, out ulong Arg3, out ulong Arg4);
    [PreserveSig] int GetNumberSupportedProcessorTypes(out uint Number);
    [PreserveSig] int GetSupportedProcessorTypes(uint Start, uint Count, [Out] uint[] Types);
    [PreserveSig] int GetProcessorTypeNames(uint Type, StringBuilder FullNameBuffer, uint FullNameBufferSize, out uint FullNameSize, StringBuilder AbbrevNameBuffer, uint AbbrevNameBufferSize, out uint AbbrevNameSize);
    [PreserveSig] int GetEffectiveProcessorType(out uint Type);
    [PreserveSig] int SetEffectiveProcessorType(uint Type);
    [PreserveSig] int GetExecutionStatus(out uint Status);
    [PreserveSig] int SetExecutionStatus(uint Status);
    [PreserveSig] int GetCodeLevel(out uint Level);
    [PreserveSig] int SetCodeLevel(uint Level);
    [PreserveSig] int GetEngineOptions(out uint Options);
    [PreserveSig] int AddEngineOptions(uint Options);
    [PreserveSig] int RemoveEngineOptions(uint Options);
    [PreserveSig] int SetEngineOptions(uint Options);
    [PreserveSig] int GetSystemErrorControl(out uint OutputLevel, out uint BreakLevel);
    [PreserveSig] int SetSystemErrorControl(uint OutputLevel, uint BreakLevel);
    [PreserveSig] int GetTextMacro(uint Slot, StringBuilder Buffer, uint BufferSize, out uint MacroSize);
    [PreserveSig] int SetTextMacro(uint Slot, [MarshalAs(UnmanagedType.LPStr)] string Macro);
    [PreserveSig] int GetRadix(out uint Radix);
    [PreserveSig] int SetRadix(uint Radix);
    [PreserveSig] int Evaluate([MarshalAs(UnmanagedType.LPStr)] string Expression, uint DesiredType, out DEBUG_VALUE Value, out uint RemainderIndex);
    [PreserveSig] int CoerceValue(ref DEBUG_VALUE In, uint OutType, out DEBUG_VALUE Out);
    [PreserveSig] int CoerceValues(uint Count, [In] DEBUG_VALUE[] In, [In] uint[] OutTypes, [Out] DEBUG_VALUE[] Out);
    [PreserveSig] int Execute(uint OutputControl, [MarshalAs(UnmanagedType.LPStr)] string Command, uint Flags);
    [PreserveSig] int ExecuteCommandFile(uint OutputControl, [MarshalAs(UnmanagedType.LPStr)] string CommandFile, uint Flags);
    [PreserveSig] int GetNumberBreakpoints(out uint Number);
    [PreserveSig] int GetBreakpointByIndex(uint Index, [MarshalAs(UnmanagedType.Interface)] out object Bp);
    [PreserveSig] int GetBreakpointById(uint Id, [MarshalAs(UnmanagedType.Interface)] out object Bp);
    [PreserveSig] int GetBreakpointParameters(uint Count, [In] uint[]? Ids, uint Start, [Out] DEBUG_BREAKPOINT_PARAMETERS[] Params);
    [PreserveSig] int AddBreakpoint(uint Type, uint DesiredId, [MarshalAs(UnmanagedType.Interface)] out object Bp);
    [PreserveSig] int RemoveBreakpoint([MarshalAs(UnmanagedType.Interface)] object Bp);
    [PreserveSig] int AddExtension([MarshalAs(UnmanagedType.LPStr)] string Path, uint Flags, out ulong Handle);
    [PreserveSig] int RemoveExtension(ulong Handle);
    [PreserveSig] int GetExtensionByPath([MarshalAs(UnmanagedType.LPStr)] string Path, out ulong Handle);
    [PreserveSig] int CallExtension(ulong Handle, [MarshalAs(UnmanagedType.LPStr)] string Function, [MarshalAs(UnmanagedType.LPStr)] string? Arguments);
    [PreserveSig] int GetExtensionFunction(ulong Handle, [MarshalAs(UnmanagedType.LPStr)] string FuncName, out IntPtr Function);
    [PreserveSig] int GetWindbgExtensionApis32(IntPtr Api);
    [PreserveSig] int GetWindbgExtensionApis64(IntPtr Api);
    [PreserveSig] int GetNumberEventFilters(out uint SpecificEvents, out uint SpecificExceptions, out uint ArbitraryExceptions);
    [PreserveSig] int GetEventFilterText(uint Index, StringBuilder Buffer, uint BufferSize, out uint TextSize);
    [PreserveSig] int GetEventFilterCommand(uint Index, StringBuilder Buffer, uint BufferSize, out uint CommandSize);
    [PreserveSig] int SetEventFilterCommand(uint Index, [MarshalAs(UnmanagedType.LPStr)] string Command);
    [PreserveSig] int GetSpecificFilterParameters(uint Start, uint Count, IntPtr Params);
    [PreserveSig] int SetSpecificFilterParameters(uint Start, uint Count, IntPtr Params);
    [PreserveSig] int GetSpecificFilterArgument(uint Index, StringBuilder Buffer, uint BufferSize, out uint ArgumentSize);
    [PreserveSig] int SetSpecificFilterArgument(uint Index, [MarshalAs(UnmanagedType.LPStr)] string Argument);
    [PreserveSig] int GetExceptionFilterParameters(uint Count, [In] uint[]? Codes, uint Start, IntPtr Params);
    [PreserveSig] int SetExceptionFilterParameters(uint Count, IntPtr Params);
    [PreserveSig] int GetExceptionFilterSecondCommand(uint Index, StringBuilder Buffer, uint BufferSize, out uint CommandSize);
    [PreserveSig] int SetExceptionFilterSecondCommand(uint Index, [MarshalAs(UnmanagedType.LPStr)] string Command);
    [PreserveSig] int WaitForEvent(uint Flags, uint Timeout);

    // We stop here — IDebugControl2/3/4 methods are beyond what we need.
    // If needed later, add remaining vtable stubs.
}

[ComImport, Guid("d98ada1f-29e9-4ef5-a6c0-e53349883212")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDebugDataSpaces4
{
    // IDebugDataSpaces (vtable 3+)
    [PreserveSig] int ReadVirtual(ulong Offset, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] Buffer, uint BufferSize, out uint BytesRead);
    [PreserveSig] int WriteVirtual(ulong Offset, [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] Buffer, uint BufferSize, out uint BytesWritten);
    [PreserveSig] int SearchVirtual(ulong Offset, ulong Length, IntPtr Pattern, uint PatternSize, uint PatternGranularity, out ulong MatchOffset);
    [PreserveSig] int ReadVirtualUncached(ulong Offset, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] Buffer, uint BufferSize, out uint BytesRead);
    [PreserveSig] int WriteVirtualUncached(ulong Offset, [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] Buffer, uint BufferSize, out uint BytesWritten);
    [PreserveSig] int ReadPointersVirtual(uint Count, ulong Offset, [Out] ulong[] Ptrs);
    [PreserveSig] int WritePointersVirtual(uint Count, ulong Offset, [In] ulong[] Ptrs);
    [PreserveSig] int ReadPhysical(ulong Offset, [Out] byte[] Buffer, uint BufferSize, out uint BytesRead);
    [PreserveSig] int WritePhysical(ulong Offset, [In] byte[] Buffer, uint BufferSize, out uint BytesWritten);
    [PreserveSig] int ReadControl(uint Processor, ulong Offset, [Out] byte[] Buffer, uint BufferSize, out uint BytesRead);
    [PreserveSig] int WriteControl(uint Processor, ulong Offset, [In] byte[] Buffer, uint BufferSize, out uint BytesWritten);
    [PreserveSig] int ReadIo(uint InterfaceType, uint BusNumber, uint AddressSpace, ulong Offset, [Out] byte[] Buffer, uint BufferSize, out uint BytesRead);
    [PreserveSig] int WriteIo(uint InterfaceType, uint BusNumber, uint AddressSpace, ulong Offset, [In] byte[] Buffer, uint BufferSize, out uint BytesWritten);
    [PreserveSig] int ReadMsr(uint Msr, out ulong Value);
    [PreserveSig] int WriteMsr(uint Msr, ulong Value);
    [PreserveSig] int ReadBusData(uint BusDataType, uint BusNumber, uint SlotNumber, uint Offset, [Out] byte[] Buffer, uint BufferSize, out uint BytesRead);
    [PreserveSig] int WriteBusData(uint BusDataType, uint BusNumber, uint SlotNumber, uint Offset, [In] byte[] Buffer, uint BufferSize, out uint BytesWritten);
    [PreserveSig] int CheckLowMemory();
    [PreserveSig] int ReadDebuggerData(uint Index, [Out] byte[] Buffer, uint BufferSize, out uint DataSize);
    [PreserveSig] int ReadProcessorSystemData(uint Processor, uint Index, [Out] byte[] Buffer, uint BufferSize, out uint DataSize);

    // We stop at IDebugDataSpaces — sufficient for ReadVirtual/WriteVirtual.
}

[ComImport, Guid("1656afa9-19c6-4e3a-97e7-5dc9160cf9c4")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDebugRegisters2
{
    // IDebugRegisters (vtable 3+)
    [PreserveSig] int GetNumberRegisters(out uint Number);
    [PreserveSig] int GetDescription(uint Register, StringBuilder Buffer, uint BufferSize, out uint DescSize, out DEBUG_REGISTER_DESCRIPTION Desc);
    [PreserveSig] int GetIndexByName([MarshalAs(UnmanagedType.LPStr)] string Name, out uint Index);
    [PreserveSig] int GetValue(uint Register, out DEBUG_VALUE Value);
    [PreserveSig] int SetValue(uint Register, ref DEBUG_VALUE Value);
    [PreserveSig] int GetValues(uint Count, [In] uint[]? Indices, uint Start, [Out] DEBUG_VALUE[] Values);
    [PreserveSig] int SetValues(uint Count, [In] uint[]? Indices, uint Start, [In] DEBUG_VALUE[] Values);
    [PreserveSig] int OutputRegisters(uint OutputControl, uint Flags);
    [PreserveSig] int GetInstructionOffset(out ulong Offset);
    [PreserveSig] int GetStackOffset(out ulong Offset);
    [PreserveSig] int GetFrameOffset(out ulong Offset);

    // IDebugRegisters2 additions
    [PreserveSig] int GetInstructionOffset2(uint Source, out ulong Offset);
    [PreserveSig] int GetStackOffset2(uint Source, out ulong Offset);
    [PreserveSig] int GetFrameOffset2(uint Source, out ulong Offset);
    [PreserveSig] int GetNumberPseudoRegisters(out uint Number);
    [PreserveSig] int GetPseudoDescription(uint Register, StringBuilder Buffer, uint BufferSize, out uint DescSize, out ulong TypeModule, out uint TypeId);
    [PreserveSig] int GetPseudoIndexByName([MarshalAs(UnmanagedType.LPStr)] string Name, out uint Index);
    [PreserveSig] int GetPseudoValues(uint Source, uint Count, [In] uint[]? Indices, uint Start, [Out] DEBUG_VALUE[] Values);
    [PreserveSig] int SetPseudoValues(uint Source, uint Count, [In] uint[]? Indices, uint Start, [In] DEBUG_VALUE[] Values);
    [PreserveSig] int GetValues2(uint Source, uint Count, [In] uint[]? Indices, uint Start, [Out] DEBUG_VALUE[] Values);
    [PreserveSig] int SetValues2(uint Source, uint Count, [In] uint[]? Indices, uint Start, [In] DEBUG_VALUE[] Values);
    [PreserveSig] int OutputRegisters2(uint OutputControl, uint Source, uint Flags);
    [PreserveSig] int GetDescription2(uint Register, StringBuilder Buffer, uint BufferSize, out uint DescSize, out DEBUG_REGISTER_DESCRIPTION Desc);
}

[ComImport, Guid("f02fbecc-50ac-4f36-9ad9-c975e8f32ff8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDebugSymbols3
{
    // IDebugSymbols (vtable 3+)
    [PreserveSig] int GetSymbolOptions(out uint Options);
    [PreserveSig] int AddSymbolOptions(uint Options);
    [PreserveSig] int RemoveSymbolOptions(uint Options);
    [PreserveSig] int SetSymbolOptions(uint Options);
    [PreserveSig] int GetNameByOffset(ulong Offset, StringBuilder Buffer, uint BufferSize, out uint NameSize, out ulong Displacement);
    [PreserveSig] int GetOffsetByName([MarshalAs(UnmanagedType.LPStr)] string Symbol, out ulong Offset);
    [PreserveSig] int GetNearNameByOffset(ulong Offset, int Delta, StringBuilder Buffer, uint BufferSize, out uint NameSize, out ulong Displacement);
    [PreserveSig] int GetLineByOffset(ulong Offset, out uint Line, StringBuilder FileBuffer, uint FileBufferSize, out uint FileSize, out ulong Displacement);
    [PreserveSig] int GetOffsetByLine(uint Line, [MarshalAs(UnmanagedType.LPStr)] string File, out ulong Offset);
    [PreserveSig] int GetNumberModules(out uint Loaded, out uint Unloaded);
    [PreserveSig] int GetModuleByIndex(uint Index, out ulong Base);
    [PreserveSig] int GetModuleByModuleName([MarshalAs(UnmanagedType.LPStr)] string Name, uint StartIndex, out uint Index, out ulong Base);
    [PreserveSig] int GetModuleByOffset(ulong Offset, uint StartIndex, out uint Index, out ulong Base);
    [PreserveSig] int GetModuleNames(uint Index, ulong Base, StringBuilder ImageNameBuffer, uint ImageNameBufferSize, out uint ImageNameSize, StringBuilder ModuleNameBuffer, uint ModuleNameBufferSize, out uint ModuleNameSize, StringBuilder LoadedImageNameBuffer, uint LoadedImageNameBufferSize, out uint LoadedImageNameSize);
    [PreserveSig] int GetModuleParameters(uint Count, [In] ulong[]? Bases, uint Start, [Out] DEBUG_MODULE_PARAMETERS[] Params);
    [PreserveSig] int GetSymbolModule([MarshalAs(UnmanagedType.LPStr)] string Symbol, out ulong Base);
    [PreserveSig] int GetTypeName(ulong Module, uint TypeId, StringBuilder Buffer, uint BufferSize, out uint NameSize);
    [PreserveSig] int GetTypeId(ulong Module, [MarshalAs(UnmanagedType.LPStr)] string Name, out uint TypeId);
    [PreserveSig] int GetTypeSize(ulong Module, uint TypeId, out uint Size);
    [PreserveSig] int GetFieldOffset(ulong Module, uint TypeId, [MarshalAs(UnmanagedType.LPStr)] string Field, out uint Offset);
    [PreserveSig] int GetSymbolTypeId([MarshalAs(UnmanagedType.LPStr)] string Symbol, out uint TypeId, out ulong Module);
    [PreserveSig] int GetOffsetTypeId(ulong Offset, out uint TypeId, out ulong Module);
    [PreserveSig] int ReadTypedDataVirtual(ulong Offset, ulong Module, uint TypeId, [Out] byte[] Buffer, uint BufferSize, out uint BytesRead);
    [PreserveSig] int WriteTypedDataVirtual(ulong Offset, ulong Module, uint TypeId, [In] byte[] Buffer, uint BufferSize, out uint BytesWritten);
    [PreserveSig] int OutputTypedDataVirtual(uint OutputControl, ulong Offset, ulong Module, uint TypeId, uint Flags);
    [PreserveSig] int ReadTypedDataPhysical(ulong Offset, ulong Module, uint TypeId, [Out] byte[] Buffer, uint BufferSize, out uint BytesRead);
    [PreserveSig] int WriteTypedDataPhysical(ulong Offset, ulong Module, uint TypeId, [In] byte[] Buffer, uint BufferSize, out uint BytesWritten);
    [PreserveSig] int OutputTypedDataPhysical(uint OutputControl, ulong Offset, ulong Module, uint TypeId, uint Flags);
    [PreserveSig] int GetScope(out ulong InstructionOffset, IntPtr ScopeFrame, IntPtr ScopeContext, uint ScopeContextSize);
    [PreserveSig] int SetScope(ulong InstructionOffset, IntPtr ScopeFrame, IntPtr ScopeContext, uint ScopeContextSize);
    [PreserveSig] int ResetScope();
    [PreserveSig] int GetScopeSymbolGroup(uint Flags, [MarshalAs(UnmanagedType.Interface)] object? Update, [MarshalAs(UnmanagedType.Interface)] out object Symbols);
    [PreserveSig] int CreateSymbolGroup([MarshalAs(UnmanagedType.Interface)] out object Group);
    [PreserveSig] int StartSymbolMatch([MarshalAs(UnmanagedType.LPStr)] string Pattern, out ulong Handle);
    [PreserveSig] int GetNextSymbolMatch(ulong Handle, StringBuilder Buffer, uint BufferSize, out uint MatchSize, out ulong Offset);
    [PreserveSig] int EndSymbolMatch(ulong Handle);
    [PreserveSig] int Reload([MarshalAs(UnmanagedType.LPStr)] string Module);
    [PreserveSig] int GetSymbolPath(StringBuilder Buffer, uint BufferSize, out uint PathSize);
    [PreserveSig] int SetSymbolPath([MarshalAs(UnmanagedType.LPStr)] string Path);
    [PreserveSig] int AppendSymbolPath([MarshalAs(UnmanagedType.LPStr)] string Addition);
    [PreserveSig] int GetImagePath(StringBuilder Buffer, uint BufferSize, out uint PathSize);
    [PreserveSig] int SetImagePath([MarshalAs(UnmanagedType.LPStr)] string Path);
    [PreserveSig] int AppendImagePath([MarshalAs(UnmanagedType.LPStr)] string Addition);
    [PreserveSig] int GetSourcePath(StringBuilder Buffer, uint BufferSize, out uint PathSize);
    [PreserveSig] int GetSourcePathElement(uint Index, StringBuilder Buffer, uint BufferSize, out uint ElementSize);
    [PreserveSig] int SetSourcePath([MarshalAs(UnmanagedType.LPStr)] string Path);
    [PreserveSig] int AppendSourcePath([MarshalAs(UnmanagedType.LPStr)] string Addition);
    [PreserveSig] int FindSourceFile(uint StartElement, [MarshalAs(UnmanagedType.LPStr)] string File, uint Flags, out uint FoundElement, StringBuilder Buffer, uint BufferSize, out uint FoundSize);
    [PreserveSig] int GetSourceFileLineOffsets([MarshalAs(UnmanagedType.LPStr)] string File, [Out] ulong[]? Buffer, uint BufferLines, out uint FileLines);

    // We stop at IDebugSymbols — sufficient for GetNameByOffset, modules, symbol path.
}

[ComImport, Guid("489468e6-7d0f-4af5-87ab-25207454d553")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDebugSystemObjects4
{
    // IDebugSystemObjects (vtable 3+)
    [PreserveSig] int GetEventThread(out uint Id);
    [PreserveSig] int GetEventProcess(out uint Id);
    [PreserveSig] int GetCurrentThreadId(out uint Id);
    [PreserveSig] int SetCurrentThreadId(uint Id);
    [PreserveSig] int GetCurrentProcessId(out uint Id);
    [PreserveSig] int SetCurrentProcessId(uint Id);
    [PreserveSig] int GetNumberThreads(out uint Number);
    [PreserveSig] int GetTotalNumberThreads(out uint Total, out uint LargestProcess);
    [PreserveSig] int GetThreadIdsByIndex(uint Start, uint Count, [Out] uint[]? Ids, [Out] uint[]? SysIds);
    [PreserveSig] int GetThreadIdByProcessor(uint Processor, out uint Id);
    [PreserveSig] int GetCurrentThreadDataOffset(out ulong Offset);
    [PreserveSig] int GetThreadIdByDataOffset(ulong Offset, out uint Id);
    [PreserveSig] int GetCurrentThreadTeb(out ulong Offset);
    [PreserveSig] int GetThreadIdByTeb(ulong Offset, out uint Id);
    [PreserveSig] int GetCurrentThreadSystemId(out uint SysId);
    [PreserveSig] int GetThreadIdBySystemId(uint SysId, out uint Id);
    [PreserveSig] int GetCurrentProcessDataOffset(out ulong Offset);
    [PreserveSig] int GetProcessIdByDataOffset(ulong Offset, out uint Id);
    [PreserveSig] int GetCurrentProcessPeb(out ulong Offset);
    [PreserveSig] int GetProcessIdByPeb(ulong Offset, out uint Id);
    [PreserveSig] int GetCurrentProcessSystemId(out uint SysId);
    [PreserveSig] int GetProcessIdBySystemId(uint SysId, out uint Id);
    [PreserveSig] int GetCurrentProcessHandle(out ulong Handle);
    [PreserveSig] int GetProcessIdByHandle(ulong Handle, out uint Id);
    [PreserveSig] int GetNumberProcesses(out uint Number);
    [PreserveSig] int GetProcessIdsByIndex(uint Start, uint Count, [Out] uint[]? Ids, [Out] uint[]? SysIds);
    [PreserveSig] int GetCurrentProcessUpTime(out uint UpTime);
    [PreserveSig] int GetImplicitThreadDataOffset(out ulong Offset);
    [PreserveSig] int SetImplicitThreadDataOffset(ulong Offset);
    [PreserveSig] int GetImplicitProcessDataOffset(out ulong Offset);
    [PreserveSig] int SetImplicitProcessDataOffset(ulong Offset);

    // IDebugSystemObjects2/3/4 — skipped for now
}

// ============================================================================
// Callbacks
// ============================================================================

[ComImport, Guid("4bf58045-d654-4c40-b0af-683090f356dc")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDebugOutputCallbacks
{
    [PreserveSig] int Output(uint Mask, [MarshalAs(UnmanagedType.LPStr)] string Text);
}

// IDebugEventCallbacks — vtable must match exactly
[ComImport, Guid("337be28b-5036-4d72-b6bf-c45fbb9f2eaa")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDebugEventCallbacks
{
    [PreserveSig] int GetInterestMask(out uint Mask);
    [PreserveSig] int Breakpoint(IntPtr Bp);  // IDebugBreakpoint* — use IntPtr to avoid RCW creation
    [PreserveSig] int Exception(IntPtr Exception, uint FirstChance);
    [PreserveSig] int CreateThread(ulong Handle, ulong DataOffset, ulong StartOffset);
    [PreserveSig] int ExitThread(uint ExitCode);
    [PreserveSig] int CreateProcess(ulong ImageFileHandle, ulong Handle, ulong BaseOffset,
        uint ModuleSize, IntPtr ModuleName, IntPtr ImageName, uint CheckSum, uint TimeDateStamp,
        ulong InitialThreadHandle, ulong ThreadDataOffset, ulong StartOffset);
    [PreserveSig] int ExitProcess(uint ExitCode);
    [PreserveSig] int LoadModule(ulong ImageFileHandle, ulong BaseOffset, uint ModuleSize,
        IntPtr ModuleName, IntPtr ImageName, uint CheckSum, uint TimeDateStamp);
    [PreserveSig] int UnloadModule(IntPtr ImageBaseName, ulong BaseOffset);
    [PreserveSig] int SystemError(uint Error, uint Level);
    [PreserveSig] int SessionStatus(uint Status);
    [PreserveSig] int ChangeDebuggeeState(uint Flags, ulong Argument);
    [PreserveSig] int ChangeEngineState(uint Flags, ulong Argument);
    [PreserveSig] int ChangeSymbolState(uint Flags, ulong Argument);
}

[ComImport, Guid("5bd9d474-5975-423a-b88b-65a8e7110e65")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDebugBreakpoint
{
    [PreserveSig] int GetId(out uint Id);
    [PreserveSig] int GetType(out uint BreakType, out uint ProcType);
    [PreserveSig] int GetAdder([MarshalAs(UnmanagedType.Interface)] out object Adder);
    [PreserveSig] int GetFlags(out uint Flags);
    [PreserveSig] int AddFlags(uint Flags);
    [PreserveSig] int RemoveFlags(uint Flags);
    [PreserveSig] int SetFlags(uint Flags);
    [PreserveSig] int GetOffset(out ulong Offset);
    [PreserveSig] int SetOffset(ulong Offset);
    [PreserveSig] int GetDataParameters(out uint Size, out uint AccessType);
    [PreserveSig] int SetDataParameters(uint Size, uint AccessType);
    [PreserveSig] int GetPassCount(out uint Count);
    [PreserveSig] int SetPassCount(uint Count);
    [PreserveSig] int GetCurrentPassCount(out uint Count);
    [PreserveSig] int GetMatchThreadId(out uint Id);
    [PreserveSig] int SetMatchThreadId(uint Thread);
    [PreserveSig] int GetCommand(StringBuilder Buffer, uint BufferSize, out uint CommandSize);
    [PreserveSig] int SetCommand([MarshalAs(UnmanagedType.LPStr)] string Command);
    [PreserveSig] int GetOffsetExpression(StringBuilder Buffer, uint BufferSize, out uint ExpressionSize);
    [PreserveSig] int SetOffsetExpression([MarshalAs(UnmanagedType.LPStr)] string Expression);
    [PreserveSig] int GetParameters(out DEBUG_BREAKPOINT_PARAMETERS Params);
}
