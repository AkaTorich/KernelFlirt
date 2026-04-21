using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using KernelFlirt.UI.Models;
using KernelFlirt.UI.ViewModels;

namespace KernelFlirt.UI;

public partial class MainWindow : Window
{
    private MainViewModel VM => (MainViewModel)DataContext;
    private readonly List<ContentControl> _pluginWrappers = [];
    private readonly Dictionary<string, List<TabItem>> _pluginTabs = [];
    private readonly Dictionary<string, List<MenuItem>> _pluginMenuItems = [];
    private string? _currentPluginName;
    private Services.CommandConsole? _console;
    private readonly List<string> _consoleHistory = [];
    private int _consoleHistoryIdx = -1;

    public record ConsoleCmd(string Name, string Hint);
    private static readonly ConsoleCmd[] _allCmds =
    [
        new("g",       "continue execution"),
        new("go",      "continue execution (alias for g)"),
        new("t",       "step into"),
        new("sti",     "step into (alias)"),
        new("p",       "step over"),
        new("sto",     "step over (alias)"),
        new("bp",      "bp <expr>          set software breakpoint"),
        new("bc",      "bc <expr>          clear breakpoint at addr"),
        new("bl",      "list breakpoints"),
        new("d",       "d <expr>           follow in Hex Dump"),
        new("dump",    "dump <expr>        follow in Hex Dump"),
        new("dis",     "dis <expr>         navigate Disassembly"),
        new("u",       "u <expr>           navigate Disassembly"),
        new("r",       "r <reg>[=<expr>]   read/write register"),
        new("?",       "? <expr>           evaluate expression"),
        new("eval",    "eval <expr>        evaluate expression"),
        new("findall", "findall <pattern>  binary search"),
        new("find",    "find <pattern>     binary search (alias)"),
        new("clear",   "clear output"),
    ];

