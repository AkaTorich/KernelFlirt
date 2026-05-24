# KfConsole — Command-Line Debugger

`KfConsole.exe` is a console front-end for the KernelFlirt driver, sharing the same hook,
the same relay, and the same IOCTL surface as the WPF UI. It is meant for situations where
GUI is impractical — remote VMs over SSH, scripted debugging sessions, batch analysis,
or simply for users who prefer a WinDbg/x64dbg-style REPL.

The binary lives in `bin\Console\KfConsole.exe`. It ships with bundled `dbghelp.dll` and
`symsrv.dll` so PDB downloading from the Microsoft symbol server works out of the box.

## Launching

```cmd
KfConsole.exe                       :: REPL without auto-connect
KfConsole.exe local                 :: open \\.\KernelFlirt directly (no relay)
KfConsole.exe 10.100.102.6          :: connect to a relay (default port 31337)
KfConsole.exe 10.100.102.6:31337    :: explicit host:port
```

The same model as the UI: in the typical setup the VM runs `KfLoader.exe load` and
`KfRelay.exe`; the console runs on the host machine.

## Prompt

```
kf>                                  not connected
kf*>                                 connected, no target
kf(7236:1876/x64/brk)>               PID 7236, TID 1876, x64, stopped
kf(7236:1876/x64/run)>               same, but the target is running
kf(8108:2900/x86/brk)>               WoW64 / 32-bit target
```

* PID and TID are shown in cyan.
* `x64`/`x86` indicates the target's bitness (auto-detected via `Peb32Address`).
* `brk` (red) and `run` (green) reflect whether the target is paused or executing.

## Command Reference

All numeric arguments are parsed as **expressions**, not bare hex. Supported tokens:
hex literals (`0x...`, `1234h`, or bare `1234`), registers (`rip`, `rsp+8`, `eax`),
symbols (`module!Name`), pointer dereference (`[mem]`), and arithmetic
(`+ - * /`, parentheses, `<<`, `>>`, `&`, `|`, `^`). Numbers default to hex
(same as WinDbg).

### Connection

| Command | Description |
|---------|-------------|
| `connect [local \| host[:port]]` | Open the local driver or connect to a relay. |
| `disconnect` | Tear down the connection. |
| `q` / `quit` / `exit` | Exit the REPL. |

### Process & Target

| Command | Description |
|---------|-------------|
| `open <path>` | Launch a process under debug (relay-side only). Patches the entry with `EB FE`, waits for the thread to reach it, restores the original bytes, and stops. Symbols load automatically for all modules visible at entry. |
| `attach <pid>` | Take an already-running process under debug. Auto-detects WoW64 via `Peb32Address`. |
| `detach` | Remove the hook, reset driver state. |
| `reset` | Clear all breakpoints in the driver without detaching. |
| `procs` / `ps` | List all running processes. |
| `mods` | List modules of the current target. |
| `kmods` | List kernel modules (ntoskrnl + drivers) via `IOCTL_KF_ENUM_KERNEL_MODULES`. |
| `threads` / `tt` | List threads of the current target. |
| `tid <tid>` | Select the active thread (registers/step apply to it). |

### Registers

| Command | Description |
|---------|-------------|
| `r` | Print all registers (16-byte hex on x64, 8-byte on x86/WoW64). |
| `r <name>` | Print a single register. Accepts `rax`/`eax`/`r10`/`rip`/`eflags`/`cs`/`dr0`/… |
| `r <name>=<expr>` | Write the register. Driver validates segment selectors (CS ∈ {0x10, 0x33, 0x23}, SS ∈ {0x18, 0x2B, 0x00}) and rejects non-canonical RIP/RSP — invalid values won't reach IRET. |

### Memory

