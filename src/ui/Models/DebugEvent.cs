namespace KernelFlirt.UI.Models;

public enum DebugEventType
{
    Breakpoint = 1,
    SingleStep = 2,
    HwBreakpoint = 3,
    HwWatchpoint = 4,
    MemoryBp = 5,
    AccessViolation = 6
}

public class DebugEventRegisters
{
    public ulong Rax, Rbx, Rcx, Rdx;
    public ulong Rsi, Rdi, Rbp, Rsp;
    public ulong R8, R9, R10, R11, R12, R13, R14, R15;
    public ulong Rip;
    public ulong Rflags;
}

public class DebugEvent
{
    public DebugEventType Type { get; set; }
    public uint ProcessId { get; set; }
    public uint ThreadId { get; set; }
    public ulong Address { get; set; }
    public bool IsKernelMode { get; set; }
    public uint ExceptionCode { get; set; }
    public ulong FaultAddress { get; set; }
    public DebugEventRegisters? Registers { get; set; }

    public string TypeName => Type switch
    {
        DebugEventType.Breakpoint => "INT3",
        DebugEventType.SingleStep => "Step",
        DebugEventType.HwBreakpoint => "HW BP",
        DebugEventType.HwWatchpoint => "HW Watch",
        DebugEventType.MemoryBp => "Mem BP",
        DebugEventType.AccessViolation => "Access Violation",
        _ => "Unknown"
    };

    public bool Is32Bit { get; set; }
    public string AddressHex => Is32Bit ? $"{Address:X8}" : $"{Address:X16}";
}
