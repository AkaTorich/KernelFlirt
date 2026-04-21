# Changelog

## v1.9.1 — 2026-04-22

### Process Launch (Relay-side entry-point patch)

- **Relay now patches the entry point** before returning from `CreateRemoteProcess`. Previously the UI had to read the exe's entry-point bytes through the driver right after `CREATE_SUSPENDED`, but `.text` pages are demand-loaded, so `ReadMemory` came back empty and we fell through to a poll-attach that stopped somewhere deep in `wow64cpu!` instead of the real entry.
- Patch is always a 2-byte `EB FE` spin loop (both 32-bit and 64-bit). INT3 doesn't survive Windows 10's optimized exception dispatch before our driver is attached — the process would just terminate with `STATUS_BREAKPOINT (0x80000003)`. The spin loop is ugly but reliable: the UI resumes the thread, polls IP until it sits on entry, suspends, then restores the original bytes.
- New `KF_CREATE_PROCESS_OUT` fields: `EntryPointAddress`, `EntryOriginalBytes[2]`, `EntryPatchBytes`, `EntryIs32Bit`.
- **File on disk is not touched** — everything goes through `WriteProcessMemory` under `VirtualProtectEx(PAGE_EXECUTE_READWRITE)`. The image-mapped page triggers a copy-on-write fault resolved synchronously by the kernel.

### Driver Safety Tightened

- `MmIsAddressValid` probe (introduced in v1.9.0) replaced with a canonical-address-range check. `MmIsAddressValid` rejects valid-but-paged-out user-mode pages, which broke reading `.text` right after entry — `MmCopyVirtualMemory` handles those pages correctly via demand-paging. The canonical check still blocks the obviously-bogus addresses (`0x0000ffed00b86000`-style) that used to bugcheck the VM.

### Disassembly

- **Fast-path `RefreshDisassembly` now syncs BP markers too.** When RIP fell inside the current window, the fast path only toggled `IsCurrentInstruction`; newly set breakpoints stayed invisible in the view until you navigated away. Now every visible row's `HasBreakpoint` is recomputed from the `Breakpoints` list on each refresh.

### Hex Dump

- **Non-printable byte replacement changed** from `.` (low dot, easily lost between rows at high zoom) to `·` (middle dot — centered on the glyph, visible at every zoom level).
- **Row height dropped in favour of padding.** Fixed `Border.Height = fontSize × 1.3/1.5` made adjacent glyphs touch at large zoom levels; replaced with natural WPF row-height + 2 px vertical padding on the row border.

### Appearance Window

- **Theme-aware tree text.** TreeView items (`General`, `Disassembly`, `Stack` …) and their expanded leaves read their `Foreground` from a new `AppearanceTextBrush` theme key. Previously the default `ContentPresenter` ignored the inherited `TreeViewItem.Foreground` and showed black text on the dark panel. Each of the 10 shipped themes got a colour picked to match its palette.
- **Preview foreground falls back to `FgBrush`** when no color is entered, so `Example Text` stays readable after the dialog opens without a selection.

### Font / Layout Persistence Fix

- **Panel fonts now applied *before* persisted layout.** `ApplyLineFontSize` scales column widths by `LineFontSize / 11`, so applying layout before fonts meant the saved widths got stomped by a subsequent font change. Order is now `ApplyPanelFonts → ApplyPersistedLayout`.
- **Row/Column splitter ratios restored as a pair.** Only setting one side of a paired `GridSplitter` (e.g. `TopRow` but not `BottomRow`) mixed star + auto units and ended up with one row collapsed to zero. Top+Bottom rows, top Disasm↔Registers columns, and bottom HexDump↔Stack columns are now applied only when both sides of the pair have a value.
- `IsBreakState = true` now set after the EB FE polling path so register editing and flag-toggle clicks work on the very first break (previously greyed out until the user stepped once).

### Command-Line Tools

- `KfLoader.exe` with no arguments defaults to `load` (already shipped in v1.9.0, reaffirmed as the documented behaviour).

## v1.9.0 — 2026-04-21

### Command Console

- **OllyDbg/x64dbg-style command bar** at the bottom of the main window (above the status bar).
- **Commands**: `g`/`go`/`run`, `t`/`sti`, `p`/`sto`, `bp <expr>`, `bc <expr>`, `bl`, `d <expr>`, `dis`/`u <expr>`, `r <reg>[=<expr>]`, `? <expr>`/`eval`, `findall <pattern>`, `clear`/`cls`.
- **Expression evaluator** — registers (`rax`, `rip`, `eflags` …), hex literals (`0x1234`, `1234h`, bare hex), decimals, `module!symbol`, `+ - * /`, parentheses, `[mem]` dereference (qword).
- **History** — `↑` / `↓` browse previous commands. `Esc` clears and drops focus.
- **`:` (Shift+;) anywhere** — focuses the console (vim-style), unless you're already typing in a TextBox.
- **Autocomplete popup** with ghost-text hint when empty; `Tab` / click picks a suggestion.
- **Documentation**: [`docs/cmd_commands.md`](docs/cmd_commands.md).

### Appearance

- **View → Appearance…** — x64dbg-inspired color editor: tree of categories on the left (General / Disassembly / Stack / Plugins / Tabs / Script / Jump Arrows / Scroll Bars), 30-swatch palette + hex input + live preview on the right.
- **Foreground + Background** — a second BG picker appears only for keys that actually have a paired background (breakpoint row, current-line highlight).
- **Font tab** — per-panel monospace font + size picker for Disassembly / Hex Dump / Stack / Registers. Shows a live preview line for each panel.
- **Embedded fonts** — Intel One Mono (SIL OFL 1.1) shipped with the app in 8 weights; always available in the font picker regardless of system install.
- **View → Themes** — all theme presets from the `themes/` folder surfaced as a submenu; one click applies the theme.

