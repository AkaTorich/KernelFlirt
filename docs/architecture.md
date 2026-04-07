# Architecture

## Overview

```
  Host machine                           VM (Windows 10, testsigning)
┌──────────────────┐    TCP:31337    ┌──────────────────┐     IOCTL      ┌──────────────────┐
│  KernelFlirt UI  │◄───────────────►│   KfRelay.exe    │◄──────────────►│ KernelFlirt.sys  │
│  (WPF / .NET 9)  │  CMD+DBG ch.    │   (TCP proxy)    │  DeviceIoCtl   │ (WDM Driver)     │
└──────────────────┘                 └──────────────────┘                └──────────────────┘
                                     ┌──────────────────┐  SCM API
                                     │  KfLoader.exe    │──────────────────────┘
                                     │  (C / Console)   │  load / unload / status
                                     └──────────────────┘
```

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
