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
        // ── State ─────────────────────────────────────────────────────────
        MakeTool("get_debugger_state",
            "Return current debugger state: connected, break/running, target PID, selected TID, bitness",
            new { type = "object", properties = new { } }),

        // ── Breakpoints ───────────────────────────────────────────────────
        MakeTool("set_breakpoint", "Set a software (INT3) breakpoint at the given address",
            new { type = "object", properties = new {
                address = new { type = "string", description = "Hex address, e.g. 0x7ff64f961190" }
            }, required = new[] { "address" } }),

        MakeTool("set_hardware_breakpoint",
            "Set a hardware execute breakpoint (DR0-DR3). Works even when code is patched or encrypted.",
            new { type = "object", properties = new {
                address = new { type = "string", description = "Hex address" }
            }, required = new[] { "address" } }),

        MakeTool("set_hw_write_watchpoint",
            "Set a hardware write watchpoint. Breaks when the address is written to.",
            new { type = "object", properties = new {
                address = new { type = "string",  description = "Hex address to watch" },
                length  = new { type = "integer", description = "Watch size in bytes: 1, 2, 4 or 8 (default 1)" }
            }, required = new[] { "address" } }),

        MakeTool("set_hw_access_watchpoint",
            "Set a hardware read/write watchpoint. Breaks on any access (read or write).",
            new { type = "object", properties = new {
                address = new { type = "string",  description = "Hex address to watch" },
                length  = new { type = "integer", description = "Watch size in bytes: 1, 2, 4 or 8 (default 1)" }
            }, required = new[] { "address" } }),

        MakeTool("set_memory_breakpoint",
            "Set a memory (page guard) breakpoint. Breaks on any access to the page containing the address.",
            new { type = "object", properties = new {
                address = new { type = "string", description = "Hex address" }
            }, required = new[] { "address" } }),

        MakeTool("remove_breakpoint", "Remove a breakpoint by its handle ID",
            new { type = "object", properties = new {
                handle = new { type = "integer", description = "Breakpoint handle returned by set_* tools" }
            }, required = new[] { "handle" } }),

        MakeTool("list_breakpoints", "List all active breakpoints with types, addresses, symbols and hit counts",
            new { type = "object", properties = new { } }),

        // ── Memory ────────────────────────────────────────────────────────
        MakeTool("read_memory", "Read memory at address and return hex dump (max 512 bytes)",
            new { type = "object", properties = new {
                address = new { type = "string",  description = "Hex address to read from" },
                size    = new { type = "integer", description = "Number of bytes to read (max 512)" }
            }, required = new[] { "address", "size" } }),

        MakeTool("read_pointer",
            "Read a single pointer (8 bytes x64 / 4 bytes x32) at address and resolve its symbol",
            new { type = "object", properties = new {
                address = new { type = "string", description = "Hex address of the pointer variable" }
            }, required = new[] { "address" } }),

        MakeTool("read_string",
            "Read a null-terminated ASCII/ANSI string from memory (up to 256 chars)",
            new { type = "object", properties = new {
                address = new { type = "string", description = "Hex address of the char* string" }
            }, required = new[] { "address" } }),

        MakeTool("read_unicode_string",
            "Read a null-terminated UTF-16LE wide string from memory (up to 256 chars)",
            new { type = "object", properties = new {
                address = new { type = "string", description = "Hex address of the WCHAR* string" }
            }, required = new[] { "address" } }),

        MakeTool("write_memory", "Write bytes to memory at address",
            new { type = "object", properties = new {
                address   = new { type = "string", description = "Hex address to write to" },
                hex_bytes = new { type = "string", description = "Hex bytes to write, e.g. '90 90 CC'" }
            }, required = new[] { "address", "hex_bytes" } }),

        MakeTool("search_memory",
            "Search for a byte pattern in a memory range. Use ?? as wildcard for unknown bytes. " +
            "Example pattern: '48 8B ?? ?? E8 ?? ?? ?? ??'",
            new { type = "object", properties = new {
                start   = new { type = "string",  description = "Hex start address of the range" },
                size    = new { type = "integer", description = "Range size in bytes to scan (max 16 MB)" },
                pattern = new { type = "string",  description = "Hex pattern with optional ?? wildcards" }
            }, required = new[] { "start", "size", "pattern" } }),

        MakeTool("read_registers", "Read all CPU registers of the current thread",
            new { type = "object", properties = new { } }),

        MakeTool("write_rip",
            "Redirect execution by changing RIP (instruction pointer) of the selected thread",
            new { type = "object", properties = new {
                address = new { type = "string", description = "New RIP value (hex address)" }
            }, required = new[] { "address" } }),

        MakeTool("allocate_memory",
            "Allocate RWX virtual memory in the target process. Returns the allocated address.",
            new { type = "object", properties = new {
                size = new { type = "integer", description = "Number of bytes to allocate" }
            }, required = new[] { "size" } }),

        MakeTool("free_memory",
            "Free previously allocated virtual memory in the target process",
            new { type = "object", properties = new {
                address = new { type = "string", description = "Hex address returned by allocate_memory" }
            }, required = new[] { "address" } }),

        MakeTool("protect_memory",
            "Change memory page protection (VirtualProtectEx). Returns the old protection value.\n" +
            "Common values: 0x02=PAGE_READONLY  0x04=PAGE_READWRITE  0x20=PAGE_EXECUTE_READ  0x40=PAGE_EXECUTE_READWRITE",
            new { type = "object", properties = new {
                address    = new { type = "string",  description = "Hex base address of the region" },
                size       = new { type = "integer", description = "Region size in bytes" },
                protection = new { type = "integer", description = "New protection constant, e.g. 0x40" }
            }, required = new[] { "address", "size", "protection" } }),

        // ── Disassembly / Decompilation ───────────────────────────────────
        MakeTool("disassemble", "Disassemble instructions at address (NASM syntax with symbols)",
            new { type = "object", properties = new {
                address = new { type = "string",  description = "Hex address to start disassembly" },
                count   = new { type = "integer", description = "Number of instructions (default 20, max 50)" }
            }, required = new[] { "address" } }),

        MakeTool("navigate_disasm", "Navigate the main disassembly view to a specific address",
            new { type = "object", properties = new {
                address = new { type = "string", description = "Hex address to navigate to" }
            }, required = new[] { "address" } }),

        MakeTool("disasm_go_back", "Go back to previous disassembly location (undo navigate_disasm/decompile)",
            new { type = "object", properties = new { } }),

        MakeTool("decompile",
            "Decompile function at address to C pseudocode (like IDA Pro Hex-Rays). " +
            "Much better for understanding code than raw disassembly. Takes a few seconds.",
            new { type = "object", properties = new {
                address = new { type = "string", description = "Hex address of the function to decompile" }
            }, required = new[] { "address" } }),

        // ── Symbols / Modules ─────────────────────────────────────────────
        MakeTool("resolve_symbol", "Resolve a symbol name to address or address to symbol name",
            new { type = "object", properties = new {
                name = new { type = "string", description = "Symbol like 'kernel32!CreateFileW' or hex address" }
            }, required = new[] { "name" } }),

        MakeTool("list_modules", "List all user-mode modules loaded in the target process",
            new { type = "object", properties = new { } }),

        MakeTool("list_kernel_modules", "List all kernel-mode drivers currently loaded in the system",
            new { type = "object", properties = new { } }),

        // ── Process / Threads ─────────────────────────────────────────────
        MakeTool("list_processes", "List all running processes on the system",
            new { type = "object", properties = new { } }),

        MakeTool("list_threads", "List all threads in the target process",
            new { type = "object", properties = new { } }),

        MakeTool("suspend_thread", "Suspend a specific thread by TID",
            new { type = "object", properties = new {
                tid = new { type = "integer", description = "Thread ID to suspend" }
            }, required = new[] { "tid" } }),

        MakeTool("resume_thread", "Resume a suspended thread by TID",
            new { type = "object", properties = new {
                tid = new { type = "integer", description = "Thread ID to resume" }
            }, required = new[] { "tid" } }),

        MakeTool("get_peb_address",
            "Get the PEB (Process Environment Block) address of the target process",
            new { type = "object", properties = new { } }),

        // ── Execution control ─────────────────────────────────────────────
        MakeTool("continue_execution",
            "Resume process execution (Run/F9). Call wait_for_break before reading state afterwards.",
            new { type = "object", properties = new { } }),

        MakeTool("single_step", "Step Into (F7) — execute one instruction, following into CALL instructions",
            new { type = "object", properties = new { } }),

        MakeTool("step_over",
            "Step Over (F8) — execute one instruction, stepping OVER call instructions",
            new { type = "object", properties = new { } }),

        MakeTool("step_out", "Step Out (Ctrl+F9) — execute until current function returns",
            new { type = "object", properties = new { } }),

        MakeTool("run_to_address",
            "Run to Address (F4) — resume execution until the specified address is hit. Call wait_for_break after.",
            new { type = "object", properties = new {
                address = new { type = "string", description = "Hex address to run to" }
            }, required = new[] { "address" } }),

        MakeTool("skip_instruction",
            "Skip Instruction (Ctrl+F8) — move RIP past current instruction WITHOUT executing it",
            new { type = "object", properties = new { } }),

        MakeTool("pause_execution",
            "Pause (F12) — suspend a running process. Use when process is running and you need to break in.",
            new { type = "object", properties = new { } }),

        MakeTool("wait_for_break",
            "Wait for the process to enter break state (hit a breakpoint or complete a step). " +
            "MUST be called after continue_execution or run_to_address before reading memory/registers.",
            new { type = "object", properties = new {
                timeout_ms = new { type = "integer", description = "Timeout in milliseconds (default 10000)" }
            } }),

        // ── Anti-debug bypass ─────────────────────────────────────────────
        MakeTool("clear_debug_port",
            "Clear DebugPort in EPROCESS — hides process from IsDebuggerPresent / NtQueryInformationProcess(ProcessDebugPort)",
            new { type = "object", properties = new { } }),

        MakeTool("clear_thread_hide",
            "Clear HideFromDebugger flag on all threads — prevents debugger detection via thread enumeration",
            new { type = "object", properties = new { } }),

        MakeTool("install_ntqsi_hook",
            "Install kernel hook on NtQuerySystemInformation to hide the debugger process from process lists",
            new { type = "object", properties = new { } }),

        MakeTool("remove_ntqsi_hook",
            "Remove the NtQuerySystemInformation kernel hook",
            new { type = "object", properties = new { } }),

        MakeTool("probe_ntqsi_hook",
            "Check if the NtQuerySystemInformation hook is installed and return its status",
            new { type = "object", properties = new { } }),

        MakeTool("spoof_shared_user_data",
            "Enable or disable SharedUserData (KUSER_SHARED_DATA) time spoofing — defeats timing-based anti-debug",
            new { type = "object", properties = new {
                enable = new { type = "boolean", description = "true to enable spoofing, false to disable" }
            }, required = new[] { "enable" } }),

        // ── UI helpers ────────────────────────────────────────────────────
        MakeTool("add_unpacked_module",
            "Register a dynamically unpacked PE as a virtual module. " +
            "Triggers section/import/string refresh in the UI — use after manual unpacking.",
            new { type = "object", properties = new {
                pe_base = new { type = "string", description = "Hex base address where the PE is mapped in memory" },
                name    = new { type = "string", description = "Display name for the module, e.g. 'unpacked.exe'" }
            }, required = new[] { "pe_base", "name" } }),

        MakeTool("refresh_modules",
            "Force a refresh of the module list and sections tab in the UI",
            new { type = "object", properties = new { } }),
    ];

    /// <summary>
    /// Execute a tool call and return the result as a string.
    /// </summary>
    public string Execute(string toolName, string argumentsJson)
    {
        try
        {
            if (!_api.IsConnected && toolName != "get_debugger_state")
                return "Error: Not connected to target";

            using var args = JsonDocument.Parse(argumentsJson);
            var root = args.RootElement;

            return toolName switch
            {
                // State
                "get_debugger_state"       => ExecGetDebuggerState(),

                // Breakpoints
                "set_breakpoint"           => ExecSetBreakpoint(root, PluginBreakpointType.Software),
                "set_hardware_breakpoint"  => ExecSetBreakpoint(root, PluginBreakpointType.Hardware),
                "set_hw_write_watchpoint"  => ExecSetWatchpoint(root, PluginBreakpointType.HwWrite),
                "set_hw_access_watchpoint" => ExecSetWatchpoint(root, PluginBreakpointType.HwReadWrite),
                "set_memory_breakpoint"    => ExecSetBreakpoint(root, PluginBreakpointType.Memory),
                "remove_breakpoint"        => ExecRemoveBreakpoint(root),
                "list_breakpoints"         => ExecListBreakpoints(),

                // Memory
                "read_memory"              => ExecReadMemory(root),
                "read_pointer"             => ExecReadPointer(root),
                "read_string"              => ExecReadString(root, unicode: false),
                "read_unicode_string"      => ExecReadString(root, unicode: true),
                "write_memory"             => ExecWriteMemory(root),
                "search_memory"            => ExecSearchMemory(root),
                "read_registers"           => ExecReadRegisters(),
                "write_rip"                => ExecWriteRip(root),
                "allocate_memory"          => ExecAllocateMemory(root),
                "free_memory"              => ExecFreeMemory(root),
                "protect_memory"           => ExecProtectMemory(root),

                // Disassembly / Decompilation
                "disassemble"              => ExecDisassemble(root),
                "navigate_disasm"          => ExecNavigateDisasm(root),
                "disasm_go_back"           => ExecDisasmGoBack(),
                "decompile"                => ExecDecompile(root),

                // Symbols / modules
                "resolve_symbol"           => ExecResolveSymbol(root),
                "list_modules"             => ExecListModules(),
                "list_kernel_modules"      => ExecListKernelModules(),

                // Process / threads
                "list_processes"           => ExecListProcesses(),
                "list_threads"             => ExecListThreads(),
                "suspend_thread"           => ExecSuspendThread(root),
                "resume_thread"            => ExecResumeThread(root),
                "get_peb_address"          => ExecGetPebAddress(),

                // Execution control
                "continue_execution"       => ExecContinue(),
                "single_step"              => ExecSingleStep(),
                "step_over"                => ExecStepOver(),
                "step_out"                 => ExecStepOut(),
                "run_to_address"           => ExecRunToAddress(root),
                "skip_instruction"         => ExecSkipInstruction(),
                "pause_execution"          => ExecPause(),
                "wait_for_break"           => ExecWaitForBreak(root),

                // Anti-debug bypass
                "clear_debug_port"         => ExecClearDebugPort(),
                "clear_thread_hide"        => ExecClearThreadHide(),
                "install_ntqsi_hook"       => ExecInstallNtQsiHook(),
                "remove_ntqsi_hook"        => ExecRemoveNtQsiHook(),
                "probe_ntqsi_hook"         => ExecProbeNtQsiHook(),
                "spoof_shared_user_data"   => ExecSpoofSharedUserData(root),

                // UI
                "add_unpacked_module"      => ExecAddUnpackedModule(root),
                "refresh_modules"          => ExecRefreshModules(),

                _ => $"Unknown tool: {toolName}"
            };
        }
        catch (Exception ex)
        {
            return $"Error executing {toolName}: {ex.Message}";
        }
    }

    // ── State ────────────────────────────────────────────────────────────────

    private string ExecGetDebuggerState()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Connected  : {_api.IsConnected}");
        sb.AppendLine($"BreakState : {_api.IsBreakState}");
        sb.AppendLine($"TargetPID  : {_api.TargetPid}");
        sb.AppendLine($"SelectedTID: {_api.SelectedThreadId}");
        sb.AppendLine($"Bitness    : {(_api.Is32Bit ? "32-bit" : "64-bit")}");
        return sb.ToString();
    }

    // ── Breakpoints ──────────────────────────────────────────────────────────

    private string ExecSetBreakpoint(JsonElement args, PluginBreakpointType type)
    {
        if (!_api.IsBreakState) return "Error: Process must be in break state to set breakpoints";

        var addr   = ParseAddress(args.GetProperty("address").GetString()!);
        var handle = _api.Breakpoints.SetBreakpoint(_api.TargetPid, _api.SelectedThreadId, addr, type);

        if (handle == null)
            return $"Failed to set {type} breakpoint at 0x{addr:X}";

        var sym = _api.Symbols.ResolveAddress(addr);
        var symStr = sym != null ? $" ({sym})" : "";
        return $"{type} breakpoint set at 0x{addr:X}{symStr}, handle={handle.Value}";
    }

    private string ExecSetWatchpoint(JsonElement args, PluginBreakpointType type)
    {
        if (!_api.IsBreakState) return "Error: Process must be in break state";

        var addr = ParseAddress(args.GetProperty("address").GetString()!);
        uint len = args.TryGetProperty("length", out var le) ? le.GetUInt32() : 1;
        if (len is not (1 or 2 or 4 or 8)) return "Error: length must be 1, 2, 4 or 8";

        var handle = _api.Breakpoints.SetBreakpoint(_api.TargetPid, _api.SelectedThreadId, addr, type, len);
        if (handle == null)
            return $"Failed to set {type} watchpoint at 0x{addr:X}";

        var sym = _api.Symbols.ResolveAddress(addr);
        var symStr = sym != null ? $" ({sym})" : "";
        return $"{type} watchpoint ({len} bytes) at 0x{addr:X}{symStr}, handle={handle.Value}";
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

    // ── Memory ───────────────────────────────────────────────────────────────

    private string ExecReadMemory(JsonElement args)
    {
        var addr = ParseAddress(args.GetProperty("address").GetString()!);
        var size = args.GetProperty("size").GetUInt32();
        if (size > 512) size = 512;

        var data = _api.Memory.ReadMemory(_api.TargetPid, addr, size);
        if (data == null) return $"Failed to read memory at 0x{addr:X}";

        int printable = data.Count(b => b is >= 0x20 and < 0x7F);
        bool isStringLike = printable > data.Length / 2;

        var sb = new StringBuilder();
        sb.AppendLine($"[{data.Length} bytes at 0x{addr:X}]");

        if (isStringLike && data.Length <= 128)
        {
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

    private string ExecReadPointer(JsonElement args)
    {
        var addr    = ParseAddress(args.GetProperty("address").GetString()!);
        var ptrSize = _api.Is32Bit ? 4u : 8u;
        var data    = _api.Memory.ReadMemory(_api.TargetPid, addr, ptrSize);
        if (data == null) return $"Failed to read memory at 0x{addr:X}";

        ulong ptr = ptrSize == 8
            ? BitConverter.ToUInt64(data, 0)
            : BitConverter.ToUInt32(data, 0);

        var sym = _api.Symbols.ResolveAddress(ptr);
        var symStr = sym != null ? $" ({sym})" : "";
        return $"[0x{addr:X}] → 0x{ptr:X}{symStr}";
    }

    private string ExecReadString(JsonElement args, bool unicode)
    {
        var addr    = ParseAddress(args.GetProperty("address").GetString()!);
        var readLen = unicode ? 512u : 256u;
        var data    = _api.Memory.ReadMemory(_api.TargetPid, addr, readLen);
        if (data == null) return $"Failed to read memory at 0x{addr:X}";

        string result;
        if (unicode)
        {
            int end = 0;
            while (end + 1 < data.Length && (data[end] != 0 || data[end + 1] != 0)) end += 2;
            result = Encoding.Unicode.GetString(data, 0, end);
        }
        else
        {
            int end = Array.IndexOf(data, (byte)0);
            if (end < 0) end = data.Length;
            result = Encoding.Latin1.GetString(data, 0, end);
        }

        return $"[0x{addr:X}] \"{result}\"  ({(unicode ? "UTF-16LE" : "ASCII")}, {result.Length} chars)";
    }

    private string ExecWriteMemory(JsonElement args)
    {
        if (!_api.IsBreakState) return "Error: Process must be in break state";

        var addr   = ParseAddress(args.GetProperty("address").GetString()!);
        var hexStr = args.GetProperty("hex_bytes").GetString()!;

        var clean = hexStr.Replace(" ", "").Replace("-", "");
        if (clean.Length % 2 != 0) return "Error: Invalid hex string length";

        var bytes = new byte[clean.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = byte.Parse(clean.Substring(i * 2, 2), System.Globalization.NumberStyles.HexNumber);

        var ok = _api.Memory.WriteMemory(_api.TargetPid, addr, bytes);
        return ok ? $"Wrote {bytes.Length} bytes to 0x{addr:X}" : $"Failed to write memory at 0x{addr:X}";
    }

    private string ExecSearchMemory(JsonElement args)
    {
        var start   = ParseAddress(args.GetProperty("start").GetString()!);
        var size    = Math.Min((uint)args.GetProperty("size").GetInt64(), 16u * 1024 * 1024);
        var patStr  = args.GetProperty("pattern").GetString()!.Trim();

        var tokens  = patStr.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var pattern = new byte?[tokens.Length];
        for (int i = 0; i < tokens.Length; i++)
            pattern[i] = tokens[i] == "??" ? null : byte.Parse(tokens[i], System.Globalization.NumberStyles.HexNumber);

        if (pattern.Length == 0) return "Error: empty pattern";

        const uint chunkSize = 64 * 1024;
        var results = new List<ulong>();
        ulong scanned = 0;
        int overlap = pattern.Length - 1;
        byte[]? prev = null;

        while (scanned < size && results.Count < 100)
        {
            var read  = (uint)Math.Min(chunkSize, size - scanned);
            var chunk = _api.Memory.ReadMemory(_api.TargetPid, start + scanned, read);
            if (chunk == null) break;

            byte[] buf;
            ulong  bufBase;
            if (prev != null && overlap > 0)
            {
                int off = Math.Max(0, prev.Length - overlap);
                buf = new byte[prev.Length - off + chunk.Length];
                Buffer.BlockCopy(prev, off, buf, 0, prev.Length - off);
                Buffer.BlockCopy(chunk, 0, buf, prev.Length - off, chunk.Length);
                bufBase = start + scanned - (ulong)(prev.Length - off);
            }
            else
            {
                buf     = chunk;
                bufBase = start + scanned;
            }

            for (int i = 0; i <= buf.Length - pattern.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (pattern[j] is byte b && buf[i + j] != b) { match = false; break; }
                }
                if (match)
                {
                    var hitAddr = bufBase + (ulong)i;
                    if (hitAddr >= start && hitAddr < start + size)
                        results.Add(hitAddr);
                }
            }

            prev     = chunk;
            scanned += read;
        }

        if (results.Count == 0) return $"Pattern not found in 0x{start:X}..0x{start + size:X}";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {results.Count} match(es):{(results.Count >= 100 ? " (stopped at 100)" : "")}");
        foreach (var hit in results)
        {
            var sym = _api.Symbols.ResolveAddress(hit);
            var symStr = sym != null ? $" ({sym})" : "";
            sb.AppendLine($"  0x{hit:X}{symStr}");
        }
        return sb.ToString();
    }

    private string ExecReadRegisters()
    {
        if (!_api.IsBreakState) return "Error: Process must be in break state";

        var regs = _api.Memory.ReadRegisters(_api.TargetPid, _api.SelectedThreadId);
        if (regs == null || regs.Count == 0) return "Failed to read registers";

        var sb = new StringBuilder();
        foreach (var r in regs.Where(r => !r.IsFlag))
            sb.AppendLine($"{r.Name,-6} = 0x{r.Value:X16}");

        var flagStr = string.Join(" ", regs.Where(r => r.IsFlag && r.Value != 0).Select(r => r.Name));
        if (!string.IsNullOrEmpty(flagStr))
            sb.AppendLine($"FLAGS: {flagStr}");

        return sb.ToString();
    }

    private string ExecWriteRip(JsonElement args)
    {
        if (!_api.IsBreakState) return "Error: Process must be in break state";
        var addr = ParseAddress(args.GetProperty("address").GetString()!);
        var ok   = _api.Memory.WriteRip(_api.TargetPid, _api.SelectedThreadId, addr);
        var sym  = _api.Symbols.ResolveAddress(addr);
        var symStr = sym != null ? $" ({sym})" : "";
        return ok ? $"RIP set to 0x{addr:X}{symStr}" : $"Failed to set RIP to 0x{addr:X}";
    }

    private string ExecAllocateMemory(JsonElement args)
    {
        if (!_api.IsBreakState) return "Error: Process must be in break state";
        var size = (ulong)args.GetProperty("size").GetInt64();
        var addr = _api.Memory.AllocateMemory(_api.TargetPid, size);
        return addr != 0
            ? $"Allocated {size} bytes at 0x{addr:X} (RWX)"
            : "Failed to allocate memory";
    }

    private string ExecFreeMemory(JsonElement args)
    {
        if (!_api.IsBreakState) return "Error: Process must be in break state";
        var addr = ParseAddress(args.GetProperty("address").GetString()!);
        return _api.Memory.FreeMemory(_api.TargetPid, addr)
            ? $"Freed memory at 0x{addr:X}"
            : $"Failed to free memory at 0x{addr:X}";
    }

    private string ExecProtectMemory(JsonElement args)
    {
        if (!_api.IsBreakState) return "Error: Process must be in break state";
        var addr = ParseAddress(args.GetProperty("address").GetString()!);
        var size = (uint)args.GetProperty("size").GetInt64();
        var prot = (uint)args.GetProperty("protection").GetInt64();
        var (ok, old) = _api.Memory.ProtectMemory(_api.TargetPid, addr, size, prot);
        return ok
            ? $"Protection at 0x{addr:X}+0x{size:X}: 0x{old:X} → 0x{prot:X}"
            : $"Failed to change protection at 0x{addr:X}";
    }

    // ── Disassembly / Decompilation ──────────────────────────────────────────

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

    private string ExecNavigateDisasm(JsonElement args)
    {
        var addr = ParseAddress(args.GetProperty("address").GetString()!);
        _api.UI.NavigateDisassembly(addr);
        return $"Navigated disassembly view to 0x{addr:X}";
    }

    private string ExecDisasmGoBack()
    {
        _api.UI.DisasmGoBack();
        return "Navigated back to previous disassembly location";
    }

    private string ExecDecompile(JsonElement args)
    {
        if (!_api.IsBreakState) return "Error: Process must be in break state";

        var addr = ParseAddress(args.GetProperty("address").GetString()!);
        _api.UI.DecompileFunction(addr);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        string lastCode = _api.UI.GetDecompiledCode();

        while (sw.ElapsedMilliseconds < 30000)
        {
            Thread.Sleep(200);
            var code = _api.UI.GetDecompiledCode();
            if (!string.IsNullOrEmpty(code) && code != lastCode && !code.Contains("Decompiling..."))
            {
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

    // ── Symbols / Modules ────────────────────────────────────────────────────

    private string ExecResolveSymbol(JsonElement args)
    {
        var name = args.GetProperty("name").GetString()!.Trim();

        if (name.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || name.All(c => "0123456789abcdefABCDEF".Contains(c)))
        {
            var addr = ParseAddress(name);
            var sym  = _api.Symbols.ResolveAddress(addr);
            return sym != null ? $"0x{addr:X} = {sym}" : $"No symbol found at 0x{addr:X}";
        }

        var resolved = _api.Symbols.ResolveNameToAddress(name);
        return resolved != 0 ? $"{name} = 0x{resolved:X}" : $"Symbol '{name}' not found";
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

    private string ExecListKernelModules()
    {
        var mods = _api.Symbols.GetKernelModules();
        if (mods == null || mods.Count == 0) return "No kernel modules found";

        var sb = new StringBuilder();
        foreach (var m in mods)
            sb.AppendLine($"0x{m.BaseAddress:X16}+0x{m.Size:X8}  #{m.LoadOrder,-4}  {m.Name}");
        return sb.ToString();
    }

    // ── Process / Threads ────────────────────────────────────────────────────

    private string ExecListProcesses()
    {
        var procs = _api.Process.EnumProcesses();
        if (procs == null || procs.Count == 0) return "No processes found";

        var sb = new StringBuilder();
        foreach (var p in procs.OrderBy(x => x.ProcessId))
            sb.AppendLine($"PID={p.ProcessId,6}  Session={p.SessionId}  {p.Name}");
        return sb.ToString();
    }

    private string ExecListThreads()
    {
        var threads = _api.Process.EnumThreads(_api.TargetPid);
        if (threads == null || threads.Count == 0) return "No threads found";

        var sb = new StringBuilder();
        foreach (var t in threads)
        {
            var sym     = _api.Symbols.ResolveAddress(t.StartAddress);
            var symStr  = sym != null ? $"  ({sym})" : "";
            var current = t.ThreadId == _api.SelectedThreadId ? " <<< current" : "";
            sb.AppendLine($"TID={t.ThreadId}  Start=0x{t.StartAddress:X}{symStr}  State={t.State}{current}");
        }
        return sb.ToString();
    }

    private string ExecSuspendThread(JsonElement args)
    {
        var tid = (uint)args.GetProperty("tid").GetInt64();
        return _api.Process.SuspendThread(tid)
            ? $"Thread {tid} suspended"
            : $"Failed to suspend thread {tid}";
    }

    private string ExecResumeThread(JsonElement args)
    {
        var tid = (uint)args.GetProperty("tid").GetInt64();
        return _api.Process.ResumeThread(tid)
            ? $"Thread {tid} resumed"
            : $"Failed to resume thread {tid}";
    }

    private string ExecGetPebAddress()
    {
        var (peb64, peb32) = _api.Process.GetPebAddress(_api.TargetPid);
        var sb = new StringBuilder();
        sb.AppendLine($"PEB64 = 0x{peb64:X}");
        if (peb32 != 0) sb.AppendLine($"PEB32 = 0x{peb32:X}");
        return sb.ToString();
    }

    // ── Execution control ────────────────────────────────────────────────────

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
        var sym    = _api.Symbols.ResolveAddress(addr);
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
                var rip  = regs?.FirstOrDefault(r => r.Name is "RIP" or "EIP")?.Value ?? 0;
                var sym  = rip != 0 ? _api.Symbols.ResolveAddress(rip) : null;
                var symStr = sym != null ? $" ({sym})" : "";
                return $"Break at RIP=0x{rip:X}{symStr} after {sw.ElapsedMilliseconds}ms";
            }
            Thread.Sleep(20);
        }

        return $"Timeout after {timeoutMs}ms — process is still running. Use pause_execution to force stop.";
    }

    // ── Anti-debug bypass ────────────────────────────────────────────────────

    private string ExecClearDebugPort()
    {
        return _api.Process.ClearDebugPort(_api.TargetPid)
            ? "DebugPort cleared — NtQueryInformationProcess(ProcessDebugPort) will return 0"
            : "Failed to clear DebugPort";
    }

    private string ExecClearThreadHide()
    {
        return _api.Process.ClearThreadHide(_api.TargetPid)
            ? "HideFromDebugger cleared on all threads"
            : "Failed to clear thread hide flags";
    }

    private string ExecInstallNtQsiHook()
    {
        return _api.Process.InstallNtQsiHook()
            ? "NtQuerySystemInformation hook installed — debugger process hidden from process lists"
            : "Failed to install NtQSI hook";
    }

    private string ExecRemoveNtQsiHook()
    {
        return _api.Process.RemoveNtQsiHook()
            ? "NtQuerySystemInformation hook removed"
            : "Failed to remove NtQSI hook";
    }

    private string ExecProbeNtQsiHook()
    {
        return _api.Process.ProbeNtQsiHook();
    }

    private string ExecSpoofSharedUserData(JsonElement args)
    {
        var enable = args.GetProperty("enable").GetBoolean();
        return _api.Process.SetSpoofSharedUserData(enable)
            ? $"SharedUserData spoofing {(enable ? "enabled" : "disabled")}"
            : $"Failed to {(enable ? "enable" : "disable")} SharedUserData spoofing";
    }

    // ── UI helpers ────────────────────────────────────────────────────────────

    private string ExecAddUnpackedModule(JsonElement args)
    {
        var peBase = ParseAddress(args.GetProperty("pe_base").GetString()!);
        var name   = args.GetProperty("name").GetString()!;
        _api.UI.AddUnpackedModule(peBase, name);
        return $"Registered unpacked module '{name}' at 0x{peBase:X}";
    }

    private string ExecRefreshModules()
    {
        _api.UI.RefreshModulesAndSections();
        return "Module list refreshed";
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private ulong ParseAddress(string s)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            s = s[2..];
        return ulong.Parse(s, System.Globalization.NumberStyles.HexNumber);
    }

    private static object MakeTool(string name, string desc, object parameters) => new
    {
        type = "function",
        function = new { name, description = desc, parameters }
    };
}
