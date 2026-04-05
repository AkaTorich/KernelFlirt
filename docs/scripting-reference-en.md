# KernelFlirt Scripting Reference

Scripting plugin — C# REPL with full access to the debugger API.  
Language: **C#** (Roslyn). Variables persist between executions.  
Hotkeys: **F5** / **Ctrl+Enter** = run. Select a fragment and press F5 to run only the selection.

---

## Shortcuts (global variables)

Available directly without any prefix:

| Shortcut | Type | Description |
|----------|------|-------------|
| `api` | `IDebuggerApi` | Full debugger API |
| `print("text")` | `void` | Print to output panel |
| `ReadMem(addr, size)` | `byte[]?` | Read `size` bytes at address |
| `WriteMem(addr, data)` | `bool` | Write bytes at address |
| `ReadString(addr)` | `string` | Null-terminated ASCII string (max 256) |
| `ReadString(addr, 1024)` | `string` | ASCII string with custom limit |
| `ReadWString(addr)` | `string` | Null-terminated Unicode (UTF-16) string |
| `ReadPtr(addr)` | `ulong` | Read pointer (8 bytes x64 / 4 bytes x86) |
| `ReadU32(addr)` | `uint` | Read 4 bytes as uint32 |
| `ReadU64(addr)` | `ulong` | Read 8 bytes as uint64 |
| `Reg("RAX")` | `ulong` | Register value by name |
| `RIP` | `ulong` | Current RIP (instruction pointer) |
| `RSP` | `ulong` | Current RSP (stack pointer) |
| `Sym(addr)` | `string?` | Symbol name at address (null if none) |
| `Addr("module!func")` | `ulong` | Address by symbol name |

---

## Full API (`api.*`)

### api — debugger state

```csharp
api.IsConnected      // bool — connected to target
api.IsBreakState     // bool — process is suspended
api.TargetPid        // uint — PID of debugged process
api.SelectedThreadId // uint — selected thread ID
api.Is32Bit          // bool — 32-bit process (WoW64)
```

### api.Memory — memory and registers

```csharp
api.Memory.ReadMemory(pid, addr, size)          // byte[]? — read memory
api.Memory.WriteMemory(pid, addr, data)         // bool — write memory
api.Memory.ReadRegisters(pid, tid)              // List<PluginRegister> — all registers
api.Memory.WriteRip(pid, tid, newRip)           // bool — change RIP
api.Memory.WriteRipAndRsp(tid, newRip, newRsp)  // bool — change RIP and RSP
api.Memory.ProtectMemory(pid, addr, size, prot) // (bool, uint) — change page protection
api.Memory.AllocateMemory(pid, size)            // ulong — allocate memory
api.Memory.FreeMemory(pid, addr)                // bool — free memory
```

### api.Breakpoints — breakpoints

```csharp
api.Breakpoints.SetBreakpoint(pid, tid, addr, type)  // uint? — set BP, returns handle
api.Breakpoints.RemoveBreakpoint(handle)              // bool — remove BP
api.Breakpoints.GetAll()                              // List<PluginBreakpoint> — all BPs

// Types:
PluginBreakpointType.Software    // INT3
PluginBreakpointType.Hardware    // DR0-3 execute
PluginBreakpointType.HwWrite     // DR0-3 write watchpoint
PluginBreakpointType.HwReadWrite // DR0-3 read/write watchpoint
PluginBreakpointType.Memory      // PAGE_GUARD
```

### api.Symbols — symbols

```csharp
api.Symbols.ResolveAddress(addr)         // string? — symbol name
api.Symbols.ResolveNameToAddress(name)   // ulong — address by name ("kernel32!CreateFileW")
api.Symbols.GetModules()                 // List<PluginModuleInfo> — user-mode modules
api.Symbols.GetKernelModules()           // List<PluginKernelModuleInfo> — kernel modules
```

### api.Process — processes and threads