### Theme System

- **New theme keys**: `SplitterDashBrush`, `JumpArrowBrush`, `JumpArrowTakenBrush`, `JumpArrowNotTakenBrush`, `JumpArrowRipBrush`, `SelectionBrush` (newly respected by panels). Previously-hardcoded colors in Hex Dump, Disassembly selection and jump arrow overlays are now read from the active theme.
- **All ten shipped themes** (`default-dark`, `dracula`, `hacker`, `ida-pro`, `long_night`, `monokai`, `ollydbg`, `ollydbg-light`, `sakura`, `x64dbg`) updated with the new keys.

### Disassembly View

- **Five resizable columns** — BP marker / Jumps gutter / Address / Bytes / Mnemonic+Hints. Columns are separated by dashed vertical splitters which can be dragged; positions persist across sessions.
- **Jump-arrow gutter** (`JumpArrowsCanvas`) — custom `FrameworkElement` renders jmp/jcc/call arrows via `DrawingContext`. Lane-assignment avoids overlapping parallel jumps. Dedicated styles for RIP (yellow horizontal), taken branch (red), non-taken branch (grey dashed), normal (theme green).
- **Mnemonic cell** — custom renderer (`MnemonicCell : FrameworkElement`) draws mnemonic + operands + clickable symbol + trailing comment with proper ellipsis and per-token color. Colors are read from the active theme on every paint so live theme swaps take effect instantly.
- **x64dbg-style live hints** (opt-in via View → Live Hints) — register values next to the instruction (`rdx:rc4_strings+1570`) plus batched memory deref previews (`[rip+0x1b2d]="[*] RC4 String Decryptor"`). Background enrichment reads one 4 KB page per cluster of operands so hints cost at most a handful of reads per break.
- **MemHint split** — memory-deref previews stored in a separate field (`Instruction.MemHint`) so sync and async passes can't duplicate each other's output.
- **Column widths scale with zoom** — `ApplyLineFontSize` multiplies BP/Addr/Bytes/Mnem widths by `LineFontSize / 11` so columns stay aligned at any zoom level.

### Ctrl+Wheel Zoom

- **Per-panel zoom** (`Disasm` / `Registers+Flags` / `Hex Dump` / `Stack`) bound to `Ctrl+Wheel`, `Ctrl++`, `Ctrl+-`, `Ctrl+0`. Range 0.5× – 6.0× (font 11pt → up to ~66pt).
- **Column widths scale together with font** so columns stay aligned at any zoom level.
- **Dashed splitters and jump arrows** follow the horizontal scroll offset via `TranslateTransform`.
- **Persisted** — zoom level for each of the four panels saved in `kf_settings.txt` (`Ui.Zoom.*`).
- **Mouse-wheel handler** uses `AddHandler(PreviewMouseWheelEvent, …, handledEventsToo: true)` — works even if a ScrollViewer marked the event Handled first.

### Live Hints — Quiet by Default

- **Opt-in** — `View → Live Hints (x64dbg-style)`. Off by default because rebuilding clickable InlineUIContainer values for every visible row adds latency on each step. Setting persisted as `Ui.LiveHints=1`.
- **User-code only** — hints are emitted only for instructions inside the main `.exe` module. System-DLL frames stay clean.
- **Sub-register deduplication** — `al`/`ax`/`eax` don't emit a hint if `rax` already did (same underlying value).
- **Tiny-value filter** — values below `0x10000` are treated as constants, not pointers, and skip symbol resolution.
- **Row-over-row dedup** — identical `rax:X, rdx:Y` on adjacent rows is shown only on the first.

### Zoom & Step Latency

- **Fast-path `RefreshDisassembly`** — when the new RIP falls inside the currently displayed instruction window, the refresh only flips the `IsCurrentInstruction` marker instead of re-reading 2 KB of memory and re-disassembling. Cuts typical stepping latency from ~700 ms to ~one network round-trip.
- **`RefreshDisassembly`** read size lowered from 8 KB / 512 instructions to 2 KB / 128 instructions.
- **Register annotation moved to background** — raw register values are pushed to the UI immediately; symbol + string annotation runs on a worker thread and patches the existing Register entries when ready.
- **Stack annotation** — the sync pass now produces symbol-only annotations using cached dbghelp + module lists (no per-entry `TryReadStringAtAsync` / `IsReturnAddressAsync`, which had been doing ~64 per-break round-trips). `"return to"` prefixes and string previews are filled in from a background task afterwards.

### Jump Arrows

- **Custom `FrameworkElement`** (`JumpArrowsCanvas`) with `OnRender` replaces the original Shape-per-line Canvas. Lane assignment for overlapping arrows; Rip/Taken/NotTaken/Normal pens sourced from theme resources on every paint.

### Column Splitters / Resizing

- **Disassembly**: 5 dashed splitters (BP | Jumps | Address | Bytes | Mnemonic | Hint).
- **Hex Dump**: 2 splitters (Address | Hex | ASCII).
- **Stack**: 2 splitters (Offset | Address | Annotation).
- **Registers**: 2 splitters (Name | Value | Annotation).
- **Horizontal scroll sync** — dashed splitters in all panels track their inner `ScrollViewer.HorizontalOffset` through a `TranslateTransform`.

### Window / Dock Layout Persistence

- Window position, size, and maximized state saved to `kf_settings.txt` on close and restored on launch.
- GridSplitter ratios for top/bottom rows and for the two column pairs (Disasm↔Registers, HexDump↔Stack) persist.
- Per-panel column widths (`Ui.Col.Disasm.*`, `Ui.Col.Hex.*`, `Ui.Col.Stack.*`, `Ui.Col.Reg.*`) persist.

### Flags Panel

