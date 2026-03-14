namespace KernelFlirt.UI.Models;

/// <summary>
/// Represents a RUNTIME_FUNCTION entry from the PE .pdata section.
/// Used for x64 table-based exception handling.
/// </summary>
public class ExceptionEntry
{
    public int Index { get; set; }
    public string ModuleName { get; set; } = "";
    public ulong FunctionStart { get; set; }
    public ulong FunctionEnd { get; set; }
    public ulong UnwindInfoAddr { get; set; }
    public uint FunctionSize => (uint)(FunctionEnd - FunctionStart);
    public string? Symbol { get; set; }
    public bool HasBreakpoint { get; set; }

    public string StartHex => $"{FunctionStart:X16}";
    public string EndHex => $"{FunctionEnd:X16}";
    public string SizeHex => $"0x{FunctionSize:X}";
    public string Display => Symbol ?? StartHex;
}
