namespace KernelFlirt.UI.Models;

public enum BreakpointType
{
    Software = 0,
    Hardware = 1,
    HwWrite = 2,
    HwReadWrite = 3,
    Memory = 4
}

public class Breakpoint
{
    public uint Handle { get; set; }
    public ulong Address { get; set; }
    public BreakpointType Type { get; set; }
    public byte OriginalByte { get; set; }  // Original byte before 0xCC (SW BP only)
    public bool Enabled { get; set; } = true;
    public string? ModuleName { get; set; }
    public string? Condition { get; set; }     // e.g. "RAX==0" or "RCX!=0"
    public string? LogExpression { get; set; }  // log without breaking
    public uint HitCount { get; set; }
    public bool Is32Bit { get; set; }
    public string AddressHex => Is32Bit ? $"{Address:X8}" : $"{Address:X16}";
    public string TypeName => Type switch
    {
        BreakpointType.Software => "SW",
        BreakpointType.Hardware => "HW Exec",
        BreakpointType.HwWrite => "HW Write",
        BreakpointType.HwReadWrite => "HW R/W",
        BreakpointType.Memory => "Memory",
        _ => "?"
    };
    public bool IsConditional => !string.IsNullOrEmpty(Condition);
    public string StatusText => Enabled ? (IsConditional ? "Cond" : "On") : "Off";
}
