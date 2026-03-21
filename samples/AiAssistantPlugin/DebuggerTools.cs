using System.Text;
using System.Text.Json;
using Iced.Intel;
using KernelFlirt.SDK;

namespace AiAssistantPlugin;

/// <summary>
/// Defines debugger tools that the AI can call, and executes them via the SDK.
/// </summary>
public class DebuggerTools
{
    private readonly IDebuggerApi _api;

    public DebuggerTools(IDebuggerApi api)
    {
        _api = api;
    }

    /// <summary>
    /// OpenAI-compatible tool definitions to include in API requests.
    /// </summary>
    public static object[] GetToolDefinitions() =>
    [
        MakeTool("set_breakpoint", "Set a software breakpoint at the given address",
            new { type = "object", properties = new {
                address = new { type = "string", description = "Hex address, e.g. 0x7ff64f961190" }
            }, required = new[] { "address" } }),

        MakeTool("remove_breakpoint", "Remove a breakpoint by its handle ID",
            new { type = "object", properties = new {
                handle = new { type = "integer", description = "Breakpoint handle returned by set_breakpoint" }
            }, required = new[] { "handle" } }),

        MakeTool("list_breakpoints", "List all active breakpoints",
            new { type = "object", properties = new { } }),

        MakeTool("read_memory", "Read memory at address and return hex dump",
            new { type = "object", properties = new {
                address = new { type = "string", description = "Hex address to read from" },
                size = new { type = "integer", description = "Number of bytes to read (max 4096)" }
            }, required = new[] { "address", "size" } }),

        MakeTool("write_memory", "Write bytes to memory at address",
            new { type = "object", properties = new {
                address = new { type = "string", description = "Hex address to write to" },
                hex_bytes = new { type = "string", description = "Hex string of bytes to write, e.g. '90 90 CC'" }
            }, required = new[] { "address", "hex_bytes" } }),

        MakeTool("read_registers", "Read all CPU registers of the current thread",
            new { type = "object", properties = new { } }),

        MakeTool("disassemble", "Disassemble instructions at address",
            new { type = "object", properties = new {
                address = new { type = "string", description = "Hex address to start disassembly" },
                count = new { type = "integer", description = "Number of instructions (default 20, max 50)" }
            }, required = new[] { "address" } }),

        MakeTool("list_modules", "List all loaded modules in the target process",
            new { type = "object", properties = new { } }),

        MakeTool("list_threads", "List all threads in the target process",
            new { type = "object", properties = new { } }),

        MakeTool("resolve_symbol", "Resolve a symbol name to address or address to symbol name",
            new { type = "object", properties = new {
                name = new { type = "string", description = "Symbol name like 'kernel32!CreateFileW' or hex address" }
            }, required = new[] { "name" } }),

        MakeTool("navigate_disasm", "Navigate the main disassembly view to a specific address",
            new { type = "object", properties = new {
                address = new { type = "string", description = "Hex address to navigate to" }
            }, required = new[] { "address" } }),

        MakeTool("continue_execution", "Resume process execution (Run/F9). Process will run until next breakpoint or pause.",
            new { type = "object", properties = new { } }),

        MakeTool("single_step", "Step Into (F7) — execute one instruction, following into CALL instructions",
            new { type = "object", properties = new { } }),

        MakeTool("step_over", "Step Over (F8) — execute one instruction, stepping OVER call instructions (doesn't enter functions)",
            new { type = "object", properties = new { } }),

        MakeTool("step_out", "Step Out (Ctrl+F9) — execute until current function returns (run to return address from [RSP])",
            new { type = "object", properties = new { } }),

        MakeTool("run_to_address", "Run to Address (F4) — resume execution until the specified address is hit",
            new { type = "object", properties = new {
                address = new { type = "string", description = "Hex address to run to, e.g. 0x7ff64f961200" }
            }, required = new[] { "address" } }),

        MakeTool("skip_instruction", "Skip Instruction (Ctrl+F8) — move RIP past current instruction WITHOUT executing it. Useful to skip calls or jumps.",
            new { type = "object", properties = new { } }),

        MakeTool("pause_execution", "Pause (F12) — suspend a running process. Use when process is running and you need to break in.",
            new { type = "object", properties = new { } }),

        MakeTool("wait_for_break", "Wait for the process to enter break state (hit a breakpoint or complete a step). MUST be called after continue_execution or run_to_address before reading memory/registers. Timeout in milliseconds (default 5000).",
            new { type = "object", properties = new {
                timeout_ms = new { type = "integer", description = "Timeout in milliseconds, default 5000" }
            } }),

        MakeTool("decompile", "Decompile function at address to C pseudocode (like IDA Pro Hex-Rays). Much better for understanding code than raw disassembly. Takes a few seconds.",
            new { type = "object", properties = new {
                address = new { type = "string", description = "Hex address of the function to decompile" }
            }, required = new[] { "address" } }),
    ];

