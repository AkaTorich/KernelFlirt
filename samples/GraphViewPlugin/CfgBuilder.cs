using System.Globalization;
using Gee.External.Capstone;
using Gee.External.Capstone.X86;
using KernelFlirt.SDK;

namespace GraphViewPlugin;

/// <summary>
/// A single disassembled instruction with branch info.
/// </summary>
public sealed class CfgInstruction
{
    public ulong Address { get; init; }
    public int Size { get; init; }
    public string Mnemonic { get; init; } = "";
    public string Operands { get; init; } = "";
    public byte[] Bytes { get; init; } = [];

    public bool IsBranch { get; set; }
    public bool IsConditional { get; set; }
    public bool IsCall { get; set; }
    public bool IsRet { get; set; }
    public bool IsUnconditionalJmp { get; set; }
    public ulong BranchTarget { get; set; }

    public string Text => string.IsNullOrEmpty(Operands) ? Mnemonic : $"{Mnemonic} {Operands}";
    public string AddressHex(bool is32) => is32 ? $"{Address:X8}" : $"{Address:X16}";
}

/// <summary>
/// A basic block: a sequence of instructions with a single entry and single exit.
/// </summary>
public sealed class BasicBlock
{
    public ulong StartAddress { get; init; }
    public ulong EndAddress => Instructions.Count > 0
        ? Instructions[^1].Address + (ulong)Instructions[^1].Size
        : StartAddress;
    public List<CfgInstruction> Instructions { get; } = new();

    /// <summary>Successor block addresses (fall-through and/or branch target).</summary>
    public List<ulong> Successors { get; } = new();

    /// <summary>Edge labels: true = conditional taken, false = fall-through, null = unconditional.</summary>
    public List<bool?> EdgeTypes { get; } = new();
}

/// <summary>
/// Builds a control flow graph from a function in memory.
/// Disassembles the function, splits into basic blocks, and computes edges.
/// </summary>
public sealed class CfgBuilder
{
    private readonly IDebuggerApi _api;

    public CfgBuilder(IDebuggerApi api)
    {
        _api = api;
    }

    /// <summary>
    /// Build a CFG starting at the given function address.
    /// Disassembles until RET or max size, splits into basic blocks.
    /// </summary>
    public List<BasicBlock> Build(ulong functionAddress, uint maxSize = 0x2000)
    {
        // Read function code from memory
        var code = _api.Memory.ReadMemory(_api.TargetPid, functionAddress, maxSize);
        if (code == null || code.Length == 0) return [];

        // Disassemble all instructions
        var instructions = Disassemble(code, functionAddress);
        if (instructions.Count == 0) return [];

        // Find function boundaries (stop at RET or padding)
        var funcInstrs = TrimToFunction(instructions);
        if (funcInstrs.Count == 0) return [];

        // Identify basic block leaders (split points)
        var leaders = FindLeaders(funcInstrs, functionAddress);

        // Build basic blocks
        var blocks = BuildBlocks(funcInstrs, leaders);

        // Compute edges between blocks
        ComputeEdges(blocks);

        return blocks;
    }

    private List<CfgInstruction> Disassemble(byte[] code, ulong baseAddress)
    {
        var mode = _api.Is32Bit ? X86DisassembleMode.Bit32 : X86DisassembleMode.Bit64;
        using var disasm = CapstoneDisassembler.CreateX86Disassembler(mode);
        disasm.EnableInstructionDetails = false;

        var result = new List<CfgInstruction>();
        var capstoneInstrs = disasm.Disassemble(code, (long)baseAddress);

        foreach (var instr in capstoneInstrs)
        {
            var ci = new CfgInstruction
            {
                Address = (ulong)instr.Address,
                Size = instr.Bytes.Length,
                Mnemonic = instr.Mnemonic,
                Operands = instr.Operand,
                Bytes = instr.Bytes
            };

            ClassifyInstruction(ci);
            result.Add(ci);
        }

        return result;
    }

    private static void ClassifyInstruction(CfgInstruction instr)
    {
        var m = instr.Mnemonic.ToLowerInvariant();

        instr.IsCall = m == "call";
        instr.IsRet = m is "ret" or "retn" or "retf";
        instr.IsUnconditionalJmp = m == "jmp";

        instr.IsConditional = m.StartsWith('j') && m != "jmp" && m != "jmpe";

        instr.IsBranch = instr.IsConditional || instr.IsUnconditionalJmp;

        // Parse branch target from operands
        if (instr.IsBranch && !string.IsNullOrEmpty(instr.Operands))
        {
            instr.BranchTarget = ParseBranchTarget(instr);
        }
    }

    private static ulong ParseBranchTarget(CfgInstruction instr)
    {
        var ops = instr.Operands.Trim();

        // Direct address: "0x7ff612340" or "0x401000"
        if (ops.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (ulong.TryParse(ops[2..], NumberStyles.HexNumber, null, out ulong addr))
                return addr;
        }

        // Plain hex without 0x prefix (some Capstone versions)
        if (ulong.TryParse(ops, NumberStyles.HexNumber, null, out ulong addr2))
            return addr2;

        // Indirect jumps (jmp [rax], jmp qword ptr [...]) — can't resolve statically
        return 0;
    }

