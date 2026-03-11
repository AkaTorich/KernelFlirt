namespace KernelFlirt.UI.Models;

public enum DebugEventType
{
    Breakpoint = 1,
    SingleStep = 2,
    HwBreakpoint = 3,
    HwWatchpoint = 4,
    MemoryBp = 5
}

public class DebugEvent
{
    public DebugEventType Type { get; set; }
    public uint ProcessId { get; set; }
    public uint ThreadId { get; set; }
    public ulong Address { get; set; }
    public bool IsKernelMode { get; set; }

    public string TypeName => Type switch
    {
        DebugEventType.Breakpoint => "INT3",
        DebugEventType.SingleStep => "Step",
        DebugEventType.HwBreakpoint => "HW BP",
        DebugEventType.HwWatchpoint => "HW Watch",
        DebugEventType.MemoryBp => "Mem BP",
        _ => "Unknown"
    };

    public string AddressHex => $"{Address:X16}";
}
