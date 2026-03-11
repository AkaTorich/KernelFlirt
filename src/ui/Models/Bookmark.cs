namespace KernelFlirt.UI.Models;

public class Bookmark
{
    public ulong Address { get; set; }
    public string Label { get; set; } = "";
    public string? ModuleName { get; set; }
    public string AddressHex => $"{Address:X16}";
}
