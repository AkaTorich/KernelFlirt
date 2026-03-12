# KernelFlirt

Windows kernel-level debugger with an OllyDbg-style interface. Designed for security research and reverse engineering in VM environments.

## Architecture

```
┌────────────────────────┐     DeviceIoControl      ┌──────────────────────┐
│   KernelFlirt UI       │◄────────────────────────► │  KernelFlirt.sys     │
│   (WPF / C# / .NET 9)  │    \\.\KernelFlirt       │  (WDM Kernel Driver) │
└────────────────────────┘                           └──────────────────────┘
                                                              ▲
┌────────────────────────┐     SCM API                        │
│   KfLoader.exe         │───── CreateService / StartService ─┘
│   (C / Console)        │
└────────────────────────┘
```

Three components:

| Component | Language | Description |
|-----------|----------|-------------|
| **KernelFlirt.UI** | C# / WPF | OllyDbg-style debugger interface |
| **KernelFlirt.sys** | C / WDM | Kernel driver — memory, breakpoints, debug hooks |
| **KfLoader.exe** | C | CLI tool to load/unload the driver via SCM |

## Features

### Debugging
- **Software breakpoints** — INT3 injection with automatic byte restore
- **Hardware breakpoints** — DR0-DR3 execute breakpoints (up to 4 simultaneous)
- **Hardware watchpoints** — DR0-DR3 write and read/write data watchpoints with configurable length (1/2/4/8 bytes)
- **Memory breakpoints** — PAGE_GUARD-based memory access detection via ZwProtectVirtualMemory
- **Conditional breakpoints** — Break only when expression is true (e.g. `RAX==0`, `RCX!=0`, `RDX>0x100`)
- **Log breakpoints** — Log register/expression values without breaking execution
- **Hit count** — Each breakpoint tracks how many times it was triggered
- **Single step** (F7) — TF flag manipulation via PsSetContextThread
- **Step over** (F8) — Temporary INT3 at next instruction to skip CALL
- **Step out** (Ctrl+F9) — Temporary INT3 at return address [RSP] to leave current function
- **Run to cursor** (F4) — Temporary INT3 at selected disassembly address
- **Process attach/detach** — Suspends main thread on attach, resumes all threads on detach

### Memory
- **Read/write process memory** — Via MmCopyVirtualMemory (up to 1MB per read)
- **Hex dump view** — Classic 16 bytes/line hex+ASCII display with address navigation
- **Binary search** — Find byte patterns in memory with `??` wildcard support
- **String search** — Find ASCII and Unicode strings across all loaded modules
- **Intermodular calls** — Find CALL instructions that target other modules (API calls)
- **Patches tracking** — All memory modifications are recorded, can be restored individually or all at once

### Introspection
- **Module enumeration** — PEB->Ldr->InMemoryOrderModuleList walk for user-mode DLLs
- **Kernel module enumeration** — ZwQuerySystemInformation(SystemModuleInformation) for loaded drivers
- **Thread enumeration** — Full thread list with state, priority, start address
- **Thread suspend/resume** — Individual thread control
- **Thread switching** — Switch debug context to any thread
- **Register read/write** — Full x64 CONTEXT: RAX-R15, RIP, RFLAGS, segments, DR0-DR3/DR6/DR7
- **Call stack** — Heuristic stack walk with return address resolution to module+offset symbols
- **SEH chain** — Structured exception handler chain enumeration
- **Bookmarks** — Save labeled addresses for quick navigation

### Kernel Debug Hook
- **KiDebugRoutine hook** — Intercepts #DB/#BP traps at kernel level, like WinDbg/KD
- **Pattern scanning** — Finds KiDebugRoutine pointer by scanning KdChangeOption for RIP-relative MOV/LEA instructions
- **Inverted call model** — Pending IRP completed when debug event fires
- **Thread blocking** — Faulting thread blocked via KeWaitForSingleObject until UI sends CONTINUE
- **Atomic install** — InterlockedExchangePointer for safe hook swap
- **Event types** — Breakpoint, Single Step, HW Breakpoint, HW Watchpoint, Memory BP (PAGE_GUARD)