- CF/PF/AF/ZF/SF/TF/IF/DF/OF moved out of the vertical register list into a 3×3 grid at the bottom of the Registers panel. Clicking a flag toggles it.
- `1` values are highlighted red; `0` values use the normal foreground color.

### Toolbar & Icons

- **SVG icons for every toolbar button** (glossy Aqua-style, drawn in-project) — Run / Pause / Continue / Restart / Step Into / Step Over / Step Out / Run to Cursor / BP / HW BP / Watch Write / Watch R/W / Memory BP / Go to / Attach / Open / Connect / Disconnect (orange lightning bolt).
- **Tab icons** — all 16 built-in tabs and all 17 plugin tabs have custom SVG icons. Plugin tabs keep their per-tab FG/BG colors from the theme via `Tag`-based identification.
- **Menu icons** — every pulldown item in File / Debug / Search / View / Symbols / Help gets a matching 16×16 SVG icon.
- **Toolbar expanded** — every menu item now has a matching toolbar button grouped by category (File / Process / Execution / Breakpoints / Search / View / Symbols / Service / Help / Go-to).
- **SharpVectors.Reloaded** added as a NuGet reference for runtime SVG rendering.

### Driver Safety

- `MmCopyVirtualMemory` now pre-validated — before calling it the IOCTL attaches to the target process and probes each 4 KB page with `MmIsAddressValid`. If any page is unmapped it returns `STATUS_ACCESS_VIOLATION` instead of bugchecking. Previously bad user-mode addresses (page-faulting deep inside `MmProbeAndLockPages`) could crash the VM with `PAGE_FAULT_IN_NONPAGED_AREA (0x50)`.

### Driver Loader

- `KfLoader.exe` with no arguments now defaults to **`load`** — the common case. Explicit `unload`, `status`, `info` still work as before.

### Fonts

- **Project-wide default** changed to `Lucida Console` so monospaced data panels (Disassembly / Hex Dump / Stack / Registers) render with aligned columns. UI chrome (menu, toolbar, tabs, status bar) inherits the same family for a uniform look.
- **Per-panel override** in Appearance → Font — each data panel's font family and size are saved as `Ui.Font.*` / `Ui.FontSize.*` in `kf_settings.txt` and re-applied on launch.
- **Embedded Intel One Mono** family resolved through a `pack://` URI so the font works even if the user has never installed it system-wide.

### Disassembly Hints Off-by-Default Performance

- Even when Live Hints are disabled the previous `RebuildLiveHints` pass fired on every `Instructions.CollectionChanged`. That pass now short-circuits immediately if `LiveHintsEnabled == false`, so there's no cost for users who never enabled it.

### Disasm / Hex Layout Fixes

- Per-row `Grid.Height` fixed to `ceil(fontSize × 1.3)` so column sizing changes can't cause blank rows between items.
- Each text cell wrapped in a fixed-Width `Border` with `ClipToBounds` so long symbol names (`InlineUIContainer` children) can't push the Grid to remeasure a wider column and bleed into neighbours.
- `Lucida Console` forced on all four data panels so hex bytes, addresses, and ASCII previews share the same glyph grid.

### Address Column Simplification

- Disassembly Address column now always shows the hex address (`00007FF6\`12340000`). Previously the column was overwritten with the resolved symbol name for function-start addresses, which looked like a group header and hid the real address.

### "Not in main module" hint gating

- Live hints in the disassembly column are emitted only for instructions whose address falls inside the main `.exe` module. System-DLL frames (ntdll, kernelbase, msvcrt, …) stay clean of hint spam.

### Dump Analysis Tooling

- `python parse_minidump.py <dump.dmp>` — minimal, dependency-free minidump parser. Reads DUMP\_HEADER64 (bugcheck code + four args), extracts the saved CONTEXT (RIP/RAX/RCX/…), and lists every `.sys` path referenced in the dump. Does not download symbols, does not load `kd.exe`, does not hit the Microsoft symbol server, does not explode the RAM of your host while you're trying to debug the VM.

## v1.8.1 — 2026-04-19

### Debugger UX

- **Restart Sample button** — new green `🔄` toolbar button (and `Debug → Restart Sample` menu item) relaunches the last opened sample and breaks at its PE entry point without showing the file browser again. Works for both EXE and driver targets.
- **Run / Pause as emoji** — toolbar buttons replaced text labels: Run → `▶`, Restart → `🔄`, Pause → `⏸`.

### Stack Annotations

- **`return to` prefix** — stack entries that are return addresses (detected by checking for CALL opcode in the 6 bytes preceding the address) are labeled `return to module!FuncName+0xOffset`, matching x64dbg/OllyDbg style.
- **String content** — if a stack/register value points to a readable ASCII or UTF-16 string (≥3 chars), the annotation shows the string in quotes (`"s3cr3tK3y!"`). String takes priority over module+offset.
- **`module!symbol` format** — all module annotations now use WinDbg/x64dbg notation: `ntdll!LdrInitializeThunk+0x18` instead of `ntdll.dll+0x1234`.

### Register Annotations

- **Annotation column** added to the Registers DataGrid — same teal color as stack, same resolution logic: string → `return to module!sym` → `module!sym` → empty.
- **RIP / RSP / RBP excluded** — these three registers never show annotations (their context is already visible in the disassembly and stack views).

### Disassembler Scroll Behavior

- **Auto-scroll to RIP on break** — disassembly view scrolls to the current instruction whenever `SetInstructions` is called with a non-null RIP.
- **Scroll-up clamped at RIP** — scrolling up past the top of the loaded content now snaps back to RIP instead of loading code above it. Disassembly only extends downward.

## v1.8.0 — 2026-04-07

### New Plugin: Xrefs (Cross-References)

