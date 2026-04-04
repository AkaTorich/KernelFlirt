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

    /// <summary>Resolved symbol for call/jmp target (e.g. "kernel32!CreateFileW"). Replaces address in operands.</summary>
    public string? ResolvedSymbol { get; set; }

    /// <summary>Operands with symbol name substituted for address.</summary>
    public string DisplayOperands =>
        ResolvedSymbol != null ? ResolvedSymbol : Operands;

    /// <summary>Mnemonic + display operands.</summary>
    public string Text => string.IsNullOrEmpty(DisplayOperands) ? Mnemonic : $"{Mnemonic} {DisplayOperands}";
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
        // Find the module containing this function — only graph code within this module
        var modules = _api.Symbols.GetModules();
        ulong modBase = 0, modEnd = 0;
        if (modules != null)
        {
            foreach (var mod in modules)
            {
                if (functionAddress >= mod.BaseAddress && functionAddress < mod.BaseAddress + mod.Size)
                {
                    modBase = mod.BaseAddress;
                    modEnd = mod.BaseAddress + mod.Size;
                    break;
                }
            }
        }

        // Read function code from memory
        var code = _api.Memory.ReadMemory(_api.TargetPid, functionAddress, maxSize);
        if (code == null || code.Length == 0) return [];

        // Disassemble all instructions
        var instructions = Disassemble(code, functionAddress);
        if (instructions.Count == 0) return [];

        // Resolve symbols for call/jmp targets
        ResolveSymbols(instructions);

        // Find function boundaries (stop at RET or padding)
        var funcInstrs = TrimToFunction(instructions);
        if (funcInstrs.Count == 0) return [];

        // Identify basic block leaders (split points)
        // Only consider branch targets within the disassembled range (not external modules)
        var leaders = FindLeaders(funcInstrs, functionAddress, modBase, modEnd);

        // Build basic blocks
        var blocks = BuildBlocks(funcInstrs, leaders);

        // Remove blocks that are outside the function (IAT thunks, library stubs)
        // Keep only blocks reachable from the entry point
        blocks = FilterReachableBlocks(blocks, functionAddress);

        // Compute edges between blocks
        ComputeEdges(blocks, modBase, modEnd);

        return blocks;
    }

    /// <summary>
    /// Resolve symbol names for branch/call targets.
    /// Handles direct calls, RIP-relative indirect (IAT), and thunk following.
    /// </summary>
    private void ResolveSymbols(List<CfgInstruction> instructions)
    {
        foreach (var instr in instructions)
        {
            if (!instr.IsCall && !instr.IsBranch) continue;

            ulong targetAddr = instr.BranchTarget;

            // Case 1: RIP-relative indirect (call [rip+0x...]) — read IAT pointer
            if (targetAddr == 0 && !string.IsNullOrEmpty(instr.Operands))
                targetAddr = TryResolveRipRelative(instr);

            // Case 2: Absolute indirect (call dword ptr [0x...]) — x86 IAT
            if (targetAddr == 0 && !string.IsNullOrEmpty(instr.Operands))
                targetAddr = TryResolveAbsoluteIndirect(instr);

            if (targetAddr == 0) continue;

            var sym = _api.Symbols.ResolveAddress(targetAddr);

            // Case 3: If target is a thunk (jmp [rip+xxx]), follow it to the real API
            if (sym == null || (sym != null && !sym.Contains('!') && !sym.Contains('+') ))
            {
                var realTarget = TryFollowThunk(targetAddr);
                if (realTarget != 0)
                {
                    var realSym = _api.Symbols.ResolveAddress(realTarget);
                    if (!string.IsNullOrEmpty(realSym))
                        sym = realSym;
                }
            }

            // Format as MODULE!Function if target is in a different module
            if (!string.IsNullOrEmpty(sym) && !sym.Contains('!'))
            {
                var targetModule = FindModuleName(targetAddr);
                if (targetModule != null)
                    sym = $"{targetModule}!{sym}";
            }

            if (!string.IsNullOrEmpty(sym))
                instr.ResolvedSymbol = sym;
        }
    }

    /// <summary>
    /// Find which module an address belongs to. Returns module name or null.
    /// </summary>
    private string? FindModuleName(ulong address)
    {
        var modules = _api.Symbols.GetModules();
        if (modules == null) return null;
        foreach (var mod in modules)
        {
            if (address >= mod.BaseAddress && address < mod.BaseAddress + mod.Size)
                return mod.Name;
        }
        return null;
    }

    /// <summary>
    /// If the target address is a JMP thunk (jmp [rip+xxx] or jmp [addr]),
    /// follow it to get the real API address.
    /// </summary>
    private ulong TryFollowThunk(ulong addr)
    {
        var bytes = _api.Memory.ReadMemory(_api.TargetPid, addr, 8);
        if (bytes == null || bytes.Length < 6) return 0;

        int offset = 0;

        // Skip REX prefix (48) if present
        if (bytes[0] == 0x48) offset = 1;

        if (offset + 6 > bytes.Length) return 0;

        // FF 25 xx xx xx xx = jmp [rip + disp32] (x64 IAT thunk)
        // 48 FF 25 xx xx xx xx = same with REX.W
        if (bytes[offset] == 0xFF && bytes[offset + 1] == 0x25)
        {
            int instrLen = offset + 6;
            int disp = BitConverter.ToInt32(bytes, offset + 2);

            if (_api.Is32Bit)
            {
                // x86: FF 25 [abs32] — disp is absolute address
                uint iatAddr = (uint)disp;
                var ptrData = _api.Memory.ReadMemory(_api.TargetPid, iatAddr, 4);
                if (ptrData == null) return 0;
                return BitConverter.ToUInt32(ptrData);
            }
            else
            {
                // x64: FF 25 [rip+disp32] — disp is relative to next instruction
                ulong iatAddr = addr + (ulong)instrLen + (ulong)disp;
                var ptrData = _api.Memory.ReadMemory(_api.TargetPid, iatAddr, 8);
                if (ptrData == null) return 0;
                return BitConverter.ToUInt64(ptrData);
            }
        }

        // E9 xx xx xx xx = jmp rel32 (relative jump thunk)
        if (bytes[0] == 0xE9 && bytes.Length >= 5)
        {
            int rel = BitConverter.ToInt32(bytes, 1);
            return addr + 5 + (ulong)rel;
        }

        return 0;
    }

    /// <summary>
    /// Try to resolve absolute indirect: call/jmp dword ptr [0x12345678] (x86 IAT)
    /// </summary>
    private ulong TryResolveAbsoluteIndirect(CfgInstruction instr)
    {
        if (!_api.Is32Bit) return 0;
        var ops = instr.Operands;
        // Match: dword ptr [0x...]
        int bracketStart = ops.IndexOf('[');
        int bracketEnd = ops.IndexOf(']');
        if (bracketStart < 0 || bracketEnd < 0) return 0;
        var inner = ops[(bracketStart + 1)..bracketEnd].Trim();
        if (inner.Contains("rip") || inner.Contains('+') || inner.Contains('-'))
            return 0; // not a simple absolute address

        if (inner.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            inner = inner[2..];
        if (!ulong.TryParse(inner, NumberStyles.HexNumber, null, out ulong iatAddr))
            return 0;

        var ptrData = _api.Memory.ReadMemory(_api.TargetPid, iatAddr, 4);
        if (ptrData == null) return 0;
        return BitConverter.ToUInt32(ptrData);
    }

    /// <summary>
    /// Try to resolve a RIP-relative indirect operand like [rip + 0x1234].
    /// Reads the pointer at the effective address and resolves the pointed-to function.
    /// </summary>
    private ulong TryResolveRipRelative(CfgInstruction instr)
    {
        var ops = instr.Operands;
        int ripIdx = ops.IndexOf("rip", StringComparison.OrdinalIgnoreCase);
        if (ripIdx < 0) return 0;

        int bracketEnd = ops.IndexOf(']', ripIdx);
        if (bracketEnd < 0) return 0;

        var inner = ops[(ripIdx + 3)..bracketEnd].Trim();
        if (inner.Length < 2) return 0;

        char sign = inner[0];
        if (sign != '+' && sign != '-') return 0;

        var hexStr = inner[1..].Trim();
        if (hexStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            hexStr = hexStr[2..];

        if (!ulong.TryParse(hexStr, NumberStyles.HexNumber, null, out ulong offset))
            return 0;

        // Effective address = RIP (after instruction) +/- offset
        ulong effectiveAddr = sign == '+'
            ? instr.Address + (ulong)instr.Size + offset
            : instr.Address + (ulong)instr.Size - offset;

        // Read the pointer at that address (IAT entry → actual function)
        int ptrSize = _api.Is32Bit ? 4 : 8;
        var ptrData = _api.Memory.ReadMemory(_api.TargetPid, effectiveAddr, (uint)ptrSize);
        if (ptrData == null) return 0;

        return ptrSize == 8 ? BitConverter.ToUInt64(ptrData) : BitConverter.ToUInt32(ptrData);
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

        // Parse branch/call target from operands
        if ((instr.IsBranch || instr.IsCall) && !string.IsNullOrEmpty(instr.Operands))
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
    private static HashSet<ulong> FindLeaders(List<CfgInstruction> instructions, ulong entryPoint,
        ulong modBase, ulong modEnd)
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

                // Branch target is a leader — but ONLY if it's within our module
                if (instr.BranchTarget != 0 && instrAddresses.Contains(instr.BranchTarget))
                {
                    bool inModule = modEnd == 0 || (instr.BranchTarget >= modBase && instr.BranchTarget < modEnd);
                    if (inModule)
                        leaders.Add(instr.BranchTarget);
                }
            }
        }

        return leaders;
    }

    /// <summary>
    /// Keep only blocks reachable from the entry point via internal edges.
    /// This removes IAT thunks and library stubs that happen to be in the read buffer.
    /// </summary>
    private static List<BasicBlock> FilterReachableBlocks(List<BasicBlock> blocks, ulong entryPoint)
    {
        // Identify thunk/stub blocks that should be excluded from the graph
        var thunkAddrs = new HashSet<ulong>();
        foreach (var b in blocks)
        {
            // Single jmp [indirect] — IAT thunk
            if (b.Instructions.Count == 1)
            {
                var instr = b.Instructions[0];
                if (instr.IsUnconditionalJmp)
                    thunkAddrs.Add(b.StartAddress);
            }
            // Short stub (1-3 instructions) ending with jmp, with resolved external symbol
            if (b.Instructions.Count <= 3)
            {
                var last = b.Instructions[^1];
                if (last.IsUnconditionalJmp && last.ResolvedSymbol != null && last.ResolvedSymbol.Contains('!'))
                    thunkAddrs.Add(b.StartAddress);
            }
        }

        var blockByAddr = new Dictionary<ulong, BasicBlock>();
        foreach (var b in blocks)
            blockByAddr[b.StartAddress] = b;

        // BFS from entry point, skipping thunks
        var reachable = new HashSet<ulong>();
        var queue = new Queue<ulong>();
        queue.Enqueue(entryPoint);
        reachable.Add(entryPoint);

        while (queue.Count > 0)
        {
            var addr = queue.Dequeue();
            if (!blockByAddr.TryGetValue(addr, out var block)) continue;
            if (thunkAddrs.Contains(addr)) continue; // don't traverse into thunks

            var lastInstr = block.Instructions[^1];

            // Fall-through to next block (if not unconditional jmp or ret)
            if (!lastInstr.IsRet && !lastInstr.IsUnconditionalJmp)
            {
                var nextAddr = lastInstr.Address + (ulong)lastInstr.Size;
                if (blockByAddr.ContainsKey(nextAddr) && !thunkAddrs.Contains(nextAddr)
                    && reachable.Add(nextAddr))
                    queue.Enqueue(nextAddr);
            }

            // Branch targets (jmp/jcc within function) — skip thunks
            if (lastInstr.IsBranch && lastInstr.BranchTarget != 0)
            {
                if (blockByAddr.ContainsKey(lastInstr.BranchTarget)
                    && !thunkAddrs.Contains(lastInstr.BranchTarget)
                    && reachable.Add(lastInstr.BranchTarget))
                    queue.Enqueue(lastInstr.BranchTarget);
            }
        }

        return blocks.Where(b => reachable.Contains(b.StartAddress)).ToList();
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
    private static void ComputeEdges(List<BasicBlock> blocks, ulong modBase = 0, ulong modEnd = 0)
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