```csharp
api.Process.EnumProcesses()              // List<PluginProcessInfo>
api.Process.EnumThreads(pid)             // List<PluginThreadInfo>
api.Process.SuspendThread(tid)           // bool
api.Process.ResumeThread(tid)            // bool
api.Process.GetPebAddress(pid)           // (ulong PEB, ulong PEB32)
api.Process.ClearDebugPort(pid)          // bool — anti-debug bypass
api.Process.ClearThreadHide(pid)         // bool — anti-debug bypass
api.Process.InstallNtQsiHook()           // bool — hide process from NtQuerySystemInformation
api.Process.RemoveNtQsiHook()            // bool
```

### api.UI — user interface

```csharp
api.UI.NavigateDisassembly(addr)                   // jump to address in disassembly
api.UI.SetAddressAnnotation(addr, "comment")       // set comment at address
api.UI.SetAddressAnnotation(addr, null)            // remove comment
api.UI.GetAddressAnnotation(addr)                  // string? — get comment
api.UI.GetAllAnnotations()                         // Dictionary<ulong, string>
api.UI.RefreshDisassembly()                        // refresh disassembly view
api.UI.DecompileFunction(addr)                     // start decompilation
api.UI.GetDecompiledCode()                         // string — RetDec output
api.UI.DisasmGoBack()                              // go back in navigation history
api.UI.AddUnpackedModule(peBase, name)             // add unpacked module
api.UI.RefreshModulesAndSections()                 // refresh module lists
```

### api.Log — logging

```csharp
api.Log.Info("message")     // output to Log tab
api.Log.Warning("message")
api.Log.Error("message")
```

### api — execution control

```csharp
api.Continue()          // resume execution (F5/F9)
api.SingleStep()        // step into (F7)
api.StepOver()          // step over (F8)
api.StepOut()           // step out of function (Ctrl+F9)
api.RunToCursor(addr)   // run to address (F4)
api.SkipInstruction()   // skip instruction (Ctrl+F8)
api.Pause()             // break (F12)
```

### api — events

```csharp
api.OnDebugEvent += (PluginDebugEvent evt) => { ... };
api.OnConnected += () => { ... };
api.OnDisconnected += () => { ... };
api.OnBreakStateEntered += () => { ... };
api.OnBreakStateExited += () => { ... };
api.OnBeforeRun += () => { ... };

// Event filter — return true to suppress UI break
api.OnDebugEventFilter += (PluginDebugEvent evt) => {
    print($"BP at 0x{evt.Address:X}");
    return false; // false = show in UI
};
```

---

## Auto-imported namespaces

```
System
System.Collections.Generic
System.IO
System.Linq
System.Text
System.Threading.Tasks
KernelFlirt.SDK
```

---

## Examples

### Basics

```csharp
// Show all registers
var regs = api.Memory.ReadRegisters(api.TargetPid, api.SelectedThreadId);
foreach (var r in regs.Where(r => !r.IsFlag))
    print($"{r.Name,-4} = 0x{r.Value:X016}");
```

```csharp
// Read 16 bytes at RIP
var bytes = ReadMem(RIP, 16);
print(BitConverter.ToString(bytes));
```

```csharp
// List modules
foreach (var m in api.Symbols.GetModules())
    print($"0x{m.BaseAddress:X} {m.Size,8:X} {m.Name}");
```

### Annotations

```csharp
// Name a function by offset from module base
var baseAddr = api.Symbols.GetModules()[0].BaseAddress;
api.UI.SetAddressAnnotation(baseAddr + 0x538, "ParseConfig");
api.UI.RefreshDisassembly();
```

```csharp
// Auto-annotate all unnamed call targets as sub_XXXX
var main = api.Symbols.GetModules()[0];
var data = ReadMem(main.BaseAddress, main.Size);
int count = 0;
for (uint i = 0; i < data.Length - 5; i++) {
    if (data[i] != 0xE8) continue;
    int rel = BitConverter.ToInt32(data, (int)i + 1);
    ulong target = main.BaseAddress + i + 5 + (ulong)rel;
    if (target < main.BaseAddress || target >= main.BaseAddress + main.Size) continue;
    if (Sym(target) == null || Sym(target).Contains("+0x")) {
        if (api.UI.GetAddressAnnotation(target) == null) {
            api.UI.SetAddressAnnotation(target, $"sub_{target:X}");
            count++;
        }
    }
}
api.UI.RefreshDisassembly();
print($"Annotated {count} functions");
```