    public MainWindow()
    {
        InitializeComponent();
        if (VM.ThemeColors.Count > 0)
            ApplyThemeColors(VM.ThemeColors);
        ApplyPersistedLayout();
        SetupFlagsGrid();
        StackList.Tag = _stackCols;
        _console = new Services.CommandConsole(VM);
        VM.LiveHintsChanged += () => RefreshDisasmView();
        Loaded += (_, _) => HookColumnOverlayScroll();
        PopulateThemesMenu();
        ApplyPanelFonts();
        // AddHandler with handledEventsToo so we intercept wheel even if a ScrollViewer
        // upstream marked it Handled (the default tunneling route misses some cases).
        AddHandler(PreviewMouseWheelEvent, new MouseWheelEventHandler(OnCtrlMouseWheel), true);
        LoadDecompilerHighlighting();
        VM.Instructions.CollectionChanged += (_, _) => RefreshDisasmView();
        VM.FilteredSections.CollectionChanged += (_, _) => RefreshNavBar();
        NavBar.SizeChanged += (_, _) => RefreshNavBar();
        VM.DisasmAppend += (instrs, trimTop) =>
        {
            DisasmControl.AppendInstructions(instrs);
            if (trimTop > 0) DisasmControl.TrimTop(trimTop);
        };
        VM.DisasmPrepend += (instrs, trimBottom) =>
        {
            DisasmControl.PrependInstructions(instrs);
            if (trimBottom > 0) DisasmControl.TrimBottom(trimBottom);
        };
        VM.BreakpointMarkersChanged += () =>
        {
            ImportsGrid.Items.Refresh();
            FunctionsGrid.Items.Refresh();
            SearchGrid.Items.Refresh();
            ExceptionsGrid.Items.Refresh();
            SectionsGrid.Items.Refresh();
        };
        VM.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.HexData))
                UpdateHexDumpDisplay();
            if (e.PropertyName == nameof(MainViewModel.IsDecompiling) && VM.IsDecompiling)
                MainTabControl.SelectedItem = DecompilerTab;
            if (e.PropertyName == nameof(MainViewModel.DecompiledCode))
                UpdateDecompilerText();
        };

        // Plugin UI integration — track which plugin is being initialized
        VM.SwitchToDisasmTab = () => MainTabControl.SelectedIndex = 0;

        VM.AddPluginMenuItem = (header, callback) =>
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => callback();
            PluginsMenu.Items.Add(item);
            // Track menu item by current plugin name
            if (_currentPluginName != null)
            {
                if (!_pluginMenuItems.ContainsKey(_currentPluginName))
                    _pluginMenuItems[_currentPluginName] = [];
                _pluginMenuItems[_currentPluginName].Add(item);
            }
        };
        VM.AddPluginToolPanel = (title, content) =>
        {
            var wrapper = new ContentControl { Content = content };
            ApplyPluginResources(wrapper);
            _pluginWrappers.Add(wrapper);
            var tab = new TabItem { Header = BuildPluginTabHeader(_currentPluginName, title), Content = wrapper, Tag = title };
            MainTabControl.Items.Insert(MainTabControl.Items.Count - 1, tab); // Before Log tab
            // Track tab by current plugin name
            if (_currentPluginName != null)
            {
                if (!_pluginTabs.ContainsKey(_currentPluginName))
                    _pluginTabs[_currentPluginName] = [];
                _pluginTabs[_currentPluginName].Add(tab);
            }
        };

        // Callback to set current plugin name before each plugin.Initialize()
        VM.OnPluginInitializing = name => _currentPluginName = name;

        // Wire up tab/menu show/hide for plugin enable/disable
        VM.PluginManager.SetTabVisible = (pluginName, visible) =>
        {
            if (_pluginTabs.TryGetValue(pluginName, out var tabs))
            {
                foreach (var tab in tabs)
                {
                    tab.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                    if (!visible && MainTabControl.SelectedItem == tab)
                        MainTabControl.SelectedIndex = 0;
                }
            }
            if (_pluginMenuItems.TryGetValue(pluginName, out var items))
            {
                foreach (var mi in items)
                    mi.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            }
        };

        VM.LoadPlugins();

        // Re-apply tab colors now that plugin tabs exist
        if (VM.ThemeColors.Count > 0)
            ApplyTabColors(VM.ThemeColors);

        // Add Settings item to Plugins menu
        if (PluginsMenu.Items.Count > 0)
            PluginsMenu.Items.Insert(0, new Separator());
        var settingsItem = new MenuItem { Header = "_Settings..." };
        settingsItem.Click += (_, _) =>
        {
            var win = new PluginSettingsWindow(VM.PluginManager) { Owner = this };
            win.ShowDialog();
        };
        PluginsMenu.Items.Insert(0, settingsItem);
    }

    private void LoadDecompilerHighlighting()
    {
        ApplyDecompilerHighlighting();
        DecompilerOutput.SetResourceReference(ICSharpCode.AvalonEdit.TextEditor.BackgroundProperty, "ScriptBgBrush");
        DecompilerOutput.SetResourceReference(ICSharpCode.AvalonEdit.TextEditor.ForegroundProperty, "ScriptFgBrush");

        // Context menu
        var ctx = new ContextMenu();
        var copyItem = new MenuItem { Header = "Copy", InputGestureText = "Ctrl+C" };
        copyItem.Click += (_, _) => DecompilerOutput.Copy();
        var selectAllItem = new MenuItem { Header = "Select All", InputGestureText = "Ctrl+A" };
        selectAllItem.Click += (_, _) => DecompilerOutput.SelectAll();
        var copyAllItem = new MenuItem { Header = "Copy All" };
        copyAllItem.Click += (_, _) =>
        {
            if (!string.IsNullOrEmpty(VM.DecompiledCode))
                Clipboard.SetText(VM.DecompiledCode);
        };
        ctx.Items.Add(copyItem);
        ctx.Items.Add(selectAllItem);
        ctx.Items.Add(new Separator());
        ctx.Items.Add(copyAllItem);
        DecompilerOutput.ContextMenu = ctx;
    }

    private void ApplyDecompilerHighlighting()
    {
        try
        {
            var dict = Application.Current.Resources.MergedDictionaries[0];
            string Col(string key, string fallback)
            {
                if (dict.Contains(key) && dict[key] is SolidColorBrush b)
                    return $"#{b.Color.R:X2}{b.Color.G:X2}{b.Color.B:X2}";
                return fallback;
            }

            var keyword = Col("ScriptKeywordBrush", "#569CD6");
            var control = Col("ScriptControlBrush", "#C586C0");
            var type    = Col("ScriptTypeBrush",    "#4EC9B0");
            var str     = Col("ScriptStringBrush",  "#CE9178");
            var comment = Col("ScriptCommentBrush", "#6A9955");
            var number  = Col("ScriptNumberBrush",  "#B5CEA8");
            var method  = Col("ScriptMethodBrush",  "#DCDCAA");
            var punct   = Col("ScriptPunctuationBrush", "#DCDCDC");
            var fg      = Col("ScriptFgBrush",      "#DCDCDC");

            var xshd = $"""
                <?xml version="1.0"?>
                <SyntaxDefinition name="C-Themed" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
                  <Color name="Comment"       foreground="{comment}" />
                  <Color name="String"        foreground="{str}" />
                  <Color name="Preprocessor"  foreground="{control}" />
                  <Color name="Punctuation"   foreground="{punct}" />
                  <Color name="NumberLiteral" foreground="{number}" />
                  <Color name="Keywords"      foreground="{keyword}" fontWeight="bold" />
                  <Color name="ControlFlow"   foreground="{control}" fontWeight="bold" />
                  <Color name="Types"         foreground="{type}" />
                  <Color name="MethodCall"    foreground="{method}" />
                  <Color name="Default"       foreground="{fg}" />

                  <RuleSet ignoreCase="false">
                    <Span color="Comment" begin="//" />
                    <Span color="Comment" multiline="true" begin="/\*" end="\*/" />
                    <Span color="String">
                      <Begin>"</Begin>
                      <End>"</End>
                      <RuleSet><Span begin="\\" end="." /></RuleSet>
                    </Span>
                    <Span color="String">
                      <Begin>'</Begin>
                      <End>'</End>
                      <RuleSet><Span begin="\\" end="." /></RuleSet>
                    </Span>
                    <Span color="Preprocessor" begin="\#" />

                    <Rule color="NumberLiteral">\b0[xX][0-9a-fA-F]+[uUlL]*\b</Rule>
                    <Rule color="NumberLiteral">\b[0-9][0-9]*\.?[0-9]*([eE][+-]?[0-9]+)?[fFdD]?\b</Rule>

                    <Keywords color="ControlFlow">
                      <Word>if</Word><Word>else</Word><Word>switch</Word><Word>case</Word>
                      <Word>for</Word><Word>while</Word><Word>do</Word>
                      <Word>break</Word><Word>continue</Word><Word>return</Word>
                      <Word>goto</Word><Word>default</Word>
                    </Keywords>
                    <Keywords color="Types">
                      <Word>void</Word><Word>int</Word><Word>unsigned</Word><Word>signed</Word>
                      <Word>char</Word><Word>short</Word><Word>long</Word><Word>float</Word><Word>double</Word>
                      <Word>bool</Word><Word>struct</Word><Word>union</Word><Word>enum</Word><Word>typedef</Word>
                      <Word>const</Word><Word>static</Word><Word>extern</Word><Word>volatile</Word>
                      <Word>inline</Word><Word>register</Word><Word>auto</Word><Word>restrict</Word>
                      <Word>size_t</Word><Word>int8_t</Word><Word>int16_t</Word><Word>int32_t</Word><Word>int64_t</Word>
                      <Word>uint8_t</Word><Word>uint16_t</Word><Word>uint32_t</Word><Word>uint64_t</Word>
                      <Word>BYTE</Word><Word>WORD</Word><Word>DWORD</Word><Word>QWORD</Word>
                      <Word>BOOL</Word><Word>HANDLE</Word><Word>PVOID</Word><Word>LPVOID</Word>
                      <Word>HMODULE</Word><Word>FARPROC</Word><Word>LPCSTR</Word><Word>LPSTR</Word>
                      <Word>LPCWSTR</Word><Word>LPWSTR</Word><Word>NTSTATUS</Word>
                      <Word>NULL</Word><Word>TRUE</Word><Word>FALSE</Word>
                    </Keywords>
                    <Keywords color="Keywords">
                      <Word>sizeof</Word><Word>typeof</Word><Word>alignof</Word>
                      <Word>__cdecl</Word><Word>__stdcall</Word><Word>__fastcall</Word><Word>__thiscall</Word>
                      <Word>__declspec</Word><Word>__attribute__</Word>
                    </Keywords>
                    <Rule color="MethodCall">[\w]+(?=\s*\()</Rule>
                    <Rule color="Punctuation">[()\[\];,.*&amp;!~+-/%&lt;&gt;=|^?:]</Rule>
                  </RuleSet>
                </SyntaxDefinition>
                """;

            using var reader = new XmlTextReader(new System.IO.StringReader(xshd));
            DecompilerOutput.SyntaxHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
        }
        catch { /* fallback: no highlighting */ }
    }

    // ── Navigation Bar (memory map) ─────────────────────────────────────

    // Cached mapping: pixel X → virtual address (for click navigation)
    private readonly List<(double X, double Width, ulong Address, string Name)> _navBarRegions = new();

    /// <summary>
    /// Rebuild the navigation bar from current modules and sections.
    /// Called after modules/sections are loaded and on break state.
    /// </summary>
    internal void RefreshNavBar()
    {
        NavBar.Children.Clear();
        _navBarRegions.Clear();

        var modules = VM.Modules.ToList();
        if (modules.Count == 0) return;

        // Only the main module (first in list = debugged executable)
        var mainMod = modules[0];
        ulong minAddr = mainMod.BaseAddress;
        ulong maxAddr = mainMod.BaseAddress + mainMod.Size;
        ulong totalSpan = maxAddr - minAddr;
        if (totalSpan == 0) return;

        var sections = VM.AllSections
            .Where(s => s.VirtualAddress >= minAddr && s.VirtualAddress < maxAddr && s.VirtualSize > 0)
            .OrderBy(s => s.VirtualAddress)
            .ToList();
        if (sections.Count == 0) return;

        double barWidth = NavBar.ActualWidth;
        if (barWidth < 10) barWidth = 1400;
        double barHeight = NavBar.Height;

        // Pack sections side-by-side, sqrt-scaled so small sections stay visible
        double totalWeight = 0;
        foreach (var s in sections) totalWeight += Math.Sqrt(s.VirtualSize);
        if (totalWeight < 0.001) return;

        double xPos = 0;
        foreach (var sec in sections)
        {
            double w = Math.Max(6, Math.Sqrt(sec.VirtualSize) / totalWeight * barWidth);

            Brush brush;
            uint ch = sec.Characteristics;
            bool isCode = (ch & 0x00000020) != 0;    // IMAGE_SCN_CNT_CODE
            bool isData = (ch & 0x00000040) != 0;    // IMAGE_SCN_CNT_INITIALIZED_DATA
            bool isUninit = (ch & 0x00000080) != 0;   // IMAGE_SCN_CNT_UNINITIALIZED_DATA
            bool isExec = (ch & 0x20000000) != 0;     // IMAGE_SCN_MEM_EXECUTE
            bool isWrite = (ch & 0x80000000) != 0;    // IMAGE_SCN_MEM_WRITE

            if (isCode || isExec)
                brush = new SolidColorBrush(Color.FromRgb(0x26, 0x6C, 0xC5)); // blue = code
            else if (isWrite && isData)
                brush = new SolidColorBrush(Color.FromRgb(0x4E, 0x9A, 0x4E)); // green = writable data
            else if (isData)
                brush = new SolidColorBrush(Color.FromRgb(0x8B, 0x8B, 0x4E)); // yellow-ish = readonly data
            else if (isUninit)
                brush = new SolidColorBrush(Color.FromRgb(0x6E, 0x4E, 0x8B)); // purple = bss
            else
                brush = new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x50)); // gray = other

            var rect = new System.Windows.Shapes.Rectangle
            {
                Width = w,
                Height = barHeight,
                Fill = brush,
                ToolTip = $"{sec.ModuleName} — {sec.Name}\n0x{sec.VirtualAddress:X} size 0x{sec.VirtualSize:X}\n{sec.Flags}"
            };
            Canvas.SetLeft(rect, xPos);
            Canvas.SetTop(rect, 0);
            NavBar.Children.Add(rect);

            _navBarRegions.Add((xPos, w, sec.VirtualAddress, $"{sec.ModuleName}:{sec.Name}"));

            // Section name label (only if wide enough to fit)
            if (w > 20)
            {
                var label = new TextBlock
                {
                    Text = sec.Name,
                    FontSize = 9,
                    FontFamily = new FontFamily("Consolas"),
                    Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
                    IsHitTestVisible = false
                };
                label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double labelW = label.DesiredSize.Width;
                if (labelW <= w - 2)
                {
                    Canvas.SetLeft(label, xPos + 3);
                    Canvas.SetTop(label, (barHeight - label.DesiredSize.Height) / 2);
                    NavBar.Children.Add(label);
                }
            }

            xPos += w;
        }

        // Helper: map a virtual address to pixel X in packed layout
        double AddrToX(ulong addr)
        {
            double px = 0;
            foreach (var s in sections)
            {
                double sw = Math.Max(6, Math.Sqrt(s.VirtualSize) / totalWeight * barWidth);
                if (addr >= s.VirtualAddress && addr < s.VirtualAddress + s.VirtualSize)
                    return px + (double)(addr - s.VirtualAddress) / s.VirtualSize * sw;
                px += sw;
            }
            return -1;
        }

        // Draw RIP indicator (triangle + line) — real RIP from registers, not disasm cursor
        var ripReg = VM.Registers.FirstOrDefault(r => r.Name == "RIP" || r.Name == "EIP");
        var rip = ripReg?.Value ?? 0;
        double ripX = AddrToX(rip);
        if (ripX >= 0)
        {
            // Vertical line
            var ripLine = new System.Windows.Shapes.Line
            {
                X1 = ripX, X2 = ripX,
                Y1 = 0, Y2 = barHeight,
                Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0x00)),
                StrokeThickness = 2
            };
            NavBar.Children.Add(ripLine);

            // Triangle marker on top
            var triangle = new System.Windows.Shapes.Polygon
            {
                Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0x00)),
                Points = new PointCollection
                {
                    new System.Windows.Point(ripX, 0),
                    new System.Windows.Point(ripX - 5, 6),
                    new System.Windows.Point(ripX + 5, 6)
                },
                ToolTip = $"RIP = 0x{rip:X16}"
            };
            NavBar.Children.Add(triangle);
        }

        // Draw breakpoint markers
        foreach (var bp in VM.Breakpoints)
        {
            double bpX = AddrToX(bp.Address);
            if (bpX < 0) continue;
            var bpLine = new System.Windows.Shapes.Line
            {
                X1 = bpX, X2 = bpX,
                Y1 = 0, Y2 = barHeight,
                Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0x30, 0x30)),
                StrokeThickness = 1.5
            };
            NavBar.Children.Add(bpLine);
        }

        // Draw bookmark markers (orange diamonds)
        foreach (var (addr, note) in VM.AddressAnnotations)
        {
            double bmX = AddrToX(addr);
            if (bmX < 0) continue;
            var diamond = new System.Windows.Shapes.Polygon
            {
                Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x47)),
                Points = new PointCollection
                {
                    new System.Windows.Point(bmX, barHeight / 2 - 4),
                    new System.Windows.Point(bmX + 4, barHeight / 2),
                    new System.Windows.Point(bmX, barHeight / 2 + 4),
                    new System.Windows.Point(bmX - 4, barHeight / 2)
                },
                ToolTip = $"0x{addr:X}\n{note}"
            };
            NavBar.Children.Add(diamond);
        }

        // Draw navigation cursor (white triangle pointing up from bottom)
        var cursor = VM.DisasmAddress;
        if (cursor != rip)
        {
            double curX = AddrToX(cursor);
            if (curX >= 0)
            {
                var curTriangle = new System.Windows.Shapes.Polygon
                {
                    Fill = Brushes.White,
                    Points = new PointCollection
                    {
                        new System.Windows.Point(curX, barHeight),
                        new System.Windows.Point(curX - 4, barHeight - 6),
                        new System.Windows.Point(curX + 4, barHeight - 6)
                    },
                    ToolTip = $"Cursor = 0x{cursor:X16}"
                };
                NavBar.Children.Add(curTriangle);
            }
        }
    }

    private void NavBar_MouseMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(NavBar);
        var sections = VM.AllSections;
        foreach (var (x, w, secAddr, name) in _navBarRegions)
        {
            if (pos.X >= x && pos.X <= x + w)
            {
                var sec = sections.FirstOrDefault(s => s.VirtualAddress == secAddr);
                if (sec != null && w > 0)
                {
                    double ratio = (pos.X - x) / w;
                    ulong addr = secAddr + (ulong)(ratio * sec.VirtualSize);
                    NavBar.ToolTip = $"{sec.ModuleName}:{sec.Name}\n0x{addr:X16}\nSection: 0x{sec.VirtualAddress:X} — 0x{sec.VirtualAddress + sec.VirtualSize:X}\n{sec.Flags}";
                }
                else
                    NavBar.ToolTip = name;
                return;
            }
        }
        NavBar.ToolTip = null;
    }

    private void NavBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(NavBar);
        var sections = VM.AllSections;
        foreach (var (x, w, secAddr, name) in _navBarRegions)
        {
            if (pos.X >= x && pos.X <= x + w)
            {
                // Find section to get its size
                var sec = sections.FirstOrDefault(s => s.VirtualAddress == secAddr);
                if (sec != null && w > 0)
                {
                    double ratio = (pos.X - x) / w;
                    ulong targetAddr = secAddr + (ulong)(ratio * sec.VirtualSize);
                    VM.NavigateDisasmTo(targetAddr);
                }
                else
                {
                    VM.NavigateDisasmTo(secAddr);
                }
                return;
            }
        }
    }

    private void UpdateDecompilerText()
    {
        DecompilerOutput.Text = VM.DecompiledCode ?? "";
    }

    private void ApplyPersistedLayout()
    {
        var scr = System.Windows.SystemParameters.VirtualScreenWidth;
        var scrH = System.Windows.SystemParameters.VirtualScreenHeight;
        if (VM.UiWindowWidth is { } w && w >= 400 && w <= scr + 200) Width = w;
        if (VM.UiWindowHeight is { } h && h >= 300 && h <= scrH + 200) Height = h;
        if (VM.UiWindowLeft is { } l && VM.UiWindowTop is { } t &&
            l > -2000 && t > -2000 && l < scr && t < scrH)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = l;
            Top = t;
        }
        if (VM.UiZoomDisasm is { } zd) SetPanelScale("Disasm", zd);
        if (VM.UiZoomRegisters is { } zr) SetPanelScale("Registers", zr);
        if (VM.UiZoomHex is { } zh) SetPanelScale("Hex", zh);
        if (VM.UiZoomStack is { } zs) SetPanelScale("Stack", zs);
        ApplyDockLayout();
        Loaded += (_, _) =>
        {
            if (VM.UiWindowState == "Maximized") WindowState = WindowState.Maximized;
        };
    }

    private void OnCtrlMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control) return;
        var panel = FindPanelUnderCursor();
        if (panel == null) return;
        SetPanelScale(panel, GetPanelZoom(panel) + (e.Delta > 0 ? 0.1 : -0.1));
        e.Handled = true;
    }

    private string? FindPanelUnderCursor()
    {
        // Check each panel's bounding rect in window coordinates — avoids
        // InputHitTest/VisualTree traversal issues caused by overlay canvases
        // that sit on top of the real content.
        if (IsMouseOver(DisasmControl))   return "Disasm";
        if (IsMouseOver(HexDumpControl))  return "Hex";
        if (IsMouseOver(StackList))       return "Stack";
        if (IsMouseOver(RegistersGrid))   return "Registers";
        if (IsMouseOver(FlagsGrid))       return "Registers";
        if (IsMouseOver(RegistersPanel))  return "Registers";
        return null;
    }

    private bool IsMouseOver(FrameworkElement el)
    {
        if (el == null || !el.IsVisible) return false;
        var pos = Mouse.GetPosition(el);
        return pos.X >= 0 && pos.Y >= 0 && pos.X <= el.ActualWidth && pos.Y <= el.ActualHeight;
    }

    private const double BaseFont = 11.0;

    private double GetPanelZoom(string name)
    {
        double fs = name switch
        {
            "Disasm" => DisasmControl.LineFontSize,
            "Hex" => HexDumpControl.LineFontSize,
            "Registers" => (double)RegistersPanel.GetValue(System.Windows.Documents.TextElement.FontSizeProperty),
            "Stack" => Resources["StackFontSize"] is double s ? s : 11.0,
            _ => BaseFont,
        };
        double baseFs = name == "Stack" ? 11.0 : BaseFont;
        return fs / baseFs;
    }

    private void ApplyDockLayout()
    {
        if (VM.UiTopRowRatio is { } v && v > 0) TopRow.Height = new GridLength(v, GridUnitType.Star);
        if (VM.UiBotRowRatio is { } v2 && v2 > 0) BottomRow.Height = new GridLength(v2, GridUnitType.Star);
        if (VM.UiTopLeftRatio is { } v3 && v3 > 0) TopLeftCol.Width = new GridLength(v3, GridUnitType.Star);
        if (VM.UiTopRightRatio is { } v4 && v4 > 0) TopRightCol.Width = new GridLength(v4, GridUnitType.Star);
        if (VM.UiBotLeftRatio is { } v5 && v5 > 0) BotLeftCol.Width = new GridLength(v5, GridUnitType.Star);
        if (VM.UiBotRightRatio is { } v6 && v6 > 0) BotRightCol.Width = new GridLength(v6, GridUnitType.Star);

        // Panel column widths
        if (VM.UiColDisasmBp is { } cdb && cdb > 0) DragColumn(0, cdb - DisasmControl.BpColWidth);
        if (VM.UiColDisasmAddr is { } cda && cda > 0) DragColumn(1, cda - DisasmControl.AddrColWidth);
        if (VM.UiColDisasmBytes is { } cdy && cdy > 0) DragColumn(2, cdy - DisasmControl.BytesColWidth);
        if (VM.UiColHexAddr is { } cha && cha > 0) { HexDumpControl.AddressColWidth = cha; }
        if (VM.UiColHexHex is { } chh && chh > 0) { HexDumpControl.HexColWidth = chh; }
        if (VM.UiColStackOffset is { } cso && cso > 0)
        {
            _stackCols.OffsetW = new GridLength(cso);
            StackOffsetCol.Width = new GridLength(cso);
        }
        if (VM.UiColStackAddr is { } csa && csa > 0)
        {
            _stackCols.AddrW = new GridLength(csa);
            StackAddrCol.Width = new GridLength(csa);
        }
        if (VM.UiColRegName is { } crn && crn > 0)
        {
            RegistersGrid.Columns[0].Width = new System.Windows.Controls.DataGridLength(crn);
            RegNameCol.Width = new GridLength(crn);
        }
        if (VM.UiColRegVal is { } crv && crv > 0)
        {
            RegistersGrid.Columns[1].Width = new System.Windows.Controls.DataGridLength(crv);
            RegValCol.Width = new GridLength(crv);
        }
    }

    // helper to access DisasmView internal DragColumn (reflection-free — make method public in DisasmView)
    private void DragColumn(int idx, double delta) => DisasmControl.DragColumnPublic(idx, delta);

    private void SetPanelScale(string name, double s)
    {
        s = Math.Round(Math.Clamp(s, 0.5, 6.0), 2);
        switch (name)
        {
            case "Disasm":
                DisasmControl.LineFontSize = BaseFont * s;
                break;
            case "Hex":
                HexDumpControl.LineFontSize = BaseFont * s;
                break;
            case "Registers":
                System.Windows.Documents.TextElement.SetFontSize(RegistersPanel, BaseFont * s);
                break;
            case "Stack":
                Resources["StackFontSize"] = 11.0 * s;
                break;
        }
    }

    private void SetupFlagsGrid()
    {
        var all = System.Windows.Data.CollectionViewSource.GetDefaultView(VM.Registers);
        // Main grid: hide flags
        all.Filter = o => o is Models.Register r && !r.IsFlag;
        // Flags-only view for ItemsControl
        var flagsView = new System.Windows.Data.CollectionViewSource { Source = VM.Registers };
        flagsView.View.Filter = o => o is Models.Register r && r.IsFlag;
        FlagsGrid.ItemsSource = flagsView.View;
        VM.Registers.CollectionChanged += (_, _) =>
        {
            all.Refresh();
            flagsView.View.Refresh();
        };
    }

    public class ColumnWidths : System.ComponentModel.INotifyPropertyChanged
    {
        private GridLength _offsetW = new(60);
        private GridLength _addrW = new(160);
        public GridLength OffsetW { get => _offsetW; set { _offsetW = value; PropertyChanged?.Invoke(this, new(nameof(OffsetW))); } }
        public GridLength AddrW { get => _addrW; set { _addrW = value; PropertyChanged?.Invoke(this, new(nameof(AddrW))); } }
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }
    private readonly ColumnWidths _stackCols = new();

    private void OnStackSplitterDrag0(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        double w = Math.Max(20, _stackCols.OffsetW.Value + e.HorizontalChange);
        _stackCols.OffsetW = new GridLength(w);
        StackOffsetCol.Width = new GridLength(w);
    }

    private void OnStackSplitterDrag1(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        double w = Math.Max(20, _stackCols.AddrW.Value + e.HorizontalChange);
        _stackCols.AddrW = new GridLength(w);
        StackAddrCol.Width = new GridLength(w);
    }

    private void OnRegSplitterDrag0(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        double w = Math.Max(30, RegistersGrid.Columns[0].ActualWidth + e.HorizontalChange);
        RegistersGrid.Columns[0].Width = new System.Windows.Controls.DataGridLength(w);
        RegNameCol.Width = new GridLength(w);
    }

    private void OnRegSplitterDrag1(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        double w = Math.Max(30, RegistersGrid.Columns[1].ActualWidth + e.HorizontalChange);
        RegistersGrid.Columns[1].Width = new System.Windows.Controls.DataGridLength(w);
        RegValCol.Width = new GridLength(w);
    }

    private async void OnConsoleKeyDown(object sender, KeyEventArgs e)
    {
        if (_console == null) return;

        bool popupOpen = ConsolePopup.IsOpen;

        if (e.Key == Key.Enter)
        {
            ConsolePopup.IsOpen = false;
            var line = ConsoleInput.Text;
            if (!string.IsNullOrWhiteSpace(line))
            {
                _consoleHistory.Add(line);
                _consoleHistoryIdx = _consoleHistory.Count;
                var result = await _console.ExecuteAsync(line);
                ConsoleOutput.Text = result;
                ConsoleInput.Clear();
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Tab)
        {
            // Accept highlighted suggestion (or first one)
            if (popupOpen && ConsoleSuggestions.Items.Count > 0)
            {
                if (ConsoleSuggestions.SelectedItem is not ConsoleCmd sel)
                    sel = (ConsoleCmd)ConsoleSuggestions.Items[0]!;
                AcceptSuggestion(sel);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Up)
        {
            if (popupOpen && ConsoleSuggestions.Items.Count > 0)
            {
                int i = ConsoleSuggestions.SelectedIndex;
                ConsoleSuggestions.SelectedIndex = i <= 0 ? ConsoleSuggestions.Items.Count - 1 : i - 1;
                ConsoleSuggestions.ScrollIntoView(ConsoleSuggestions.SelectedItem);
                e.Handled = true;
                return;
            }
            if (_consoleHistory.Count > 0 && _consoleHistoryIdx > 0)
            {
                _consoleHistoryIdx--;
                ConsoleInput.Text = _consoleHistory[_consoleHistoryIdx];
                ConsoleInput.CaretIndex = ConsoleInput.Text.Length;
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Down)
        {
            if (popupOpen && ConsoleSuggestions.Items.Count > 0)
            {
                int i = ConsoleSuggestions.SelectedIndex;
                ConsoleSuggestions.SelectedIndex = i >= ConsoleSuggestions.Items.Count - 1 ? 0 : i + 1;
                ConsoleSuggestions.ScrollIntoView(ConsoleSuggestions.SelectedItem);
                e.Handled = true;
                return;
            }
            if (_consoleHistoryIdx < _consoleHistory.Count - 1)
            {
                _consoleHistoryIdx++;
                ConsoleInput.Text = _consoleHistory[_consoleHistoryIdx];
                ConsoleInput.CaretIndex = ConsoleInput.Text.Length;
            }
            else
            {
                _consoleHistoryIdx = _consoleHistory.Count;
                ConsoleInput.Clear();
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (popupOpen) { ConsolePopup.IsOpen = false; e.Handled = true; return; }
            ConsoleInput.Clear();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private void OnConsoleTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var text = ConsoleInput.Text;
        ConsoleHint.Visibility = string.IsNullOrEmpty(text) ? Visibility.Visible : Visibility.Collapsed;

        // Only suggest command name — first word before space
        int spIdx = text.IndexOf(' ');
        string typed = spIdx < 0 ? text.TrimStart() : "";
        if (string.IsNullOrWhiteSpace(typed))
        {
            // Empty input → show all
            ConsoleSuggestions.ItemsSource = _allCmds;
            ConsolePopup.IsOpen = ConsoleInput.IsFocused;
            ConsoleSuggestions.SelectedIndex = -1;
            return;
        }
        if (spIdx >= 0) { ConsolePopup.IsOpen = false; return; }
        var matches = _allCmds.Where(c => c.Name.StartsWith(typed, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0) { ConsolePopup.IsOpen = false; return; }
        ConsoleSuggestions.ItemsSource = matches;
        ConsoleSuggestions.SelectedIndex = 0;
        ConsolePopup.IsOpen = true;
    }

    private void OnConsoleLostFocus(object sender, RoutedEventArgs e)
    {
        // Close popup unless focus moved into the popup itself
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!ConsoleInput.IsKeyboardFocusWithin && !ConsoleSuggestions.IsKeyboardFocusWithin)
                ConsolePopup.IsOpen = false;
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnSuggestionPicked(object sender, MouseButtonEventArgs e)
    {
        if (ConsoleSuggestions.SelectedItem is ConsoleCmd cmd)
            AcceptSuggestion(cmd);
    }

    private void AcceptSuggestion(ConsoleCmd cmd)
    {
        // If the command takes an argument, add a trailing space; otherwise plain
        bool wantsArg = cmd.Hint.Contains("<");
        ConsoleInput.Text = wantsArg ? cmd.Name + " " : cmd.Name;
        ConsoleInput.CaretIndex = ConsoleInput.Text.Length;
        ConsolePopup.IsOpen = false;
        ConsoleInput.Focus();
    }

    private void PopulateThemesMenu()
    {
        ThemesMenu.Items.Clear();
        foreach (var name in ViewModels.MainViewModel.ListThemePresets())
        {
            var item = new MenuItem { Header = name };
            string captured = name;
            item.Click += (_, _) =>
            {
                VM.ApplyThemePreset(captured);
                ApplyThemeColors(VM.ThemeColors);
                RefreshDisasmView();
            };
            ThemesMenu.Items.Add(item);
        }
    }

    private void HookColumnOverlayScroll()
    {
        HookInnerScroll(StackList, sv => sv.ScrollChanged += (_, e) => StackColumnXform.X = -e.HorizontalOffset);
        HookInnerScroll(RegistersGrid, sv => sv.ScrollChanged += (_, e) => RegColumnXform.X = -e.HorizontalOffset);
    }

    private static void HookInnerScroll(DependencyObject root, Action<ScrollViewer> hook)
    {
        var sv = FindDescendant<ScrollViewer>(root);
        if (sv != null) hook(sv);
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T t) return t;
            var rec = FindDescendant<T>(child);
            if (rec != null) return rec;
        }
        return null;
    }

    private void OnFlagClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is Models.Register reg)
            VM.ToggleFlag(reg);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // ":" focuses the command console (like vim). Skip if user is already typing in a TextBox.
        if (e.Key == Key.OemSemicolon && (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift
            && Keyboard.FocusedElement is not TextBox)
        {
            ConsoleInput.Focus();
            e.Handled = true;
            return;
        }
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            double? delta = null;
            bool reset = false;
            if (e.Key == Key.OemPlus || e.Key == Key.Add) delta = 0.1;
            else if (e.Key == Key.OemMinus || e.Key == Key.Subtract) delta = -0.1;
            else if (e.Key == Key.D0 || e.Key == Key.NumPad0) reset = true;

            if (delta.HasValue || reset)
            {
                var panel = FindPanelUnderCursor();
                if (panel != null)
                {
                    if (reset) SetPanelScale(panel, 1.0);
                    else SetPanelScale(panel, GetPanelZoom(panel) + delta!.Value);
                    e.Handled = true;
                    return;
                }
            }
        }
        if (e.Key == Key.F2 || e.SystemKey == Key.F2)
        {
            ulong addr = GetSelectedAddressFromActiveTab();
            if (addr != 0)
            {
                VM.SetBreakpointAtAddress(addr);
                e.Handled = true;
                return;
            }
            // Disassembly tab: use the standard command
            VM.ToggleBreakpointCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.F11)
        {
            ToggleFullscreen();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && VM.CanDisasmGoBack)
        {
            VM.DisasmGoBackCommand.Execute(null);
            e.Handled = true;
        }
    }

    private WindowState _preFullscreenState;
    private WindowStyle _preFullscreenStyle;
    private bool _isFullscreen;

    private void ToggleFullscreen()
    {
        if (_isFullscreen)
        {
            WindowStyle = _preFullscreenStyle;
            WindowState = _preFullscreenState;
            _isFullscreen = false;
        }
        else
        {
            _preFullscreenState = WindowState;
            _preFullscreenStyle = WindowStyle;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            _isFullscreen = true;
        }
    }

    private void OnToggleFullscreen(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void RefreshDisasmView()
    {
        var rip = VM.Registers.FirstOrDefault(r => r.Name == VM.IpRegName)?.Value;
        DisasmControl.SetInstructions(VM.Instructions, rip);
        RefreshNavBar();
    }

    /* ================================================================== */
    /*  Menu / Toolbar handlers                                            */
    /* ================================================================== */

    private void OnExitClick(object sender, RoutedEventArgs e) => Close();
    private void OnAboutClick(object sender, RoutedEventArgs e) =>
        MessageBox.Show("KernelFlirt - Kernel Debugger", "About", MessageBoxButton.OK);

    private void OnRefreshAllClick(object sender, RoutedEventArgs e)
    {
        VM.RefreshModules();
        VM.RefreshKernelModules();
        VM.RefreshThreads();
        VM.RefreshRegisters();
        VM.RefreshDisassembly();
        VM.RefreshStack();
        VM.RefreshCallStack();
        VM.RefreshHexDump();
        VM.RefreshImports();
        UpdateHexDumpDisplay();
    }

    private void OnRefreshSehClick(object sender, RoutedEventArgs e)
    {
        VM.RefreshSehChain();
    }

    private void OnGoToClick(object sender, RoutedEventArgs e)
    {
        VM.GoToAddressCommand.Execute(GoToBox.Text);
    }

    private void OnGoToKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            VM.GoToAddressCommand.Execute(GoToBox.Text);
            e.Handled = true;
        }
    }

    private void OnHexGoClick(object sender, RoutedEventArgs e)
    {
        VM.GoToHexAddressCommand.Execute(HexAddrBox.Text);
        UpdateHexDumpDisplay();
    }

    private void OnHexAddrKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            VM.GoToHexAddressCommand.Execute(HexAddrBox.Text);
            UpdateHexDumpDisplay();
            e.Handled = true;
        }
    }

    /* ================================================================== */
    /*  Module context menu                                                */
    /* ================================================================== */

    private void OnModuleDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGrid grid && grid.SelectedItem is ModuleInfo module)
        {
            VM.NavigateDisasmTo(VM.ResolveEntryPoint(module.BaseAddress));
            RefreshDisasmView();
        }
    }

    private void OnModuleFollowInDisasm(object sender, RoutedEventArgs e)
    {
        if (ModulesGrid.SelectedItem is ModuleInfo mod)
            VM.FollowInDisasmCommand.Execute(mod.BaseAddress);
    }

    private void OnModuleFollowInDump(object sender, RoutedEventArgs e)
    {
        if (ModulesGrid.SelectedItem is ModuleInfo mod)
        {
            VM.FollowInDumpCommand.Execute(mod.BaseAddress);
            UpdateHexDumpDisplay();
        }
    }

    private void OnModuleCopyBase(object sender, RoutedEventArgs e)
    {
        if (ModulesGrid.SelectedItem is ModuleInfo mod)
            Clipboard.SetText($"{mod.BaseAddress:X16}");
    }

    private void OnModuleShowImports(object sender, RoutedEventArgs e)
    {
        if (ModulesGrid.SelectedItem is ModuleInfo mod)
            VM.RefreshImports(mod.BaseAddress);
    }

    private void OnModuleShowExports(object sender, RoutedEventArgs e)
    {
        if (ModulesGrid.SelectedItem is ModuleInfo mod)
            VM.RefreshExports(mod.BaseAddress);
    }

    private void OnModuleShowFunctions(object sender, RoutedEventArgs e)
    {
        if (ModulesGrid.SelectedItem is ModuleInfo mod)
            VM.RefreshFunctionsForModule(mod.BaseAddress, mod.Name);
    }

    /* ================================================================== */
    /*  Kernel module context menu                                         */
    /* ================================================================== */

    private void OnKernelModuleDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGrid grid && grid.SelectedItem is KernelModuleInfo mod)
            NavigateToKernelModule(mod);
    }

    private void OnKernelModuleGoToEntry(object sender, RoutedEventArgs e)
    {
        if (KernelModulesGrid.SelectedItem is KernelModuleInfo mod)
            NavigateToKernelModule(mod);
    }

    private void OnKernelModuleGoToBase(object sender, RoutedEventArgs e)
    {
        if (KernelModulesGrid.SelectedItem is KernelModuleInfo mod)
        {
            VM.PushDisasmHistory();
            VM.DisasmAddress = mod.BaseAddress;
            VM.TargetPid = 4;
            VM.RefreshDisassembly();
            RefreshDisasmView();
        }
    }

    private void OnKernelModuleFollowInDump(object sender, RoutedEventArgs e)
    {
        if (KernelModulesGrid.SelectedItem is KernelModuleInfo mod)
        {
            VM.HexAddress = mod.BaseAddress;
            VM.TargetPid = 4;
            VM.RefreshHexDump();
        }
    }

    private void OnKernelModuleCopyBase(object sender, RoutedEventArgs e)
    {
        if (KernelModulesGrid.SelectedItem is KernelModuleInfo mod)
            Clipboard.SetText($"{mod.BaseAddress:X16}");
    }

    private void OnKernelModuleCopyName(object sender, RoutedEventArgs e)
    {
        if (KernelModulesGrid.SelectedItem is KernelModuleInfo mod)
            Clipboard.SetText(mod.Name);
    }

    private void OnKernelModuleShowImports(object sender, RoutedEventArgs e)
    {
        if (KernelModulesGrid.SelectedItem is KernelModuleInfo mod)
            VM.RefreshImports(mod.BaseAddress, 4);
    }

    private void OnKernelModuleShowFunctions(object sender, RoutedEventArgs e)
    {
        if (KernelModulesGrid.SelectedItem is KernelModuleInfo mod)
            VM.RefreshFunctionsForModule(mod.BaseAddress, mod.Name);
    }

    private void NavigateToKernelModule(KernelModuleInfo mod)
    {
        VM.PushDisasmHistory();
        var ep = VM.ResolveKernelEntryPoint(mod.BaseAddress);
        VM.DisasmAddress = ep;
        VM.TargetPid = 4;
        VM.RefreshDisassembly();
        RefreshDisasmView();
        VM.Log($"Kernel module: {mod.Name} entry at {ep:X16}");
    }

    /* ================================================================== */
    /*  Thread context menu                                                */
    /* ================================================================== */

    private void OnThreadDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGrid grid && grid.SelectedItem is ThreadInfo thread)
            VM.SwitchThreadCommand.Execute(thread.ThreadId);
    }

    private void OnThreadSwitch(object sender, RoutedEventArgs e)
    {
        if (ThreadsGrid.SelectedItem is ThreadInfo t)
            VM.SwitchThreadCommand.Execute(t.ThreadId);
    }

    private void OnThreadSuspend(object sender, RoutedEventArgs e)
    {
        if (ThreadsGrid.SelectedItem is ThreadInfo t)
            VM.SuspendThreadCommand.Execute(t.ThreadId);
    }

    private void OnThreadResume(object sender, RoutedEventArgs e)
    {
        if (ThreadsGrid.SelectedItem is ThreadInfo t)
            VM.ResumeThreadCommand.Execute(t.ThreadId);
    }

    private void OnThreadFollowStart(object sender, RoutedEventArgs e)
    {
        if (ThreadsGrid.SelectedItem is ThreadInfo t)
            VM.FollowInDisasmCommand.Execute(t.StartAddress);
    }

    /* ================================================================== */
    /*  Register context menu                                              */
    /* ================================================================== */

    private void OnRegisterFollowInDump(object sender, RoutedEventArgs e)
    {
        if (RegistersGrid.SelectedItem is Register reg)
        {
            VM.FollowInDumpCommand.Execute(reg.Value);
            UpdateHexDumpDisplay();
        }
    }

    private void OnRegisterFollowInDisasm(object sender, RoutedEventArgs e)
    {
        if (RegistersGrid.SelectedItem is Register reg)
            VM.FollowInDisasmCommand.Execute(reg.Value);
    }

    private void OnRegisterDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (RegistersGrid.SelectedItem is Register reg)
            VM.EditRegister(reg);
    }

    private void OnRegisterModify(object sender, RoutedEventArgs e)
    {
        if (RegistersGrid.SelectedItem is Register reg)
            VM.EditRegister(reg);
    }

    private void OnRegisterToggleFlag(object sender, RoutedEventArgs e)
    {
        if (RegistersGrid.SelectedItem is Register reg)
            VM.ToggleFlag(reg);
    }

    private void OnRegisterZero(object sender, RoutedEventArgs e)
    {
        if (RegistersGrid.SelectedItem is Register reg)
            VM.ZeroRegister(reg);
    }

    private void OnRegisterIncrement(object sender, RoutedEventArgs e)
    {
        if (RegistersGrid.SelectedItem is Register reg)
            VM.IncrementRegister(reg);
    }

    private void OnRegisterDecrement(object sender, RoutedEventArgs e)
    {
        if (RegistersGrid.SelectedItem is Register reg)
            VM.DecrementRegister(reg);
    }

    /* ================================================================== */
    /*  Stack context menu                                                 */
    /* ================================================================== */

    private void OnStackFollowInDump(object sender, RoutedEventArgs e)
    {
        if (StackList.SelectedItem is Models.StackEntry entry)
        {
            ulong addr = ParseStackAddress(entry.Address);
            if (addr != 0)
            {
                VM.FollowInDumpCommand.Execute(addr);
                UpdateHexDumpDisplay();
            }
        }
    }

    private void OnStackFollowInDisasm(object sender, RoutedEventArgs e)
    {
        if (StackList.SelectedItem is Models.StackEntry entry)
        {
            ulong addr = ParseStackAddress(entry.Address);
            if (addr != 0)
                VM.FollowInDisasmCommand.Execute(addr);
        }
    }

    private void OnStackCopy(object sender, RoutedEventArgs e)
    {
        if (StackList.SelectedItem is Models.StackEntry entry)
            Clipboard.SetText(entry.ToString());
    }

    private static ulong ParseStackAddress(string hexAddr)
    {
        if (ulong.TryParse(hexAddr.Trim(), System.Globalization.NumberStyles.HexNumber, null, out var val))
            return val;
        return 0;
    }

    /* ================================================================== */
    /*  Breakpoint context menu                                            */
    /* ================================================================== */

    private void OnBreakpointDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (BpGrid.SelectedItem is Breakpoint bp)
        {
            VM.DisasmAddress = bp.Address;
            VM.RefreshDisassembly();
            RefreshDisasmView();
        }
    }

    private void OnBreakpointGoTo(object sender, RoutedEventArgs e)
    {
        if (BpGrid.SelectedItem is Breakpoint bp)
        {
            VM.NavigateDisasmTo(bp.Address);
            RefreshDisasmView();
        }
    }

    private void OnBreakpointFollowInDump(object sender, RoutedEventArgs e)
    {
        if (BpGrid.SelectedItem is Breakpoint bp)
        {
            VM.FollowInDumpCommand.Execute(bp.Address);
            UpdateHexDumpDisplay();
        }
    }

    private void OnBreakpointRemove(object sender, RoutedEventArgs e)
    {
        if (BpGrid.SelectedItem is Breakpoint bp)
        {
            VM.SelectedDisasmAddress = bp.Address;
            VM.ToggleBreakpointCommand.Execute(null);
        }
    }

    /* ================================================================== */
    /*  Call Stack context menu                                             */
    /* ================================================================== */

    private void OnCallStackDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (CallStackGrid.SelectedItem is CallStackFrame frame)
            VM.FollowInDisasmCommand.Execute(frame.ReturnAddress);
    }

    private void OnCallStackFollowDisasm(object sender, RoutedEventArgs e)
    {
        if (CallStackGrid.SelectedItem is CallStackFrame frame)
            VM.FollowInDisasmCommand.Execute(frame.ReturnAddress);
    }

    private void OnCallStackFollowDump(object sender, RoutedEventArgs e)
    {
        if (CallStackGrid.SelectedItem is CallStackFrame frame)
        {
            VM.FollowInDumpCommand.Execute(frame.StackAddress);
            UpdateHexDumpDisplay();
        }
    }

    private void OnCallStackCopy(object sender, RoutedEventArgs e)
    {
        if (CallStackGrid.SelectedItem is CallStackFrame frame)
            Clipboard.SetText($"{frame.ReturnAddressHex} {frame.Symbol}");
    }

    /* ================================================================== */
    /*  Bookmark context menu                                              */
    /* ================================================================== */

    // Old bookmark handlers removed — replaced by Bookmarks/Notes plugin

    /* ================================================================== */
    /*  Patches context menu                                               */
    /* ================================================================== */

    private void OnPatchRestore(object sender, RoutedEventArgs e)
    {
        if (PatchesGrid.SelectedItem is Patch p)
            VM.RestorePatchCommand.Execute(p);
    }

    private void OnPatchGoTo(object sender, RoutedEventArgs e)
    {
        if (PatchesGrid.SelectedItem is Patch p)
            VM.FollowInDisasmCommand.Execute(p.Address);
    }

    /* ================================================================== */
    /*  Search context menu                                                */
    /* ================================================================== */

    private void OnSearchResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SearchGrid.SelectedItem is SearchResult sr)
            VM.FollowInDisasmCommand.Execute(sr.Address);
    }

    private void OnSearchFollowDisasm(object sender, RoutedEventArgs e)
    {
        if (SearchGrid.SelectedItem is SearchResult sr)
            VM.FollowInDisasmCommand.Execute(sr.Address);
    }

    private void OnSearchFollowDump(object sender, RoutedEventArgs e)
    {
        if (SearchGrid.SelectedItem is SearchResult sr)
        {
            VM.FollowInDumpCommand.Execute(sr.Address);
            UpdateHexDumpDisplay();
        }
    }

    private void OnSearchSetBp(object sender, RoutedEventArgs e)
    {
        if (SearchGrid.SelectedItem is SearchResult sr)
        {
            VM.SelectedDisasmAddress = sr.Address;
            VM.ToggleBreakpointCommand.Execute(null);
        }
    }

    /* ================================================================== */
    /*  Hex dump — now handled by HexDumpView control                       */
    /* ================================================================== */

    /* ================================================================== */
    /*  Shared: right-click selects DataGrid row under cursor              */
    /* ================================================================== */

    private void DataGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid dg) return;
        var dep = (DependencyObject)e.OriginalSource;
        while (dep != null && dep is not DataGridRow)
            dep = System.Windows.Media.VisualTreeHelper.GetParent(dep);
        if (dep is DataGridRow row)
            dg.SelectedItem = row.Item;
    }

    /* ================================================================== */
    /*  Imports context menu                                               */
    /* ================================================================== */

    private void OnImportDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ImportsGrid.SelectedItem is ImportEntry imp)
        {
            VM.FollowInDisasmCommand.Execute(imp.ResolvedAddress);
            MainTabControl.SelectedIndex = 0;
        }
    }

    private void OnImportFollowDisasm(object sender, RoutedEventArgs e)
    {
        if (ImportsGrid.SelectedItem is ImportEntry imp)
        {
            VM.FollowInDisasmCommand.Execute(imp.ResolvedAddress);
            MainTabControl.SelectedIndex = 0;
        }
    }

    private void OnImportFollowDump(object sender, RoutedEventArgs e)
    {
        if (ImportsGrid.SelectedItem is ImportEntry imp)
        {
            VM.FollowInDumpCommand.Execute(imp.IatAddress);
            UpdateHexDumpDisplay();
        }
    }

    private void OnBpButtonClick(object sender, RoutedEventArgs e)
    {
        ulong addr = GetSelectedAddressFromActiveTab();
        if (addr != 0)
            VM.SetBreakpointAtAddress(addr);
        else
            VM.ToggleBreakpointCommand.Execute(null);
    }

    private ulong GetSelectedAddressFromActiveTab()
    {
        var tab = MainTabControl.SelectedItem as TabItem;
        if (tab == null) return 0;
        var header = tab.Tag as string ?? tab.Header?.ToString();
        return header switch
        {
            "Imports" => (ImportsGrid.SelectedItem as ImportEntry)?.ResolvedAddress ?? 0,
            "Exports" => (ExportsGrid.SelectedItem as ExportEntry)?.Address ?? 0,
            "Functions" => (FunctionsGrid.SelectedItem as FunctionEntry)?.Address ?? 0,
            "Search" => (SearchGrid.SelectedItem as SearchResult)?.Address ?? 0,
            "Exceptions" => (ExceptionsGrid.SelectedItem as ExceptionEntry)?.FunctionStart ?? 0,
            _ => 0
        };
    }

    private void OnImportSetBp(object sender, RoutedEventArgs e)
    {
        if (ImportsGrid.SelectedItem is ImportEntry imp)
            VM.SetBreakpointAtAddress(imp.ResolvedAddress);
    }

    private void OnImportCopy(object sender, RoutedEventArgs e)
    {
        if (ImportsGrid.SelectedItem is ImportEntry imp)
            Clipboard.SetText($"{imp.Module}!{imp.Display} IAT={imp.IatHex} -> {imp.ResolvedHex}");
    }

    /* ================================================================== */
    /*  Exports tab                                                        */
    /* ================================================================== */

    private void OnExportDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ExportsGrid.SelectedItem is ExportEntry exp)
        {
            VM.FollowInDisasmCommand.Execute(exp.Address);
            MainTabControl.SelectedIndex = 0;
        }
    }

    private void OnExportFollowDisasm(object sender, RoutedEventArgs e)
    {
        if (ExportsGrid.SelectedItem is ExportEntry exp)
        {
            VM.FollowInDisasmCommand.Execute(exp.Address);
            MainTabControl.SelectedIndex = 0;
        }
    }

    private void OnExportFollowDump(object sender, RoutedEventArgs e)
    {
        if (ExportsGrid.SelectedItem is ExportEntry exp)
        {
            VM.FollowInDumpCommand.Execute(exp.Address);
            UpdateHexDumpDisplay();
        }
    }

    private void OnExportDecompile(object sender, RoutedEventArgs e)
    {
        if (ExportsGrid.SelectedItem is ExportEntry exp)
            DecompileAddress(exp.Address);
    }

    private void OnExportSetBp(object sender, RoutedEventArgs e)
    {
        if (ExportsGrid.SelectedItem is ExportEntry exp)
            VM.SetBreakpointAtAddress(exp.Address);
    }

    private void OnExportCopy(object sender, RoutedEventArgs e)
    {
        if (ExportsGrid.SelectedItem is ExportEntry exp)
            Clipboard.SetText($"{exp.Module}!{exp.Display} {exp.AddressHex}");
    }

    /* ================================================================== */
    /*  Functions tab                                                       */
    /* ================================================================== */

    private void OnFunctionDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FunctionsGrid.SelectedItem is FunctionEntry fn)
        {
            VM.FollowInDisasmCommand.Execute(fn.Address);
            MainTabControl.SelectedIndex = 0;
        }
    }

    private void OnFunctionFollowDisasm(object sender, RoutedEventArgs e)
    {
        if (FunctionsGrid.SelectedItem is FunctionEntry fn)
        {
            VM.FollowInDisasmCommand.Execute(fn.Address);
            MainTabControl.SelectedIndex = 0;
        }
    }

    private void OnFunctionSetBp(object sender, RoutedEventArgs e)
    {
        if (FunctionsGrid.SelectedItem is FunctionEntry fn)
            VM.SetBreakpointAtAddress(fn.Address);
    }

    private void OnFunctionCopy(object sender, RoutedEventArgs e)
    {
        if (FunctionsGrid.SelectedItem is FunctionEntry fn)
            Clipboard.SetText($"{fn.Name} {fn.AddressHex}");
    }

    private void OnFunctionDecompile(object sender, RoutedEventArgs e)
    {
        if (FunctionsGrid.SelectedItem is FunctionEntry fn)
            DecompileAddress(fn.Address, fn.Size);
    }

    private void OnImportDecompile(object sender, RoutedEventArgs e)
    {
        if (ImportsGrid.SelectedItem is ImportEntry imp)
            DecompileAddress(imp.ResolvedAddress);
    }

    private void OnExceptionDecompile(object sender, RoutedEventArgs e)
    {
        if (ExceptionsGrid.SelectedItem is ExceptionEntry ex)
            DecompileAddress(ex.FunctionStart, (uint)(ex.FunctionEnd - ex.FunctionStart));
    }

    private void OnCallStackDecompile(object sender, RoutedEventArgs e)
    {
        if (CallStackGrid.SelectedItem is CallStackFrame f)
            DecompileAddress(f.ReturnAddress);
    }

    private void OnSearchDecompile(object sender, RoutedEventArgs e)
    {
        if (SearchGrid.SelectedItem is SearchResult sr)
            DecompileAddress(sr.Address);
    }

    private void OnBreakpointDecompile(object sender, RoutedEventArgs e)
    {
        if (BpGrid.SelectedItem is Breakpoint bp)
            DecompileAddress(bp.Address);
    }

    private void DecompileAddress(ulong address, uint size = 0)
    {
        if (address == 0) return;
        VM.DecompileFunction(address, size);
        MainTabControl.SelectedItem = DecompilerTab;
    }

    /* ================================================================== */
    /*  Exceptions tab                                                     */
    /* ================================================================== */

    private void OnExceptionDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ExceptionsGrid.SelectedItem is ExceptionEntry ex)
        {
            VM.FollowInDisasmCommand.Execute(ex.FunctionStart);
            MainTabControl.SelectedIndex = 0;
        }
    }

    private void OnExceptionFollowDisasm(object sender, RoutedEventArgs e)
    {
        if (ExceptionsGrid.SelectedItem is ExceptionEntry ex)
        {
            VM.FollowInDisasmCommand.Execute(ex.FunctionStart);
            MainTabControl.SelectedIndex = 0;
        }
    }

    private void OnExceptionFollowEnd(object sender, RoutedEventArgs e)
    {
        if (ExceptionsGrid.SelectedItem is ExceptionEntry ex)
        {
            // Go to last instruction (end - 1 byte, typically ret)
            VM.FollowInDisasmCommand.Execute(ex.FunctionEnd > 0 ? ex.FunctionEnd - 1 : ex.FunctionEnd);
            MainTabControl.SelectedIndex = 0;
        }
    }

    private void OnExceptionFollowDump(object sender, RoutedEventArgs e)
    {
        if (ExceptionsGrid.SelectedItem is ExceptionEntry ex)
            VM.FollowInDumpCommand.Execute(ex.FunctionStart);
    }

    private void OnExceptionSetBp(object sender, RoutedEventArgs e)
    {
        if (ExceptionsGrid.SelectedItem is ExceptionEntry ex)
            VM.SetBreakpointAtAddress(ex.FunctionStart);
    }

    private void OnExceptionSetBpEnd(object sender, RoutedEventArgs e)
    {
        if (ExceptionsGrid.SelectedItem is ExceptionEntry ex && ex.FunctionEnd > 0)
            VM.SetBreakpointAtAddress(ex.FunctionEnd - 1);
    }

    private void OnExceptionCopy(object sender, RoutedEventArgs e)
    {
        if (ExceptionsGrid.SelectedItem is ExceptionEntry ex)
            Clipboard.SetText(ex.StartHex);
    }

    private void OnExceptionCopyName(object sender, RoutedEventArgs e)
    {
        if (ExceptionsGrid.SelectedItem is ExceptionEntry ex)
            Clipboard.SetText(ex.Display);
    }

    private void OnExceptionCopyLine(object sender, RoutedEventArgs e)
    {
        if (ExceptionsGrid.SelectedItem is ExceptionEntry ex)
            Clipboard.SetText($"{ex.ModuleName}\t{ex.Display}\t{ex.StartHex}\t{ex.EndHex}\t{ex.SizeHex}");
    }

    private void OnExceptionShowUnwind(object sender, RoutedEventArgs e)
    {
        if (ExceptionsGrid.SelectedItem is ExceptionEntry ex)
            VM.ShowUnwindInfo(ex);
    }

    /* ================================================================== */
    /*  Sections tab handlers                                              */
    /* ================================================================== */

    private void OnSectionDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SectionsGrid.SelectedItem is SectionEntry sec)
        {
            VM.FollowInDisasmCommand.Execute(sec.VirtualAddress);
            MainTabControl.SelectedIndex = 0;
        }
    }

    private void OnSectionFollowDisasm(object sender, RoutedEventArgs e)
    {
        if (SectionsGrid.SelectedItem is SectionEntry sec)
        {
            VM.FollowInDisasmCommand.Execute(sec.VirtualAddress);
            MainTabControl.SelectedIndex = 0;
        }
    }

    private void OnSectionFollowDump(object sender, RoutedEventArgs e)
    {
        if (SectionsGrid.SelectedItem is SectionEntry sec)
            VM.FollowInDumpCommand.Execute(sec.VirtualAddress);
    }

    private void OnSectionMemoryBpAll(object sender, RoutedEventArgs e)
    {
        if (SectionsGrid.SelectedItem is SectionEntry sec)
        {
            // Set PAGE_GUARD on every page in the section
            uint size = sec.VirtualSize > 0 ? sec.VirtualSize : sec.RawDataSize;
            if (size == 0) size = 0x1000;
            uint pageCount = (size + 0xFFF) / 0x1000;
            for (uint i = 0; i < pageCount; i++)
            {
                ulong pageAddr = sec.VirtualAddress + i * 0x1000;
                VM.SetBreakpointAtAddressWithType(pageAddr, Models.BreakpointType.Memory);
            }
        }
    }

    private void OnSectionDumpToFile(object sender, RoutedEventArgs e)
    {
        if (SectionsGrid.SelectedItem is SectionEntry sec)
            VM.DumpSectionToFile(sec);
    }

    private void OnSectionFillNops(object sender, RoutedEventArgs e)
    {
        if (SectionsGrid.SelectedItem is SectionEntry sec)
        {
            var result = MessageBox.Show(
                $"Fill {sec.ModuleName}:{sec.Name} ({sec.VirtualSizeHex}) with NOPs (0x90)?\n\nThis is destructive and cannot be undone!",
                "Fill Section", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes)
                VM.FillSection(sec, 0x90);
        }
    }

    private void OnSectionFillZeros(object sender, RoutedEventArgs e)
    {
        if (SectionsGrid.SelectedItem is SectionEntry sec)
        {
            var result = MessageBox.Show(
                $"Fill {sec.ModuleName}:{sec.Name} ({sec.VirtualSizeHex}) with zeros?\n\nThis is destructive and cannot be undone!",
                "Fill Section", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes)
                VM.FillSection(sec, 0x00);
        }
    }

    private void OnSectionSearchBinary(object sender, RoutedEventArgs e)
    {
        if (SectionsGrid.SelectedItem is SectionEntry sec)
            VM.SearchBinaryInSection(sec);
    }

    private void OnSectionSearchString(object sender, RoutedEventArgs e)
    {
        if (SectionsGrid.SelectedItem is SectionEntry sec)
            VM.SearchStringInSection(sec);
    }

    private void OnSectionCopyAddress(object sender, RoutedEventArgs e)
    {
        if (SectionsGrid.SelectedItem is SectionEntry sec)
            Clipboard.SetText(sec.VaHex);
    }

    private void OnSectionCopyName(object sender, RoutedEventArgs e)
    {
        if (SectionsGrid.SelectedItem is SectionEntry sec)
            Clipboard.SetText($"{sec.ModuleName}:{sec.Name}");
    }

    private void OnSectionCopyLine(object sender, RoutedEventArgs e)
    {
        if (SectionsGrid.SelectedItem is SectionEntry sec)
            Clipboard.SetText($"{sec.ModuleName}\t{sec.Name}\t{sec.VaHex}\t{sec.VirtualSizeHex}\t{sec.RawSizeHex}\t{sec.CharacteristicsHex}\t{sec.Flags}");
    }

    /* ================================================================== */
    /*  Strings tab                                                        */
    /* ================================================================== */

    private void OnStringDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (StringsGrid.SelectedItem is StringEntry str)
        {
            VM.FollowInDisasmCommand.Execute(str.Address);
            MainTabControl.SelectedIndex = 0;
        }
    }

    private void OnStringFollowDisasm(object sender, RoutedEventArgs e)
    {
        if (StringsGrid.SelectedItem is StringEntry str)
        {
            VM.FollowInDisasmCommand.Execute(str.Address);
            MainTabControl.SelectedIndex = 0;
        }
    }

    private void OnStringFollowDump(object sender, RoutedEventArgs e)
    {
        if (StringsGrid.SelectedItem is StringEntry str)
            VM.FollowInDumpCommand.Execute(str.Address);
    }

    private void OnStringSetBreakpoint(object sender, RoutedEventArgs e)
    {
        if (StringsGrid.SelectedItem is StringEntry str)
            VM.ToggleBreakpointCommand.Execute(str.Address);
    }

    private void OnStringCopyAddress(object sender, RoutedEventArgs e)
    {
        if (StringsGrid.SelectedItem is StringEntry str)
            Clipboard.SetText(str.AddressHex);
    }

    private void OnStringCopyValue(object sender, RoutedEventArgs e)
    {
        if (StringsGrid.SelectedItem is StringEntry str)
            Clipboard.SetText(str.Value);
    }

    private void OnStringCopyLine(object sender, RoutedEventArgs e)
    {
        if (StringsGrid.SelectedItem is StringEntry str)
            Clipboard.SetText($"{str.ModuleName}\t{str.SectionName}\t{str.AddressHex}\t{str.TypeName}\t{str.Length}\t{str.Value}");
    }

    /* ================================================================== */
    /*  Log context menu                                                   */
    /* ================================================================== */

    private void OnLogCopyAll(object sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder();
        foreach (var msg in VM.LogMessages)
            sb.AppendLine(msg);
        Clipboard.SetText(sb.ToString());
    }

    private void OnLogClear(object sender, RoutedEventArgs e)
    {
        VM.LogMessages.Clear();
    }

    /* ================================================================== */
    /*  Hex dump display                                                   */
    /* ================================================================== */

    private void UpdateHexDumpDisplay()
    {
        var data = VM.HexData;
        if (data == null || data.Length == 0)
        {
            HexDumpControl.Clear();
            return;
        }

        // Collect memory/HW breakpoint addresses for highlighting
        var bpAddrs = new HashSet<ulong>(
            VM.Breakpoints
                .Where(b => b.Type is Models.BreakpointType.Memory
                         or Models.BreakpointType.HwWrite
                         or Models.BreakpointType.HwReadWrite)
                .Select(b => b.Address));
        HexDumpControl.SetData(data, VM.HexAddress, bpAddrs);
    }

    private void OnAppearanceClick(object sender, RoutedEventArgs e)
    {
        // Data panels default to monospaced fonts so columns (mnemonic/operand/
        // hex/stack offset) stay aligned line-to-line.
        var dDisasm = new AppearanceWindow.FontChoice(VM.UiFontDisasm ?? "Lucida Console",
                                                      VM.UiFontDisasmSize ?? 12);
        var dHex    = new AppearanceWindow.FontChoice(VM.UiFontHex ?? "Lucida Console",
                                                      VM.UiFontHexSize ?? 12);
        var dStack  = new AppearanceWindow.FontChoice(VM.UiFontStack ?? "Lucida Console",
                                                      VM.UiFontStackSize ?? 11);
        var dRegs   = new AppearanceWindow.FontChoice(VM.UiFontRegisters ?? "Lucida Console",
                                                      VM.UiFontRegistersSize ?? 12);
        var dlg = new AppearanceWindow(VM.ThemeColors, dDisasm, dHex, dStack, dRegs) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            VM.ThemeColors = dlg.ResultColors;
            VM.UiFontDisasm        = dlg.DisasmFont.Family;
            VM.UiFontDisasmSize    = dlg.DisasmFont.Size;
            VM.UiFontHex           = dlg.HexFont.Family;
            VM.UiFontHexSize       = dlg.HexFont.Size;
            VM.UiFontStack         = dlg.StackFont.Family;
            VM.UiFontStackSize     = dlg.StackFont.Size;
            VM.UiFontRegisters     = dlg.RegistersFont.Family;
            VM.UiFontRegistersSize = dlg.RegistersFont.Size;
            VM.SaveThemeColors();
            ApplyThemeColors(dlg.ResultColors);
            ApplyPanelFonts();
            RefreshDisasmView();
        }
    }

    private void ApplyPanelFonts()
    {
        if (VM.UiFontDisasm is not null)
        {
            DisasmControl.FontFamily = AppearanceWindow.ResolveFontFamily(VM.UiFontDisasm);
            DisasmControl.LineFontSize = VM.UiFontDisasmSize ?? 11;
        }
        if (VM.UiFontHex is not null)
        {
            HexDumpControl.FontFamily = AppearanceWindow.ResolveFontFamily(VM.UiFontHex);
            HexDumpControl.LineFontSize = VM.UiFontHexSize ?? 12;
        }
        if (VM.UiFontStack is not null)
        {
            StackList.FontFamily = AppearanceWindow.ResolveFontFamily(VM.UiFontStack);
            Resources["StackFontSize"] = VM.UiFontStackSize ?? 11.0;
        }
        if (VM.UiFontRegisters is not null)
        {
            var fam = AppearanceWindow.ResolveFontFamily(VM.UiFontRegisters);
            var sz = VM.UiFontRegistersSize ?? 12;
            RegistersGrid.FontFamily = fam;
            RegistersGrid.FontSize = sz;
            FlagsGrid.FontFamily = fam;
            FlagsGrid.FontSize = sz;
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var builtIn = new HashSet<string>(SettingsWindow.TabNames);
        var pluginTabs = MainTabControl.Items.OfType<TabItem>()
            .Select(t => t.Header?.ToString() ?? "")
            .Where(h => !string.IsNullOrEmpty(h) && !builtIn.Contains(h));
        var dlg = new SettingsWindow(VM.ThemeColors, pluginTabs) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            VM.ThemeColors = dlg.ResultColors;
            VM.SaveThemeColors();
            ApplyThemeColors(dlg.ResultColors);
            RefreshDisasmView();
        }
    }

    internal void ApplyThemeColors(Dictionary<string, string> colors)
    {
        var dict = Application.Current.Resources.MergedDictionaries[0];

        void SetBrush(string brushKey, string colorHex)
        {
            if (string.IsNullOrWhiteSpace(colorHex)) return;
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(colorHex);
                dict[brushKey] = new SolidColorBrush(color);
            }
            catch { /* ignore invalid */ }
        }

        // Settings key → Dark.xaml brush key
        var map = new Dictionary<string, string>
        {
            // General
            ["Bg"]              = "BgBrush",
            ["BgLight"]         = "BgLightBrush",
            ["BgPanel"]         = "BgPanelBrush",
            ["Border"]          = "BorderBrush",
            ["Fg"]              = "FgBrush",
            ["FgDim"]           = "FgDimBrush",
            ["Accent"]          = "AccentBrush",
            ["Selection"]       = "SelectionBrush",
            ["Toolbar"]         = "ToolbarBgBrush",
            ["StatusBar"]       = "StatusBarBrush",
            ["ValueChanged"]    = "ValueChangedBrush",
            // Disassembly
            ["DsmAddress"]      = "AddressBrush",
            ["DsmMnemonic"]     = "MnemonicBrush",
            ["DsmRegister"]     = "RegisterBrush",
            ["DsmBytes"]        = "HexBrush",
            ["DsmNumber"]       = "DsmNumberBrush",
            ["DsmJump"]         = "DsmJumpBrush",
            ["DsmPunctuation"]  = "DsmPunctuationBrush",
            ["DsmString"]       = "DsmStringBrush",
            ["DsmComment"]      = "DsmCommentBrush",
            ["DsmSymbol"]       = "DsmSymbolBrush",
            ["DsmBpMarker"]     = "BreakpointBrush",
            ["DsmBpRow"]        = "BpRowBrush",
            ["DsmCurrentLine"]  = "DsmCurrentLineBrush",
            ["DsmFunction"]     = "DsmFunctionBrush",
            // Column splitters + jump arrows
            ["SplitterDash"]    = "SplitterDashBrush",
            ["JumpArrow"]       = "JumpArrowBrush",
            ["JumpArrowTaken"]  = "JumpArrowTakenBrush",
            ["JumpArrowNotTaken"] = "JumpArrowNotTakenBrush",
            ["JumpArrowRip"]    = "JumpArrowRipBrush",
            // Stack
            ["StackOffset"]     = "StackOffsetBrush",
            ["StackAddress"]    = "StackAddressBrush",
            ["StackAnnotation"] = "StackAnnotationBrush",
            // Plugin controls
            ["PluginBg"]          = "PluginBgBrush",
            ["PluginFg"]          = "PluginFgBrush",
            ["PluginFgDim"]       = "PluginFgDimBrush",
            ["PluginBorder"]      = "PluginBorderBrush",
            ["PluginAccent"]      = "PluginAccentBrush",
            ["PluginControlBg"]   = "PluginControlBgBrush",
            ["PluginButtonBg"]    = "PluginButtonBgBrush",
            ["PluginButtonHover"] = "PluginButtonHoverBrush",
            ["PluginSelection"]   = "PluginSelectionBrush",
            ["PluginGridAltRow"]  = "PluginGridAltRowBrush",
            ["PluginGroupHeader"] = "PluginGroupHeaderBrush",
            ["PluginGroupBg"]     = "PluginGroupBgBrush",
            // Script editor
            ["ScriptBg"]          = "ScriptBgBrush",
            ["ScriptFg"]          = "ScriptFgBrush",
            ["ScriptKeyword"]     = "ScriptKeywordBrush",
            ["ScriptControl"]     = "ScriptControlBrush",
            ["ScriptType"]        = "ScriptTypeBrush",
            ["ScriptString"]      = "ScriptStringBrush",
            ["ScriptComment"]     = "ScriptCommentBrush",
            ["ScriptNumber"]      = "ScriptNumberBrush",
            ["ScriptMethod"]      = "ScriptMethodBrush",
            ["ScriptPunctuation"] = "ScriptPunctuationBrush",
            // ScrollBar
            ["ScrollBarBg"]           = "ScrollBarBgBrush",
            ["ScrollBarThumb"]        = "ScrollBarThumbBrush",
            ["ScrollBarThumbHover"]   = "ScrollBarThumbHoverBrush",
            ["ScrollBarThumbPressed"] = "ScrollBarThumbPressedBrush",
            ["ScrollBarArrow"]        = "ScrollBarArrowBrush",
        };

        int applied = 0;
        foreach (var (settingKey, brushKey) in map)
        {
            if (colors.TryGetValue(settingKey, out var hex))
            {
                SetBrush(brushKey, hex);
                applied++;
            }
        }
        VM.Log($"[Theme] Applied {applied}/{map.Count} brushes from {colors.Count} color entries");
        if (dict.Contains("BgBrush") && dict["BgBrush"] is SolidColorBrush bgb)
            VM.Log($"[Theme] BgBrush = {bgb.Color}");
        if (colors.TryGetValue("Bg", out var dbgBg))
            VM.Log($"[Theme] Bg setting = {dbgBg}");
        if (dict.Contains("MnemonicBrush") && dict["MnemonicBrush"] is SolidColorBrush mb)
            VM.Log($"[Theme] MnemonicBrush = {mb.Color}");
        if (colors.TryGetValue("DsmMnemonic", out var dbgHex))
            VM.Log($"[Theme] DsmMnemonic setting = {dbgHex}");

        // Tab header colors (global TabStyle + per-tab overrides)
        ApplyTabColors(colors);

        // Update plugin wrapper scopes with new plugin brush values
        foreach (var wrapper in _pluginWrappers)
            ApplyPluginResources(wrapper);

        // Re-apply decompiler highlighting with new theme colors
        ApplyDecompilerHighlighting();
    }

    /// <summary>
    /// Overrides standard brush keys (BgBrush, FgBrush, etc.) inside the plugin wrapper scope
    /// so that implicit styles resolve to PluginXxx brushes instead of the main app brushes.
    /// </summary>
    private static readonly Dictionary<string, string> _pluginIconMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Xrefs"] = "xrefs.svg",
        ["Anti-Anti-Debug"] = "antidebug.svg",
        ["API Monitor"] = "apimonitor.svg",
        ["Bookmarks"] = "bookmarks.svg",
        ["FLIRT Signatures"] = "flirt.svg",
        ["Graph View"] = "graphview.svg",
        ["MCP Server"] = "mcp.svg",
        ["Memory Scanner"] = "memscanner.svg",
        ["Network Monitor"] = "network.svg",
        ["PE Rebuilder"] = "perebuilder.svg",
        ["Scripting"] = "scripting.svg",
        ["Session Manager"] = "session.svg",
        ["String Decryptor"] = "stringdec.svg",
        ["Themida Unpacker"] = "themida.svg",
        ["VulnHunter"] = "vulnhunter.svg",
        ["AI Assistant"] = "ai.svg",
        ["Signature Detector"] = "sigdetect.svg",
    };

    private static object BuildPluginTabHeader(string? pluginName, string title)
    {
        if (pluginName == null || !_pluginIconMap.TryGetValue(pluginName, out var iconFile))
            return title;

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        try
        {
            var uri = new Uri($"pack://application:,,,/Resources/Icons/Plugins/{iconFile}", UriKind.Absolute);
            var svg = new SharpVectors.Converters.SvgViewbox
            {
                Source = uri,
                Width = 14,
                Height = 14,
                Margin = new Thickness(0, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            panel.Children.Add(svg);
        }
        catch { /* icon load failed — fallback to text only */ }
        panel.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    private static void ApplyPluginResources(ContentControl wrapper)
    {
        var app = Application.Current.Resources.MergedDictionaries[0];
        var rd = wrapper.Resources;

        void Map(string standardKey, string pluginKey)
        {
            if (app.Contains(pluginKey))
                rd[standardKey] = app[pluginKey];
        }

        Map("BgBrush",        "PluginBgBrush");
        Map("BgLightBrush",   "PluginButtonBgBrush");
        Map("BgPanelBrush",   "PluginControlBgBrush");
        Map("FgBrush",        "PluginFgBrush");
        Map("FgDimBrush",     "PluginFgDimBrush");
        Map("BorderBrush",    "PluginBorderBrush");
        Map("AccentBrush",    "PluginAccentBrush");
        Map("SelectionBrush", "PluginSelectionBrush");
        // Script editor colors (pass-through — same key name)
        foreach (var key in new[] { "ScriptBgBrush", "ScriptFgBrush", "ScriptKeywordBrush",
            "ScriptControlBrush", "ScriptTypeBrush", "ScriptStringBrush", "ScriptCommentBrush",
            "ScriptNumberBrush", "ScriptMethodBrush", "ScriptPunctuationBrush" })
        {
            if (app.Contains(key)) rd[key] = app[key];
        }
    }

    private static SolidColorBrush? TryParseBrush(Dictionary<string, string> colors, string key)
    {
        if (!colors.TryGetValue(key, out var hex) || string.IsNullOrWhiteSpace(hex)) return null;
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
        catch { return null; }
    }

    private void ApplyTabColors(Dictionary<string, string> colors)
    {
        // Global tab style colors
        var globalBg       = TryParseBrush(colors, "TabBg");
        var globalFg       = TryParseBrush(colors, "TabFg");
        var globalSelBg    = TryParseBrush(colors, "TabSelBg");
        var globalSelFg    = TryParseBrush(colors, "TabSelFg");
        var globalSelBorder = TryParseBrush(colors, "TabSelBorder");
        var globalHoverBg  = TryParseBrush(colors, "TabHoverBg");

        foreach (TabItem tab in MainTabControl.Items)
        {
            var name = tab.Tag as string ?? tab.Header?.ToString() ?? "";

            // Per-tab overrides (individual tab colors)
            var perTabFg = TryParseBrush(colors, $"Tab.{name}.Fg");
            var perTabBg = TryParseBrush(colors, $"Tab.{name}.Bg");

            // Effective colors: per-tab override > global > fallback from resources
            var tabBg      = perTabBg ?? globalBg;
            var tabFg      = perTabFg ?? globalFg;
            var selBg      = globalSelBg;
            var selFg      = perTabFg ?? globalSelFg;
            var selBorder  = globalSelBorder ?? (FindResource("AccentBrush") as SolidColorBrush);
            var hoverBg    = globalHoverBg;

            // Build custom template
            var borderFactory = new FrameworkElementFactory(typeof(System.Windows.Controls.Border), "TabBorder");
            borderFactory.SetValue(System.Windows.Controls.Border.BorderBrushProperty,
                FindResource("BorderBrush") as Brush ?? Brushes.Gray);
            borderFactory.SetValue(System.Windows.Controls.Border.BorderThicknessProperty, new Thickness(1, 1, 1, 0));
            borderFactory.SetValue(System.Windows.Controls.Border.PaddingProperty, new Thickness(12, 5, 12, 5));
            borderFactory.SetValue(System.Windows.Controls.Border.MarginProperty, new Thickness(0, 0, -1, 0));
            borderFactory.SetValue(System.Windows.Controls.Border.SnapsToDevicePixelsProperty, true);

            if (tabBg != null)
                borderFactory.SetValue(System.Windows.Controls.Border.BackgroundProperty, tabBg);
            else
                borderFactory.SetBinding(System.Windows.Controls.Border.BackgroundProperty,
                    new System.Windows.Data.Binding("Background") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });

            var contentFactory = new FrameworkElementFactory(typeof(ContentPresenter));
            contentFactory.SetValue(ContentPresenter.ContentSourceProperty, "Header");
            contentFactory.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            contentFactory.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            borderFactory.AppendChild(contentFactory);

            var template = new ControlTemplate(typeof(TabItem)) { VisualTree = borderFactory };

            // Selected trigger
            var selectedTrigger = new Trigger { Property = TabItem.IsSelectedProperty, Value = true };
            if (selBg != null)
                selectedTrigger.Setters.Add(new Setter(System.Windows.Controls.Border.BackgroundProperty, selBg) { TargetName = "TabBorder" });
            if (selFg != null)
                selectedTrigger.Setters.Add(new Setter(TabItem.ForegroundProperty, selFg));
            if (selBorder != null)
                selectedTrigger.Setters.Add(new Setter(System.Windows.Controls.Border.BorderBrushProperty, selBorder) { TargetName = "TabBorder" });
            selectedTrigger.Setters.Add(new Setter(System.Windows.Controls.Border.BorderThicknessProperty,
                new Thickness(1, 2, 1, 0)) { TargetName = "TabBorder" });
            template.Triggers.Add(selectedTrigger);

            // Hover trigger
            if (hoverBg != null)
            {
                var hoverTrigger = new Trigger { Property = TabItem.IsMouseOverProperty, Value = true };
                hoverTrigger.Setters.Add(new Setter(System.Windows.Controls.Border.BackgroundProperty, hoverBg) { TargetName = "TabBorder" });
                template.Triggers.Add(hoverTrigger);
            }

            var style = new Style(typeof(TabItem));
            if (tabFg != null)
                style.Setters.Add(new Setter(TabItem.ForegroundProperty, tabFg));
            if (tabBg != null)
                style.Setters.Add(new Setter(TabItem.BackgroundProperty, tabBg));
            style.Setters.Add(new Setter(TabItem.TemplateProperty, template));

            tab.Style = style;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        PersistLayout();
        VM.Dispose();
        base.OnClosed(e);
    }

    private void PersistLayout()
    {
        try
        {
            if (WindowState == WindowState.Normal)
            {
                VM.UiWindowLeft = Left;
                VM.UiWindowTop = Top;
                VM.UiWindowWidth = Width;
                VM.UiWindowHeight = Height;
            }
            else if (!double.IsNaN(RestoreBounds.Width) && RestoreBounds.Width > 0)
            {
                VM.UiWindowLeft = RestoreBounds.Left;
                VM.UiWindowTop = RestoreBounds.Top;
                VM.UiWindowWidth = RestoreBounds.Width;
                VM.UiWindowHeight = RestoreBounds.Height;
            }
            VM.UiWindowState = WindowState == WindowState.Maximized ? "Maximized" : "Normal";
            VM.UiFontSize = FontSize;
            VM.UiZoomDisasm = GetPanelZoom("Disasm");
            VM.UiZoomRegisters = GetPanelZoom("Registers");
            VM.UiZoomHex = GetPanelZoom("Hex");
            VM.UiZoomStack = GetPanelZoom("Stack");
            // Splitter ratios — save as star values from ActualHeight/ActualWidth
            VM.UiTopRowRatio = TopRow.ActualHeight > 0 ? TopRow.ActualHeight : null;
            VM.UiBotRowRatio = BottomRow.ActualHeight > 0 ? BottomRow.ActualHeight : null;
            VM.UiTopLeftRatio = TopLeftCol.ActualWidth > 0 ? TopLeftCol.ActualWidth : null;
            VM.UiTopRightRatio = TopRightCol.ActualWidth > 0 ? TopRightCol.ActualWidth : null;
            VM.UiBotLeftRatio = BotLeftCol.ActualWidth > 0 ? BotLeftCol.ActualWidth : null;
            VM.UiBotRightRatio = BotRightCol.ActualWidth > 0 ? BotRightCol.ActualWidth : null;
            // Panel column widths
            VM.UiColDisasmBp = DisasmControl.BpColWidth;
            VM.UiColDisasmAddr = DisasmControl.AddrColWidth;
            VM.UiColDisasmBytes = DisasmControl.BytesColWidth;
            VM.UiColHexAddr = HexDumpControl.AddressColWidth;
            VM.UiColHexHex = HexDumpControl.HexColWidth;
            VM.UiColStackOffset = _stackCols.OffsetW.Value;
            VM.UiColStackAddr = _stackCols.AddrW.Value;
            VM.UiColRegName = RegistersGrid.Columns[0].ActualWidth;
            VM.UiColRegVal = RegistersGrid.Columns[1].ActualWidth;
            VM.PersistLayout();
        }
        catch { /* ignore */ }
    }
}
