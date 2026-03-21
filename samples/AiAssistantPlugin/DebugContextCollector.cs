using System.Text;
using Iced.Intel;
using KernelFlirt.SDK;

namespace AiAssistantPlugin;

public static class DebugContextCollector
{
    public static string Collect(IDebuggerApi api, AiSettings settings)
    {
        var sb = new StringBuilder();

        if (!api.IsConnected)
        {
            sb.AppendLine("[Not connected]");
            return sb.ToString();
        }

        if (!api.IsBreakState)
        {
            sb.AppendLine("[Running]");
            return sb.ToString();
        }

        var pid = api.TargetPid;
        var tid = api.SelectedThreadId;

        // Minimal header
        sb.AppendLine($"PID={pid} TID={tid} {(api.Is32Bit ? "32-bit" : "64-bit")}");

        IReadOnlyList<PluginRegister>? regs = null;

        if (settings.IncludeRegisters)
        {
            regs = api.Memory.ReadRegisters(pid, tid);
            if (regs != null && regs.Count > 0)
            {
                // Only key registers — like IDA Pro's compact view
                var keyRegs = new[] { "RIP", "EIP", "RSP", "ESP", "RAX", "EAX", "RCX", "ECX", "RDX", "EDX", "R8", "R9", "RBX", "EBX", "RBP", "EBP" };
                var parts = new List<string>();
                foreach (var name in keyRegs)
                {
                    var r = regs.FirstOrDefault(x => x.Name == name);
                    if (r != null)
                        parts.Add($"{r.Name}=0x{r.Value:X}");
                }
                sb.AppendLine(string.Join(" ", parts));
            }
        }

        if (settings.IncludeDisasm)
        {
            regs ??= api.Memory.ReadRegisters(pid, tid);
            var rip = regs?.FirstOrDefault(r => r.Name is "RIP" or "EIP")?.Value ?? 0;

            if (rip != 0)
            {
                // Try decompiled code first (like IDA Pro)
                var decomp = api.UI.GetDecompiledCode();
                if (!string.IsNullOrEmpty(decomp) && !decomp.Contains("Decompiling...") && decomp.Length > 10)
                {
                    sb.AppendLine("--- Pseudocode ---");
                    // Truncate for context
                    if (decomp.Length > 1500)
                        decomp = decomp[..1500] + "\n// ...";
                    sb.AppendLine(decomp);
                }
                else
                {
                    // Fallback: compact disassembly (10 instructions)
                    var codeBytes = api.Memory.ReadMemory(pid, rip, 200);
                    if (codeBytes != null && codeBytes.Length > 0)
                    {
                        sb.AppendLine("--- Disasm ---");
                        var bitness = api.Is32Bit ? 32 : 64;
                        var lines = Disassemble(codeBytes, rip, bitness, 10, api.Symbols);
                        foreach (var line in lines)
                            sb.AppendLine(line);
                    }
                }
            }
        }

        if (settings.IncludeModules)
        {
            var modules = api.Symbols.GetModules();
            if (modules != null && modules.Count > 0)
            {
                sb.AppendLine("--- Modules ---");
                foreach (var m in modules)
                    sb.AppendLine($"0x{m.BaseAddress:X}+0x{m.Size:X} {m.Name}");
            }
        }

        if (settings.IncludeStack)
        {
            regs ??= api.Memory.ReadRegisters(pid, tid);
            var rsp = regs?.FirstOrDefault(r => r.Name is "RSP" or "ESP")?.Value ?? 0;
            if (rsp != 0)
            {
                var stackBytes = api.Memory.ReadMemory(pid, rsp, 64);
                if (stackBytes != null && stackBytes.Length > 0)
                {
                    sb.AppendLine("--- Stack ---");
                    var ptrSize = api.Is32Bit ? 4 : 8;
                    for (int i = 0; i + ptrSize <= stackBytes.Length; i += ptrSize)
                    {
                        ulong val = ptrSize == 8
                            ? BitConverter.ToUInt64(stackBytes, i)
                            : BitConverter.ToUInt32(stackBytes, i);
                        var sym = api.Symbols.ResolveAddress(val);
                        var symStr = sym != null ? $" {sym}" : "";
                        sb.AppendLine($"[RSP+0x{i:X}] 0x{val:X}{symStr}");
                    }
                }
            }
        }

        if (settings.IncludeThreads)
        {
            var threads = api.Process.EnumThreads(pid);
            if (threads != null && threads.Count > 0)
            {
                sb.AppendLine("--- Threads ---");
                foreach (var t in threads)
                    sb.AppendLine($"TID={t.ThreadId}{(t.ThreadId == tid ? " *" : "")}");
            }
        }

        if (settings.IncludeBreakpoints)
        {
            var bps = api.Breakpoints.GetAll();
            if (bps != null && bps.Count > 0)
            {
                sb.AppendLine("--- Breakpoints ---");
                foreach (var bp in bps)
                {
                    var sym = api.Symbols.ResolveAddress(bp.Address);
                    sb.AppendLine($"#{bp.Handle} 0x{bp.Address:X}{(sym != null ? $" {sym}" : "")} {(bp.Enabled ? "ON" : "OFF")}");
                }
            }
        }

        return sb.ToString();
    }

    private static List<string> Disassemble(byte[] code, ulong rip, int bitness, int maxInstructions, ISymbolApi symbols)
    {
        var result = new List<string>();
        var codeReader = new ByteArrayCodeReader(code);
        var decoder = Iced.Intel.Decoder.Create(bitness, codeReader);
        decoder.IP = rip;

        var formatter = new NasmFormatter();
        formatter.Options.DigitSeparator = "";
        formatter.Options.FirstOperandCharIndex = 10;
        formatter.Options.HexPrefix = "0x";
        formatter.Options.HexSuffix = null;
        formatter.Options.UppercaseHex = false;

        var output = new StringOutput();
        int count = 0;

        while (count < maxInstructions)
        {
            var instr = decoder.Decode();
            if (instr.IsInvalid) break;

            formatter.Format(instr, output);
            var sym = symbols.ResolveAddress(instr.IP);
            var prefix = instr.IP == rip ? ">" : " ";
            var symLabel = sym != null ? $" ; {sym}" : "";
            result.Add($"{prefix}{instr.IP:X} {output.ToStringAndReset()}{symLabel}");
            count++;
        }

        return result;
    }
}
