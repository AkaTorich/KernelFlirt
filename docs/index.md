# KernelFlirt

**Windows kernel-level debugger** with an OllyDbg/IDA Pro-style interface.  
Designed for security research, reverse engineering, and malware analysis in VM environments (VMware).

![KernelFlirt](screenshot.png)

## What is KernelFlirt?

KernelFlirt is a full-featured kernel debugger that connects to a Windows VM over TCP and provides:

- **Kernel & user-mode debugging** — breakpoints, stepping, register editing
- **IDA Pro-style graph view** — control flow graphs with block coloring
- **RetDec decompiler** — C pseudocode with syntax highlighting
- **17 plugins** — from AI assistant to Themida unpacker
- **C# scripting** — Roslyn REPL with full debugger API access
- **MCP server** — connect AI clients (Claude Code, Cursor) directly to the debugger
- **9 color themes** — from dracula to sakura

## Quick Start

```cmd
:: VM — load driver and start relay
KfLoader.exe load
KfRelay.exe

:: Host — launch the UI and connect
KernelFlirt.exe → Connect → VM IP
```

1. **File → Open** — browse VM filesystem, select EXE/SYS
2. Process created suspended, entry point BP set automatically
3. **F9** — run, symbols and modules load
4. Set breakpoints, step through code, inspect memory

## Documentation

| Document | EN | RU |
|----------|----|----|
| **SDK & Plugin Development** | [SDK-en.md](SDK-en.md) | [SDK-ru.md](SDK-ru.md) |
| **C# Scripting Reference** | [scripting-reference-en.md](scripting-reference-en.md) | [scripting-reference-ru.md](scripting-reference-ru.md) |
| **CLI (KfConsole)** | [cli.md](cli.md) | — |

## Console Front-End

Don't want the WPF UI? `KfConsole.exe` (in `bin\Console\`) is a WinDbg/x64dbg-style REPL
over the same driver. ANSI colors, dbghelp-resolved symbols, expression parser
(`bp ntdll!NtCreateFile if rcx!=0`), conditional breakpoints, anti-debug primitives,
Step Into/Over/Out, and full WoW64 support. See [cli.md](cli.md) for the command reference.

## Links

- [GitHub Repository](https://github.com/AkaTorich/KernelFlirt)
- [Changelog](changelog.md)
