# Themes

KernelFlirt includes 9 built-in color themes with 100+ customizable color keys.

## Built-in Themes

| Theme | Style |
|-------|-------|
| **default-dark** | Material-inspired dark blue |
| **x64dbg** | x64dbg classic dark |
| **monokai** | Monokai (Sublime Text) |
| **dracula** | Dracula palette |
| **ida-pro** | IntelliJ/IDA dark |
| **ollydbg** | OllyDbg dark |
| **ollydbg-light** | OllyDbg light (white background) |
| **long_night** | Warm dark tones |
| **sakura** | Pink/purple aesthetic |

## Switching Themes

**View → Settings** → select a theme from the dropdown → Apply.

All UI elements update instantly: disassembly colors, tab headers, plugin panels, script editor, decompiler, navigation bar.

## Customizing Colors

Each theme is a `.txt` file in the `themes/` folder with `Key=Value` pairs:

```
Color.Bg=#1A1A2E
Color.DsmMnemonic=#82AAFF
Color.Tab.Disassembly.Fg=#82AAFF
Color.PluginBg=#1A1A2E
Color.ScriptKeyword=#569CD6
```

### Color Categories

- **General:** Bg, BgLight, BgPanel, Border, Fg, FgDim, Accent, Selection, Toolbar, StatusBar
- **Disassembly:** DsmAddress, DsmMnemonic, DsmRegister, DsmBytes, DsmNumber, DsmJump, DsmString, DsmComment, DsmSymbol, DsmFunction, DsmBpMarker, DsmCurrentLine
- **Stack:** StackOffset, StackAddress, StackAnnotation
- **Tabs:** TabBg, TabFg, TabSelBg, TabSelFg, TabSelBorder, TabHoverBg + per-tab overrides (Tab.Disassembly.Fg, Tab.Graph View.Bg, etc.)
- **Plugins:** PluginBg, PluginFg, PluginFgDim, PluginBorder, PluginAccent, PluginControlBg, PluginButtonBg, PluginSelection
- **Script editor:** ScriptBg, ScriptFg, ScriptKeyword, ScriptControl, ScriptType, ScriptString, ScriptComment, ScriptNumber, ScriptMethod, ScriptPunctuation
- **Scrollbar:** ScrollBarBg, ScrollBarThumb, ScrollBarThumbHover, ScrollBarThumbPressed, ScrollBarArrow
