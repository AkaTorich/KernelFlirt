namespace KernelFlirt.UI.Models;

public class ImportEntry
{
    public string Module { get; set; } = "";
    public string Function { get; set; } = "";
    public ushort Ordinal { get; set; }
    public ulong IatAddress { get; set; }
    public ulong ResolvedAddress { get; set; }
    public string IatHex => $"{IatAddress:X16}";
    public string ResolvedHex => $"{ResolvedAddress:X16}";
    public string Display => Ordinal != 0 && string.IsNullOrEmpty(Function)
        ? $"#{Ordinal}"
        : Function;
}
