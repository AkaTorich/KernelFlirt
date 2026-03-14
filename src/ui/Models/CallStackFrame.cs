namespace KernelFlirt.UI.Models;

public class CallStackFrame
{
    public int Index { get; set; }
    public ulong ReturnAddress { get; set; }
    public ulong StackAddress { get; set; }
    public string? ModuleName { get; set; }
    public bool Is32Bit { get; set; }
    public string ReturnAddressHex => Is32Bit ? $"{ReturnAddress:X8}" : $"{ReturnAddress:X16}";
    public string StackAddressHex => Is32Bit ? $"{StackAddress:X8}" : $"{StackAddress:X16}";
    public string Symbol => ModuleName ?? (Is32Bit ? $"{ReturnAddress:X8}" : $"{ReturnAddress:X16}");
}
