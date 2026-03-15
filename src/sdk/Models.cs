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
}

public class PluginModuleInfo
{
    public ulong BaseAddress { get; set; }
    public uint Size { get; set; }
    public string Name { get; set; } = "";
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
}
