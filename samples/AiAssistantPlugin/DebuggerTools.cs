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

        MakeTool("write_rip_and_rsp",
            "Redirect execution by changing both RIP and RSP atomically (useful for IAT unpack / hijack)",
            new { type = "object", properties = new {
                rip = new { type = "string", description = "New RIP value (hex)" },
                rsp = new { type = "string", description = "New RSP value (hex)" }
            }, required = new[] { "rip", "rsp" } }),

        MakeTool("add_module_sections",
            "Manually provide section table for a module when PE header is destroyed by a packer",
            new { type = "object", properties = new {
                module_name = new { type = "string", description = "Module name (must already be in module list)" },
                sections    = new { type = "string", description = "JSON array: [{\"name\":\".text\",\"va\":\"0x1000\",\"vsize\":4096,\"chr\":0x60000020}, ...]" }
            }, required = new[] { "module_name", "sections" } }),

        // ── High-level analysis ─────────────────────────────────────────
        MakeTool("dump_stack",
            "Read the current stack (from RSP) and display each QWORD with symbol resolution",
            new { type = "object", properties = new {
                count = new { type = "integer", description = "Number of QWORD entries (default 16, max 64)" }
            } }),

        MakeTool("dump_peb",
            "Parse and display key PEB fields: ImageBase, Ldr, BeingDebugged, NtGlobalFlag, ProcessParameters, Heap, OS",
            new { type = "object", properties = new { } }),

        MakeTool("dump_teb",
            "Parse and display key TEB fields: StackBase, StackLimit, PEB, LastError, ThreadId",
            new { type = "object", properties = new { } }),

        MakeTool("dump_pe_header",
            "Parse DOS/PE headers and section table at a base address. Shows EntryPoint, sections, data directories.",
            new { type = "object", properties = new {
                address = new { type = "string", description = "Hex base address of PE (e.g. module base)" }
            }, required = new[] { "address" } }),

        MakeTool("dump_imports",
            "Parse the Import Address Table (IAT) of a PE. Shows each imported DLL and its functions.",
            new { type = "object", properties = new {
                address = new { type = "string", description = "Hex base address of the PE" }
            }, required = new[] { "address" } }),

        MakeTool("dump_exports",
            "Parse the Export Directory of a PE/DLL. Shows all exported function names, ordinals, and RVAs.",
            new { type = "object", properties = new {
                address = new { type = "string", description = "Hex base address of the PE" }
            }, required = new[] { "address" } }),

        MakeTool("xrefs_to",
            "Scan .text section for CALL/JMP/LEA references to a target address (max 100 results)",
            new { type = "object", properties = new {
                address = new { type = "string", description = "Hex target address to find references to" }
            }, required = new[] { "address" } }),

        MakeTool("nop_instruction",
            "NOP-out the instruction at address. Reads instruction length and replaces with 0x90 bytes.",
            new { type = "object", properties = new {
                address = new { type = "string", description = "Hex address of the instruction to NOP" }
            }, required = new[] { "address" } }),

        MakeTool("patch_jump",
            "Force a conditional jump to always-jump or never-jump. 'always' = JMP, 'never' = NOPs.",
            new { type = "object", properties = new {
                address = new { type = "string", description = "Hex address of the conditional jump" },
                mode    = new { type = "string", description = "'always' = force jump, 'never' = NOP" }
            }, required = new[] { "address", "mode" } }),

        MakeTool("list_strings",
            "Scan a memory range for printable ASCII and Unicode strings (like the 'strings' utility)",
            new { type = "object", properties = new {
                address    = new { type = "string",  description = "Hex start address (default: main module .rdata)" },
                size       = new { type = "integer", description = "Range size in bytes (default: .rdata size, max 1 MB)" },
                min_length = new { type = "integer", description = "Minimum string length (default 4)" }
            } }),

        MakeTool("compare_memory",
            "Compare two memory regions byte-by-byte and show differences",
            new { type = "object", properties = new {
                addr1 = new { type = "string",  description = "Hex address of first region" },
                addr2 = new { type = "string",  description = "Hex address of second region" },
                size  = new { type = "integer", description = "Number of bytes to compare (max 4096)" }
            }, required = new[] { "addr1", "addr2", "size" } }),

        MakeTool("read_unicode_struct",
            "Read a UNICODE_STRING structure (Length + MaxLength + Buffer pointer) and return the string",
            new { type = "object", properties = new {
                address = new { type = "string", description = "Hex address of the UNICODE_STRING struct" }
            }, required = new[] { "address" } }),

        // ── Notes / Bookmarks ─────────────────────────────────────────────
        MakeTool("write_note",
            "Add or update a note/bookmark at an address. Shown as a comment in the disassembly " +
            "and persisted between sessions. Use to annotate functions, suspicious code, or findings.",
            new { type = "object", properties = new {
                address = new { type = "string", description = "Hex address" },
                note    = new { type = "string", description = "Note text" }
            }, required = new[] { "address", "note" } }),

        MakeTool("read_note",
            "Read the note at a specific address. Returns the note text or empty if none.",
            new { type = "object", properties = new {
                address = new { type = "string", description = "Hex address" }
            }, required = new[] { "address" } }),

        MakeTool("read_all_notes",
            "Read all notes/bookmarks. Returns all annotated addresses with notes. " +
            "Useful to get context from previous analysis sessions.",
            new { type = "object", properties = new { } }),

        MakeTool("execute_script",
            "Execute a C# script in the Scripting plugin REPL. Variables persist between calls. " +
            "Use 'scripting_reference' tool first to learn the API. " +
            "IMPORTANT: After decompiling, unnamed functions appear as 'module.exe+0xOFFSET'. Use this to name them: " +
            "var b = api.Symbols.GetModules()[0].BaseAddress; api.Symbols.RegisterFunction(b + 0xOFFSET, \"Name\"); api.UI.RefreshDisassembly(); " +
            "Names will appear in disassembly and Graph View. " +
            "Shortcuts: api, print(), ReadMem(), WriteMem(), ReadString(), ReadPtr(), Reg(), RIP, RSP, Sym(), Addr()",
            new { type = "object", properties = new {
                code = new { type = "string", description = "C# code to execute" }
            }, required = new[] { "code" } }),

        MakeTool("scripting_reference",
            "Get the complete C# scripting API reference. Call BEFORE writing scripts.",
            new { type = "object", properties = new { } }),

        MakeTool("remove_note",
            "Remove a note/bookmark at an address.",
            new { type = "object", properties = new {
                address = new { type = "string", description = "Hex address" }
            }, required = new[] { "address" } }),
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
                "write_rip_and_rsp"        => ExecWriteRipAndRsp(root),
                "add_module_sections"      => ExecAddModuleSections(root),

                // High-level analysis
                "dump_stack"               => ExecDumpStack(root),
                "dump_peb"                 => ExecDumpPeb(),
                "dump_teb"                 => ExecDumpTeb(),
                "dump_pe_header"           => ExecDumpPeHeader(root),
                "dump_imports"             => ExecDumpImports(root),
                "dump_exports"             => ExecDumpExports(root),
                "xrefs_to"                 => ExecXrefsTo(root),
                "nop_instruction"          => ExecNopInstruction(root),
                "patch_jump"               => ExecPatchJump(root),
                "list_strings"             => ExecListStrings(root),
                "compare_memory"           => ExecCompareMemory(root),
                "read_unicode_struct"      => ExecReadUnicodeStruct(root),

                // Notes / Bookmarks
                "write_note"               => ExecWriteNote(root),
                "read_note"                => ExecReadNote(root),
                "read_all_notes"           => ExecReadAllNotes(),
                "remove_note"              => ExecRemoveNote(root),
                "execute_script"           => ExecScript(root).GetAwaiter().GetResult(),
                "scripting_reference"      => ExecScriptingReference(),

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

    private ulong _ripBeforeResume;

    private void SnapshotRip()
    {
        var regs = _api.Memory.ReadRegisters(_api.TargetPid, _api.SelectedThreadId);
        _ripBeforeResume = regs?.FirstOrDefault(r => r.Name is "RIP" or "EIP")?.Value ?? 0;
    }

    private string ExecContinue()
    {
        if (!_api.IsBreakState) return "Error: Process is not in break state";
        SnapshotRip();
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
        SnapshotRip();
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

        var sw       = System.Diagnostics.Stopwatch.StartNew();
        var startRip = _ripBeforeResume;

        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (_api.IsBreakState)
            {
                var regs = _api.Memory.ReadRegisters(_api.TargetPid, _api.SelectedThreadId);
                var rip  = regs?.FirstOrDefault(r => r.Name is "RIP" or "EIP")?.Value ?? 0;

                if (rip != startRip || sw.ElapsedMilliseconds > 500)
                {
                    var sym    = rip != 0 ? _api.Symbols.ResolveAddress(rip) : null;
                    var symStr = sym != null ? $" ({sym})" : "";
                    return $"Break at RIP=0x{rip:X}{symStr} after {sw.ElapsedMilliseconds}ms";
                }
            }
            Thread.Sleep(30);
        }

        if (_api.IsBreakState)
        {
            var regs = _api.Memory.ReadRegisters(_api.TargetPid, _api.SelectedThreadId);
            var rip  = regs?.FirstOrDefault(r => r.Name is "RIP" or "EIP")?.Value ?? 0;
            var sym  = rip != 0 ? _api.Symbols.ResolveAddress(rip) : null;
            var symStr = sym != null ? $" ({sym})" : "";
            return $"Break at RIP=0x{rip:X}{symStr} after {sw.ElapsedMilliseconds}ms";
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

    // ── Extended commands ──────────────────────────────────────────────────

    private string ExecWriteRipAndRsp(JsonElement args)
    {
        if (!_api.IsBreakState) return "Error: Process must be in break state";
        var rip = ParseAddress(args.GetProperty("rip").GetString()!);
        var rsp = ParseAddress(args.GetProperty("rsp").GetString()!);
        var ok  = _api.Memory.WriteRipAndRsp(_api.SelectedThreadId, rip, rsp);
        var sym = _api.Symbols.ResolveAddress(rip);
        var symStr = sym != null ? $" ({sym})" : "";
        return ok ? $"RIP=0x{rip:X}{symStr}, RSP=0x{rsp:X}" : "Failed to set RIP/RSP";
    }

    private string ExecAddModuleSections(JsonElement args)
    {
        var modName  = args.GetProperty("module_name").GetString()!;
        var secJson  = args.GetProperty("sections").GetString()!;
        var sections = JsonSerializer.Deserialize<JsonElement[]>(secJson)!;
        var list     = new List<PluginSectionInfo>();
        foreach (var s in sections)
        {
            list.Add(new PluginSectionInfo
            {
                Name            = s.GetProperty("name").GetString()!,
                VirtualAddress  = (uint)ParseAddress(s.GetProperty("va").GetString()!),
                VirtualSize     = (uint)s.GetProperty("vsize").GetInt64(),
                Characteristics = (uint)s.GetProperty("chr").GetInt64()
            });
        }
        _api.UI.AddModuleSections(modName, list);
        return $"Added {list.Count} sections to '{modName}'";
    }

    private string ExecDumpStack(JsonElement args)
    {
        if (!_api.IsBreakState) return "Error: Process must be in break state";
        int count = 16;
        if (args.TryGetProperty("count", out var c)) count = Math.Min(c.GetInt32(), 64);
        var ptrSize = _api.Is32Bit ? 4u : 8u;

        var regs = _api.Memory.ReadRegisters(_api.TargetPid, _api.SelectedThreadId);
        var rsp  = regs?.FirstOrDefault(r => r.Name is "RSP" or "ESP")?.Value ?? 0;
        if (rsp == 0) return "Failed to read RSP";

        var data = _api.Memory.ReadMemory(_api.TargetPid, rsp, (uint)(count * ptrSize));
        if (data == null) return $"Failed to read stack at 0x{rsp:X}";

        var sb = new StringBuilder();
        sb.AppendLine($"Stack dump from RSP=0x{rsp:X}:");
        for (int i = 0; i < count && i * (int)ptrSize < data.Length; i++)
        {
            ulong val = ptrSize == 8
                ? BitConverter.ToUInt64(data, i * 8)
                : BitConverter.ToUInt32(data, i * 4);
            var sym    = _api.Symbols.ResolveAddress(val);
            var symStr = sym != null ? $" ({sym})" : "";
            var tag    = i == 0 ? " <<< RSP" : "";
            sb.AppendLine($"  [RSP+0x{i * ptrSize:X2}]  0x{val:X16}{symStr}{tag}");
        }
        return sb.ToString();
    }

    private string ExecDumpPeb()
    {
        if (!_api.IsBreakState) return "Error: Process must be in break state";
        var (peb64, _) = _api.Process.GetPebAddress(_api.TargetPid);
        if (peb64 == 0) return "Failed to get PEB address";

        var data = _api.Memory.ReadMemory(_api.TargetPid, peb64, 0x400);
        if (data == null) return $"Failed to read PEB at 0x{peb64:X}";

        var sb = new StringBuilder();
        sb.AppendLine($"PEB @ 0x{peb64:X}");

        byte beingDebugged = data[2];
        ulong imageBase = BitConverter.ToUInt64(data, 0x10);
        ulong ldr       = BitConverter.ToUInt64(data, 0x18);
        ulong procParms = BitConverter.ToUInt64(data, 0x20);
        ulong heap      = BitConverter.ToUInt64(data, 0x30);
        uint  ntGlobal  = BitConverter.ToUInt32(data, 0xBC);
        uint  numProc   = BitConverter.ToUInt32(data, 0xB8);
        uint  osMajor   = BitConverter.ToUInt32(data, 0x118);
        uint  osMinor   = BitConverter.ToUInt32(data, 0x11C);

        sb.AppendLine($"  BeingDebugged    : {beingDebugged}");
        sb.AppendLine($"  ImageBaseAddress : 0x{imageBase:X}");
        sb.AppendLine($"  Ldr              : 0x{ldr:X}");
        sb.AppendLine($"  ProcessParameters: 0x{procParms:X}");
        sb.AppendLine($"  ProcessHeap      : 0x{heap:X}");
        sb.AppendLine($"  NtGlobalFlag     : 0x{ntGlobal:X}{(ntGlobal == 0 ? " (clean)" : "")}");
        sb.AppendLine($"  NumberOfProcessors: {numProc}");
        sb.AppendLine($"  OSVersion        : {osMajor}.{osMinor}");

        if (procParms != 0)
        {
            var pp = _api.Memory.ReadMemory(_api.TargetPid, procParms, 0x100);
            if (pp != null && pp.Length >= 0x80)
            {
                ushort imgLen = BitConverter.ToUInt16(pp, 0x60);
                ulong  imgBuf = BitConverter.ToUInt64(pp, 0x68);
                ushort cmdLen = BitConverter.ToUInt16(pp, 0x70);
                ulong  cmdBuf = BitConverter.ToUInt64(pp, 0x78);
                if (imgBuf != 0 && imgLen > 0)
                {
                    var s = _api.Memory.ReadMemory(_api.TargetPid, imgBuf, imgLen);
                    if (s != null) sb.AppendLine($"  ImagePathName    : {Encoding.Unicode.GetString(s)}");
                }
                if (cmdBuf != 0 && cmdLen > 0)
                {
                    var s = _api.Memory.ReadMemory(_api.TargetPid, cmdBuf, Math.Min(cmdLen, (ushort)512));
                    if (s != null) sb.AppendLine($"  CommandLine      : {Encoding.Unicode.GetString(s)}");
                }
            }
        }
        return sb.ToString();
    }

    private string ExecDumpTeb()
    {
        if (!_api.IsBreakState) return "Error: Process must be in break state";
        var regs = _api.Memory.ReadRegisters(_api.TargetPid, _api.SelectedThreadId);
        // TEB is at gs:[0x30] on x64 — we read it from the GS_BASE register or known offset
        // Simpler: read the self-pointer at TEB+0x30
        // For x64: TEB address can be obtained by reading gs:[0x30]
        // We'll use a different approach: read GS base from segment registers if available,
        // or calculate from known TEB structure
        var gsBase = regs?.FirstOrDefault(r => r.Name == "GS_BASE")?.Value ?? 0;
        // If no GS_BASE register, try reading TEB from the PEB thread data
        if (gsBase == 0)
        {
            // Fallback: read TEB self-pointer via NtCurrentTeb pattern
            // The TEB self-pointer is at offset 0x30 from TEB base
            // We can get it from the thread info
            return "Error: Could not determine TEB address (GS_BASE not available)";
        }

        var data = _api.Memory.ReadMemory(_api.TargetPid, gsBase, 0x100);
        if (data == null) return $"Failed to read TEB at 0x{gsBase:X}";

        var sb = new StringBuilder();
        sb.AppendLine($"TEB @ 0x{gsBase:X}");
        ulong stackBase  = BitConverter.ToUInt64(data, 0x08);
        ulong stackLimit = BitConverter.ToUInt64(data, 0x10);
        ulong self       = BitConverter.ToUInt64(data, 0x30);
        ulong peb        = BitConverter.ToUInt64(data, 0x60);
        uint  lastErr    = BitConverter.ToUInt32(data, 0x68);
        uint  tid        = BitConverter.ToUInt32(data, 0x48);
        uint  pid        = BitConverter.ToUInt32(data, 0x40);
        ushort flags     = BitConverter.ToUInt16(data, 0xEF + 1); // SameTebFlags at 0x17EE for Win10+

        sb.AppendLine($"  Self            : 0x{self:X}");
        sb.AppendLine($"  ProcessId       : {pid}");
        sb.AppendLine($"  ThreadId        : {tid}");
        sb.AppendLine($"  StackBase       : 0x{stackBase:X}");
        sb.AppendLine($"  StackLimit      : 0x{stackLimit:X}");
        sb.AppendLine($"  Stack size      : 0x{stackBase - stackLimit:X} ({(stackBase - stackLimit) / 1024} KB)");
        sb.AppendLine($"  PEB             : 0x{peb:X}");
        sb.AppendLine($"  LastErrorValue  : 0x{lastErr:X} ({lastErr})");
        return sb.ToString();
    }

    private string ExecDumpPeHeader(JsonElement args)
    {
        var baseAddr = ParseAddress(args.GetProperty("address").GetString()!);
        var data = _api.Memory.ReadMemory(_api.TargetPid, baseAddr, 0x1000);
        if (data == null || data.Length < 0x40) return $"Failed to read PE at 0x{baseAddr:X}";
        if (data[0] != 'M' || data[1] != 'Z') return $"Not a valid PE — no MZ signature at 0x{baseAddr:X}";

        uint peOff = BitConverter.ToUInt32(data, 0x3C);
        if (peOff + 0x18 > data.Length) return "PE offset out of range";

        ushort magic = BitConverter.ToUInt16(data, (int)peOff + 0x18);
        bool pe32p   = magic == 0x20B;
        int optOff   = (int)peOff + 0x18;

        uint ep       = BitConverter.ToUInt32(data, optOff + 0x10);
        uint imgSize  = BitConverter.ToUInt32(data, optOff + 0x38);
        ushort numSec = BitConverter.ToUInt16(data, (int)peOff + 6);
        ushort optSize = BitConverter.ToUInt16(data, (int)peOff + 0x14);

        var sb = new StringBuilder();
        sb.AppendLine($"PE @ 0x{baseAddr:X}");
        sb.AppendLine($"  Magic           : 0x{magic:X} ({(pe32p ? "PE32+ (x64)" : "PE32 (x86)")})");
        sb.AppendLine($"  EntryPoint RVA  : 0x{ep:X} → 0x{baseAddr + ep:X}");
        sb.AppendLine($"  ImageSize       : 0x{imgSize:X}");
        sb.AppendLine($"  Sections        : {numSec}");

        // Data directories
        int ddOff = optOff + (pe32p ? 0x70 : 0x60);
        int ddCount = Math.Min(BitConverter.ToInt32(data, optOff + (pe32p ? 0x6C : 0x5C)), 16);
        string[] ddNames = ["Export","Import","Resource","Exception","Security","BaseReloc",
                            "Debug","Architecture","GlobalPtr","TLS","LoadConfig","BoundImport",
                            "IAT","DelayImport","CLR","Reserved"];
        sb.AppendLine("  Data Directories:");
        for (int i = 0; i < ddCount && ddOff + i * 8 + 8 <= data.Length; i++)
        {
            uint rva  = BitConverter.ToUInt32(data, ddOff + i * 8);
            uint size = BitConverter.ToUInt32(data, ddOff + i * 8 + 4);
            if (rva != 0)
                sb.AppendLine($"    [{i,2}] {(i < ddNames.Length ? ddNames[i] : "?"),-14}  RVA=0x{rva:X8}  Size=0x{size:X}");
        }

        // Section table
        int secOff = (int)peOff + 0x18 + optSize;
        sb.AppendLine("  Sections:");
        for (int i = 0; i < numSec && secOff + i * 40 + 40 <= data.Length; i++)
        {
            int o    = secOff + i * 40;
            var name = Encoding.ASCII.GetString(data, o, 8).TrimEnd('\0');
            uint vs  = BitConverter.ToUInt32(data, o + 8);
            uint va  = BitConverter.ToUInt32(data, o + 12);
            uint rs  = BitConverter.ToUInt32(data, o + 16);
            uint chr = BitConverter.ToUInt32(data, o + 36);
            string flags = ((chr & 0x20000000) != 0 ? "X" : "") +
                           ((chr & 0x40000000) != 0 ? "R" : "") +
                           ((chr & 0x80000000) != 0 ? "W" : "");
            sb.AppendLine($"    {name,-10} VA=0x{va:X8}  VSize=0x{vs:X8}  RSize=0x{rs:X8}  {flags}");
        }
        return sb.ToString();
    }

    private string ExecDumpImports(JsonElement args)
    {
        var baseAddr = ParseAddress(args.GetProperty("address").GetString()!);
        var hdr = _api.Memory.ReadMemory(_api.TargetPid, baseAddr, 0x1000);
        if (hdr == null || hdr.Length < 0x40 || hdr[0] != 'M' || hdr[1] != 'Z')
            return $"Not a valid PE at 0x{baseAddr:X}";

        uint peOff   = BitConverter.ToUInt32(hdr, 0x3C);
        ushort magic = BitConverter.ToUInt16(hdr, (int)peOff + 0x18);
        bool pe32p   = magic == 0x20B;
        int ddOff    = (int)peOff + 0x18 + (pe32p ? 0x70 : 0x60);
        uint impRva  = BitConverter.ToUInt32(hdr, ddOff + 8);  // Import DD is index 1
        uint impSize = BitConverter.ToUInt32(hdr, ddOff + 12);
        if (impRva == 0) return "No import directory";

        uint iatRva = BitConverter.ToUInt32(hdr, ddOff + 12 * 8);  // IAT DD is index 12

        var impData = _api.Memory.ReadMemory(_api.TargetPid, baseAddr + impRva, Math.Max(impSize, 4096u));
        if (impData == null) return "Failed to read import directory";

        var sb = new StringBuilder();
        sb.AppendLine("Import Directory:");
        int ptrSize = pe32p ? 8 : 4;

        for (int desc = 0; desc + 20 <= impData.Length; desc += 20)
        {
            uint iltRva  = BitConverter.ToUInt32(impData, desc);
            uint nameRva = BitConverter.ToUInt32(impData, desc + 12);
            uint iatRvaE = BitConverter.ToUInt32(impData, desc + 16);
            if (nameRva == 0) break;

            var nameBytes = _api.Memory.ReadMemory(_api.TargetPid, baseAddr + nameRva, 128);
            var dllName = nameBytes != null
                ? Encoding.ASCII.GetString(nameBytes, 0, Array.IndexOf(nameBytes, (byte)0) is int idx && idx >= 0 ? idx : nameBytes.Length)
                : "???";

            sb.AppendLine($"\n  {dllName}  (IAT=0x{iatRvaE:X}, ILT=0x{iltRva:X})");

            // Read IAT entries
            var iat = _api.Memory.ReadMemory(_api.TargetPid, baseAddr + iatRvaE, 512);
            if (iat == null) continue;

            for (int i = 0; i * ptrSize + ptrSize <= iat.Length; i++)
            {
                ulong entry = pe32p
                    ? BitConverter.ToUInt64(iat, i * 8)
                    : BitConverter.ToUInt32(iat, i * 4);
                if (entry == 0) break;

                var sym = _api.Symbols.ResolveAddress(entry);
                sb.AppendLine($"    [{i,3}] 0x{entry:X} {sym ?? ""}");
            }
        }
        return sb.ToString();
    }

    private string ExecDumpExports(JsonElement args)
    {
        var baseAddr = ParseAddress(args.GetProperty("address").GetString()!);
        var hdr = _api.Memory.ReadMemory(_api.TargetPid, baseAddr, 0x1000);
        if (hdr == null || hdr.Length < 0x40 || hdr[0] != 'M' || hdr[1] != 'Z')
            return $"Not a valid PE at 0x{baseAddr:X}";

        uint peOff   = BitConverter.ToUInt32(hdr, 0x3C);
        ushort magic = BitConverter.ToUInt16(hdr, (int)peOff + 0x18);
        bool pe32p   = magic == 0x20B;
        int ddOff    = (int)peOff + 0x18 + (pe32p ? 0x70 : 0x60);
        uint expRva  = BitConverter.ToUInt32(hdr, ddOff);
        uint expSize = BitConverter.ToUInt32(hdr, ddOff + 4);
        if (expRva == 0) return "No export directory";

        var expData = _api.Memory.ReadMemory(_api.TargetPid, baseAddr + expRva, Math.Max(expSize, 4096u));
        if (expData == null || expData.Length < 40) return "Failed to read export directory";

        uint numFuncs   = BitConverter.ToUInt32(expData, 20);
        uint numNames   = BitConverter.ToUInt32(expData, 24);
        uint funcsRva   = BitConverter.ToUInt32(expData, 28);
        uint namesRva   = BitConverter.ToUInt32(expData, 32);
        uint ordinalsRva = BitConverter.ToUInt32(expData, 36);
        uint ordBase    = BitConverter.ToUInt32(expData, 16);

        var funcs = _api.Memory.ReadMemory(_api.TargetPid, baseAddr + funcsRva, numFuncs * 4);
        var names = _api.Memory.ReadMemory(_api.TargetPid, baseAddr + namesRva, numNames * 4);
        var ords  = _api.Memory.ReadMemory(_api.TargetPid, baseAddr + ordinalsRva, numNames * 2);
        if (funcs == null || names == null || ords == null) return "Failed to read export tables";

        var sb = new StringBuilder();
        sb.AppendLine($"Exports ({numNames} named, {numFuncs} total, ordinal base {ordBase}):");
        int shown = 0;
        for (int i = 0; i < (int)numNames && shown < 500; i++)
        {
            uint nameRva = BitConverter.ToUInt32(names, i * 4);
            ushort ord   = BitConverter.ToUInt16(ords, i * 2);
            uint funcRva = BitConverter.ToUInt32(funcs, ord * 4);

            var nameBytes = _api.Memory.ReadMemory(_api.TargetPid, baseAddr + nameRva, 128);
            var funcName  = nameBytes != null
                ? Encoding.ASCII.GetString(nameBytes, 0, Array.IndexOf(nameBytes, (byte)0) is int idx && idx >= 0 ? idx : nameBytes.Length)
                : "???";
            sb.AppendLine($"  [{ord + ordBase,5}]  0x{funcRva:X8}  → 0x{baseAddr + funcRva:X}  {funcName}");
            shown++;
        }
        return sb.ToString();
    }

    private string ExecXrefsTo(JsonElement args)
    {
        var target   = ParseAddress(args.GetProperty("address").GetString()!);
        var modules  = _api.Symbols.GetModules();
        var mainMod  = modules?.FirstOrDefault();
        if (mainMod == null) return "No modules loaded";

        var textBase = mainMod.BaseAddress + 0x1000;
        var hdr = _api.Memory.ReadMemory(_api.TargetPid, mainMod.BaseAddress, 0x400);
        uint textSize = 0;
        if (hdr != null && hdr.Length >= 0x200)
        {
            uint peOff = BitConverter.ToUInt32(hdr, 0x3C);
            ushort optSize = BitConverter.ToUInt16(hdr, (int)peOff + 0x14);
            int secOff = (int)peOff + 0x18 + optSize;
            if (secOff + 40 <= hdr.Length)
            {
                textBase = mainMod.BaseAddress + BitConverter.ToUInt32(hdr, secOff + 12);
                textSize = BitConverter.ToUInt32(hdr, secOff + 8);
            }
        }
        if (textSize == 0) textSize = 0x10000;

        var code = _api.Memory.ReadMemory(_api.TargetPid, textBase, textSize);
        if (code == null) return $"Failed to read .text at 0x{textBase:X}";

        var bitness    = _api.Is32Bit ? 32 : 64;
        var codeReader = new ByteArrayCodeReader(code);
        var decoder    = Iced.Intel.Decoder.Create(bitness, codeReader);
        decoder.IP     = textBase;

        var results = new List<(ulong addr, string type)>();
        while (decoder.IP < textBase + textSize && results.Count < 100)
        {
            var instr = decoder.Decode();
            if (instr.IsInvalid) break;

            ulong instrTarget = 0;
            string type = "";
            if (instr.FlowControl is FlowControl.Call or FlowControl.UnconditionalBranch or FlowControl.ConditionalBranch)
            {
                if (instr.Op0Kind == OpKind.NearBranch64 || instr.Op0Kind == OpKind.NearBranch32 || instr.Op0Kind == OpKind.NearBranch16)
                {
                    instrTarget = instr.NearBranchTarget;
                    type = instr.FlowControl == FlowControl.Call ? "CALL" : "JMP";
                }
            }
            else if (instr.Mnemonic == Mnemonic.Lea && instr.MemoryBase == Register.RIP)
            {
                instrTarget = instr.MemoryDisplacement64;
                type = "LEA";
            }

            if (instrTarget == target)
                results.Add((instr.IP, type));
        }

        if (results.Count == 0) return $"No references to 0x{target:X} found in .text";

        var sb = new StringBuilder();
        var sym = _api.Symbols.ResolveAddress(target);
        sb.AppendLine($"Cross-references to 0x{target:X}{(sym != null ? $" ({sym})" : "")} ({results.Count} found):");
        foreach (var (addr, type) in results)
        {
            var s = _api.Symbols.ResolveAddress(addr);
            sb.AppendLine($"  0x{addr:X16}  {type}  {(s != null ? $"({s})" : "")}");
        }
        return sb.ToString();
    }

    private string ExecNopInstruction(JsonElement args)
    {
        if (!_api.IsBreakState) return "Error: Process must be in break state";
        var addr = ParseAddress(args.GetProperty("address").GetString()!);

        var code = _api.Memory.ReadMemory(_api.TargetPid, addr, 15);
        if (code == null) return $"Failed to read at 0x{addr:X}";

        var bitness = _api.Is32Bit ? 32 : 64;
        var decoder = Iced.Intel.Decoder.Create(bitness, new ByteArrayCodeReader(code));
        decoder.IP  = addr;
        var instr   = decoder.Decode();
        if (instr.IsInvalid) return $"Invalid instruction at 0x{addr:X}";

        var nops = new byte[instr.Length];
        Array.Fill(nops, (byte)0x90);
        var ok = _api.Memory.WriteMemory(_api.TargetPid, addr, nops);

        var formatter = new NasmFormatter();
        var output    = new StringOutput();
        formatter.Format(instr, output);
        return ok
            ? $"NOPed {instr.Length} bytes at 0x{addr:X}: {output.ToStringAndReset()} → {instr.Length}x NOP"
            : $"Failed to write NOPs at 0x{addr:X}";
    }

    private string ExecPatchJump(JsonElement args)
    {
        if (!_api.IsBreakState) return "Error: Process must be in break state";
        var addr = ParseAddress(args.GetProperty("address").GetString()!);
        var mode = args.GetProperty("mode").GetString()!.ToLowerInvariant();
        if (mode is not ("always" or "never")) return "Error: mode must be 'always' or 'never'";

        var code = _api.Memory.ReadMemory(_api.TargetPid, addr, 15);
        if (code == null) return $"Failed to read at 0x{addr:X}";

        var bitness = _api.Is32Bit ? 32 : 64;
        var decoder = Iced.Intel.Decoder.Create(bitness, new ByteArrayCodeReader(code));
        decoder.IP  = addr;
        var instr   = decoder.Decode();

        if (instr.FlowControl != FlowControl.ConditionalBranch)
            return $"Instruction at 0x{addr:X} is not a conditional jump";

        byte[] patch;
        if (mode == "never")
        {
            patch = new byte[instr.Length];
            Array.Fill(patch, (byte)0x90);
        }
        else
        {
            if (instr.Length == 2)
                patch = new byte[] { 0xEB, code[1] }; // short JMP
            else
            {
                patch = new byte[instr.Length];
                Array.Fill(patch, (byte)0x90);
                patch[0] = 0xE9;
                Buffer.BlockCopy(code, instr.Length - 4, patch, 1, 4); // reuse rel32
            }
        }

        var ok = _api.Memory.WriteMemory(_api.TargetPid, addr, patch);
        return ok
            ? $"Patched at 0x{addr:X}: {(mode == "always" ? "forced JMP" : "NOPed")} ({instr.Length} bytes)"
            : $"Failed to patch at 0x{addr:X}";
    }

    private string ExecListStrings(JsonElement args)
    {
        ulong startAddr = 0;
        uint  size      = 0;
        int   minLen    = 4;

        if (args.TryGetProperty("min_length", out var ml)) minLen = ml.GetInt32();
        if (args.TryGetProperty("address", out var ap)) startAddr = ParseAddress(ap.GetString()!);
        if (args.TryGetProperty("size", out var sp)) size = (uint)sp.GetInt64();

        if (startAddr == 0 || size == 0)
        {
            var modules = _api.Symbols.GetModules();
            var main    = modules?.FirstOrDefault();
            if (main == null) return "No modules loaded";
            var hdr     = _api.Memory.ReadMemory(_api.TargetPid, main.BaseAddress, 0x400);
            if (hdr != null && hdr.Length >= 0x200)
            {
                uint peOff = BitConverter.ToUInt32(hdr, 0x3C);
                ushort optSz = BitConverter.ToUInt16(hdr, (int)peOff + 0x14);
                int secOff = (int)peOff + 0x18 + optSz;
                ushort numSec = BitConverter.ToUInt16(hdr, (int)peOff + 6);
                for (int i = 0; i < numSec && secOff + i * 40 + 40 <= hdr.Length; i++)
                {
                    int o = secOff + i * 40;
                    var name = Encoding.ASCII.GetString(hdr, o, 8).TrimEnd('\0');
                    if (name == ".rdata")
                    {
                        startAddr = main.BaseAddress + BitConverter.ToUInt32(hdr, o + 12);
                        size = BitConverter.ToUInt32(hdr, o + 8);
                        break;
                    }
                }
            }
            if (startAddr == 0) { startAddr = main.BaseAddress + 0x1000; size = 0x10000; }
        }

        size = Math.Min(size, 1024 * 1024);
        var data = _api.Memory.ReadMemory(_api.TargetPid, startAddr, size);
        if (data == null) return $"Failed to read memory at 0x{startAddr:X}";

        var sb = new StringBuilder();
        int found = 0;

        // ASCII
        int run = 0; int runStart = 0;
        for (int i = 0; i <= data.Length && found < 500; i++)
        {
            bool printable = i < data.Length && data[i] is >= 0x20 and < 0x7F;
            if (printable) { if (run == 0) runStart = i; run++; }
            else
            {
                if (run >= minLen && i < data.Length && data[i] == 0)
                { sb.AppendLine($"  0x{startAddr + (ulong)runStart:X}  A  \"{Encoding.ASCII.GetString(data, runStart, run)}\""); found++; }
                run = 0;
            }
        }

        // Unicode (UTF-16LE)
        for (int i = 0; i + 1 < data.Length && found < 500; i += 2)
        {
            int wRun = 0; int wStart = i;
            while (i + 1 < data.Length)
            {
                ushort ch = BitConverter.ToUInt16(data, i);
                if (ch >= 0x20 && ch < 0x7F) { wRun++; i += 2; }
                else break;
            }
            if (wRun >= minLen && i + 1 < data.Length && BitConverter.ToUInt16(data, i) == 0)
            { sb.AppendLine($"  0x{startAddr + (ulong)wStart:X}  W  \"{Encoding.Unicode.GetString(data, wStart, wRun * 2)}\""); found++; }
        }

        sb.Insert(0, $"Strings in 0x{startAddr:X}+0x{size:X} ({found} found, A=ASCII W=Wide):\n");
        return sb.ToString();
    }

    private string ExecCompareMemory(JsonElement args)
    {
        var addr1 = ParseAddress(args.GetProperty("addr1").GetString()!);
        var addr2 = ParseAddress(args.GetProperty("addr2").GetString()!);
        var size  = Math.Min((uint)args.GetProperty("size").GetInt64(), 4096u);

        var data1 = _api.Memory.ReadMemory(_api.TargetPid, addr1, size);
        var data2 = _api.Memory.ReadMemory(_api.TargetPid, addr2, size);
        if (data1 == null) return $"Failed to read at 0x{addr1:X}";
        if (data2 == null) return $"Failed to read at 0x{addr2:X}";

        int len = Math.Min(data1.Length, data2.Length);
        var diffs = new List<(int off, byte a, byte b)>();
        for (int i = 0; i < len && diffs.Count < 200; i++)
            if (data1[i] != data2[i]) diffs.Add((i, data1[i], data2[i]));

        if (diffs.Count == 0) return $"Regions are identical ({len} bytes)";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {diffs.Count} difference(s) in {len} bytes:");
        sb.AppendLine($"  {"Offset",-12} {"Addr1",-20} {"Addr2",-20} {"Val1",-6} {"Val2"}");
        foreach (var (off, a, b) in diffs)
            sb.AppendLine($"  +0x{off:X6}   0x{addr1 + (ulong)off:X16}  0x{addr2 + (ulong)off:X16}  0x{a:X2}   0x{b:X2}");
        return sb.ToString();
    }

    private string ExecReadUnicodeStruct(JsonElement args)
    {
        var addr = ParseAddress(args.GetProperty("address").GetString()!);
        var data = _api.Memory.ReadMemory(_api.TargetPid, addr, 16);
        if (data == null) return $"Failed to read at 0x{addr:X}";

        ushort len    = BitConverter.ToUInt16(data, 0);
        ushort maxLen = BitConverter.ToUInt16(data, 2);
        ulong  buf    = BitConverter.ToUInt64(data, _api.Is32Bit ? 4 : 8);

        if (buf == 0 || len == 0) return $"UNICODE_STRING at 0x{addr:X}: Length={len}, Buffer=NULL";

        var strData = _api.Memory.ReadMemory(_api.TargetPid, buf, len);
        if (strData == null) return $"UNICODE_STRING at 0x{addr:X}: Length={len}, Buffer=0x{buf:X} (unreadable)";

        var str = Encoding.Unicode.GetString(strData);
        return $"UNICODE_STRING at 0x{addr:X}: \"{str}\" (Length={len}, MaxLength={maxLen}, Buffer=0x{buf:X})";
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

    // ── Notes / Bookmarks ────────────────────────────────────────────────────

    private string ExecWriteNote(JsonElement a)
    {
        var addr = ParseAddress(a.GetProperty("address").GetString()!);
        var note = a.GetProperty("note").GetString()!;
        _api.UI.SetAddressAnnotation(addr, note);
        _api.UI.RefreshDisassembly();
        return $"Note set at 0x{addr:X}: {note}";
    }

    private string ExecReadNote(JsonElement a)
    {
        var addr = ParseAddress(a.GetProperty("address").GetString()!);
        var note = _api.UI.GetAddressAnnotation(addr);
        return note != null ? $"0x{addr:X}: {note}" : $"No note at 0x{addr:X}";
    }

    private string ExecReadAllNotes()
    {
        var all = _api.UI.GetAllAnnotations();
        if (all.Count == 0) return "No notes/bookmarks";
        var sb = new StringBuilder();
        sb.AppendLine($"{all.Count} note(s):");
        foreach (var (addr, note) in all.OrderBy(kv => kv.Key))
        {
            var sym = _api.Symbols.ResolveAddress(addr);
            sb.AppendLine($"  0x{addr:X16}  {(sym != null ? $"({sym})  " : "")}{note}");
        }
        return sb.ToString();
    }

    private string ExecRemoveNote(JsonElement a)
    {
        var addr = ParseAddress(a.GetProperty("address").GetString()!);
        _api.UI.SetAddressAnnotation(addr, null);
        _api.UI.RefreshDisassembly();
        return $"Note removed at 0x{addr:X}";
    }

    private async Task<string> ExecScript(JsonElement a)
    {
        var code = a.GetProperty("code").GetString()!;
        var executor = _api.UI.GetPluginData("ScriptExecute") as Func<string, Task<string>>;
        if (executor == null)
            return "Error: Scripting plugin not loaded or disabled.";
        return await executor(code);
    }

    private string ExecScriptingReference() => ScriptRef;

    private const string ScriptRef = """
# KernelFlirt Scripting Reference

C# REPL with full debugger API access. Variables persist between executions.

## Shortcuts
| Shortcut | Description |
|----------|-------------|
| `api` | Full IDebuggerApi |
| `print("text")` | Print to output |
| `ReadMem(addr, size)` | Read bytes |
| `WriteMem(addr, data)` | Write bytes |
| `ReadString(addr)` | ASCII string |
| `ReadWString(addr)` | Unicode string |
| `ReadPtr(addr)` | Read pointer |
| `ReadU32(addr)` / `ReadU64(addr)` | Read uint |
| `Reg("RAX")` | Register value |
| `RIP` / `RSP` | Instruction/stack pointer |
| `Sym(addr)` | Symbol name |
| `Addr("module!func")` | Address by name |

## API: `api.Memory.*`
ReadMemory, WriteMemory, ReadRegisters, WriteRip, AllocateMemory, FreeMemory, ProtectMemory

## API: `api.Breakpoints.*`
SetBreakpoint, RemoveBreakpoint, GetAll, ToggleBreakpoint

## API: `api.Symbols.*`
`ResolveAddress(addr)` → string?, `ResolveNameToAddress(name)` → ulong, `GetModules()`, `GetKernelModules()`
`RegisterFunction(addr, name, size)` — name a function. Args: ulong address, string name, uint size (bytes, MUST specify to avoid overlap with next function)

## API: `api.UI.*`
NavigateDisassembly, SetAddressAnnotation, RefreshDisassembly, DecompileFunction, GetDecompiledCode

## API: Execution
api.Continue(), SingleStep(), StepOver(), StepOut(), RunToCursor(), SkipInstruction(), Pause()

## API: Events
api.OnDebugEventFilter += evt => { return false; }; // return true to suppress break

## Examples
```csharp
// Registers
var regs = api.Memory.ReadRegisters(api.TargetPid, api.SelectedThreadId);
foreach (var r in regs.Where(r => !r.IsFlag)) print($"{r.Name,-4} = 0x{r.Value:X016}");
```
```csharp
// Logging breakpoint
var target = Addr("ws2_32!send");
api.OnDebugEventFilter += evt => {
    if (evt.Address != target) return false;
    print($"send({(int)Reg("R8")}): {Encoding.ASCII.GetString(ReadMem(ReadPtr(Reg("RDX")), (uint)Math.Min((int)Reg("R8"), 128)))}");
    return false;
};
api.Breakpoints.SetBreakpoint(api.TargetPid, 0, target, PluginBreakpointType.Software);
```

## IMPORTANT: Naming unnamed functions

After decompiling, unnamed functions appear as `module.exe+0x1470` (module+offset) in disassembly and decompiled code.
You SHOULD name them based on what they do using `RegisterFunction`.
This makes all subsequent decompilation and disassembly human-readable.

**Always do this after analyzing a function's purpose via decompile.**

```csharp
// Name functions with SIZE — critical to avoid overlapping names
var b = api.Symbols.GetModules()[0].BaseAddress;
api.Symbols.RegisterFunction(b + 0x1000, "RC4_Init", 0x120);
api.Symbols.RegisterFunction(b + 0x1120, "RC4_Crypt", 0x190);
api.Symbols.RegisterFunction(b + 0x1320, "PrintString", 0x50);
api.Symbols.RegisterFunction(b + 0x1470, "DecryptRC4String", 0x100);
api.UI.RefreshDisassembly();
```

**CRITICAL: Always specify the size parameter!** Without size, RegisterFunction uses a default
large range and neighboring functions show as `FuncName+0xOffset` instead of their own name.
Calculate size as: next function address - this function address.

After naming, the function name appears in the disassembly view and Graph View (CFG).
The decompiler (RetDec) may not pick up the new name, but disassembly and graph will.

Workflow: decompile → understand what function does → name it with size via RegisterFunction → RefreshDisassembly.
""";
}
