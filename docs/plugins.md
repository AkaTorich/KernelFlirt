# Plugins

KernelFlirt ships with 17 plugins. All are built from source in the `samples/` directory.

## Reverse Engineering

| Plugin | Description |
|--------|-------------|
| **Graph View** | IDA-style control flow graph with MSAGL layout, block coloring, collapse/expand, function navigation, double-click on calls |
| **Xrefs** | Cross-reference analysis — find all CALL/JMP/LEA/MOV references to or from any address |
| **FLIRT Signatures** | IDA-compatible function recognition by byte patterns (.pat files + built-in MSVC CRT database) |
| **Signature Detector** | PEiD-compatible packer/compiler detection with 4445 community signatures |
| **PE Rebuilder** | PE dumper with automatic IAT reconstruction — Scylla-style one-click rebuild |
| **String Decryptor** | Automated string decryption (XOR, RC4, custom) |
| **VulnHunter** | Dangerous API usage scanner |

## Dynamic Analysis

| Plugin | Description |
|--------|-------------|
| **API Monitor** | Real-time API function interception with parameter capture and logging |
| **Network Monitor** | Network traffic capture — hooks send/recv/connect/WinINet/WinHTTP with data preview |
| **Memory Scanner** | CheatEngine-style value scanning with subsequent filtering |
| **Themida Unpacker** | Automated Themida/WinLicense unpacker (Magicmida engine) |

## Automation & AI

| Plugin | Description |
|--------|-------------|
| **C# Scripting** | Roslyn REPL with full debugger API, AvalonEdit with syntax highlighting, persistent state |
| **AI Assistant** | Chat-based reverse engineering assistant (any OpenAI-compatible API) with 65+ debugger tools |
| **MCP Server** | Model Context Protocol server — connect Claude Code, Cursor, or any MCP client directly to the debugger |
| **Session Manager** | Save/load full session (breakpoints, comments, function names, graph colors) with ASLR auto-rebase |
| **Bookmarks/Notes** | Address bookmarks with annotations, synced to disassembly comments, persisted per target |
| **Anti-Debug Bypass** | Automatic PEB.BeingDebugged, NtGlobalFlag, DebugPort, ThreadHide, HeapFlags patching on attach |

## Developing Plugins

See the [SDK Documentation (EN)](SDK-en.md) or [SDK Documentation (RU)](SDK-ru.md) for a complete guide to building your own plugins.
