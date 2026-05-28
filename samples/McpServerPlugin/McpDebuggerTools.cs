using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Iced.Intel;
using KernelFlirt.SDK;

namespace McpServerPlugin;

/// <summary>
/// MCP tool definitions and their implementations.
/// Tool schemas use JSON Schema (inputSchema) as required by the MCP spec.
/// </summary>
public class McpDebuggerTools
{
    private readonly IDebuggerApi _api;

    public McpDebuggerTools(IDebuggerApi api) => _api = api;

    // ── UI-thread helpers ─────────────────────────────────────────────────────
    // The debugger API properties (IsBreakState, etc.) and execution commands
    // (Continue, Step*) are only valid on the WPF UI thread.  MCP calls arrive
    // on HttpListener threads, so we must marshal through the Dispatcher.

    private void OnUi(Action action)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d != null && !d.CheckAccess())
            d.Invoke(action);
        else
            action();
    }

    private T OnUi<T>(Func<T> func)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d != null && !d.CheckAccess())
            return d.Invoke(func);
        return func();
    }

    // ── Tool definitions ─────────────────────────────────────────────────────

    public object[] GetToolDefinitions() =>
    [
        // ── State ──────────────────────────────────────────────────────────
        Tool("get_debugger_state",
             "Return current debugger state: connected, break state, target PID, selected TID, bitness",
             Obj()),

        // ── Breakpoints ────────────────────────────────────────────────────
        Tool("set_breakpoint",
             "Set a software (INT3) breakpoint at the given address",
             Obj(Prop("address", "string", "Hex address, e.g. 0x7ff64f961190")),
             required: ["address"]),

        Tool("set_hardware_breakpoint",
             "Set a hardware execute breakpoint (DR0-DR3). Works on execute, survives code patching.",
             Obj(Prop("address", "string", "Hex address")),
             required: ["address"]),

        Tool("set_hw_write_watchpoint",
             "Set a hardware write watchpoint. Breaks when the address is written to.",
             Obj(Prop("address", "string", "Hex address to watch"),
                 Prop("length",  "integer", "Watch size in bytes: 1, 2, 4 or 8 (default 1)")),
             required: ["address"]),

        Tool("set_hw_access_watchpoint",
             "Set a hardware read/write watchpoint. Breaks on any access (read or write) to the address.",
             Obj(Prop("address", "string",  "Hex address to watch"),
                 Prop("length",  "integer", "Watch size in bytes: 1, 2, 4 or 8 (default 1)")),
             required: ["address"]),

        Tool("set_memory_breakpoint",
             "Set a memory (page guard) breakpoint. Breaks on any access to the memory page containing the address.",
             Obj(Prop("address", "string", "Hex address")),
             required: ["address"]),

        Tool("remove_breakpoint",
             "Remove a breakpoint by its handle",
             Obj(Prop("handle", "integer", "Breakpoint handle returned by set_* tools")),
             required: ["handle"]),

        Tool("list_breakpoints",
             "List all active breakpoints with types, addresses, symbols and hit counts",
             Obj()),

        // ── Memory ────────────────────────────────────────────────────────
        Tool("read_memory",
             "Read raw memory at address and return a hex dump (max 512 bytes)",
             Obj(Prop("address", "string",  "Hex address"),
                 Prop("size",    "integer", "Bytes to read (max 512)")),
             required: ["address", "size"]),

        Tool("read_pointer",
             "Read a single pointer (8 bytes x64 / 4 bytes x32) at address and resolve its symbol",
             Obj(Prop("address", "string", "Hex address of the pointer")),
             required: ["address"]),

        Tool("read_string",
             "Read a null-terminated ASCII string from memory (up to 256 chars)",
             Obj(Prop("address", "string", "Hex address of the string")),
             required: ["address"]),

        Tool("read_unicode_string",
             "Read a null-terminated UTF-16LE wide string from memory (up to 256 chars)",
             Obj(Prop("address", "string", "Hex address of the WCHAR* string")),
             required: ["address"]),

        Tool("write_memory",
             "Write bytes to memory at address",
             Obj(Prop("address",   "string", "Hex address"),
                 Prop("hex_bytes", "string", "Hex bytes, e.g. '90 90 CC'")),
             required: ["address", "hex_bytes"]),

        Tool("search_memory",
             "Search for a byte pattern in a memory range. Use ?? as wildcard for unknown bytes.",
             Obj(Prop("start",   "string",  "Hex start address"),
                 Prop("size",    "integer", "Range size in bytes to search (max 16 MB)"),
                 Prop("pattern", "string",  "Hex pattern, e.g. '48 8B ?? ?? E8 ?? ?? ?? ??' or '4D 5A'")),
             required: ["start", "size", "pattern"]),

        Tool("read_registers",
             "Read all CPU registers of the selected thread",
             Obj()),

        Tool("write_rip",
             "Redirect execution by changing RIP (instruction pointer) of the selected thread",
             Obj(Prop("address", "string", "New RIP value (hex)")),
             required: ["address"]),

        Tool("allocate_memory",
             "Allocate virtual memory in the target process (VirtualAllocEx). Returns the allocated address.",
             Obj(Prop("size", "integer", "Number of bytes to allocate")),
             required: ["size"]),

        Tool("free_memory",
             "Free previously allocated virtual memory in the target process (VirtualFreeEx)",
             Obj(Prop("address", "string", "Hex address returned by allocate_memory")),
             required: ["address"]),

        Tool("protect_memory",
             "Change memory page protection (VirtualProtectEx). Returns the old protection value.\n" +
             "Common values: 0x02=PAGE_READONLY, 0x04=PAGE_READWRITE, 0x20=PAGE_EXECUTE_READ, 0x40=PAGE_EXECUTE_READWRITE",
             Obj(Prop("address",    "string",  "Hex address of the region"),
                 Prop("size",       "integer", "Region size in bytes"),
                 Prop("protection", "integer", "New protection constant (e.g. 0x40 for PAGE_EXECUTE_READWRITE)")),
             required: ["address", "size", "protection"]),

        // ── Disassembly / Decompilation ───────────────────────────────────
        Tool("disassemble",
             "Disassemble instructions at address (NASM syntax with symbols)",
             Obj(Prop("address", "string",  "Hex address to start disassembly"),
                 Prop("count",   "integer", "Number of instructions (default 20, max 50)")),
             required: ["address"]),

        Tool("navigate_disasm",
             "Navigate the disassembly view to a specific address",
             Obj(Prop("address", "string", "Hex address")),
             required: ["address"]),

        Tool("disasm_go_back",
             "Undo the last navigate_disasm / decompile navigation",
             Obj()),

        Tool("decompile",
             "Decompile function at address to C pseudocode (RetDec). Preferred over raw disassembly.",
             Obj(Prop("address", "string", "Hex address of the function")),
             required: ["address"]),

        // ── Symbols / Modules ─────────────────────────────────────────────
        Tool("resolve_symbol",
             "Resolve a symbol name → address, or an address → symbol name",
             Obj(Prop("name", "string", "Symbol like 'kernel32!CreateFileW' or hex address like '0x7ffXXX'")),
             required: ["name"]),

        Tool("list_modules",
             "List all user-mode modules loaded in the target process",
             Obj()),

        Tool("list_kernel_modules",
             "List all kernel-mode drivers currently loaded in the system",
             Obj()),

        // ── Process / Threads ─────────────────────────────────────────────
        Tool("list_processes",
             "List all running processes on the system",
             Obj()),

        Tool("list_threads",
             "List all threads in the target process",
             Obj()),

        Tool("suspend_thread",
             "Suspend a thread by TID (increment suspend count)",
             Obj(Prop("tid", "integer", "Thread ID to suspend")),
             required: ["tid"]),

        Tool("resume_thread",
             "Resume a suspended thread by TID (decrement suspend count)",
             Obj(Prop("tid", "integer", "Thread ID to resume")),
             required: ["tid"]),

        Tool("switch_thread",
             "Switch debugger focus to the given TID — updates registers, disassembly, stack and call-stack views to reflect that thread. Does not change its suspend count.",
             Obj(Prop("tid", "integer", "Thread ID to make active")),
             required: ["tid"]),

        Tool("get_peb_address",
             "Get the PEB (Process Environment Block) address of the target process",
             Obj()),

        // ── Execution control ─────────────────────────────────────────────
        Tool("continue_execution",
             "Resume process execution (F9). Call wait_for_break before reading state afterwards.",
             Obj()),

        Tool("single_step",
             "Step Into (F7) — execute one instruction, following into CALL instructions",
             Obj()),

        Tool("step_over",
             "Step Over (F8) — execute one instruction, skipping over CALL instructions",
             Obj()),

        Tool("step_out",
             "Step Out (Ctrl+F9) — run until the current function returns",
             Obj()),

        Tool("run_to_address",
             "Run to Address (F4). Call wait_for_break after.",
             Obj(Prop("address", "string", "Hex address to run to")),
             required: ["address"]),

        Tool("skip_instruction",
             "Skip Instruction (Ctrl+F8) — advance RIP past current instruction without executing it",
             Obj()),

        Tool("pause_execution",
             "Pause (F12) — suspend a running process and enter break state",
             Obj()),

        Tool("wait_for_break",
             "Wait for the process to hit a breakpoint or complete a step. " +
             "MUST be called after continue_execution or run_to_address before reading state.",
             Obj(Prop("timeout_ms", "integer", "Timeout in milliseconds (default 10000)"))),

        // ── Anti-debug bypass ─────────────────────────────────────────────
        Tool("clear_debug_port",
             "Clear the DebugPort field in EPROCESS — hides the process from IsDebuggerPresent / NtQueryInformationProcess",
             Obj()),

        Tool("clear_thread_hide",
             "Set HideFromDebugger flag on all threads — hides threads from debugger detection",
             Obj()),

        Tool("install_ntqsi_hook",
             "Install a kernel hook on NtQuerySystemInformation to hide the debugger process from process lists",
             Obj()),

        Tool("remove_ntqsi_hook",
             "Remove the NtQuerySystemInformation kernel hook",
             Obj()),

        Tool("probe_ntqsi_hook",
             "Check if the NtQuerySystemInformation hook is active and return its status string",
             Obj()),

        Tool("spoof_shared_user_data",
             "Enable or disable SharedUserData spoofing (hides debugger timing from KUSER_SHARED_DATA reads)",
             Obj(Prop("enable", "boolean", "true to enable spoofing, false to disable")),
             required: ["enable"]),

        // ── UI helpers ────────────────────────────────────────────────────
        Tool("add_unpacked_module",
             "Register a dynamically unpacked PE as a virtual module (triggers section/import/string refresh in UI)",
             Obj(Prop("pe_base", "string", "Hex base address where the PE is mapped in memory"),
                 Prop("name",    "string", "Display name for the module, e.g. 'unpacked.exe'")),
             required: ["pe_base", "name"]),

        Tool("refresh_modules",
             "Force a refresh of the module list and sections tab in the UI",
             Obj()),

        // ── Composite / high-level commands ─────────────────────────────
        Tool("write_rip_and_rsp",
             "Redirect execution by changing both RIP and RSP (useful for IAT unpack / hijack). " +
             "Sets instruction pointer and stack pointer atomically.",
             Obj(Prop("rip", "string", "New RIP value (hex)"),
                 Prop("rsp", "string", "New RSP value (hex)")),
             required: ["rip", "rsp"]),

        Tool("add_module_sections",
             "Manually provide section table for a module when PE header is destroyed by a packer. " +
             "Each section: { name, va (hex RVA), vsize (int), characteristics (int) }",
             Obj(Prop("module_name", "string", "Module name (must already be in module list)"),
                 Prop("sections",    "string", "JSON array: [{\"name\":\".text\",\"va\":\"0x1000\",\"vsize\":4096,\"chr\":0x60000020}, ...]")),
             required: ["module_name", "sections"]),

        Tool("dump_stack",
             "Read the current stack (from RSP) and display each QWORD with symbol resolution. " +
             "Shows call stack / pushed arguments / return addresses.",
             Obj(Prop("count", "integer", "Number of QWORD entries to read (default 16, max 64)"))),

        Tool("dump_peb",
             "Parse and display key PEB fields: ImageBase, Ldr, BeingDebugged, NtGlobalFlag, " +
             "ProcessParameters (ImagePathName, CommandLine), ProcessHeap, NumberOfProcessors, OSVersion",
             Obj()),

        Tool("dump_teb",
             "Parse and display key TEB fields: StackBase, StackLimit, PEB pointer, " +
             "LastErrorValue, TLS pointer, CurrentThreadId",
             Obj()),

        Tool("dump_pe_header",
             "Parse DOS/PE headers and section table at a base address. " +
             "Shows EntryPoint, ImageSize, sections with names/RVA/sizes/characteristics, " +
             "and data directory entries (imports, exports, relocs, etc.)",
             Obj(Prop("address", "string", "Hex base address of PE (e.g. module base)")),
             required: ["address"]),

        Tool("dump_imports",
             "Parse the Import Address Table (IAT) of a PE at the given base address. " +
             "Shows each imported DLL and its functions with current IAT values.",
             Obj(Prop("address", "string", "Hex base address of the PE")),
             required: ["address"]),

        // ── Scripting ──────────────────────────────────────────────────────
        Tool("execute_script",
             "Execute a C# script in the Scripting plugin REPL. Variables persist between calls. " +
             "Use the 'scripting_reference' tool first to learn the API. " +
             "IMPORTANT: After decompiling, unnamed functions appear as 'module.exe+0xOFFSET'. Use this tool to name them: " +
             "var b = api.Symbols.GetModules()[0].BaseAddress; api.Symbols.RegisterFunction(b + 0xOFFSET, \"MeaningfulName\"); api.UI.RefreshDisassembly(); " +
             "Names will appear in disassembly and Graph View. " +
             "Available shortcuts: api, print(), ReadMem(), WriteMem(), ReadString(), ReadPtr(), Reg(), RIP, RSP, Sym(), Addr(). " +
             "Full API via api.Memory.*, api.Breakpoints.*, api.Symbols.*, api.Process.*, api.UI.*, api.Log.*",
             Obj(Prop("code", "string", "C# code to execute")),
             required: ["code"]),

        Tool("scripting_reference",
             "Get the complete C# scripting API reference — all available shortcuts, full API methods, event handlers, and code examples. " +
             "Call this BEFORE writing any script to understand what's available.",
             Obj()),

        Tool("dump_exports",
             "Parse the Export Directory of a PE/DLL at the given base address. " +
             "Shows all exported function names, ordinals, and RVAs.",
             Obj(Prop("address", "string", "Hex base address of the PE")),
             required: ["address"]),

        Tool("xrefs_to",
             "Scan the .text section of the main module for references (CALL/JMP/LEA rel32) to a target address. " +
             "Returns all cross-references found (max 100).",
             Obj(Prop("address", "string", "Hex target address to find references to")),
             required: ["address"]),

        Tool("nop_instruction",
             "NOP-out the instruction at the given address. Reads the instruction length via disassembly " +
             "and replaces it with the correct number of 0x90 bytes.",
             Obj(Prop("address", "string", "Hex address of the instruction to NOP")),
             required: ["address"]),

        Tool("patch_jump",
             "Force a conditional jump (JCC) to always-jump or never-jump. " +
             "'always' replaces JCC with JMP, 'never' replaces with NOPs.",
             Obj(Prop("address", "string", "Hex address of the conditional jump instruction"),
                 Prop("mode",    "string", "'always' = force jump (JMP), 'never' = NOP the jump")),
             required: ["address", "mode"]),

        Tool("list_strings",
             "Scan a memory range for printable ASCII and Unicode strings (like the 'strings' utility). " +
             "Default: scans the .rdata section of the main module.",
             Obj(Prop("address",    "string",  "Hex start address (default: main module .rdata)"),
                 Prop("size",       "integer", "Range size in bytes (default: .rdata size, max 1 MB)"),
                 Prop("min_length", "integer", "Minimum string length (default 4)"))),

        Tool("compare_memory",
             "Compare two memory regions byte-by-byte and show differences. " +
             "Useful for detecting patches (file-on-disk vs in-memory).",
             Obj(Prop("addr1", "string",  "Hex address of first region"),
                 Prop("addr2", "string",  "Hex address of second region"),
                 Prop("size",  "integer", "Number of bytes to compare (max 4096)")),
             required: ["addr1", "addr2", "size"]),

        Tool("read_unicode_struct",
             "Read a UNICODE_STRING structure (Length + MaxLength + Buffer pointer) and return the string contents.",
             Obj(Prop("address", "string", "Hex address of the UNICODE_STRING struct")),
             required: ["address"]),

        // ── Notes / Bookmarks ─────────────────────────────────────────────
        Tool("write_note",
             "Add or update a note/bookmark at an address. The note is shown as a comment in the disassembly " +
             "and persisted between sessions (via Bookmarks/Notes plugin). Use this to annotate important " +
             "addresses, functions, suspicious code, or analysis findings.",
             Obj(Prop("address", "string", "Hex address to annotate"),
                 Prop("note",    "string", "Note text (e.g. 'decryption loop', 'OEP candidate', 'anti-debug check')")),
             required: ["address", "note"]),

        Tool("read_note",
             "Read the note/bookmark at a specific address. Returns the note text or empty if none.",
             Obj(Prop("address", "string", "Hex address")),
             required: ["address"]),

        Tool("read_all_notes",
             "Read all notes/bookmarks. Returns a list of all annotated addresses with their notes. " +
             "Useful to get context about previous analysis sessions.",
             Obj()),

        Tool("remove_note",
             "Remove a note/bookmark at an address.",
             Obj(Prop("address", "string", "Hex address")),
             required: ["address"]),
    ];

    // ── Dispatch ─────────────────────────────────────────────────────────────

    public string Execute(string toolName, string argsJson)
    {
        try
        {
            if (!_api.IsConnected && toolName != "get_debugger_state")
                return "Error: not connected to target";

            using var doc  = JsonDocument.Parse(argsJson);
            var       root = doc.RootElement;

            return toolName switch
            {
                // State
                "get_debugger_state"        => ExecGetDebuggerState(),

                // Breakpoints
                "set_breakpoint"            => ExecSetBreakpoint(root, PluginBreakpointType.Software),
                "set_hardware_breakpoint"   => ExecSetBreakpoint(root, PluginBreakpointType.Hardware),
                "set_hw_write_watchpoint"   => ExecSetWatchpoint(root, PluginBreakpointType.HwWrite),
                "set_hw_access_watchpoint"  => ExecSetWatchpoint(root, PluginBreakpointType.HwReadWrite),
                "set_memory_breakpoint"     => ExecSetBreakpoint(root, PluginBreakpointType.Memory),
                "remove_breakpoint"         => ExecRemoveBreakpoint(root),
                "list_breakpoints"          => ExecListBreakpoints(),

                // Memory
                "read_memory"               => ExecReadMemory(root),
                "read_pointer"              => ExecReadPointer(root),
                "read_string"               => ExecReadString(root, unicode: false),
                "read_unicode_string"       => ExecReadString(root, unicode: true),
                "write_memory"              => ExecWriteMemory(root),
                "search_memory"             => ExecSearchMemory(root),
                "read_registers"            => ExecReadRegisters(),
                "write_rip"                 => ExecWriteRip(root),
                "allocate_memory"           => ExecAllocateMemory(root),
                "free_memory"               => ExecFreeMemory(root),
                "protect_memory"            => ExecProtectMemory(root),

                // Disassembly / decompilation
                "disassemble"               => ExecDisassemble(root),
                "navigate_disasm"           => ExecNavigateDisasm(root),
                "disasm_go_back"            => ExecDisasmGoBack(),
                "decompile"                 => ExecDecompile(root),

                // Symbols / modules
                "resolve_symbol"            => ExecResolveSymbol(root),
                "list_modules"              => ExecListModules(),
                "list_kernel_modules"       => ExecListKernelModules(),

                // Process / threads
                "list_processes"            => ExecListProcesses(),
                "list_threads"              => ExecListThreads(),
                "suspend_thread"            => ExecSuspendThread(root),
                "resume_thread"             => ExecResumeThread(root),
                "switch_thread"             => ExecSwitchThread(root),
                "get_peb_address"           => ExecGetPebAddress(),

                // Execution control
                "continue_execution"        => ExecContinue(),
                "single_step"               => ExecSingleStep(),
                "step_over"                 => ExecStepOver(),
                "step_out"                  => ExecStepOut(),
                "run_to_address"            => ExecRunToAddress(root),
                "skip_instruction"          => ExecSkipInstruction(),
                "pause_execution"           => ExecPause(),
                "wait_for_break"            => ExecWaitForBreak(root),

                // Anti-debug bypass
                "clear_debug_port"          => ExecClearDebugPort(),
                "clear_thread_hide"         => ExecClearThreadHide(),
                "install_ntqsi_hook"        => ExecInstallNtQsiHook(),
                "remove_ntqsi_hook"         => ExecRemoveNtQsiHook(),
                "probe_ntqsi_hook"          => ExecProbeNtQsiHook(),
                "spoof_shared_user_data"    => ExecSpoofSharedUserData(root),

                // UI
                "add_unpacked_module"       => ExecAddUnpackedModule(root),
                "refresh_modules"           => ExecRefreshModules(),

                // Composite / high-level
                "write_rip_and_rsp"         => ExecWriteRipAndRsp(root),
                "add_module_sections"       => ExecAddModuleSections(root),
                "dump_stack"                => ExecDumpStack(root),
                "dump_peb"                  => ExecDumpPeb(),
                "dump_teb"                  => ExecDumpTeb(),
                "dump_pe_header"            => ExecDumpPeHeader(root),
                "dump_imports"              => ExecDumpImports(root),
                "dump_exports"              => ExecDumpExports(root),
                "xrefs_to"                  => ExecXrefsTo(root),
                "nop_instruction"           => ExecNopInstruction(root),
                "patch_jump"                => ExecPatchJump(root),
                "list_strings"              => ExecListStrings(root),
                "compare_memory"            => ExecCompareMemory(root),
                "read_unicode_struct"       => ExecReadUnicodeStruct(root),

                // Notes / Bookmarks
                "write_note"                => ExecWriteNote(root),
                "read_note"                 => ExecReadNote(root),
                "read_all_notes"            => ExecReadAllNotes(),
                "remove_note"               => ExecRemoveNote(root),

                // Scripting
                "execute_script"            => ExecScript(root).GetAwaiter().GetResult(),
                "scripting_reference"       => ExecScriptingReference(),

                _ => $"Unknown tool: {toolName}"
            };
        }
        catch (Exception ex)
        {
            return $"Error in {toolName}: {ex.Message}";
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

    private string ExecSetBreakpoint(JsonElement a, PluginBreakpointType type)
    {
        if (!_api.IsBreakState) return "Error: process must be in break state";
        var addr   = ParseHex(a.GetProperty("address").GetString()!);
        var handle = _api.Breakpoints.SetBreakpoint(_api.TargetPid, _api.SelectedThreadId, addr, type);
        if (handle is null) return $"Failed to set {type} breakpoint at 0x{addr:X}";
        var sym = _api.Symbols.ResolveAddress(addr);
        return $"{type} breakpoint set at 0x{addr:X}{Sym(sym)}, handle={handle.Value}";
    }

    private string ExecSetWatchpoint(JsonElement a, PluginBreakpointType type)
    {
        if (!_api.IsBreakState) return "Error: process must be in break state";
        var addr   = ParseHex(a.GetProperty("address").GetString()!);
        uint len   = a.TryGetProperty("length", out var le) ? le.GetUInt32() : 1;
        if (len is not (1 or 2 or 4 or 8)) return "Error: length must be 1, 2, 4 or 8";
        var handle = _api.Breakpoints.SetBreakpoint(_api.TargetPid, _api.SelectedThreadId, addr, type, len);
        if (handle is null) return $"Failed to set {type} watchpoint at 0x{addr:X}";
        var sym = _api.Symbols.ResolveAddress(addr);
        return $"{type} watchpoint ({len} bytes) at 0x{addr:X}{Sym(sym)}, handle={handle.Value}";
    }

    private string ExecRemoveBreakpoint(JsonElement a)
    {
        var handle = a.GetProperty("handle").GetUInt32();
        return _api.Breakpoints.RemoveBreakpoint(handle)
            ? $"Breakpoint #{handle} removed"
            : $"Failed to remove breakpoint #{handle}";
    }

    private string ExecListBreakpoints()
    {
        var bps = _api.Breakpoints.GetAll();
        if (bps is null || bps.Count == 0) return "No breakpoints set";
        var sb = new StringBuilder();
        foreach (var bp in bps)
        {
            var sym = _api.Symbols.ResolveAddress(bp.Address);
            sb.AppendLine($"#{bp.Handle,3}  {bp.Type,-14}  0x{bp.Address:X16}{Sym(sym),-40}  Hits={bp.HitCount,4}  {(bp.Enabled ? "ON" : "OFF")}");
        }
        return sb.ToString();
    }

    // ── Memory ───────────────────────────────────────────────────────────────

    private string ExecReadMemory(JsonElement a)
    {
        var addr = ParseHex(a.GetProperty("address").GetString()!);
        var size = Math.Min(a.GetProperty("size").GetUInt32(), 512u);
        var data = _api.Memory.ReadMemory(_api.TargetPid, addr, size);
        if (data is null) return $"Failed to read memory at 0x{addr:X}";
        return FormatHexDump(data, addr);
    }

    private string ExecReadPointer(JsonElement a)
    {
        var addr    = ParseHex(a.GetProperty("address").GetString()!);
        var ptrSize = _api.Is32Bit ? 4u : 8u;
        var data    = _api.Memory.ReadMemory(_api.TargetPid, addr, ptrSize);
        if (data is null) return $"Failed to read memory at 0x{addr:X}";
        ulong ptr = ptrSize == 8
            ? BitConverter.ToUInt64(data, 0)
            : BitConverter.ToUInt32(data, 0);
        var sym = _api.Symbols.ResolveAddress(ptr);
        return $"[0x{addr:X}] → 0x{ptr:X}{Sym(sym)}";
    }

    private string ExecReadString(JsonElement a, bool unicode)
    {
        var addr    = ParseHex(a.GetProperty("address").GetString()!);
        var maxLen  = 256u;
        var readLen = unicode ? maxLen * 2 : maxLen;
        var data    = _api.Memory.ReadMemory(_api.TargetPid, addr, readLen);
        if (data is null) return $"Failed to read memory at 0x{addr:X}";

        string result;
        if (unicode)
        {
            // Find UTF-16 null terminator (two consecutive zero bytes at even offset)
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

    private string ExecWriteMemory(JsonElement a)
    {
        if (!_api.IsBreakState) return "Error: process must be in break state";
        var addr  = ParseHex(a.GetProperty("address").GetString()!);
        var bytes = ParseHexBytes(a.GetProperty("hex_bytes").GetString()!);
        if (bytes is null) return "Error: invalid hex bytes string";
        return _api.Memory.WriteMemory(_api.TargetPid, addr, bytes)
            ? $"Wrote {bytes.Length} bytes to 0x{addr:X}"
            : $"Failed to write memory at 0x{addr:X}";
    }

    private string ExecSearchMemory(JsonElement a)
    {
        var start   = ParseHex(a.GetProperty("start").GetString()!);
        var size    = Math.Min((uint)a.GetProperty("size").GetInt64(), 16u * 1024 * 1024); // cap 16 MB
        var patStr  = a.GetProperty("pattern").GetString()!.Trim();

        // Parse pattern: "48 8B ?? E8 ??" → byte?[]
        var tokens  = patStr.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var pattern = new byte?[tokens.Length];
        for (int i = 0; i < tokens.Length; i++)
            pattern[i] = tokens[i] == "??" ? null : byte.Parse(tokens[i], System.Globalization.NumberStyles.HexNumber);

        if (pattern.Length == 0) return "Error: empty pattern";

        // Read in 64 KB chunks to avoid one giant allocation
        const uint chunkSize = 64 * 1024;
        var results = new List<ulong>();
        ulong scanned = 0;

        // Overlap by (pattern.Length - 1) bytes to catch matches that straddle chunks
        int overlap = pattern.Length - 1;
        byte[]? prev = null;

        while (scanned < size && results.Count < 100)
        {
            var read = (uint)Math.Min(chunkSize, size - scanned);
            var chunk = _api.Memory.ReadMemory(_api.TargetPid, start + scanned, read);
            if (chunk is null) break;

            // Prepend overlap from previous chunk
            byte[] buf;
            ulong  bufBase;
            if (prev != null && overlap > 0)
            {
                buf = new byte[Math.Min(overlap, prev.Length) + chunk.Length];
                int off = Math.Max(0, prev.Length - overlap);
                Buffer.BlockCopy(prev, off, buf, 0, prev.Length - off);
                Buffer.BlockCopy(chunk, 0, buf, prev.Length - off, chunk.Length);
                bufBase = start + scanned - (ulong)(prev.Length - off);
            }
            else
            {
                buf     = chunk;
                bufBase = start + scanned;
            }

            // Scan buf for pattern
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
                    // Only include hits within [start, start+size)
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
            sb.AppendLine($"  0x{hit:X}{Sym(sym)}");
        }
        return sb.ToString();
    }

    private string ExecReadRegisters()
    {
        if (!_api.IsBreakState) return "Error: process must be in break state";
        var regs = _api.Memory.ReadRegisters(_api.TargetPid, _api.SelectedThreadId);
        if (regs is null || regs.Count == 0) return "Failed to read registers";
        var sb = new StringBuilder();
        foreach (var r in regs.Where(r => !r.IsFlag))
            sb.AppendLine($"{r.Name,-6} = 0x{r.Value:X16}");
        var flags = string.Join(" ", regs.Where(r => r.IsFlag && r.Value != 0).Select(r => r.Name));
        if (!string.IsNullOrEmpty(flags)) sb.AppendLine($"FLAGS: {flags}");
        return sb.ToString();
    }

    private string ExecWriteRip(JsonElement a)
    {
        if (!_api.IsBreakState) return "Error: process must be in break state";
        var addr = ParseHex(a.GetProperty("address").GetString()!);
        var ok   = _api.Memory.WriteRip(_api.TargetPid, _api.SelectedThreadId, addr);
        var sym  = _api.Symbols.ResolveAddress(addr);
        return ok ? $"RIP set to 0x{addr:X}{Sym(sym)}" : $"Failed to set RIP to 0x{addr:X}";
    }

    private string ExecAllocateMemory(JsonElement a)
    {
        if (!_api.IsBreakState) return "Error: process must be in break state";
        var size = (ulong)a.GetProperty("size").GetInt64();
        var addr = _api.Memory.AllocateMemory(_api.TargetPid, size);
        return addr != 0
            ? $"Allocated {size} bytes at 0x{addr:X} (PAGE_EXECUTE_READWRITE)"
            : "Failed to allocate memory";
    }

    private string ExecFreeMemory(JsonElement a)
    {
        if (!_api.IsBreakState) return "Error: process must be in break state";
        var addr = ParseHex(a.GetProperty("address").GetString()!);
        return _api.Memory.FreeMemory(_api.TargetPid, addr)
            ? $"Freed memory at 0x{addr:X}"
            : $"Failed to free memory at 0x{addr:X}";
    }

    private string ExecProtectMemory(JsonElement a)
    {
        if (!_api.IsBreakState) return "Error: process must be in break state";
        var addr  = ParseHex(a.GetProperty("address").GetString()!);
        var size  = (uint)a.GetProperty("size").GetInt64();
        var prot  = (uint)a.GetProperty("protection").GetInt64();
        var (ok, old) = _api.Memory.ProtectMemory(_api.TargetPid, addr, size, prot);
        return ok
            ? $"Protection changed at 0x{addr:X}+0x{size:X}: 0x{old:X} → 0x{prot:X}"
            : $"Failed to change protection at 0x{addr:X}";
    }

    // ── Disassembly / Decompilation ──────────────────────────────────────────

    private string ExecDisassemble(JsonElement a)
    {
        var addr  = ParseHex(a.GetProperty("address").GetString()!);
        int count = 20;
        if (a.TryGetProperty("count", out var ce)) count = Math.Min(ce.GetInt32(), 50);

        var code = _api.Memory.ReadMemory(_api.TargetPid, addr, (uint)(count * 15));
        if (code is null || code.Length == 0) return $"Failed to read memory at 0x{addr:X}";

        var bitness = _api.Is32Bit ? 32 : 64;
        var reader  = new ByteArrayCodeReader(code);
        var dec     = Iced.Intel.Decoder.Create(bitness, reader);
        dec.IP = addr;

        var fmt = new NasmFormatter();
        fmt.Options.DigitSeparator = "";
        fmt.Options.FirstOperandCharIndex = 10;
        fmt.Options.HexPrefix    = "0x";
        fmt.Options.HexSuffix    = null;
        fmt.Options.UppercaseHex = false;

        var out_ = new StringOutput();
        var sb   = new StringBuilder();
        for (int i = 0; i < count; i++)
        {
            var instr = dec.Decode();
            if (instr.IsInvalid) break;
            fmt.Format(instr, out_);
            var sym = _api.Symbols.ResolveAddress(instr.IP);
            sb.AppendLine($"{instr.IP:X16}  {out_.ToStringAndReset()}{(sym != null ? $"  ; {sym}" : "")}");
        }
        return sb.ToString();
    }

    private string ExecNavigateDisasm(JsonElement a)
    {
        var addr = ParseHex(a.GetProperty("address").GetString()!);
        OnUi(() => _api.UI.NavigateDisassembly(addr));
        return $"Navigated to 0x{addr:X}";
    }

    private string ExecDisasmGoBack()
    {
        OnUi(() => _api.UI.DisasmGoBack());
        return "Navigated back";
    }

    private string ExecDecompile(JsonElement a)
    {
        if (!_api.IsBreakState) return "Error: process must be in break state";
        var addr = ParseHex(a.GetProperty("address").GetString()!);
        OnUi(() => _api.UI.DecompileFunction(addr));

        var last = OnUi(() => _api.UI.GetDecompiledCode());
        while (true)
        {
            Thread.Sleep(200);
            var code = OnUi(() => _api.UI.GetDecompiledCode());
            if (!string.IsNullOrEmpty(code) && code != last && !code.Contains("Decompiling..."))
                return code.Length > 3000 ? code[..3000] + "\n// ... (truncated)" : code;
        }
    }

    // ── Symbols / Modules ────────────────────────────────────────────────────

    private string ExecResolveSymbol(JsonElement a)
    {
        var name = a.GetProperty("name").GetString()!.Trim();
        if (name.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            || name.All(c => "0123456789abcdefABCDEF".Contains(c)))
        {
            var addr = ParseHex(name);
            var sym  = _api.Symbols.ResolveAddress(addr);
            return sym != null ? $"0x{addr:X} = {sym}" : $"No symbol at 0x{addr:X}";
        }
        var resolved = _api.Symbols.ResolveNameToAddress(name);
        return resolved != 0 ? $"{name} = 0x{resolved:X}" : $"Symbol '{name}' not found";
    }

    private string ExecListModules()
    {
        var mods = _api.Symbols.GetModules();
        if (mods is null || mods.Count == 0) return "No modules loaded";
        var sb = new StringBuilder();
        foreach (var m in mods)
            sb.AppendLine($"0x{m.BaseAddress:X16}  +0x{m.Size:X8}  {m.Name}");
        return sb.ToString();
    }

    private string ExecListKernelModules()
    {
        var mods = _api.Symbols.GetKernelModules();
        if (mods is null || mods.Count == 0) return "No kernel modules found";
        var sb = new StringBuilder();
        foreach (var m in mods)
            sb.AppendLine($"0x{m.BaseAddress:X16}  +0x{m.Size:X8}  #{m.LoadOrder,-4}  {m.Name}");
        return sb.ToString();
    }

    // ── Process / Threads ────────────────────────────────────────────────────

    private string ExecListProcesses()
    {
        var procs = _api.Process.EnumProcesses();
        if (procs is null || procs.Count == 0) return "No processes found";
        var sb = new StringBuilder();
        foreach (var p in procs.OrderBy(x => x.ProcessId))
            sb.AppendLine($"PID={p.ProcessId,6}  Session={p.SessionId}  {p.Name}");
        return sb.ToString();
    }

    private string ExecListThreads()
    {
        var threads = _api.Process.EnumThreads(_api.TargetPid);
        if (threads is null || threads.Count == 0) return "No threads found";
        var sb = new StringBuilder();
        foreach (var t in threads)
        {
            var sym     = _api.Symbols.ResolveAddress(t.StartAddress);
            var current = t.ThreadId == _api.SelectedThreadId ? " <<< current" : "";
            sb.AppendLine($"TID={t.ThreadId,6}  Start=0x{t.StartAddress:X16}{Sym(sym)}  State={t.State}  Pri={t.Priority}{current}");
        }
        return sb.ToString();
    }

    private string ExecSuspendThread(JsonElement a)
    {
        var tid = (uint)a.GetProperty("tid").GetInt64();
        return _api.Process.SuspendThread(tid)
            ? $"Thread {tid} suspended"
            : $"Failed to suspend thread {tid}";
    }

    private string ExecResumeThread(JsonElement a)
    {
        var tid = (uint)a.GetProperty("tid").GetInt64();
        return _api.Process.ResumeThread(tid)
            ? $"Thread {tid} resumed"
            : $"Failed to resume thread {tid}";
    }

    private string ExecSwitchThread(JsonElement a)
    {
        var tid = (uint)a.GetProperty("tid").GetInt64();
        var threads = _api.Process.EnumThreads(_api.TargetPid);
        if (threads is null || threads.All(t => t.ThreadId != tid))
            return $"TID {tid} not found in PID {_api.TargetPid}";
        _api.Process.SwitchToThread(tid);
        return $"Switched debugger focus to TID {tid} (registers, disassembly, stack updated)";
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

    private void SnapshotRip()
    {
        var regs = _api.Memory.ReadRegisters(_api.TargetPid, _api.SelectedThreadId);
        _ripBeforeResume = regs?.FirstOrDefault(r => r.Name is "RIP" or "EIP")?.Value ?? 0;
    }

    private string ExecContinue()
    {
        if (!OnUi(() => _api.IsBreakState)) return "Error: process is not in break state";
        SnapshotRip();
        OnUi(() => _api.Continue());
        return "Resumed (F9). Call wait_for_break before reading state.";
    }

    private string ExecSingleStep()
    {
        if (!OnUi(() => _api.IsBreakState)) return "Error: process is not in break state";
        OnUi(() => _api.SingleStep());
        return "Step Into (F7) executed";
    }

    private string ExecStepOver()
    {
        if (!OnUi(() => _api.IsBreakState)) return "Error: process is not in break state";
        OnUi(() => _api.StepOver());
        return "Step Over (F8) executed";
    }

    private string ExecStepOut()
    {
        if (!OnUi(() => _api.IsBreakState)) return "Error: process is not in break state";
        OnUi(() => _api.StepOut());
        return "Step Out (Ctrl+F9) executed";
    }

    private string ExecRunToAddress(JsonElement a)
    {
        if (!OnUi(() => _api.IsBreakState)) return "Error: process is not in break state";
        var addr = ParseHex(a.GetProperty("address").GetString()!);
        SnapshotRip();
        OnUi(() => _api.RunToCursor(addr));
        return $"Running to 0x{addr:X}{Sym(_api.Symbols.ResolveAddress(addr))} (F4). Call wait_for_break after.";
    }

    private string ExecSkipInstruction()
    {
        if (!OnUi(() => _api.IsBreakState)) return "Error: process is not in break state";
        OnUi(() => _api.SkipInstruction());
        return "Instruction skipped (Ctrl+F8)";
    }

    private string ExecPause()
    {
        if (OnUi(() => _api.IsBreakState)) return "Process is already paused";
        OnUi(() => _api.Pause());
        return "Pause (F12) sent";
    }

    // Shared: last RIP before continue/run was issued, used by wait_for_break
    private ulong _ripBeforeResume;

    private string ExecWaitForBreak(JsonElement a)
    {
        int timeout = 10_000;
        if (a.TryGetProperty("timeout_ms", out var tp)) timeout = tp.GetInt32();

        var sw        = System.Diagnostics.Stopwatch.StartNew();
        var startRip  = _ripBeforeResume;

        // Poll until: (a) we're in break state AND (b) RIP changed from pre-resume value.
        // This handles the case where the process resumes and breaks again so fast
        // that we never observe IsBreakState == false.
        while (sw.ElapsedMilliseconds < timeout)
        {
            bool inBreak = OnUi(() => _api.IsBreakState);
            if (inBreak)
            {
                var regs = _api.Memory.ReadRegisters(_api.TargetPid, _api.SelectedThreadId);
                var rip  = regs?.FirstOrDefault(r => r.Name is "RIP" or "EIP")?.Value ?? 0;

                if (rip != startRip || sw.ElapsedMilliseconds > 500)
                {
                    // RIP changed → execution happened; or enough time passed → report current state
                    var sym = rip != 0 ? _api.Symbols.ResolveAddress(rip) : null;
                    return $"Break at RIP=0x{rip:X}{Sym(sym)} after {sw.ElapsedMilliseconds}ms";
                }
            }
            Thread.Sleep(30);
        }

        // Final check
        if (OnUi(() => _api.IsBreakState))
        {
            var regs = _api.Memory.ReadRegisters(_api.TargetPid, _api.SelectedThreadId);
            var rip  = regs?.FirstOrDefault(r => r.Name is "RIP" or "EIP")?.Value ?? 0;
            var sym  = rip != 0 ? _api.Symbols.ResolveAddress(rip) : null;
            return $"Break at RIP=0x{rip:X}{Sym(sym)} after {sw.ElapsedMilliseconds}ms";
        }

        return $"Timeout after {timeout}ms — process still running. Use pause_execution to force break.";
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

    private string ExecSpoofSharedUserData(JsonElement a)
    {
        var enable = a.GetProperty("enable").GetBoolean();
        return _api.Process.SetSpoofSharedUserData(enable)
            ? $"SharedUserData spoofing {(enable ? "enabled" : "disabled")}"
            : $"Failed to {(enable ? "enable" : "disable")} SharedUserData spoofing";
    }

    // ── UI helpers ────────────────────────────────────────────────────────────

    private string ExecAddUnpackedModule(JsonElement a)
    {
        var peBase = ParseHex(a.GetProperty("pe_base").GetString()!);
        var name   = a.GetProperty("name").GetString()!;
        _api.UI.AddUnpackedModule(peBase, name);
        return $"Registered unpacked module '{name}' at 0x{peBase:X}";
    }

    private string ExecRefreshModules()
    {
        _api.UI.RefreshModulesAndSections();
        return "Module list refreshed";
    }

    // ── Composite / high-level commands ─────────────────────────────────────

    private string ExecWriteRipAndRsp(JsonElement a)
    {
        if (!_api.IsBreakState) return "Error: process must be in break state";
        var rip = ParseHex(a.GetProperty("rip").GetString()!);
        var rsp = ParseHex(a.GetProperty("rsp").GetString()!);
        var ok  = _api.Memory.WriteRipAndRsp(_api.SelectedThreadId, rip, rsp);
        return ok
            ? $"RIP=0x{rip:X}{Sym(_api.Symbols.ResolveAddress(rip))}, RSP=0x{rsp:X}"
            : "Failed to write RIP/RSP";
    }

    private string ExecAddModuleSections(JsonElement a)
    {
        var modName     = a.GetProperty("module_name").GetString()!;
        var sectionsStr = a.GetProperty("sections").GetString()!;

        using var doc = JsonDocument.Parse(sectionsStr);
        var sections = new List<PluginSectionInfo>();
        foreach (var s in doc.RootElement.EnumerateArray())
        {
            sections.Add(new PluginSectionInfo
            {
                Name           = s.GetProperty("name").GetString() ?? ".?",
                VirtualAddress = ParseHex(s.GetProperty("va").GetString()!),
                VirtualSize    = (uint)s.GetProperty("vsize").GetInt64(),
                Characteristics = (uint)s.GetProperty("chr").GetInt64()
            });
        }

        _api.UI.AddModuleSections(modName, sections);
        return $"Added {sections.Count} sections to '{modName}'";
    }

    private string ExecDumpStack(JsonElement a)
    {
        if (!_api.IsBreakState) return "Error: process must be in break state";
        int count = 16;
        if (a.TryGetProperty("count", out var ce)) count = Math.Clamp(ce.GetInt32(), 1, 64);

        var regs = _api.Memory.ReadRegisters(_api.TargetPid, _api.SelectedThreadId);
        var rsp  = regs?.FirstOrDefault(r => r.Name is "RSP" or "ESP")?.Value ?? 0;
        if (rsp == 0) return "Failed to read RSP";

        var ptrSize = _api.Is32Bit ? 4u : 8u;
        var data    = _api.Memory.ReadMemory(_api.TargetPid, rsp, ptrSize * (uint)count);
        if (data is null) return $"Failed to read stack at 0x{rsp:X}";

        var sb = new StringBuilder();
        sb.AppendLine($"Stack dump from RSP=0x{rsp:X}:");
        for (int i = 0; i < count; i++)
        {
            var off  = i * (int)ptrSize;
            if (off + (int)ptrSize > data.Length) break;
            ulong val = ptrSize == 8
                ? BitConverter.ToUInt64(data, off)
                : BitConverter.ToUInt32(data, off);
            var sym    = _api.Symbols.ResolveAddress(val);
            var marker = i == 0 ? " <<< RSP" : "";
            sb.AppendLine($"  [RSP+0x{off:X2}]  0x{val:X16}{Sym(sym)}{marker}");
        }
        return sb.ToString();
    }

    private string ExecDumpPeb()
    {
        if (!_api.IsBreakState) return "Error: process must be in break state";
        var (peb64, _) = _api.Process.GetPebAddress(_api.TargetPid);
        if (peb64 == 0) return "Failed to get PEB address";

        // Read first 0x400 bytes of PEB
        var peb = _api.Memory.ReadMemory(_api.TargetPid, peb64, 0x400);
        if (peb is null) return $"Failed to read PEB at 0x{peb64:X}";

        var sb = new StringBuilder();
        sb.AppendLine($"PEB @ 0x{peb64:X}");

        byte beingDebugged = peb[0x02];
        uint ntGlobalFlag  = BitConverter.ToUInt32(peb, 0xBC);
        ulong imageBase    = BitConverter.ToUInt64(peb, 0x10);
        ulong ldr          = BitConverter.ToUInt64(peb, 0x18);
        ulong procParams   = BitConverter.ToUInt64(peb, 0x20);
        ulong procHeap     = BitConverter.ToUInt64(peb, 0x30);
        uint  numProc      = BitConverter.ToUInt32(peb, 0xB8);
        uint  osMajor      = BitConverter.ToUInt32(peb, 0x118);
        uint  osMinor      = BitConverter.ToUInt32(peb, 0x11C);

        sb.AppendLine($"  BeingDebugged    : {beingDebugged}");
        sb.AppendLine($"  ImageBaseAddress : 0x{imageBase:X}");
        sb.AppendLine($"  Ldr              : 0x{ldr:X}");
        sb.AppendLine($"  ProcessParameters: 0x{procParams:X}");
        sb.AppendLine($"  ProcessHeap      : 0x{procHeap:X}");
        sb.AppendLine($"  NtGlobalFlag     : 0x{ntGlobalFlag:X} {((ntGlobalFlag & 0x70) != 0 ? "(DEBUGGER FLAGS SET)" : "(clean)")}");
        sb.AppendLine($"  NumberOfProcessors: {numProc}");
        sb.AppendLine($"  OSVersion        : {osMajor}.{osMinor}");

        // Read ProcessParameters to get ImagePathName and CommandLine
        if (procParams != 0)
        {
            var pp = _api.Memory.ReadMemory(_api.TargetPid, procParams, 0x100);
            if (pp != null)
            {
                // ImagePathName: UNICODE_STRING at offset 0x60
                ushort imgLen  = BitConverter.ToUInt16(pp, 0x60);
                ulong  imgBuf = BitConverter.ToUInt64(pp, 0x68);
                if (imgLen > 0 && imgBuf != 0)
                {
                    var imgData = _api.Memory.ReadMemory(_api.TargetPid, imgBuf, imgLen);
                    if (imgData != null)
                        sb.AppendLine($"  ImagePathName    : {Encoding.Unicode.GetString(imgData)}");
                }

                // CommandLine: UNICODE_STRING at offset 0x70
                ushort cmdLen  = BitConverter.ToUInt16(pp, 0x70);
                ulong  cmdBuf = BitConverter.ToUInt64(pp, 0x78);
                if (cmdLen > 0 && cmdBuf != 0)
                {
                    var cmdData = _api.Memory.ReadMemory(_api.TargetPid, cmdBuf, Math.Min(cmdLen, (ushort)512));
                    if (cmdData != null)
                        sb.AppendLine($"  CommandLine      : {Encoding.Unicode.GetString(cmdData)}");
                }
            }
        }

        return sb.ToString();
    }

    private string ExecDumpTeb()
    {
        if (!_api.IsBreakState) return "Error: process must be in break state";

        // TEB is typically at gs:[0x30] — we read it from the register
        // For x64, TEB address can be obtained by reading GS base or from the register dump
        // We'll use the PEB address approach: PEB is at TEB+0x60, so TEB = PEB - offset
        // Alternatively, read it from memory — but simplest: read GS base if available
        // Actually, let's just get it by reading the Self pointer at TEB+0x30 (on x64, TEB
        // stores a self pointer at offset 0x30)

        // We need to find TEB. On x64, the stack range in TEB can help us locate it.
        // Simpler: read the thread's TEB via NtQueryInformationThread, but we don't have that.
        // Best approach: use the fact that PEB is at TEB+0x60. We know PEB, so search.
        var (peb64, _) = _api.Process.GetPebAddress(_api.TargetPid);
        if (peb64 == 0) return "Failed to get PEB address";

        // Heuristic: TEB is usually at PEB - 0x1000 * (tid_index+1) or nearby.
        // Better: for the current thread, GS:0x30 = linear address of TEB.
        // Since we can read memory around PEB, try PEB + 0x1000, PEB - 0x1000, etc.
        // Actually the most reliable way: scan a few pages before PEB for the self-pointer pattern.

        // Try common TEB locations (usually TEB is at PEB ± small offset, on same page range)
        ulong teb = 0;
        for (long delta = 0x1000; delta <= 0x10000; delta += 0x1000)
        {
            ulong candidate = peb64 + (ulong)delta;
            var probe = _api.Memory.ReadMemory(_api.TargetPid, candidate + 0x30, 8);
            if (probe != null && BitConverter.ToUInt64(probe, 0) == candidate)
            {
                teb = candidate; break;
            }
            candidate = peb64 - (ulong)delta;
            if (candidate > 0x10000) // sanity
            {
                probe = _api.Memory.ReadMemory(_api.TargetPid, candidate + 0x30, 8);
                if (probe != null && BitConverter.ToUInt64(probe, 0) == candidate)
                {
                    teb = candidate; break;
                }
            }
        }

        if (teb == 0) return "Could not locate TEB (self-pointer scan failed)";

        var data = _api.Memory.ReadMemory(_api.TargetPid, teb, 0x1800);
        if (data is null) return $"Failed to read TEB at 0x{teb:X}";

        var sb = new StringBuilder();
        sb.AppendLine($"TEB @ 0x{teb:X}");

        ulong stackBase  = BitConverter.ToUInt64(data, 0x08);
        ulong stackLimit = BitConverter.ToUInt64(data, 0x10);
        ulong self       = BitConverter.ToUInt64(data, 0x30);
        ulong pebPtr     = BitConverter.ToUInt64(data, 0x60);
        uint  lastError  = BitConverter.ToUInt32(data, 0x68);
        uint  curTid     = BitConverter.ToUInt32(data, 0x48);
        uint  curPid     = BitConverter.ToUInt32(data, 0x40);

        sb.AppendLine($"  Self            : 0x{self:X}");
        sb.AppendLine($"  ProcessId       : {curPid}");
        sb.AppendLine($"  ThreadId        : {curTid}");
        sb.AppendLine($"  StackBase       : 0x{stackBase:X}");
        sb.AppendLine($"  StackLimit      : 0x{stackLimit:X}");
        sb.AppendLine($"  Stack size      : 0x{stackBase - stackLimit:X} ({(stackBase - stackLimit) / 1024} KB)");
        sb.AppendLine($"  PEB             : 0x{pebPtr:X}");
        sb.AppendLine($"  LastErrorValue  : 0x{lastError:X} ({lastError})");

        // SameTebFlags at 0x17EE (Win10+)
        if (data.Length > 0x17F0)
        {
            ushort sameTebFlags = BitConverter.ToUInt16(data, 0x17EE);
            sb.AppendLine($"  SameTebFlags    : 0x{sameTebFlags:X}{((sameTebFlags & 0x02) != 0 ? " (DbgInDebugger SET)" : "")}");
        }

        return sb.ToString();
    }

    private string ExecDumpPeHeader(JsonElement a)
    {
        var baseAddr = ParseHex(a.GetProperty("address").GetString()!);
        var header = _api.Memory.ReadMemory(_api.TargetPid, baseAddr, 0x1000); // first page
        if (header is null) return $"Failed to read memory at 0x{baseAddr:X}";

        if (header[0] != 0x4D || header[1] != 0x5A) return "Not a valid PE — missing MZ signature";

        uint peOff = BitConverter.ToUInt32(header, 0x3C);
        if (peOff + 0x120 > header.Length) return $"PE header offset 0x{peOff:X} out of range";

        var sb = new StringBuilder();
        sb.AppendLine($"PE @ 0x{baseAddr:X}");

        ushort magic  = BitConverter.ToUInt16(header, (int)peOff + 0x18);
        bool   pe32p  = magic == 0x020B; // PE32+
        sb.AppendLine($"  Magic           : 0x{magic:X} ({(pe32p ? "PE32+ (x64)" : "PE32 (x86)")})");

        ushort numSec = BitConverter.ToUInt16(header, (int)peOff + 0x06);
        uint   entryRva;
        uint   imageSize;
        int    optOff = (int)peOff + 0x18;

        if (pe32p)
        {
            entryRva  = BitConverter.ToUInt32(header, optOff + 0x10);
            imageSize = BitConverter.ToUInt32(header, optOff + 0x38);
        }
        else
        {
            entryRva  = BitConverter.ToUInt32(header, optOff + 0x10);
            imageSize = BitConverter.ToUInt32(header, optOff + 0x38);
        }

        sb.AppendLine($"  EntryPoint RVA  : 0x{entryRva:X} → 0x{baseAddr + entryRva:X}");
        sb.AppendLine($"  ImageSize       : 0x{imageSize:X}");
        sb.AppendLine($"  Sections        : {numSec}");

        // Data directories
        int ddOff  = pe32p ? optOff + 0x70 : optOff + 0x60;
        int ddCount = Math.Min((int)BitConverter.ToUInt32(header, ddOff - 4), 16);
        string[] ddNames = ["Export", "Import", "Resource", "Exception", "Security", "BaseReloc",
                            "Debug", "Architecture", "GlobalPtr", "TLS", "LoadConfig", "BoundImport",
                            "IAT", "DelayImport", "CLR", "Reserved"];

        sb.AppendLine("  Data Directories:");
        for (int i = 0; i < ddCount && ddOff + i * 8 + 8 <= header.Length; i++)
        {
            uint rva  = BitConverter.ToUInt32(header, ddOff + i * 8);
            uint size = BitConverter.ToUInt32(header, ddOff + i * 8 + 4);
            if (rva != 0 || size != 0)
                sb.AppendLine($"    [{i,2}] {(i < ddNames.Length ? ddNames[i] : "?"),-14}  RVA=0x{rva:X8}  Size=0x{size:X}");
        }

        // Section table
        int secOff = (int)peOff + 0x18 + BitConverter.ToUInt16(header, (int)peOff + 0x14);
        sb.AppendLine("  Sections:");
        for (int i = 0; i < numSec && secOff + 40 <= header.Length; i++)
        {
            string name = Encoding.ASCII.GetString(header, secOff, 8).TrimEnd('\0');
            uint   vsize = BitConverter.ToUInt32(header, secOff + 0x08);
            uint   vaddr = BitConverter.ToUInt32(header, secOff + 0x0C);
            uint   rsize = BitConverter.ToUInt32(header, secOff + 0x10);
            uint   chars = BitConverter.ToUInt32(header, secOff + 0x24);
            string flags = "";
            if ((chars & 0x20000000) != 0) flags += "X";
            if ((chars & 0x40000000) != 0) flags += "R";
            if ((chars & 0x80000000) != 0) flags += "W";
            sb.AppendLine($"    {name,-8}  VA=0x{vaddr:X8}  VSize=0x{vsize:X8}  RSize=0x{rsize:X8}  {flags}");
            secOff += 40;
        }

        return sb.ToString();
    }

    private string ExecDumpImports(JsonElement a)
    {
        var baseAddr = ParseHex(a.GetProperty("address").GetString()!);
        var header = _api.Memory.ReadMemory(_api.TargetPid, baseAddr, 0x1000);
        if (header is null) return $"Failed to read PE at 0x{baseAddr:X}";
        if (header[0] != 0x4D || header[1] != 0x5A) return "Not a valid PE";

        uint peOff   = BitConverter.ToUInt32(header, 0x3C);
        ushort magic = BitConverter.ToUInt16(header, (int)peOff + 0x18);
        bool pe32p   = magic == 0x020B;
        int ddOff    = pe32p ? (int)peOff + 0x18 + 0x70 : (int)peOff + 0x18 + 0x60;

        // Import directory = data dir [1]
        uint impRva  = BitConverter.ToUInt32(header, ddOff + 8);
        uint impSize = BitConverter.ToUInt32(header, ddOff + 12);
        if (impRva == 0) return "No import directory";

        var impData = _api.Memory.ReadMemory(_api.TargetPid, baseAddr + impRva, Math.Min(impSize, 8192u));
        if (impData is null) return "Failed to read import directory";

        var sb = new StringBuilder();
        sb.AppendLine("Import Directory:");
        int entrySize = 20; // IMAGE_IMPORT_DESCRIPTOR

        for (int off = 0; off + entrySize <= impData.Length; off += entrySize)
        {
            uint ilt     = BitConverter.ToUInt32(impData, off);
            uint nameRva = BitConverter.ToUInt32(impData, off + 12);
            uint iat     = BitConverter.ToUInt32(impData, off + 16);
            if (nameRva == 0 && ilt == 0) break; // null terminator

            // Read DLL name
            var nameData = _api.Memory.ReadMemory(_api.TargetPid, baseAddr + nameRva, 128);
            string dllName = "???";
            if (nameData != null)
            {
                int end = Array.IndexOf(nameData, (byte)0);
                if (end < 0) end = nameData.Length;
                dllName = Encoding.ASCII.GetString(nameData, 0, end);
            }

            sb.AppendLine($"\n  {dllName}  (IAT=0x{iat:X}, ILT=0x{ilt:X})");

            // Walk IAT entries
            ulong iatAddr = baseAddr + iat;
            uint  ptrSize = pe32p ? 8u : 4u;
            for (int i = 0; i < 200; i++) // max 200 imports per DLL
            {
                var ptrData = _api.Memory.ReadMemory(_api.TargetPid, iatAddr + (ulong)(i * (int)ptrSize), ptrSize);
                if (ptrData is null) break;
                ulong val = ptrSize == 8 ? BitConverter.ToUInt64(ptrData, 0) : BitConverter.ToUInt32(ptrData, 0);
                if (val == 0) break;

                var sym = _api.Symbols.ResolveAddress(val);
                if (sym != null)
                    sb.AppendLine($"    [{i,3}] 0x{val:X} {sym}");
                else
                    sb.AppendLine($"    [{i,3}] 0x{val:X}");
            }
        }

        return sb.ToString();
    }

    private string ExecDumpExports(JsonElement a)
    {
        var baseAddr = ParseHex(a.GetProperty("address").GetString()!);
        var header = _api.Memory.ReadMemory(_api.TargetPid, baseAddr, 0x1000);
        if (header is null) return $"Failed to read PE at 0x{baseAddr:X}";
        if (header[0] != 0x4D || header[1] != 0x5A) return "Not a valid PE";

        uint peOff   = BitConverter.ToUInt32(header, 0x3C);
        ushort magic = BitConverter.ToUInt16(header, (int)peOff + 0x18);
        bool pe32p   = magic == 0x020B;
        int ddOff    = pe32p ? (int)peOff + 0x18 + 0x70 : (int)peOff + 0x18 + 0x60;

        // Export directory = data dir [0]
        uint expRva  = BitConverter.ToUInt32(header, ddOff);
        uint expSize = BitConverter.ToUInt32(header, ddOff + 4);
        if (expRva == 0) return "No export directory";

        var expData = _api.Memory.ReadMemory(_api.TargetPid, baseAddr + expRva, Math.Min(expSize, 0x10000u));
        if (expData is null) return "Failed to read export directory";

        // We also need to read the function/name/ordinal arrays which may be outside the export dir
        uint numFuncs = BitConverter.ToUInt32(expData, 0x14);
        uint numNames = BitConverter.ToUInt32(expData, 0x18);
        uint funcsRva = BitConverter.ToUInt32(expData, 0x1C);
        uint namesRva = BitConverter.ToUInt32(expData, 0x20);
        uint ordsRva  = BitConverter.ToUInt32(expData, 0x24);
        uint ordBase  = BitConverter.ToUInt32(expData, 0x10);

        // Read DLL name
        uint nameRva = BitConverter.ToUInt32(expData, 0x0C);
        var dllNameData = _api.Memory.ReadMemory(_api.TargetPid, baseAddr + nameRva, 128);
        string dllName = "?";
        if (dllNameData != null)
        {
            int e = Array.IndexOf(dllNameData, (byte)0);
            dllName = Encoding.ASCII.GetString(dllNameData, 0, e < 0 ? dllNameData.Length : e);
        }

        var funcsData = _api.Memory.ReadMemory(_api.TargetPid, baseAddr + funcsRva, numFuncs * 4);
        var namesData = _api.Memory.ReadMemory(_api.TargetPid, baseAddr + namesRva, numNames * 4);
        var ordsData  = _api.Memory.ReadMemory(_api.TargetPid, baseAddr + ordsRva,  numNames * 2);
        if (funcsData is null || namesData is null || ordsData is null) return "Failed to read export tables";

        var sb = new StringBuilder();
        sb.AppendLine($"Exports from {dllName} ({numNames} named, {numFuncs} total, ordinal base {ordBase}):");

        for (int i = 0; i < (int)numNames && i < 500; i++)
        {
            uint fnameRva = BitConverter.ToUInt32(namesData, i * 4);
            ushort ord    = BitConverter.ToUInt16(ordsData, i * 2);
            uint   funcRva = BitConverter.ToUInt32(funcsData, ord * 4);

            var fnameData = _api.Memory.ReadMemory(_api.TargetPid, baseAddr + fnameRva, 128);
            string fname = "?";
            if (fnameData != null)
            {
                int e = Array.IndexOf(fnameData, (byte)0);
                fname = Encoding.ASCII.GetString(fnameData, 0, e < 0 ? fnameData.Length : e);
            }

            sb.AppendLine($"  [{ord + ordBase,5}]  0x{funcRva:X8}  → 0x{baseAddr + funcRva:X}  {fname}");
        }

        return sb.ToString();
    }

    private string ExecXrefsTo(JsonElement a)
    {
        var target = ParseHex(a.GetProperty("address").GetString()!);

        // Find main module to scan
        var mods = _api.Symbols.GetModules();
        if (mods is null || mods.Count == 0) return "No modules loaded";
        var mainMod = mods[0]; // first module = main exe

        var code = _api.Memory.ReadMemory(_api.TargetPid, mainMod.BaseAddress, mainMod.Size);
        if (code is null) return $"Failed to read module {mainMod.Name}";

        var results = new List<(ulong addr, string type)>();
        var bitness = _api.Is32Bit ? 32 : 64;
        var reader  = new ByteArrayCodeReader(code);
        var dec     = Iced.Intel.Decoder.Create(bitness, reader);
        dec.IP = mainMod.BaseAddress;

        // Scan all instructions
        while (reader.CanReadByte && results.Count < 100)
        {
            var instr = dec.Decode();
            if (instr.IsInvalid) { dec.IP += 1; reader.Position = (int)(dec.IP - mainMod.BaseAddress); continue; }

            // Check for relative call/jmp to target
            if (instr.FlowControl is FlowControl.Call or FlowControl.UnconditionalBranch or FlowControl.ConditionalBranch)
            {
                ulong dest = 0;
                if (instr.Op0Kind == OpKind.NearBranch64) dest = instr.NearBranch64;
                else if (instr.Op0Kind == OpKind.NearBranch32) dest = instr.NearBranch32;
                else if (instr.Op0Kind == OpKind.NearBranch16) dest = instr.NearBranch16;

                if (dest == target)
                {
                    string type = instr.FlowControl == FlowControl.Call ? "CALL" :
                                  instr.FlowControl == FlowControl.ConditionalBranch ? "JCC" : "JMP";
                    results.Add((instr.IP, type));
                }
            }

            // Check for LEA reg,[rip+disp] pointing to target
            if (instr.Mnemonic == Mnemonic.Lea)
            {
                for (int i = 0; i < instr.OpCount; i++)
                {
                    if (instr.GetOpKind(i) == OpKind.Memory && instr.MemoryBase == Register.RIP)
                    {
                        ulong addr = instr.IPRelativeMemoryAddress;
                        if (addr == target)
                            results.Add((instr.IP, "LEA"));
                    }
                }
            }
        }

        if (results.Count == 0) return $"No xrefs to 0x{target:X} found in {mainMod.Name}";

        var sb = new StringBuilder();
        sb.AppendLine($"Cross-references to 0x{target:X}{Sym(_api.Symbols.ResolveAddress(target))} ({results.Count} found):");
        foreach (var (addr, type) in results)
            sb.AppendLine($"  0x{addr:X16}  {type,-4}{Sym(_api.Symbols.ResolveAddress(addr))}");
        return sb.ToString();
    }

    private string ExecNopInstruction(JsonElement a)
    {
        if (!_api.IsBreakState) return "Error: process must be in break state";
        var addr = ParseHex(a.GetProperty("address").GetString()!);

        // Read and decode one instruction to get its length
        var code = _api.Memory.ReadMemory(_api.TargetPid, addr, 15);
        if (code is null) return $"Failed to read memory at 0x{addr:X}";

        var reader = new ByteArrayCodeReader(code);
        var dec    = Iced.Intel.Decoder.Create(_api.Is32Bit ? 32 : 64, reader);
        dec.IP = addr;
        var instr = dec.Decode();
        if (instr.IsInvalid) return $"Invalid instruction at 0x{addr:X}";

        var nops = new byte[instr.Length];
        Array.Fill(nops, (byte)0x90);
        var ok = _api.Memory.WriteMemory(_api.TargetPid, addr, nops);

        var fmt = new NasmFormatter();
        var out_ = new StringOutput();
        fmt.Format(instr, out_);

        return ok
            ? $"NOPed {instr.Length} bytes at 0x{addr:X}: {out_.ToStringAndReset()} → {instr.Length}x NOP"
            : $"Failed to write NOPs at 0x{addr:X}";
    }

    private string ExecPatchJump(JsonElement a)
    {
        if (!_api.IsBreakState) return "Error: process must be in break state";
        var addr = ParseHex(a.GetProperty("address").GetString()!);
        var mode = a.GetProperty("mode").GetString()!.ToLowerInvariant();
        if (mode is not ("always" or "never")) return "Error: mode must be 'always' or 'never'";

        var code = _api.Memory.ReadMemory(_api.TargetPid, addr, 15);
        if (code is null) return $"Failed to read at 0x{addr:X}";

        var reader = new ByteArrayCodeReader(code);
        var dec    = Iced.Intel.Decoder.Create(_api.Is32Bit ? 32 : 64, reader);
        dec.IP = addr;
        var instr = dec.Decode();
        if (instr.IsInvalid) return $"Invalid instruction at 0x{addr:X}";
        if (instr.FlowControl != FlowControl.ConditionalBranch)
            return $"Instruction at 0x{addr:X} is not a conditional jump";

        byte[] patch;
        string desc;

        if (mode == "never")
        {
            patch = new byte[instr.Length];
            Array.Fill(patch, (byte)0x90);
            desc = $"NOPed ({instr.Length} bytes)";
        }
        else // always
        {
            if (instr.Length == 2)
            {
                // Short JCC (7x XX) → short JMP (EB XX)
                patch = new byte[] { 0xEB, code[1] };
                desc = "short JCC → JMP";
            }
            else if (instr.Length == 6)
            {
                // Near JCC (0F 8x XX XX XX XX) → near JMP (E9 XX XX XX XX + NOP)
                patch = new byte[6];
                patch[0] = 0xE9;
                Buffer.BlockCopy(code, 2, patch, 1, 4);
                patch[5] = 0x90;
                desc = "near JCC → JMP + NOP";
            }
            else
            {
                return $"Unsupported JCC encoding ({instr.Length} bytes) at 0x{addr:X}";
            }
        }

        var ok = _api.Memory.WriteMemory(_api.TargetPid, addr, patch);
        return ok ? $"Patched at 0x{addr:X}: {desc}" : $"Failed to patch at 0x{addr:X}";
    }

    private string ExecListStrings(JsonElement a)
    {
        ulong start;
        uint  size;
        int   minLen = 4;

        if (a.TryGetProperty("min_length", out var ml)) minLen = Math.Max(ml.GetInt32(), 2);

        if (a.TryGetProperty("address", out var ap))
        {
            start = ParseHex(ap.GetString()!);
            size  = a.TryGetProperty("size", out var sp) ? (uint)Math.Min(sp.GetInt64(), 1024 * 1024) : 0x10000;
        }
        else
        {
            // Default: find .rdata of main module
            var mods = _api.Symbols.GetModules();
            if (mods is null || mods.Count == 0) return "No modules loaded";
            var mainMod = mods[0];

            // Parse PE to find .rdata
            var hdr = _api.Memory.ReadMemory(_api.TargetPid, mainMod.BaseAddress, 0x1000);
            if (hdr is null) return "Failed to read PE header";
            uint peOff = BitConverter.ToUInt32(hdr, 0x3C);
            ushort numSec = BitConverter.ToUInt16(hdr, (int)peOff + 0x06);
            int secOff = (int)peOff + 0x18 + BitConverter.ToUInt16(hdr, (int)peOff + 0x14);

            start = 0; size = 0;
            for (int i = 0; i < numSec && secOff + 40 <= hdr.Length; i++)
            {
                string sname = Encoding.ASCII.GetString(hdr, secOff, 8).TrimEnd('\0');
                if (sname == ".rdata")
                {
                    start = mainMod.BaseAddress + BitConverter.ToUInt32(hdr, secOff + 0x0C);
                    size  = Math.Min(BitConverter.ToUInt32(hdr, secOff + 0x08), 1024 * 1024);
                    break;
                }
                secOff += 40;
            }
            if (start == 0) return "Could not find .rdata section";
        }

        var data = _api.Memory.ReadMemory(_api.TargetPid, start, size);
        if (data is null) return $"Failed to read 0x{start:X}+0x{size:X}";

        var sb = new StringBuilder();
        int found = 0;

        // ASCII strings
        int run = 0;
        for (int i = 0; i < data.Length && found < 500; i++)
        {
            if (data[i] is >= 0x20 and < 0x7F)
            {
                run++;
            }
            else
            {
                if (data[i] == 0 && run >= minLen)
                {
                    string s = Encoding.ASCII.GetString(data, i - run, run);
                    sb.AppendLine($"  0x{start + (ulong)(i - run):X}  A  \"{s}\"");
                    found++;
                }
                run = 0;
            }
        }

        // Unicode strings (scan for WCHAR sequences)
        run = 0;
        for (int i = 0; i + 1 < data.Length && found < 500; i += 2)
        {
            ushort wc = BitConverter.ToUInt16(data, i);
            if (wc >= 0x20 && wc < 0x7F)
            {
                run++;
            }
            else
            {
                if (wc == 0 && run >= minLen)
                {
                    string s = Encoding.Unicode.GetString(data, i - run * 2, run * 2);
                    sb.AppendLine($"  0x{start + (ulong)(i - run * 2):X}  W  \"{s}\"");
                    found++;
                }
                run = 0;
            }
        }

        if (found == 0) return $"No strings (min {minLen} chars) found in 0x{start:X}+0x{size:X}";
        sb.Insert(0, $"Strings in 0x{start:X}+0x{size:X} ({found} found, A=ASCII W=Wide):\n");
        return sb.ToString();
    }

    private string ExecCompareMemory(JsonElement a)
    {
        var addr1 = ParseHex(a.GetProperty("addr1").GetString()!);
        var addr2 = ParseHex(a.GetProperty("addr2").GetString()!);
        var size  = Math.Min((uint)a.GetProperty("size").GetInt64(), 4096u);

        var data1 = _api.Memory.ReadMemory(_api.TargetPid, addr1, size);
        var data2 = _api.Memory.ReadMemory(_api.TargetPid, addr2, size);
        if (data1 is null) return $"Failed to read memory at 0x{addr1:X}";
        if (data2 is null) return $"Failed to read memory at 0x{addr2:X}";

        int len = Math.Min(data1.Length, data2.Length);
        var diffs = new List<(int offset, byte b1, byte b2)>();

        for (int i = 0; i < len && diffs.Count < 200; i++)
        {
            if (data1[i] != data2[i])
                diffs.Add((i, data1[i], data2[i]));
        }

        if (diffs.Count == 0) return $"Regions are identical ({len} bytes compared)";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {diffs.Count} difference(s) in {len} bytes:");
        sb.AppendLine($"  {"Offset",-10} {"Addr1",-18} {"Addr2",-18} {"Val1",-6} {"Val2",-6}");
        foreach (var (off, b1, b2) in diffs)
            sb.AppendLine($"  +0x{off:X6}   0x{addr1 + (ulong)off:X16}  0x{addr2 + (ulong)off:X16}  0x{b1:X2}   0x{b2:X2}");
        return sb.ToString();
    }

    private string ExecReadUnicodeStruct(JsonElement a)
    {
        var addr = ParseHex(a.GetProperty("address").GetString()!);

        // UNICODE_STRING: Length (USHORT) + MaxLength (USHORT) + pad + Buffer (PVOID)
        var data = _api.Memory.ReadMemory(_api.TargetPid, addr, 16);
        if (data is null) return $"Failed to read at 0x{addr:X}";

        ushort length    = BitConverter.ToUInt16(data, 0);
        ushort maxLength = BitConverter.ToUInt16(data, 2);
        ulong  buffer    = _api.Is32Bit
            ? BitConverter.ToUInt32(data, 4)
            : BitConverter.ToUInt64(data, 8);

        if (buffer == 0) return $"UNICODE_STRING at 0x{addr:X}: Length={length}, Buffer=NULL";
        if (length == 0) return $"UNICODE_STRING at 0x{addr:X}: Length=0, Buffer=0x{buffer:X}";

        var strData = _api.Memory.ReadMemory(_api.TargetPid, buffer, Math.Min(length, (ushort)512));
        if (strData is null) return $"Failed to read buffer at 0x{buffer:X}";

        string text = Encoding.Unicode.GetString(strData);
        return $"UNICODE_STRING at 0x{addr:X}: Length={length}, MaxLength={maxLength}, Buffer=0x{buffer:X}\n  \"{text}\"";
    }

    // ── Schema helpers ────────────────────────────────────────────────────────

    private static object Tool(string name, string desc, object schema, string[]? required = null)
    {
        if (required is { Length: > 0 })
            return new { name, description = desc, inputSchema = WithRequired(schema, required) };
        return new { name, description = desc, inputSchema = schema };
    }

    private static object WithRequired(object schema, string[] required)
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(schema))!.AsObject();
        var arr  = new JsonArray();
        foreach (var r in required) arr.Add(r);
        node["required"] = arr;
        return node;
    }

    private static object Obj(params (string name, string type, string desc)[] props)
    {
        if (props.Length == 0)
            return new { type = "object", properties = new { } };
        var properties = props.ToDictionary(p => p.name, p => (object)new { type = p.type, description = p.desc });
        return new { type = "object", properties };
    }

    private static (string, string, string) Prop(string n, string t, string d) => (n, t, d);

    // ── Misc helpers ──────────────────────────────────────────────────────────

    private static ulong ParseHex(string s)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return ulong.Parse(s, System.Globalization.NumberStyles.HexNumber);
    }

    private static byte[]? ParseHexBytes(string hexStr)
    {
        var clean = hexStr.Replace(" ", "").Replace("-", "");
        if (clean.Length % 2 != 0) return null;
        var bytes = new byte[clean.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = byte.Parse(clean.AsSpan(i * 2, 2), System.Globalization.NumberStyles.HexNumber);
        return bytes;
    }

    private static string FormatHexDump(byte[] data, ulong baseAddr)
    {
        int printable = data.Count(b => b is >= 0x20 and < 0x7F);
        bool isText   = printable > data.Length / 2;
        var  sb       = new StringBuilder();
        sb.AppendLine($"[{data.Length} bytes @ 0x{baseAddr:X}]");

        if (isText && data.Length <= 128)
        {
            sb.Append("HEX:   "); foreach (var b in data) sb.Append($"{b:X2} "); sb.AppendLine();
            sb.Append("ASCII: \"");
            foreach (var b in data) sb.Append(b is >= 0x20 and < 0x7F ? (char)b : b == 0 ? '·' : '.');
            sb.AppendLine("\"");
        }
        else
        {
            for (int i = 0; i < data.Length; i += 16)
            {
                int n = Math.Min(16, data.Length - i);
                sb.Append($"{baseAddr + (ulong)i:X16}  ");
                for (int j = 0; j < n;  j++) sb.Append($"{data[i+j]:X2} ");
                for (int j = n; j < 16; j++) sb.Append("   ");
                sb.Append(" |");
                for (int j = 0; j < n; j++) { var b = data[i+j]; sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.'); }
                sb.AppendLine("|");
            }
        }
        return sb.ToString();
    }

    private static string Sym(string? s) => s != null ? $" ({s})" : "";

    // ── Notes / Bookmarks ────────────────────────────────────────────────────

    private string ExecWriteNote(JsonElement a)
    {
        var addr = ParseHex(a.GetProperty("address").GetString()!);
        var note = a.GetProperty("note").GetString()!;
        OnUi(() => _api.UI.SetAddressAnnotation(addr, note));
        OnUi(() => _api.UI.RefreshDisassembly());
        return $"Note set at 0x{addr:X}: {note}";
    }

    private string ExecReadNote(JsonElement a)
    {
        var addr = ParseHex(a.GetProperty("address").GetString()!);
        var note = OnUi(() => _api.UI.GetAddressAnnotation(addr));
        return note != null ? $"0x{addr:X}: {note}" : $"No note at 0x{addr:X}";
    }

    private string ExecReadAllNotes()
    {
        var all = OnUi(() => _api.UI.GetAllAnnotations());
        if (all.Count == 0) return "No notes/bookmarks";
        var sb = new StringBuilder();
        sb.AppendLine($"{all.Count} note(s):");
        foreach (var (addr, note) in all.OrderBy(kv => kv.Key))
        {
            var sym = OnUi(() => _api.Symbols.ResolveAddress(addr));
            sb.AppendLine($"  0x{addr:X16}  {(sym != null ? $"({sym})  " : "")}{note}");
        }
        return sb.ToString();
    }

    private string ExecRemoveNote(JsonElement a)
    {
        var addr = ParseHex(a.GetProperty("address").GetString()!);
        OnUi(() => _api.UI.SetAddressAnnotation(addr, null));
        OnUi(() => _api.UI.RefreshDisassembly());
        return $"Note removed at 0x{addr:X}";
    }

    // ── Scripting ──────────────────────────────────────────────────────────

    private async Task<string> ExecScript(JsonElement a)
    {
        var code = a.GetProperty("code").GetString()!;
        var executor = OnUi(() => _api.UI.GetPluginData("ScriptExecute") as Func<string, Task<string>>);
        if (executor == null)
            return "Error: Scripting plugin not loaded or disabled.";
        return await executor(code);
    }

    private string ExecScriptingReference() => ScriptingRef;

    private const string ScriptingRef = """
# KernelFlirt Scripting Reference

C# REPL with full debugger API access. Variables persist between executions.

## Shortcuts (global variables)

| Shortcut | Description |
|----------|-------------|
| `api` | Full `IDebuggerApi` |
| `print("text")` | Print to output |
| `ReadMem(addr, size)` | Read bytes → `byte[]?` |
| `WriteMem(addr, data)` | Write bytes → `bool` |
| `ReadString(addr)` | ASCII string (max 256) |
| `ReadString(addr, 1024)` | ASCII with custom limit |
| `ReadWString(addr)` | Unicode (UTF-16) string |
| `ReadPtr(addr)` | Read pointer (8/4 bytes) |
| `ReadU32(addr)` / `ReadU64(addr)` | Read uint32/uint64 |
| `Reg("RAX")` | Register value by name |
| `RIP` / `RSP` | Instruction/stack pointer |
| `Sym(addr)` | Symbol name (null if none) |
| `Addr("module!func")` | Address by symbol name |

## Full API

### State
`api.IsConnected`, `api.IsBreakState`, `api.TargetPid`, `api.SelectedThreadId`, `api.Is32Bit`

### Memory (`api.Memory.*`)
`ReadMemory(pid, addr, size)`, `WriteMemory(pid, addr, data)`, `ReadRegisters(pid, tid)`,
`WriteRip(pid, tid, rip)`, `WriteRipAndRsp(tid, rip, rsp)`,
`ProtectMemory(pid, addr, size, prot)`, `AllocateMemory(pid, size)`, `FreeMemory(pid, addr)`

### Breakpoints (`api.Breakpoints.*`)
`SetBreakpoint(pid, tid, addr, type)` → `uint?`, `RemoveBreakpoint(handle)`, `GetAll()`,
`ToggleBreakpoint(addr, type)` — via UI (updates list + disasm)
Types: `Software`, `Hardware`, `HwWrite`, `HwReadWrite`, `Memory`

### Symbols (`api.Symbols.*`)
`ResolveAddress(addr)` → string?, `ResolveNameToAddress(name)` → ulong, `GetModules()`, `GetKernelModules()`,
`RegisterFunction(addr, name, size)` — register a user-defined function name at address. Args: ulong address, string name, uint size (size in bytes — MUST specify to avoid overlapping with next function).
`GetRegisteredFunctions()` → list of all named functions

### UI (`api.UI.*`)
`NavigateDisassembly(addr)`, `SetAddressAnnotation(addr, text)`, `GetAllAnnotations()`,
`RefreshDisassembly()`, `DecompileFunction(addr)`, `GetDecompiledCode()`, `DisasmGoBack()`

### Execution control
`api.Continue()`, `api.SingleStep()`, `api.StepOver()`, `api.StepOut()`,
`api.RunToCursor(addr)`, `api.SkipInstruction()`, `api.Pause()`

### Events
`api.OnDebugEvent`, `api.OnBreakStateEntered`, `api.OnBeforeRun`
`api.OnDebugEventFilter` — return true to suppress UI break

## Examples

```csharp
// Show registers
var regs = api.Memory.ReadRegisters(api.TargetPid, api.SelectedThreadId);
foreach (var r in regs.Where(r => !r.IsFlag))
    print($"{r.Name,-4} = 0x{r.Value:X016}");
```

```csharp
// Logging breakpoint
var target = Addr("ws2_32!send");
api.OnDebugEventFilter += evt => {
    if (evt.Address != target) return false;
    var buf = ReadPtr(Reg("RDX"));
    var len = (int)Reg("R8");
    print($"send({len}): {Encoding.ASCII.GetString(ReadMem(buf, (uint)Math.Min(len, 128)))}");
    return false;
};
api.Breakpoints.SetBreakpoint(api.TargetPid, 0, target, PluginBreakpointType.Software);
```

```csharp
// Walk linked list
var head = Addr("ntdll!PebLdr") + 0x10;
var entry = ReadPtr(head);
while (entry != head && entry != 0) {
    print($"0x{ReadPtr(entry + 0x30):X} {ReadWString(ReadPtr(entry + 0x48 + 8))}");
    entry = ReadPtr(entry);
}
```

```csharp
// Dump vtable
var vt = ReadPtr(Reg("RCX"));
for (int i = 0; i < 20; i++) {
    var f = ReadPtr(vt + (ulong)(i * 8));
    print($"[{i,2}] 0x{f:X} {Sym(f) ?? "???"}");
}
```

## IMPORTANT: Naming unnamed functions

After decompiling, unnamed functions appear as `rc4_strings.exe+0x1470` (module+offset) in disassembly and decompiled code.
You SHOULD name them based on what they do. Use `RegisterFunction` to assign meaningful names.
This makes all subsequent decompilation and disassembly output human-readable.

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
large range and neighboring functions will show as `FuncName+0xOffset` instead of their own name.
Calculate size as: next function address - this function address.

After naming, the function name will appear in the disassembly view and in the Graph View plugin (CFG).
The decompiler (RetDec) may not pick up the new name, but the disassembly and graph will.
This helps the user navigate and understand the code structure.

Workflow: decompile → understand what function does → name it with size via RegisterFunction → RefreshDisassembly.
""";
}
