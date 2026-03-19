# Changelog

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
- **Fixed XOR test strings** (`xor_strings.c`) — 6 encrypted strings had wrong bytes causing garbled decryption: `Hello, World)` -> `!`, `cmd,ere` -> `cmd.exe`, `CreateRemoteTfread` -> `Thread`, `VirtualAllocEr` -> `Ex`, `HKEA%LOCAL%MACHINE.SODTWARE` -> `HKEY_LOCAL_MACHINE\SOFTWARE`, `http,//evil.com/pasload.bin` -> `http://evil.com/payload.bin`.

### Build

- `build.ps1` now copies `kf_settings.txt` to `bin\UI\`.
