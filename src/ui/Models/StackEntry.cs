namespace KernelFlirt.UI.Models;

public class StackEntry
{
    public string Offset { get; set; } = "";     // e.g. "RSP+00"
    public string Address { get; set; } = "";     // e.g. "00007FFA14E77344"
    public string? Annotation { get; set; }       // e.g. "ntdll.dll+0x1234" or "\"Hello\""

    /// <summary>Fallback for ListBox plain-text scenarios (copy, etc.)</summary>
    public override string ToString() =>
        Annotation != null
            ? $"{Offset}  {Address}  {Annotation}"
            : $"{Offset}  {Address}";
}
