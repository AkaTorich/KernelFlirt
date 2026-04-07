# Quick Start

## Requirements

- **Host:** Windows 10/11 x64 with .NET 9 Runtime
- **VM:** Windows 10 x64 in VMware with `bcdedit /set testsigning on`
- **Network:** TCP connectivity between host and VM

## Setup

### VM Side

1. Copy `KfLoader.exe`, `KfRelay.exe`, and `KernelFlirt.sys` to the VM
2. Open an elevated command prompt:

```cmd
KfLoader.exe load
KfRelay.exe
```

KfRelay listens on port **31337** by default.

### Host Side

1. Run `KernelFlirt.exe`
2. Click **Connect** in the toolbar
3. Enter the VM's IP address (e.g., `10.100.102.4`)
4. Status bar shows "Connected" and kernel modules load

## Opening a Program

1. **File → Open & Debug** — browse the VM filesystem remotely
2. Select an EXE or SYS file
3. Process is created suspended with a breakpoint at the entry point
4. Press **F9** (Run) — execution stops at the entry point
5. Modules, imports, strings, and sections load automatically

## Opening a Service

1. **Debug → Debug Service** — enter the service name
2. KernelFlirt patches the entry point, starts the service through SCM
3. Catches the entry point breakpoint
4. Full debugging from `ServiceMain`

## Kernel Driver Debugging

1. Connect to VM
2. Open **Kernel Modules** tab — all loaded drivers listed
3. Double-click any module to navigate to it
4. Set breakpoints on driver functions
5. Trigger the driver (e.g., send IOCTL from a test app)
6. Breakpoint hits — inspect kernel-mode state

## First Steps

| Action | Shortcut |
|--------|----------|
| Toggle breakpoint | **F2** |
| Run / Continue | **F9** / **F5** |
| Step into | **F7** |
| Step over | **F8** |
| Step out | **Ctrl+F9** |
| Run to cursor | **F4** |
| Go to address | **Ctrl+G** |
| Decompile function | Right-click → Decompile |
