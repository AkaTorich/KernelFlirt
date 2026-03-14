namespace KernelFlirt.UI.Models;

public class Register
{
    public string Name { get; set; } = "";
    public ulong Value { get; set; }
    public ulong PreviousValue { get; set; }
    public bool IsFlag { get; set; }
    public bool Is32Bit { get; set; }
    public bool Changed => Value != PreviousValue;
    public string ValueHex => IsFlag ? $"{Value}" : (Is32Bit ? $"{Value:X8}" : $"{Value:X16}");

    private static readonly string[] FlagNames = ["CF", "PF", "AF", "ZF", "SF", "TF", "IF", "DF", "OF"];
    private static readonly int[] FlagBits     = [  0,    2,    4,    6,    7,    8,    9,   10,   11 ];

    public static List<Register> ExpandFlags(ulong rflags)
    {
        var list = new List<Register>(FlagNames.Length);
        for (int i = 0; i < FlagNames.Length; i++)
        {
            list.Add(new Register
            {
                Name = FlagNames[i],
                Value = (rflags >> FlagBits[i]) & 1,
                IsFlag = true,
            });
        }
        return list;
    }
}
