namespace KernelFlirt.UI.Models;

public class ModuleInfo
{
    public ulong BaseAddress { get; set; }
    public uint Size { get; set; }
    public string Name { get; set; } = "";
    public bool Is32Bit { get; set; }
    public string BaseHex => Is32Bit ? $"{BaseAddress:X8}" : $"{BaseAddress:X16}";
    public string SizeHex => $"{Size:X8}";
    public string EndHex => Is32Bit ? $"{BaseAddress + Size:X8}" : $"{BaseAddress + Size:X16}";
}
