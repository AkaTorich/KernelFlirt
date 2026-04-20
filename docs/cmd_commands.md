# Command Console

KernelFlirt has an OllyDbg / x64dbg style command bar at the bottom of the
main window. Type a command and press **Enter** to run it; the short result
appears on the right side of the bar.

## Focus

- Click the **Command** TextBox at the bottom of the window.
- Or press **`:`** (Shift+`;`) from anywhere — focus jumps to the console
  (vim-style). This works unless you are already typing in a TextBox.
- **Esc** clears the input and drops focus back to the main view.

## History

- **↑** — previous command
- **↓** — next command (or clear when past the last one)

## Commands

| Command | Aliases | Description |
|---|---|---|
| `g` | `go`, `run` | Continue execution |
| `t` | `sti`, `stepi` | Step into |
| `p` | `sto`, `stepo` | Step over |
| `bp <expr>` |  | Set a software breakpoint at the address |
| `bc <expr>` |  | Clear the breakpoint at the address |
| `bl` |  | List first few breakpoints |
| `d <expr>` | `dump` | Follow the address in the Hex Dump panel |
| `dis <expr>` | `u`, `disasm` | Navigate the Disassembly view to the address |
| `r <reg>` |  | Show current value of a register |
| `r <reg>=<expr>` |  | Set a register |
| `? <expr>` | `eval` | Evaluate an expression (hex / dec / signed dec) |
| `findall <pat>` | `find` | Search binary pattern via the Search command |
| `clear` | `cls` | Clear the output line |

## Expression evaluator

The argument to most commands is an expression. Supported:

- **Hex literals**: `0x1234`, `1234h`, or bare hex containing A–F (e.g. `DEAD`)
- **Decimal literals**: `1234`
- **Registers**: `rax`, `rbx`, `rip`, `rsp`, `rflags`, `eax` … (case-insensitive)
- **Module base**: just the module name, e.g. `ntdll`, `kernel32`, `rc4_strings`
- **`module!symbol`**: `ntdll!NtReadFile`, `kernel32!LoadLibraryW`
  (requires symbols to be loaded)
- **Arithmetic**: `+`, `-`, `*`, `/`
- **Parentheses**: `(rax + 4) * 2`
- **Dereference**: `[addr]` — reads a qword (8 bytes) from the target and
  substitutes its value. Example: `[rip+10]`, `[rsp]`

## Examples

```text
g
t
p
bp ntdll!NtTerminateProcess
bp rc4_strings+0x1570
bp [rsp]
bc ntdll!NtTerminateProcess
bl
d rbp-40
dis kernel32!LoadLibraryW
r rax
r rax = rbx + 0x10
? (rax + 4) * 2
? [rip+10]
findall 48 8B 05 ?? ?? ?? ??
```

## Notes

- Numbers without `0x` / `h` are treated as **hex** when they consist only of
  hex digits (x64dbg behaviour). Use a trailing space or explicit decimal
  context if you really need decimal — the evaluator prefers hex because most
  inputs in a debugger are addresses.
- All commands run on the UI thread; they are effectively wrappers around
  the same ViewModel commands that the toolbar buttons invoke, so state
  (breakpoints, registers, dump view) updates immediately.
- Expression errors and unknown commands produce a short `err:` / `unknown:`
  message next to the input — nothing crashes.
