# KernelFlirt Plugin SDK

## Overview

KernelFlirt SDK allows you to create plugins for the KernelFlirt kernel debugger. Plugins are .NET 9 class libraries placed in the `plugins/` folder next to `KernelFlirt.exe`. They are loaded automatically at startup.

**Target framework:** `net9.0-windows`
**Namespace:** `KernelFlirt.SDK`
**NuGet dependencies:** none (SDK is a project reference)

## Quick Start

1. Create a .NET 9 class library project
2. Reference `KernelFlirt.SDK.csproj`
3. Implement `IKernelFlirtPlugin`
4. Build and copy the DLL to `plugins/`

Minimal `.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0-windows</TargetFramework>
    <EnableDynamicLoading>true</EnableDynamicLoading>
    <UseWPF>true</UseWPF>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\sdk\KernelFlirt.SDK.csproj">
      <Private>false</Private>
      <ExcludeAssets>runtime</ExcludeAssets>
    </ProjectReference>
  </ItemGroup>
</Project>
```

Minimal plugin:
```csharp
using KernelFlirt.SDK;

public class MyPlugin : IKernelFlirtPlugin
{
    public string Name => "My Plugin";
    public string Description => "Does something useful";
    public string Version => "1.0";

    private IDebuggerApi _api = null!;

    public void Initialize(IDebuggerApi api)
    {
        _api = api;
        api.Log.Info("My plugin loaded!");
    }

    public void Shutdown() { }
}
```

---

## IKernelFlirtPlugin

Entry point for every plugin. The host discovers classes implementing this interface via reflection.

| Member | Description |
|--------|-------------|
| `string Name` | Display name shown in the plugin list |
| `string Description` | Short description of the plugin |
| `string Version` | Version string (e.g. `"1.0"`) |
| `void Initialize(IDebuggerApi api)` | Called once at startup. Save the `api` reference for later use. Register event handlers here. |
| `void Shutdown()` | Called when the application exits. Clean up resources. |

---

## IDebuggerApi

Main API surface passed to `Initialize()`. Provides access to all sub-APIs and debugger state.

### Sub-APIs

| Property | Type | Description |
|----------|------|-------------|
| `Memory` | `IMemoryApi` | Read/write process memory and registers |
| `Breakpoints` | `IBreakpointApi` | Set and remove breakpoints |
| `Symbols` | `ISymbolApi` | Resolve addresses to names and vice versa |
| `Process` | `IProcessApi` | Process/thread enumeration, anti-debug helpers |
| `Log` | `ILogApi` | Write messages to the log panel |
| `UI` | `IUiApi` | Add UI elements, navigate disassembly |

### State Properties

| Property | Type | Description |
|----------|------|-------------|
| `IsConnected` | `bool` | `true` when connected to the target (VM or local driver) |
| `IsBreakState` | `bool` | `true` when the target process is paused (breakpoint hit, single-step, etc.) |
| `TargetPid` | `uint` | PID of the debugged process (0 if none) |
| `SelectedThreadId` | `uint` | Currently selected thread ID |
| `Is32Bit` | `bool` | `true` if the target process is 32-bit (WoW64) |

### Events