    /// <summary>
    /// Execute a tool call and return the result as a string.
    /// </summary>
    public string Execute(string toolName, string argumentsJson)
    {
        try
        {
            if (!_api.IsConnected)
                return "Error: Not connected to target";

            using var args = JsonDocument.Parse(argumentsJson);
            var root = args.RootElement;

            return toolName switch
            {
                "set_breakpoint" => ExecSetBreakpoint(root),
                "remove_breakpoint" => ExecRemoveBreakpoint(root),
                "list_breakpoints" => ExecListBreakpoints(),
                "read_memory" => ExecReadMemory(root),
                "write_memory" => ExecWriteMemory(root),
                "read_registers" => ExecReadRegisters(),
                "disassemble" => ExecDisassemble(root),
                "list_modules" => ExecListModules(),
                "list_threads" => ExecListThreads(),
                "resolve_symbol" => ExecResolveSymbol(root),
                "navigate_disasm" => ExecNavigateDisasm(root),
                "continue_execution" => ExecContinue(),
                "single_step" => ExecSingleStep(),
                "step_over" => ExecStepOver(),
                "step_out" => ExecStepOut(),
                "run_to_address" => ExecRunToAddress(root),
                "skip_instruction" => ExecSkipInstruction(),
                "pause_execution" => ExecPause(),
                "wait_for_break" => ExecWaitForBreak(root),
                "decompile" => ExecDecompile(root),
                _ => $"Unknown tool: {toolName}"
            };
        }
        catch (Exception ex)
        {
            return $"Error executing {toolName}: {ex.Message}";
        }
    }

