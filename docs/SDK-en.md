# KernelFlirt Plugin SDK -- Complete Reference and Development Guide

**Version:** 1.8.1
**Framework:** .NET 9.0 (net9.0-windows)
**Namespace:** `KernelFlirt.SDK`

---

## Table of Contents

1. [Introduction](#1-introduction)
   - 1.1 [What is KernelFlirt?](#11-what-is-kernelflirt)
   - 1.2 [Plugin Architecture Overview](#12-plugin-architecture-overview)
   - 1.3 [How Plugins Are Loaded](#13-how-plugins-are-loaded)
   - 1.4 [Capabilities and Limitations](#14-capabilities-and-limitations)
2. [Getting Started](#2-getting-started)
   - 2.1 [Prerequisites](#21-prerequisites)
   - 2.2 [Creating a Plugin Project](#22-creating-a-plugin-project)
   - 2.3 [The .csproj File Explained](#23-the-csproj-file-explained)
   - 2.4 [The IKernelFlirtPlugin Interface](#24-the-ikernelflirtplugin-interface)
   - 2.5 [Minimal Plugin](#25-minimal-plugin)
   - 2.6 [Building Your Plugin](#26-building-your-plugin)
   - 2.7 [Deploying to the plugins/ Folder](#27-deploying-to-the-plugins-folder)
   - 2.8 [Debugging Your Plugin](#28-debugging-your-plugin)
3. [Core API: IDebuggerApi](#3-core-api-idebuggerapi)
   - 3.1 [Sub-API Properties](#31-sub-api-properties)
   - 3.2 [Debugger State Properties](#32-debugger-state-properties)
   - 3.3 [Execution Control Methods](#33-execution-control-methods)
   - 3.4 [Events](#34-events)
4. [Memory API: IMemoryApi](#4-memory-api-imemoryapi)
   - 4.1 [ReadMemory](#41-readmemory)
   - 4.2 [WriteMemory](#42-writememory)
   - 4.3 [ReadRegisters](#43-readregisters)
   - 4.4 [WriteRip](#44-writerip)
   - 4.5 [WriteRipAndRsp](#45-writeripandrsp)
   - 4.6 [ProtectMemory](#46-protectmemory)
   - 4.7 [AllocateMemory](#47-allocatememory)
   - 4.8 [FreeMemory](#48-freememory)
5. [Breakpoint API: IBreakpointApi](#5-breakpoint-api-ibreakpointapi)
   - 5.1 [SetBreakpoint](#51-setbreakpoint)
   - 5.2 [RemoveBreakpoint](#52-removebreakpoint)
   - 5.3 [GetAll](#53-getall)
   - 5.4 [ToggleBreakpoint](#54-togglebreakpoint)
   - 5.5 [Breakpoint Types](#55-breakpoint-types)
6. [Symbol API: ISymbolApi](#6-symbol-api-isymbolapi)
   - 6.1 [ResolveAddress](#61-resolveaddress)
   - 6.2 [ResolveNameToAddress](#62-resolvenametoaddress)
   - 6.3 [GetModules](#63-getmodules)
   - 6.4 [GetKernelModules](#64-getkernelmodules)
   - 6.5 [RegisterFunction](#65-registerfunction)
   - 6.6 [GetRegisteredFunctions](#66-getregisteredfunctions)
7. [Process API: IProcessApi](#7-process-api-iprocessapi)
   - 7.1 [EnumProcesses](#71-enumprocesses)
   - 7.2 [EnumThreads](#72-enumthreads)
   - 7.3 [SuspendThread / ResumeThread](#73-suspendthread--resumethread)
   - 7.4 [GetPebAddress](#74-getpebaddress)
   - 7.5 [Anti-Debug Methods](#75-anti-debug-methods)
8. [Log API: ILogApi](#8-log-api-ilogapi)
   - 8.1 [Info, Warning, Error](#81-info-warning-error)
9. [UI API: IUiApi](#9-ui-api-iuiapi)
   - 9.1 [NavigateDisassembly](#91-navigatedisassembly)
   - 9.2 [DisasmGoBack](#92-disasmgoback)
   - 9.3 [AddMenuItem](#93-addmenuitem)
   - 9.4 [AddToolPanel](#94-addtoolpanel)
   - 9.5 [Annotations](#95-annotations)
   - 9.6 [Decompilation](#96-decompilation)
   - 9.7 [Module Management](#97-module-management)
   - 9.8 [Cross-Plugin Communication](#98-cross-plugin-communication)
   - 9.9 [Note Events](#99-note-events)
10. [Data Models](#10-data-models)
    - 10.1 [PluginRegister](#101-pluginregister)
    - 10.2 [PluginBreakpoint](#102-pluginbreakpoint)
    - 10.3 [PluginModuleInfo](#103-pluginmoduleinfo)
    - 10.4 [PluginKernelModuleInfo](#104-pluginkernelmoduleinfo)
    - 10.5 [PluginProcessInfo](#105-pluginprocessinfo)
    - 10.6 [PluginThreadInfo](#106-pluginthreadinfo)
    - 10.7 [PluginSectionInfo](#107-pluginsectioninfo)
    - 10.8 [PluginFunctionEntry](#108-pluginfunctionentry)
    - 10.9 [PluginDebugEvent](#109-plugindebugevent)
    - 10.10 [PluginScriptHost](#1010-pluginscripthost)
11. [Enumerations](#11-enumerations)
    - 11.1 [PluginBreakpointType](#111-pluginbreakpointtype)
    - 11.2 [PluginDebugEventType](#112-plugindebugeventtype)
12. [Debug Events and Filters](#12-debug-events-and-filters)
    - 12.1 [OnDebugEvent vs OnDebugEventFilter](#121-ondebugevent-vs-ondebugeventfilter)
    - 12.2 [The PluginDebugEvent Object](#122-the-plugindebugevent-object)
    - 12.3 [ContinueMode Values](#123-continuemode-values)
    - 12.4 [Controlling Execution Flow](#124-controlling-execution-flow)
    - 12.5 [Guard Page Tracing Pattern](#125-guard-page-tracing-pattern)
    - 12.6 [API Hooking Pattern](#126-api-hooking-pattern)
    - 12.7 [Driver-Side Trace Mode](#127-driver-side-trace-mode)
13. [UI Development](#13-ui-development)
    - 13.1 [WPF Fundamentals for Plugins](#131-wpf-fundamentals-for-plugins)
    - 13.2 [Adding a Tool Panel](#132-adding-a-tool-panel)
    - 13.3 [DataGrid Patterns](#133-datagrid-patterns)
    - 13.4 [Theming and Brushes](#134-theming-and-brushes)
    - 13.5 [Context Menus](#135-context-menus)
    - 13.6 [Toolbar Buttons](#136-toolbar-buttons)
    - 13.7 [Dialog Windows](#137-dialog-windows)
    - 13.8 [Progress Indication](#138-progress-indication)
    - 13.9 [Status Bars with Data Binding](#139-status-bars-with-data-binding)
14. [Threading Model](#14-threading-model)
    - 14.1 [UI Thread vs Background Threads](#141-ui-thread-vs-background-threads)
    - 14.2 [Dispatcher Marshaling](#142-dispatcher-marshaling)
    - 14.3 [Async/Await Patterns](#143-asyncawait-patterns)
    - 14.4 [Cancellation](#144-cancellation)
    - 14.5 [Thread Safety of API Calls](#145-thread-safety-of-api-calls)
15. [Cross-Plugin Communication](#15-cross-plugin-communication)
    - 15.1 [SetPluginData / GetPluginData](#151-setplugindata--getplugindata)
    - 15.2 [Common Data Keys](#152-common-data-keys)
    - 15.3 [Exposing Functions to Other Plugins](#153-exposing-functions-to-other-plugins)
16. [Plugin Persistence](#16-plugin-persistence)
    - 16.1 [File-Based Persistence](#161-file-based-persistence)
    - 16.2 [Target-Aware File Naming](#162-target-aware-file-naming)
    - 16.3 [Session Save/Load](#163-session-saveload)
    - 16.4 [Module Rebasing on Load](#164-module-rebasing-on-load)
17. [RegisterFunction and Size Parameter](#17-registerfunction-and-size-parameter)
    - 17.1 [Why Size Matters](#171-why-size-matters)
    - 17.2 [Usage Patterns](#172-usage-patterns)
    - 17.3 [Interaction with ResolveAddress](#173-interaction-with-resolveaddress)
18. [Anti-Debug API](#18-anti-debug-api)
    - 18.1 [ClearDebugPort](#181-cleardebugport)
    - 18.2 [ClearThreadHide](#182-clearthreadhide)
    - 18.3 [NtQuerySystemInformation Hook](#183-ntquerysysteminformation-hook)
    - 18.4 [SharedUserData Spoofing](#184-shareduserdata-spoofing)
    - 18.5 [PEB Patching](#185-peb-patching)
    - 18.6 [Building a Complete Anti-Debug Plugin](#186-building-a-complete-anti-debug-plugin)
19. [Complete Example Plugins](#19-complete-example-plugins)
    - 19.1 [Simple: Menu Item Logger](#191-simple-menu-item-logger)
    - 19.2 [Medium: Bookmarks Panel with DataGrid](#192-medium-bookmarks-panel-with-datagrid)
    - 19.3 [Complex: Background Memory Scanner](#193-complex-background-memory-scanner)
    - 19.4 [Advanced: API Monitor with Debug Event Filters](#194-advanced-api-monitor-with-debug-event-filters)
20. [Best Practices and Common Pitfalls](#20-best-practices-and-common-pitfalls)
    - 20.1 [Do's](#201-dos)
    - 20.2 [Don'ts](#202-donts)
    - 20.3 [Common Pitfalls](#203-common-pitfalls)
    - 20.4 [Performance Considerations](#204-performance-considerations)
21. [Architecture Deep Dive](#21-architecture-deep-dive)
    - 21.1 [Kernel Driver Communication](#211-kernel-driver-communication)
    - 21.2 [Remote Debugging via KfRelay](#212-remote-debugging-via-kfrelay)
    - 21.3 [Debug Event Pipeline](#213-debug-event-pipeline)
    - 21.4 [Symbol Engine](#214-symbol-engine)
22. [Appendix A: Sample Plugins Overview](#22-appendix-a-sample-plugins-overview)
23. [Appendix B: Windows Memory Protection Constants](#23-appendix-b-windows-memory-protection-constants)
24. [Appendix C: Register Names](#24-appendix-c-register-names)

---

## 1. Introduction

### 1.1 What is KernelFlirt?

KernelFlirt is a Windows kernel-level debugger designed for reverse engineering and malware analysis. Unlike traditional debuggers that rely on the Windows Debug API (`DebugActiveProcess`), KernelFlirt operates through a custom kernel driver that hooks `KdpStub` in `ntoskrnl.exe`. This approach provides several unique advantages:

- **Invisible to anti-debug checks**: The debugger does not create a debug port, does not set `PEB.BeingDebugged`, and is invisible to standard anti-debug detection techniques such as `NtQueryInformationProcess(ProcessDebugPort)`.
- **Kernel-mode access**: Direct access to kernel memory, kernel modules, and system structures.
- **Cross-VM debugging**: Supports remote debugging of virtual machines via a TCP relay (`KfRelay.exe`), allowing the debugger UI to run on the host while the driver operates inside the VM.
- **Full WPF-based UI**: Modern interface with disassembly, decompilation (via RetDec), registers, memory view, modules, imports, exports, strings, and more.

### 1.2 Plugin Architecture Overview

KernelFlirt provides a plugin SDK that allows developers to extend the debugger with custom functionality. The plugin system is built around a set of well-defined interfaces:

```
IKernelFlirtPlugin (your entry point)
    |
    +-- Initialize(IDebuggerApi api)
            |
            +-- IDebuggerApi (main API surface)
                    |
                    +-- IMemoryApi      (read/write memory, registers)
                    +-- IBreakpointApi  (software/hardware/memory breakpoints)
                    +-- ISymbolApi      (symbol resolution, modules)
                    +-- IProcessApi     (process/thread management, anti-debug)
                    +-- ILogApi         (log panel output)
                    +-- IUiApi          (UI elements, navigation, annotations)
```

Plugins are .NET 9 class libraries (DLLs) placed in the `plugins/` directory next to `KernelFlirt.exe`. They are discovered and loaded automatically at startup via reflection.

### 1.3 How Plugins Are Loaded

At startup, KernelFlirt scans the `plugins/` folder for DLL files. For each DLL:

1. The assembly is loaded into an `AssemblyLoadContext`.
2. All types implementing `IKernelFlirtPlugin` are discovered via reflection.
3. An instance of each plugin is created using the parameterless constructor.
4. `Initialize(IDebuggerApi api)` is called, passing the API surface.
5. The plugin's `Name`, `Description`, and `Version` are displayed in the plugin list.

On application exit, `Shutdown()` is called on every loaded plugin, giving each plugin a chance to save state and clean up resources.

### 1.4 Capabilities and Limitations

**What plugins can do:**
- Read and write target process memory
- Set and manage breakpoints (software, hardware, memory)
- Intercept and filter debug events before the UI processes them
- Control execution (continue, single-step, step over, step out, run to address)
- Resolve symbols and register custom function names
- Add custom UI panels (tabs) and menu items
- Enumerate processes, threads, and modules
- Apply anti-debug patches
- Allocate and free memory in the target process
- Modify instruction pointer (RIP) and stack pointer (RSP)
- Communicate with other plugins via shared data
- Persist state to disk

**Limitations:**
- Only x64 debugging is supported. The `Is32Bit` property exists for future WoW64 support but is not currently functional.
- Plugins cannot add custom toolbar buttons to the main toolbar (only menu items and tab panels).
- The decompiler (RetDec) is asynchronous; results are not immediately available after calling `DecompileFunction`.
- The NtQuerySystemInformation hook (`InstallNtQsiHook`) triggers PatchGuard BSOD after approximately 5-10 minutes.

---

## 2. Getting Started

### 2.1 Prerequisites

- **Visual Studio 2022** or later (or any .NET 9 SDK-compatible IDE)
- **.NET 9 SDK** (net9.0-windows target)
- **KernelFlirt source code** (for the SDK project reference)
- **Windows 10/11** host for development

### 2.2 Creating a Plugin Project

Create a new .NET 9 class library project. The simplest way is via the command line:

```bash
mkdir MyPlugin
cd MyPlugin
dotnet new classlib -n MyPlugin --framework net9.0-windows
```

Then modify the generated `.csproj` to add the required plugin settings and SDK reference.

### 2.3 The .csproj File Explained

A complete plugin `.csproj` file:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net9.0-windows</TargetFramework>
    <RootNamespace>MyPlugin</RootNamespace>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
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

**Key settings explained:**

| Setting | Purpose |
|---------|---------|
| `TargetFramework` = `net9.0-windows` | Required. Must match the host application's framework. |
| `EnableDynamicLoading` = `true` | **Critical.** Tells the build system to copy all dependencies to the output folder and prepare the assembly for dynamic loading via `AssemblyLoadContext`. Without this, your plugin may fail to load at runtime with missing dependency errors. |
| `UseWPF` = `true` | Required if your plugin creates any WPF UI elements (panels, buttons, menus, etc.). Can be omitted for headless plugins that only use the Log API. |
| `<Private>false</Private>` | Prevents the SDK DLL from being copied to your output. The host already has it loaded. Including a second copy would cause type identity conflicts. |
| `<ExcludeAssets>runtime</ExcludeAssets>` | Same purpose as `Private=false`; ensures the SDK assembly is not in your output folder. |

**Adding NuGet packages:**

If your plugin needs additional NuGet packages (e.g., `System.Text.Json`, a charting library, etc.), add them normally:

```xml
<ItemGroup>
  <PackageReference Include="System.Text.Json" Version="9.0.0" />
</ItemGroup>
```

The `EnableDynamicLoading` flag ensures that all dependencies are correctly resolved at runtime.

### 2.4 The IKernelFlirtPlugin Interface

Every plugin must implement the `IKernelFlirtPlugin` interface:

```csharp
namespace KernelFlirt.SDK;

public interface IKernelFlirtPlugin
{
    string Name { get; }
    string Description { get; }
    string Version { get; }

    void Initialize(IDebuggerApi api);
    void Shutdown();
}
```

| Member | Type | Description |
|--------|------|-------------|
| `Name` | `string` (property) | Display name shown in the KernelFlirt plugin list and log messages. Should be concise (e.g., "Bookmarks", "API Monitor", "Memory Scanner"). |
| `Description` | `string` (property) | A short one-line description of what the plugin does. Shown in the plugin list tooltip. |
| `Version` | `string` (property) | Version string in any format you choose (e.g., `"1.0"`, `"1.0.0"`, `"2.1-beta"`). |
| `Initialize(IDebuggerApi api)` | `void` method | Called once at application startup after the plugin DLL is loaded. This is where you save the `api` reference, register event handlers, add menu items, and create UI panels. The `api` object remains valid for the entire lifetime of the plugin. |
| `Shutdown()` | `void` method | Called when KernelFlirt is closing. Use this to save state, unsubscribe from events, cancel background tasks, and release resources. |

### 2.5 Minimal Plugin

The absolute minimum plugin that compiles and runs:

```csharp
using KernelFlirt.SDK;

namespace MyPlugin;

public class Plugin : IKernelFlirtPlugin
{
    public string Name => "My Plugin";
    public string Description => "A minimal example plugin";
    public string Version => "1.0";

    private IDebuggerApi _api = null!;

    public void Initialize(IDebuggerApi api)
    {
        _api = api;
        api.Log.Info("My Plugin loaded successfully!");
    }

    public void Shutdown() { }
}
```

When loaded, this plugin will print `[Plugin] My Plugin loaded successfully!` to the KernelFlirt log panel.

### 2.6 Building Your Plugin

If your plugin is part of the KernelFlirt solution, the main build script handles everything:

```bash
powershell -File build.ps1
```

For standalone plugin projects, use:

```bash
dotnet build -c Release
```

The output DLL will be in `bin/Release/net9.0-windows/`.

### 2.7 Deploying to the plugins/ Folder

Copy your built DLL (and any third-party dependency DLLs) to the `plugins/` folder next to `KernelFlirt.exe`:

```
KernelFlirt.exe
plugins/
    MyPlugin.dll
    SomeDependency.dll    (if any)
```

**Do NOT copy `KernelFlirt.SDK.dll`** to the plugins folder. The host application already has it loaded. Copying it would cause type identity conflicts (your plugin's `IKernelFlirtPlugin` would be a different type from the host's `IKernelFlirtPlugin`).

Restart KernelFlirt to load the new plugin. There is no hot-reload mechanism; the application must be restarted.

### 2.8 Debugging Your Plugin

To debug your plugin during development:

1. Set KernelFlirt as the startup project (or launch it manually).
2. Attach Visual Studio to the `KernelFlirt.exe` process (`Debug > Attach to Process`).
3. Set breakpoints in your plugin code.
4. Your plugin's breakpoints will be hit when the code executes.

Alternatively, use `System.Diagnostics.Debugger.Launch()` in your `Initialize` method during development to trigger a JIT debugger attach dialog.

---

## 3. Core API: IDebuggerApi

The `IDebuggerApi` interface is the central entry point for all plugin interactions with the debugger. It is passed to your plugin's `Initialize` method and provides access to all sub-APIs, debugger state, execution control, and events.

### 3.1 Sub-API Properties

| Property | Type | Description |
|----------|------|-------------|
| `Memory` | `IMemoryApi` | Read and write target process memory. Read and modify CPU registers. Allocate and free memory. Change page protections. |
| `Breakpoints` | `IBreakpointApi` | Set, remove, toggle, and enumerate breakpoints of all types (software, hardware execution, hardware write watchpoint, hardware read/write watchpoint, memory). |
| `Symbols` | `ISymbolApi` | Resolve addresses to symbol names and vice versa. Enumerate loaded modules (user-mode and kernel). Register and manage user-defined function names. |
| `Process` | `IProcessApi` | Enumerate processes and threads on the target machine. Suspend and resume threads. Access PEB. Anti-debug utility methods. |
| `Log` | `ILogApi` | Write informational, warning, and error messages to the KernelFlirt log panel. |
| `UI` | `IUiApi` | Add menu items and tool panels. Navigate disassembly. Manage annotations. Request decompilation. Cross-plugin data sharing. Module management for unpackers. |

### 3.2 Debugger State Properties

These read-only properties reflect the current state of the debugger:

| Property | Type | Description |
|----------|------|-------------|
| `IsConnected` | `bool` | `true` when the debugger is connected to a target (either a local driver or a remote VM via KfRelay). When `false`, most API calls will fail or return empty results. |
| `IsBreakState` | `bool` | `true` when the target process is suspended (paused). This happens after a breakpoint hit, single-step completion, manual pause (F12), or any debug exception. When `false`, the process is running and memory/register reads may be unreliable. |
| `TargetPid` | `uint` | The process ID of the currently debugged process. Returns `0` if no process is selected. This value is needed for most `IMemoryApi` and `IBreakpointApi` calls. |
| `SelectedThreadId` | `uint` | The thread ID of the currently selected thread in the UI. This is typically the thread that caused the last debug event. Used for register reads and single-stepping. |
| `Is32Bit` | `bool` | `true` if the target process is a 32-bit (WoW64) process. **Note:** WoW64 debugging is not currently supported; this property exists for future use. Always assume x64. |

**Usage pattern:**
```csharp
void DoSomething()
{
    if (!_api.IsConnected)
    {
        _api.Log.Warning("Not connected to target");
        return;
    }
    if (!_api.IsBreakState)
    {
        _api.Log.Warning("Target must be paused (break state)");
        return;
    }

    uint pid = _api.TargetPid;
    uint tid = _api.SelectedThreadId;
    // Now safe to read memory, registers, etc.
}
```

### 3.3 Execution Control Methods

These methods control the execution flow of the debugged process. All require `IsBreakState == true` (except `Pause`).

| Method | Signature | Description |
|--------|-----------|-------------|
| `Continue()` | `void Continue()` | Resume process execution. Equivalent to pressing Run (F9) in the UI. Can be called from an `OnDebugEventFilter` handler to auto-continue after processing an event. |
| `SingleStep()` | `void SingleStep()` | Execute exactly one instruction on the current thread, then break. Follows into `CALL` instructions (Step Into, F7). Sets the Trap Flag (TF) in RFLAGS. |
| `StepOver()` | `void StepOver()` | Step over the current instruction (F8). For `CALL` instructions, this sets a temporary breakpoint at the instruction following the `CALL` and resumes execution. For all other instructions, behaves identically to `SingleStep()`. |
| `StepOut()` | `void StepOut()` | Step out of the current function (Ctrl+F9). Reads the return address from `[RSP]` (top of stack) and sets a temporary breakpoint at that address, then resumes execution. |
| `RunToCursor(ulong address)` | `void RunToCursor(ulong address)` | Run to the specified address (F4 / Run to Cursor). Sets a temporary breakpoint at `address` and resumes execution. The breakpoint is automatically removed when hit. |
| `SkipInstruction()` | `void SkipInstruction()` | Skip the current instruction without executing it (Ctrl+F8). Moves RIP past the current instruction by advancing it by the instruction's length. Useful for skipping over anti-debug checks or problematic instructions. |
| `Pause()` | `void Pause()` | Pause a running process (F12). Suspends all threads. This is the only execution control method that can be called when `IsBreakState == false`. |

**Example -- auto-continue on a breakpoint:**
```csharp
_api.OnDebugEventFilter += (evt) =>
{
    if (evt.Type == PluginDebugEventType.Breakpoint && evt.Address == _myBpAddress)
    {
        // Log the hit and continue automatically
        _api.Log.Info($"BP hit at {evt.Address:X16}, auto-continuing");
        _api.Continue();
        return true; // suppress UI break
    }
    return false;
};
```

### 3.4 Events

The `IDebuggerApi` provides six events that plugins can subscribe to. Event subscription is typically done in `Initialize()`.

#### OnConnected

```csharp
event Action? OnConnected;
```

Fires when the debugger establishes a connection to the target (local driver or remote VM). Use this to initialize state that depends on having a connection, such as reading kernel module lists.

**Thread:** UI thread.

```csharp
api.OnConnected += () =>
{
    api.Log.Info("Connected! Kernel modules available.");
    var kmods = api.Symbols.GetKernelModules();
    api.Log.Info($"Found {kmods.Count} kernel modules");
};
```

#### OnDisconnected

```csharp
event Action? OnDisconnected;
```

Fires when the debugger disconnects from the target. Use this to clean up connection-dependent state.

**Thread:** UI thread.

#### OnBreakStateEntered

```csharp
event Action? OnBreakStateEntered;
```

Fires when the target process transitions from running to paused. This happens after any debug event (breakpoint hit, single-step, exception, manual pause). This is the correct time to read registers, memory, and update UI panels.

**Thread:** UI thread.

**Important:** This event fires AFTER the debug event has been processed. If an `OnDebugEventFilter` handler returns `true` (suppressing the event), `OnBreakStateEntered` does NOT fire.

```csharp
api.OnBreakStateEntered += () =>
{
    Application.Current.Dispatcher.BeginInvoke(() =>
    {
        var regs = api.Memory.ReadRegisters(api.TargetPid, api.SelectedThreadId);
        var rip = regs.First(r => r.Name == "RIP");
        UpdateMyPanel(rip.Value);
    });
};
```

#### OnBreakStateExited

```csharp
event Action? OnBreakStateExited;
```

Fires when the target process transitions from paused to running. Use this to disable UI elements that only work in break state.

**Thread:** UI thread.

#### OnBeforeRun

```csharp
event Action? OnBeforeRun;
```

Fires just before the process resumes execution (when the user presses F9, or when `Continue()` is called). This is the ideal place to set or adjust breakpoints before execution resumes.

**Thread:** UI thread.

```csharp
api.OnBeforeRun += () =>
{
    // Ensure our monitoring breakpoint is always set before running
    var bps = api.Breakpoints.GetAll();
    if (!bps.Any(b => b.Address == _monitorAddress))
    {
        api.Breakpoints.SetBreakpoint(api.TargetPid, 0, _monitorAddress,
            PluginBreakpointType.Software);
    }
};
```

#### OnDebugEvent

```csharp
event Action<PluginDebugEvent>? OnDebugEvent;
```

Fires for every debug event AFTER the UI has processed it. This is an informational event -- you cannot suppress it or control how the process resumes. Use this for logging and monitoring.

**Thread:** UI thread.

```csharp
api.OnDebugEvent += (evt) =>
{
    api.Log.Info($"Debug event: {evt.Type} at {evt.Address:X16} (TID={evt.ThreadId})");
};
```

#### OnDebugEventFilter

```csharp
event Func<PluginDebugEvent, bool>? OnDebugEventFilter;
```

**The most powerful plugin mechanism.** Fires BEFORE the UI processes a debug event. The handler receives a `PluginDebugEvent` object with both read-only event information and writable control fields.

- Return `true` to **suppress** the event -- the UI will not see it, and `OnBreakStateEntered` will not fire. The plugin is responsible for resuming execution (via `Continue()`, `SingleStep()`, or by setting `ContinueMode`).
- Return `false` to let the UI handle the event normally.

**Thread:** Background thread (NOT the UI thread). All `IUiApi` methods are auto-dispatched, but if you need to update your own WPF controls, you must use `Dispatcher.BeginInvoke`.

See [Chapter 12: Debug Events and Filters](#12-debug-events-and-filters) for comprehensive documentation.

---

## 4. Memory API: IMemoryApi

The `IMemoryApi` interface provides low-level access to the target process's virtual memory and CPU registers. All memory operations go through the kernel driver and work even on memory that is not accessible from user mode.

### 4.1 ReadMemory

```csharp
byte[]? ReadMemory(uint pid, ulong address, uint size);
```

Read `size` bytes starting at virtual address `address` from the process identified by `pid`.

**Parameters:**

| Parameter | Type | Description |
|-----------|------|-------------|
| `pid` | `uint` | Process ID. Use `_api.TargetPid` for the currently debugged process. |
| `address` | `ulong` | Virtual address to read from. Can be user-mode (e.g., `0x00007FF...`) or kernel-mode (e.g., `0xFFFFF800...`). |
| `size` | `uint` | Number of bytes to read. Practical limit depends on available memory; typical reads are 1 byte to several megabytes. |

**Returns:** `byte[]?` -- byte array of exactly `size` bytes on success, or `null` if the read failed (invalid address, paged-out memory, process does not exist).

**Examples:**

```csharp
// Read a single byte
byte[]? b = _api.Memory.ReadMemory(pid, address, 1);
if (b != null)
    _api.Log.Info($"Byte at {address:X16}: {b[0]:X2}");

// Read a QWORD (8 bytes) and convert to ulong
byte[]? data = _api.Memory.ReadMemory(pid, address, 8);
if (data != null)
{
    ulong value = BitConverter.ToUInt64(data, 0);
    _api.Log.Info($"QWORD at {address:X16}: {value:X16}");
}

// Read a null-terminated ASCII string (up to 256 bytes)
byte[]? strData = _api.Memory.ReadMemory(pid, address, 256);
if (strData != null)
{
    int nullIdx = Array.IndexOf(strData, (byte)0);
    string text = System.Text.Encoding.ASCII.GetString(strData, 0,
        nullIdx >= 0 ? nullIdx : strData.Length);
}

// Read a PE header
byte[]? header = _api.Memory.ReadMemory(pid, moduleBase, 0x1000);
if (header != null && header[0] == 'M' && header[1] == 'Z')
    _api.Log.Info("Valid PE header");

// Read a large memory block (e.g., entire .text section)
byte[]? section = _api.Memory.ReadMemory(pid, textBase, textSize);
```

### 4.2 WriteMemory

```csharp
bool WriteMemory(uint pid, ulong address, byte[] data);
```

Write `data` to virtual address `address` in the process identified by `pid`. The write goes through the kernel driver and bypasses user-mode page protections (the driver handles copy-on-write and read-only pages internally).

**Parameters:**

| Parameter | Type | Description |
|-----------|------|-------------|
| `pid` | `uint` | Process ID. |
| `address` | `ulong` | Virtual address to write to. |
| `data` | `byte[]` | Byte array containing the data to write. The entire array is written. |

**Returns:** `bool` -- `true` if the write succeeded, `false` if it failed.

**Examples:**

```csharp
// NOP out a 2-byte instruction (e.g., JZ short)
bool ok = _api.Memory.WriteMemory(pid, address, new byte[] { 0x90, 0x90 });

// Write a QWORD value
ulong value = 0xDEADBEEFCAFEBABE;
_api.Memory.WriteMemory(pid, address, BitConverter.GetBytes(value));

// Patch a JZ to JMP (unconditional)
_api.Memory.WriteMemory(pid, address, new byte[] { 0xEB }); // short JMP

// Write a null-terminated ASCII string
string text = "patched\0";
_api.Memory.WriteMemory(pid, address, System.Text.Encoding.ASCII.GetBytes(text));
```

### 4.3 ReadRegisters

```csharp
IReadOnlyList<PluginRegister> ReadRegisters(uint pid, uint tid);
```

Read all CPU registers of the specified thread. The target must be in break state.

**Parameters:**

| Parameter | Type | Description |
|-----------|------|-------------|
| `pid` | `uint` | Process ID (use `_api.TargetPid`). |
| `tid` | `uint` | Thread ID (use `_api.SelectedThreadId` for the current thread, or enumerate threads via `IProcessApi`). |

**Returns:** `IReadOnlyList<PluginRegister>` -- a list of register entries. Each entry has a `Name`, `Value`, and `IsFlag` property.

The returned list includes:
- **General purpose:** RAX, RBX, RCX, RDX, RSI, RDI, RBP, RSP, R8-R15
- **Instruction pointer:** RIP
- **Flags register:** RFLAGS
- **Individual flags:** CF, PF, AF, ZF, SF, TF, IF, DF, OF (with `IsFlag = true`)
- **Debug registers:** DR0, DR1, DR2, DR3, DR6, DR7
- **Segment registers:** CS, DS, ES, FS, GS, SS

**Examples:**

```csharp
var regs = _api.Memory.ReadRegisters(_api.TargetPid, _api.SelectedThreadId);

// Get RIP
ulong rip = regs.First(r => r.Name == "RIP").Value;

// Get all general-purpose registers
foreach (var reg in regs.Where(r => !r.IsFlag))
    _api.Log.Info($"  {reg.Name} = {reg.Value:X16}");

// Check the Zero Flag
bool zf = regs.First(r => r.Name == "ZF").Value != 0;
_api.Log.Info($"Zero Flag: {zf}");

// Read function arguments (Windows x64 calling convention)
ulong arg1_rcx = regs.First(r => r.Name == "RCX").Value;
ulong arg2_rdx = regs.First(r => r.Name == "RDX").Value;
ulong arg3_r8  = regs.First(r => r.Name == "R8").Value;
ulong arg4_r9  = regs.First(r => r.Name == "R9").Value;

// Read RSP for stack-based arguments (arg5+)
ulong rsp = regs.First(r => r.Name == "RSP").Value;
// Arg5 is at [RSP+0x28] (after return address and shadow space)
byte[]? arg5data = _api.Memory.ReadMemory(_api.TargetPid, rsp + 0x28, 8);
```

### 4.4 WriteRip

```csharp
bool WriteRip(uint pid, uint tid, ulong newRip);
```

Set the instruction pointer (RIP) of the specified thread to a new value. The thread must be suspended (break state).

**Parameters:**

| Parameter | Type | Description |
|-----------|------|-------------|
| `pid` | `uint` | Process ID. |
| `tid` | `uint` | Thread ID. |
| `newRip` | `ulong` | New value for RIP. |

**Returns:** `bool` -- `true` on success.

**Use cases:**
- Redirecting execution to skip over anti-debug checks.
- Jumping to a specific function for analysis.
- Implementing trampoline-based hooks.

```csharp
// Skip over an anti-debug call by jumping past it
_api.Memory.WriteRip(_api.TargetPid, _api.SelectedThreadId, addressAfterAntiDebugCall);
```

### 4.5 WriteRipAndRsp

```csharp
bool WriteRipAndRsp(uint tid, ulong newRip, ulong newRsp);
```

Set both RIP and RSP atomically. This is important when you need to redirect execution AND fix the stack pointer (e.g., after simulating a function return).

**Parameters:**

| Parameter | Type | Description |
|-----------|------|-------------|
| `tid` | `uint` | Thread ID. Note: unlike `WriteRip`, this method does not take a `pid` parameter -- it uses the current target process. |
| `newRip` | `ulong` | New value for RIP. |
| `newRsp` | `ulong` | New value for RSP. |

**Returns:** `bool` -- `true` on success.

**Example -- simulate a function return:**
```csharp
var regs = _api.Memory.ReadRegisters(_api.TargetPid, _api.SelectedThreadId);
ulong rsp = regs.First(r => r.Name == "RSP").Value;

// Read return address from [RSP]
byte[]? retAddrData = _api.Memory.ReadMemory(_api.TargetPid, rsp, 8);
ulong retAddr = BitConverter.ToUInt64(retAddrData!, 0);

// "Return" from the function by setting RIP to return address
// and popping RSP (RSP += 8)
_api.Memory.WriteRipAndRsp(_api.SelectedThreadId, retAddr, rsp + 8);
```

### 4.6 ProtectMemory

```csharp
(bool ok, uint oldProtection) ProtectMemory(uint pid, ulong address, uint size, uint newProtection);
```

Change the virtual memory protection of a region. This is the kernel-level equivalent of `VirtualProtectEx`.

**Parameters:**

| Parameter | Type | Description |
|-----------|------|-------------|
| `pid` | `uint` | Process ID. |
| `address` | `ulong` | Start address of the memory region. |
| `size` | `uint` | Size of the region in bytes. |
| `newProtection` | `uint` | New protection flags. Uses Windows memory protection constants (see [Appendix B](#23-appendix-b-windows-memory-protection-constants)). |

**Returns:** A tuple of `(bool ok, uint oldProtection)`:
- `ok`: `true` if the protection was changed successfully.
- `oldProtection`: The previous protection value (can be used to restore later).

**Common protection constants:**

| Constant | Value | Description |
|----------|-------|-------------|
| `PAGE_NOACCESS` | `0x01` | No access allowed. Triggers AV on any access. Used for guard pages. |
| `PAGE_READONLY` | `0x02` | Read-only. |
| `PAGE_READWRITE` | `0x04` | Read and write. |
| `PAGE_EXECUTE` | `0x10` | Execute only. |
| `PAGE_EXECUTE_READ` | `0x20` | Execute and read. |
| `PAGE_EXECUTE_READWRITE` | `0x40` | Full access (execute, read, write). |

**Example -- guard page tracing:**
```csharp
// Make a page non-accessible to catch accesses
var (ok, oldProt) = _api.Memory.ProtectMemory(pid, pageBase, 0x1000, 0x01);
if (ok)
    _api.Log.Info($"Guard set. Old protection: {oldProt:X}");

// ... later, in the AV handler, restore access:
_api.Memory.ProtectMemory(pid, pageBase, 0x1000, oldProt);
```

### 4.7 AllocateMemory

```csharp
ulong AllocateMemory(uint pid, ulong size);
```

Allocate a block of virtual memory in the target process. The allocated memory is committed and has `PAGE_EXECUTE_READWRITE` protection.

**Parameters:**

| Parameter | Type | Description |
|-----------|------|-------------|
| `pid` | `uint` | Process ID. |
| `size` | `ulong` | Size to allocate in bytes. Will be rounded up to page granularity (4KB). |

**Returns:** `ulong` -- the base address of the allocated memory block, or `0` on failure.

**Example:**
```csharp
// Allocate a code cave for a hook trampoline
ulong cave = _api.Memory.AllocateMemory(_api.TargetPid, 0x1000); // 4KB
if (cave != 0)
{
    // Write trampoline code
    byte[] trampoline = { 0x48, 0xB8, /* ... mov rax, addr ... */, 0xFF, 0xE0 }; // jmp rax
    _api.Memory.WriteMemory(_api.TargetPid, cave, trampoline);
    _api.Log.Info($"Code cave at {cave:X16}");
}
```

### 4.8 FreeMemory

```csharp
bool FreeMemory(uint pid, ulong address);
```

Free a previously allocated memory block.

**Parameters:**

| Parameter | Type | Description |
|-----------|------|-------------|
| `pid` | `uint` | Process ID. |
| `address` | `ulong` | Base address returned by `AllocateMemory`. |

**Returns:** `bool` -- `true` on success.

---

## 5. Breakpoint API: IBreakpointApi

The `IBreakpointApi` interface provides full control over breakpoints of all types.

### 5.1 SetBreakpoint

```csharp
uint? SetBreakpoint(uint pid, uint tid, ulong address, PluginBreakpointType type, uint length = 1);
```

Set a breakpoint at the specified address.

**Parameters:**

| Parameter | Type | Description |
|-----------|------|-------------|
| `pid` | `uint` | Process ID. |
| `tid` | `uint` | Thread ID. For software breakpoints, this is usually `0` (all threads). For hardware breakpoints, this specifies which thread's debug registers to use (each thread has independent DR0-DR3). |
| `address` | `ulong` | Address where the breakpoint is set. For software breakpoints, this is the instruction address. For hardware watchpoints, this is the data address to watch. |
| `type` | `PluginBreakpointType` | The type of breakpoint (see [Section 5.5](#55-breakpoint-types)). |
| `length` | `uint` | Length of the watched region in bytes. Only relevant for hardware watchpoints (`HwWrite`, `HwReadWrite`). Valid values: `1`, `2`, `4`, `8`. Default is `1`. Ignored for `Software` and `Hardware` (execution) breakpoints. |

**Returns:** `uint?` -- a breakpoint handle (non-zero integer) on success, or `null` if the breakpoint could not be set (e.g., all 4 hardware debug registers are in use, invalid address).

**Examples:**

```csharp
uint pid = _api.TargetPid;
uint tid = _api.SelectedThreadId;

// Software breakpoint (INT3)
uint? swBp = _api.Breakpoints.SetBreakpoint(pid, 0, 0x140001000,
    PluginBreakpointType.Software);

// Hardware execution breakpoint (uses DR0-DR3)
uint? hwExec = _api.Breakpoints.SetBreakpoint(pid, tid, 0x140001000,
    PluginBreakpointType.Hardware);

// Hardware write watchpoint: break when a DWORD at address is written
uint? hwWrite = _api.Breakpoints.SetBreakpoint(pid, tid, 0x140008000,
    PluginBreakpointType.HwWrite, 4);

// Hardware read/write watchpoint: break on any access to 8 bytes
uint? hwRW = _api.Breakpoints.SetBreakpoint(pid, tid, 0x140008000,
    PluginBreakpointType.HwReadWrite, 8);

// Memory breakpoint (page protection based)
uint? memBp = _api.Breakpoints.SetBreakpoint(pid, 0, 0x140020000,
    PluginBreakpointType.Memory);
```

### 5.2 RemoveBreakpoint

```csharp
bool RemoveBreakpoint(uint handle);
```

Remove a breakpoint by its handle.

**Parameters:**

| Parameter | Type | Description |
|-----------|------|-------------|
| `handle` | `uint` | The breakpoint handle returned by `SetBreakpoint`. |

**Returns:** `bool` -- `true` if the breakpoint was found and removed, `false` if the handle was invalid.

```csharp
if (swBp.HasValue)
{
    _api.Breakpoints.RemoveBreakpoint(swBp.Value);
    _api.Log.Info("Breakpoint removed");
}
```

### 5.3 GetAll

```csharp
IReadOnlyList<PluginBreakpoint> GetAll();
```

Returns a snapshot of all currently active breakpoints, including those set by the UI and other plugins.

**Returns:** `IReadOnlyList<PluginBreakpoint>` -- list of all breakpoints.

```csharp
var bps = _api.Breakpoints.GetAll();
foreach (var bp in bps)
{
    _api.Log.Info($"BP #{bp.Handle}: {bp.Address:X16} Type={bp.Type} " +
        $"Enabled={bp.Enabled} Hits={bp.HitCount}");
}
```

### 5.4 ToggleBreakpoint

```csharp
void ToggleBreakpoint(ulong address, PluginBreakpointType type = PluginBreakpointType.Software);
```

Toggle a breakpoint at the specified address. If a breakpoint exists at this address, it is removed. If no breakpoint exists, one is added.

**Parameters:**

| Parameter | Type | Description |
|-----------|------|-------------|
| `address` | `ulong` | The address to toggle. |
| `type` | `PluginBreakpointType` | Type of breakpoint to create if toggling on. Defaults to `Software`. |

**Key difference from `SetBreakpoint`:** `ToggleBreakpoint` operates through the UI layer. It updates the breakpoint list view, disassembly view markers (red dots), and the driver state all at once. Use this method when you want breakpoint changes to be visible and consistent in the UI.

`SetBreakpoint` is lower-level -- it sets the breakpoint in the driver but does not update the UI markers. Use `SetBreakpoint` for programmatic breakpoints in `OnDebugEventFilter` handlers where you need precise control.

**Example -- restoring session breakpoints:**
```csharp
// Restore breakpoints from a saved session
foreach (var savedBp in sessionData.Breakpoints)
{
    _api.Breakpoints.ToggleBreakpoint(savedBp.Address,
        (PluginBreakpointType)savedBp.Type);
}
```

### 5.5 Breakpoint Types

The `PluginBreakpointType` enumeration defines all supported breakpoint types:

| Value | Name | Int | Mechanism | Description |
|-------|------|-----|-----------|-------------|
| `Software` | Software | 0 | `INT3` (0xCC) | The classic software breakpoint. The driver saves the original byte at the address, replaces it with `0xCC`, and restores it when the breakpoint is hit. Unlimited count. Works on any executable address. |
| `Hardware` | Hardware | 1 | DR0-DR3 (execute) | Hardware execution breakpoint. Uses x86 debug registers. Limited to 4 per thread (DR0, DR1, DR2, DR3). Does not modify memory. Cannot be detected by integrity checks. |
| `HwWrite` | HwWrite | 2 | DR0-DR3 (write) | Hardware data write watchpoint. Triggers when the CPU writes to the specified address. The `length` parameter controls the watched range (1, 2, 4, or 8 bytes). Limited to 4 per thread. |
| `HwReadWrite` | HwReadWrite | 3 | DR0-DR3 (read/write) | Hardware data read/write watchpoint. Triggers on any data access (read or write) to the specified address. Same limitations as `HwWrite`. |
| `Memory` | Memory | 4 | Page protection | Memory breakpoint based on page protection manipulation. The driver changes the page protection to `PAGE_NOACCESS` (or `PAGE_GUARD`) and catches the resulting access violation. Can cover large regions. Slower than hardware watchpoints but no DR register limit. |

---

## 6. Symbol API: ISymbolApi

The `ISymbolApi` interface provides symbol resolution powered by dbghelp.dll and the Microsoft symbol server.

### 6.1 ResolveAddress

```csharp
string? ResolveAddress(ulong address);
```

Resolve a virtual address to a human-readable symbol name.

**Parameters:**

| Parameter | Type | Description |
|-----------|------|-------------|
| `address` | `ulong` | The virtual address to resolve. |

**Returns:** `string?` -- the symbol name in the format `"module!function+0xOffset"` (e.g., `"kernel32!CreateFileW"`, `"ntdll!NtQueryInformationProcess+0x15"`), or `null` if no symbol could be resolved.

Resolution order:
1. User-defined functions registered via `RegisterFunction` (checked first).
2. Export symbols from loaded modules.
3. PDB symbols (if available from the symbol server).

```csharp
string? name = _api.Symbols.ResolveAddress(0x00007FFE199B0000);
// Returns: "KERNELBASE!CreateFileW" (if symbols are loaded)

string? name2 = _api.Symbols.ResolveAddress(0x00007FFE199B0042);
// Returns: "KERNELBASE!CreateFileW+0x42"
```

### 6.2 ResolveNameToAddress

```csharp
ulong ResolveNameToAddress(string name);
```

Resolve a symbol name to its virtual address.

**Parameters:**

| Parameter | Type | Description |
|-----------|------|-------------|
| `name` | `string` | Symbol name in the format `"module!function"`. The module name can include or omit the `.dll` extension (both `"kernel32!Sleep"` and `"kernel32.dll!Sleep"` work). |

**Returns:** `ulong` -- the address of the symbol, or `0` if not found.

```csharp
ulong sleep = _api.Symbols.ResolveNameToAddress("kernel32!Sleep");
ulong ntqip = _api.Symbols.ResolveNameToAddress("ntdll!NtQueryInformationProcess");

if (sleep != 0)
    _api.Breakpoints.SetBreakpoint(_api.TargetPid, 0, sleep,
        PluginBreakpointType.Software);
```

### 6.3 GetModules

```csharp
IReadOnlyList<PluginModuleInfo> GetModules();
```

Get all loaded user-mode modules of the currently debugged process.

**Returns:** `IReadOnlyList<PluginModuleInfo>` -- list of modules. The first module in the list is typically the main executable.

Each `PluginModuleInfo` contains:
- `BaseAddress` (`ulong`): Module base address in the process's virtual address space.
- `Size` (`uint`): Size of the module in bytes.
- `Name` (`string`): Module file name (e.g., `"notepad.exe"`, `"kernel32.dll"`).

```csharp
var modules = _api.Symbols.GetModules();
foreach (var mod in modules)
{
    _api.Log.Info($"  {mod.Name}: {mod.BaseAddress:X16} - " +
        $"{mod.BaseAddress + mod.Size:X16} ({mod.Size:X} bytes)");
}

// Find which module contains an address
PluginModuleInfo? FindModule(ulong addr)
{
    return modules.FirstOrDefault(m =>
        addr >= m.BaseAddress && addr < m.BaseAddress + m.Size);
}
```

### 6.4 GetKernelModules

```csharp
IReadOnlyList<PluginKernelModuleInfo> GetKernelModules();
```

Get all loaded kernel-mode modules (drivers). Requires a connection to the target.

**Returns:** `IReadOnlyList<PluginKernelModuleInfo>` -- list of kernel modules.

Each `PluginKernelModuleInfo` contains:
- `BaseAddress` (`ulong`): Module base in kernel virtual address space (high addresses, e.g., `0xFFFFF800...`).
- `Size` (`uint`): Module size.
- `LoadOrder` (`ushort`): Load order index.
- `Name` (`string`): Module name (e.g., `"ntoskrnl.exe"`, `"win32kfull.sys"`).

```csharp
var kmods = _api.Symbols.GetKernelModules();
var ntoskrnl = kmods.FirstOrDefault(m =>
    m.Name.Contains("ntoskrnl", StringComparison.OrdinalIgnoreCase));
if (ntoskrnl != null)
    _api.Log.Info($"ntoskrnl at {ntoskrnl.BaseAddress:X16}");
```

### 6.5 RegisterFunction

```csharp
void RegisterFunction(ulong address, string? name, uint size = 0);
```

Register a user-defined function name at a given address. This is used to label functions discovered during analysis (especially for stripped binaries, packed executables, or dynamically generated code).

**Parameters:**

| Parameter | Type | Description |
|-----------|------|-------------|
| `address` | `ulong` | The start address of the function. |
| `name` | `string?` | The name to assign. Set to `null` to **unregister** a previously registered function. |
| `size` | `uint` | **CRITICAL parameter.** The size of the function in bytes. When set to a non-zero value, `ResolveAddress` will return this function name for ALL addresses within the range `[address, address + size)`. For example, if you register `"MyFunction"` at `0x1000` with size `0x50`, then `ResolveAddress(0x1020)` will return `"MyFunction+0x20"`. If `size` is `0`, only the exact address will resolve. Default is `0`. |

**Why the `size` parameter is critical:**

Without a proper `size` value, any address inside the function body (other than the exact start address) will NOT resolve to your registered function name. This means the disassembly view, call stacks, and other UI elements will show raw hex addresses instead of meaningful names.

```csharp
// Register a function with its size -- ResolveAddress works for the entire range
_api.Symbols.RegisterFunction(0x140005000, "DecryptString", 0x120);
// Now ResolveAddress(0x140005050) returns "DecryptString+0x50"

// Register without size -- only the exact address resolves
_api.Symbols.RegisterFunction(0x140005000, "DecryptString");
// ResolveAddress(0x140005050) returns null!

// Unregister a function
_api.Symbols.RegisterFunction(0x140005000, null);
```

### 6.6 GetRegisteredFunctions

```csharp
IReadOnlyList<PluginFunctionEntry> GetRegisteredFunctions();
```

Returns all user-defined functions registered via `RegisterFunction`.

**Returns:** `IReadOnlyList<PluginFunctionEntry>` -- list of function entries with `Address`, `Name`, and `Size`.

```csharp
var funcs = _api.Symbols.GetRegisteredFunctions();
_api.Log.Info($"{funcs.Count} user functions registered:");
foreach (var f in funcs)
    _api.Log.Info($"  {f.Name} @ {f.Address:X16} (size: {f.Size:X})");
```

---

## 7. Process API: IProcessApi

The `IProcessApi` interface provides process and thread management, plus anti-debug utility methods.

### 7.1 EnumProcesses

```csharp
IReadOnlyList<PluginProcessInfo> EnumProcesses();
```

Enumerate all processes on the target machine. This is a kernel-level enumeration (reads EPROCESS linked list) and includes system processes.

**Returns:** `IReadOnlyList<PluginProcessInfo>` -- list of processes.

Each `PluginProcessInfo` contains:
- `ProcessId` (`uint`): Process ID.
- `SessionId` (`uint`): Terminal Services session ID (0 for services, 1+ for user sessions).
- `Name` (`string`): Process name (e.g., `"notepad.exe"`, `"System"`).

```csharp
var procs = _api.Process.EnumProcesses();
foreach (var p in procs)
    _api.Log.Info($"PID {p.ProcessId}: {p.Name} (session {p.SessionId})");
```

### 7.2 EnumThreads

```csharp
IReadOnlyList<PluginThreadInfo> EnumThreads(uint pid);
```

Enumerate all threads of a specific process.

**Parameters:**

| Parameter | Type | Description |
|-----------|------|-------------|
| `pid` | `uint` | Process ID to enumerate threads for. |

**Returns:** `IReadOnlyList<PluginThreadInfo>` -- list of threads.

Each `PluginThreadInfo` contains:
- `ThreadId` (`uint`): Thread ID (TID).
- `StartAddress` (`ulong`): The address of the thread's start function.
- `State` (`uint`): Thread state flags (Windows KTHREAD state).
- `Priority` (`uint`): Thread priority.

```csharp
var threads = _api.Process.EnumThreads(_api.TargetPid);
foreach (var t in threads)
{
    string startName = _api.Symbols.ResolveAddress(t.StartAddress) ?? $"{t.StartAddress:X16}";
    _api.Log.Info($"  TID {t.ThreadId}: start={startName} prio={t.Priority}");
}
```

### 7.3 SuspendThread / ResumeThread

```csharp
bool SuspendThread(uint tid);
bool ResumeThread(uint tid);
```

Suspend or resume a specific thread.

**Parameters:**

| Parameter | Type | Description |
|-----------|------|-------------|
| `tid` | `uint` | Thread ID to suspend or resume. |

**Returns:** `bool` -- `true` on success.

**Use case:** Freeze threads during analysis to prevent race conditions, or isolate a specific thread for debugging.

```csharp
// Suspend all threads except the current one
var threads = _api.Process.EnumThreads(_api.TargetPid);
foreach (var t in threads)
{
    if (t.ThreadId != _api.SelectedThreadId)
        _api.Process.SuspendThread(t.ThreadId);
}
```

### 7.4 GetPebAddress

```csharp
(ulong PebAddress, ulong Peb32Address) GetPebAddress(uint pid);
```

Get the PEB (Process Environment Block) address for a process.

**Parameters:**

| Parameter | Type | Description |
|-----------|------|-------------|
| `pid` | `uint` | Process ID. |

**Returns:** A tuple:
- `PebAddress` (`ulong`): The 64-bit PEB address.
- `Peb32Address` (`ulong`): The WoW64 (32-bit) PEB address. `0` for native 64-bit processes.

```csharp
var (peb64, peb32) = _api.Process.GetPebAddress(_api.TargetPid);
_api.Log.Info($"PEB64: {peb64:X16}");

// Read PEB.BeingDebugged (offset 0x02 in PEB)
byte[]? bd = _api.Memory.ReadMemory(_api.TargetPid, peb64 + 2, 1);
if (bd != null)
    _api.Log.Info($"PEB.BeingDebugged = {bd[0]}");
```

### 7.5 Anti-Debug Methods

These methods provide kernel-level anti-anti-debug capabilities. They operate directly on kernel structures and are invisible to the target process.

#### ClearDebugPort

```csharp
bool ClearDebugPort(uint pid);
```

Zeroes out the `DebugPort` field in the process's `EPROCESS` kernel structure. This defeats multiple anti-debug checks:
- `NtQueryInformationProcess(ProcessDebugPort)` returns 0.
- `NtQueryInformationProcess(ProcessDebugObjectHandle)` fails.
- `NtQueryInformationProcess(ProcessDebugFlags)` returns 1 (not debugged).
- `NtClose` with invalid handle trick (no longer generates debug exception).

**Parameters:** `pid` (`uint`) -- Process ID.
**Returns:** `bool` -- `true` on success.

#### ClearThreadHide

```csharp
bool ClearThreadHide(uint pid);
```

Clears the `HideFromDebugger` bit in `CrossThreadFlags` for ALL threads in the process. This defeats `NtSetInformationThread(ThreadHideFromDebugger)` -- a technique where the target hides threads from debugger events.

**Parameters:** `pid` (`uint`) -- Process ID.
**Returns:** `bool` -- `true` on success.

#### InstallNtQsiHook / RemoveNtQsiHook

```csharp
bool InstallNtQsiHook();
bool RemoveNtQsiHook();
```

Install or remove an inline hook on `NtQuerySystemInformation` in `ntoskrnl.exe` to spoof `SystemKernelDebuggerInformation` (information class 0x23). When hooked, the function reports that no kernel debugger is present.

**WARNING:** This patches kernel code and **triggers PatchGuard BSOD** after approximately 5-10 minutes. Use only for short analysis sessions.

**Returns:** `bool` -- `true` on success.

#### ProbeNtQsiHook

```csharp
string ProbeNtQsiHook();
```

Diagnostic method that probes the `NtQuerySystemInformation` function bytes and returns a string with the current state (hooked or not, byte values, disassembly snippet).

**Returns:** `string` -- diagnostic information.

#### SetSpoofSharedUserData

```csharp
bool SetSpoofSharedUserData(bool enable);
```

Enable or disable spoofing of `SharedUserData` (`KUSER_SHARED_DATA` at `0x7FFE0000`). When enabled, the `KdDebuggerEnabled` field in shared user data is set to `0`, hiding the kernel debugger presence from user-mode queries.

**Parameters:** `enable` (`bool`) -- `true` to enable spoofing, `false` to disable.
**Returns:** `bool` -- `true` on success.

---

## 8. Log API: ILogApi

The `ILogApi` interface provides a simple way to write messages to the KernelFlirt log panel.

### 8.1 Info, Warning, Error

```csharp
void Info(string message);
void Warning(string message);
void Error(string message);
```

| Method | Prefix | Color | Description |
|--------|--------|-------|-------------|
| `Info(string message)` | `[Plugin]` | Default (white/light gray) | General informational messages. Use for status updates, progress reports, and debug output. |
| `Warning(string message)` | `[Plugin] WARNING:` | Yellow | Warnings about potential issues. Use when something unexpected happened but the plugin can continue. |
| `Error(string message)` | `[Plugin] ERROR:` | Red | Error messages. Use when an operation failed and the user should be aware. |

**Thread safety:** These methods are thread-safe and can be called from any thread. The messages are automatically dispatched to the UI thread for display.

```csharp
_api.Log.Info("Starting analysis...");
_api.Log.Warning("Symbol resolution failed for module xyz.dll");
_api.Log.Error("Memory read failed at 0x140001000");

// Include context in messages
_api.Log.Info($"[MyPlugin] Found {count} cross-references to {address:X16}");
```

---

## 9. UI API: IUiApi

The `IUiApi` interface allows plugins to interact with the KernelFlirt user interface. All methods are thread-safe -- they are automatically dispatched to the UI thread if called from a background thread.

### 9.1 NavigateDisassembly

```csharp
void NavigateDisassembly(ulong address);
```

Scroll the disassembly view to show the instruction at the specified address. This also updates the current position in the navigation history (allowing `DisasmGoBack` to return to the previous location).

**Parameters:**

| Parameter | Type | Description |
|-----------|------|-------------|
| `address` | `ulong` | Address to navigate to. |

```csharp
// Double-click handler: navigate to the selected address
_grid.MouseDoubleClick += (_, _) =>
{
    if (_grid.SelectedItem is MyResult result)
        _api.UI.NavigateDisassembly(result.Address);
};
```

### 9.2 DisasmGoBack

```csharp
void DisasmGoBack();
```

Navigate back to the previous disassembly location (undo the last `NavigateDisassembly` call). Works like the "Back" button in a web browser.

### 9.3 AddMenuItem

```csharp
void AddMenuItem(string header, Action callback);
```

Add a menu item to the **Plugins** menu in the KernelFlirt menu bar.

**Parameters:**

| Parameter | Type | Description |
|-----------|------|-------------|
| `header` | `string` | The menu item text. Use an underscore `_` before a letter to create a keyboard accelerator (e.g., `"Find _Xrefs"` makes Alt+X the accelerator). |
| `callback` | `Action` | The callback invoked when the menu item is clicked. Runs on the UI thread. |

```csharp
api.UI.AddMenuItem("Add _Bookmark at RIP", OnAddBookmarkAtRip);
api.UI.AddMenuItem("Save _Session...", OnSaveSession);
api.UI.AddMenuItem("API Monitor: _Start", () => _panel.StartMonitoring());
```

### 9.4 AddToolPanel

```csharp
void AddToolPanel(string title, object wpfContent);
```

Add a custom tab panel to the KernelFlirt main window. The panel appears as a new tab alongside the built-in tabs (Disassembly, Registers, Memory, etc.).

**Parameters:**

| Parameter | Type | Description |
|-----------|------|-------------|
| `title` | `string` | Tab header text (e.g., `"Bookmarks"`, `"Memory Scanner"`, `"Graph View"`). |
| `wpfContent` | `object` | A WPF `UIElement` (e.g., `Grid`, `StackPanel`, `UserControl`, `ScrollViewer`, etc.). The type is `object` to avoid requiring WPF references in the interface. |

**Call this once during `Initialize`.** Adding the same panel multiple times creates duplicate tabs.

```csharp
// Simple panel with a text block
var panel = new StackPanel();
panel.Children.Add(new TextBlock { Text = "Hello from my plugin!" });
api.UI.AddToolPanel("My Plugin", panel);

// Complex panel with a custom UserControl
var panel = new MyCustomPanel(api);
api.UI.AddToolPanel("Analysis", panel);
```

See [Chapter 13: UI Development](#13-ui-development) for detailed guidance on building panels.

### 9.5 Annotations

Annotations are text comments displayed in the disassembly view next to instructions, formatted as `; comment`. They are useful for labeling code, leaving notes during analysis, and communicating findings.

#### SetAddressAnnotation

```csharp
void SetAddressAnnotation(ulong address, string? annotation);
```

Set a text annotation for an address. The annotation appears as `; annotation text` in the disassembly view after the instruction at that address.

**Parameters:**

| Parameter | Type | Description |
|-----------|------|-------------|
| `address` | `ulong` | The address to annotate. |
| `annotation` | `string?` | The annotation text. Set to `null` or empty string to remove an existing annotation. |

```csharp
_api.UI.SetAddressAnnotation(0x140001000, "Entry point of decryption routine");
_api.UI.SetAddressAnnotation(0x140001050, "XOR key loaded here");
_api.UI.SetAddressAnnotation(0x140001000, null); // Remove annotation
```

#### GetAddressAnnotation

```csharp
string? GetAddressAnnotation(ulong address);
```

Get the annotation for a specific address.

**Parameters:** `address` (`ulong`) -- The address to query.
**Returns:** `string?` -- The annotation text, or `null` if no annotation exists.

#### GetAllAnnotations

```csharp
IReadOnlyDictionary<ulong, string> GetAllAnnotations();
```

Get all annotations as a dictionary mapping addresses to annotation text.

**Returns:** `IReadOnlyDictionary<ulong, string>` -- All current annotations.

#### RefreshDisassembly

```csharp
void RefreshDisassembly();
```

Force the disassembly view to re-render, reflecting any annotation changes, breakpoint changes, or other visual updates.

**Call this after bulk annotation changes** to avoid per-annotation refresh overhead:

```csharp
// Bulk-add annotations, then refresh once
foreach (var (addr, text) in myAnnotations)
    _api.UI.SetAddressAnnotation(addr, text);
_api.UI.RefreshDisassembly(); // Single refresh at the end
```

### 9.6 Decompilation

#### DecompileFunction

```csharp
void DecompileFunction(ulong address);
```

Request decompilation of the function at the given address using the RetDec decompiler backend. This is an **asynchronous** operation -- the decompilation runs in the background and may take several seconds.

**Parameters:** `address` (`ulong`) -- Any address within the function to decompile.

#### GetDecompiledCode

```csharp
string GetDecompiledCode();
```

Get the result of the last decompilation request.

**Returns:** `string` -- C pseudocode text, or empty string if no decompilation has been done or the decompilation is still in progress.

**Usage pattern:**

```csharp
_api.UI.DecompileFunction(functionAddress);

// Wait for decompilation to complete (simple approach)
await Task.Delay(3000);
string code = _api.UI.GetDecompiledCode();
if (!string.IsNullOrEmpty(code))
    _api.Log.Info($"Decompiled:\n{code}");
```

### 9.7 Module Management

These methods are designed for unpacker plugins that need to register dynamically unpacked PE files.

#### AddUnpackedModule

```csharp
void AddUnpackedModule(ulong peBase, string name);
```

Register a dynamically unpacked PE image at the specified base address. KernelFlirt will parse the PE header and refresh all related views: module list, sections, imports, exports, strings, and functions.

**Parameters:**

| Parameter | Type | Description |
|-----------|------|-------------|
| `peBase` | `ulong` | Base address of the unpacked PE in memory. |
| `name` | `string` | A display name for the module (e.g., `"unpacked_payload"`). |

#### RefreshModulesAndSections

```csharp
void RefreshModulesAndSections();
```

Force refresh the module list and sections tab. Call this after modifying module-related data.

#### AddModuleSections

```csharp
void AddModuleSections(string moduleName, IReadOnlyList<PluginSectionInfo> sections);
```

Provide section entries for a module directly, bypassing PE header parsing. Use this when a packer has zeroed or corrupted the PE header (anti-dump technique) but you know the section layout.

**Parameters:**

| Parameter | Type | Description |
|-----------|------|-------------|
| `moduleName` | `string` | Name of the module (must match a registered module). |
| `sections` | `IReadOnlyList<PluginSectionInfo>` | List of section definitions. |

```csharp
var sections = new List<PluginSectionInfo>
{
    new() { Name = ".text", VirtualAddress = peBase + 0x1000,
            VirtualSize = 0x5000, Characteristics = 0x60000020 },
    new() { Name = ".rdata", VirtualAddress = peBase + 0x6000,
            VirtualSize = 0x2000, Characteristics = 0x40000040 },
    new() { Name = ".data", VirtualAddress = peBase + 0x8000,
            VirtualSize = 0x1000, Characteristics = 0xC0000040 }
};
_api.UI.AddModuleSections("packed.exe", sections);
```

### 9.8 Cross-Plugin Communication

#### SetPluginData

```csharp
void SetPluginData(string key, object? value);
```

Store arbitrary data under a string key. The data is persisted in memory for the lifetime of the application and is accessible to all loaded plugins. Use this for cross-plugin communication.

**Parameters:**

| Parameter | Type | Description |
|-----------|------|-------------|
| `key` | `string` | A unique key name. Convention: use your plugin name as a prefix (e.g., `"GraphBlockColors"`, `"ScriptExecute"`). |
| `value` | `object?` | Any .NET object. Set to `null` to remove the key. |

#### GetPluginData

```csharp
object? GetPluginData(string key);
```

Retrieve data previously stored via `SetPluginData`.

**Parameters:** `key` (`string`) -- The key to look up.
**Returns:** `object?` -- The stored value, or `null` if the key does not exist.

```csharp
// Plugin A (GraphView) stores block colors
var colors = new Dictionary<ulong, Color>();
colors[0x140001000] = Colors.Red;
_api.UI.SetPluginData("GraphBlockColors", colors);

// Plugin B (Session Manager) reads them
if (_api.UI.GetPluginData("GraphBlockColors") is Dictionary<ulong, Color> colors)
{
    foreach (var (addr, color) in colors)
        SaveBlockColor(addr, color);
}
```

### 9.9 Note Events

These events fire when the user interacts with annotations via the disassembly context menu (right-click > Add Note / Edit Note / Remove Note).

```csharp
event Action<ulong, string>? OnNoteAdded;    // (address, noteText)
event Action<ulong, string>? OnNoteEdited;   // (address, newNoteText)
event Action<ulong>?         OnNoteRemoved;  // (address)
```

**Use case:** The Bookmarks plugin listens to these events to stay synchronized with annotations created outside the plugin:

```csharp
api.UI.OnNoteAdded += (addr, note) =>
    Application.Current.Dispatcher.BeginInvoke(() => OnExternalNoteAdded(addr, note));
api.UI.OnNoteEdited += (addr, note) =>
    Application.Current.Dispatcher.BeginInvoke(() => OnExternalNoteEdited(addr, note));
api.UI.OnNoteRemoved += addr =>
    Application.Current.Dispatcher.BeginInvoke(() => OnExternalNoteRemoved(addr));
```

---

## 10. Data Models

This chapter provides detailed documentation for every data model class in the SDK.

### 10.1 PluginRegister

Represents a single CPU register value.

```csharp
public class PluginRegister
{
    public string Name { get; set; }   // Register name
    public ulong Value { get; set; }   // Register value (64-bit)
    public bool IsFlag { get; set; }   // true for individual CPU flags
}
```

| Field | Type | Description |
|-------|------|-------------|
| `Name` | `string` | Register name. General-purpose: `"RAX"`, `"RBX"`, `"RCX"`, `"RDX"`, `"RSI"`, `"RDI"`, `"RBP"`, `"RSP"`, `"R8"` through `"R15"`. Special: `"RIP"`, `"RFLAGS"`. Debug: `"DR0"` through `"DR7"`. Segment: `"CS"`, `"DS"`, `"ES"`, `"FS"`, `"GS"`, `"SS"`. Individual flags: `"CF"`, `"PF"`, `"AF"`, `"ZF"`, `"SF"`, `"TF"`, `"IF"`, `"DF"`, `"OF"`. |
| `Value` | `ulong` | The register value. For general-purpose and special registers, this is the full 64-bit value. For individual flags (`IsFlag == true`), the value is `0` or `1`. |
| `IsFlag` | `bool` | `true` for individual CPU flags (CF, PF, AF, ZF, SF, TF, IF, DF, OF). These are extracted from RFLAGS for convenience. `false` for all other registers. |

### 10.2 PluginBreakpoint

Represents a breakpoint managed by the debugger.

```csharp
public class PluginBreakpoint
{
    public uint Handle { get; set; }
    public ulong Address { get; set; }
    public PluginBreakpointType Type { get; set; }
    public bool Enabled { get; set; }
    public string? Condition { get; set; }
    public uint HitCount { get; set; }
    public byte OriginalByte { get; set; }
}
```

| Field | Type | Description |
|-------|------|-------------|
| `Handle` | `uint` | Unique handle for this breakpoint. Used with `RemoveBreakpoint()` to remove it. |
| `Address` | `ulong` | The address where the breakpoint is set. |
| `Type` | `PluginBreakpointType` | The breakpoint type (Software, Hardware, HwWrite, HwReadWrite, Memory). |
| `Enabled` | `bool` | Whether the breakpoint is currently active. |
| `Condition` | `string?` | Optional condition expression (for conditional breakpoints). `null` for unconditional breakpoints. |
| `HitCount` | `uint` | Number of times this breakpoint has been hit since it was created. |
| `OriginalByte` | `byte` | For software breakpoints: the original byte that was replaced with `0xCC` (INT3). Used internally to restore the byte when the breakpoint is hit or removed. |

### 10.3 PluginModuleInfo

Represents a user-mode module (DLL or EXE) loaded in the target process.

```csharp
public class PluginModuleInfo
{
    public ulong BaseAddress { get; set; }
    public uint Size { get; set; }
    public string Name { get; set; }
}
```

| Field | Type | Description |
|-------|------|-------------|
| `BaseAddress` | `ulong` | Base address of the module in the process's virtual address space. For example, `0x00007FF6A0000000`. |
| `Size` | `uint` | Total size of the module in bytes (from `OptionalHeader.SizeOfImage`). |
| `Name` | `string` | File name of the module (e.g., `"kernel32.dll"`, `"notepad.exe"`). Does not include the full path. |

### 10.4 PluginKernelModuleInfo

Represents a kernel-mode module (driver) loaded in the system.

```csharp
public class PluginKernelModuleInfo
{
    public ulong BaseAddress { get; set; }
    public uint Size { get; set; }
    public ushort LoadOrder { get; set; }
    public string Name { get; set; }
}
```

| Field | Type | Description |
|-------|------|-------------|
| `BaseAddress` | `ulong` | Base address in kernel virtual address space (e.g., `0xFFFFF80070200000`). |
| `Size` | `uint` | Module size in bytes. |
| `LoadOrder` | `ushort` | Load order index (0 = first loaded, typically `ntoskrnl.exe`). |
| `Name` | `string` | Module file name (e.g., `"ntoskrnl.exe"`, `"win32kfull.sys"`, `"tcpip.sys"`). |

### 10.5 PluginProcessInfo

Represents a process on the target machine.

```csharp
public class PluginProcessInfo
{
    public uint ProcessId { get; set; }
    public uint SessionId { get; set; }
    public string Name { get; set; }
}
```

| Field | Type | Description |
|-------|------|-------------|
| `ProcessId` | `uint` | Windows Process ID (PID). |
| `SessionId` | `uint` | Terminal Services session ID. Session 0 is for services. Session 1+ is for interactive user logons. |
| `Name` | `string` | Process image name (e.g., `"notepad.exe"`, `"svchost.exe"`, `"System"`). |

### 10.6 PluginThreadInfo

Represents a thread within a process.

```csharp
public class PluginThreadInfo
{
    public uint ThreadId { get; set; }
    public ulong StartAddress { get; set; }
    public uint State { get; set; }
    public uint Priority { get; set; }
}
```

| Field | Type | Description |
|-------|------|-------------|
| `ThreadId` | `uint` | Windows Thread ID (TID). |
| `StartAddress` | `ulong` | The virtual address of the function where the thread started execution (the function passed to `CreateThread` or similar). |
| `State` | `uint` | Thread state flags from the kernel's `KTHREAD` structure. Common values: 0 = Initialized, 1 = Ready, 2 = Running, 5 = Waiting. |
| `Priority` | `uint` | Thread scheduling priority. Higher values = higher priority. Typical range: 0-31. |

### 10.7 PluginSectionInfo

Represents a PE section for module management.

```csharp
public class PluginSectionInfo
{
    public string Name { get; set; }
    public ulong VirtualAddress { get; set; }
    public uint VirtualSize { get; set; }
    public uint Characteristics { get; set; }
}
```

| Field | Type | Description |
|-------|------|-------------|
| `Name` | `string` | Section name (e.g., `".text"`, `".rdata"`, `".data"`, `".rsrc"`, `".reloc"`). |
| `VirtualAddress` | `ulong` | **Absolute** virtual address of the section (NOT an RVA). For example, if the module base is `0x140000000` and the section RVA is `0x1000`, this should be `0x140001000`. |
| `VirtualSize` | `uint` | Size of the section in bytes (before alignment). |
| `Characteristics` | `uint` | PE section characteristics flags. Common values: `0x60000020` (code, execute, read), `0x40000040` (initialized data, read), `0xC0000040` (initialized data, read, write). |

### 10.8 PluginFunctionEntry

Represents a user-defined function registered via `RegisterFunction`.

```csharp
public class PluginFunctionEntry
{
    public ulong Address { get; set; }
    public string Name { get; set; }
    public uint Size { get; set; }
}
```

| Field | Type | Description |
|-------|------|-------------|
| `Address` | `ulong` | Start address of the function. |
| `Name` | `string` | User-defined function name. |
| `Size` | `uint` | Size of the function body in bytes. `0` if not specified during registration. |

### 10.9 PluginDebugEvent

Represents a debug event. Contains both read-only event information and writable fields that control how the process resumes.

```csharp
public class PluginDebugEvent
{
    // Read-only event information
    public PluginDebugEventType Type { get; set; }
    public uint ProcessId { get; set; }
    public uint ThreadId { get; set; }
    public ulong Address { get; set; }
    public bool IsKernelMode { get; set; }
    public uint ExceptionCode { get; set; }
    public ulong FaultAddress { get; set; }
    public uint AccessType { get; set; }

    // Writable control fields
    public uint ContinueMode { get; set; }
    public ulong NewRip { get; set; }
    public ulong NewRsp { get; set; }
    public ulong TraceRangeBase { get; set; }
    public ulong TraceRangeEnd { get; set; }
    public uint TraceMaxSteps { get; set; }
}
```

**Read-only fields:**

| Field | Type | Description |
|-------|------|-------------|
| `Type` | `PluginDebugEventType` | The type of debug event. See [Section 11.2](#112-plugindebugeventtype). |
| `ProcessId` | `uint` | PID of the process that generated the event. |
| `ThreadId` | `uint` | TID of the thread that generated the event. |
| `Address` | `ulong` | The value of RIP at the time of the event. For breakpoints, this is the breakpoint address. |
| `IsKernelMode` | `bool` | `true` if the event occurred while the CPU was in kernel mode (ring 0). `false` for user-mode events. |
| `ExceptionCode` | `uint` | The Windows exception code. Examples: `0x80000003` (breakpoint), `0x80000004` (single step), `0xC0000005` (access violation). |
| `FaultAddress` | `ulong` | For `AccessViolation` events: the virtual address that caused the fault. `0` for non-AV events. |
| `AccessType` | `uint` | For `AccessViolation` events: the type of access. `0` = read, `1` = write, `8` = execute. |

**Writable control fields (set in `OnDebugEventFilter` handler):**

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `ContinueMode` | `uint` | `0` | Controls how the process resumes. See [Section 12.3](#123-continuemode-values). |
| `NewRip` | `ulong` | `0` | If non-zero, overrides RIP before resuming. Used for execution redirection (e.g., IAT tracing, hook trampolines). The value is applied via the kernel's `CONTEXT` structure. |
| `NewRsp` | `ulong` | `0` | If non-zero, overrides RSP before resuming. Used together with `NewRip` to restore stack state after redirection. |
| `TraceRangeBase` | `ulong` | `0` | For `ContinueMode = 4` (Trace): inclusive start of the trace range. |
| `TraceRangeEnd` | `ulong` | `0` | For `ContinueMode = 4` (Trace): exclusive end of the trace range. |
| `TraceMaxSteps` | `uint` | `0` | For `ContinueMode = 4` (Trace): maximum number of single-step iterations before the driver reports back. `0` defaults to 500,000. |

### 10.10 PluginScriptHost

A globals host class used by the Scripting plugin for Roslyn script execution.

```csharp
public class PluginScriptHost
{
    public IDebuggerApi api { get; set; }
    public Action<string> print { get; set; }
}
```

| Field | Type | Description |
|-------|------|-------------|
| `api` | `IDebuggerApi` | The debugger API, accessible as a global variable in scripts. |
| `print` | `Action<string>` | A print function that outputs to the script console. |

This class lives in the SDK (shared assembly) to avoid `AssemblyLoadContext` conflicts with the Roslyn compiler. Plugin developers generally do not need to interact with this class directly unless building a custom scripting engine.

---

## 11. Enumerations

### 11.1 PluginBreakpointType

```csharp
public enum PluginBreakpointType
{
    Software    = 0,   // INT3 software breakpoint
    Hardware    = 1,   // Hardware execution breakpoint (DR0-DR3)
    HwWrite     = 2,   // Hardware write watchpoint
    HwReadWrite = 3,   // Hardware read/write watchpoint
    Memory      = 4    // Page protection-based memory breakpoint
}
```

Detailed comparison:

| Type | Mechanism | Limit | Detectable | Data Watch | Execution Watch |
|------|-----------|-------|------------|------------|-----------------|
| Software | Replaces byte with 0xCC | Unlimited | Yes (memory integrity checks) | No | Yes |
| Hardware | Debug registers DR0-DR3 | 4 per thread | Yes (reading DR7) | No | Yes |
| HwWrite | Debug registers DR0-DR3 | 4 per thread | Yes (reading DR7) | Write only | No |
| HwReadWrite | Debug registers DR0-DR3 | 4 per thread | Yes (reading DR7) | Read + Write | No |
| Memory | Page protection change | Unlimited | Possible (VirtualQuery) | Any access | Yes |

### 11.2 PluginDebugEventType

```csharp
public enum PluginDebugEventType
{
    Breakpoint      = 1,   // INT3 software breakpoint hit
    SingleStep      = 2,   // Single-step completed (Trap Flag)
    HwBreakpoint    = 3,   // Hardware execution breakpoint hit
    HwWatchpoint    = 4,   // Hardware data watchpoint triggered
    MemoryBp        = 5,   // Memory breakpoint triggered
    AccessViolation = 6    // Access violation (page fault)
}
```

| Value | Name | Trigger | Typical Use |
|-------|------|---------|-------------|
| 1 | `Breakpoint` | `INT3` instruction executed | Standard software breakpoint hits. Exception code `0x80000003`. |
| 2 | `SingleStep` | Trap Flag (TF) set in RFLAGS | Fired after each instruction when single-stepping (F7). Also fired after ContinueMode 2 or 3. |
| 3 | `HwBreakpoint` | Hardware execution BP (DR0-DR3) | Hardware breakpoint triggered when instruction at DR0-DR3 address is about to execute. |
| 4 | `HwWatchpoint` | Hardware data watchpoint | Fired when a watched memory location is accessed (written or read/written, depending on type). |
| 5 | `MemoryBp` | Page protection violation | Fired when a memory breakpoint's guarded page is accessed. |
| 6 | `AccessViolation` | Page fault exception | General access violation. Exception code `0xC0000005`. Can be a real bug or a guard page trigger used for tracing. |

---

## 12. Debug Events and Filters

This chapter covers the most powerful and complex aspect of the plugin SDK: intercepting and handling debug events.

### 12.1 OnDebugEvent vs OnDebugEventFilter

KernelFlirt provides two event mechanisms for debug events. Understanding the difference is essential:

| Aspect | `OnDebugEvent` | `OnDebugEventFilter` |
|--------|----------------|---------------------|
| **When it fires** | AFTER the UI processes the event | BEFORE the UI processes the event |
| **Thread** | UI thread | Background thread |
| **Return value** | `void` (no return) | `bool` -- `true` to suppress, `false` to pass through |
| **Can control resume** | No | Yes (via `ContinueMode`, `NewRip`, `NewRsp`) |
| **Can suppress UI break** | No | Yes (return `true`) |
| **Typical use** | Logging, statistics, non-intrusive monitoring | API hooking, trace stepping, guard page handling, auto-continue logic |

**Rule of thumb:**
- Use `OnDebugEvent` when you just want to observe events without affecting debugger behavior.
- Use `OnDebugEventFilter` when you need to intercept events and control what happens next.

### 12.2 The PluginDebugEvent Object

See [Section 10.9](#109-plugindebugevent) for the complete field reference. The key insight is that `PluginDebugEvent` serves dual purpose:

1. **Input** (read-only fields): tells you what happened (event type, address, process/thread, exception details).
2. **Output** (writable fields): tells the driver what to do next (continue mode, RIP/RSP override, trace range).

### 12.3 ContinueMode Values

The `ContinueMode` field controls how the target process resumes after the event is handled:

| Value | Name | Behavior |
|-------|------|----------|
| `0` | **Run** | Resume execution normally. This is the default. If you return `true` from the filter and leave `ContinueMode = 0`, the process continues running at full speed. |
| `1` | **StepPast** | Step past a software breakpoint, then auto-continue. The driver temporarily restores the original byte, single-steps one instruction, re-sets the INT3, and resumes running. This is how the debugger handles "continue past a breakpoint" internally. |
| `2` | **StepInto** | Step past a software breakpoint, then report a `SingleStep` event. Like StepPast, but instead of auto-continuing, the driver fires another debug event after the step. Use this when you want to examine the state immediately after the breakpoint instruction. |
| `3` | **Handled** | Mark the exception as handled (suppress it from reaching the process's SEH chain) and set the Trap Flag for a single-step. Used for guard page tracing: you catch the AV on a no-access page, handle it by restoring access, and use ContinueMode 3 to step over the faulting instruction so you can re-arm the guard afterward. |
| `4` | **Trace** | Fast driver-side trace. The driver executes instructions internally (via single-stepping) while RIP remains within `[TraceRangeBase, TraceRangeEnd)`. It reports a `SingleStep` event only when RIP exits the range OR when `TraceMaxSteps` is reached. This is much faster than user-mode single-stepping because it avoids the kernel-to-user-mode roundtrip for each step. Used for tracing through packer wrappers and thunks. |

### 12.4 Controlling Execution Flow

When your `OnDebugEventFilter` handler returns `true`, you are taking ownership of the event. The UI will not break, and `OnBreakStateEntered` will not fire. You have several options for what to do:

**Option 1: Let ContinueMode handle it (most common)**

Set `ContinueMode` on the event object and return `true`. The driver applies the mode automatically.

```csharp
private bool OnFilter(PluginDebugEvent evt)
{
    if (evt.Type == PluginDebugEventType.Breakpoint && evt.Address == _myBp)
    {
        LogHit(evt);
        evt.ContinueMode = 1; // StepPast: step over BP and continue
        return true;
    }
    return false;
}
```

**Option 2: Call Continue() or SingleStep() explicitly**

```csharp
private bool OnFilter(PluginDebugEvent evt)
{
    if (evt.Type == PluginDebugEventType.Breakpoint && evt.Address == _myBp)
    {
        LogHit(evt);
        _api.Continue(); // Resume manually
        return true;
    }
    return false;
}
```

**Option 3: Redirect execution with NewRip/NewRsp**

```csharp
private bool OnFilter(PluginDebugEvent evt)
{
    if (evt.Type == PluginDebugEventType.Breakpoint && evt.Address == _iatStubAddr)
    {
        // Read the real function address from the IAT entry
        var data = _api.Memory.ReadMemory(evt.ProcessId, _iatEntry, 8);
        ulong realAddr = BitConverter.ToUInt64(data!);

        // Redirect execution to the real function
        evt.NewRip = realAddr;
        evt.ContinueMode = 0; // Run
        return true;
    }
    return false;
}
```

### 12.5 Guard Page Tracing Pattern

A common anti-debug and unpacking technique involves guard pages (pages with `PAGE_NOACCESS` protection). When accessed, they trigger an access violation. A plugin can use this for tracing:

```csharp
private ulong _guardBase;
private uint _guardSize;
private uint _origProt;
private bool _rearmOnStep;

// Setup: make a page non-accessible
public void SetGuard(ulong pageBase, uint size)
{
    _guardBase = pageBase;
    _guardSize = size;
    var (ok, oldProt) = _api.Memory.ProtectMemory(
        _api.TargetPid, pageBase, size, 0x01); // PAGE_NOACCESS
    _origProt = oldProt;
}

private bool OnFilter(PluginDebugEvent evt)
{
    // Step 1: catch the access violation on our guarded page
    if (evt.Type == PluginDebugEventType.AccessViolation)
    {
        if (evt.FaultAddress >= _guardBase &&
            evt.FaultAddress < _guardBase + _guardSize)
        {
            // Log the access
            string accessStr = evt.AccessType switch
            {
                0 => "READ", 1 => "WRITE", 8 => "EXECUTE", _ => "?"
            };
            _api.Log.Info($"Guard hit: {accessStr} at {evt.FaultAddress:X16}" +
                $" from RIP={evt.Address:X16}");

            // Temporarily restore access so the instruction can complete
            _api.Memory.ProtectMemory(
                _api.TargetPid, _guardBase, _guardSize, _origProt);

            // ContinueMode 3: suppress AV + set Trap Flag
            evt.ContinueMode = 3;
            _rearmOnStep = true;
            return true;
        }
    }

    // Step 2: re-arm the guard after the instruction completes
    if (evt.Type == PluginDebugEventType.SingleStep && _rearmOnStep)
    {
        _api.Memory.ProtectMemory(
            _api.TargetPid, _guardBase, _guardSize, 0x01);
        _rearmOnStep = false;
        evt.ContinueMode = 0; // Continue running
        return true;
    }

    return false;
}
```

### 12.6 API Hooking Pattern

The `OnDebugEventFilter` is the foundation for API monitoring. Here is a simplified pattern:

```csharp
private Dictionary<ulong, string> _hookedApis = new();
private Dictionary<uint, (string api, ulong retAddr)> _pendingReturns = new();

public void StartMonitoring()
{
    // Set breakpoints on API entry points
    ulong createFile = _api.Symbols.ResolveNameToAddress("kernelbase!CreateFileW");
    if (createFile != 0)
    {
        _api.Breakpoints.SetBreakpoint(
            _api.TargetPid, 0, createFile, PluginBreakpointType.Software);
        _hookedApis[createFile] = "CreateFileW";
    }

    _api.OnDebugEventFilter += OnFilter;
}

private bool OnFilter(PluginDebugEvent evt)
{
    if (evt.Type == PluginDebugEventType.Breakpoint &&
        _hookedApis.TryGetValue(evt.Address, out string? apiName))
    {
        // Read arguments (Windows x64: RCX, RDX, R8, R9)
        var regs = _api.Memory.ReadRegisters(evt.ProcessId, evt.ThreadId);
        ulong rcx = regs.First(r => r.Name == "RCX").Value;
        ulong rsp = regs.First(r => r.Name == "RSP").Value;

        // Read return address from [RSP]
        var retData = _api.Memory.ReadMemory(evt.ProcessId, rsp, 8);
        ulong retAddr = BitConverter.ToUInt64(retData!);

        // Read first argument as Unicode string (for CreateFileW)
        var strData = _api.Memory.ReadMemory(evt.ProcessId, rcx, 512);
        string path = System.Text.Encoding.Unicode.GetString(strData ?? []);
        int nullIdx = path.IndexOf('\0');
        if (nullIdx >= 0) path = path[..nullIdx];

        _api.Log.Info($"[API] {apiName}(\"{path}\")");

        // Set return breakpoint to capture the return value
        _api.Breakpoints.SetBreakpoint(
            evt.ProcessId, evt.ThreadId, retAddr, PluginBreakpointType.Software);
        _pendingReturns[evt.ThreadId] = (apiName, retAddr);

        evt.ContinueMode = 1; // StepPast and continue
        return true;
    }

    // Check for return breakpoints
    if (evt.Type == PluginDebugEventType.Breakpoint &&
        _pendingReturns.TryGetValue(evt.ThreadId, out var pending) &&
        evt.Address == pending.retAddr)
    {
        var regs = _api.Memory.ReadRegisters(evt.ProcessId, evt.ThreadId);
        ulong rax = regs.First(r => r.Name == "RAX").Value;

        _api.Log.Info($"[API] {pending.api} returned: {rax:X16}");
        _pendingReturns.Remove(evt.ThreadId);

        evt.ContinueMode = 1;
        return true;
    }

    return false;
}
```

### 12.7 Driver-Side Trace Mode

`ContinueMode = 4` (Trace) enables high-performance tracing that runs entirely in kernel mode:

```csharp
private bool OnFilter(PluginDebugEvent evt)
{
    if (evt.Type == PluginDebugEventType.Breakpoint &&
        evt.Address == _packerThunkEntry)
    {
        // The packer's thunk is at [thunkBase, thunkBase + 0x200)
        // Trace through it without reporting every step
        evt.ContinueMode = 4;
        evt.TraceRangeBase = _thunkBase;
        evt.TraceRangeEnd = _thunkBase + 0x200;
        evt.TraceMaxSteps = 100000;
        return true;
    }

    if (evt.Type == PluginDebugEventType.SingleStep &&
        evt.Address >= _thunkBase && evt.Address < _thunkBase + 0x200)
    {
        // Max steps reached but still in range -- continue tracing
        evt.ContinueMode = 4;
        evt.TraceRangeBase = _thunkBase;
        evt.TraceRangeEnd = _thunkBase + 0x200;
        evt.TraceMaxSteps = 100000;
        return true;
    }

    if (evt.Type == PluginDebugEventType.SingleStep)
    {
        // Exited the trace range -- we're at the real function
        _api.Log.Info($"Thunk resolved: real function at {evt.Address:X16}");
        string? name = _api.Symbols.ResolveAddress(evt.Address);
        if (name != null)
            _api.Log.Info($"  -> {name}");
        return false; // Let UI handle (break)
    }

    return false;
}
```

---

## 13. UI Development

KernelFlirt plugins can create rich user interfaces using WPF (Windows Presentation Foundation). This chapter covers common UI patterns used in plugin development.

### 13.1 WPF Fundamentals for Plugins

KernelFlirt is a WPF application running on .NET 9. Your plugin's UI elements are hosted inside the main window as tab content. Key points:

- Your `.csproj` must include `<UseWPF>true</UseWPF>`.
- You can use any WPF control: `Grid`, `StackPanel`, `DockPanel`, `DataGrid`, `TextBox`, `Button`, `ComboBox`, `CheckBox`, `TreeView`, `ScrollViewer`, `ProgressBar`, `TabControl`, `ListView`, etc.
- WPF data binding works normally.
- You can create custom `UserControl` classes for complex panels.
- The KernelFlirt window uses a dark theme. See [Section 13.4](#134-theming-and-brushes) for theming guidance.

### 13.2 Adding a Tool Panel

The typical pattern for adding a panel:

```csharp
public void Initialize(IDebuggerApi api)
{
    _api = api;
    BuildUi(); // Create UI and call AddToolPanel inside
}

private void BuildUi()
{
    var root = new Grid();
    root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // toolbar
    root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // content
    root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // status bar

    // Row 0: Toolbar
    var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4) };
    // ... add buttons to toolbar ...
    Grid.SetRow(toolbar, 0);
    root.Children.Add(toolbar);

    // Row 1: Main content (e.g., DataGrid)
    var grid = BuildDataGrid();
    Grid.SetRow(grid, 1);
    root.Children.Add(grid);

    // Row 2: Status
    var status = new TextBlock { Margin = new Thickness(4), Foreground = Brushes.Gray };
    Grid.SetRow(status, 2);
    root.Children.Add(status);

    _api.UI.AddToolPanel("My Panel", root);
}
```

**Alternatively, use a custom UserControl:**

```csharp
public class MyPanel : System.Windows.Controls.UserControl
{
    private readonly IDebuggerApi _api;

    public MyPanel(IDebuggerApi api)
    {
        _api = api;
        Content = BuildUi();
    }

    private UIElement BuildUi()
    {
        // ... build and return the UI tree ...
    }
}

// In Initialize:
var panel = new MyPanel(api);
api.UI.AddToolPanel("My Plugin", panel);
```

### 13.3 DataGrid Patterns

The `DataGrid` is the most common control in KernelFlirt plugins, used to display tabular data (results, breakpoints, bookmarks, API calls, etc.). Here is the standard setup:

```csharp
private DataGrid BuildDataGrid()
{
    var grid = new DataGrid
    {
        AutoGenerateColumns = false,    // Define columns explicitly
        IsReadOnly = true,              // Read-only display
        SelectionMode = DataGridSelectionMode.Single,
        HeadersVisibility = DataGridHeadersVisibility.Column,
        GridLinesVisibility = DataGridGridLinesVisibility.None,

        // Dark theme colors
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        RowBackground = Brushes.Transparent,
        AlternatingRowBackground = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)),

        // Monospace font for addresses
        FontFamily = new FontFamily("Consolas"),
        FontSize = 12
    };

    // Define columns with explicit bindings
    grid.Columns.Add(new DataGridTextColumn
    {
        Header = "Address",
        Binding = new System.Windows.Data.Binding("AddressHex"),
        Width = 150
    });
    grid.Columns.Add(new DataGridTextColumn
    {
        Header = "Module",
        Binding = new System.Windows.Data.Binding("Display"),
        Width = 160
    });
    grid.Columns.Add(new DataGridTextColumn
    {
        Header = "Details",
        Binding = new System.Windows.Data.Binding("Details"),
        Width = new DataGridLength(1, DataGridLengthUnitType.Star)  // fill remaining space
    });

    // Double-click to navigate
    grid.MouseDoubleClick += (_, _) =>
    {
        if (grid.SelectedItem is MyResult result)
            _api.UI.NavigateDisassembly(result.Address);
    };

    return grid;
}
```

**Refreshing DataGrid data:**

```csharp
private void RefreshGrid()
{
    _grid.ItemsSource = null;     // Force reset
    _grid.ItemsSource = _results; // Re-bind
}
```

**Data model for DataGrid items:**

```csharp
public class MyResult
{
    public ulong Address { get; set; }
    public string Module { get; set; } = "";
    public string Details { get; set; } = "";

    // Computed properties for display
    public string AddressHex => $"{Address:X16}";
    public string Display => string.IsNullOrEmpty(Module) ? AddressHex : Module;
}
```

### 13.4 Theming and Brushes

KernelFlirt uses a dark theme. To make your plugin fit visually, follow these guidelines:

**Background colors:**
```csharp
// Panel background (matches KernelFlirt's dark theme)
var bg = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x2E)); // dark blue-gray

// Input field background
var inputBg = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x3A));

// Border color
var border = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x5A));

// Alternating row color for DataGrid
var altRow = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255));
```

**Text colors:**
```csharp
// Primary text
Foreground = Brushes.White;

// Secondary text (labels, status)
Foreground = Brushes.LightGray;

// Muted text (timestamps, metadata)
Foreground = Brushes.Gray;
```

**Using application resources (if available):**

KernelFlirt defines theme brushes that plugins can access through `Application.Current.Resources`:

```csharp
// Try to use the application's theme brush
if (Application.Current.Resources["PluginBgBrush"] is Brush bgBrush)
    panel.Background = bgBrush;
else
    panel.Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x2E));
```

### 13.5 Context Menus

Context menus provide right-click functionality on DataGrid rows and other controls:

```csharp
var ctx = new ContextMenu();

var goToItem = new MenuItem { Header = "Go to Address" };
goToItem.Click += (_, _) =>
{
    if (_grid.SelectedItem is MyResult r)
        _api.UI.NavigateDisassembly(r.Address);
};
ctx.Items.Add(goToItem);

var copyItem = new MenuItem { Header = "Copy Address" };
copyItem.Click += (_, _) =>
{
    if (_grid.SelectedItem is MyResult r)
        Clipboard.SetText(r.AddressHex);
};
ctx.Items.Add(copyItem);

ctx.Items.Add(new Separator()); // Visual separator

var editItem = new MenuItem { Header = "Edit..." };
editItem.Click += (_, _) => OnEditSelected();
ctx.Items.Add(editItem);

var removeItem = new MenuItem { Header = "Remove" };
removeItem.Click += (_, _) => OnRemoveSelected();
ctx.Items.Add(removeItem);

_grid.ContextMenu = ctx;
```

### 13.6 Toolbar Buttons

Standard toolbar button pattern:

```csharp
var toolbar = new StackPanel
{
    Orientation = Orientation.Horizontal,
    Margin = new Thickness(4)
};

var addBtn = new Button
{
    Content = "+ Add",
    Padding = new Thickness(8, 2, 8, 2),
    Margin = new Thickness(0, 0, 4, 0)
};
addBtn.Click += (_, _) => OnAdd();
toolbar.Children.Add(addBtn);

var removeBtn = new Button
{
    Content = "- Remove",
    Padding = new Thickness(8, 2, 8, 2),
    Margin = new Thickness(0, 0, 4, 0)
};
removeBtn.Click += (_, _) => OnRemove();
toolbar.Children.Add(removeBtn);

var scanBtn = new Button
{
    Content = "Scan",
    Width = 70,
    Padding = new Thickness(4, 2, 4, 2),
    Margin = new Thickness(0, 0, 4, 0)
};
scanBtn.Click += (_, _) => StartScan();
toolbar.Children.Add(scanBtn);
```

### 13.7 Dialog Windows

Use standard WPF `Window` for modal dialogs:

```csharp
private static string? PromptString(string title, string prompt, string defaultValue)
{
    var dlg = new Window
    {
        Title = title,
        Width = 400,
        Height = 150,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        ResizeMode = ResizeMode.NoResize,
        Owner = Application.Current.MainWindow
    };

    var sp = new StackPanel { Margin = new Thickness(12) };
    sp.Children.Add(new TextBlock
    {
        Text = prompt,
        Margin = new Thickness(0, 0, 0, 6)
    });

    var tb = new TextBox { Text = defaultValue };
    sp.Children.Add(tb);

    var btnPanel = new StackPanel
    {
        Orientation = Orientation.Horizontal,
        HorizontalAlignment = HorizontalAlignment.Right,
        Margin = new Thickness(0, 10, 0, 0)
    };

    var okBtn = new Button
    {
        Content = "OK",
        Width = 70,
        IsDefault = true,
        Margin = new Thickness(0, 0, 6, 0)
    };
    okBtn.Click += (_, _) => { dlg.DialogResult = true; dlg.Close(); };

    var cancelBtn = new Button
    {
        Content = "Cancel",
        Width = 70,
        IsCancel = true
    };

    btnPanel.Children.Add(okBtn);
    btnPanel.Children.Add(cancelBtn);
    sp.Children.Add(btnPanel);

    dlg.Content = sp;
    tb.Focus();
    tb.SelectAll();

    return dlg.ShowDialog() == true ? tb.Text : null;
}
```

**File dialogs:**

```csharp
// Save file dialog
var saveDlg = new Microsoft.Win32.SaveFileDialog
{
    Filter = "KF Session (*.kfsession)|*.kfsession",
    Title = "Save Session",
    DefaultExt = ".kfsession",
    FileName = "session.kfsession"
};
if (saveDlg.ShowDialog() == true)
{
    File.WriteAllText(saveDlg.FileName, jsonData);
}

// Open file dialog
var openDlg = new Microsoft.Win32.OpenFileDialog
{
    Filter = "KF Session (*.kfsession)|*.kfsession",
    Title = "Load Session"
};
if (openDlg.ShowDialog() == true)
{
    string json = File.ReadAllText(openDlg.FileName);
}
```

### 13.8 Progress Indication

For long-running operations (memory scans, etc.), use a `ProgressBar`:

```csharp
// Add a progress bar to the UI
_progress = new ProgressBar
{
    Height = 4,
    Margin = new Thickness(4, 0, 4, 2),
    Visibility = Visibility.Collapsed  // Hidden by default
};

// Show determinate progress during scan
_progress.Visibility = Visibility.Visible;
_progress.IsIndeterminate = false;
_progress.Value = 0;

// Update from background thread
Application.Current?.Dispatcher.BeginInvoke(() =>
{
    _progress.Value = (regionsDone * 100) / totalRegions;
});

// Hide when done
_progress.Visibility = Visibility.Collapsed;
```

### 13.9 Status Bars with Data Binding

```csharp
// Status text with data binding to item count
var status = new TextBlock
{
    Margin = new Thickness(4),
    Foreground = Brushes.Gray,
    FontSize = 11
};
status.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Items.Count")
{
    Source = _grid,
    StringFormat = "{0} item(s)"
});
```

---

## 14. Threading Model

Understanding KernelFlirt's threading model is essential for writing correct and responsive plugins.

### 14.1 UI Thread vs Background Threads

KernelFlirt uses two main thread categories:

1. **UI Thread (STA):** The WPF dispatcher thread. All UI elements must be created and modified on this thread. Events like `OnConnected`, `OnDisconnected`, `OnBreakStateEntered`, `OnBreakStateExited`, `OnBeforeRun`, and `OnDebugEvent` fire on this thread.

2. **Background Thread:** Debug events from the kernel driver arrive on a background thread. The `OnDebugEventFilter` handler is called on this thread for maximum responsiveness (no UI dispatch delay).

### 14.2 Dispatcher Marshaling

When you need to update UI elements from a background thread (e.g., inside `OnDebugEventFilter` or an `async Task`), you must marshal to the UI thread:

```csharp
// BeginInvoke: asynchronous (non-blocking, fire-and-forget)
Application.Current.Dispatcher.BeginInvoke(() =>
{
    _statusText.Text = "Processing...";
    _results.Add(newResult);
    RefreshGrid();
});

// Invoke: synchronous (blocks until UI thread executes the delegate)
Application.Current.Dispatcher.Invoke(() =>
{
    _panel.UpdateDisplay();
});
```

**When to use which:**
- `BeginInvoke`: For UI updates that don't need to complete before your code continues. This is the most common case and avoids deadlocks.
- `Invoke`: When you need the UI update to complete before proceeding (rare; risk of deadlock if the UI thread is waiting for your thread).

**Critical safety check:**

```csharp
// Check if already on the UI thread
if (Application.Current?.Dispatcher.CheckAccess() == true)
{
    // Already on UI thread, update directly
    _statusText.Text = "Done";
}
else
{
    // On background thread, dispatch
    Application.Current?.Dispatcher.BeginInvoke(() => _statusText.Text = "Done");
}
```

### 14.3 Async/Await Patterns

The `async/await` pattern works well for long-running operations that update the UI:

```csharp
private async void StartScan()
{
    // Capture UI state (on UI thread)
    var pattern = _patternBox.Text;
    var pid = _api.TargetPid;
    _scanBtn.IsEnabled = false;
    _progress.Visibility = Visibility.Visible;

    var results = new List<ScanResult>();

    // Heavy work on background thread
    await Task.Run(() =>
    {
        foreach (var region in regions)
        {
            if (_cts.Token.IsCancellationRequested) break;

            var data = _api.Memory.ReadMemory(pid, region.Base, region.Size);
            if (data == null) continue;

            // ... scan data for pattern matches ...

            // Update UI periodically
            Application.Current?.Dispatcher.BeginInvoke(() =>
                _statusText.Text = $"Found {results.Count} results...");
        }
    });

    // Back on UI thread after await
    _grid.ItemsSource = results;
    _scanBtn.IsEnabled = true;
    _progress.Visibility = Visibility.Collapsed;
    _statusText.Text = $"Done: {results.Count} result(s)";
}
```

### 14.4 Cancellation

Use `CancellationTokenSource` for cancellable background operations:

```csharp
private CancellationTokenSource? _cts;

private async void StartScan()
{
    _cts = new CancellationTokenSource();
    var ct = _cts.Token;

    _stopBtn.IsEnabled = true;

    await Task.Run(() =>
    {
        foreach (var region in regions)
        {
            if (ct.IsCancellationRequested) break;
            // ... do work ...
        }
    }, ct);

    _stopBtn.IsEnabled = false;
}

// Stop button handler
_stopBtn.Click += (_, _) => _cts?.Cancel();

// Cleanup in Shutdown
public void Shutdown() => _cts?.Cancel();
```

### 14.5 Thread Safety of API Calls

| API | Thread Safe | Notes |
|-----|-------------|-------|
| `ILogApi` (Info/Warning/Error) | Yes | Auto-dispatched to UI thread |
| `IMemoryApi` (ReadMemory, WriteMemory, ReadRegisters, etc.) | Yes | Kernel IOCTL calls are serialized |
| `IBreakpointApi` (SetBreakpoint, RemoveBreakpoint, etc.) | Yes | Kernel IOCTL calls |
| `ISymbolApi` (ResolveAddress, ResolveNameToAddress, etc.) | Yes | Symbol engine has internal locking |
| `IProcessApi` (EnumProcesses, EnumThreads, etc.) | Yes | Kernel IOCTL calls |
| `IUiApi` (NavigateDisassembly, AddMenuItem, etc.) | Yes | Auto-dispatched to UI thread |
| `IUiApi.SetPluginData / GetPluginData` | Partially | The dictionary is not locked; avoid concurrent writes from multiple threads |
| Direct WPF control access | No | Must use Dispatcher |

---

## 15. Cross-Plugin Communication

### 15.1 SetPluginData / GetPluginData

The `SetPluginData` and `GetPluginData` methods on `IUiApi` provide a simple key-value store for sharing data between plugins. Data is stored in memory for the lifetime of the application.

```csharp
// Writer plugin stores data
_api.UI.SetPluginData("MyPlugin.Results", results);

// Reader plugin retrieves data
if (_api.UI.GetPluginData("MyPlugin.Results") is List<ScanResult> results)
{
    // Use the results
}
```

### 15.2 Common Data Keys

The following data keys are used by built-in KernelFlirt plugins:

| Key | Type | Provider | Description |
|-----|------|----------|-------------|
| `"GraphBlockColors"` | `Dictionary<ulong, Color>` | Graph View plugin | Block highlight colors for the CFG graph. Maps block start addresses to WPF `Color` values. |
| `"ScriptExecute"` | `Func<string, Task<string>>` | Scripting plugin | A function that executes C# code and returns the output. Used by the MCP Server and AI Assistant plugins to run scripts. |

### 15.3 Exposing Functions to Other Plugins

You can expose callable functions through `SetPluginData`:

```csharp
// Provider plugin
Func<string, Task<string>> executeScript = async (code) =>
{
    try { return await _engine.ExecuteAsync(code); }
    catch (Exception ex) { return $"Error: {ex.Message}"; }
};
_api.UI.SetPluginData("ScriptExecute", executeScript);

// Consumer plugin
if (_api.UI.GetPluginData("ScriptExecute") is Func<string, Task<string>> execute)
{
    string result = await execute("api.Memory.ReadMemory(api.TargetPid, 0x140000000, 16)");
    _api.Log.Info($"Script result: {result}");
}
```

---

## 16. Plugin Persistence

Plugins often need to save and load state across sessions (bookmarks, settings, analysis results, etc.). This chapter covers the recommended patterns.

### 16.1 File-Based Persistence

The simplest approach is saving state to JSON files in the `plugins/` directory:

```csharp
private string _savePath = "";

public void Initialize(IDebuggerApi api)
{
    _api = api;
    var pluginsDir = Path.Combine(AppContext.BaseDirectory, "plugins");
    _savePath = Path.Combine(pluginsDir, "myplugin.json");
    LoadState();
}

public void Shutdown()
{
    SaveState();
}

private void SaveState()
{
    try
    {
        var json = JsonSerializer.Serialize(_data,
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_savePath, json);
    }
    catch (Exception ex)
    {
        _api.Log.Warning($"Save failed: {ex.Message}");
    }
}

private void LoadState()
{
    try
    {
        if (!File.Exists(_savePath)) return;
        var json = File.ReadAllText(_savePath);
        _data = JsonSerializer.Deserialize<MyData>(json) ?? new();
    }
    catch (Exception ex)
    {
        _api.Log.Warning($"Load failed: {ex.Message}");
    }
}
```

### 16.2 Target-Aware File Naming

For data that is specific to the target being debugged (bookmarks, analysis results), use the target's module name in the file name:

```csharp
private void UpdateTarget()
{
    // Determine target name from main module
    string target = "";
    var modules = _api.Symbols.GetModules();
    if (modules.Count > 0)
        target = Path.GetFileNameWithoutExtension(modules[0].Name);

    if (string.IsNullOrEmpty(target)) return;

    // Save old data, switch to new target
    if (_currentTarget != target)
    {
        if (!string.IsNullOrEmpty(_currentTarget))
            SaveState();
        _currentTarget = target;
        _savePath = Path.Combine(_pluginsDir, $"{target}.myplugin.json");
        LoadState();
    }
}
```

The Bookmarks plugin follows this pattern, creating files like `notepad.bookmarks.json`, `crackme.bookmarks.json`, etc.

### 16.3 Session Save/Load

For a comprehensive session save/load system (like the Session Manager plugin), save multiple types of data together:

```csharp
public class SessionData
{
    public List<BpEntry> Breakpoints { get; set; } = new();
    public List<AnnotationEntry> Annotations { get; set; } = new();
    public List<FunctionEntry> Functions { get; set; } = new();
    public List<BlockColorEntry> BlockColors { get; set; } = new();
    public List<ModuleEntry> Modules { get; set; } = new();  // For rebasing
}

// Save:
var data = new SessionData();

// Save breakpoints
foreach (var bp in _api.Breakpoints.GetAll())
    data.Breakpoints.Add(new BpEntry { Address = bp.Address, Type = (int)bp.Type });

// Save annotations
foreach (var (addr, text) in _api.UI.GetAllAnnotations())
    data.Annotations.Add(new AnnotationEntry { Address = addr, Text = text });

// Save user-defined functions
foreach (var f in _api.Symbols.GetRegisteredFunctions())
    data.Functions.Add(new FunctionEntry
    {
        Address = f.Address, Name = f.Name, Size = f.Size
    });

// Save module bases for rebasing
foreach (var mod in _api.Symbols.GetModules())
    data.Modules.Add(new ModuleEntry
    {
        Name = mod.Name, BaseAddress = mod.BaseAddress, Size = mod.Size
    });
```

### 16.4 Module Rebasing on Load

When loading a session, module base addresses may have changed (ASLR). The Session Manager plugin handles this with a rebase map:

```csharp
// Build rebase map: saved base -> current base
var currentModules = _api.Symbols.GetModules();
var rebaseMap = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

foreach (var saved in sessionData.Modules)
{
    var current = currentModules.FirstOrDefault(m =>
        m.Name.Equals(saved.Name, StringComparison.OrdinalIgnoreCase));
    if (current != null)
    {
        long delta = (long)current.BaseAddress - (long)saved.BaseAddress;
        rebaseMap[saved.Name] = delta;
    }
}

// Rebase function: adjust an address from saved session to current layout
ulong Rebase(ulong addr, List<ModuleEntry> savedModules)
{
    foreach (var mod in savedModules)
    {
        if (addr >= mod.BaseAddress && addr < mod.BaseAddress + mod.Size)
        {
            if (rebaseMap.TryGetValue(mod.Name, out long delta))
                return (ulong)((long)addr + delta);
            break;
        }
    }
    return addr; // Not in any saved module, return as-is
}

// Use during load
foreach (var bp in sessionData.Breakpoints)
{
    ulong addr = Rebase(bp.Address, sessionData.Modules);
    _api.Breakpoints.ToggleBreakpoint(addr, (PluginBreakpointType)bp.Type);
}
```

---

## 17. RegisterFunction and Size Parameter

### 17.1 Why Size Matters

The `RegisterFunction` method's `size` parameter is a critical but often overlooked feature. Without it, the debugger's symbol resolution (`ResolveAddress`) will only recognize the exact start address of your function. Any address inside the function body will return `null` or fall back to a less useful symbol.

Consider a function at `0x140005000` that is `0x120` bytes long:

| Scenario | `ResolveAddress(0x140005030)` Result |
|----------|--------------------------------------|
| `RegisterFunction(0x140005000, "Decrypt")` (no size) | `null` (not found) |
| `RegisterFunction(0x140005000, "Decrypt", 0x120)` (with size) | `"Decrypt+0x30"` |

The second form is dramatically more useful because:
- The disassembly view shows function names for all instructions in the function.
- Call stacks resolve correctly (even mid-function).
- Cross-reference analysis can identify function boundaries.
- The graph view can delineate function blocks.

### 17.2 Usage Patterns

**Registering functions discovered during analysis:**

```csharp
// After analyzing a packed binary and finding the OEP
_api.Symbols.RegisterFunction(oepAddress, "OEP_main", estimatedSize);

// Registering functions from a signature scan
foreach (var match in signatureMatches)
{
    _api.Symbols.RegisterFunction(match.Address, match.Name, match.Size);
}
```

**Estimating function size:**

If you don't know the exact size, you can estimate it by scanning for the function epilogue:

```csharp
uint EstimateFunctionSize(ulong funcStart, uint maxSize = 0x10000)
{
    byte[]? code = _api.Memory.ReadMemory(_api.TargetPid, funcStart, maxSize);
    if (code == null) return 0;

    // Look for common x64 epilogues: RET (C3) or INT3 padding after RET
    for (int i = 0; i < code.Length - 1; i++)
    {
        if (code[i] == 0xC3) // RET
        {
            // Check if followed by INT3 padding or another function prologue
            if (i + 1 < code.Length && (code[i + 1] == 0xCC || code[i + 1] == 0x40 ||
                code[i + 1] == 0x48 || code[i + 1] == 0x55))
            {
                return (uint)(i + 1);
            }
        }
    }
    return 0;
}
```

### 17.3 Interaction with ResolveAddress

User-defined functions (registered via `RegisterFunction`) take priority over export symbols and PDB symbols. This means you can override symbol names:

```csharp
// Override a symbol with a more meaningful name
_api.Symbols.RegisterFunction(addr, "RealDecryptionFunc", size);
// Now ResolveAddress returns "RealDecryptionFunc+0xNN" instead of "sub_140005000"
```

To remove a user-defined function:
```csharp
_api.Symbols.RegisterFunction(addr, null); // Unregister
// Now ResolveAddress falls back to export/PDB symbols
```

---

## 18. Anti-Debug API

KernelFlirt provides kernel-level anti-anti-debug capabilities through the `IProcessApi`. These methods directly modify kernel structures and are invisible to the target process.

### 18.1 ClearDebugPort

```csharp
bool ClearDebugPort(uint pid);
```

Zeroes the `EPROCESS.DebugPort` field for the target process. This single call defeats multiple common anti-debug checks:

| Anti-Debug Check | How ClearDebugPort Defeats It |
|-----------------|-------------------------------|
| `NtQueryInformationProcess(ProcessDebugPort)` | Returns 0 (no debugger) |
| `NtQueryInformationProcess(ProcessDebugObjectHandle)` | Returns STATUS_PORT_NOT_SET |
| `NtQueryInformationProcess(ProcessDebugFlags)` | Returns 1 (PROCESS_NO_DEBUG_INHERIT) |
| `NtClose(InvalidHandle)` with OB_FLAG_PROTECTCLOSE | No debug exception generated |

```csharp
_api.Process.ClearDebugPort(_api.TargetPid);
_api.Log.Info("DebugPort cleared");
```

**Important:** The debug port is created by the system when a debugger attaches. Since KernelFlirt does not use the standard debug API, there is typically no debug port to begin with. However, some anti-debug routines may create one as a probe. Call `ClearDebugPort` after the first break to be safe.

### 18.2 ClearThreadHide

```csharp
bool ClearThreadHide(uint pid);
```

Clears the `HideFromDebugger` flag in `ETHREAD.CrossThreadFlags` for all threads in the process.

Some packers call `NtSetInformationThread(ThreadHideFromDebugger)` on their threads to prevent debuggers from receiving debug events for those threads. This method reverses that.

```csharp
_api.Process.ClearThreadHide(_api.TargetPid);
_api.Log.Info("ThreadHideFromDebugger cleared for all threads");
```

### 18.3 NtQuerySystemInformation Hook

```csharp
bool InstallNtQsiHook();
bool RemoveNtQsiHook();
string ProbeNtQsiHook();
```

These methods install/remove an inline hook on `NtQuerySystemInformation` in the kernel to spoof `SystemKernelDebuggerInformation` (class 0x23). When hooked, the function reports that no kernel debugger is present.

**WARNING:** This patches kernel code and **triggers PatchGuard (KPP) BSOD** after approximately 5-10 minutes. Use only for short analysis sessions when absolutely necessary.

```csharp
// Install hook (use sparingly!)
bool ok = _api.Process.InstallNtQsiHook();
if (ok)
    _api.Log.Warning("NtQSI hook installed — PatchGuard will BSOD in ~5 min!");

// Probe to verify hook state
string info = _api.Process.ProbeNtQsiHook();
_api.Log.Info(info);

// Remove before PatchGuard fires
_api.Process.RemoveNtQsiHook();
```

### 18.4 SharedUserData Spoofing

```csharp
bool SetSpoofSharedUserData(bool enable);
```

`KUSER_SHARED_DATA` (mapped at `0x7FFE0000` in every process) contains the `KdDebuggerEnabled` field at offset `0x2D4`. Anti-debug code reads this field to detect kernel debuggers. When spoofing is enabled, KernelFlirt modifies this field to report no debugger.

```csharp
_api.Process.SetSpoofSharedUserData(true);
_api.Log.Info("SharedUserData spoofed: KdDebuggerEnabled = 0");
```

### 18.5 PEB Patching

For PEB-based anti-debug checks, use `IMemoryApi` to patch PEB fields directly:

```csharp
uint pid = _api.TargetPid;
var (peb, _) = _api.Process.GetPebAddress(pid);

// Clear PEB.BeingDebugged (offset 0x02)
_api.Memory.WriteMemory(pid, peb + 0x02, new byte[] { 0x00 });

// Fix PEB.NtGlobalFlag (offset 0x0BC): clear FLG_HEAP_ENABLE_TAIL_CHECK |
// FLG_HEAP_ENABLE_FREE_CHECK | FLG_HEAP_VALIDATE_PARAMETERS
byte[]? flags = _api.Memory.ReadMemory(pid, peb + 0xBC, 4);
if (flags != null)
{
    uint ntGlobalFlag = BitConverter.ToUInt32(flags) & ~0x70u; // Clear bits 4,5,6
    _api.Memory.WriteMemory(pid, peb + 0xBC, BitConverter.GetBytes(ntGlobalFlag));
}

// Fix heap flags: ProcessHeap at PEB+0x30
byte[]? heapPtrData = _api.Memory.ReadMemory(pid, peb + 0x30, 8);
if (heapPtrData != null)
{
    ulong heapBase = BitConverter.ToUInt64(heapPtrData);

    // _HEAP.Flags at offset 0x70: set to HEAP_GROWABLE (0x02)
    _api.Memory.WriteMemory(pid, heapBase + 0x70, BitConverter.GetBytes(0x02u));

    // _HEAP.ForceFlags at offset 0x74: set to 0
    _api.Memory.WriteMemory(pid, heapBase + 0x74, BitConverter.GetBytes(0x00u));
}
```

### 18.6 Building a Complete Anti-Debug Plugin

The Anti-Anti-Debug sample plugin (`samples/AntiDebugPlugin`) provides a comprehensive UI with checkboxes for each anti-debug technique. Here is the architectural pattern:

```csharp
public class AntiDebugPlugin : IKernelFlirtPlugin
{
    public string Name => "Anti-Anti-Debug";
    public string Description => "ScyllaHide-style anti-anti-debug";
    public string Version => "2.0";

    private IDebuggerApi? _api;
    private AntiDebugPanel? _panel;

    public void Initialize(IDebuggerApi api)
    {
        _api = api;
        _panel = new AntiDebugPanel(api);

        api.UI.AddToolPanel("Anti-Debug", _panel);
        api.UI.AddMenuItem("Apply Anti-Debug patches", () => _panel.ApplyPatches());

        // Auto-apply on connect and break
        api.OnConnected += () =>
        {
            if (_panel.AutoApply)
                Application.Current.Dispatcher.BeginInvoke(
                    () => _panel.ApplyKernelPatches());
        };
        api.OnBreakStateEntered += () =>
        {
            if (_panel.AutoApply && _api.IsBreakState)
                Application.Current.Dispatcher.Invoke(
                    () => _panel.ApplyPatches());
        };
    }

    public void Shutdown() { }
}
```

The panel provides checkboxes for each anti-debug technique:
- PEB.BeingDebugged
- PEB.NtGlobalFlag
- Heap Flags
- Debug Port (ClearDebugPort)
- Thread Hide From Debugger (ClearThreadHide)
- SystemKernelDebuggerInformation (NtQSI hook)
- SharedUserData
- And many more...

---

## 19. Complete Example Plugins

### 19.1 Simple: Menu Item Logger

This plugin adds a menu item that logs the current RIP and its symbol name.

```csharp
using KernelFlirt.SDK;

namespace RipLoggerPlugin;

public class Plugin : IKernelFlirtPlugin
{
    public string Name => "RIP Logger";
    public string Description => "Log current RIP with symbol resolution";
    public string Version => "1.0";

    private IDebuggerApi _api = null!;

    public void Initialize(IDebuggerApi api)
    {
        _api = api;
        api.UI.AddMenuItem("Log _RIP", OnLogRip);
        api.Log.Info("[RipLogger] Ready. Use Plugins > Log RIP in break state.");
    }

    public void Shutdown() { }

    private void OnLogRip()
    {
        if (!_api.IsBreakState)
        {
            _api.Log.Warning("Must be in break state to log RIP");
            return;
        }

        var regs = _api.Memory.ReadRegisters(_api.TargetPid, _api.SelectedThreadId);
        var rip = regs.FirstOrDefault(r => r.Name == "RIP");
        if (rip == null)
        {
            _api.Log.Error("Could not read RIP");
            return;
        }

        string? symbol = _api.Symbols.ResolveAddress(rip.Value);
        string symbolStr = symbol ?? "(unknown)";
        _api.Log.Info($"RIP = {rip.Value:X16}  [{symbolStr}]");

        // Also log the first 4 function arguments (x64 calling convention)
        ulong rcx = regs.First(r => r.Name == "RCX").Value;
        ulong rdx = regs.First(r => r.Name == "RDX").Value;
        ulong r8  = regs.First(r => r.Name == "R8").Value;
        ulong r9  = regs.First(r => r.Name == "R9").Value;
        _api.Log.Info($"  Args: RCX={rcx:X16} RDX={rdx:X16} R8={r8:X16} R9={r9:X16}");
    }
}
```

### 19.2 Medium: Bookmarks Panel with DataGrid

This is a simplified version of the Bookmarks plugin showing the full panel-with-DataGrid pattern:

```csharp
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using KernelFlirt.SDK;

namespace SimpleBookmarks;

public class Bookmark
{
    public ulong Address { get; set; }
    public string Note { get; set; } = "";
    public string AddressHex => $"{Address:X16}";
}

public class Plugin : IKernelFlirtPlugin
{
    public string Name => "Simple Bookmarks";
    public string Description => "Minimal bookmarks with persistence";
    public string Version => "1.0";

    private IDebuggerApi _api = null!;
    private readonly List<Bookmark> _bookmarks = [];
    private DataGrid _grid = null!;
    private string _savePath = "";

    public void Initialize(IDebuggerApi api)
    {
        _api = api;
        _savePath = Path.Combine(AppContext.BaseDirectory, "plugins", "bookmarks.json");

        LoadFromDisk();
        BuildUi();

        api.UI.AddMenuItem("_Bookmark RIP", OnBookmarkRip);
    }

    public void Shutdown() => SaveToDisk();

    private void BuildUi()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(1, GridUnitType.Star)
        });

        // Toolbar
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(4)
        };

        var removeBtn = new Button
        {
            Content = "Remove",
            Padding = new Thickness(8, 2, 8, 2)
        };
        removeBtn.Click += (_, _) =>
        {
            if (_grid.SelectedItem is Bookmark bm)
            {
                _bookmarks.Remove(bm);
                RefreshGrid();
                SaveToDisk();
            }
        };
        toolbar.Children.Add(removeBtn);
        Grid.SetRow(toolbar, 0);
        root.Children.Add(toolbar);

        // DataGrid
        _grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            SelectionMode = DataGridSelectionMode.Single,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.None,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            RowBackground = Brushes.Transparent,
            AlternatingRowBackground = new SolidColorBrush(
                Color.FromArgb(20, 255, 255, 255)),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12
        };

        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Address",
            Binding = new Binding("AddressHex"),
            Width = 160
        });
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Note",
            Binding = new Binding("Note"),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star)
        });

        _grid.MouseDoubleClick += (_, _) =>
        {
            if (_grid.SelectedItem is Bookmark bm)
                _api.UI.NavigateDisassembly(bm.Address);
        };

        Grid.SetRow(_grid, 1);
        root.Children.Add(_grid);

        RefreshGrid();
        _api.UI.AddToolPanel("Bookmarks", root);
    }

    private void OnBookmarkRip()
    {
        if (!_api.IsBreakState) return;
        var regs = _api.Memory.ReadRegisters(_api.TargetPid, _api.SelectedThreadId);
        var rip = regs.First(r => r.Name == "RIP");

        if (_bookmarks.Any(b => b.Address == rip.Value)) return;

        _bookmarks.Add(new Bookmark { Address = rip.Value, Note = "Bookmarked" });
        RefreshGrid();
        SaveToDisk();
    }

    private void RefreshGrid()
    {
        _grid.ItemsSource = null;
        _grid.ItemsSource = _bookmarks;
    }

    private void SaveToDisk()
    {
        try
        {
            var json = JsonSerializer.Serialize(_bookmarks,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_savePath, json);
        }
        catch { }
    }

    private void LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_savePath)) return;
            var loaded = JsonSerializer.Deserialize<List<Bookmark>>(
                File.ReadAllText(_savePath));
            if (loaded != null) _bookmarks.AddRange(loaded);
        }
        catch { }
    }
}
```

### 19.3 Complex: Background Memory Scanner

This example demonstrates async scanning with cancellation, progress bar, and chunked memory reads. See the full `MemoryScannerPlugin` sample in `samples/MemoryScannerPlugin/` for the production version.

Key patterns used:
- `CancellationTokenSource` for stop button
- `Task.Run` for background scanning
- `Dispatcher.BeginInvoke` for periodic UI updates
- Chunked memory reads (256KB per read) with overlap to catch patterns spanning chunks
- Multiple scan modes (main module, all modules, full process memory)
- AOB (Array of Bytes) pattern with wildcard support (`??`)

```csharp
// Core scan loop (simplified)
await Task.Run(() =>
{
    foreach (var (baseAddr, size, modName) in regions)
    {
        if (ct.IsCancellationRequested) break;

        const uint CHUNK = 256 * 1024;
        uint overlap = (uint)pattern.Length - 1;

        for (ulong offset = 0; offset < size; )
        {
            if (ct.IsCancellationRequested) break;

            uint readSize = (uint)Math.Min(CHUNK, size - offset);
            var data = _api.Memory.ReadMemory(pid, baseAddr + offset, readSize);

            if (data != null)
            {
                for (int i = 0; i <= data.Length - pattern.Length; i += alignment)
                {
                    if (MatchesPattern(data, i, pattern))
                    {
                        ulong hitAddr = baseAddr + offset + (ulong)i;
                        // ... add result, update UI ...
                    }
                }
            }

            // Advance with overlap to catch cross-chunk matches
            offset += readSize > overlap ? readSize - overlap : readSize;
        }
    }
}, ct);
```

### 19.4 Advanced: API Monitor with Debug Event Filters

The API Monitor plugin (`samples/ApiMonitorPlugin/`) demonstrates the most advanced plugin pattern: intercepting API calls using breakpoints and `OnDebugEventFilter`:

1. **Setup phase:** Resolve API addresses with `ResolveNameToAddress`, set software breakpoints on each API entry point.

2. **Entry handler:** When a breakpoint is hit at an API entry:
   - Read function arguments from registers (RCX, RDX, R8, R9) and stack.
   - Parse arguments based on the API definition (string parameters, handles, flags, etc.).
   - Read the return address from `[RSP]`.
   - Set a temporary breakpoint at the return address.
   - Use `ContinueMode = 1` (StepPast) to continue execution.
   - Return `true` from the filter to suppress the UI break.

3. **Return handler:** When the return breakpoint is hit:
   - Read the return value from RAX.
   - Log the complete API call with arguments and return value.
   - Remove the return breakpoint.
   - Use `ContinueMode = 1` (StepPast) to continue.
   - Return `true` to suppress.

4. **UI panel:** Displays a live-updating DataGrid with columns for index, time, thread, API name, arguments, and return value. Supports filtering by API category, searching, and exporting.

The full source code is in `samples/ApiMonitorPlugin/ApiMonitorPlugin.cs`.

---

## 20. Best Practices and Common Pitfalls

### 20.1 Do's

1. **Always check `IsConnected` and `IsBreakState`** before reading memory or registers:
   ```csharp
   if (!_api.IsConnected || !_api.IsBreakState) return;
   ```

2. **Always check for `null` returns** from `ReadMemory`:
   ```csharp
   var data = _api.Memory.ReadMemory(pid, addr, size);
   if (data == null) { _api.Log.Warning("Read failed"); return; }
   ```

3. **Use `BeginInvoke` (not `Invoke`)** for UI updates from background threads to avoid deadlocks:
   ```csharp
   Application.Current.Dispatcher.BeginInvoke(() => UpdateUi());
   ```

4. **Save state in `Shutdown()`** to preserve data across sessions.

5. **Use `ToggleBreakpoint`** when you want the UI to reflect breakpoint changes. Use `SetBreakpoint` for silent/internal breakpoints.

6. **Always specify `size` in `RegisterFunction`** to get proper symbol resolution across the entire function body.

7. **Cancel background tasks in `Shutdown()`**:
   ```csharp
   public void Shutdown() => _cts?.Cancel();
   ```

8. **Prefix log messages** with your plugin name for clarity:
   ```csharp
   _api.Log.Info("[MyPlugin] Analysis complete");
   ```

9. **Use `Consolas` font** for addresses and hex data in UI elements for proper alignment.

10. **Handle exceptions** in event handlers to prevent crashing the host:
    ```csharp
    api.OnBreakStateEntered += () =>
    {
        try { UpdatePanel(); }
        catch (Exception ex) { api.Log.Error($"[MyPlugin] {ex.Message}"); }
    };
    ```

### 20.2 Don'ts

1. **Don't copy `KernelFlirt.SDK.dll`** to your output folder. The SDK is provided by the host application. Including a duplicate causes type identity conflicts and your plugin will fail to load.

2. **Don't block the UI thread** with long-running operations. Use `Task.Run` for heavy work:
   ```csharp
   // BAD: blocks UI
   for (int i = 0; i < 10000; i++)
       _api.Memory.ReadMemory(pid, addr + (ulong)i * 8, 8);

   // GOOD: runs in background
   await Task.Run(() => { /* heavy work */ });
   ```

3. **Don't call WPF methods from background threads** without dispatching:
   ```csharp
   // BAD: crashes
   _grid.ItemsSource = results;

   // GOOD: dispatched
   Application.Current.Dispatcher.BeginInvoke(() => _grid.ItemsSource = results);
   ```

4. **Don't forget to return `true` from `OnDebugEventFilter`** when you handle the event. Returning `false` after calling `Continue()` or setting `ContinueMode` causes undefined behavior (the UI tries to process an event that is already being continued).

5. **Don't store references to `PluginDebugEvent` objects** beyond the scope of the filter callback. The object may be reused or modified by the framework.

6. **Don't use `Invoke` (synchronous dispatch) from `OnDebugEventFilter`** -- it can deadlock if the UI thread is waiting for the debug event to be processed.

7. **Don't ignore the return value of `SetBreakpoint`** -- it returns `null` when the breakpoint could not be set (e.g., all hardware registers are in use).

8. **Don't call `AddToolPanel` or `AddMenuItem` from `Shutdown`** -- the UI is being torn down and these calls will fail.

### 20.3 Common Pitfalls

**Pitfall 1: Plugin DLL not loading**

Symptoms: Plugin does not appear in the plugin list. No error messages.

Causes:
- Missing `EnableDynamicLoading` in `.csproj`. The SDK assembly cannot be resolved at runtime.
- `KernelFlirt.SDK.dll` is present in the `plugins/` folder (type conflict).
- Wrong target framework (`net8.0` instead of `net9.0-windows`).
- Missing `<UseWPF>true</UseWPF>` when using WPF types.
- Assembly loading exception silently caught by the host. Check the KernelFlirt log output.

**Pitfall 2: UI updates not appearing**

Symptoms: Code runs without errors but the UI does not update.

Causes:
- Calling WPF methods from a background thread without `Dispatcher.BeginInvoke`.
- DataGrid binding not refreshed -- call `_grid.ItemsSource = null; _grid.ItemsSource = _list;`.
- Forgetting to call `RefreshDisassembly()` after annotation changes.

**Pitfall 3: Deadlock on Dispatcher.Invoke**

Symptoms: Application freezes.

Cause: Calling `Dispatcher.Invoke` (synchronous) from `OnDebugEventFilter` while the UI thread is waiting for the event to complete.

Fix: Always use `Dispatcher.BeginInvoke` (asynchronous) in event filter handlers.

**Pitfall 4: Memory read returns null unexpectedly**

Causes:
- Target is not in break state (`IsBreakState == false`).
- Address is invalid or paged out.
- Process ID is wrong (using 0 instead of `_api.TargetPid`).
- Reading kernel memory with a user-mode PID.

**Pitfall 5: Breakpoint not hitting**

Causes:
- Using `SetBreakpoint` with wrong PID/TID.
- For hardware breakpoints: all 4 debug registers are already in use.
- The address is in a different module that has not been loaded yet.
- ASLR: the address has changed since the last session.

**Pitfall 6: Event handler called after plugin expects to be stopped**

Cause: Event handlers remain subscribed after the plugin logically "stops" an operation but does not unsubscribe.

Fix: Track your active state and early-return from handlers:

```csharp
private bool _isMonitoring;

private bool OnFilter(PluginDebugEvent evt)
{
    if (!_isMonitoring) return false; // Not active
    // ... handle event ...
}
```

### 20.4 Performance Considerations

1. **Minimize memory reads in `OnDebugEventFilter`**: Each `ReadMemory` call is a kernel IOCTL roundtrip. Read only what you need.

2. **Batch memory reads**: Instead of reading many small blocks, read one large block and parse it in memory:
   ```csharp
   // BAD: 100 IOCTLs
   for (int i = 0; i < 100; i++)
       _api.Memory.ReadMemory(pid, base + (ulong)i * 8, 8);

   // GOOD: 1 IOCTL
   byte[]? block = _api.Memory.ReadMemory(pid, base, 800);
   for (int i = 0; i < 100; i++)
       ulong val = BitConverter.ToUInt64(block!, i * 8);
   ```

3. **Use `ContinueMode = 4` (Trace)** for stepping through long code sequences instead of per-step `OnDebugEventFilter` callbacks.

4. **Throttle UI updates**: Don't update the DataGrid for every single result during a scan. Batch updates:
   ```csharp
   if (_results.Count % 100 == 0)
       Application.Current?.Dispatcher.BeginInvoke(() => RefreshGrid());
   ```

5. **Use chunked reading with overlap** for pattern scanning across large memory regions (see MemoryScanner sample).

---

## 21. Architecture Deep Dive

### 21.1 Kernel Driver Communication

KernelFlirt communicates with the target system through a custom kernel driver. The driver hooks `KdpStub` in `ntoskrnl.exe`, which is the function called by the kernel for debug exceptions (`#BP` and `#DB` trap handlers).

The communication path:
```
Plugin API call
    -> IDebuggerApi implementation
        -> IOCTL to kernel driver (DeviceIoControl)
            -> Driver reads/writes kernel/process memory
        <- IOCTL response
    <- API return value
```

For breakpoints, the driver:
- **Software BP**: Saves the original byte, writes `0xCC` (INT3). On hit, restores the byte, reports the event, and optionally re-sets the breakpoint when stepping past.
- **Hardware BP**: Programs the debug registers (DR0-DR3, DR7) in the thread's context.
- **Memory BP**: Changes page protection to `PAGE_NOACCESS`/`PAGE_GUARD`.

### 21.2 Remote Debugging via KfRelay

For debugging virtual machines, KernelFlirt uses a relay architecture:

```
KernelFlirt UI (host)  <--TCP-->  KfRelay.exe (VM)  <--IOCTL-->  Kernel Driver (VM)
```

`KfRelay.exe` runs inside the VM and forwards IOCTLs over TCP. This allows the debugger UI to run on the host machine while the driver operates inside the VM. The relay is transparent to plugins -- all API calls work identically regardless of whether the target is local or remote.

### 21.3 Debug Event Pipeline

When a debug exception occurs in the target:

```
1. CPU exception (#BP or #DB)
2. ntoskrnl trap handler
3. KdpStub (hooked by KernelFlirt driver)
4. Driver suspends all threads, reads context
5. Driver signals event to user mode
6. KernelFlirt UI receives event
7. OnDebugEventFilter handlers called (background thread)
   - If any handler returns true: event is suppressed, ContinueMode is applied
   - If all handlers return false: event proceeds to UI
8. UI updates (disassembly, registers, etc.)
9. OnBreakStateEntered fires (UI thread)
10. OnDebugEvent fires (UI thread)
11. User presses F9 (or plugin calls Continue())
12. OnBeforeRun fires (UI thread)
13. OnBreakStateExited fires (UI thread)
14. Driver resumes threads
```

### 21.4 Symbol Engine

KernelFlirt uses `dbghelp.dll` from the Windows SDK (the full version, not the stripped System32 version) for symbol resolution. It connects to the Microsoft Symbol Server to download PDB files for system DLLs.

Symbol resolution order:
1. User-defined functions (`RegisterFunction`)
2. Export symbols from PE export tables
3. PDB symbols (downloaded from symbol server)
4. Module name + offset fallback

---

## 22. Appendix A: Sample Plugins Overview

The `samples/` directory contains the following plugins:

| Plugin | Directory | Description |
|--------|-----------|-------------|
| **Bookmarks** | `BookmarksPlugin/` | Address bookmarks with notes, persisted to JSON. Integrates with disassembly annotations. |
| **Xrefs** | `XrefsPlugin/` | Cross-reference analysis: find callers/references to a given address by scanning `.text` sections for CALL and MOV instructions. |
| **Session Manager** | `SessionPlugin/` | Save and load complete debugging sessions (breakpoints, annotations, function names, graph colors) with ASLR rebase support. |
| **Graph View** | `GraphViewPlugin/` | Control flow graph (CFG) visualization using MSAGL layout engine. |
| **Memory Scanner** | `MemoryScannerPlugin/` | AOB (Array of Bytes) pattern scanner with wildcard support. Scans process memory in chunked reads with background threading. |
| **Anti-Anti-Debug** | `AntiDebugPlugin/` | Comprehensive anti-debug bypass with checkbox UI for each technique (PEB, DebugPort, NtQSI, SharedUserData, DRx, timing, etc.). |
| **API Monitor** | `ApiMonitorPlugin/` | Intercept and log WinAPI calls using breakpoints and `OnDebugEventFilter`. Captures arguments and return values. |
| **Scripting** | `ScriptingPlugin/` | C# scripting console using Roslyn. Full debugger API available as globals in scripts. |
| **Signature Detector** | `SignatureDetector/` | PEiD-style packer/compiler signature detection by scanning PE sections for known byte patterns. |
| **PE Rebuilder** | `PeRebuilder/` | Dump and rebuild PE files from memory. Includes import reconstruction and export resolution. |
| **VulnHunter** | `VulnHunterPlugin/` | Vulnerability hunting: scans imports for dangerous API calls (strcpy, sprintf, etc.) and monitors them at runtime. |
| **Network Monitor** | `NetworkMonitorPlugin/` | Monitor network-related API calls (connect, send, recv, etc.). |
| **FLIRT** | `FlirtPlugin/` | Function Library Identification and Recognition Technology. Identifies known library functions by byte patterns. |
| **String Decryptor** | `StringDecryptorPlugin/` | Automated string decryption by tracing through decryption routines. |
| **Themida** | `ThemidaPlugin/` | Themida/WinLicense unpacker using `OnDebugEventFilter` for OEP finding and IAT reconstruction. |
| **AI Assistant** | `AiAssistantPlugin/` | AI-powered analysis assistant using Claude/OpenAI APIs. Reads debugger context and provides analysis. |
| **MCP Server** | `McpServerPlugin/` | Model Context Protocol server that exposes the debugger API over HTTP for AI tool integration. |

---

## 23. Appendix B: Windows Memory Protection Constants

These constants are used with `IMemoryApi.ProtectMemory()`:

| Constant | Value | Meaning |
|----------|-------|---------|
| `PAGE_NOACCESS` | `0x01` | All access disabled. Any access triggers an access violation. |
| `PAGE_READONLY` | `0x02` | Read-only access. Writes trigger AV. |
| `PAGE_READWRITE` | `0x04` | Read and write access. |
| `PAGE_WRITECOPY` | `0x08` | Copy-on-write. First write creates a private copy. |
| `PAGE_EXECUTE` | `0x10` | Execute-only. Reads and writes trigger AV. |
| `PAGE_EXECUTE_READ` | `0x20` | Execute and read. Writes trigger AV. |
| `PAGE_EXECUTE_READWRITE` | `0x40` | Full access (execute, read, write). |
| `PAGE_EXECUTE_WRITECOPY` | `0x80` | Execute, read, and copy-on-write. |
| `PAGE_GUARD` | `0x100` | Guard page modifier (combine with above). First access triggers `STATUS_GUARD_PAGE_VIOLATION` and removes the guard. |
| `PAGE_NOCACHE` | `0x200` | Non-cacheable memory modifier. |
| `PAGE_WRITECOMBINE` | `0x400` | Write-combine memory modifier. |

---

## 24. Appendix C: Register Names

The following register names are returned by `IMemoryApi.ReadRegisters()`:

**General Purpose Registers (IsFlag = false):**

| Name | Description | Windows x64 Calling Convention |
|------|-------------|-------------------------------|
| `RAX` | Accumulator | Return value |
| `RBX` | Base | Callee-saved (non-volatile) |
| `RCX` | Counter | 1st argument |
| `RDX` | Data | 2nd argument |
| `RSI` | Source Index | Callee-saved (non-volatile) |
| `RDI` | Destination Index | Callee-saved (non-volatile) |
| `RBP` | Base Pointer | Callee-saved (non-volatile) |
| `RSP` | Stack Pointer | Callee-saved (non-volatile) |
| `R8` | General | 3rd argument |
| `R9` | General | 4th argument |
| `R10` | General | Caller-saved (volatile) |
| `R11` | General | Caller-saved (volatile) |
| `R12` | General | Callee-saved (non-volatile) |
| `R13` | General | Callee-saved (non-volatile) |
| `R14` | General | Callee-saved (non-volatile) |
| `R15` | General | Callee-saved (non-volatile) |

**Special Registers (IsFlag = false):**

| Name | Description |
|------|-------------|
| `RIP` | Instruction Pointer |
| `RFLAGS` | Flags register (full 64-bit value) |

**Individual CPU Flags (IsFlag = true):**

| Name | Bit | Description |
|------|-----|-------------|
| `CF` | 0 | Carry Flag |
| `PF` | 2 | Parity Flag |
| `AF` | 4 | Auxiliary Carry Flag |
| `ZF` | 6 | Zero Flag |
| `SF` | 7 | Sign Flag |
| `TF` | 8 | Trap Flag (single-step) |
| `IF` | 9 | Interrupt Enable Flag |
| `DF` | 10 | Direction Flag |
| `OF` | 11 | Overflow Flag |

**Debug Registers (IsFlag = false):**

| Name | Description |
|------|-------------|
| `DR0` | Debug Address Register 0 (hardware breakpoint address) |
| `DR1` | Debug Address Register 1 |
| `DR2` | Debug Address Register 2 |
| `DR3` | Debug Address Register 3 |
| `DR6` | Debug Status Register (which DR triggered, single-step, etc.) |
| `DR7` | Debug Control Register (enables/configures DR0-DR3) |

**Segment Registers (IsFlag = false):**

| Name | Description |
|------|-------------|
| `CS` | Code Segment |
| `DS` | Data Segment |
| `ES` | Extra Segment |
| `FS` | FS Segment (TEB on x86, not commonly used on x64) |
| `GS` | GS Segment (TEB on x64, kernel PCR) |
| `SS` | Stack Segment |

---

*This document is part of the KernelFlirt project. For questions, bug reports, or feature requests, see the project repository.*