### UI (OllyDbg-style)
- **Dark theme** — Custom color palette: dark background (#1E1E1E), blue addresses, yellow mnemonics, green registers, red jumps
- **Disassembly view** — Per-token syntax highlighting with breakpoint markers (red dot), current instruction highlight, backtick address format
- **Registers panel** — Changed values highlighted in red, right-click Follow in Dump/Disasm
- **Stack panel** — RSP-relative display, right-click Follow/Copy
- **Hex dump panel** — 16 bytes/line with ASCII sidebar, right-click Copy/Search
- **10 bottom tabs**: Breakpoints, Modules, Kernel Modules, Threads, Call Stack, Bookmarks, Patches, SEH Chain, Search, Log
- **Right-click context menus on every panel** — Follow in Dump, Follow in Disassembler, Copy, Toggle BP, Search, etc.
- **Process picker dialog** — Filter by name or PID, double-click to attach
- **Input dialogs** — For conditional breakpoints, log expressions, bookmarks, search patterns

### Loader
- **Service management** — Load/unload/status/info via Windows SCM API
- **VM detection** — CPUID leaf 0x40000000 hypervisor vendor detection (VMware, VirtualBox, Hyper-V, KVM, Xen)
- **Test signing check** — NtQuerySystemInformation(SystemCodeIntegrityInformation) CODEINTEGRITY_OPTION_TESTSIGN flag

## Keyboard Shortcuts

| Key | Action |
|-----|--------|
| F2 | Toggle software breakpoint at selected address |
| F4 | Run to cursor |
| F5 | Continue execution (resume from debug hook) |
| F7 | Step into |
| F8 | Step over |
| F9 | Run |
| F12 | Pause (suspend thread) |
| Ctrl+G | Go to RIP |
| Ctrl+F9 | Step out (execute till return) |
| Ctrl+F | Search binary pattern |

## UI Layout

```
┌──────────────────────────────────────────────────────────────┐
│ File | Debug | Search | View                                 │
│ [Open][Connect] [PID][Attach][Detach] [Run][Pause][F5][F7]   │
│ [F8][Out][F4] [BP][HW][WW][RW][Mem] [Hook][Unhook] [Addr Go]│
├──────────────────────────────┬───────────────────────────────┤
│  Disassembly                 │  Registers                    │
│  ● 00007FF6`00401000  ...    │  RAX  0000000000000001        │
│    00007FF6`00401005  ...    │  RBX  0000000000000000        │
│    Right-click: BP, Follow,  │  Right-click: Follow, Copy    │
│    Copy, Search, Bookmark    ├───────────────────────────────┤
│                              │  Stack                        │
│                              │  RSP+00  00007FF600401234     │
│                              │  Right-click: Follow, Copy    │
├──────────────────────────────┴───────────────────────────────┤
│  Hex Dump  [Address: ___________] [Go]                       │
│  00007FF600400000  48 89 5C 24 08 48 89 6C  H.\$.H.l        │
│  Right-click: Copy, Follow, Search Binary, Search String     │
├──────────────────────────────────────────────────────────────┤
│ Breakpoints│Modules│KernelMod│Threads│CallStack│Bookmarks│   │
│ Patches│SEH Chain│Search│Log                                 │
│ Each tab has context menu: Follow, Copy, Remove, etc.        │
└──────────────────────────────────────────────────────────────┘
```

## IOCTL Protocol

Device: `\\.\KernelFlirt` — Method: `METHOD_BUFFERED` — Device type: `0x8000`

| IOCTL | Code | Input | Output |
|-------|------|-------|--------|
| READ_MEMORY | 0x800 | PID, Address, Size | byte[] |
| WRITE_MEMORY | 0x801 | PID, Address, Size, Data | NTSTATUS |
| SET_BREAKPOINT | 0x802 | PID, TID, Address, Type, Length | Handle |
| REMOVE_BREAKPOINT | 0x803 | Handle | — |
| SINGLE_STEP | 0x804 | PID, TID | — |
| READ_REGISTERS | 0x810 | PID, TID | KF_REGISTERS |
| WRITE_REGISTERS | 0x811 | PID, TID, KF_REGISTERS | — |
| ENUM_MODULES | 0x820 | PID | KF_MODULE_ENTRY[] |
| ENUM_KERNEL_MODULES | 0x821 | — | KF_KERNEL_MODULE_ENTRY[] |
| ENUM_THREADS | 0x830 | PID | KF_THREAD_ENTRY[] |
| SUSPEND_THREAD | 0x831 | TID | — |
| RESUME_THREAD | 0x832 | TID | — |
| INSTALL_HOOK | 0x840 | — | — |
| REMOVE_HOOK | 0x841 | — | — |
| WAIT_DEBUG_EVENT | 0x842 | — | KF_DEBUG_EVENT |
| CONTINUE_DEBUG_EVENT | 0x843 | — | — |
| PING | 0x8FF | — | Version, Magic |

### Breakpoint Types

| Type | Code | Mechanism |
|------|------|-----------|
| Software | 0 | INT3 (0xCC) byte injection |
| Hardware Execute | 1 | DR0-DR3, condition=00 |
| Hardware Write | 2 | DR0-DR3, condition=01 |
| Hardware Read/Write | 3 | DR0-DR3, condition=11 |
| Memory | 4 | PAGE_GUARD via ZwProtectVirtualMemory |

### Debug Event Types

| Type | Code | Trigger |
|------|------|---------|
| Breakpoint | 1 | STATUS_BREAKPOINT (INT3) |
| Single Step | 2 | STATUS_SINGLE_STEP (TF flag) |
| HW Breakpoint | 3 | DR0-3 execute, DR6 bit set |
| HW Watchpoint | 4 | DR0-3 write/RW, DR7 condition != 0 |
| Memory BP | 5 | STATUS_GUARD_PAGE_VIOLATION |

## Building

### Requirements
- Visual Studio 2022 with C++ desktop workload
- Windows Driver Kit (WDK) 10.0.26100.0+
- .NET 9 SDK
- Windows 10/11 x64

### Build

```bash
# Driver (kernel)
MSBuild src/driver/driver.vcxproj -p:Configuration=Release -p:Platform=x64

# Loader (usermode CLI)
MSBuild src/loader/loader.vcxproj -p:Configuration=Release -p:Platform=x64

# UI (WPF)
dotnet build src/ui/KernelFlirt.UI.csproj -c Release
```

### Output
- `src/driver/build/driver/Release/KernelFlirt.sys`
- `src/loader/build/loader/Release/KfLoader.exe`
- `src/ui/bin/Release/net9.0-windows/KernelFlirt.exe`

## Usage

```bash
# 1. Enable test signing (requires reboot)
bcdedit /set testsigning on

# 2. Load the driver
KfLoader.exe load --path KernelFlirt.sys

# 3. Check status
KfLoader.exe status

# 4. Launch the UI
KernelFlirt.exe

# 5. In the UI:
#    - Click "Connect" to connect to the driver
#    - Click "Open" to select a process, or enter PID and click "Attach"
#    - Use F7/F8/F9 to debug
#    - Right-click anywhere for context menu

# 6. Unload when done
KfLoader.exe unload
```

## Project Structure

```
KernelFlirt/
├── KernelFlirt.sln
├── README.md
├── README.ru.md
├── include/
│   └── kf_shared.h                 # Shared IOCTL codes and structures
├── src/
│   ├── driver/                      # Kernel driver (C / WDM)
│   │   ├── driver.vcxproj
│   │   ├── main.c                   # DriverEntry, Unload, dispatch
│   │   ├── device.c                 # Device creation, symbolic link
│   │   ├── ioctl.c                  # IOCTL dispatcher
│   │   ├── memory.c                 # MmCopyVirtualMemory read/write
│   │   ├── breakpoint.c             # SW/HW/Memory breakpoints, DR7 encoding
│   │   ├── singlestep.c             # TF flag single step
│   │   ├── registers.c              # CONTEXT read/write
│   │   ├── modules.c                # PEB→Ldr module enumeration
│   │   ├── kmodules.c               # Kernel module enumeration
│   │   ├── threads.c                # Thread enum/suspend/resume
│   │   ├── debughook.c              # KiDebugRoutine hook + debug event handler
│   │   ├── debughook.h
│   │   └── ntundoc.h                # Undocumented NT API declarations
│   ├── loader/                      # Driver loader CLI (C)
│   │   ├── loader.vcxproj
│   │   ├── main.c                   # CLI entry point (load/unload/status/info)
│   │   ├── service.c                # SCM service management
│   │   └── vmdetect.c               # Hypervisor + testsigning detection
│   └── ui/                          # WPF debugger UI (C#)
│       ├── KernelFlirt.UI.csproj
│       ├── App.xaml / App.xaml.cs
│       ├── MainWindow.xaml / .cs
│       ├── Themes/Dark.xaml          # OllyDbg dark color scheme
│       ├── Controls/
│       │   └── DisasmView.xaml/.cs   # Syntax-highlighted disassembly + context menu
│       ├── Views/
│       │   └── ProcessPickerDialog.xaml/.cs
│       ├── ViewModels/
│       │   └── MainViewModel.cs      # All debugging commands + search + bookmarks
│       ├── Models/
│       │   ├── Instruction.cs        # Disassembled instruction
│       │   ├── Register.cs           # Register name/value/changed
│       │   ├── Breakpoint.cs         # BP with condition, log expr, hit count
│       │   ├── ModuleInfo.cs         # User-mode module
│       │   ├── KernelModuleInfo.cs   # Kernel driver module
│       │   ├── ThreadInfo.cs         # Thread state
│       │   ├── DebugEvent.cs         # Debug event from kernel hook
│       │   ├── CallStackFrame.cs     # Parsed call stack frame
│       │   ├── Bookmark.cs           # Named address bookmark
│       │   ├── Patch.cs              # Memory patch record
│       │   ├── SehEntry.cs           # SEH chain entry
│       │   └── SearchResult.cs       # Binary/string search result
│       ├── Services/
│       │   ├── DriverComm.cs         # DeviceIoControl wrapper (all IOCTLs)
│       │   ├── Disassembler.cs       # Capstone x86-64 wrapper
│       │   └── Symbols.cs            # Module+offset symbol resolution
│       └── Converters/
│           └── HexValueConverter.cs  # ulong <-> hex string
```

## Safety

- **VM only** — This driver is intended for use in virtual machines with testsigning enabled
- **Input validation** — All IOCTL handlers validate input/output buffer sizes before access
- **SEH protection** — `__try/__except` around user-mode pointer access in kernel
- **ProbeForRead/ProbeForWrite** — Usermode pointer validation
- **Atomic hook install** — `InterlockedExchangePointer` for KiDebugRoutine replacement
- **IRP cancel routines** — Pending WAIT_DEBUG_EVENT IRPs are properly cancelable
- **IRQL-aware blocking** — KeWaitForSingleObject only at IRQL <= APC_LEVEL
- **Spin lock protection** — Global breakpoint table and debug event state protected by KSPIN_LOCK

## Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| CommunityToolkit.Mvvm | 8.4.0 | MVVM framework (ObservableObject, RelayCommand) |
| Dirkster.AvalonDock | 4.72.1 | Docking panel layout |
| AvalonEdit | 6.3.0.90 | Text editor component |
| Gee.External.Capstone | 2.3.0 | x86-64 disassembler (Capstone bindings) |

## License

For educational and security research purposes only. Use responsibly in authorized environments.