    private ulong ParseAddress(string s)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            s = s[2..];
        return ulong.Parse(s, System.Globalization.NumberStyles.HexNumber);
    }

    private string ExecSetBreakpoint(JsonElement args)
    {
        if (!_api.IsBreakState) return "Error: Process must be in break state to set breakpoints";

        var addr = ParseAddress(args.GetProperty("address").GetString()!);
        var handle = _api.Breakpoints.SetBreakpoint(_api.TargetPid, _api.SelectedThreadId, addr, PluginBreakpointType.Software);

        if (handle == null)
            return $"Failed to set breakpoint at 0x{addr:X}";

        var sym = _api.Symbols.ResolveAddress(addr);
        var symStr = sym != null ? $" ({sym})" : "";
        return $"Breakpoint set at 0x{addr:X}{symStr}, handle={handle.Value}";
    }

    private string ExecRemoveBreakpoint(JsonElement args)
    {
        var handle = args.GetProperty("handle").GetUInt32();
        var ok = _api.Breakpoints.RemoveBreakpoint(handle);
        return ok ? $"Breakpoint #{handle} removed" : $"Failed to remove breakpoint #{handle}";
    }

    private string ExecListBreakpoints()
    {
        var bps = _api.Breakpoints.GetAll();
        if (bps == null || bps.Count == 0) return "No breakpoints set";

        var sb = new StringBuilder();
        foreach (var bp in bps)
        {
            var sym = _api.Symbols.ResolveAddress(bp.Address);
            var symStr = sym != null ? $" ({sym})" : "";
            sb.AppendLine($"#{bp.Handle} {bp.Type} @ 0x{bp.Address:X}{symStr}  Hits={bp.HitCount}  {(bp.Enabled ? "ON" : "OFF")}");
        }
        return sb.ToString();
    }

    private string ExecReadMemory(JsonElement args)
    {
        var addr = ParseAddress(args.GetProperty("address").GetString()!);
        var size = args.GetProperty("size").GetUInt32();
        if (size > 512) size = 512; // Cap to save context window

        var data = _api.Memory.ReadMemory(_api.TargetPid, addr, size);
        if (data == null) return $"Failed to read memory at 0x{addr:X}";

        // Check if data is mostly printable (string-like)
        int printable = data.Count(b => b is >= 0x20 and < 0x7F);
        bool isStringLike = printable > data.Length / 2;

        var sb = new StringBuilder();
        sb.AppendLine($"[{data.Length} bytes at 0x{addr:X}]");

        if (isStringLike && data.Length <= 128)
        {
            // Compact string view for small reads of text data
            sb.Append("HEX: ");
            foreach (var b in data) sb.Append($"{b:X2} ");
            sb.AppendLine();
            sb.Append("ASCII: \"");
            foreach (var b in data)
                sb.Append(b is >= 0x20 and < 0x7F ? (char)b : b == 0 ? '\0' : '.');
            sb.AppendLine("\"");
        }
        else
        {
            // Standard hex dump, compact
            for (int i = 0; i < data.Length; i += 16)
            {
                var lineLen = Math.Min(16, data.Length - i);
                sb.Append($"{addr + (ulong)i:X}: ");
                for (int j = 0; j < lineLen; j++)
                    sb.Append($"{data[i + j]:X2} ");
                sb.Append(" ");
                for (int j = 0; j < lineLen; j++)
                {
                    var b = data[i + j];
                    sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
                }
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }

    private string ExecWriteMemory(JsonElement args)
    {
        if (!_api.IsBreakState) return "Error: Process must be in break state";

        var addr = ParseAddress(args.GetProperty("address").GetString()!);
        var hexStr = args.GetProperty("hex_bytes").GetString()!;

        // Parse hex bytes: "90 90 CC" or "9090CC"
        var clean = hexStr.Replace(" ", "").Replace("-", "");
        if (clean.Length % 2 != 0) return "Error: Invalid hex string length";

        var bytes = new byte[clean.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = byte.Parse(clean.Substring(i * 2, 2), System.Globalization.NumberStyles.HexNumber);

        var ok = _api.Memory.WriteMemory(_api.TargetPid, addr, bytes);
        return ok ? $"Wrote {bytes.Length} bytes to 0x{addr:X}" : $"Failed to write memory at 0x{addr:X}";
    }

    private string ExecReadRegisters()
    {
        if (!_api.IsBreakState) return "Error: Process must be in break state";

        var regs = _api.Memory.ReadRegisters(_api.TargetPid, _api.SelectedThreadId);
        if (regs == null || regs.Count == 0) return "Failed to read registers";

        var sb = new StringBuilder();
        foreach (var r in regs.Where(r => !r.IsFlag))
            sb.AppendLine($"{r.Name,-6} = 0x{r.Value:X16}");

        var flags = regs.Where(r => r.IsFlag && r.Value != 0).Select(r => r.Name);
        var flagStr = string.Join(" ", flags);
        if (!string.IsNullOrEmpty(flagStr))
            sb.AppendLine($"FLAGS: {flagStr}");

        return sb.ToString();
    }

    private string ExecDisassemble(JsonElement args)
    {
        var addr = ParseAddress(args.GetProperty("address").GetString()!);
        var count = 20;
        if (args.TryGetProperty("count", out var countEl))
            count = Math.Min(countEl.GetInt32(), 50);

        var codeBytes = _api.Memory.ReadMemory(_api.TargetPid, addr, (uint)(count * 15));
        if (codeBytes == null || codeBytes.Length == 0) return $"Failed to read memory at 0x{addr:X}";

        var bitness = _api.Is32Bit ? 32 : 64;
        var codeReader = new ByteArrayCodeReader(codeBytes);
        var decoder = Iced.Intel.Decoder.Create(bitness, codeReader);
        decoder.IP = addr;

        var formatter = new NasmFormatter();
        formatter.Options.DigitSeparator = "";
        formatter.Options.FirstOperandCharIndex = 10;
        formatter.Options.HexPrefix = "0x";
        formatter.Options.HexSuffix = null;
        formatter.Options.UppercaseHex = false;

        var output = new StringOutput();
        var sb = new StringBuilder();
        int n = 0;

        while (n < count)
        {
            var instr = decoder.Decode();
            if (instr.IsInvalid) break;

            formatter.Format(instr, output);
            var sym = _api.Symbols.ResolveAddress(instr.IP);
            var symStr = sym != null ? $"  ; {sym}" : "";
            sb.AppendLine($"{instr.IP:X16}  {output.ToStringAndReset()}{symStr}");
            n++;
        }

        return sb.ToString();
    }

    private string ExecListModules()
    {
        var modules = _api.Symbols.GetModules();
        if (modules == null || modules.Count == 0) return "No modules loaded";

        var sb = new StringBuilder();
        foreach (var m in modules)
            sb.AppendLine($"0x{m.BaseAddress:X16}+0x{m.Size:X8}  {m.Name}");
        return sb.ToString();
    }

    private string ExecListThreads()
    {
        var threads = _api.Process.EnumThreads(_api.TargetPid);
        if (threads == null || threads.Count == 0) return "No threads found";

        var sb = new StringBuilder();
        foreach (var t in threads)
        {
            var sym = _api.Symbols.ResolveAddress(t.StartAddress);
            var symStr = sym != null ? $"  ({sym})" : "";
            var current = t.ThreadId == _api.SelectedThreadId ? " <<< current" : "";
            sb.AppendLine($"TID={t.ThreadId}  Start=0x{t.StartAddress:X}{symStr}  State={t.State}{current}");
        }
        return sb.ToString();
    }

    private string ExecResolveSymbol(JsonElement args)
    {
        var name = args.GetProperty("name").GetString()!.Trim();

        // Try as address first
        if (name.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || name.All(c => "0123456789abcdefABCDEF".Contains(c)))
        {
            var addr = ParseAddress(name);
            var sym = _api.Symbols.ResolveAddress(addr);
            return sym != null ? $"0x{addr:X} = {sym}" : $"No symbol found at 0x{addr:X}";
        }

        // Try as symbol name
        var resolved = _api.Symbols.ResolveNameToAddress(name);
        return resolved != 0 ? $"{name} = 0x{resolved:X}" : $"Symbol '{name}' not found";
    }

    private string ExecNavigateDisasm(JsonElement args)
    {
        var addr = ParseAddress(args.GetProperty("address").GetString()!);
        _api.UI.NavigateDisassembly(addr);
        return $"Navigated disassembly view to 0x{addr:X}";
    }

    private string ExecContinue()
    {
        if (!_api.IsBreakState) return "Error: Process is not in break state";
        _api.Continue();
        return "Process resumed (Run/F9). Call wait_for_break before reading memory/registers.";
    }

    private string ExecSingleStep()
    {
        if (!_api.IsBreakState) return "Error: Process is not in break state";
        _api.SingleStep();
        return "Step Into (F7) executed — stepped one instruction, following into calls";
    }

    private string ExecStepOver()
    {
        if (!_api.IsBreakState) return "Error: Process is not in break state";
        _api.StepOver();
        return "Step Over (F8) executed — stepped one instruction, skipping over calls";
    }

    private string ExecStepOut()
    {
        if (!_api.IsBreakState) return "Error: Process is not in break state";
        _api.StepOut();
        return "Step Out (Ctrl+F9) executed — running until current function returns";
    }

    private string ExecRunToAddress(JsonElement args)
    {
        if (!_api.IsBreakState) return "Error: Process is not in break state";
        var addr = ParseAddress(args.GetProperty("address").GetString()!);
        _api.RunToCursor(addr);
        var sym = _api.Symbols.ResolveAddress(addr);
        var symStr = sym != null ? $" ({sym})" : "";
        return $"Running to 0x{addr:X}{symStr} (F4). Call wait_for_break before reading memory/registers.";
    }

    private string ExecSkipInstruction()
    {
        if (!_api.IsBreakState) return "Error: Process is not in break state";
        _api.SkipInstruction();
        return "Instruction skipped (Ctrl+F8) — RIP moved past current instruction without executing it";
    }

    private string ExecPause()
    {
        if (_api.IsBreakState) return "Process is already paused";
        _api.Pause();
        return "Pause (F12) — suspending all threads";
    }

    private string ExecWaitForBreak(JsonElement args)
    {
        var timeoutMs = 10000;
        if (args.TryGetProperty("timeout_ms", out var toProp))
            timeoutMs = toProp.GetInt32();

        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Phase 1: Wait for process to LEAVE break state (Continue is async on UI thread)
        if (_api.IsBreakState)
        {
            while (sw.ElapsedMilliseconds < 2000)
            {
                if (!_api.IsBreakState) break;
                Thread.Sleep(20);
            }
            if (_api.IsBreakState)
                return "Process did not leave break state — Continue may not have been called";
        }

        // Phase 2: Wait for process to ENTER break state (hit BP / complete step)
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (_api.IsBreakState)
            {
                var regs = _api.Memory.ReadRegisters(_api.TargetPid, _api.SelectedThreadId);
                var rip = regs?.FirstOrDefault(r => r.Name is "RIP" or "EIP")?.Value ?? 0;
                var sym = rip != 0 ? _api.Symbols.ResolveAddress(rip) : null;
                var symStr = sym != null ? $" ({sym})" : "";
                return $"Break at RIP=0x{rip:X}{symStr} after {sw.ElapsedMilliseconds}ms";
            }
            Thread.Sleep(20);
        }

        return $"Timeout after {timeoutMs}ms — process is still running. Use pause_execution to force stop.";
    }

    private string ExecDecompile(JsonElement args)
    {
        if (!_api.IsBreakState) return "Error: Process must be in break state";

        var addr = ParseAddress(args.GetProperty("address").GetString()!);

        // Trigger decompilation
        _api.UI.DecompileFunction(addr);

        // Wait for decompilation to complete (polls DecompiledCode)
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string lastCode = _api.UI.GetDecompiledCode();

        // Wait for it to change from current / "Decompiling..."
        while (sw.ElapsedMilliseconds < 30000)
        {
            Thread.Sleep(200);
            var code = _api.UI.GetDecompiledCode();
            if (!string.IsNullOrEmpty(code) && code != lastCode && !code.Contains("Decompiling..."))
            {
                // Truncate if too long
                if (code.Length > 3000)
                    code = code[..3000] + "\n// ... (truncated)";
                return code;
            }
        }

        var finalCode = _api.UI.GetDecompiledCode();
        if (!string.IsNullOrEmpty(finalCode) && !finalCode.Contains("Decompiling..."))
            return finalCode.Length > 3000 ? finalCode[..3000] + "\n// ... (truncated)" : finalCode;

        return "Decompilation timed out. RetDec may not be installed.";
    }

    private static object MakeTool(string name, string desc, object parameters) => new
    {
        type = "function",
        function = new { name, description = desc, parameters }
    };
}
