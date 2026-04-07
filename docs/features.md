# Features

## Debugging

- **Software breakpoints** (INT3) — unlimited, persistent across sessions
- **Hardware breakpoints** (DR0-DR3) — execute, per-thread
- **Hardware watchpoints** — write and read/write data (1/2/4/8 bytes)
- **Memory breakpoints** — PAGE_GUARD based, catch first access
- **Conditional and logging breakpoints** — via C# scripting
- **Step into** (F7), **step over** (F8), **step out** (Ctrl+F9), **run to cursor** (F4)
- **Register editing** — modify any GPR, RIP, RFLAGS, DR0-7 via double-click
- **Inline assembler** — type assembly or hex bytes, auto NOP padding
- **Patch tracking** — all patches recorded with undo capability

## Analysis

- **Disassembly** — x86/x64, syntax-highlighted, symbol-resolved call targets
- **Hex dump** — binary pattern search with `??` wildcards
- **String search** — ASCII/Unicode across all modules
- **RetDec decompiler** — C pseudocode with theme-aware syntax highlighting
- **Graph View** — IDA-style CFG with block coloring, collapse/expand, function navigation
- **Navigation bar** — color-coded section map with RIP/breakpoint/bookmark markers
- **PDB symbols** — automatic download from Microsoft Symbol Server
- **Function naming** — `RegisterFunction` with size for user-defined names
- **Cross-references** — find all callers/references to any address

## Views

| Tab | Description |
|-----|-------------|
| Disassembly | Main code view with syntax highlighting |
| Breakpoints | All breakpoints with type, address, hit count |
| Modules | User-mode modules with base, size, name |
| Kernel Modules | All kernel drivers |
| Threads | Thread list with suspend/resume |
| Call Stack | Current call stack |
| Sections | PE sections for all modules |
| Strings | ASCII/Unicode string search |
| Imports / Exports | PE import/export tables |
| Functions | Discovered functions |
| Decompiler | RetDec C pseudocode |
| Hex Dump | Raw memory view |
| Stack | Stack view with annotations |
| Registers | All GPR, flags, debug registers |

## Remote File Browser

- Browse VM filesystem over TCP
- Upload, download, delete, rename files
- Double-click EXE to open in debugger
- Drag-and-drop upload
