namespace KernelFlirt.UI.Models;

public class SehEntry
{
    public int Index { get; set; }
    public ulong HandlerAddress { get; set; }
    public ulong NextRecord { get; set; }
    public string? ModuleName { get; set; }
    public string HandlerHex => $"{HandlerAddress:X16}";
    public string NextHex => NextRecord == ulong.MaxValue ? "FFFFFFFFFFFFFFFF (end)" : $"{NextRecord:X16}";
    public string Symbol => ModuleName ?? $"{HandlerAddress:X16}";
}
