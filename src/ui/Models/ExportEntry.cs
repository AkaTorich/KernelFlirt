namespace KernelFlirt.UI.Models;

public class ExportEntry
{
    public string Module { get; set; } = "";
    public string Function { get; set; } = "";
    public ushort Ordinal { get; set; }
    public ulong Address { get; set; }
    public bool HasBreakpoint { get; set; }
    public string AddressHex => $"{Address:X16}";
    public string Display => string.IsNullOrEmpty(Function) ? $"#{Ordinal}" : Function;
}
