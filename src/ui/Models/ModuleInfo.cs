namespace KernelFlirt.UI.Models;

public class ModuleInfo
{
    public ulong BaseAddress { get; set; }
    public uint Size { get; set; }
    public string Name { get; set; } = "";
    public string BaseHex => $"{BaseAddress:X16}";
    public string SizeHex => $"{Size:X8}";
    public string EndHex => $"{BaseAddress + Size:X16}";
}
