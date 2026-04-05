using System.Globalization;
using Gee.External.Capstone;
using Gee.External.Capstone.X86;
using KernelFlirt.SDK;

namespace XrefsPlugin;

/// <summary>
/// Type of cross-reference.
/// </summary>
public enum XrefType
{
    Call,       // CALL target
    Jump,       // JMP/Jcc target
    DataRead,   // MOV reg, [addr] / LEA reg, [addr]
    DataWrite,  // MOV [addr], reg
    Unknown
}

/// <summary>
/// A single cross-reference result.
/// </summary>
public sealed class XrefResult
{
    public ulong FromAddress { get; init; }
    public ulong ToAddress { get; init; }
    public XrefType Type { get; init; }
    public string Instruction { get; init; } = "";
    public string FromSymbol { get; set; } = "";
    public string FromModule { get; set; } = "";

    // Display helpers for DataGrid
    public string FromHex => $"{FromAddress:X16}";
    public string ToHex => $"{ToAddress:X16}";
    public string TypeStr => Type.ToString();
    public string Location => string.IsNullOrEmpty(FromSymbol) ? FromModule : FromSymbol;
}

/// <summary>
/// Scans modules for cross-references to a target address.
/// Finds code xrefs (CALL/JMP) and data xrefs (LEA/MOV with address).
/// </summary>
public sealed class XrefScanner
{
    private readonly IDebuggerApi _api;

    public XrefScanner(IDebuggerApi api) => _api = api;

    /// <summary>
    /// Find all xrefs TO the given target address within the specified module.
    /// Disassembles the module's executable sections looking for references.
    /// </summary>
    public List<XrefResult> FindXrefsTo(ulong targetAddr, PluginModuleInfo module,
        Action<string>? onStatus = null, CancellationToken ct = default)
    {
        var results = new List<XrefResult>();
        var ptrSize = _api.Is32Bit ? 4 : 8;

        // Scan the module in chunks
        const uint chunkSize = 0x10000; // 64KB
        ulong scanEnd = module.BaseAddress + module.Size;

        for (ulong addr = module.BaseAddress; addr < scanEnd && !ct.IsCancellationRequested;)
        {
            uint readSize = (uint)Math.Min(chunkSize, scanEnd - addr);
            var code = _api.Memory.ReadMemory(_api.TargetPid, addr, readSize);
            if (code == null || code.Length == 0) { addr += readSize; continue; }

            onStatus?.Invoke($"Scanning {module.Name} 0x{addr:X}...");

            // 1) Disassemble and look for code xrefs (CALL/JMP to target)
            ScanCodeXrefs(code, addr, targetAddr, results, ct);

            // 2) Scan for raw pointer (data xref) — target address as little-endian bytes
            ScanPointerXrefs(code, addr, targetAddr, ptrSize, results);

            addr += readSize;
        }

        return results;
    }

    /// <summary>
    /// Find all xrefs FROM the given address (what does this function call/reference?).
    /// Disassembles the function and collects outgoing references.
    /// </summary>
    public List<XrefResult> FindXrefsFrom(ulong funcAddr,
        Action<string>? onStatus = null, CancellationToken ct = default)
    {
        var results = new List<XrefResult>();

        onStatus?.Invoke($"Disassembling function at 0x{funcAddr:X}...");

        var code = _api.Memory.ReadMemory(_api.TargetPid, funcAddr, 0x2000);
        if (code == null || code.Length == 0) return results;

        var mode = _api.Is32Bit ? X86DisassembleMode.Bit32 : X86DisassembleMode.Bit64;
        using var disasm = CapstoneDisassembler.CreateX86Disassembler(mode);
        disasm.EnableInstructionDetails = false;

        var instructions = disasm.Disassemble(code, (long)funcAddr);
        bool pastRet = false;
        int paddingAfterRet = 0;

        foreach (var instr in instructions)
        {
            if (ct.IsCancellationRequested) break;

            var m = instr.Mnemonic.ToLowerInvariant();

            // Stop at function end
            if (pastRet)
            {
                if (m is "int3" or "nop" or "cc") { paddingAfterRet++; if (paddingAfterRet >= 2) break; continue; }
                pastRet = false;
                paddingAfterRet = 0;
            }
            if (m is "ret" or "retn" or "retf") { pastRet = true; continue; }

            bool isCall = m == "call";
            bool isBranch = m.StartsWith('j');
            bool isLea = m == "lea";

            if (!isCall && !isBranch && !isLea) continue;

            ulong target = ParseTarget(instr, (ulong)instr.Address, instr.Bytes.Length);
            if (target == 0) continue;

            // For LEA, resolve RIP-relative
            if (isLea)
            {
                target = TryResolveRipRelativeLea(instr);
                if (target == 0) continue;
            }

            var xtype = isCall ? XrefType.Call : isBranch ? XrefType.Jump : XrefType.DataRead;

            results.Add(new XrefResult
            {
                FromAddress = (ulong)instr.Address,
                ToAddress = target,
                Type = xtype,
                Instruction = $"{instr.Mnemonic} {instr.Operand}"
            });
        }

        // Resolve symbols for targets
        foreach (var xr in results)
        {
            var sym = _api.Symbols.ResolveAddress(xr.ToAddress);
            if (sym != null) xr.FromSymbol = sym; // reuse field: for "from" analysis this is the target symbol
        }

        return results;
    }

