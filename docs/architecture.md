# Architecture

## Overview

KernelFlirt is a distributed debugger split across two machines: the **host** runs the WPF user interface on .NET 9, and the **target VM** (Windows 10 with testsigning enabled) runs a kernel driver plus a small relay daemon.

The host's `KernelFlirt.UI` connects to the VM over TCP on port `31337`, where `KfRelay.exe` listens for commands. The relay acts as a thin proxy — it receives JSON-RPC-style messages from the UI and translates them into `DeviceIoControl` calls against `KernelFlirt.sys`, the WDM kernel driver that does the actual memory reads/writes, breakpoint management, and debug event dispatch via an inline hook on `KdpStub`.

Two logical channels are multiplexed over the single TCP connection: the **CMD channel** carries synchronous request/response traffic (read memory, set breakpoint, get registers), while the **DBG channel** streams asynchronous debug events (breakpoint hit, exception, thread start) from the driver back to the UI. This separation lets the UI pump events in parallel with active command execution without blocking.

On the VM there is also `KfLoader.exe` — a small C console utility that uses the Service Control Manager (SCM) API to install, start, stop, and uninstall the kernel driver. It's only needed once per boot to get the driver loaded; after that, all communication flows through the relay.

## Components

| Component | Language | Description |
|-----------|----------|-------------|
| **KernelFlirt.UI** | C# / WPF | Debugger interface (runs on host) |
| **KernelFlirt.sys** | C / WDM | Kernel driver — memory, breakpoints, KdTrap inline hook |
| **KfRelay.exe** | C | TCP relay on VM, proxies IOCTLs over network |
| **KfLoader.exe** | C | CLI to load/unload the driver via SCM |
| **KernelFlirt.SDK** | C# / .NET 9 | Plugin SDK — full debugger API for extensions |

## Communication

The UI communicates with the VM over two TCP channels:

- **CMD channel** — synchronous request/response (memory read, breakpoint set, etc.)
- **DBG channel** — asynchronous debug events (breakpoint hit, exception, etc.)

Both channels are multiplexed over the same TCP connection to KfRelay, which translates them to `DeviceIoControl` calls to the kernel driver.

## Debug Hook

The driver installs an inline hook on `KdpStub` — the kernel's debug exception dispatcher. When a debug exception (#BP or #DB) occurs in the target process:

1. Driver captures the exception context (registers, address)
2. Suspends the target thread
3. Reports the event to KfRelay → UI
4. Waits for continue/step command from UI
5. Resumes the target thread

This approach works without `bcdedit /set debug on` — DebugView and other debug-dependent tools continue to work normally.

## Plugin System

Plugins are .NET 9 DLLs in the `plugins/` folder. Each implements `IKernelFlirtPlugin` with `Initialize(IDebuggerApi api)` and `Shutdown()`. The SDK provides access to:

- Memory read/write and registers
- Breakpoints (software, hardware, memory, watchpoints)
- Symbol resolution (PDB via Microsoft Symbol Server)
- Process/thread enumeration
- UI integration (panels, menus, annotations)
- Execution control (continue, step, pause)
- Debug events and filters
- Cross-plugin communication
