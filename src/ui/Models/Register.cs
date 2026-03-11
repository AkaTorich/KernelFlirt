namespace KernelFlirt.UI.Models;

public class Register
{
    public string Name { get; set; } = "";
    public ulong Value { get; set; }
    public ulong PreviousValue { get; set; }
    public bool Changed => Value != PreviousValue;
    public string ValueHex => $"{Value:X16}";
}
