namespace KernelFlirt.UI.Models;

public class KernelModuleInfo
{
    public ulong BaseAddress { get; set; }
    public uint Size { get; set; }
    public ushort LoadOrder { get; set; }
    public string Name { get; set; } = "";
    public string BaseHex => $"{BaseAddress:X16}";
    public string SizeHex => $"{Size:X8}";
}
