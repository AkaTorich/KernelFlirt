namespace KernelFlirt.UI.Models;

public class Instruction
{
    public ulong Address { get; set; }
    public byte[] Bytes { get; set; } = [];
    public string Mnemonic { get; set; } = "";
    public string Operands { get; set; } = "";
    public int Size { get; set; }
    public bool HasBreakpoint { get; set; }
    public bool IsCurrentInstruction { get; set; }

    public string AddressHex => $"{Address:X16}";
    public string BytesHex => BitConverter.ToString(Bytes).Replace("-", " ");
    public string FullText => string.IsNullOrEmpty(Operands) ? Mnemonic : $"{Mnemonic} {Operands}";
}
