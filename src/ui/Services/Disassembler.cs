using Gee.External.Capstone;
using Gee.External.Capstone.X86;
using KernelFlirt.UI.Models;

namespace KernelFlirt.UI.Services;

/// <summary>
/// Disassembler service wrapping Capstone for x86/x86-64.
/// </summary>
public class Disassembler : IDisposable
{
    private CapstoneX86Disassembler _disasm;
    private bool _is32Bit;

    public bool Is32Bit => _is32Bit;

    public Disassembler()
    {
        _disasm = CapstoneDisassembler.CreateX86Disassembler(X86DisassembleMode.Bit64);
        _disasm.EnableInstructionDetails = false;
    }

    public void SetMode(bool is32Bit)
    {
        if (_is32Bit == is32Bit) return;
        _is32Bit = is32Bit;
        _disasm.Dispose();
        _disasm = CapstoneDisassembler.CreateX86Disassembler(
            is32Bit ? X86DisassembleMode.Bit32 : X86DisassembleMode.Bit64);
        _disasm.EnableInstructionDetails = false;
    }

    public List<Instruction> Disassemble(byte[] code, ulong baseAddress, int maxInstructions = 256)
    {
        var result = new List<Instruction>();

        var instructions = _disasm.Disassemble(code, (long)baseAddress);

        int count = 0;
        foreach (var instr in instructions)
        {
            if (count >= maxInstructions) break;

            result.Add(new Instruction
            {
                Address = (ulong)instr.Address,
                Bytes = instr.Bytes,
                Mnemonic = instr.Mnemonic,
                Operands = instr.Operand,
                Size = instr.Bytes.Length,
                Is32Bit = _is32Bit
            });
            count++;
        }

        return result;
    }

    public void Dispose()
    {
        _disasm.Dispose();
        GC.SuppressFinalize(this);
    }
}
