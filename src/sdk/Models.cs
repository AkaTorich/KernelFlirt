namespace KernelFlirt.SDK;

public enum PluginBreakpointType
{
    Software = 0,
    Hardware = 1,
    HwWrite = 2,
    HwReadWrite = 3,
    Memory = 4
}

public enum PluginDebugEventType
{
    Breakpoint = 1,
    SingleStep = 2,
    HwBreakpoint = 3,
    HwWatchpoint = 4,
    MemoryBp = 5,
    AccessViolation = 6
}

public class PluginRegister
{
    public string Name { get; set; } = "";
    public ulong Value { get; set; }
    public bool IsFlag { get; set; }
}

public class PluginBreakpoint
{
    public uint Handle { get; set; }
    public ulong Address { get; set; }
    public PluginBreakpointType Type { get; set; }
    public bool Enabled { get; set; }
    public string? Condition { get; set; }
    public uint HitCount { get; set; }
    public byte OriginalByte { get; set; }
}

public class PluginModuleInfo
{
    public ulong BaseAddress { get; set; }
    public uint Size { get; set; }
    public string Name { get; set; } = "";
}

public class PluginFunctionEntry
{
    public ulong Address { get; set; }
    public string Name { get; set; } = "";
    public uint Size { get; set; }
}

public class PluginKernelModuleInfo
{
    public ulong BaseAddress { get; set; }
    public uint Size { get; set; }
    public ushort LoadOrder { get; set; }
    public string Name { get; set; } = "";
}

public class PluginProcessInfo
{
    public uint ProcessId { get; set; }
    public uint SessionId { get; set; }
    public string Name { get; set; } = "";
}

public class PluginThreadInfo
{
    public uint ThreadId { get; set; }
    public ulong StartAddress { get; set; }
    public uint State { get; set; }
    public uint Priority { get; set; }
}

public class PluginSectionInfo
{
    public string Name { get; set; } = "";
    public ulong VirtualAddress { get; set; }
    public uint VirtualSize { get; set; }
    public uint Characteristics { get; set; }
}

public class PluginDebugEvent
{
    public PluginDebugEventType Type { get; set; }
    public uint ProcessId { get; set; }
    public uint ThreadId { get; set; }
    public ulong Address { get; set; }
    public bool IsKernelMode { get; set; }
    public uint ExceptionCode { get; set; }
    public ulong FaultAddress { get; set; }
    public uint AccessType { get; set; }    // For AV: 0=read, 1=write, 8=execute

    /// <summary>
    /// Set by plugin in OnDebugEventFilter to control how the process is continued.
    /// 0=Run (default), 1=StepPast, 2=StepInto, 3=Handled (suppress AV + single-step).
    /// </summary>
    public uint ContinueMode { get; set; }

    /// <summary>
    /// Optional: override RIP before resuming (for IAT tracing).
    /// Set to non-zero to redirect execution. Applied via ContextRecord in kernel.
    /// </summary>
    public ulong NewRip { get; set; }

    /// <summary>
    /// Optional: override RSP before resuming. Set to non-zero to restore stack pointer.
    /// </summary>
    public ulong NewRsp { get; set; }

    /// <summary>
    /// For ContinueMode=4 (Trace): driver steps internally while RIP is in [TraceRangeBase, TraceRangeEnd).
    /// Reports SingleStep only when RIP exits range or TraceMaxSteps reached.
    /// </summary>
    public ulong TraceRangeBase { get; set; }
    public ulong TraceRangeEnd { get; set; }
    public uint TraceMaxSteps { get; set; }
}

/// <summary>
/// Script globals host for the Scripting plugin.
/// Lives in the SDK (shared assembly) to avoid AssemblyLoadContext conflicts with Roslyn.
/// </summary>
public class PluginScriptHost
{
    public IDebuggerApi api { get; set; } = null!;
    public Action<string> print { get; set; } = Console.WriteLine;
}