    private void ScanCodeXrefs(byte[] code, ulong baseAddr, ulong targetAddr,
        List<XrefResult> results, CancellationToken ct)
    {
        var mode = _api.Is32Bit ? X86DisassembleMode.Bit32 : X86DisassembleMode.Bit64;
        using var disasm = CapstoneDisassembler.CreateX86Disassembler(mode);
        disasm.EnableInstructionDetails = false;

        var instructions = disasm.Disassemble(code, (long)baseAddr);

        foreach (var instr in instructions)
        {
            if (ct.IsCancellationRequested) break;

            var m = instr.Mnemonic.ToLowerInvariant();
            bool isCall = m == "call";
            bool isBranch = m.StartsWith('j');
            bool isLea = m == "lea";
            bool isMov = m == "mov";

            if (!isCall && !isBranch && !isLea && !isMov) continue;

            ulong instrAddr = (ulong)instr.Address;

            if (isCall || isBranch)
            {
                ulong target = ParseTarget(instr, instrAddr, instr.Bytes.Length);
                if (target == targetAddr)
                {
                    results.Add(new XrefResult
                    {
                        FromAddress = instrAddr,
                        ToAddress = targetAddr,
                        Type = isCall ? XrefType.Call : XrefType.Jump,
                        Instruction = $"{instr.Mnemonic} {instr.Operand}"
                    });
                }

                // Also check indirect: call [rip+xxx] where the pointer points to target
                if (target == 0 && !_api.Is32Bit)
                {
                    ulong indirect = TryResolveRipRelativePtr(instr);
                    if (indirect == targetAddr)
                    {
                        results.Add(new XrefResult
                        {
                            FromAddress = instrAddr,
                            ToAddress = targetAddr,
                            Type = isCall ? XrefType.Call : XrefType.Jump,
                            Instruction = $"{instr.Mnemonic} {instr.Operand}"
                        });
                    }
                }
            }
            else if (isLea)
            {
                ulong leaTarget = TryResolveRipRelativeLea(instr);
                if (leaTarget == targetAddr)
                {
                    results.Add(new XrefResult
                    {
                        FromAddress = instrAddr,
                        ToAddress = targetAddr,
                        Type = XrefType.DataRead,
                        Instruction = $"{instr.Mnemonic} {instr.Operand}"
                    });
                }
            }
            else if (isMov)
            {
                // mov reg, [rip+xxx] or mov [rip+xxx], reg — data access
                ulong movTarget = TryResolveRipRelativePtr(instr);
                if (movTarget == targetAddr)
                {
                    var ops = instr.Operand ?? "";
                    bool isWrite = ops.Contains('[') && ops.IndexOf('[') < ops.IndexOf(',');
                    results.Add(new XrefResult
                    {
                        FromAddress = instrAddr,
                        ToAddress = targetAddr,
                        Type = isWrite ? XrefType.DataWrite : XrefType.DataRead,
                        Instruction = $"{instr.Mnemonic} {instr.Operand}"
                    });
                }
            }
        }
    }

    /// <summary>
    /// Scan for raw pointer bytes in data (e.g. vtable, function pointer table).
    /// </summary>
    private static void ScanPointerXrefs(byte[] data, ulong baseAddr, ulong targetAddr,
        int ptrSize, List<XrefResult> results)
    {
        byte[] needle = ptrSize == 8
            ? BitConverter.GetBytes(targetAddr)
            : BitConverter.GetBytes((uint)targetAddr);

        for (int i = 0; i <= data.Length - ptrSize; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (data[i + j] != needle[j]) { match = false; break; }
            }
            if (!match) continue;

            ulong ptrAddr = baseAddr + (ulong)i;
            // Skip if we already have a code xref at this address (avoid duplicates)
            if (results.Any(r => r.FromAddress == ptrAddr)) continue;

            results.Add(new XrefResult
            {
                FromAddress = ptrAddr,
                ToAddress = targetAddr,
                Type = XrefType.Unknown,
                Instruction = $"dq 0x{targetAddr:X}"
            });
        }
    }

    private static ulong ParseTarget(Gee.External.Capstone.X86.X86Instruction instr,
        ulong instrAddr, int instrSize)
    {
        var ops = (instr.Operand ?? "").Trim();
        if (ops.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (ulong.TryParse(ops[2..], NumberStyles.HexNumber, null, out ulong addr))
                return addr;
        }
        if (ulong.TryParse(ops, NumberStyles.HexNumber, null, out ulong addr2))
            return addr2;
        return 0;
    }

    /// <summary>
    /// Resolve RIP-relative indirect: [rip + disp32] → read pointer → actual address.
    /// Used for call [rip+xxx], jmp [rip+xxx], mov reg, [rip+xxx].
    /// </summary>
    private ulong TryResolveRipRelativePtr(Gee.External.Capstone.X86.X86Instruction instr)
    {
        var ops = instr.Operand ?? "";
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

        ulong effectiveAddr = sign == '+'
            ? (ulong)instr.Address + (ulong)instr.Bytes.Length + offset
            : (ulong)instr.Address + (ulong)instr.Bytes.Length - offset;

        int ptrSize = _api.Is32Bit ? 4 : 8;
        var ptrData = _api.Memory.ReadMemory(_api.TargetPid, effectiveAddr, (uint)ptrSize);
        if (ptrData == null) return 0;

        return ptrSize == 8 ? BitConverter.ToUInt64(ptrData) : BitConverter.ToUInt32(ptrData);
    }

    /// <summary>
    /// Resolve LEA with RIP-relative: lea reg, [rip + disp32] → effective address (no dereference).
    /// </summary>
    private static ulong TryResolveRipRelativeLea(Gee.External.Capstone.X86.X86Instruction instr)
    {
        var ops = instr.Operand ?? "";
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

        return sign == '+'
            ? (ulong)instr.Address + (ulong)instr.Bytes.Length + offset
            : (ulong)instr.Address + (ulong)instr.Bytes.Length - offset;
    }
}