- **Xrefs TO** — find all references to a given address across modules: `CALL`, `JMP` (direct and IAT indirect via `[rip+xxx]`), `LEA reg, [rip+xxx]`, `MOV` with RIP-relative addressing, raw pointer references in data (vtables, function pointer tables).
- **Xrefs FROM** — find all outgoing references from a function (what it calls/references).
- **Scope selector** — scan current module only or all loaded modules.
- **Symbol resolution** — each xref shows source module, resolved symbol name, and full instruction text.
- **Navigation** — double-click any xref to jump to source in disassembly. Context menu: Go to Source, Go to Target, Copy Address, Copy All Results.
- **Cancellation** — Stop button to cancel long scans.
- **Menu integration** — "Find Xrefs at RIP" in Plugins menu.

### New Plugin: Session Manager

- **Save/Load full session state** to `.kfsession` files (JSON format).
- **Breakpoints** — saved and restored via UI toggle (updates breakpoint list, disasm markers, and driver).
- **Annotations/Comments** — all address annotations saved and restored.
- **User-defined functions** — function names registered via `RegisterFunction` saved with address and size.
- **Graph block colors** — custom block colors from Graph View plugin saved as `#RRGGBB`.
- **ASLR auto-rebase** — module table saved alongside data; on load, all addresses automatically rebased if modules shifted.
- **Auto-naming** — save dialog defaults to `TargetName.kfsession`.

### Scripting Plugin: Syntax Highlighting

- **AvalonEdit code editor** — replaced plain TextBox with AvalonEdit `TextEditor` with line numbers, undo/redo, and search.
- **Dark theme C# highlighting** — VS Code Dark+ color scheme: blue keywords (`#569CD6`), pink control flow (`#C586C0`), teal types (`#4EC9B0`), orange strings (`#CE9178`), green comments (`#6A9955`), light-green numbers (`#B5CEA8`), yellow method calls (`#DCDCAA`).
- **Built-in C# definition recoloring** — uses AvalonEdit's built-in C# grammar with colors remapped for dark background (no XML parsing, crash-safe).

### Graph View Plugin

- **Fixed zoom** — zoom now correctly keeps the point under the mouse cursor stable. Previous formula was wrong for the `translate → scale` transform order, causing the graph to "fly away" on zoom in/out.
- **Fixed pan speed** — mouse delta divided by current scale, so panning is 1:1 with cursor movement at any zoom level.
- **Block colors exposed** — `_blockColors` dictionary published via `SetPluginData("GraphBlockColors")` for cross-plugin access (used by Session Manager).

### SDK Extensions

- `IBreakpointApi.ToggleBreakpoint(address, type)` — toggle breakpoint via UI (updates list, disasm markers, and driver). Used by Session Manager for correct BP restoration.
- `ISymbolApi.GetRegisteredFunctions()` — returns all user-defined function names. New `PluginFunctionEntry` model (Address, Name, Size).
- `IUiApi.SetPluginData(key, value)` / `GetPluginData(key)` — cross-plugin data store for runtime communication (e.g. graph colors).

### Themes

- **6 missing plugin tab colors** added to all 9 themes: Network Monitor, FLIRT Signatures, Scripting, Graph View, Xrefs, Exports.

### Build

- `build.ps1` now builds XrefsPlugin and SessionPlugin.
- Removed unused field `SymbolService._functionTableDirty` (CS0414 warning fix).

## v1.7.1 — 2026-04-05

### New Plugin: Network Monitor

- **Real-time network traffic capture** — hooks 28 network API functions across Winsock (send/recv/sendto/recvfrom/WSASend/WSARecv/WSASendTo/WSARecvFrom), control (connect/accept/bind/listen/closesocket), WinINet (InternetOpen/Connect, HttpOpenRequest/SendRequest, InternetReadFile), and WinHTTP (WinHttpOpen/Connect/OpenRequest/SendRequest/ReadData).
- **Live traffic grid** — DataGrid with columns: index, timestamp, TID, direction (SEND/RECV/CTRL/HTTP), function name, data size, details, return value. Auto-scrolls to latest event.
- **Data preview** — for send operations, captures first 64 bytes of the buffer. Shows as ASCII if printable, hex dump otherwise.
- **Socket address parsing** — `connect`/`bind` calls show parsed `sockaddr_in` (IP:port).
- **Return value capture** — one-shot breakpoint at return address captures RAX (bytes sent/received, new socket handle, etc.).
- **Direction filter** — filter by SEND/RECV/CTRL/HTTP or show all.
- **Text filter** — filter by function name, details, or data preview.
- **Export CSV** — save full trace to CSV file.
- **Detail panel** — click any row to see full event details in the bottom panel.
- **x86/x64 support** — handles both fastcall (x64) and stdcall (x86) calling conventions.
- **Auto-stop on disconnect** — hooks automatically removed when debugger disconnects.

### Build

- `build.ps1` now builds and copies NetworkMonitorPlugin.

## v1.7.0 — 2026-04-04

### New Plugin: C# Scripting Console

- **Roslyn-based C# REPL** — write and execute C# scripts with full access to `IDebuggerApi` directly in the debugger. No compilation, no separate project — type code, press F5, see results.
- **REPL state** — variables persist between executions. Define a variable in one run, use it in the next.
- **Built-in shortcuts** — `ReadMem()`, `WriteMem()`, `ReadString()`, `ReadWString()`, `ReadPtr()`, `ReadU32()`, `ReadU64()`, `Reg("RAX")`, `RIP`, `RSP`, `Sym(addr)`, `Addr("name")`, `print()` — all available as top-level globals without boilerplate.
- **Run selection** — select a fragment of code and press F5 to execute only the selected portion.
- **Event handlers from scripts** — register `OnDebugEventFilter` callbacks to build custom breakpoint loggers, API tracers, and auto-unpackers without writing a full plugin.
- **Load/Save .csx files** — save scripts to disk and reload them later.
- **Console.WriteLine capture** — `Console.WriteLine` output is redirected to the output panel alongside `print()`.
- **Cancellation** — Stop button cancels long-running scripts.