| Command | Description |
|---------|-------------|
| `d <addr> [count=64]` | Hex dump (16 bytes per line + ASCII). |
| `dq <addr> [count=8]` | QWORD dump with symbol resolution for each value. |
| `dd <addr> [count=16]` | DWORD dump (4 per row). |
| `dw <addr> [count=32]` | WORD dump (8 per row). |
| `dp <addr> [count=8]` | Pointer-sized dump (4 bytes on WoW64, 8 on x64) with symbol resolution for each value. |
| `da <addr> [count=64]` | ASCII string dump (stops at NUL or `count` chars). |
| `du <addr> [count=64]` | UTF-16 string dump (stops at NUL or `count` chars). |
| `u <addr> [count=16]` | Disassembly (Iced / NASM syntax). Each `call`/`jmp`/`jcc` with a resolvable target gets a `; module!func` annotation in green. The current RIP row is prefixed with a red `►`. |
| `e <addr> <hex bytes>` | Write memory. Bytes can be space-separated or solid (`e 401570 90 90 c3` or `e 401570 9090c3`). |
| `s <addr> <len> <pattern>` | Search memory. Pattern is hex bytes with `??` wildcards (`s 401000 1000 48 8b ?? c3`), quoted ASCII (`"MZ"`), or `L"unicode"`. Capped at 4 MB per call and 256 reported hits. |
| `.alloc <size> [prot=rwx]` | Allocate memory in the target (`IOCTL_KF_ALLOC_MEMORY`); prints the new base. Protection: mnemonics `rwx`/`rw`/`rx`/`r`/`ro`/`na`/`x` or a raw hex `PAGE_*` value. |
| `.free <addr>` | Free a region (`IOCTL_KF_FREE_MEMORY`). |
| `.protect <addr> <size> <prot>` | Change page protection (`IOCTL_KF_PROTECT_MEMORY`); prints the old protection. |

### Breakpoints

| Command | Description |
|---------|-------------|
| `bp <addr>` | Software breakpoint (INT3 via MDL — works on RX `.text` pages). |
| `bp <addr> if <expr>` | Conditional BP. After each hit the driver pauses, CLI evaluates `<expr>`; if the result is 0, the thread is silently released via `ContinueDebugEvent(Run)` without UI notification. |
| `ba <e\|r\|w><len> <addr>` | Hardware breakpoint / watchpoint via DR0-3. Mode `e` = execute, `w` = write, `r` = read/write; `len ∈ {1,2,4,8}` (e.g. `ba w4 401000`). Routes through `IOCTL_KF_SET_BREAKPOINT` with `Type`/`Length`. |
| `bm <addr> [size]` | Memory breakpoint (PAGE_GUARD), `KF_BP_MEMORY`. |
| `bl` | List active breakpoints. Temp BPs (Step Over/Out, run-to-cursor) are marked `[temp]`; hardware/memory BPs are tagged `<hw-e>`/`<hw-w>`/`<hw-rw>`/`<mem>`. |
| `bc <addr \| handle \| all>` | Remove a BP by address or handle, or all at once. |

### Execution

