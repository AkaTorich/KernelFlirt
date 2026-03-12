namespace KernelFlirt.UI.Models;

public class FunctionEntry
{
    public string Name { get; set; } = "";
    public ulong Address { get; set; }
    public uint Size { get; set; }
    public string AddressHex => $"{Address:X16}";
    public string SizeHex => Size > 0 ? $"0x{Size:X}" : "";
}