### New Plugin: Graph View (CFG)

- **Control flow graph visualization** — IDA Pro-style graph view of functions. Basic blocks as nodes, edges for branches. MSAGL Sugiyama layered layout for automatic top-down arrangement.
- **Capstone disassembly** — own disassembler instance, independent of the main UI. Parses branch targets, classifies instructions (call/jmp/jcc/ret), detects function boundaries.
- **Symbol resolution** — call targets show as `MODULE!FunctionName` (e.g. `KERNEL32.dll!CreateFileA`). Follows IAT thunks (`FF 25`, `48 FF 25`, `E9 rel32`) to resolve real API names. Uses PDB function table as fallback when `SymFromAddr` fails.
- **Syntax highlighting** — mnemonics colored by type: blue for regular, purple for branches/ret, yellow for calls. Resolved call operands highlighted. Address column in gray.
- **Current RIP highlighting** — block containing current instruction pointer shown with green border.
- **Function navigation** — double-click on underlined call targets or right-click → "Graph: MODULE!func" to navigate into called functions. Full navigation stack with **Back (Shift+Esc)** to return.
- **Block context menu** — Set Color (7 colors + reset), Collapse/Expand block, Copy Address/Assembly, Add Comment (synced with Bookmarks plugin), Toggle Breakpoint, Graph called functions, Collapse All/Expand All, Reset All Colors, Go Back.
- **Zoom & pan** — mouse wheel zooms relative to cursor position, left-drag pans, Fit/1:1 buttons. `BitmapCache` for GPU-accelerated smooth pan/zoom.
- **Thunk/stub filtering** — IAT stubs and library code blocks automatically excluded from graph via reachability analysis from entry point.
- **User annotations** — comments from Bookmarks plugin shown as green `; comment` text in graph blocks.

### New Plugin: FLIRT Signatures

- **IDA-compatible FLIRT pattern matching** — recognizes statically linked library functions by matching byte patterns at function entry points. Loads `.pat` files from `plugins/FLIRTpat/` directory.
- **Function discovery** — enumerates functions via `.pdata` (RUNTIME_FUNCTION) on x64 or prologue scanning (`55 8B EC`) on x86.
- **Bulk memory reads** — reads entire executable sections at once, then indexes locally. Avoids thousands of per-function kernel round-trips.
- **Prefix-indexed matching** — O(1) signature lookup via 2-byte prefix hash instead of linear scan.
- **Built-in fallback database** — ~50 common MSVC x64 CRT patterns (security_init_cookie, initterm, malloc, free, strlen, printf, etc.) when no `.pat` files are present.
- **Apply/Clear annotations** — one-click `[FLIRT] function_name` annotations in the disassembly view, with undo via Clear.
- **.pat generator tool** (`tools/GeneratePatFiles.csproj`) — generates `.pat` files from MSVC `.lib` archives (libcmt, libvcruntime, libcpmt, libconcrt, libucrt) for both x64 and x86. Auto-discovers Visual Studio and Windows SDK paths.

### Build

