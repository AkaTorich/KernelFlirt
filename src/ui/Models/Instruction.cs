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

    /// <summary>Symbol comment shown to the right of operands (e.g. "ntdll!NtDeviceIoControlFile").</summary>
    public string? Comment { get; set; }

    /// <summary>Symbol name for the address column (e.g. "nt!KiSystemCall64" at function entry).</summary>
    public string? AddressLabel { get; set; }

    /// <summary>Resolved target address for branch instructions (call/jmp/jcc).</summary>
    public ulong BranchTargetAddress { get; set; }

    /// <summary>Symbol name for the branch target (replaces hex address in operands display).</summary>
    public string? BranchTargetSymbol { get; set; }

    public bool Is32Bit { get; set; }
    public string AddressHex => Is32Bit ? $"{Address:X8}" : $"{Address:X16}";
    public string BytesHex => BitConverter.ToString(Bytes).Replace("-", " ");
    public string FullText => string.IsNullOrEmpty(Operands) ? Mnemonic : $"{Mnemonic} {Operands}";
}