| Command | Description |
|---------|-------------|
| `g` / `run` / `go` | Continue execution. For suspend-state (entry-point), this maps to `ResumeThread`; otherwise to `ContinueDebugEvent(Run)`. On WoW64 the active BPs are converted to `EB FE` spin-traps and EIP is polled across all threads. |
| `g <addr>` | Run to cursor — sets a temporary SW BP at `<addr>`, continues, and auto-removes it on hit (like Step Over/Out temps). Not supported on WoW64 (the KdTrap hook doesn't catch 32-bit exceptions) — use `bp <addr>` + `g` instead. |
| `t` / `sti` | Step Into (one instruction). On native x64 in suspend-state, sets TF via `IOCTL_KF_SINGLE_STEP` + `ResumeThread`. On WoW64, decodes the current instruction with Iced and uses an EB-FE spin-trap on `EIP + size`. |
| `p` / `sto` | Step Over. Decodes the current instruction; targets = `[ip + size, branch_target]` for `jcc`, `[branch_target]` for unconditional `jmp`, `[ip + size]` otherwise. Native x64: places temporary SW BPs (`[temp]`). WoW64: spin-trap on the same set. |
| `o` / `out` | Step Out. Reads the return address from the top of the stack and places a temporary BP (or spin-trap on WoW64). |
| `ss` | Force the TF bit in `KTRAP_FRAME` of the current thread, without continuing. Useful before manual `g`. |
| `wait` | Wait for a single debug event from the driver. Normally not needed — a background listener processes events automatically. |
| `interrupt` | `SuspendThread` on the current TID — to force a stop on a running target. |
| `suspend [tid]` | `SuspendThread` on the given TID (defaults to the current one). |
| `resume [tid]` | `ResumeThread` on the given TID (defaults to the current one). |

### Inspection

| Command | Description |
|---------|-------------|
| `k [frames]` | Call stack via frame-pointer (RBP/EBP) unwinding with symbol resolution. Accurate for functions with a classic prologue; approximate under FPO/leaf frames. Default 64 frames. |
| `!peb` / `peb` | Parse the target PEB: `BeingDebugged` (red when set), `ImageBaseAddress`, `Ldr`, `ProcessParameters`, `NtGlobalFlag`. Uses the 32-bit layout for WoW64 targets, 64-bit otherwise. |
| `stats` | Inline-hook diagnostics (`IOCTL_KF_GET_HOOK_STATS`): call / BP-hit / step counters, `KiDebugRoutine` addr/orig/now, `KdpStub` and `KdTrap` addresses, and fast-trace counters. |

### Anti-Debug (driver primitives)

These rely on existing driver IOCTLs originally added for the UI's anti-debug menu.

| Command | Description |
|---------|-------------|
| `ad clr_debug_port` | Zero out `EPROCESS.DebugPort` of the target. |
| `ad clr_thread_hide` | Clear `HideFromDebugger` on every thread of the target. |
| `ad ntqsi on \| off` | Install/remove the `NtQuerySystemInformation` hook that spoofs class `0x23` (SystemKernelDebuggerInformation). |
| `ad spoof on \| off` | Toggle `KUSER_SHARED_DATA.KdDebuggerEnabled` spoofing (usermode reads see `FALSE` while the kernel flag stays `TRUE` for KdTrap). |

### Misc

| Command | Description |
|---------|-------------|
| `color [on\|off]` | Toggle ANSI coloring. On by default; useful to disable when piping output to a file. |
| `help` / `?` | Show the built-in command list. |

## Symbols

`KfConsole` uses the bundled `dbghelp.dll` and `symsrv.dll` (copied next to the EXE
by the build). On `attach` or `open`, every loaded module is processed:

1. The driver reads the target's PE-header via `IOCTL_KF_READ_MEMORY`.
2. The CLI parses the Debug Directory and extracts the RSDS GUID + Age + PDB name.
3. `SymFindFileInPathW` locates or downloads the PDB through the symbol server
   chain (default: `srv*C:\Symbols*https://msdl.microsoft.com/download/symbols`).
4. `SymLoadModuleExW` is called with the resolved PDB path, and a follow-up
   `SymGetModuleInfoW64` verifies the load succeeded (`SymType == Pdb`).

If a module's PDB cannot be found, the CLI prints the reason and skips that
module — no fake `module+offset` placeholders. The first run downloads
`ntdll.pdb`/`kernel32.pdb`/`kernelbase.pdb` (~30 MB total); subsequent sessions
reuse the local cache.

To use private PDBs for your own builds, drop them next to the executable on
the relay machine **or** put them anywhere along `_NT_SYMBOL_PATH` on the host
running `KfConsole.exe`.

## Output Coloring

Coloring is on by default. The palette roughly mirrors VS Code Dark+ / x64dbg:

| Color | Used for |
|-------|----------|
| Cyan | Register names, PIDs, TIDs, field labels in `!peb` / `stats` |
| Orange | Hex literals, immediates, register values |
| Blue | Regular x86/x64 mnemonics, BP-kind tags (`<hw-w>`, `<mem>`) |
| Magenta | Control-flow mnemonics (`jmp`, `call`, `ret`, `jcc`), action keywords (`step into`, `running`) |
| Yellow | Symbols, paths, architecture tag |
| Green | ASCII strings in dumps, `if` expression body, success markers |
| Red | Faulting addresses, AV events, breakpoint markers (`►`, `[N]`), error markers `✗` |
| Gray | Addresses, separators, dim metadata |

`color off` disables coloring globally — escape codes will not be emitted, useful
for pipe-friendly output (`KfConsole.exe ... > log.txt`).

## Readline & History

In interactive mode the REPL uses `ReadLine.Reboot` for emacs-style line editing:

* `↑` / `↓` browse history (persisted across sessions to `%APPDATA%\KernelFlirt\history.txt`).
* `Ctrl+R` — reverse search in history.
* `Tab` — completion on the first word against the built-in command list.
* `Ctrl+A` / `Ctrl+E` / `Ctrl+K` — emacs bindings (line start/end/kill).

When stdin is redirected (`type cmds.txt | KfConsole.exe`), the REPL falls back to plain
`Console.ReadLine` so batch input works correctly.

## Asynchronous Events

A background listener thread continuously calls `WAIT_DEBUG_EVENT` on the DBG channel.
When a breakpoint hits, the listener prints a banner on top of the current prompt:

```
*** BP at 00007FFA`14E77344  PID=7236 TID=1876  ntdll!NtCreateFile
    RAX=0...  RBX=0...
kf(7236:1876/x64/brk)>
```

This means you can issue read-only commands (`r`, `mods`, `d`, …) on the prompt while
the target is `run`, and the next break-event will appear without you typing `wait`
explicitly. Step/run commands (`t`, `p`, `g`) require the target to be stopped first.

## Worked Example

```text
kf> connect 10.100.102.6:31337
✓ connected (10.100.102.6:31337), driver v0x10000

kf*> open C:\Temp\rc4_strings.exe
• creating process: C:\Temp\rc4_strings.exe
✓ created PID=8424 TID=10136 ImageBase=00007FF7`2EA10000 (x64)
• entry=00007FF7`2EA11570 (orig=48 83, patched 2 byte EB FE)
✓ reached entry after 100 ms
symbols: 3/4 modules loaded
✓ stopped at entry point of C:\Temp\rc4_strings.exe

kf(8424:10136/x64/brk)> bp ntdll!NtCreateFile if rcx!=0
✓ bp [1] 00007FFF`BBF8E030  ntdll!NtCreateFile  if rcx!=0

kf(8424:10136/x64/brk)> g
✓ running (PID 8424, TID 10136, resumed from suspend)

*** BP at 00007FFF`BBF8E030  PID=8424 TID=10136  ntdll!NtCreateFile
    RAX=0000000000000000  RBX=...  RCX=00000010`0007FE00  RDX=...
    RIP=00007FFF`BBF8E030  RSP=00000010`0007FD68  RBP=...

kf(8424:10136/x64/brk)> u rip 5
►  00007FFF`BBF8E030  4c 8b d1                  mov r10, rcx       ntdll!NtCreateFile
   00007FFF`BBF8E033  b8 55 00 00 00            mov eax, 0x55
   00007FFF`BBF8E038  f6 04 25 08 03 fe 7f 01   test byte ptr [0x7ffe0308], 1
   00007FFF`BBF8E040  75 03                     jne 0x7fff bbf8e045
   00007FFF`BBF8E042  0f 05                     syscall

kf(8424:10136/x64/brk)> dq rsp 4
  00000010`0007FD68  00007FFFB99E1234   kernelbase!CreateFileW+0x84
  00000010`0007FD70  ...

kf(8424:10136/x64/brk)> o
✓ step out: return-address @ [00000010`0007FD68] = 00007FFFB99E1234

*** BP at 00007FFF`B99E1234  PID=8424 TID=10136  kernelbase!CreateFileW+0x84
    ...

kf(8424:10136/x64/brk)> bc all
✓ cleared 0 BPs

kf(8424:10136/x64/brk)> detach
✓ detached
```

## Known Limitations

* **`open` requires the relay.** The relay-side IOCTL `CREATE_PROCESS` handles entry-point
  patching; the local path falls back to plain `CreateProcessW` without entry patching,
  so the process resumes immediately. Use `attach <pid>` after manual launch instead.
* **Step Out at function prolog** may read garbage as the return address (the real one
  isn't on the stack yet). Same caveat as in the UI; for tight breakpointing use `bp`
  on a known address inside the function.
* **WoW64 `g` blocks the REPL** until a BP is hit (synchronous EIP polling). UI does the
  same work in a background task; the CLI version doesn't yet have cancellation —
  Ctrl+C will terminate the process. Avoid `g` on WoW64 if there are no BPs to land on.
* **No SymFromName for unloaded modules.** `bp ntdll!Foo` works only after `attach`/`open`
  has finished loading the module list. Set the BP after the target is stopped at entry.
* **`k` is a frame-pointer walk.** It follows the RBP/EBP chain, so it's accurate only for
  functions built with a classic prologue (`push rbp; mov rbp, rsp`). FPO-optimized or leaf
  frames may be skipped or misreported — for an exact stop, set a `bp` inside the function.
