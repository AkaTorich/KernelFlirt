# KernelFlirt Project Guidelines

## MCP Debugger Usage (kf-debugger)

When analyzing a program in the debugger:
- **Always use `decompile` first** to get C pseudocode of key functions (entry point, main). This gives a complete overview much faster than raw disassembly.
- Use `disassemble` only for small snippets or when decompilation is unavailable/fails.
- Use `read_string` / `read_unicode_string` to resolve string references found in decompiled code.
- Start analysis with `get_debugger_state` + `list_modules` to understand what's loaded, then `decompile` the entry point or main function.
