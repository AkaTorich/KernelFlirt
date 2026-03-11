namespace KernelFlirt.UI.Models;

public class CallStackFrame
{
    public int Index { get; set; }
    public ulong ReturnAddress { get; set; }
    public ulong StackAddress { get; set; }
    public string? ModuleName { get; set; }
    public string ReturnAddressHex => $"{ReturnAddress:X16}";
    public string StackAddressHex => $"{StackAddress:X16}";
    public string Symbol => ModuleName ?? $"{ReturnAddress:X16}";
}