| Event | Signature | Description |
|-------|-----------|-------------|
| `OnConnected` | `Action` | Fires when the debugger connects to the target |
| `OnDisconnected` | `Action` | Fires when the debugger disconnects |
| `OnBreakStateEntered` | `Action` | Fires when the process stops (breakpoint, step, etc.) |
| `OnBreakStateExited` | `Action` | Fires when the process resumes |
| `OnBeforeRun` | `Action` | Fires just before the process resumes (Run/F9). Good place to set breakpoints. |
| `OnDebugEvent` | `Action<PluginDebugEvent>` | Fires for every debug event (informational, after the UI processes it) |
| `OnDebugEventFilter` | `Func<PluginDebugEvent, bool>` | **Critical event.** Fires BEFORE the UI processes a debug event. Return `true` to suppress the UI break (plugin handles it). Return `false` to let the UI handle it normally. See [Debug Event Filter](#debug-event-filter) section. |

### Methods

| Method | Description |
|--------|-------------|
| `void Continue()` | Resume the process (equivalent to Run / F9) |
| `void SingleStep()` | Execute one instruction on the current thread |

---

## IMemoryApi

Read and write target process memory and registers.

| Method | Description |
|--------|-------------|
| `byte[]? ReadMemory(uint pid, ulong address, uint size)` | Read `size` bytes from `address` in process `pid`. Returns `null` on failure. |
| `bool WriteMemory(uint pid, ulong address, byte[] data)` | Write `data` to `address` in process `pid`. Returns success. |
| `IReadOnlyList<PluginRegister> ReadRegisters(uint pid, uint tid)` | Read all registers of thread `tid` in process `pid`. Returns a list of `PluginRegister` with Name/Value. Register names: `RAX`, `RBX`, `RCX`, `RDX`, `RSI`, `RDI`, `RBP`, `RSP`, `R8`-`R15`, `RIP`, `RFLAGS`, `DR0`-`DR7`, segment registers. |
| `bool WriteRip(uint pid, uint tid, ulong newRip)` | Set RIP of thread `tid` to `newRip`. Thread must be suspended (break state). |
| `bool WriteRipAndRsp(uint tid, ulong newRip, ulong newRsp)` | Set both RIP and RSP atomically. |
| `(bool ok, uint oldProtection) ProtectMemory(uint pid, ulong address, uint size, uint newProtection)` | Change page protection. `newProtection` uses Windows constants: `0x01`=PAGE_NOACCESS, `0x02`=PAGE_READONLY, `0x04`=PAGE_READWRITE, `0x10`=PAGE_EXECUTE, `0x20`=PAGE_EXECUTE_READ, `0x40`=PAGE_EXECUTE_READWRITE. Returns old protection. |

**Example — read a QWORD:**
```csharp
var data = _api.Memory.ReadMemory(pid, address, 8);
if (data != null)
{
    ulong value = BitConverter.ToUInt64(data);
}
```

**Example — read RIP:**
```csharp
var regs = _api.Memory.ReadRegisters(pid, tid);
ulong rip = regs.First(r => r.Name == "RIP").Value;
```

---

## IBreakpointApi

Manage software and hardware breakpoints.

| Method | Description |
|--------|-------------|
| `uint? SetBreakpoint(uint pid, uint tid, ulong address, PluginBreakpointType type, uint length = 1)` | Set a breakpoint. Returns a handle (uint) on success, `null` on failure. `pid`/`tid` specify the target. `length` is relevant for hardware watchpoints (1/2/4/8 bytes). |
| `bool RemoveBreakpoint(uint handle)` | Remove a breakpoint by handle. |
| `IReadOnlyList<PluginBreakpoint> GetAll()` | Get all active breakpoints. |

### PluginBreakpointType

| Value | Description |
|-------|-------------|
| `Software` (0) | INT3 software breakpoint. Breaks on execute. |
| `Hardware` (1) | Hardware execution breakpoint (DR0-DR3). |
| `HwWrite` (2) | Hardware write watchpoint. Breaks when the address is written. |
| `HwReadWrite` (3) | Hardware read/write watchpoint. |
| `Memory` (4) | Memory breakpoint (page protection based). |

**Example:**
```csharp
uint? handle = _api.Breakpoints.SetBreakpoint(pid, tid, 0x140001000, PluginBreakpointType.Software);
if (handle.HasValue)
    _api.Log.Info($"BP set, handle={handle.Value}");
```

---

## ISymbolApi

Symbol resolution via the integrated symbol engine (dbghelp + Microsoft symbol server).

| Method | Description |
|--------|-------------|
| `string? ResolveAddress(ulong address)` | Resolve an address to a symbol name (e.g. `"kernel32!CreateFileW"`). Returns `null` if no symbol found. |
| `ulong ResolveNameToAddress(string name)` | Resolve a symbol name to an address. Format: `"module!function"` (e.g. `"kernel32!Sleep"`, `"ntdll.dll!NtQueryInformationProcess"`). Returns 0 if not found. |
| `IReadOnlyList<PluginModuleInfo> GetModules()` | Get all loaded user-mode modules of the target process. |
| `IReadOnlyList<PluginKernelModuleInfo> GetKernelModules()` | Get all loaded kernel modules. |

**Example:**
```csharp
ulong sleepAddr = _api.Symbols.ResolveNameToAddress("kernel32!Sleep");
string? name = _api.Symbols.ResolveAddress(0x7FFE199B8580);
// name = "KERNELBASE!SleepEx+0x..."
```

---

## IProcessApi

Process and thread management, anti-debug utilities.

| Method | Description |
|--------|-------------|
| `IReadOnlyList<PluginProcessInfo> EnumProcesses()` | List all processes on the target machine. |
| `IReadOnlyList<PluginThreadInfo> EnumThreads(uint pid)` | List all threads of process `pid`. |
| `bool SuspendThread(uint tid)` | Suspend a thread. |
| `bool ResumeThread(uint tid)` | Resume a suspended thread. |
| `(ulong PebAddress, ulong Peb32Address) GetPebAddress(uint pid)` | Get the PEB address (64-bit and WoW64 32-bit) of a process. |
| `bool ClearDebugPort(uint pid)` | Zero out `EPROCESS.DebugPort`. Defeats `NtQueryInformationProcess(ProcessDebugPort)`, `ProcessDebugObjectHandle`, `ProcessDebugFlags`, and `NtClose` invalid handle checks. |
| `bool ClearThreadHide(uint pid)` | Clear `HideFromDebugger` bit in `CrossThreadFlags` for all threads in the process. Defeats `NtSetInformationThread(ThreadHideFromDebugger)`. |
| `bool InstallNtQsiHook()` | Install an inline hook on `NtQuerySystemInformation` to spoof `SystemKernelDebuggerInformation` (class 0x23). **WARNING: triggers PatchGuard BSOD after 5-10 minutes.** |
| `bool RemoveNtQsiHook()` | Remove the NtQSI hook. |
| `string ProbeNtQsiHook()` | Diagnostic: probe the NtQSI function bytes and disassembly. Returns a string with details. |

---

## ILogApi

Write messages to the KernelFlirt log panel.

| Method | Description |
|--------|-------------|
| `void Info(string message)` | Log an informational message. Prefixed with `[Plugin]`. |
| `void Warning(string message)` | Log a warning. Prefixed with `[Plugin] WARNING:`. Shown in yellow. |
| `void Error(string message)` | Log an error. Prefixed with `[Plugin] ERROR:`. Shown in red. |

---

## IUiApi

Interact with the KernelFlirt UI. All methods are thread-safe (automatically dispatched to the UI thread).

| Method | Description |
|--------|-------------|
| `void NavigateDisassembly(ulong address)` | Scroll the disassembly view to `address`. |
| `void AddMenuItem(string header, Action callback)` | Add a menu item to the Plugins menu. |
| `void AddToolPanel(string title, object wpfContent)` | Add a custom WPF tab to the main window. `wpfContent` must be a WPF UIElement (e.g. `StackPanel`, `Grid`, etc.). |
| `void AddUnpackedModule(ulong peBase, string name)` | Register a dynamically unpacked PE at `peBase`. Triggers module/section/import/string/function refresh. |
| `void RefreshModulesAndSections()` | Force refresh the modules and sections tabs. |
| `void AddModuleSections(string moduleName, IReadOnlyList<PluginSectionInfo> sections)` | Provide section info directly (bypasses PE header parsing). Use when packer zeroes PE headers. |

**Example — custom tab:**
```csharp
var panel = new StackPanel();
panel.Children.Add(new TextBlock { Text = "Hello from plugin!" });
var button = new Button { Content = "Click me" };
button.Click += (s, e) => _api.Log.Info("Button clicked!");
panel.Children.Add(button);
_api.UI.AddToolPanel("My Tab", panel);
```

---

## Debug Event Filter

The most powerful plugin mechanism. `OnDebugEventFilter` lets you intercept debug events before the UI processes them.

### PluginDebugEvent

Passed to the filter callback. Contains event info and **writable fields** to control how the process resumes.

**Read-only fields (event info):**

| Field | Type | Description |
|-------|------|-------------|
| `Type` | `PluginDebugEventType` | Event type (Breakpoint, SingleStep, AccessViolation, etc.) |
| `ProcessId` | `uint` | Process that triggered the event |
| `ThreadId` | `uint` | Thread that triggered the event |
| `Address` | `ulong` | RIP at the time of the event |
| `IsKernelMode` | `bool` | `true` if the event occurred in kernel mode |
| `ExceptionCode` | `uint` | Windows exception code (e.g. `0x80000003` for breakpoint) |
| `FaultAddress` | `ulong` | For AccessViolation: the address that was accessed |
| `AccessType` | `uint` | For AccessViolation: `0`=read, `1`=write, `8`=execute |

**Writable fields (control resume):**

| Field | Type | Description |
|-------|------|-------------|
| `ContinueMode` | `uint` | How to resume. See table below. Default=0. |
| `NewRip` | `ulong` | Override RIP before resuming. Set to non-zero to redirect execution. |
| `NewRsp` | `ulong` | Override RSP before resuming. |
| `TraceRangeBase` | `ulong` | For ContinueMode=4: trace range start (inclusive). |
| `TraceRangeEnd` | `ulong` | For ContinueMode=4: trace range end (exclusive). |
| `TraceMaxSteps` | `uint` | For ContinueMode=4: max steps before reporting (0 = 500,000). |

### ContinueMode Values

| Value | Name | Description |
|-------|------|-------------|
| 0 | Run | Resume execution normally (default). |
| 1 | StepPast | Step past a software breakpoint, then auto-continue (like F9 over a BP). |
| 2 | StepInto | Step past a software breakpoint, then report SingleStep (like F7). |
| 3 | Handled | Suppress the exception (AV won't reach process SEH) + set Trap Flag for single-step. Used for guard page tracing. |
| 4 | Trace | Fast driver-side trace. The driver steps internally while RIP is in `[TraceRangeBase, TraceRangeEnd)`. Reports a SingleStep event only when RIP exits the range or `TraceMaxSteps` is reached. Used for IAT tracing through packer wrappers. |

### PluginDebugEventType

| Value | Description |
|-------|-------------|
| `Breakpoint` (1) | Software breakpoint (INT3) hit |
| `SingleStep` (2) | Single-step completed (Trap Flag) |
| `HwBreakpoint` (3) | Hardware execution breakpoint hit |
| `HwWatchpoint` (4) | Hardware data watchpoint triggered |
| `MemoryBp` (5) | Memory breakpoint triggered |
| `AccessViolation` (6) | Access violation (page fault) |

### Example — Guard Page Tracing

```csharp
private bool OnDebugEventFilter(PluginDebugEvent evt)
{
    if (evt.Type == PluginDebugEventType.AccessViolation)
    {
        ulong fault = evt.FaultAddress;
        if (fault >= _guardBase && fault < _guardEnd)
        {
            // Remove guard, let the access happen, re-arm on next step
            _api.Memory.ProtectMemory(pid, _guardBase, _guardSize, 0x04); // PAGE_READWRITE
            evt.ContinueMode = 3; // Handled: suppress AV + set TF
            _rearmOnStep = true;
            return true; // plugin handles this event
        }
    }

    if (evt.Type == PluginDebugEventType.SingleStep && _rearmOnStep)
    {
        _api.Memory.ProtectMemory(pid, _guardBase, _guardSize, 0x01); // PAGE_NOACCESS
        _rearmOnStep = false;
        return true; // suppress, keep running
    }

    return false; // let UI handle
}
```

---

## Data Models

### PluginRegister
```csharp
public class PluginRegister
{
    public string Name { get; set; }   // "RAX", "RIP", "RFLAGS", "CF", "ZF", etc.
    public ulong Value { get; set; }   // Register value
    public bool IsFlag { get; set; }   // true for individual flags (CF, ZF, SF, etc.)
}
```

### PluginBreakpoint
```csharp
public class PluginBreakpoint
{
    public uint Handle { get; set; }              // Unique handle for removal
    public ulong Address { get; set; }            // Breakpoint address
    public PluginBreakpointType Type { get; set; } // Software, Hardware, etc.
    public bool Enabled { get; set; }
    public string? Condition { get; set; }
    public uint HitCount { get; set; }
}
```

### PluginModuleInfo
```csharp
public class PluginModuleInfo
{
    public ulong BaseAddress { get; set; }  // Module base in process VA space
    public uint Size { get; set; }          // Module size
    public string Name { get; set; }        // e.g. "kernel32.dll"
}
```

### PluginKernelModuleInfo
```csharp
public class PluginKernelModuleInfo
{
    public ulong BaseAddress { get; set; }  // Kernel VA
    public uint Size { get; set; }
    public ushort LoadOrder { get; set; }
    public string Name { get; set; }        // e.g. "ntoskrnl.exe"
}
```

### PluginProcessInfo
```csharp
public class PluginProcessInfo
{
    public uint ProcessId { get; set; }
    public uint SessionId { get; set; }
    public string Name { get; set; }   // e.g. "notepad.exe"
}
```

### PluginThreadInfo
```csharp
public class PluginThreadInfo
{
    public uint ThreadId { get; set; }
    public ulong StartAddress { get; set; }
    public uint State { get; set; }     // Thread state flags
    public uint Priority { get; set; }
}
```

### PluginSectionInfo
```csharp
public class PluginSectionInfo
{
    public string Name { get; set; }           // e.g. ".text", ".rdata"
    public ulong VirtualAddress { get; set; }  // RVA (absolute, not relative)
    public uint VirtualSize { get; set; }
    public uint Characteristics { get; set; }  // PE section flags
}
```

---

## Architecture Notes

- KernelFlirt debugs via a **kernel driver** that hooks `KdpStub` in `ntoskrnl.exe`. This allows debugging without a standard kernel debugger connection.
- The driver communicates with the UI via **IOCTLs**. For remote debugging (VM), a **relay** (`KfRelay.exe`) on the target machine forwards IOCTLs over TCP.
- Debug events arrive on a **background thread**. `OnDebugEventFilter` is called on this thread, NOT the UI thread. All `IUiApi` methods are automatically dispatched to the UI thread.
- When `OnDebugEventFilter` returns `true`, the `ContinueMode` and `NewRip`/`NewRsp` fields are sent to the driver. The process resumes without the UI ever seeing the event.
- `OnDebugEvent` (non-filter) fires on the UI thread AFTER the event is processed.
- Only **x64** debugging is supported. `Is32Bit` exists for future WoW64 support but is not currently functional.
