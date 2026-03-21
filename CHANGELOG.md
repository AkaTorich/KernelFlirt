# Changelog

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