- `build.ps1` now builds and copies ScriptingPlugin (+ Roslyn), FlirtPlugin, and GraphViewPlugin (+ MSAGL).
- `build.ps1` creates `bin\UI\plugins\FLIRTpat\` directory for `.pat` signature files.
- `.pat` files are now included in the plugin data file copy step.

### Symbol Resolution

- **Function table fallback** — when `SymFromAddr` fails (common with certain PDBs), `ResolveAddress` now falls back to a function lookup table built from `SymEnumSymbols`. Binary search by address returns `funcName` or `funcName+0xOffset`.
- **Cache fix** — `module+offset` fallback results are no longer cached, so they get re-resolved after PDB loads.
- **Cache invalidation on module load** — stale symbol cache entries within a module's address range are cleared when `SymLoadModuleExW` loads new symbols.

## v1.6.0 — 2026-03-30

### Service Debugging (Debug Service)

- **Fixed: services now start through SCM** — previously `HandleStartService` used `CreateProcess` directly, bypassing the Service Control Manager. `StartServiceCtrlDispatcher` would fail and ServiceMain was never called. Now the relay copies the binary with a `_kfdebug` suffix, patches the entry point to `EB FE` (infinite loop), temporarily swaps `ImagePath` via `ChangeServiceConfig`, and starts through `StartServiceA`. The SCM pipe connection is preserved so `StartServiceCtrlDispatcher` succeeds and ServiceMain is reached.
- **Two-phase start (prepare + start)** — `START_SERVICE` pseudo-IOCTL only prepares (copy, patch, swap ImagePath). The UI then installs the debug hook targeting the service PID, and uses `START_DRIVER` to call `StartServiceA` in a background thread — same proven pattern as driver loading.
- **PID discovery via toolhelp** — SCM doesn't populate `dwProcessId` until `StartServiceCtrlDispatcher` is called (which can't happen while spinning at EB FE). `HandleQueryServicePid` now falls back to `CreateToolhelp32Snapshot` + `Process32First/Next` to find the `_kfdebug` process by image name.
- **Entry point INT3 injection from UI** — after the process spins at EB FE and the debug hook is installed, the UI sets a software breakpoint (`SetBreakpoint`) at the entry point. The driver's `CR0.WP` trick handles read-only `.text` pages. `WaitDebugEvent` catches the INT3.
- **Original bytes restore via ProtectMemory** — after catching the entry point INT3, the UI changes page protection to RWX, writes the real original bytes (returned by relay in `KF_START_SERVICE_OUT.OriginalBytes[2]`), and restores protection. Previously `WriteMemory` silently failed on RX pages.
- **CONTINUE_STEP_PAST + event loop** — continuing from the entry point INT3 uses `CONTINUE_STEP_PAST` (mode 1) with a loop to skip spurious single-step events, matching the driver loading flow.
- **Leftover cleanup** — if a previous run left the `ImagePath` pointing to a `_kfdebug` copy, `HandleStartService` detects and restores the original path before proceeding. A deferred background thread restores `ImagePath` and deletes the copy after the process exits.
- **svchost rejection** — svchost-hosted services are detected and rejected with a clear error message.
- **RefreshImports/Sections/Exceptions** — now called when stopped at ServiceMain or StartServiceCtrlDispatcher (previously missing, so imports tab was empty).

### MCP Plugin

- **Removed decompilation timeout** — `ExecDecompile` no longer times out after 30 seconds. The polling loop now waits indefinitely until RetDec finishes, so AI assistants always receive the complete decompiled output.

## v1.5.1 — 2026-03-28

### Register Editing

- **Modify register value** — double-click any register (or right-click → Modify Value) to enter a new hex value. Works for all general-purpose registers (RAX–R15 / EAX–ESP), RIP/EIP, RFLAGS/EFLAGS, and debug registers (DR0–DR7).
- **Toggle CPU flags** — double-click or right-click → Toggle Flag on individual flags (CF, PF, AF, ZF, SF, TF, IF, DF, OF) to flip them in RFLAGS/EFLAGS.
- **Zero / Increment / Decrement** — context menu shortcuts to zero out a register or adjust it by ±1.
- **Read-modify-write via WRITE_REGISTERS IOCTL** — new `WriteRegisterByName` in DriverComm reads the full register set, patches the target field, and writes back atomically. Editing is only available in break state.

### Driver Stability

- **Fixed BSOD when VM left overnight with debugged process open** — system would blue-screen if the target VM was left running overnight with a process attached in the debugger.

## v1.5.0 — 2026-03-27

### Inline Assembler

- **Assemble dialog** (Space or context menu → Assemble) — type assembly text (`mov eax, 1`, `xor eax, eax`, `jmp 0x401000`) or hex bytes (`B8 01 00 00 00`), see live preview of encoded bytes with automatic NOP padding when the new instruction is shorter than the original.
- **NOP Instruction** (context menu) — replaces the selected instruction with NOPs (fills entire instruction size).
- **Fill with NOPs** (context menu) — fills N bytes at the selected address with `0x90`.
- **x86/x64 assembler** built on Iced.Intel — supports 80+ instruction types: `mov`, `add`, `sub`, `xor`, `and`, `or`, `cmp`, `test`, `lea`, `push`, `pop`, `inc`, `dec`, `jmp`, `call`, all conditional jumps (`je`/`jne`/`jg`/`jl`/...), `shl`/`shr`, `movzx`/`movsx`, `cmovxx`, `bsf`/`bsr`, memory operands (`[rax+8]`, `dword [ebp-4]`), and more.
- **Patch tracking** — all patches recorded in Patches list with original bytes for undo. Patches included in PE Rebuilder dumps.

### Driver Stability

- **Fixed BSOD on detach/disconnect** — IRP double-complete race condition caused `DRIVER_IRQL_NOT_LESS_OR_EQUAL` or system hang. `IoSetCancelRoutine` return value now checked under spinlock in `KfDebugHookDeactivate`, `KfDebugHookCleanup`, and `KfReportAndBlock`. If cancel routine already owns the IRP, we skip completion. Standard DDK safe-cancel pattern.
- **Fixed stepping not working after re-open** — `KfDebugHookDeactivate` and `KfSetTargetPid` now reset full session state (`g_EventPending`, `g_TraceActive`, `g_ContinueMode`, `g_ContinueReady`, `g_ContinueEvent`). Previously stale state from previous session caused step events to be lost.
- **KdDebuggerEnabled re-assertion** — `KfSetTargetPid` and `KfInstallDebugHook` (early return path) now call `KfReassertDebugFlags()` after all `DbgPrint` calls, not before. `DbgPrint` resets `KdDebuggerEnabled=FALSE` via KD transport when no kernel debugger is attached.
- **Process exit notification** — `PsSetCreateProcessNotifyRoutine` callback auto-deactivates debug hook when target process exits, cancelling pending WAIT IRP.

### Relay

- **Synchronous DBG channel** — replaced thread pool dispatch (`QueueUserWorkItem`) with direct synchronous `DeviceIoControl` loop for the DBG channel. Eliminates stale workers holding pending IOCTLs and corrupting the TCP response stream.
- **Process exit reporting** — `[dbg] Process XXXX exited with code: N (0xN)` printed when debugged process terminates.

### Step/Run Reliability

- **Direct WAIT+Continue for stepping** — `StepIn` and `PluginSingleStep` no longer use `StartDebugListener`. Instead, `WaitDebugEvent` (DBG channel) and `ContinueDebugEvent` (CMD channel) are sent in parallel, with 5-second timeout and diagnostic stats on failure.
- **DebugListener exits on first null** — prevents ghost WAIT IRPs that desynchronize the DBG TCP stream between sessions. Previously the listener looped and sent additional WAITs after process exit, creating stale pending IRPs in the driver.
- **DBG channel interrupt** — `StopDebugListener` sets a short `ReadTimeout` on the DBG stream to force-unblock any pending TCP read, then restores infinite timeout.

### Disassembler

- **Right-click selects instruction** — `MouseRightButtonDown` now sets `SelectedDisasmAddress` so context menu actions work without prior left-click.

## v1.4.0 — 2026-03-27

### New Plugin: PE Rebuilder

- **PE Rebuilder / Import Reconstructor plugin** — Scylla-style PE dumper with IAT reconstruction. Dumps process PE from memory, fixes headers, rebuilds import directory.
- **Auto Rebuild** — one-click workflow: detects OEP from RIP, finds ImageBase, auto-scans IAT, resolves all imports, dumps PE with fixed imports and save dialog. No manual input required.
- **IAT auto-detection** — disassembles from OEP, finds `call [mem]`/`jmp [mem]` patterns, walks backward/forward to determine IAT boundaries. Falls back to PE import directory (entry 12) if heuristic fails.
- **ExportResolver** — parses export tables of all loaded modules, follows JMP trampolines (up to 5 hops: `E9 rel32`, `FF 25`, `48 FF 25`, `mov rax,imm64; jmp rax`), builds forward resolution map (e.g. `ntdll.RtlAllocateHeap` → `kernel32.HeapAlloc`) from 12 key DLLs.
- **PE scan backwards** — if RIP is not in any known module (e.g. unpacked code), scans memory backwards page-by-page looking for MZ+PE signature.
- **Import tree view** — displays reconstructed imports grouped by DLL with function names and IAT addresses.
- **Header fixes** — corrects OEP RVA, disables ASLR, fixes section raw data pointers, adds `.import` section with full IMAGE_IMPORT_DESCRIPTOR array + Hint/Name table.
- **All UI colors via theme system** — `SetResourceReference` for every control, no hardcoded colors.

### New Plugin: Signature Detector

- **PEiD-compatible signature detector** — scans process memory against 4445 packer/compiler signatures from the SANS/PEiD community database (`userdb.txt`).
- **Scan Main Module / Scan All Modules** — entry-point-only and full-scan modes.
- **NOTICE.txt** — attribution for community-contributed signature database.

### Plugin System Fixes

- **Plugin enable/disable now works correctly** — fixed checkbox binding (`UpdateSourceTrigger=PropertyChanged` + `Click` event instead of `Checked`/`Unchecked`), direct `cb.IsChecked` read from sender.
- **Tabs and menu items hide/show on disable** — tracks plugin name → tabs/menus mapping via `OnPluginInitializing` callback. Resolves mismatch between `plugin.Name` and `AddToolPanel` title (e.g. "Anti-Anti-Debug" vs "Anti-Debug").
- **Plugin state persisted in `kf_settings.txt`** — `DisabledPlugins=name1,name2` line, restored on startup via `ApplyPersistedState`. No separate `plugins_state.json`.
- **Plugin Settings window** enlarged to 620×450.

### Disassembler

- **Dynamic scroll loading** — disassembly view loads instructions on demand when scrolling up/down instead of reading a fixed 4096-byte block. Keeps ~1000 instructions in view, trims opposite end to avoid memory bloat.

### UI

- **Toolbar icons** — Open (⊕), Debug (📂), Connect (🔌) buttons replaced text labels with emoji glyphs.
- **Unified Attach/Detach button** — single button toggles between "Attach" and "Detach" based on `IsDebugHookActive` state via `DataTrigger`.

### Relay

- **Crash fix on disconnect** — added `CancelIoEx` + `CancelSynchronousIo` on DBG channel handle before closing, preventing crash when thread pool workers are inside blocking `DeviceIoControl` (e.g. `WAIT_DEBUG_EVENT`). Increased wait timeout 3s→5s with `TerminateThread` fallback.

### Build

- **`build.ps1` PATH fix** — auto-adds `%ProgramFiles%\dotnet` and `%SystemRoot%\System32` to `$env:PATH` at script start.
- **PE Rebuilder** added to plugin build list.
- **Signature Detector** added to plugin build list.

## v1.3.0 — 2026-03-24

### New Plugin: MCP Server

- **MCP (Model Context Protocol) Server plugin** — exposes the full debugger API as an MCP SSE server. Any MCP-compatible AI client (Claude Code, Cursor, Windsurf, etc.) can connect and control the debugger remotely.
- **62 debugger tools** available over MCP — the most comprehensive debugger AI integration available:
  - **State**: `get_debugger_state`, `read_registers`
  - **Breakpoints** (5 types): `set_breakpoint`, `set_hardware_breakpoint`, `set_hw_write_watchpoint`, `set_hw_access_watchpoint`, `set_memory_breakpoint`, `remove_breakpoint`, `list_breakpoints`
  - **Memory**: `read_memory`, `read_pointer`, `read_string`, `read_unicode_string`, `read_unicode_struct`, `write_memory`, `search_memory`, `compare_memory`, `allocate_memory`, `free_memory`, `protect_memory`
  - **Registers**: `write_rip`, `write_rip_and_rsp`
  - **Disassembly**: `disassemble`, `decompile`, `navigate_disasm`, `disasm_go_back`
  - **Symbols**: `resolve_symbol`, `list_strings`, `xrefs_to`
  - **PE analysis**: `dump_pe_header`, `dump_imports`, `dump_exports`, `dump_peb`, `dump_teb`, `dump_stack`
  - **Modules**: `list_modules`, `list_kernel_modules`, `refresh_modules`, `add_unpacked_module`, `add_module_sections`
  - **Process/Threads**: `list_processes`, `list_threads`, `suspend_thread`, `resume_thread`, `get_peb_address`
  - **Execution**: `continue_execution`, `single_step`, `step_over`, `step_out`, `run_to_address`, `skip_instruction`, `pause_execution`, `wait_for_break`
  - **Patching**: `nop_instruction`, `patch_jump`
  - **Anti-debug bypass**: `clear_debug_port`, `clear_thread_hide`, `install_ntqsi_hook`, `remove_ntqsi_hook`, `probe_ntqsi_hook`, `spoof_shared_user_data`
- **Settings panel** in "MCP Server" tab — status indicator (green/gray), port configuration with persistence, start/stop buttons, copyable `.mcp.json` snippet, real-time activity log with timestamps.
- **Server instructions** embedded in MCP — tells AI clients to prefer `decompile` over `disassemble` and outlines the recommended analysis workflow.
- **WPF Dispatcher marshaling** — all UI-thread and execution-control API calls are dispatched via `Dispatcher.Invoke`, ensuring correct behavior when called from MCP's HttpListener threads.
- **RIP-tracking `wait_for_break`** — records RIP before resume and detects state changes by RIP delta, not phase transitions. Works correctly even when breakpoints hit instantly (<1ms).

### AI Assistant Plugin

- **14 new tools** added (now 62 total, matching MCP): `write_rip_and_rsp`, `add_module_sections`, `dump_stack`, `dump_peb`, `dump_teb`, `dump_pe_header`, `dump_imports`, `dump_exports`, `xrefs_to`, `nop_instruction`, `patch_jump`, `list_strings`, `compare_memory`, `read_unicode_struct`.
- **`wait_for_break` fix** — same RIP-tracking approach as MCP, fixing hangs when breakpoints trigger faster than the poll interval.
- **Updated default system prompt** — analysis workflow guidance: decompile-first, read_string for references, read_pointer for vtables, resolve_symbol before decompiling sub-functions.

### Themes

- **MCP Server tab colors** added to all 9 themes (green/teal tones — network/server style).
- **MCP Settings panel** fully themed via `SetResourceReference` — `PluginBgBrush`, `PluginFgBrush`, `PluginFgDimBrush`, `PluginAccentBrush`, `PluginControlBgBrush`, `PluginButtonBgBrush`, `PluginBorderBrush`. Automatically adapts to theme changes.

## v1.2.0 — 2026-03-21

### AI Assistant Plugin

- **New plugin: AI Assistant** — interactive chat-based reverse engineering assistant integrated into KernelFlirt. Works like AI plugins in IDA Pro — analyzes code, explains functions, sets breakpoints, reads memory, steps through code.
- **Universal AI provider support** — works with any OpenAI-compatible API: DeepSeek, Qwen, ChatGPT, Ollama, LM Studio, Anthropic (via proxy), and others. Configurable endpoint, model, API key, temperature, max tokens, and system prompt.
- **Decompiler integration** — `decompile` tool sends C pseudocode to AI for analysis (like Hex-Rays in IDA Pro), much more efficient than raw disassembly.
- **Debugger tool calling** — AI can execute real debugger actions: set/remove breakpoints, read/write memory, read registers, step in/over/out, continue execution, disassemble, resolve symbols, navigate disassembly, list modules/threads.
- **Settings dialog** — provider presets (DeepSeek, OpenAI, Anthropic, Ollama, LM Studio, Qwen, Custom), API key input, model selection, token/temperature sliders, editable system prompt with reset.
- **Chat history management** — automatic context trimming to stay within token limits.
- **AI Assistant tab colors** added to all 9 themes (purple/violet tones).

### Remote File Browser

- **Full-featured file browser** when connecting to relay VM via Open & Debug.
- **5 new relay IOCTLs** — `READ_FILE`, `WRITE_FILE`, `DELETE_PATH`, `CREATE_DIR`, `RENAME_PATH`.
- **File operations** — download, upload, delete, rename, create folder, copy path. Chunked transfer with progress.
- **Navigation** — back/forward history, up button, editable address bar, drive selector, refresh.
- **Multi-select**, drag-and-drop upload, keyboard shortcuts (F2, Del, F5, Backspace, Alt+arrows).
- **Double-click**: folders navigate, .exe/.sys open in debugger, others download.

### Disassembler

- **Go Back** in context menu — returns to previous location after following imports, symbols, or Go To commands.

## v1.1.0 — 2026-03-19

### Theme System: Plugin Customization

- **12 new plugin color keys** — `PluginBg`, `PluginFg`, `PluginFgDim`, `PluginBorder`, `PluginAccent`, `PluginControlBg`, `PluginButtonBg`, `PluginButtonHover`, `PluginSelection`, `PluginGridAltRow`, `PluginGroupHeader`, `PluginGroupBg`. All plugin controls inherit these colors automatically via implicit WPF styles — plugin authors no longer need to hardcode any colors.
- **Plugin wrapper** — SDK wraps each plugin's content in a `ContentControl` with scoped `ResourceDictionary`, remapping standard WPF brush keys to `PluginXxx` equivalents. Plugins automatically pick up theme colors without any code changes.
- **Implicit styles for all WPF controls** in `Dark.xaml` — `CheckBox`, `GroupBox`, `Label`, `ListView`, `ListViewItem`, `ListBoxItem`, `ScrollViewer`, `DataGridRow`, `DataGridCell`, `ToolTip`, `TextBox`, `ComboBox`. Plugins using standard WPF controls get themed for free.
- **Per-plugin tab header colors** — each plugin tab can have individual `Fg`/`Bg` overrides (`Tab.Anti-Debug.Fg`, `Tab.API Monitor.Bg`, etc.). Falls back to global tab style if not set.
- **"Plugins" tab in Settings** — color pickers for all 12 plugin control colors + per-plugin tab header Fg/Bg overrides with theme selector and reset button.
- **All 9 theme presets updated** with unique plugin color palettes: default-dark, dracula, ida-pro, long_night, monokai, ollydbg, ollydbg-light, sakura, x64dbg.
- **All 4 plugins cleaned** — removed hardcoded `Foreground`, `Background`, `BorderBrush` from ThemidaPlugin, StringDecryptorPlugin, AntiDebugPlugin, ApiMonitorPlugin. Simplified `MakeStyledComboBox` in StringDecryptorPlugin (120+ lines of custom ControlTemplate replaced with 10 lines).

### Bug Fixes

- **Plugin tab colors not applied on startup** — `ApplyTabColors` was called before `LoadPlugins()`, so plugin tabs didn't exist yet. Added re-apply after plugin loading.

### Build

- `build.ps1` now copies `kf_settings.txt` to `bin\UI\`.
