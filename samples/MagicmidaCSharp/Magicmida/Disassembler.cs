using Iced.Intel;

namespace Magicmida;

/// <summary>
/// Wrapper around Iced disassembler, replacing BeaEngine.
/// Provides a similar interface to the Delphi TDisasm/DisasmCheck pattern.
/// </summary>
public class DisasmResult
{
    public Instruction Instruction;
    public ulong VirtualAddress;
    public int Length;
    public string Mnemonic = "";
    public string FullInstruction = "";

    // Operand info
    public bool IsCallDwordPtr; // call dword ptr [addr]
    public bool IsJmpDwordPtr;  // jmp dword ptr [addr]
    public ulong MemoryDisplacement;
    public bool IsCall;     // call rel
    public bool IsJmp;      // jmp rel
    public bool IsRet;      // ret/retn
    public ulong BranchTarget;
}

public static class Disassembler
{
    public static int Bitness =>
#if CPUX86
        32;
#else
        64;
#endif

    public static unsafe DisasmResult Disassemble(byte* code, uint codeSize, ulong virtualAddress)
    {
        int len = (int)Math.Min(codeSize, 15);
        var codeArr = new byte[len];
        for (int i = 0; i < len; i++) codeArr[i] = code[i];
        var reader = new ByteArrayCodeReader(codeArr);
        var decoder = Iced.Intel.Decoder.Create(Bitness, reader);
        decoder.IP = virtualAddress;

        var instr = decoder.Decode();
        if (instr.IsInvalid)
            throw new Exception($"Disasm failed at 0x{virtualAddress:X}");

        var result = new DisasmResult
        {
            Instruction = instr,
            VirtualAddress = virtualAddress,
            Length = instr.Length,
            Mnemonic = instr.Mnemonic.ToString(),
        };

        var formatter = new NasmFormatter();
        var output = new StringOutput();
        formatter.Format(instr, output);
        result.FullInstruction = output.ToStringAndReset();

        // Analyze operands
        if (instr.FlowControl == FlowControl.IndirectCall || instr.FlowControl == FlowControl.IndirectBranch)
        {
            if (instr.Op0Kind == OpKind.Memory)
            {
                result.MemoryDisplacement = instr.MemoryDisplacement64;
#if CPUX64
                // RIP-relative addressing
                if (instr.IsIPRelativeMemoryOperand)
                    result.MemoryDisplacement = instr.IPRelativeMemoryAddress;
#endif
                if (instr.Mnemonic == Mnemonic.Call)
                    result.IsCallDwordPtr = true;
                else if (instr.Mnemonic == Mnemonic.Jmp)
                    result.IsJmpDwordPtr = true;
            }
        }

        if (instr.FlowControl == FlowControl.Call || instr.FlowControl == FlowControl.UnconditionalBranch)
        {
            if (instr.Op0Kind == OpKind.NearBranch16 || instr.Op0Kind == OpKind.NearBranch32 || instr.Op0Kind == OpKind.NearBranch64)
            {
                result.BranchTarget = instr.NearBranchTarget;
                result.IsCall = instr.Mnemonic == Mnemonic.Call;
                result.IsJmp = instr.Mnemonic == Mnemonic.Jmp;
            }
        }

        if (instr.FlowControl == FlowControl.Return)
            result.IsRet = true;

        return result;
    }
}
