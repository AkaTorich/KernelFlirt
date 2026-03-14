namespace KernelFlirt.UI.Models;

public class SearchResult
{
    public ulong Address { get; set; }
    public string? ModuleName { get; set; }
    public string Preview { get; set; } = "";
    public bool HasBreakpoint { get; set; }
    public bool Is32Bit { get; set; }
    public string AddressHex => Is32Bit ? $"{Address:X8}" : $"{Address:X16}";
}
