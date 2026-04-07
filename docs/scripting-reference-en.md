# KernelFlirt C# Scripting Reference

**Version:** 1.8.1  
**Engine:** Roslyn C# REPL  
**Hotkeys:** **F5** / **Ctrl+Enter** = Run | Select fragment + F5 = Run selection only

The Scripting plugin provides a fully interactive C# REPL with persistent state and complete access to the KernelFlirt kernel debugger API. Scripts run inside the debugger process, can read/write target memory, set breakpoints, control execution, and manipulate the UI.

---

## Table of Contents

1. [Getting Started](#1-getting-started)
2. [Shortcuts (Global Variables)](#2-shortcuts-global-variables)
3. [REPL Behavior](#3-repl-behavior)
4. [Debugger State (api.*)](#4-debugger-state)
5. [Memory API (api.Memory.*)](#5-memory-api)
6. [Breakpoint API (api.Breakpoints.*)](#6-breakpoint-api)
7. [Symbol API (api.Symbols.*)](#7-symbol-api)
8. [Process API (api.Process.*)](#8-process-api)
9. [UI API (api.UI.*)](#9-ui-api)
10. [Log API (api.Log.*)](#10-log-api)
11. [Execution Control](#11-execution-control)
12. [Events](#12-events)
13. [Data Models](#13-data-models)
14. [Function Naming (RegisterFunction)](#14-function-naming)
15. [Anti-Debug Bypass](#15-anti-debug-bypass)
16. [PE Analysis](#16-pe-analysis)
17. [Stack Walking](#17-stack-walking)
18. [String Decryption](#18-string-decryption)
19. [IAT Reconstruction](#19-iat-reconstruction)
20. [Unpacker Scripting](#20-unpacker-scripting)
21. [Memory Scanning](#21-memory-scanning)
22. [Recipes](#22-recipes)
23. [Tips and Pitfalls](#23-tips-and-pitfalls)
24. [Auto-imported Namespaces](#24-auto-imported-namespaces)

---

## 1. Getting Started

The Scripting tab in KernelFlirt provides a C# code editor and output panel. Type or paste code into the editor and press **F5** or **Ctrl+Enter** to execute. You can also select a fragment of code and press **F5** to run only the selection.

The REPL exposes two primary globals:

- `api` -- the full `IDebuggerApi` interface to the debugger
- `print(string)` -- output text to the scripting output panel

A set of helper shortcuts (lambdas) are injected automatically on the first execution and persist throughout the session.

**Minimal example:**

```csharp
// Print current instruction pointer
print($"RIP = 0x{Reg("RIP"):X}");
```

**Requirements for scripting:**

- The debugger must be connected to a target (`api.IsConnected == true`)
- For most memory/register operations, the target must be in break state (`api.IsBreakState == true`)
- The target PID must be set (`api.TargetPid != 0`) for user-mode operations

---

## 2. Shortcuts (Global Variables)

These helper lambdas and variables are injected automatically on the first script execution. They persist across all subsequent runs in the same session. Use `Reset State` to clear them and re-inject on next run.

### Memory Read Shortcuts

| Shortcut | Signature | Return Type | Description |
|----------|-----------|-------------|-------------|
| `ReadMem` | `(ulong addr, uint size)` | `byte[]?` | Read `size` bytes from the target process at `addr`. Returns `null` on failure. Uses `api.TargetPid` automatically. |
| `WriteMem` | `(ulong addr, byte[] data)` | `bool` | Write `data` bytes to `addr` in target process. Returns `true` on success. |
| `ReadString` | `(ulong addr, int maxLen)` | `string` | Read null-terminated ASCII string at `addr`. Reads up to `maxLen` bytes. Returns `"<read failed>"` on error. |
| `ReadWString` | `(ulong addr, int maxLen)` | `string` | Read null-terminated Unicode (UTF-16LE) string at `addr`. `maxLen` is in characters (reads `maxLen * 2` bytes). Returns `"<read failed>"` on error. |
| `ReadPtr` | `(ulong addr)` | `ulong` | Read a pointer-sized value (8 bytes on x64, 4 bytes on x86). Returns `0` on failure. |
| `ReadU32` | `(ulong addr)` | `uint` | Read 4 bytes as an unsigned 32-bit integer. Returns `0` on failure. |
| `ReadU64` | `(ulong addr)` | `ulong` | Read 8 bytes as an unsigned 64-bit integer. Returns `0` on failure. |

### Register and Symbol Shortcuts

| Shortcut | Signature | Return Type | Description |
|----------|-----------|-------------|-------------|
| `Reg` | `(string name)` | `ulong` | Read a register value by name (case-insensitive). Examples: `Reg("RAX")`, `Reg("rsp")`, `Reg("R8")`. Returns `0` if not found. |
| `Sym` | `(ulong addr)` | `string?` | Resolve an address to a symbol name. Returns `null` if no symbol found. Equivalent to `api.Symbols.ResolveAddress(addr)`. |
| `Addr` | `(string name)` | `ulong` | Resolve a symbol name to an address. Format: `"module!function"`. Returns `0` if not found. Equivalent to `api.Symbols.ResolveNameToAddress(name)`. |

### Other Globals

| Name | Type | Description |
|------|------|-------------|
| `api` | `IDebuggerApi` | The full debugger API object. All sub-APIs are accessible through it. |
| `print` | `Action<string>` | Print text to the scripting output panel. Also sent to the Log tab with `[Script]` prefix. |

### Shortcut Implementation Details

`ReadString` and `ReadWString` both scan for null terminators in the read buffer. `ReadString` looks for a single `0x00` byte; `ReadWString` looks for two consecutive `0x00` bytes on a 2-byte boundary. If no terminator is found, the full buffer is returned as a string.

`ReadPtr` checks `api.Is32Bit` and reads either 4 or 8 bytes accordingly, returning a `ulong` in both cases.

**Examples:**

```csharp
// Read a string from memory
var s = ReadString(Reg("RCX"), 512);
print($"Arg1 = {s}");

// Read a wide string
var ws = ReadWString(ReadPtr(Reg("RDX")), 260);
print($"Path = {ws}");

// Check a DWORD value
var flags = ReadU32(Reg("RSP") + 0x28);
print($"Flags = 0x{flags:X08}");

// Write 4 bytes
WriteMem(Reg("RCX") + 0x10, BitConverter.GetBytes(42u));
```

---

## 3. REPL Behavior

### Variable Persistence

Variables defined in one execution persist in all subsequent executions within the same session. This is a key feature of the Roslyn scripting REPL.

```csharp
// Run 1:
var snapshot = ReadMem(Reg("RIP"), 0x100);

// Run 2 (later, after target runs and breaks again):
var current = ReadMem(Reg("RIP"), 0x100);
// 'snapshot' is still available here
```

### Reset State

Click the **Reset State** button in the Scripting panel to clear all persisted variables and the script state. The next execution will re-inject the preamble shortcuts.

### Return Values

The last expression in a script is automatically displayed as the return value. For `byte[]` arrays, the output is formatted as space-separated hex bytes. For strings, the output is wrapped in quotes.

```csharp
// This automatically prints the byte array as hex
ReadMem(Reg("RIP"), 16)
// Output: 48 89 5C 24 08 48 89 6C 24 10 ...

// This prints the string in quotes
Sym(Reg("RIP"))
// Output: "ntdll!LdrInitializeThunk+0x10"
```

### Console.WriteLine

All `Console.WriteLine` and `Console.Write` calls are captured and redirected to the scripting output panel.

```csharp
Console.WriteLine("Hello from script");
// Output appears in the scripting panel, same as print()
```

### Async Support

`await` is supported directly in the REPL. No need to wrap code in `async` methods.

```csharp
await Task.Delay(1000);
print("One second later");
```

### Error Handling

- **Compilation errors** are displayed with Roslyn diagnostics (line numbers, error codes)
- **Runtime exceptions** show the exception type and message
- Scripts that throw do not corrupt the REPL state; you can fix and re-run

### Selection Execution

Select any fragment of text in the editor and press **F5** to execute only the selection. This is useful for re-running individual statements without executing the entire script.

### Cross-Plugin Script Execution

Scripts can be executed programmatically from other plugins or MCP tools via `SetPluginData`:

```csharp
// Stored by the ScriptingPlugin at initialization:
// api.UI.SetPluginData("ScriptExecute", Func<string, Task<string>>)

// Another plugin can retrieve and invoke it:
var exec = (Func<string, Task<string>>)api.UI.GetPluginData("ScriptExecute");
string result = await exec("Reg(\"RIP\")");
```

---

## 4. Debugger State

These properties are available directly on the `api` object and provide information about the current debugger and target state.

### Properties

#### `api.IsConnected`
- **Type:** `bool`
- **Description:** Returns `true` if the debugger is connected to a target (kernel or user-mode process). Most API calls require this to be `true`.

#### `api.IsBreakState`
- **Type:** `bool`
- **Description:** Returns `true` if the target is currently suspended (break state). Memory reads, register access, and stepping commands require break state.

#### `api.TargetPid`
- **Type:** `uint`
- **Description:** The process ID of the currently debugged process. Used as the first parameter for most `Memory` and `Breakpoint` API calls. Value is `0` when no process is targeted.

#### `api.SelectedThreadId`
- **Type:** `uint`
- **Description:** The currently selected thread ID. This is the thread whose registers are displayed and whose context is used for stepping. Can be changed by selecting a different thread in the Threads panel.

#### `api.Is32Bit`
- **Type:** `bool`
- **Description:** Returns `true` if the target process is a 32-bit (WoW64) process on a 64-bit system. Affects pointer size in `ReadPtr` and register names available.

**Examples:**

```csharp
// Guard clause for scripts that need break state
if (!api.IsBreakState) {
    print("ERROR: Target must be paused. Press F12 first.");
    return;
}

// Print debugger status
print($"Connected: {api.IsConnected}");
print($"Break state: {api.IsBreakState}");
print($"PID: {api.TargetPid}");
print($"Thread: {api.SelectedThreadId}");
print($"32-bit: {api.Is32Bit}");
```

---

## 5. Memory API

All memory operations are accessed through `api.Memory`. These methods operate on the target process memory through the kernel driver.

### Methods

#### `ReadMemory`

```csharp
byte[]? ReadMemory(uint pid, ulong address, uint size)
```

| Parameter | Type | Description |
|-----------|------|-------------|
| `pid` | `uint` | Process ID of the target process. Use `api.TargetPid`. |
| `address` | `ulong` | Virtual address to read from. |
| `size` | `uint` | Number of bytes to read. |
| **Returns** | `byte[]?` | Byte array containing the read data, or `null` if the read failed (invalid address, unmapped page, etc.). |

#### `WriteMemory`

```csharp
bool WriteMemory(uint pid, ulong address, byte[] data)
```

| Parameter | Type | Description |
|-----------|------|-------------|
| `pid` | `uint` | Process ID of the target process. |
| `address` | `ulong` | Virtual address to write to. |
| `data` | `byte[]` | Bytes to write. |
| **Returns** | `bool` | `true` if the write succeeded, `false` otherwise. |

**Note:** Writing to read-only pages (e.g., `.text` section) works through the kernel driver without requiring `VirtualProtect`. The driver handles page protection bypass.

#### `ReadRegisters`

```csharp
IReadOnlyList<PluginRegister> ReadRegisters(uint pid, uint tid)
```

| Parameter | Type | Description |
|-----------|------|-------------|
| `pid` | `uint` | Process ID. |
| `tid` | `uint` | Thread ID whose registers to read. Use `api.SelectedThreadId` for the current thread. |
| **Returns** | `IReadOnlyList<PluginRegister>` | List of all registers (general-purpose, segment, flags). Each entry has `Name` (string), `Value` (ulong), and `IsFlag` (bool). |

Registers include: RAX, RBX, RCX, RDX, RSI, RDI, RBP, RSP, R8-R15, RIP, RFLAGS, CS, DS, ES, FS, GS, SS. Flag registers (CF, ZF, SF, OF, etc.) have `IsFlag = true`.

#### `WriteRip`

```csharp
bool WriteRip(uint pid, uint tid, ulong newRip)
```

| Parameter | Type | Description |
|-----------|------|-------------|
| `pid` | `uint` | Process ID. |
| `tid` | `uint` | Thread ID. |
| `newRip` | `ulong` | New instruction pointer value. |
| **Returns** | `bool` | `true` if RIP was changed successfully. |

**Warning:** Changing RIP to an invalid address will cause a crash when the target resumes.

#### `WriteRipAndRsp`

```csharp
bool WriteRipAndRsp(uint tid, ulong newRip, ulong newRsp)
```

| Parameter | Type | Description |
|-----------|------|-------------|
| `tid` | `uint` | Thread ID. |
| `newRip` | `ulong` | New instruction pointer value. |
| `newRsp` | `ulong` | New stack pointer value. |
| **Returns** | `bool` | `true` on success. |

**Note:** Unlike `WriteRip`, this method does not take a `pid` parameter -- it operates on the currently targeted process.

Use this to redirect execution to a custom code cave or allocated trampoline while preserving/changing the stack.

#### `ProtectMemory`

```csharp
(bool ok, uint oldProtection) ProtectMemory(uint pid, ulong address, uint size, uint newProtection)
```

| Parameter | Type | Description |
|-----------|------|-------------|
| `pid` | `uint` | Process ID. |
| `address` | `ulong` | Start address of the region (page-aligned internally). |
| `size` | `uint` | Size of the region in bytes. |
| `newProtection` | `uint` | New protection constant (PAGE_EXECUTE_READWRITE = 0x40, PAGE_READWRITE = 0x04, etc.). |
| **Returns** | `(bool ok, uint oldProtection)` | Tuple: success flag and the previous protection value. |

Common protection constants:
- `0x02` -- PAGE_READONLY
- `0x04` -- PAGE_READWRITE
- `0x10` -- PAGE_EXECUTE
- `0x20` -- PAGE_EXECUTE_READ
- `0x40` -- PAGE_EXECUTE_READWRITE

#### `AllocateMemory`

```csharp
ulong AllocateMemory(uint pid, ulong size)
```

| Parameter | Type | Description |
|-----------|------|-------------|
| `pid` | `uint` | Process ID. |
| `size` | `ulong` | Number of bytes to allocate. Rounded up to page size (4096). |
| **Returns** | `ulong` | Base address of the allocated region, or `0` on failure. Memory is allocated with PAGE_EXECUTE_READWRITE protection. |

#### `FreeMemory`

```csharp
bool FreeMemory(uint pid, ulong address)
```

| Parameter | Type | Description |
|-----------|------|-------------|
| `pid` | `uint` | Process ID. |
| `address` | `ulong` | Base address of a previously allocated region. |
| **Returns** | `bool` | `true` if the memory was freed. |

**Examples:**

```csharp
// Read the first page of the main module
var mod = api.Symbols.GetModules()[0];
var page = api.Memory.ReadMemory(api.TargetPid, mod.BaseAddress, 0x1000);
if (page != null)
    print($"MZ check: {(char)page[0]}{(char)page[1]}");

// Write a NOP sled (5 bytes)
api.Memory.WriteMemory(api.TargetPid, Reg("RIP"), new byte[] { 0x90, 0x90, 0x90, 0x90, 0x90 });

// Show all general-purpose registers
var regs = api.Memory.ReadRegisters(api.TargetPid, api.SelectedThreadId);
foreach (var r in regs.Where(r => !r.IsFlag))
    print($"{r.Name,-6} = 0x{r.Value:X016}");

// Allocate executable memory, write shellcode, redirect RIP
var cave = api.Memory.AllocateMemory(api.TargetPid, 0x1000);
print($"Code cave at 0x{cave:X}");
var code = new byte[] { 0xCC }; // INT3
api.Memory.WriteMemory(api.TargetPid, cave, code);
api.Memory.WriteRip(api.TargetPid, api.SelectedThreadId, cave);

// Change page protection
var (ok, oldProt) = api.Memory.ProtectMemory(api.TargetPid, 0x7FF600001000, 0x1000, 0x40);
print($"Changed protection: {ok}, old = 0x{oldProt:X}");

// Read a DWORD and a QWORD using the low-level API
var buf4 = api.Memory.ReadMemory(api.TargetPid, Reg("RSP"), 4);
uint dword = BitConverter.ToUInt32(buf4);
var buf8 = api.Memory.ReadMemory(api.TargetPid, Reg("RSP") + 8, 8);
ulong qword = BitConverter.ToUInt64(buf8);
print($"[RSP] = 0x{dword:X08}, [RSP+8] = 0x{qword:X016}");
```

---

## 6. Breakpoint API

All breakpoint operations are accessed through `api.Breakpoints`. KernelFlirt supports software breakpoints (INT3), hardware breakpoints (DR0-DR3), hardware watchpoints, and memory breakpoints (PAGE_GUARD).

### Methods

#### `SetBreakpoint`

```csharp
uint? SetBreakpoint(uint pid, uint tid, ulong address, PluginBreakpointType type, uint length = 1)
```

| Parameter | Type | Description |
|-----------|------|-------------|
| `pid` | `uint` | Process ID. |
| `tid` | `uint` | Thread ID. Pass `0` for all threads (software BPs are thread-agnostic; hardware BPs apply to the specified thread only). |
| `address` | `ulong` | Address where the breakpoint is set. |
| `type` | `PluginBreakpointType` | Type of breakpoint (see enum below). |
| `length` | `uint` | Length of the watched region for hardware watchpoints: 1, 2, 4, or 8 bytes. Ignored for software breakpoints. Default: `1`. |
| **Returns** | `uint?` | Breakpoint handle on success, or `null` if the breakpoint could not be set (e.g., all 4 HW debug registers in use). |

#### `RemoveBreakpoint`

```csharp
bool RemoveBreakpoint(uint handle)
```

| Parameter | Type | Description |
|-----------|------|-------------|
| `handle` | `uint` | Handle returned by `SetBreakpoint`. |
| **Returns** | `bool` | `true` if the breakpoint was removed. |

#### `GetAll`

```csharp
IReadOnlyList<PluginBreakpoint> GetAll()
```

| **Returns** | `IReadOnlyList<PluginBreakpoint>` | List of all currently set breakpoints, including those set by the UI. |

#### `ToggleBreakpoint`

```csharp
void ToggleBreakpoint(ulong address, PluginBreakpointType type = PluginBreakpointType.Software)
```

| Parameter | Type | Description |
|-----------|------|-------------|
| `address` | `ulong` | Address to toggle. |
| `type` | `PluginBreakpointType` | Breakpoint type. Default: `Software`. |

If a breakpoint exists at `address`, it is removed. Otherwise, a new breakpoint is added. This method updates the UI (breakpoint list, disassembly markers) and the driver simultaneously.

### PluginBreakpointType Enum

| Value | Name | Description |
|-------|------|-------------|
| `0` | `Software` | INT3 software breakpoint. Replaces the first byte of the instruction with `0xCC`. Unlimited count. |
| `1` | `Hardware` | Hardware execution breakpoint. Uses DR0-DR3. Maximum 4 per thread. |
| `2` | `HwWrite` | Hardware write watchpoint. Triggers when data at the address is written. Uses DR0-DR3. |
| `3` | `HwReadWrite` | Hardware read/write watchpoint. Triggers on any access (read or write). Uses DR0-DR3. |
| `4` | `Memory` | Memory breakpoint using PAGE_GUARD. Triggers on any access to the page. Can watch large regions. |

**Examples:**

```csharp
// Set a software breakpoint at a function
var addr = Addr("kernel32!CreateFileW");
var handle = api.Breakpoints.SetBreakpoint(api.TargetPid, 0, addr, PluginBreakpointType.Software);
print($"BP handle: {handle}");

// Set a hardware write watchpoint on a 4-byte variable
var globalVar = Addr("myapp!g_counter");
var hwHandle = api.Breakpoints.SetBreakpoint(
    api.TargetPid, api.SelectedThreadId,
    globalVar, PluginBreakpointType.HwWrite, 4);

// List all breakpoints
foreach (var bp in api.Breakpoints.GetAll())
    print($"BP #{bp.Handle}: 0x{bp.Address:X} type={bp.Type} hits={bp.HitCount} enabled={bp.Enabled}");

// Remove a breakpoint
api.Breakpoints.RemoveBreakpoint(handle.Value);

// Toggle BP at current RIP (sets if missing, removes if present)
api.Breakpoints.ToggleBreakpoint(Reg("RIP"));

// Set a hardware execution BP (stealthier than INT3, not visible in memory)
var stealthBp = api.Breakpoints.SetBreakpoint(
    api.TargetPid, api.SelectedThreadId,
    Addr("ntdll!NtQueryInformationProcess"),
    PluginBreakpointType.Hardware);

// Memory breakpoint on an entire page (for data tracking)
var memBp = api.Breakpoints.SetBreakpoint(
    api.TargetPid, 0,
    0x7FF600040000, PluginBreakpointType.Memory);
```

---

## 7. Symbol API

All symbol operations are accessed through `api.Symbols`. This API provides symbol resolution, module enumeration, and user-defined function naming.

### Methods

#### `ResolveAddress`

```csharp
string? ResolveAddress(ulong address)
```

| Parameter | Type | Description |
|-----------|------|-------------|
| `address` | `ulong` | Virtual address to resolve. |
| **Returns** | `string?` | Symbol name in `module!function+offset` format, or `null` if no symbol covers this address. If a user-registered function (via `RegisterFunction`) covers the address, that name is returned instead. |

#### `ResolveNameToAddress`

```csharp
ulong ResolveNameToAddress(string name)
```

| Parameter | Type | Description |
|-----------|------|-------------|
| `name` | `string` | Symbol name in `module!function` format (e.g., `"kernel32!CreateFileW"`, `"ntdll!NtQuerySystemInformation"`). |
| **Returns** | `ulong` | Virtual address of the symbol, or `0` if not found. |

#### `GetModules`

```csharp
IReadOnlyList<PluginModuleInfo> GetModules()
```

| **Returns** | `IReadOnlyList<PluginModuleInfo>` | List of user-mode modules loaded in the target process. Each entry has `BaseAddress`, `Size`, and `Name`. |

#### `GetKernelModules`

```csharp
IReadOnlyList<PluginKernelModuleInfo> GetKernelModules()
```

| **Returns** | `IReadOnlyList<PluginKernelModuleInfo>` | List of kernel-mode modules (drivers). Each entry has `BaseAddress`, `Size`, `LoadOrder`, and `Name`. |

#### `RegisterFunction`

```csharp
void RegisterFunction(ulong address, string? name, uint size = 0)
```

| Parameter | Type | Description |
|-----------|------|-------------|
| `address` | `ulong` | Start address of the function. |
| `name` | `string?` | Name to assign. Pass `null` to unregister a previously registered name. |
| `size` | `uint` | Size of the function in bytes. When non-zero, `ResolveAddress` returns `name+0xNN` for any address within the range `[address, address + size)`. **Always specify size when known** -- it enables offset-based resolution within the function body. Default: `0`. |

**Important:** `RegisterFunction` must ALWAYS be called with the `size` parameter for meaningful results. Without `size`, only the exact start address resolves to the name. With `size`, all addresses within the function body resolve to `name+0xOffset`.

#### `GetRegisteredFunctions`

```csharp
IReadOnlyList<PluginFunctionEntry> GetRegisteredFunctions()
```

| **Returns** | `IReadOnlyList<PluginFunctionEntry>` | List of all user-defined functions registered via `RegisterFunction`. Each entry has `Address`, `Name`, and `Size`. |

**Examples:**

```csharp
// Resolve current RIP to a symbol
var sym = api.Symbols.ResolveAddress(Reg("RIP"));
print($"Current location: {sym ?? "unknown"}");

// Find an API address
var addr = api.Symbols.ResolveNameToAddress("ntdll!NtCreateFile");
print($"NtCreateFile = 0x{addr:X}");

// List all user-mode modules with sizes
foreach (var m in api.Symbols.GetModules())
    print($"0x{m.BaseAddress:X016}  {m.Size,10:X}  {m.Name}");

// List kernel modules
foreach (var km in api.Symbols.GetKernelModules())
    print($"#{km.LoadOrder,3}  0x{km.BaseAddress:X016}  {km.Size,8:X}  {km.Name}");

// Register a function name with size
api.Symbols.RegisterFunction(0x7FF600001000, "DecryptConfig", 0x150);
// Now Sym(0x7FF600001020) returns "DecryptConfig+0x20"

// Unregister a function
api.Symbols.RegisterFunction(0x7FF600001000, null, 0);

// List all registered functions
foreach (var f in api.Symbols.GetRegisteredFunctions())
    print($"0x{f.Address:X}  size=0x{f.Size:X}  {f.Name}");
```

---

## 8. Process API

All process and thread operations are accessed through `api.Process`. This API provides process/thread enumeration, thread control, PEB access, and anti-debug bypass features.

### Methods

#### `EnumProcesses`

```csharp
IReadOnlyList<PluginProcessInfo> EnumProcesses()
```

| **Returns** | `IReadOnlyList<PluginProcessInfo>` | List of all processes visible from the kernel. Each entry has `ProcessId`, `SessionId`, and `Name`. |

#### `EnumThreads`

```csharp
IReadOnlyList<PluginThreadInfo> EnumThreads(uint pid)
```

| Parameter | Type | Description |
|-----------|------|-------------|
| `pid` | `uint` | Process ID to enumerate threads for. |
| **Returns** | `IReadOnlyList<PluginThreadInfo>` | List of threads. Each entry has `ThreadId`, `StartAddress`, `State`, and `Priority`. |

#### `SuspendThread`

```csharp
bool SuspendThread(uint tid)
```

| Parameter | Type | Description |
|-----------|------|-------------|
| `tid` | `uint` | Thread ID to suspend. |
| **Returns** | `bool` | `true` on success. |

#### `ResumeThread`

```csharp
bool ResumeThread(uint tid)
```

| Parameter | Type | Description |
|-----------|------|-------------|
| `tid` | `uint` | Thread ID to resume. |
| **Returns** | `bool` | `true` on success. |

#### `GetPebAddress`

```csharp
(ulong PebAddress, ulong Peb32Address) GetPebAddress(uint pid)
```

| Parameter | Type | Description |
|-----------|------|-------------|
| `pid` | `uint` | Process ID. |
| **Returns** | `(ulong PebAddress, ulong Peb32Address)` | Tuple with the 64-bit PEB address and the 32-bit PEB address (for WoW64 processes). `Peb32Address` is `0` for native 64-bit processes. |

#### `ClearDebugPort`

```csharp
bool ClearDebugPort(uint pid)
```

| Parameter | Type | Description |
|-----------|------|-------------|
| `pid` | `uint` | Process ID. |
| **Returns** | `bool` | `true` if the debug port was cleared. |

Zeroes the `DebugPort` field in the EPROCESS structure. This hides the debugger from `NtQueryInformationProcess(ProcessDebugPort)` and `CheckRemoteDebuggerPresent` checks.

#### `ClearThreadHide`

```csharp
bool ClearThreadHide(uint pid)
```

| Parameter | Type | Description |
|-----------|------|-------------|
| `pid` | `uint` | Process ID. |
| **Returns** | `bool` | `true` on success. |

Clears the `ThreadHideFromDebugger` flag on all threads in the process. This counteracts `NtSetInformationThread(ThreadHideFromDebugger)` which is commonly used by packers and anti-debug code.

#### `InstallNtQsiHook`

```csharp
bool InstallNtQsiHook()
```

| **Returns** | `bool` | `true` if the hook was installed. |

Installs a kernel-mode hook on `NtQuerySystemInformation` that filters out the debugged process from the `SystemProcessInformation` class results. This hides the process from Task Manager, Process Explorer, and `EnumProcesses` calls made by anti-debug code.

#### `RemoveNtQsiHook`

```csharp
bool RemoveNtQsiHook()
```

| **Returns** | `bool` | `true` if the hook was removed. |

#### `ProbeNtQsiHook`

```csharp
string ProbeNtQsiHook()
```

| **Returns** | `string` | Diagnostic string describing the current hook state, target PID being filtered, and hook integrity status. |

#### `SetSpoofSharedUserData`

```csharp
bool SetSpoofSharedUserData(bool enable)
```

| Parameter | Type | Description |
|-----------|------|-------------|
| `enable` | `bool` | `true` to enable spoofing, `false` to disable. |
| **Returns** | `bool` | `true` on success. |

When enabled, patches the `KUSER_SHARED_DATA.KdDebuggerEnabled` byte to `0` so user-mode anti-debug code reading `SharedUserData->KdDebuggerEnabled` sees no kernel debugger attached.

**Examples:**

```csharp
// List all processes
foreach (var p in api.Process.EnumProcesses())
    print($"PID {p.ProcessId,6}  Session {p.SessionId}  {p.Name}");

// List threads of the current process
foreach (var t in api.Process.EnumThreads(api.TargetPid))
    print($"TID {t.ThreadId,6}  Start=0x{t.StartAddress:X}  State={t.State}  Pri={t.Priority}");

// Suspend a specific thread
api.Process.SuspendThread(1234);

// Get PEB address
var (peb, peb32) = api.Process.GetPebAddress(api.TargetPid);
print($"PEB = 0x{peb:X}, PEB32 = 0x{peb32:X}");

// Read ImageBaseAddress from PEB (+0x10)
var imageBase = ReadPtr(peb + 0x10);
print($"ImageBase = 0x{imageBase:X}");

// Full anti-debug bypass
api.Process.ClearDebugPort(api.TargetPid);
api.Process.ClearThreadHide(api.TargetPid);
api.Process.InstallNtQsiHook();
api.Process.SetSpoofSharedUserData(true);
print("Anti-debug bypass active");
```

---

## 9. UI API

All user interface operations are accessed through `api.UI`. This API controls the disassembly view, annotations, decompilation, module management, plugin data storage, and note events.

### Methods

#### `NavigateDisassembly`

```csharp
void NavigateDisassembly(ulong address)
```

| Parameter | Type | Description |
|-----------|------|-------------|
| `address` | `ulong` | Address to navigate to in the disassembly view. The view scrolls to show the instruction at this address. |

#### `DisasmGoBack`

```csharp
void DisasmGoBack()
```

Navigates back to the previous disassembly location. Works like a browser back button -- each `NavigateDisassembly` call pushes to a history stack.

#### `AddMenuItem`

```csharp
void AddMenuItem(string header, Action callback)
```

| Parameter | Type | Description |
|-----------|------|-------------|
| `header` | `string` | Menu item text displayed in the Plugins menu. |
| `callback` | `Action` | Action to execute when the menu item is clicked. |

#### `AddToolPanel`

```csharp
void AddToolPanel(string title, object wpfContent)
```

| Parameter | Type | Description |
|-----------|------|-------------|
| `title` | `string` | Title for the tool panel tab. |
| `wpfContent` | `object` | A WPF `UIElement` (e.g., `UserControl`, `StackPanel`) to display as the panel content. |

#### `AddUnpackedModule`

```csharp
void AddUnpackedModule(ulong peBase, string name)
```

| Parameter | Type | Description |
|-----------|------|-------------|
| `peBase` | `ulong` | Base address where the unpacked PE image resides in memory. |
| `name` | `string` | Display name for the module (e.g., `"unpacked_payload"`). |

Adds a dynamically unpacked PE image as a virtual module. The PE header at `peBase` is parsed for sections, imports, exports, and strings. All views (Modules, Sections, Imports, Strings, Functions) are refreshed.

#### `RefreshModulesAndSections`

```csharp
void RefreshModulesAndSections()
```

Forces a refresh of the module list and sections tab. Call this after manual modifications to module data.

#### `AddModuleSections`

```csharp
void AddModuleSections(string moduleName, IReadOnlyList<PluginSectionInfo> sections)
```

| Parameter | Type | Description |
|-----------|------|-------------|
| `moduleName` | `string` | Name of the module these sections belong to. |
| `sections` | `IReadOnlyList<PluginSectionInfo>` | List of section entries. |

Provides section information directly, bypassing PE header parsing. Use this when the PE header has been zeroed or corrupted by a packer (anti-dump technique).

#### `DecompileFunction`

```csharp
void DecompileFunction(ulong address)
```

| Parameter | Type | Description |
|-----------|------|-------------|
| `address` | `ulong` | Address within the function to decompile. |

Starts asynchronous decompilation using RetDec. The result is not immediately available -- call `GetDecompiledCode()` after a short delay.

#### `GetDecompiledCode`

```csharp
string GetDecompiledCode()
```

| **Returns** | `string` | The C pseudocode output from the most recent decompilation, or empty string if no decompilation has been performed. |

#### `SetAddressAnnotation`

```csharp
void SetAddressAnnotation(ulong address, string? annotation)
```

| Parameter | Type | Description |
|-----------|------|-------------|
| `address` | `ulong` | Address to annotate. |
| `annotation` | `string?` | Annotation text. Shown as `"; comment"` in disassembly. Pass `null` or empty string to remove the annotation. |

#### `GetAddressAnnotation`

```csharp
string? GetAddressAnnotation(ulong address)
```

| Parameter | Type | Description |
|-----------|------|-------------|
| `address` | `ulong` | Address to query. |
| **Returns** | `string?` | The annotation text, or `null` if no annotation exists. |

#### `GetAllAnnotations`

```csharp
IReadOnlyDictionary<ulong, string> GetAllAnnotations()
```

| **Returns** | `IReadOnlyDictionary<ulong, string>` | Dictionary mapping addresses to their annotation strings. |

#### `RefreshDisassembly`

```csharp
void RefreshDisassembly()
```

Redraws the disassembly view to reflect changes in annotations, function names, or memory patches. Call this after batch annotation or patching operations.

#### `SetPluginData`

```csharp
void SetPluginData(string key, object? value)
```

| Parameter | Type | Description |
|-----------|------|-------------|
| `key` | `string` | Storage key. |
| `value` | `object?` | Value to store. Pass `null` to remove the key. |

General-purpose key-value storage shared across all plugins. Use for cross-plugin communication.

#### `GetPluginData`

```csharp
object? GetPluginData(string key)
```

| Parameter | Type | Description |
|-----------|------|-------------|
| `key` | `string` | Storage key. |
| **Returns** | `object?` | The stored value, or `null` if the key does not exist. |

### Events

#### `OnNoteAdded`

```csharp
event Action<ulong, string>? OnNoteAdded
```

Fires when the user adds a note via the disassembly context menu. Parameters: `(address, noteText)`.

#### `OnNoteEdited`

```csharp
event Action<ulong, string>? OnNoteEdited
```

Fires when the user edits an existing note. Parameters: `(address, newNoteText)`.

#### `OnNoteRemoved`

```csharp
event Action<ulong>? OnNoteRemoved
```

Fires when the user removes a note. Parameter: `(address)`.

**Examples:**

```csharp
// Navigate to a function and annotate it
var target = Addr("kernel32!CreateFileW");
api.UI.NavigateDisassembly(target);
api.UI.SetAddressAnnotation(target, "File creation API - check path in RCX");
api.UI.RefreshDisassembly();

// Go back to where we were
api.UI.DisasmGoBack();

// Decompile a function and print the result
api.UI.DecompileFunction(Reg("RIP"));
await Task.Delay(3000); // wait for RetDec
var code = api.UI.GetDecompiledCode();
print(code);

// Export all annotations to a file
var annotations = api.UI.GetAllAnnotations();
var sb = new StringBuilder();
foreach (var (addr, text) in annotations)
    sb.AppendLine($"0x{addr:X016} ; {text}");
File.WriteAllText(@"C:\Temp\annotations.txt", sb.ToString());
print($"Exported {annotations.Count} annotations");

// Add sections for a module with zeroed PE header
var sections = new List<PluginSectionInfo> {
    new() { Name = ".text",  VirtualAddress = 0x1000, VirtualSize = 0x5000,  Characteristics = 0x60000020 },
    new() { Name = ".rdata", VirtualAddress = 0x6000, VirtualSize = 0x2000,  Characteristics = 0x40000040 },
    new() { Name = ".data",  VirtualAddress = 0x8000, VirtualSize = 0x1000,  Characteristics = 0xC0000040 },
};
api.UI.AddModuleSections("packed_payload", sections);

// Cross-plugin data sharing
api.UI.SetPluginData("oep_address", 0x7FF600001234UL);
// In another script/plugin:
var oep = (ulong)api.UI.GetPluginData("oep_address");
print($"OEP from unpacker: 0x{oep:X}");

// Listen for note events
api.UI.OnNoteAdded += (addr, text) => print($"Note added at 0x{addr:X}: {text}");
api.UI.OnNoteRemoved += (addr) => print($"Note removed at 0x{addr:X}");
```

---

## 10. Log API

The Log API is accessed through `api.Log`. Messages appear in the Log tab of the KernelFlirt UI with appropriate color coding.

### Methods

#### `Info`

```csharp
void Info(string message)
```

| Parameter | Type | Description |
|-----------|------|-------------|
| `message` | `string` | Informational message text. Displayed in the default log color. |

#### `Warning`

```csharp
void Warning(string message)
```

| Parameter | Type | Description |
|-----------|------|-------------|
| `message` | `string` | Warning message text. Displayed in yellow/orange. |

#### `Error`

```csharp
void Error(string message)
```

| Parameter | Type | Description |
|-----------|------|-------------|
| `message` | `string` | Error message text. Displayed in red. |

**Difference between `print()` and `api.Log.Info()`:** `print()` writes to the scripting output panel. `api.Log.Info()` writes to the main Log tab. The ScriptingPlugin internally sends `print()` output to `api.Log.Info()` with a `[Script]` prefix, so both outputs appear in the Log tab -- but `print()` also appears in the script output pane.

**Examples:**

```csharp
// Structured logging during analysis
api.Log.Info("[Analyzer] Starting IAT analysis...");
api.Log.Warning("[Analyzer] Module has no .reloc section -- may be packed");
api.Log.Error("[Analyzer] Failed to read PE header at 0x7FF600000000");

// Progress reporting
int total = api.Symbols.GetModules().Count;
for (int i = 0; i < total; i++) {
    var m = api.Symbols.GetModules()[i];
    api.Log.Info($"[{i+1}/{total}] Scanning {m.Name}...");
}
```

---

## 11. Execution Control

Execution control methods are on the `api` object directly. These correspond to the toolbar buttons and keyboard shortcuts in the UI.

### Methods

#### `Continue`

```csharp
void Continue()
```

Resumes process execution. Equivalent to pressing **F5** / **F9** (Run). Often called from within an `OnDebugEventFilter` handler to auto-continue past a breakpoint.

#### `SingleStep`

```csharp
void SingleStep()
```

Executes one instruction and breaks. Follows into `CALL` instructions (Step Into). Equivalent to **F7**.

#### `StepOver`

```csharp
void StepOver()
```

Steps over the current instruction. For `CALL` instructions, sets a temporary breakpoint at the next instruction and runs. For other instructions, same as `SingleStep`. Equivalent to **F8**.

#### `StepOut`

```csharp
void StepOut()
```

Steps out of the current function. Reads the return address from `[RSP]`, sets a temporary breakpoint there, and resumes. Equivalent to **Ctrl+F9**.

#### `RunToCursor`

```csharp
void RunToCursor(ulong address)
```

| Parameter | Type | Description |
|-----------|------|-------------|
| `address` | `ulong` | Address to run to. |

Sets a temporary breakpoint at `address` and resumes execution. The breakpoint is automatically removed when hit. Equivalent to **F4** (Run to Cursor).

#### `SkipInstruction`

```csharp
void SkipInstruction()
```

Advances RIP past the current instruction without executing it. Equivalent to **Ctrl+F8**. Useful for skipping anti-debug checks, `INT3` traps, or problematic instructions.

#### `Pause`

```csharp
void Pause()
```

Pauses a running process. Equivalent to **F12** (Break). Suspends all threads.

**Examples:**

```csharp
// Skip over an anti-debug INT2D
if (ReadMem(Reg("RIP"), 1)?[0] == 0xCD) {
    var nextByte = ReadMem(Reg("RIP") + 1, 1)?[0];
    if (nextByte == 0x2D) {
        print("Skipping INT 2D anti-debug trap");
        api.SkipInstruction();
    }
}

// Run to a specific address
var target = Addr("myapp!WinMain");
api.RunToCursor(target);

// Single-step 10 instructions and log each RIP
for (int i = 0; i < 10; i++) {
    api.SingleStep();
    // Note: in practice, you need to wait for the break event
    // This is shown conceptually; see Events section for proper async stepping
}
```

---

## 12. Events

KernelFlirt provides a rich event system for reacting to debugger state changes and debug exceptions. Events are on the `api` object.

### Event Delegates

#### `OnDebugEvent`

```csharp
event Action<PluginDebugEvent>? OnDebugEvent
```

Fires whenever a debug event occurs (breakpoint hit, single step complete, access violation, etc.). This is a notification-only event -- the event has already been processed by the debugger. Use `OnDebugEventFilter` to intercept events before processing.

#### `OnDebugEventFilter`

```csharp
event Func<PluginDebugEvent, bool>? OnDebugEventFilter
```

**The most powerful event handler.** Called BEFORE the UI processes the debug event. The handler function receives a `PluginDebugEvent` and returns a `bool`:

- Return `true` -- suppress the UI break. The plugin takes ownership. You must call `api.Continue()`, `api.SingleStep()`, or set `evt.ContinueMode` to resume.
- Return `false` -- let the UI handle the event normally (break, update views, etc.).

#### `OnConnected`

```csharp
event Action? OnConnected
```

Fires when the debugger connects to a target.

#### `OnDisconnected`

```csharp
event Action? OnDisconnected
```

Fires when the debugger disconnects from the target.

#### `OnBreakStateEntered`

```csharp
event Action? OnBreakStateEntered
```

Fires when the target enters break state (paused). Useful for updating UI panels or performing automatic analysis when the target stops.

#### `OnBreakStateExited`

```csharp
event Action? OnBreakStateExited
```

Fires when the target exits break state (resumes running).

#### `OnBeforeRun`

```csharp
event Action? OnBeforeRun
```

Fires just before the process resumes (before `Continue`, `SingleStep`, etc.). Plugins can set or adjust breakpoints in this handler.

### PluginDebugEvent Fields for Event Control

When handling events in `OnDebugEventFilter`, you can modify the `PluginDebugEvent` object to control how the debugger resumes:

#### `ContinueMode`

| Value | Name | Description |
|-------|------|-------------|
| `0` | Run | Default. Resume execution normally. |
| `1` | StepPast | Step past the current instruction, then run. |
| `2` | StepInto | Single-step one instruction. |
| `3` | Handled | Mark exception as handled (suppress AV) and single-step. |
| `4` | Trace | Kernel-mode trace: step internally while RIP is in `[TraceRangeBase, TraceRangeEnd)`. Reports back when RIP exits range or `TraceMaxSteps` is reached. |

#### `NewRip`

Set to non-zero to redirect execution to a different address before resuming. Applied via the kernel context record.

#### `NewRsp`

Set to non-zero to change the stack pointer before resuming.

#### `TraceRangeBase` / `TraceRangeEnd` / `TraceMaxSteps`

Used with `ContinueMode = 4` (Trace). The kernel driver steps internally while RIP stays within the range. Only reports back when RIP exits the range or the step count exceeds `TraceMaxSteps`.

**Examples:**

```csharp
// Logging breakpoint: log arguments and auto-continue
var createFile = Addr("kernel32!CreateFileW");
api.Breakpoints.SetBreakpoint(api.TargetPid, 0, createFile, PluginBreakpointType.Software);

api.OnDebugEventFilter += (evt) => {
    if (evt.Address != createFile) return false;
    
    var pathPtr = Reg("RCX");
    var path = ReadWString(pathPtr, 260);
    var access = Reg("RDX");
    print($"CreateFileW(\"{path}\", access=0x{access:X})");
    
    api.Continue();
    return true; // suppress UI break
};

// Conditional breakpoint: break only on specific condition
var targetAddr = Addr("myapp!ProcessPacket");
api.Breakpoints.SetBreakpoint(api.TargetPid, 0, targetAddr, PluginBreakpointType.Software);

api.OnDebugEventFilter += (evt) => {
    if (evt.Address != targetAddr) return false;
    
    var packetType = ReadU32(Reg("RCX"));
    if (packetType != 0x42) {
        api.Continue();
        return true; // not the packet we want, skip
    }
    
    print($"Caught packet type 0x42!");
    return false; // break in UI
};

// Redirect execution: skip a function entirely and set return value
var antiDebugFunc = Addr("myapp!IsDebuggerDetected");
api.Breakpoints.SetBreakpoint(api.TargetPid, 0, antiDebugFunc, PluginBreakpointType.Software);

api.OnDebugEventFilter += (evt) => {
    if (evt.Address != antiDebugFunc) return false;
    
    // Read return address from stack
    var retAddr = ReadPtr(Reg("RSP"));
    // Set RAX = 0 (not detected), redirect to return address
    evt.NewRip = retAddr;
    evt.NewRsp = Reg("RSP") + 8; // pop return address
    evt.ContinueMode = 0; // Run
    
    print("Bypassed IsDebuggerDetected -> returning 0");
    api.Continue();
    return true;
};

// State change monitoring
api.OnBreakStateEntered += () => {
    var rip = Reg("RIP");
    var sym = Sym(rip);
    api.Log.Info($"Stopped at 0x{rip:X} ({sym ?? "???"})");
};

api.OnBeforeRun += () => {
    api.Log.Info("Target is about to resume...");
};

// Kernel-mode trace: trace through a function range
api.OnDebugEventFilter += (evt) => {
    if (evt.Address != targetAddr) return false;
    
    evt.ContinueMode = 4; // Trace
    evt.TraceRangeBase = targetAddr;
    evt.TraceRangeEnd = targetAddr + 0x200;
    evt.TraceMaxSteps = 5000;
    return true;
};
```

---

## 13. Data Models

This section documents all data model classes in the `KernelFlirt.SDK` namespace.

### PluginRegister

Represents a single CPU register.

| Field | Type | Description |
|-------|------|-------------|
| `Name` | `string` | Register name (e.g., `"RAX"`, `"RIP"`, `"CF"`). |
| `Value` | `ulong` | Current value of the register. |
| `IsFlag` | `bool` | `true` if this is a CPU flag (CF, ZF, SF, OF, PF, AF, DF, IF, TF). `false` for general-purpose, segment, and control registers. |

```csharp
var regs = api.Memory.ReadRegisters(api.TargetPid, api.SelectedThreadId);
// General-purpose registers
foreach (var r in regs.Where(r => !r.IsFlag))
    print($"{r.Name,-6} = 0x{r.Value:X016}");
// Flags only
foreach (var r in regs.Where(r => r.IsFlag && r.Value != 0))
    print($"{r.Name} = {r.Value}");
```

### PluginModuleInfo

Represents a user-mode module (DLL/EXE) loaded in the target process.

| Field | Type | Description |
|-------|------|-------------|
| `BaseAddress` | `ulong` | Virtual address where the module is loaded. |
| `Size` | `uint` | Total size of the module in memory (sum of all sections, page-aligned). |
| `Name` | `string` | Module filename (e.g., `"ntdll.dll"`, `"myapp.exe"`). |

```csharp
var mods = api.Symbols.GetModules();
var main = mods.First(m => m.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
print($"Main module: {main.Name} at 0x{main.BaseAddress:X}, size 0x{main.Size:X}");
```

### PluginKernelModuleInfo

Represents a kernel-mode module (driver, ntoskrnl, HAL, etc.).

| Field | Type | Description |
|-------|------|-------------|
| `BaseAddress` | `ulong` | Kernel virtual address where the module is loaded. |
| `Size` | `uint` | Total size of the module in memory. |
| `LoadOrder` | `ushort` | Load order index (0 = ntoskrnl, 1 = HAL, etc.). |
| `Name` | `string` | Module filename (e.g., `"ntoskrnl.exe"`, `"CI.dll"`). |

```csharp
var kmods = api.Symbols.GetKernelModules();
var ntos = kmods.First(m => m.Name.Contains("ntoskrnl"));
print($"ntoskrnl at 0x{ntos.BaseAddress:X}, order #{ntos.LoadOrder}");
```

### PluginProcessInfo

Represents a running process.

| Field | Type | Description |
|-------|------|-------------|
| `ProcessId` | `uint` | Process ID (PID). |
| `SessionId` | `uint` | Session ID (0 = services, 1+ = interactive sessions). |
| `Name` | `string` | Process image name (e.g., `"explorer.exe"`). |

```csharp
var procs = api.Process.EnumProcesses();
foreach (var p in procs.Where(p => p.SessionId == 1))
    print($"PID {p.ProcessId,6}  {p.Name}");
```

### PluginThreadInfo

Represents a thread within a process.

| Field | Type | Description |
|-------|------|-------------|
| `ThreadId` | `uint` | Thread ID (TID). |
| `StartAddress` | `ulong` | Thread start address (the function passed to `CreateThread`). |
| `State` | `uint` | Thread state: 0=Initialized, 1=Ready, 2=Running, 3=Standby, 4=Terminated, 5=Waiting, 6=Transition, 7=DeferredReady. |
| `Priority` | `uint` | Thread priority value. |

```csharp
var threads = api.Process.EnumThreads(api.TargetPid);
foreach (var t in threads) {
    var sym = Sym(t.StartAddress) ?? "???";
    string state = t.State switch {
        2 => "RUNNING", 5 => "WAITING", 4 => "TERMINATED", _ => $"state={t.State}"
    };
    print($"TID {t.ThreadId,6}  {state,-12}  {sym}");
}
```

### PluginBreakpoint

Represents a breakpoint set in the debugger.

| Field | Type | Description |
|-------|------|-------------|
| `Handle` | `uint` | Unique handle for this breakpoint. Used with `RemoveBreakpoint`. |
| `Address` | `ulong` | Virtual address where the breakpoint is set. |
| `Type` | `PluginBreakpointType` | Breakpoint type (Software, Hardware, HwWrite, HwReadWrite, Memory). |
| `Enabled` | `bool` | Whether the breakpoint is currently active. |
| `Condition` | `string?` | Optional condition expression (for future use). |
| `HitCount` | `uint` | Number of times this breakpoint has been hit since it was set. |
| `OriginalByte` | `byte` | For software breakpoints: the original byte that was replaced by `0xCC` (INT3). |

```csharp
foreach (var bp in api.Breakpoints.GetAll()) {
    print($"#{bp.Handle} 0x{bp.Address:X} {bp.Type} hits={bp.HitCount} " +
          $"enabled={bp.Enabled} origByte=0x{bp.OriginalByte:X02}");
}
```

### PluginBreakpointType

Enum defining breakpoint types.

| Value | Name | Description |
|-------|------|-------------|
| `0` | `Software` | INT3 software breakpoint (`0xCC`). Unlimited count. Visible in memory if not hidden. |
| `1` | `Hardware` | Hardware execution breakpoint via debug registers (DR0-DR3). Max 4 per thread. Invisible to memory reads. |
| `2` | `HwWrite` | Hardware write watchpoint. Triggers on data write to the watched address. |
| `3` | `HwReadWrite` | Hardware read/write watchpoint. Triggers on any data access (read or write). |
| `4` | `Memory` | Memory breakpoint via PAGE_GUARD. Triggers access violation on first access. |

### PluginDebugEvent

Represents a debug event (breakpoint hit, exception, etc.). Passed to `OnDebugEvent` and `OnDebugEventFilter` handlers.

| Field | Type | Description |
|-------|------|-------------|
| `Type` | `PluginDebugEventType` | Event type (see enum below). |
| `ProcessId` | `uint` | PID of the process that triggered the event. |
| `ThreadId` | `uint` | TID of the thread that triggered the event. |
| `Address` | `ulong` | Address where the event occurred (e.g., breakpoint address, faulting instruction). |
| `IsKernelMode` | `bool` | `true` if the event occurred in kernel mode. |
| `ExceptionCode` | `uint` | Windows exception code (e.g., `0x80000003` for STATUS_BREAKPOINT, `0xC0000005` for STATUS_ACCESS_VIOLATION). |
| `FaultAddress` | `ulong` | For access violations: the address that was accessed. |
| `AccessType` | `uint` | For access violations: `0` = read, `1` = write, `8` = execute. |
| `ContinueMode` | `uint` | **Writable.** Set by the plugin to control resumption. `0`=Run, `1`=StepPast, `2`=StepInto, `3`=Handled, `4`=Trace. |
| `NewRip` | `ulong` | **Writable.** Set to non-zero to redirect RIP before resuming. |
| `NewRsp` | `ulong` | **Writable.** Set to non-zero to change RSP before resuming. |
| `TraceRangeBase` | `ulong` | **Writable.** Start of trace range (for `ContinueMode=4`). |
| `TraceRangeEnd` | `ulong` | **Writable.** End of trace range (for `ContinueMode=4`). |
| `TraceMaxSteps` | `uint` | **Writable.** Maximum number of trace steps (for `ContinueMode=4`). |

### PluginDebugEventType

Enum defining debug event types.

| Value | Name | Description |
|-------|------|-------------|
| `1` | `Breakpoint` | Software breakpoint (INT3) was hit. |
| `2` | `SingleStep` | Single-step completed (trace flag was set). |
| `3` | `HwBreakpoint` | Hardware execution breakpoint (DR0-DR3) triggered. |
| `4` | `HwWatchpoint` | Hardware data watchpoint (write or read/write) triggered. |
| `5` | `MemoryBp` | Memory breakpoint (PAGE_GUARD) triggered. |
| `6` | `AccessViolation` | Access violation exception. Check `FaultAddress` and `AccessType`. |

### PluginSectionInfo

Represents a PE section. Used with `AddModuleSections`.

| Field | Type | Description |
|-------|------|-------------|
| `Name` | `string` | Section name (e.g., `".text"`, `".rdata"`, `".data"`). Max 8 characters per PE spec. |
| `VirtualAddress` | `ulong` | RVA (Relative Virtual Address) of the section start. |
| `VirtualSize` | `uint` | Size of the section in memory (before page alignment). |
| `Characteristics` | `uint` | Section flags. Bitfield: `0x20`=code, `0x40`=initialized data, `0x80`=uninitialized data, `0x20000000`=executable, `0x40000000`=readable, `0x80000000`=writable. |

Common `Characteristics` combinations:
- `0x60000020` -- `.text` (code, executable, readable)
- `0x40000040` -- `.rdata` (initialized data, readable)
- `0xC0000040` -- `.data` (initialized data, readable, writable)

### PluginFunctionEntry

Represents a user-defined function name registered via `RegisterFunction`.

| Field | Type | Description |
|-------|------|-------------|
| `Address` | `ulong` | Start address of the function. |
| `Name` | `string` | User-assigned function name. |
| `Size` | `uint` | Function size in bytes. `0` means only the exact start address matches. |

### PluginScriptHost

The globals type for the Roslyn REPL. Scripts access its members directly without qualification.

| Field | Type | Description |
|-------|------|-------------|
| `api` | `IDebuggerApi` | The full debugger API. |
| `print` | `Action<string>` | Print function routed to the scripting output panel. |

---

## 14. Function Naming

The `RegisterFunction` method on `api.Symbols` lets you assign human-readable names to functions discovered during analysis. This is essential for understanding unknown or obfuscated binaries.

**Always specify the `size` parameter.** Without it, only the exact start address resolves to your name. With it, any address within `[address, address+size)` resolves to `name+0xOffset`.

### Basic Usage

```csharp
// Name a function with its size
api.Symbols.RegisterFunction(0x7FF600001000, "DecryptPayload", 0x250);

// Now these all resolve:
// Sym(0x7FF600001000) => "DecryptPayload"
// Sym(0x7FF600001020) => "DecryptPayload+0x20"
// Sym(0x7FF600001249) => "DecryptPayload+0x249"

// Remove the name
api.Symbols.RegisterFunction(0x7FF600001000, null, 0);
```

### Auto-naming Functions from Call Targets

```csharp
// Scan the .text section for CALL instructions and name all unique targets
var mod = api.Symbols.GetModules()[0];
var code = ReadMem(mod.BaseAddress, mod.Size);
if (code == null) { print("Failed to read module"); return; }

var named = new HashSet<ulong>();
int count = 0;

for (uint i = 0; i < code.Length - 5; i++) {
    if (code[i] != 0xE8) continue; // CALL rel32
    int rel = BitConverter.ToInt32(code, (int)i + 1);
    ulong target = mod.BaseAddress + i + 5 + (ulong)(long)rel;
    
    // Verify target is within module
    if (target < mod.BaseAddress || target >= mod.BaseAddress + mod.Size) continue;
    if (named.Contains(target)) continue;
    
    // Skip already-named functions
    var existing = Sym(target);
    if (existing != null && !existing.Contains("+0x")) continue;
    
    // Estimate function size by scanning for RET or next known function
    uint funcSize = 0x100; // default estimate
    api.Symbols.RegisterFunction(target, $"sub_{target:X}", funcSize);
    named.Add(target);
    count++;
}

api.UI.RefreshDisassembly();
print($"Named {count} functions");
```

### Import-based Function Naming

```csharp
// Name wrapper functions that just JMP to imports
var mod = api.Symbols.GetModules()[0];
var code = ReadMem(mod.BaseAddress, mod.Size);

for (uint i = 0; i < code.Length - 6; i++) {
    if (code[i] != 0xFF || code[i+1] != 0x25) continue; // JMP [rip+disp32]
    int disp = BitConverter.ToInt32(code, (int)i + 2);
    ulong iatSlot = mod.BaseAddress + i + 6 + (ulong)(long)disp;
    ulong importAddr = ReadPtr(iatSlot);
    var importName = Sym(importAddr);
    if (importName != null) {
        var funcName = importName.Split('!').Last();
        api.Symbols.RegisterFunction(mod.BaseAddress + i, $"j_{funcName}", 6);
    }
}
api.UI.RefreshDisassembly();
```

### Listing and Exporting Function Names

```csharp
// List all registered functions
var funcs = api.Symbols.GetRegisteredFunctions();
print($"Total registered functions: {funcs.Count}");
foreach (var f in funcs.OrderBy(f => f.Address))
    print($"0x{f.Address:X016}  size=0x{f.Size:X04}  {f.Name}");

// Export to IDC script format
var sb = new StringBuilder();
sb.AppendLine("#include <idc.idc>");
sb.AppendLine("static main() {");
foreach (var f in funcs)
    sb.AppendLine($"  MakeNameEx(0x{f.Address:X}, \"{f.Name}\", SN_CHECK);");
sb.AppendLine("}");
File.WriteAllText(@"C:\Temp\functions.idc", sb.ToString());
print("Exported to IDC");
```

---

## 15. Anti-Debug Bypass

KernelFlirt provides kernel-level anti-debug bypass capabilities. These operate at the kernel level, making them invisible to user-mode anti-debug checks.

### Complete Anti-Debug Bypass Script

```csharp
// Full anti-debug bypass for most packers/protectors
var pid = api.TargetPid;

// 1. Clear debug port (hides from NtQueryInformationProcess)
api.Process.ClearDebugPort(pid);
print("[+] DebugPort cleared");

// 2. Clear ThreadHideFromDebugger on all threads
api.Process.ClearThreadHide(pid);
print("[+] ThreadHideFromDebugger cleared on all threads");

// 3. Hide process from NtQuerySystemInformation
api.Process.InstallNtQsiHook();
print("[+] NtQSI hook installed (process hidden from enumeration)");

// 4. Spoof SharedUserData.KdDebuggerEnabled
api.Process.SetSpoofSharedUserData(true);
print("[+] KdDebuggerEnabled spoofed to 0");

// 5. Patch PEB.BeingDebugged
var (peb, _) = api.Process.GetPebAddress(pid);
WriteMem(peb + 2, new byte[] { 0 }); // PEB.BeingDebugged = FALSE
print($"[+] PEB.BeingDebugged = 0 (PEB @ 0x{peb:X})");

// 6. Patch PEB.NtGlobalFlag (offset +0xBC on x64)
WriteMem(peb + 0xBC, BitConverter.GetBytes(0u)); // Clear FLG_HEAP_*
print("[+] PEB.NtGlobalFlag = 0");

// 7. Patch heap flags (ProcessHeap at PEB+0x30)
var heapBase = ReadPtr(peb + 0x30);
WriteMem(heapBase + 0x70, BitConverter.GetBytes(2u)); // Flags = HEAP_GROWABLE
WriteMem(heapBase + 0x74, BitConverter.GetBytes(0u)); // ForceFlags = 0
print($"[+] Heap flags patched (Heap @ 0x{heapBase:X})");

print("\n=== Anti-debug bypass complete ===");
```

### Monitoring Anti-Debug Checks

```csharp
// Set breakpoints on common anti-debug APIs to log and bypass them
var targets = new Dictionary<string, string> {
    ["ntdll!NtQueryInformationProcess"] = "NtQIP",
    ["kernel32!IsDebuggerPresent"] = "IsDebuggerPresent",
    ["kernel32!CheckRemoteDebuggerPresent"] = "CheckRemoteDebugger",
    ["ntdll!NtQuerySystemInformation"] = "NtQSI",
    ["ntdll!NtSetInformationThread"] = "NtSetInfoThread",
    ["ntdll!NtClose"] = "NtClose"
};

foreach (var (sym, label) in targets) {
    var addr = Addr(sym);
    if (addr == 0) continue;
    api.Breakpoints.SetBreakpoint(api.TargetPid, 0, addr, PluginBreakpointType.Software);
    print($"[+] BP on {label} at 0x{addr:X}");
}

api.OnDebugEventFilter += (evt) => {
    if (!targets.ContainsKey(Sym(evt.Address) ?? "")) return false;
    var label = targets[Sym(evt.Address)];
    print($"[Anti-Debug] {label} called from 0x{ReadPtr(Reg("RSP")):X}");
    // Auto-continue (let it execute, we've already patched the data)
    api.Continue();
    return true;
};
```

### Checking Hook Status

```csharp
// Probe the NtQSI hook status
var status = api.Process.ProbeNtQsiHook();
print(status);

// Remove hook when done
api.Process.RemoveNtQsiHook();
print("NtQSI hook removed");

// Disable SharedUserData spoofing
api.Process.SetSpoofSharedUserData(false);
print("SharedUserData spoofing disabled");
```

---

## 16. PE Analysis

Scripts can parse PE headers, sections, imports, and exports directly from target memory.

### Dump PE Header

```csharp
// Parse PE header from a module base
var mod = api.Symbols.GetModules()[0];
var header = ReadMem(mod.BaseAddress, 0x1000);
if (header == null || header[0] != 'M' || header[1] != 'Z') {
    print("Invalid PE header (possibly packed/erased)");
    return;
}

// e_lfanew at offset 0x3C
uint e_lfanew = BitConverter.ToUInt32(header, 0x3C);
print($"e_lfanew = 0x{e_lfanew:X}");

// PE signature check
uint peSignature = BitConverter.ToUInt32(header, (int)e_lfanew);
print($"PE Signature: 0x{peSignature:X08} ({(peSignature == 0x00004550 ? "valid" : "INVALID")})");

// COFF header
ushort machine = BitConverter.ToUInt16(header, (int)e_lfanew + 4);
ushort numSections = BitConverter.ToUInt16(header, (int)e_lfanew + 6);
print($"Machine: 0x{machine:X04} ({(machine == 0x8664 ? "AMD64" : machine == 0x14C ? "i386" : "?")})");
print($"Sections: {numSections}");

// Optional header
ushort magic = BitConverter.ToUInt16(header, (int)e_lfanew + 24);
bool isPE32Plus = magic == 0x20B;
print($"Optional header magic: 0x{magic:X04} ({(isPE32Plus ? "PE32+" : "PE32")})");

int optionalOffset = (int)e_lfanew + 24;
ulong imageBase = isPE32Plus
    ? BitConverter.ToUInt64(header, optionalOffset + 24)
    : BitConverter.ToUInt32(header, optionalOffset + 28);
uint entryPointRva = BitConverter.ToUInt32(header, optionalOffset + 16);
print($"ImageBase: 0x{imageBase:X}");
print($"EntryPoint RVA: 0x{entryPointRva:X}");
print($"EntryPoint VA: 0x{mod.BaseAddress + entryPointRva:X}");
```

### Enumerate PE Sections

```csharp
var mod = api.Symbols.GetModules()[0];
var header = ReadMem(mod.BaseAddress, 0x1000);
uint e_lfanew = BitConverter.ToUInt32(header, 0x3C);
ushort numSections = BitConverter.ToUInt16(header, (int)e_lfanew + 6);
ushort optHdrSize = BitConverter.ToUInt16(header, (int)e_lfanew + 20);
int sectionTableOffset = (int)e_lfanew + 24 + optHdrSize;

print($"{"Name",-10} {"VirtAddr",10} {"VirtSize",10} {"RawSize",10} {"Chars",10}");
print(new string('-', 55));

for (int i = 0; i < numSections; i++) {
    int off = sectionTableOffset + i * 40;
    string name = Encoding.ASCII.GetString(header, off, 8).TrimEnd('\0');
    uint virtualSize = BitConverter.ToUInt32(header, off + 8);
    uint virtualAddr = BitConverter.ToUInt32(header, off + 12);
    uint rawSize = BitConverter.ToUInt32(header, off + 16);
    uint chars = BitConverter.ToUInt32(header, off + 36);
    
    string flags = "";
    if ((chars & 0x20000000) != 0) flags += "X";
    if ((chars & 0x40000000) != 0) flags += "R";
    if ((chars & 0x80000000) != 0) flags += "W";
    
    print($"{name,-10} {virtualAddr,10:X} {virtualSize,10:X} {rawSize,10:X} {chars,10:X} [{flags}]");
}
```

### Walk Import Directory

```csharp
var mod = api.Symbols.GetModules()[0];
var header = ReadMem(mod.BaseAddress, 0x1000);
uint e_lfanew = BitConverter.ToUInt32(header, 0x3C);
ushort magic = BitConverter.ToUInt16(header, (int)e_lfanew + 24);
bool isPE32Plus = magic == 0x20B;

// Import directory RVA is at optional header + 104 (PE32+) or + 96 (PE32)
int importDirOffset = (int)e_lfanew + 24 + (isPE32Plus ? 120 : 104);
uint importRva = BitConverter.ToUInt32(header, importDirOffset);
uint importSize = BitConverter.ToUInt32(header, importDirOffset + 4);

if (importRva == 0) { print("No import directory"); return; }

// Read import descriptors
var importData = ReadMem(mod.BaseAddress + importRva, importSize + 0x1000);
int descSize = 20; // IMAGE_IMPORT_DESCRIPTOR size

for (int i = 0; ; i++) {
    int off = i * descSize;
    uint ilt = BitConverter.ToUInt32(importData, off);       // OriginalFirstThunk
    uint nameRva = BitConverter.ToUInt32(importData, off + 12);
    uint iat = BitConverter.ToUInt32(importData, off + 16);  // FirstThunk
    
    if (nameRva == 0) break; // null terminator
    
    var dllName = ReadString(mod.BaseAddress + nameRva, 256);
    print($"\n=== {dllName} (IAT @ 0x{mod.BaseAddress + iat:X}) ===");
    
    // Walk ILT/IAT entries
    ulong thunkAddr = mod.BaseAddress + (ilt != 0 ? ilt : iat);
    int idx = 0;
    while (true) {
        ulong thunkValue = ReadPtr(thunkAddr + (ulong)(idx * 8));
        if (thunkValue == 0) break;
        
        if ((thunkValue & 0x8000000000000000) != 0) {
            // Import by ordinal
            print($"  [{idx,3}] Ordinal #{thunkValue & 0xFFFF}");
        } else {
            // Import by name
            var funcName = ReadString(mod.BaseAddress + (thunkValue & 0x7FFFFFFF) + 2, 256);
            print($"  [{idx,3}] {funcName}");
        }
        idx++;
    }
}
```

### Walk Export Directory

```csharp
var mod = api.Symbols.GetModules().First(m => m.Name.Contains("kernel32", StringComparison.OrdinalIgnoreCase));
var header = ReadMem(mod.BaseAddress, 0x1000);
uint e_lfanew = BitConverter.ToUInt32(header, 0x3C);

// Export directory RVA at optional header + 112 (PE32+)
int exportDirOffset = (int)e_lfanew + 24 + 112;
uint exportRva = BitConverter.ToUInt32(header, exportDirOffset);
if (exportRva == 0) { print("No exports"); return; }

var expDir = ReadMem(mod.BaseAddress + exportRva, 40);
uint numFunctions = BitConverter.ToUInt32(expDir, 20);
uint numNames = BitConverter.ToUInt32(expDir, 24);
uint addrTableRva = BitConverter.ToUInt32(expDir, 28);
uint nameTableRva = BitConverter.ToUInt32(expDir, 32);
uint ordTableRva = BitConverter.ToUInt32(expDir, 36);

print($"Exports: {numNames} named, {numFunctions} total");

var nameTable = ReadMem(mod.BaseAddress + nameTableRva, numNames * 4);
var ordTable = ReadMem(mod.BaseAddress + ordTableRva, numNames * 2);
var addrTable = ReadMem(mod.BaseAddress + addrTableRva, numFunctions * 4);

for (uint i = 0; i < Math.Min(numNames, 50u); i++) {
    uint nameRva = BitConverter.ToUInt32(nameTable, (int)i * 4);
    ushort ordinal = BitConverter.ToUInt16(ordTable, (int)i * 2);
    uint funcRva = BitConverter.ToUInt32(addrTable, ordinal * 4);
    var name = ReadString(mod.BaseAddress + nameRva, 256);
    print($"  [{ordinal,4}] 0x{mod.BaseAddress + funcRva:X}  {name}");
}
```

---

## 17. Stack Walking

### Dump Raw Stack

```csharp
// Dump the top 32 stack entries with symbol resolution
var rsp = Reg("RSP");
print($"Stack at RSP = 0x{rsp:X016}");
print($"{"Offset",-8} {"Address",-18} {"Value",-18} Symbol");
print(new string('-', 70));

for (int i = 0; i < 32; i++) {
    ulong addr = rsp + (ulong)(i * 8);
    ulong value = ReadPtr(addr);
    var sym = Sym(value);
    string symStr = sym != null ? sym : "";
    print($"+0x{i*8:X03}   0x{addr:X016}  0x{value:X016}  {symStr}");
}
```

### Simple Return Address Chain

```csharp
// Walk RBP chain for frame-pointer-based callstacks
var rbp = Reg("RBP");
var rip = Reg("RIP");
print("=== Call Stack (RBP chain) ===");
print($"  #0  0x{rip:X016}  {Sym(rip) ?? "???"}");

for (int frame = 1; frame < 50; frame++) {
    if (rbp == 0 || rbp < 0x10000) break;
    var retAddr = ReadPtr(rbp + 8);
    var prevRbp = ReadPtr(rbp);
    
    if (retAddr == 0) break;
    print($"  #{frame,-2} 0x{retAddr:X016}  {Sym(retAddr) ?? "???"}");
    
    if (prevRbp <= rbp) break; // sanity: RBP should grow upward
    rbp = prevRbp;
}
```

### Scan Stack for Return Addresses

```csharp
// Heuristic stack walk: find all pointers to executable code
var rsp = Reg("RSP");
var stack = ReadMem(rsp, 0x2000); // 8KB of stack
var modules = api.Symbols.GetModules();

print("=== Potential return addresses on stack ===");
for (int i = 0; i < stack.Length - 7; i += 8) {
    ulong val = BitConverter.ToUInt64(stack, i);
    if (val == 0) continue;
    
    // Check if value points into any module
    var mod = modules.FirstOrDefault(m =>
        val >= m.BaseAddress && val < m.BaseAddress + m.Size);
    if (mod == null) continue;
    
    var sym = Sym(val);
    print($"  RSP+0x{i:X03}  0x{val:X016}  {sym ?? mod.Name + $"+0x{val - mod.BaseAddress:X}"}");
}
```

---

## 18. String Decryption

### XOR String Decryption

```csharp
// Decrypt XOR-encrypted strings with a single-byte key
byte xorKey = 0x37;
ulong encStrAddr = 0x7FF600005000;
uint encLen = 64;

var encrypted = ReadMem(encStrAddr, encLen);
var decrypted = new byte[encrypted.Length];
for (int i = 0; i < encrypted.Length; i++) {
    decrypted[i] = (byte)(encrypted[i] ^ xorKey);
    if (decrypted[i] == 0) break;
}

string result = Encoding.ASCII.GetString(decrypted).TrimEnd('\0');
print($"Decrypted: \"{result}\"");

// Annotate the address with the decrypted string
api.UI.SetAddressAnnotation(encStrAddr, $"dec: \"{result}\"");
api.UI.RefreshDisassembly();
```

### Rolling XOR Decryption

```csharp
// XOR with a multi-byte key
byte[] key = { 0xDE, 0xAD, 0xBE, 0xEF };
ulong addr = 0x7FF600006000;
uint size = 128;

var data = ReadMem(addr, size);
var plain = new byte[data.Length];
for (int i = 0; i < data.Length; i++)
    plain[i] = (byte)(data[i] ^ key[i % key.Length]);

print($"Decrypted: {Encoding.ASCII.GetString(plain).TrimEnd('\0')}");
```

### RC4 Decryption

```csharp
// RC4 decryption of a buffer in target memory
byte[] Rc4(byte[] key, byte[] data) {
    var s = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
    int j = 0;
    for (int i = 0; i < 256; i++) {
        j = (j + s[i] + key[i % key.Length]) & 0xFF;
        (s[i], s[j]) = (s[j], s[i]);
    }
    var output = new byte[data.Length];
    int x = 0, y = 0;
    for (int i = 0; i < data.Length; i++) {
        x = (x + 1) & 0xFF;
        y = (y + s[x]) & 0xFF;
        (s[x], s[y]) = (s[y], s[x]);
        output[i] = (byte)(data[i] ^ s[(s[x] + s[y]) & 0xFF]);
    }
    return output;
}

var encData = ReadMem(0x7FF600007000, 256);
var decData = Rc4(new byte[] { 0x41, 0x42, 0x43, 0x44 }, encData);
print(Encoding.ASCII.GetString(decData).TrimEnd('\0'));
```

### Batch String Table Decryption

```csharp
// Decrypt a table of encrypted string pointers
ulong tableBase = 0x7FF600008000;
int entryCount = 20;
byte xorKey = 0x5A;

for (int i = 0; i < entryCount; i++) {
    ulong strPtr = ReadPtr(tableBase + (ulong)(i * 16));
    uint strLen = ReadU32(tableBase + (ulong)(i * 16 + 8));
    
    if (strPtr == 0 || strLen == 0 || strLen > 1024) continue;
    
    var enc = ReadMem(strPtr, strLen);
    if (enc == null) continue;
    
    var dec = enc.Select(b => (byte)(b ^ xorKey)).ToArray();
    var str = Encoding.UTF8.GetString(dec);
    
    print($"[{i,3}] 0x{strPtr:X}: \"{str}\"");
    api.UI.SetAddressAnnotation(strPtr, $"\"{str}\"");
}
api.UI.RefreshDisassembly();
```

---

## 19. IAT Reconstruction

IAT (Import Address Table) reconstruction is needed after unpacking when the packer has destroyed or redirected IAT entries.

### Dump Current IAT

```csharp
// Read IAT entries and resolve them
var mod = api.Symbols.GetModules()[0];
ulong iatBase = mod.BaseAddress + 0x3000; // typical .idata or IAT RVA -- adjust for your binary
uint iatSize = 0x200;

print("=== Import Address Table ===");
for (ulong off = 0; off < iatSize; off += 8) {
    var addr = ReadPtr(iatBase + off);
    if (addr == 0) { print("  (null - end of block)"); continue; }
    var sym = Sym(addr);
    print($"  IAT[0x{iatBase + off:X}] -> 0x{addr:X}  {sym ?? "???"}");
}
```

### IAT Reconstruction via Breakpoint Tracing

```csharp
// Trace IAT calls by setting BP at each IAT entry target
// and logging what API is actually called (for stolen/redirected IATs)
var mod = api.Symbols.GetModules()[0];
ulong iatBase = mod.BaseAddress + 0x3000;
uint numEntries = 64;

var iatMap = new Dictionary<ulong, ulong>(); // iatSlot -> resolvedAPI

for (uint i = 0; i < numEntries; i++) {
    ulong slot = iatBase + (ulong)(i * 8);
    ulong target = ReadPtr(slot);
    if (target == 0) continue;
    
    var sym = Sym(target);
    if (sym != null && !sym.Contains("+0x")) {
        // Already resolved to a known API
        iatMap[slot] = target;
        continue;
    }
    
    // Target is unknown -- might be a packer thunk
    // Read the first bytes to check for JMP
    var bytes = ReadMem(target, 16);
    if (bytes != null && bytes[0] == 0xFF && bytes[1] == 0x25) {
        // JMP [addr] -- indirect jump, follow it
        int disp = BitConverter.ToInt32(bytes, 2);
        ulong realTarget = ReadPtr(target + 6 + (ulong)(long)disp);
        var realSym = Sym(realTarget);
        print($"  IAT[0x{slot:X}] -> thunk 0x{target:X} -> 0x{realTarget:X} {realSym ?? "???"}");
        
        // Patch IAT to point directly to the real API
        WriteMem(slot, BitConverter.GetBytes(realTarget));
    } else {
        print($"  IAT[0x{slot:X}] -> 0x{target:X} (unresolved, first bytes: {BitConverter.ToString(bytes, 0, 8)})");
    }
}

print($"\nResolved {iatMap.Count} IAT entries");
```

### Fix Stolen API Bytes

```csharp
// Some packers steal the first few bytes of API functions and
// redirect the IAT to a trampoline. Detect and repair.
var mod = api.Symbols.GetModules()[0];
var mods = api.Symbols.GetModules();

foreach (var bp in api.Breakpoints.GetAll().ToList()) {
    // For each redirected call, check if the target is a trampoline
    var target = ReadPtr(bp.Address);
    if (target == 0) continue;
    
    var bytes = ReadMem(target, 32);
    if (bytes == null) continue;
    
    // Pattern: stolen bytes + JMP to real API + offset
    // Look for: JMP rel32 after some instructions
    for (int i = 0; i < 20; i++) {
        if (bytes[i] == 0xE9) { // JMP rel32
            int rel = BitConverter.ToInt32(bytes, i + 1);
            ulong jumpTarget = target + (ulong)i + 5 + (ulong)(long)rel;
            var sym = Sym(jumpTarget);
            if (sym != null) {
                print($"Trampoline at 0x{target:X} stolen bytes={i}, jumps to {sym}");
            }
            break;
        }
    }
}
```

---

## 20. Unpacker Scripting

KernelFlirt provides powerful primitives for automating unpacking of protected executables.

### Generic OEP Finder Using Memory Breakpoints

```csharp
// Set a memory BP on the .text section to detect when the unpacker
// transfers control to the original code
var mod = api.Symbols.GetModules()[0];
var header = ReadMem(mod.BaseAddress, 0x1000);
uint e_lfanew = BitConverter.ToUInt32(header, 0x3C);
ushort optHdrSize = BitConverter.ToUInt16(header, (int)e_lfanew + 20);
int sectionOff = (int)e_lfanew + 24 + optHdrSize;

// First section is usually .text
uint textRva = BitConverter.ToUInt32(header, sectionOff + 12);
uint textSize = BitConverter.ToUInt32(header, sectionOff + 8);
ulong textBase = mod.BaseAddress + textRva;

print($".text section: 0x{textBase:X} size 0x{textSize:X}");

// Set memory breakpoint on .text
var bpHandle = api.Breakpoints.SetBreakpoint(
    api.TargetPid, 0, textBase,
    PluginBreakpointType.Memory);

api.OnDebugEventFilter += (evt) => {
    if (evt.Type != PluginDebugEventType.MemoryBp) return false;
    
    var rip = evt.Address;
    if (rip >= textBase && rip < textBase + textSize) {
        print($"\n=== OEP FOUND: 0x{rip:X} ===");
        api.Breakpoints.RemoveBreakpoint(bpHandle.Value);
        return false; // break in UI
    }
    
    api.Continue();
    return true;
};

print("Memory BP set. Run the target (F5) and wait for OEP detection...");
```

### UPX-style Unpacker

```csharp
// Simple UPX unpacking: run until the final JMP to OEP
var mod = api.Symbols.GetModules()[0];
var entryPoint = Reg("RIP");

// UPX typically has a tail JMP that goes to the original code section
// Set HW BP on the entry point to single-step through the decompression

// Strategy: set BP on the PUSHAD/POPAD pattern
// After POPAD, the next JMP/CALL is to the OEP

// Alternative: single-step until RIP enters the .text section
var textBase = mod.BaseAddress + 0x1000; // adjust
var textEnd = textBase + 0x10000;        // adjust

api.OnDebugEventFilter += (evt) => {
    if (evt.Type != PluginDebugEventType.SingleStep) return false;
    
    var rip = evt.Address;
    if (rip >= textBase && rip < textEnd) {
        print($"OEP reached: 0x{rip:X}");
        api.UI.NavigateDisassembly(rip);
        return false; // break in UI
    }
    
    // Keep stepping
    evt.ContinueMode = 2; // StepInto
    return true;
};

api.SingleStep();
print("Stepping through unpacker...");
```

### Register Unpacked Module After OEP Found

```csharp
// After finding the OEP, register the unpacked PE so all views update
var oep = Reg("RIP"); // assumed to be at OEP now
var mod = api.Symbols.GetModules()[0];

// Re-read the PE header (might have been decrypted by unpacker)
var header = ReadMem(mod.BaseAddress, 0x1000);
if (header[0] == 'M' && header[1] == 'Z') {
    // PE header is valid, register as unpacked module
    api.UI.AddUnpackedModule(mod.BaseAddress, "unpacked_" + mod.Name);
    print($"Registered unpacked module at 0x{mod.BaseAddress:X}");
} else {
    // PE header was erased by packer -- provide sections manually
    print("PE header erased, adding sections manually...");
    var sections = new List<PluginSectionInfo> {
        new() { Name = ".text",  VirtualAddress = 0x1000, VirtualSize = 0x20000, Characteristics = 0x60000020 },
        new() { Name = ".rdata", VirtualAddress = 0x21000, VirtualSize = 0x8000,  Characteristics = 0x40000040 },
        new() { Name = ".data",  VirtualAddress = 0x29000, VirtualSize = 0x3000,  Characteristics = 0xC0000040 },
    };
    api.UI.AddModuleSections("unpacked_payload", sections);
}

api.UI.RefreshModulesAndSections();

// Store OEP for other plugins
api.UI.SetPluginData("oep_address", oep);
print($"OEP stored: 0x{oep:X}");
```

### Dump Unpacked Binary to Disk

```csharp
// Full PE dump from memory to disk after unpacking
var mod = api.Symbols.GetModules()[0];
var peData = ReadMem(mod.BaseAddress, mod.Size);
if (peData == null) {
    print("Failed to read module memory");
    return;
}

// Fix the entry point in the PE header to point to OEP
var oep = Reg("RIP");
uint oepRva = (uint)(oep - mod.BaseAddress);
uint e_lfanew = BitConverter.ToUInt32(peData, 0x3C);
// AddressOfEntryPoint is at optional header + 16
Array.Copy(BitConverter.GetBytes(oepRva), 0, peData, (int)e_lfanew + 24 + 16, 4);

File.WriteAllBytes(@"C:\Temp\unpacked.exe", peData);
print($"Dumped {peData.Length} bytes to C:\\Temp\\unpacked.exe");
print($"Entry point RVA fixed to 0x{oepRva:X}");
```

### Unpacker with OnBeforeRun Hook

```csharp
// Use OnBeforeRun to automatically set breakpoints before each resume
var targetApi = Addr("kernel32!VirtualProtect");
int vpCallCount = 0;

api.OnBeforeRun += () => {
    // Ensure our BP is active before each run
    var bps = api.Breakpoints.GetAll();
    if (!bps.Any(b => b.Address == targetApi))
        api.Breakpoints.SetBreakpoint(api.TargetPid, 0, targetApi, PluginBreakpointType.Software);
};

api.OnDebugEventFilter += (evt) => {
    if (evt.Address != targetApi) return false;
    
    vpCallCount++;
    var addr = Reg("RCX");
    var size = Reg("RDX");
    var prot = (uint)Reg("R8");
    print($"VirtualProtect #{vpCallCount}: addr=0x{addr:X} size=0x{size:X} prot=0x{prot:X}");
    
    // Unpackers often call VirtualProtect(PAGE_EXECUTE_READWRITE) on .text before jumping to OEP
    if (prot == 0x40 && vpCallCount > 3) {
        print("Likely final VirtualProtect before OEP -- set BP on return");
        var retAddr = ReadPtr(Reg("RSP"));
        api.Breakpoints.SetBreakpoint(api.TargetPid, 0, retAddr, PluginBreakpointType.Hardware);
    }
    
    api.Continue();
    return true;
};
```

---

## 21. Memory Scanning

### Pattern Scanning (Signature Search)

```csharp
// Search for a byte pattern with wildcards in a module
// Pattern format: "48 8B ?? ?? 48 89" where ?? is a wildcard

string PatternToString(string pattern) => pattern; // identity for clarity

bool MatchPattern(byte[] data, int offset, byte[] pattern, bool[] mask) {
    for (int i = 0; i < pattern.Length; i++) {
        if (offset + i >= data.Length) return false;
        if (mask[i] && data[offset + i] != pattern[i]) return false;
    }
    return true;
}

(byte[] pattern, bool[] mask) ParsePattern(string sig) {
    var parts = sig.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    var pattern = new byte[parts.Length];
    var mask = new bool[parts.Length];
    for (int i = 0; i < parts.Length; i++) {
        if (parts[i] == "??") {
            pattern[i] = 0;
            mask[i] = false;
        } else {
            pattern[i] = Convert.ToByte(parts[i], 16);
            mask[i] = true;
        }
    }
    return (pattern, mask);
}

// Search for a pattern
var mod = api.Symbols.GetModules()[0];
var data = ReadMem(mod.BaseAddress, mod.Size);
if (data == null) { print("Read failed"); return; }

var (pat, msk) = ParsePattern("48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ??");
var results = new List<ulong>();

for (int i = 0; i < data.Length - pat.Length; i++) {
    if (MatchPattern(data, i, pat, msk)) {
        results.Add(mod.BaseAddress + (ulong)i);
    }
}

print($"Found {results.Count} matches:");
foreach (var addr in results)
    print($"  0x{addr:X}  {Sym(addr) ?? ""}");
```

### Find All String References

```csharp
// Scan for all ASCII strings in a memory region
var mod = api.Symbols.GetModules()[0];
var data = ReadMem(mod.BaseAddress, mod.Size);
if (data == null) return;

int minLen = 6;
var strings = new List<(ulong addr, string text)>();
int start = -1;

for (int i = 0; i < data.Length; i++) {
    byte b = data[i];
    if (b >= 0x20 && b < 0x7F) {
        if (start < 0) start = i;
    } else if (b == 0 && start >= 0) {
        int len = i - start;
        if (len >= minLen) {
            var s = Encoding.ASCII.GetString(data, start, len);
            strings.Add((mod.BaseAddress + (ulong)start, s));
        }
        start = -1;
    } else {
        start = -1;
    }
}

print($"Found {strings.Count} strings (min length {minLen}):");
foreach (var (addr, text) in strings.Take(100))
    print($"  0x{addr:X}  \"{text}\"");
```

### Memory Comparison / Diff

```csharp
// Compare two memory snapshots to find changes
ulong watchAddr = 0x7FF600040000;
uint watchSize = 0x1000;

// Take first snapshot
var snap1 = ReadMem(watchAddr, watchSize);
print("Snapshot 1 taken. Run the target, then take snapshot 2.");

// (After target runs and breaks again, execute this:)
// var snap2 = ReadMem(watchAddr, watchSize);
// if (snap1 != null && snap2 != null) {
//     int changes = 0;
//     for (int i = 0; i < snap1.Length; i++) {
//         if (snap1[i] != snap2[i]) {
//             print($"  +0x{i:X03}: 0x{snap1[i]:X02} -> 0x{snap2[i]:X02}");
//             changes++;
//         }
//     }
//     print($"Total changes: {changes}");
// }
```

### Search for Pointer to Address

```csharp
// Find all pointers to a specific address within a memory range
ulong searchFor = Addr("kernel32!CreateFileW");
var mod = api.Symbols.GetModules()[0];
var data = ReadMem(mod.BaseAddress, mod.Size);
if (data == null) return;

var searchBytes = BitConverter.GetBytes(searchFor);
var xrefs = new List<ulong>();

for (int i = 0; i <= data.Length - 8; i += 8) {
    if (BitConverter.ToUInt64(data, i) == searchFor)
        xrefs.Add(mod.BaseAddress + (ulong)i);
}

print($"Found {xrefs.Count} pointers to 0x{searchFor:X}:");
foreach (var x in xrefs)
    print($"  0x{x:X}  {Sym(x) ?? ""}");
```

### Search for Relative Call/Jump to Address

```csharp
// Find all CALL/JMP instructions that target a specific address
ulong target = Addr("kernel32!VirtualAlloc");
var mod = api.Symbols.GetModules()[0];
var data = ReadMem(mod.BaseAddress, mod.Size);
if (data == null) return;

var callers = new List<(ulong addr, string type)>();

for (uint i = 0; i < data.Length - 5; i++) {
    if (data[i] != 0xE8 && data[i] != 0xE9) continue;
    int rel = BitConverter.ToInt32(data, (int)i + 1);
    ulong dest = mod.BaseAddress + i + 5 + (ulong)(long)rel;
    if (dest == target) {
        string type = data[i] == 0xE8 ? "CALL" : "JMP";
        callers.Add((mod.BaseAddress + i, type));
    }
}

print($"Found {callers.Count} references to {Sym(target)}:");
foreach (var (addr, type) in callers)
    print($"  0x{addr:X}  {type}  {Sym(addr) ?? ""}");
```

---

## 22. Recipes

### Recipe 1: Full Register Dump

```csharp
var regs = api.Memory.ReadRegisters(api.TargetPid, api.SelectedThreadId);
print("=== General Purpose Registers ===");
foreach (var r in regs.Where(r => !r.IsFlag))
    print($"  {r.Name,-6} = 0x{r.Value:X016}");
print("\n=== Flags ===");
foreach (var r in regs.Where(r => r.IsFlag))
    print($"  {r.Name,-4} = {r.Value}");
```

### Recipe 2: Hex Dump

```csharp
// Pretty hex dump like WinDBG's db command
void HexDump(ulong addr, uint size) {
    var data = ReadMem(addr, size);
    if (data == null) { print("Read failed"); return; }
    for (int i = 0; i < data.Length; i += 16) {
        var hex = new StringBuilder();
        var ascii = new StringBuilder();
        for (int j = 0; j < 16 && i + j < data.Length; j++) {
            hex.Append($"{data[i+j]:X02} ");
            ascii.Append(data[i+j] >= 0x20 && data[i+j] < 0x7F ? (char)data[i+j] : '.');
        }
        print($"0x{addr + (ulong)i:X016}  {hex,-49} {ascii}");
    }
}
HexDump(Reg("RIP"), 128);
```

### Recipe 3: Find Module by Address

```csharp
ulong addr = Reg("RIP");
var mod = api.Symbols.GetModules()
    .FirstOrDefault(m => addr >= m.BaseAddress && addr < m.BaseAddress + m.Size);
if (mod != null)
    print($"0x{addr:X} is in {mod.Name} at offset +0x{addr - mod.BaseAddress:X}");
else
    print($"0x{addr:X} is not in any known module");
```

### Recipe 4: Trace Function Calls

```csharp
// Log all calls to a set of APIs
var apis = new[] {
    "kernel32!CreateFileW", "kernel32!ReadFile", "kernel32!WriteFile",
    "kernel32!CloseHandle", "kernel32!VirtualAlloc", "kernel32!VirtualFree"
};

var bpMap = new Dictionary<ulong, string>();
foreach (var name in apis) {
    var addr = Addr(name);
    if (addr == 0) continue;
    api.Breakpoints.SetBreakpoint(api.TargetPid, 0, addr, PluginBreakpointType.Software);
    bpMap[addr] = name.Split('!')[1];
}

api.OnDebugEventFilter += (evt) => {
    if (!bpMap.TryGetValue(evt.Address, out var name)) return false;
    var retAddr = ReadPtr(Reg("RSP"));
    print($"{name}() called from {Sym(retAddr) ?? $"0x{retAddr:X}"}");
    print($"  RCX=0x{Reg("RCX"):X}  RDX=0x{Reg("RDX"):X}  R8=0x{Reg("R8"):X}  R9=0x{Reg("R9"):X}");
    api.Continue();
    return true;
};
print($"Tracing {bpMap.Count} APIs. Press F5 to run.");
```

### Recipe 5: Patch Conditional Jump

```csharp
// Patch a conditional jump to always jump (JNE -> JMP)
ulong patchAddr = Reg("RIP");
var bytes = ReadMem(patchAddr, 2);
if (bytes[0] == 0x75) {
    // Short JNE -> short JMP
    WriteMem(patchAddr, new byte[] { 0xEB });
    print($"Patched JNE -> JMP at 0x{patchAddr:X}");
} else if (bytes[0] == 0x0F && bytes[1] == 0x85) {
    // Near JNE -> Near JMP
    WriteMem(patchAddr, new byte[] { 0xE9 });
    // Also need to adjust displacement by 1 (JNE is 6 bytes, JMP is 5)
    var disp = ReadMem(patchAddr + 2, 4);
    int offset = BitConverter.ToInt32(disp) + 1;
    WriteMem(patchAddr + 1, BitConverter.GetBytes(offset));
    // NOP the extra byte
    WriteMem(patchAddr + 5, new byte[] { 0x90 });
    print($"Patched near JNE -> JMP at 0x{patchAddr:X}");
}
```

### Recipe 6: NOP a Function (Make it Return Immediately)

```csharp
// Replace function body with RET (or XOR EAX,EAX + RET for return 0)
ulong funcAddr = Addr("myapp!AntiDebugCheck");
// xor eax, eax ; ret
WriteMem(funcAddr, new byte[] { 0x31, 0xC0, 0xC3 });
print($"Stubbed out function at 0x{funcAddr:X} (returns 0)");
```

### Recipe 7: Watch Memory Writes with HW Watchpoint

```csharp
// Monitor writes to a specific address
ulong watchAddr = 0x7FF600050000;
var handle = api.Breakpoints.SetBreakpoint(
    api.TargetPid, api.SelectedThreadId,
    watchAddr, PluginBreakpointType.HwWrite, 4);

api.OnDebugEventFilter += (evt) => {
    if (evt.Type != PluginDebugEventType.HwWatchpoint) return false;
    
    var newValue = ReadU32(watchAddr);
    print($"Write to 0x{watchAddr:X}: new value = 0x{newValue:X08}");
    print($"  Written by: 0x{Reg("RIP"):X}  {Sym(Reg("RIP")) ?? "???"}");
    
    api.Continue();
    return true;
};
print("Watchpoint set. Run target...");
```

### Recipe 8: Enumerate and Dump All Heaps

```csharp
// Read ProcessHeaps from PEB
var (peb, _) = api.Process.GetPebAddress(api.TargetPid);
uint numHeaps = ReadU32(peb + 0xE8);
ulong heapArrayPtr = ReadPtr(peb + 0xF0);

print($"Process has {numHeaps} heaps:");
for (uint i = 0; i < numHeaps; i++) {
    ulong heapBase = ReadPtr(heapArrayPtr + (ulong)(i * 8));
    var signature = ReadU32(heapBase);
    var flags = ReadU32(heapBase + 0x70);
    print($"  Heap[{i}] = 0x{heapBase:X016}  sig=0x{signature:X08}  flags=0x{flags:X08}");
}
```

### Recipe 9: Find and Dump a Vtable

```csharp
// Read vtable from an object pointer
var objPtr = Reg("RCX");
var vtablePtr = ReadPtr(objPtr);
print($"Object @ 0x{objPtr:X}, vtable @ 0x{vtablePtr:X}");
print($"{"Idx",4} {"Address",-18} Symbol");
print(new string('-', 55));

for (int i = 0; i < 30; i++) {
    var funcPtr = ReadPtr(vtablePtr + (ulong)(i * 8));
    if (funcPtr == 0) break;
    var sym = Sym(funcPtr) ?? "???";
    print($"[{i,3}] 0x{funcPtr:X016}  {sym}");
    
    // Optionally name the vtable entry
    if (sym == "???") {
        api.Symbols.RegisterFunction(funcPtr, $"vfunc_{i}", 0x20);
    }
}
api.UI.RefreshDisassembly();
```

### Recipe 10: Inject and Execute Code

```csharp
// Allocate memory, write shellcode, execute it via RIP redirect
var cave = api.Memory.AllocateMemory(api.TargetPid, 0x1000);
print($"Code cave at 0x{cave:X}");

// Save original context
var origRip = Reg("RIP");
var origRsp = Reg("RSP");

// Write shellcode: mov rax, 0x1234; int 3
var shellcode = new byte[] {
    0x48, 0xB8, 0x34, 0x12, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // mov rax, 0x1234
    0xCC  // int 3
};
WriteMem(cave, shellcode);

// Redirect execution
api.Memory.WriteRip(api.TargetPid, api.SelectedThreadId, cave);

// After the INT3 triggers, restore original context:
// api.Memory.WriteRip(api.TargetPid, api.SelectedThreadId, origRip);
print($"RIP redirected to 0x{cave:X}. Press F5 to execute.");
print($"After INT3, restore RIP to 0x{origRip:X}");
```

### Recipe 11: Module Base + RVA Calculator

```csharp
// Quick module+RVA lookup tool
var mods = api.Symbols.GetModules();
print("Module list with RVA calculator:");
print($"{"#",3} {"Base",-18} {"Size",10} {"Name",-30} EntryPoint");
print(new string('-', 80));

for (int i = 0; i < mods.Count; i++) {
    var m = mods[i];
    // Read entry point from PE header
    var hdr = ReadMem(m.BaseAddress, 0x200);
    uint ep = 0;
    if (hdr != null && hdr[0] == 'M' && hdr[1] == 'Z') {
        uint elf = BitConverter.ToUInt32(hdr, 0x3C);
        if (elf + 44 < hdr.Length)
            ep = BitConverter.ToUInt32(hdr, (int)elf + 40);
    }
    print($"{i,3} 0x{m.BaseAddress:X016}  0x{m.Size:X08}  {m.Name,-30} +0x{ep:X}");
}

// Usage: to convert RVA to VA
// var baseAddr = mods[0].BaseAddress;
// ulong va = baseAddr + 0x1234; // RVA 0x1234
```

### Recipe 12: Breakpoint on Module Load (via LdrLoadDll)

```csharp
// Hook LdrLoadDll to detect DLL loading
var ldrLoadDll = Addr("ntdll!LdrLoadDll");
api.Breakpoints.SetBreakpoint(api.TargetPid, 0, ldrLoadDll, PluginBreakpointType.Software);

api.OnDebugEventFilter += (evt) => {
    if (evt.Address != ldrLoadDll) return false;
    
    // 3rd parameter (R8) is PUNICODE_STRING for the DLL name
    var unicodeStr = Reg("R8");
    ushort len = (ushort)ReadU32(unicodeStr);      // UNICODE_STRING.Length
    ulong buf = ReadPtr(unicodeStr + 8);            // UNICODE_STRING.Buffer
    var dllName = ReadWString(buf, len / 2);
    
    print($"[DLL Load] {dllName}");
    api.Continue();
    return true;
};
print("DLL load monitor active. Run target...");
```

### Recipe 13: Save and Restore Annotations

```csharp
// Save annotations to file
var annotations = api.UI.GetAllAnnotations();
var sb = new StringBuilder();
foreach (var (addr, text) in annotations)
    sb.AppendLine($"{addr:X016}|{text.Replace("\n", "\\n")}");
File.WriteAllText(@"C:\Temp\annotations.dat", sb.ToString());
print($"Saved {annotations.Count} annotations");

// Load annotations from file
var lines = File.ReadAllLines(@"C:\Temp\annotations.dat");
int loaded = 0;
foreach (var line in lines) {
    var parts = line.Split('|', 2);
    if (parts.Length != 2) continue;
    ulong addr = ulong.Parse(parts[0], System.Globalization.NumberStyles.HexNumber);
    string text = parts[1].Replace("\\n", "\n");
    api.UI.SetAddressAnnotation(addr, text);
    loaded++;
}
api.UI.RefreshDisassembly();
print($"Loaded {loaded} annotations");
```

### Recipe 14: TEB and TLS Slot Access

```csharp
// Read the Thread Environment Block
var regs = api.Memory.ReadRegisters(api.TargetPid, api.SelectedThreadId);
var gsBase = regs.FirstOrDefault(r => r.Name == "GS")?.Value ?? 0;
// On x64, TEB is at GS:0x30 (self-pointer), but we can get it from the PEB
// For user-mode, TEB linear address can be obtained from the kernel

var threads = api.Process.EnumThreads(api.TargetPid);
var currentThread = threads.First(t => t.ThreadId == api.SelectedThreadId);
print($"Thread {currentThread.ThreadId}: start=0x{currentThread.StartAddress:X} priority={currentThread.Priority}");

// Read TLS slots from TEB (offset 0x58 = ThreadLocalStoragePointer)
// TEB base can be read via NtQueryInformationThread or from kernel
// For now, use the GS segment (on x64, GS:0x30 = TEB self-pointer)
// The TEB is typically at a high address like 0x7FF...
```

### Recipe 15: Automated Annotation of Known Structures

```csharp
// Annotate IMAGE_DOS_HEADER fields
var mod = api.Symbols.GetModules()[0];
var baseAddr = mod.BaseAddress;

var dosFields = new (uint offset, string name)[] {
    (0x00, "e_magic (MZ)"),
    (0x02, "e_cblp"),
    (0x04, "e_cp"),
    (0x06, "e_crlc"),
    (0x08, "e_cparhdr"),
    (0x3C, "e_lfanew (PE header offset)")
};

foreach (var (off, name) in dosFields) {
    api.UI.SetAddressAnnotation(baseAddr + off, name);
}

// Annotate PE header fields
var header = ReadMem(baseAddr, 0x200);
uint elf = BitConverter.ToUInt32(header, 0x3C);
var peFields = new (uint offset, string name)[] {
    (elf + 0, "PE Signature"),
    (elf + 4, "Machine"),
    (elf + 6, "NumberOfSections"),
    (elf + 8, "TimeDateStamp"),
    (elf + 20, "SizeOfOptionalHeader"),
    (elf + 24, "Magic (PE32/PE32+)"),
    (elf + 40, "AddressOfEntryPoint"),
    (elf + 48, "ImageBase"),
    (elf + 56, "SectionAlignment"),
    (elf + 60, "FileAlignment"),
    (elf + 80, "SizeOfImage"),
    (elf + 84, "SizeOfHeaders"),
    (elf + 88, "CheckSum"),
    (elf + 136, "DataDirectory[IMPORT]"),
    (elf + 152, "DataDirectory[RESOURCE]"),
    (elf + 160, "DataDirectory[EXCEPTION]"),
    (elf + 192, "DataDirectory[IAT]"),
};

foreach (var (off, name) in peFields)
    api.UI.SetAddressAnnotation(baseAddr + off, name);

api.UI.RefreshDisassembly();
print("PE header annotated");
```

### Recipe 16: Cross-reference Scanner (XREF to Address)

```csharp
// Find all instructions that reference a specific address
ulong target = Addr("myapp!g_config");
var mod = api.Symbols.GetModules()[0];
var data = ReadMem(mod.BaseAddress, mod.Size);
if (data == null) return;

var xrefs = new List<(ulong addr, string type)>();

for (uint i = 0; i < data.Length - 4; i++) {
    // Check for RIP-relative addressing: [RIP + disp32]
    // This appears in LEA, MOV, CMP, etc.
    int disp = BitConverter.ToInt32(data, (int)i);
    ulong refAddr = mod.BaseAddress + i + 4 + (ulong)(long)disp;
    
    if (refAddr == target) {
        // Backtrack to find the instruction start (heuristic)
        xrefs.Add((mod.BaseAddress + i - 3, "RIP-relative"));
    }
    
    // Also check for CALL rel32
    if (i > 0 && data[i-1] == 0xE8) {
        ulong callTarget = mod.BaseAddress + i + 4 + (ulong)(long)disp;
        if (callTarget == target)
            xrefs.Add((mod.BaseAddress + i - 1, "CALL"));
    }
}

print($"Found {xrefs.Count} cross-references to 0x{target:X}:");
foreach (var (addr, type) in xrefs.Take(50))
    print($"  0x{addr:X}  [{type}]  {Sym(addr) ?? ""}");
```

### Recipe 17: Conditional Tracing with Logging

```csharp
// Trace execution through a function, logging each unique basic block
var funcStart = Reg("RIP");
var funcEnd = funcStart + 0x200; // estimate function size
var visited = new HashSet<ulong>();

api.OnDebugEventFilter += (evt) => {
    if (evt.Type != PluginDebugEventType.SingleStep &&
        evt.Type != PluginDebugEventType.Breakpoint) return false;
    
    var rip = evt.Address;
    
    // Stop tracing when we leave the function
    if (rip < funcStart || rip >= funcEnd) {
        print($"Trace complete. Visited {visited.Count} unique addresses.");
        return false; // break in UI
    }
    
    if (visited.Add(rip)) {
        var sym = Sym(rip);
        print($"  0x{rip:X}  {sym ?? ""}");
    }
    
    evt.ContinueMode = 2; // StepInto
    return true;
};

api.SingleStep();
print($"Tracing function 0x{funcStart:X} - 0x{funcEnd:X}...");
```

### Recipe 18: Memory Region Permissions Map

```csharp
// Map the memory layout of the target process by probing pages
var mod = api.Symbols.GetModules()[0];
ulong startAddr = mod.BaseAddress;
ulong endAddr = startAddr + mod.Size;

print($"Memory map for {mod.Name}:");
print($"{"Address",-18} {"Size",10} Permissions");
print(new string('-', 45));

// Test read access to each page
for (ulong addr = startAddr; addr < endAddr; addr += 0x1000) {
    var data = ReadMem(addr, 1);
    string perm = data != null ? "R" : "-";
    
    // Try to detect executable pages by checking for common code patterns
    if (data != null) {
        var page = ReadMem(addr, 0x100);
        bool hasCode = page != null && page.Any(b => b == 0xCC || b == 0xC3 || b == 0xE8);
        if (hasCode) perm += "X";
    }
    
    print($"0x{addr:X016}  0x1000     {perm}");
}
```

---

## 23. Tips and Pitfalls

### General Tips

1. **Always check `api.IsBreakState` before memory/register operations.** Reading memory while the target is running can fail or return stale data.

2. **Use shortcuts for common operations.** `ReadMem`, `ReadPtr`, `Reg`, `Sym`, `Addr` are much shorter than the full `api.Memory.*` calls.

3. **Variables persist between runs.** Define complex data structures once, reference them later. Use `Reset State` if you need a clean slate.

4. **Selection + F5 is your friend.** Write a big script, then select and run individual parts as needed.

5. **Return values are auto-displayed.** End your script with an expression (not a statement) to see its value without `print()`.

6. **Use `api.UI.RefreshDisassembly()` after batch changes.** Multiple annotations or function registrations are faster if you refresh once at the end.

### Common Pitfalls

1. **Forgetting `api.TargetPid`.** The `ReadMem`/`WriteMem` shortcuts use `api.TargetPid` automatically, but the raw `api.Memory.ReadMemory` requires it explicitly.

2. **Integer overflow in address arithmetic.** Always cast intermediate values to `ulong` or `long` before adding to addresses:
   ```csharp
   // WRONG: rel can be negative, uint wraps
   ulong target = mod.BaseAddress + i + 5 + (ulong)rel;
   // RIGHT: sign-extend first
   ulong target = mod.BaseAddress + i + 5 + (ulong)(long)rel;
   ```

3. **ReadMem returns null on failure.** Always null-check:
   ```csharp
   var data = ReadMem(addr, 16);
   if (data == null) { print("Read failed"); return; }
   ```

4. **Event handlers accumulate.** Each time you run a script that adds an `OnDebugEventFilter` handler, a new handler is added. Previous handlers are NOT replaced. Use `Reset State` to clear them, or guard with a flag:
   ```csharp
   // Pattern to avoid duplicate handlers:
   if (api.UI.GetPluginData("myHandler") == null) {
       api.OnDebugEventFilter += MyHandler;
       api.UI.SetPluginData("myHandler", true);
   }
   ```

5. **`RegisterFunction` without `size` is almost useless.** Only the exact start address resolves. Always provide the size:
   ```csharp
   // BAD: only 0x1000 resolves to "MyFunc"
   api.Symbols.RegisterFunction(0x7FF600001000, "MyFunc");
   // GOOD: entire range 0x1000-0x1100 resolves
   api.Symbols.RegisterFunction(0x7FF600001000, "MyFunc", 0x100);
   ```

6. **HW breakpoints are per-thread and limited to 4.** Use software breakpoints when you need many breakpoints or thread-agnostic behavior.

7. **Breakpoint on the current instruction.** If you set a software BP at `RIP`, it replaces the current byte with `0xCC`. When you continue, the original byte is restored and the instruction executes. But be aware that `OriginalByte` in `PluginBreakpoint` stores the original byte for exactly this purpose.

8. **`Continue()` from `OnDebugEventFilter` must return `true`.** If you call `api.Continue()` but return `false`, the UI will also try to handle the event, causing a double-continue.

9. **WriteMemory to code sections works directly.** The kernel driver bypasses page protection, so you do NOT need to call `ProtectMemory` first when patching code. However, for user-mode allocated memory that is not PAGE_EXECUTE, you may need to change protection for execution.

10. **Do not call `SingleStep()` in a tight loop.** Each step requires a round-trip to the kernel. Use `OnDebugEventFilter` with `ContinueMode = 2` for efficient tracing, or use `ContinueMode = 4` (Trace) for kernel-side fast stepping.

11. **Unsigned hex literals need the `UL` suffix.** Without it, C# may interpret large values as negative:
    ```csharp
    // Compilation error for values > int.MaxValue without suffix:
    ulong addr = 0x7FF600001000; // OK (fits in long)
    ulong addr = 0xFFFFF80012340000; // ERROR without UL
    ulong addr = 0xFFFFF80012340000UL; // OK
    ```

12. **Async operations and timing.** `DecompileFunction` is async -- calling `GetDecompiledCode()` immediately returns empty. Use `await Task.Delay(2000)` or check in a loop.

---

## 24. Auto-imported Namespaces

The following namespaces are automatically imported in every script. You do not need `using` statements for these:

```
System
System.Collections.Generic
System.IO
System.Linq
System.Text
System.Threading.Tasks
KernelFlirt.SDK
```

### Available Assemblies

The following assemblies are referenced and available for use:

- `System.Runtime` (core types: `object`, `string`, `int`, etc.)
- `System.Console` (`Console.WriteLine`, `Console.Write`)
- `System.Linq` (LINQ extension methods: `Where`, `Select`, `FirstOrDefault`, etc.)
- `System.Collections` (`List<T>`, `Dictionary<TKey, TValue>`, `HashSet<T>`)
- `System.Runtime.InteropServices` (`BitConverter`, `Marshal`)
- `System.Text.Encoding` (`Encoding.ASCII`, `Encoding.Unicode`, `Encoding.UTF8`)
- `System.IO` (`File.ReadAllBytes`, `File.WriteAllBytes`, `Path`, `Directory`)
- `System.Threading.Tasks` (`Task`, `Task.Delay`, `async/await`)
- `KernelFlirt.SDK` (all debugger API types and interfaces)

### Adding Custom References

If you need additional .NET assemblies, you cannot add them from within the REPL. However, you can use reflection and `Assembly.LoadFrom` for dynamic loading:

```csharp
var asm = System.Reflection.Assembly.LoadFrom(@"C:\MyLibs\MyHelper.dll");
var type = asm.GetType("MyNamespace.MyClass");
var method = type.GetMethod("DoWork");
var result = method.Invoke(null, new object[] { "arg1" });
print($"Result: {result}");
```

---

*KernelFlirt Scripting Reference -- End of Document*