    /// <summary>
    /// Trim instructions to the function body.
    /// Stop at the first RET that's not followed by reachable code, or at INT3/NOP padding.
    /// </summary>
    private static List<CfgInstruction> TrimToFunction(List<CfgInstruction> instructions)
    {
        // Collect all branch targets to know what's reachable
        var branchTargets = new HashSet<ulong>();
        foreach (var instr in instructions)
        {
            if (instr.IsBranch && instr.BranchTarget != 0)
                branchTargets.Add(instr.BranchTarget);
        }

        var result = new List<CfgInstruction>();
        bool foundRet = false;
        int paddingCount = 0;

        foreach (var instr in instructions)
        {
            // If we found RET and the next instruction is not a branch target, check for padding
            if (foundRet)
            {
                if (branchTargets.Contains(instr.Address))
                {
                    // This code is reachable — continue (e.g., multiple return paths)
                    foundRet = false;
                    paddingCount = 0;
                }
                else if (instr.Mnemonic is "int3" or "nop" or "cc")
                {
                    paddingCount++;
                    if (paddingCount >= 2) break; // padding after function
                    continue;
                }
                else
                {
                    // Non-padding, non-targeted code after RET — might be error handling
                    // Include it if it's within a reasonable distance
                    if (instr.Address - result[^1].Address > 32) break;
                    foundRet = false;
                    paddingCount = 0;
                }
            }

            result.Add(instr);

            if (instr.IsRet)
                foundRet = true;
        }

        return result;
    }

    /// <summary>
    /// Find basic block leaders (first instruction of each block).
    /// Leaders are: function entry, branch targets, instructions after branches.
    /// </summary>
    private static HashSet<ulong> FindLeaders(List<CfgInstruction> instructions, ulong entryPoint)
    {
        var leaders = new HashSet<ulong> { entryPoint };
        var instrAddresses = new HashSet<ulong>(instructions.Select(i => i.Address));

        foreach (var instr in instructions)
        {
            if (instr.IsBranch || instr.IsRet)
            {
                // Instruction after the branch is a leader (fall-through target)
                var nextAddr = instr.Address + (ulong)instr.Size;
                if (instrAddresses.Contains(nextAddr))
                    leaders.Add(nextAddr);

                // Branch target is a leader
                if (instr.BranchTarget != 0 && instrAddresses.Contains(instr.BranchTarget))
                    leaders.Add(instr.BranchTarget);
            }
        }

        return leaders;
    }

    /// <summary>
    /// Split instructions into basic blocks at leader boundaries.
    /// </summary>
    private static List<BasicBlock> BuildBlocks(List<CfgInstruction> instructions, HashSet<ulong> leaders)
    {
        var blocks = new List<BasicBlock>();
        BasicBlock? current = null;

        foreach (var instr in instructions)
        {
            if (leaders.Contains(instr.Address))
            {
                current = new BasicBlock { StartAddress = instr.Address };
                blocks.Add(current);
            }

            current?.Instructions.Add(instr);
        }

        return blocks;
    }

    /// <summary>
    /// Compute successor edges for each basic block.
    /// </summary>
    private static void ComputeEdges(List<BasicBlock> blocks)
    {
        var blockByAddr = new Dictionary<ulong, BasicBlock>();
        foreach (var b in blocks)
            blockByAddr[b.StartAddress] = b;

        for (int i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            if (block.Instructions.Count == 0) continue;

            var lastInstr = block.Instructions[^1];

            if (lastInstr.IsRet)
            {
                // No successors — function return
                continue;
            }

            if (lastInstr.IsUnconditionalJmp)
            {
                // Single successor: branch target
                if (lastInstr.BranchTarget != 0 && blockByAddr.ContainsKey(lastInstr.BranchTarget))
                {
                    block.Successors.Add(lastInstr.BranchTarget);
                    block.EdgeTypes.Add(null); // unconditional
                }
                continue;
            }

            if (lastInstr.IsConditional)
            {
                // Two successors: fall-through (false) and branch target (true)
                var fallThrough = lastInstr.Address + (ulong)lastInstr.Size;
                if (blockByAddr.ContainsKey(fallThrough))
                {
                    block.Successors.Add(fallThrough);
                    block.EdgeTypes.Add(false); // false branch (fall-through)
                }
                if (lastInstr.BranchTarget != 0 && blockByAddr.ContainsKey(lastInstr.BranchTarget))
                {
                    block.Successors.Add(lastInstr.BranchTarget);
                    block.EdgeTypes.Add(true); // true branch (taken)
                }
                continue;
            }

            // Non-branch instruction at end of block — fall through to next block
            if (i + 1 < blocks.Count)
            {
                var nextBlock = blocks[i + 1];
                block.Successors.Add(nextBlock.StartAddress);
                block.EdgeTypes.Add(null); // fall-through
            }
        }
    }
}