### Memory and structures

```csharp
// Walk a linked list (LIST_ENTRY)
var head = Addr("ntdll!PebLdr") + 0x10;
var entry = ReadPtr(head);
while (entry != head && entry != 0) {
    var baseAddr = ReadPtr(entry + 0x30);
    var namePtr = ReadPtr(entry + 0x48 + 8);
    print($"0x{baseAddr:X016}  {ReadWString(namePtr)}");
    entry = ReadPtr(entry);
}
```

```csharp
// Dump vtable
var vtable = ReadPtr(Reg("RCX"));
for (int i = 0; i < 20; i++) {
    var func = ReadPtr(vtable + (ulong)(i * 8));
    print($"[{i,2}] 0x{func:X}  {Sym(func) ?? "???"}");
}
```

```csharp
// Memory snapshot and diff
var snap = ReadMem(0x7FF612340, 0x1000);
// ... (Run, Break) ...
var now = ReadMem(0x7FF612340, 0x1000);
for (int i = 0; i < snap.Length; i++)
    if (snap[i] != now[i])
        print($"+0x{i:X3}: {snap[i]:X2} -> {now[i]:X2}");
```

### Breakpoints and tracing

```csharp
// Logging breakpoint on a function
var target = Addr("ws2_32!send");
api.OnDebugEventFilter += evt => {
    if (evt.Address != target) return false;
    var buf = ReadPtr(Reg("RDX"));
    var len = (int)Reg("R8");
    var data = ReadMem(buf, (uint)Math.Min(len, 128));
    print($"send({len}): {Encoding.ASCII.GetString(data)}");
    return false;
};
api.Breakpoints.SetBreakpoint(api.TargetPid, 0, target, PluginBreakpointType.Software);
```

```csharp
// Conditional breakpoint — break only when RAX == 0
var addr = api.Symbols.GetModules()[0].BaseAddress + 0x1234UL;
api.OnDebugEventFilter += evt => {
    if (evt.Address != addr) return false;
    if (Reg("RAX") != 0) {
        api.Continue();
        return true; // suppress UI break
    }
    return false; // RAX == 0, break in UI
};
api.Breakpoints.SetBreakpoint(api.TargetPid, 0, addr, PluginBreakpointType.Software);
```

### Patching

```csharp
// NOP an instruction (5 bytes)
WriteMem(RIP, new byte[] { 0x90, 0x90, 0x90, 0x90, 0x90 });
```

```csharp
// Patch jne to jmp (always jump)
WriteMem(0x7FF612340, new byte[] { 0xEB }); // short jmp
```

```csharp
// Write a string to allocated memory
var addr = api.Memory.AllocateMemory(api.TargetPid, 256);
WriteMem(addr, Encoding.ASCII.GetBytes("Hello\0"));
print($"String at 0x{addr:X}");
```

### File I/O

```csharp
// Dump memory region to file
var data = ReadMem(0x7FF612340, 0x10000);
File.WriteAllBytes(@"C:\Temp\dump.bin", data);
print("Dumped 64KB");
```

```csharp
// Load patch from file
var patch = File.ReadAllBytes(@"C:\Temp\patch.bin");
WriteMem(0x7FF612340, patch);
```

---

## REPL behavior

- **Variables persist between runs** — define `var x = 42;` in one Run, use `x` in the next
- **Selection + F5** — run only the selected fragment
- **Reset State** — clear all variables
- **Console.WriteLine** — redirected to output panel
- **Async supported** — `await Task.Delay(1000);`
- **Compilation errors** — shown in output with line numbers
