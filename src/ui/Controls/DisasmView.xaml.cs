using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using KernelFlirt.UI.Models;
using KernelFlirt.UI.ViewModels;

namespace KernelFlirt.UI.Controls;

/// <summary>
/// OllyDbg-style disassembly view with per-token syntax highlighting.
/// </summary>
public partial class DisasmView : UserControl
{
    // x86-64 register names for highlighting
    private static readonly HashSet<string> Registers = new(StringComparer.OrdinalIgnoreCase)
    {
        "rax","rbx","rcx","rdx","rsi","rdi","rbp","rsp",
        "r8","r9","r10","r11","r12","r13","r14","r15","rip",
        "eax","ebx","ecx","edx","esi","edi","ebp","esp",
        "r8d","r9d","r10d","r11d","r12d","r13d","r14d","r15d",
        "ax","bx","cx","dx","si","di","bp","sp",
        "al","bl","cl","dl","sil","dil","bpl","spl",
        "ah","bh","ch","dh",
        "r8b","r9b","r10b","r11b","r12b","r13b","r14b","r15b",
        "r8w","r9w","r10w","r11w","r12w","r13w","r14w","r15w",
        "cs","ds","es","fs","gs","ss",
        "xmm0","xmm1","xmm2","xmm3","xmm4","xmm5","xmm6","xmm7",
        "xmm8","xmm9","xmm10","xmm11","xmm12","xmm13","xmm14","xmm15",
        "ymm0","ymm1","ymm2","ymm3","ymm4","ymm5","ymm6","ymm7",
        "ymm8","ymm9","ymm10","ymm11","ymm12","ymm13","ymm14","ymm15",
        "cr0","cr2","cr3","cr4","cr8",
        "dr0","dr1","dr2","dr3","dr6","dr7",
    };

    // Jump/call/ret instructions (highlighted differently like OllyDbg)
    internal static readonly HashSet<string> JumpMnemonics = new(StringComparer.OrdinalIgnoreCase)
    {
        "jmp","je","jne","jz","jnz","jg","jge","jl","jle",
        "ja","jae","jb","jbe","jo","jno","js","jns","jp","jnp",
        "jcxz","jecxz","jrcxz",
        "call","ret","retn","retf","iret","iretd","iretq",
        "loop","loope","loopne","loopz","loopnz",
        "syscall","sysret","int","int3","into",
    };

    // Colors — all from theme resources so they can be changed in Settings
    private static SolidColorBrush Res(string key) =>
        Application.Current.Resources.MergedDictionaries[0][key] as SolidColorBrush
        ?? new SolidColorBrush(Colors.Magenta);
    private static SolidColorBrush AddressColor => Res("AddressBrush");
    private static SolidColorBrush BytesColor => Res("HexBrush");
    private static SolidColorBrush MnemonicColor => Res("MnemonicBrush");
    private static SolidColorBrush RegisterColor => Res("RegisterBrush");
    private static SolidColorBrush NumberColor => Res("DsmNumberBrush");
    private static SolidColorBrush JumpColor => Res("DsmJumpBrush");
    private static SolidColorBrush PunctuationColor => Res("DsmPunctuationBrush");
    private static SolidColorBrush StringColor => Res("DsmStringBrush");
    private static SolidColorBrush CommentColor => Res("DsmCommentBrush");
    private static SolidColorBrush SymbolColor => Res("DsmSymbolBrush");
    private static SolidColorBrush FunctionColor => Res("DsmFunctionBrush");
    private static SolidColorBrush BpMarkerColor => Res("BreakpointBrush");
    private static SolidColorBrush CurrentLineColor => Res("DsmCurrentLineBrush");
    private static SolidColorBrush BpLineColor => Res("BpRowBrush");

    private int _selectedIndex = -1;
    private ObservableCollection<Instruction>? _instructions;

    /// <summary>Selected instruction address — used by context menus and Run to Cursor.</summary>
    public ulong SelectedAddress { get; private set; }

    public DisasmView()
    {
        InitializeComponent();
        ScrollArea.ScrollChanged += OnScrollChanged;
        KeyDown += OnKeyDown;
        Focusable = true;
    }

    // Shared column widths — bound to every instruction row Grid
    public double BpColWidth { get; set; } = 22;
    public double JumpsColWidth { get; set; } = 40;
    public double AddrColWidth { get; set; } = 170;
    public double BytesColWidth { get; set; } = 230;
    public double MnemColWidth { get; set; } = 260;

    private void OnSplitterDrag0(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e) => DragColumn(0, e.HorizontalChange);
    private void OnSplitterDrag1(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e) => DragColumn(1, e.HorizontalChange);
    private void OnSplitterDrag2(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e) => DragColumn(2, e.HorizontalChange);
    private void OnSplitterDrag3(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e) => DragColumn(3, e.HorizontalChange);
    private void OnSplitterDrag4(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e) => DragColumn(4, e.HorizontalChange);

    public void DragColumnPublic(int colIndex, double delta) => DragColumn(colIndex, delta);

    private void DragColumn(int colIndex, double delta)
    {
        double scale = LineFontSize / 11.0;
        if (scale <= 0) scale = 1;
        var cols = ColumnHeader.ColumnDefinitions;
        double newW = Math.Max(20, cols[colIndex].Width.Value + delta);
        cols[colIndex].Width = new GridLength(newW);
        double basePx = newW / scale;
        switch (colIndex)
        {
            case 0: BpColWidth = basePx; break;
            case 1: JumpsColWidth = basePx; break;
            case 2: AddrColWidth = basePx; break;
            case 3: BytesColWidth = basePx; break;
            case 4: MnemColWidth = basePx; break;
        }
        ApplyColumnWidths();
        DrawJumpArrows();
    }

    private void ApplyColumnWidths()
    {
        double scale = LineFontSize / 11.0;
        double bpW    = BpColWidth * scale;
        double jumpsW = JumpsColWidth * scale;
        double addrW  = AddrColWidth * scale;
        double bytesW = BytesColWidth * scale;
        double mnemW  = MnemColWidth * scale;

        foreach (var item in InstructionList.Items)
        {
            if (item is Border b && b.Child is Grid g && g.ColumnDefinitions.Count >= 6)
            {
                g.ColumnDefinitions[0].Width = new GridLength(bpW);
                g.ColumnDefinitions[1].Width = new GridLength(jumpsW);
                g.ColumnDefinitions[2].Width = new GridLength(addrW);
                g.ColumnDefinitions[3].Width = new GridLength(bytesW);
                g.ColumnDefinitions[4].Width = new GridLength(mnemW);
                foreach (var child in g.Children)
                {
                    if (child is not Border cb) continue;
                    int col = Grid.GetColumn(cb);
                    double w = col switch
                    {
                        0 => bpW, 2 => addrW, 3 => bytesW, 4 => mnemW, _ => double.NaN,
                    };
                    if (!double.IsNaN(w)) cb.Width = w;
                    if (col == 4 && cb.Child is MnemonicCell mc) mc.InvalidateVisual();
                }
            }
        }
    }

    public static readonly DependencyProperty LineFontSizeProperty = DependencyProperty.Register(
        nameof(LineFontSize), typeof(double), typeof(DisasmView),
        new PropertyMetadata(11.0, OnLineFontSizeChanged));

    public double LineFontSize
    {
        get => (double)GetValue(LineFontSizeProperty);
        set => SetValue(LineFontSizeProperty, value);
    }

    private static void OnLineFontSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DisasmView v) v.ApplyLineFontSize();
    }

    private void ApplyLineFontSize()
    {
        double fs = LineFontSize;
        // Scale column widths with font size so columns stay aligned on zoom.
        double scale = fs / 11.0;
        double bpW    = BpColWidth * scale;
        double jumpsW = JumpsColWidth * scale;
        double addrW  = AddrColWidth * scale;
        double bytesW = BytesColWidth * scale;
        double mnemW  = MnemColWidth * scale;

        foreach (var item in InstructionList.Items)
        {
            if (item is Border b && b.Child is Grid g)
            {
                if (g.ColumnDefinitions.Count >= 5)
                {
                    g.ColumnDefinitions[0].Width = new GridLength(bpW);
                    g.ColumnDefinitions[1].Width = new GridLength(jumpsW);
                    g.ColumnDefinitions[2].Width = new GridLength(addrW);
                    g.ColumnDefinitions[3].Width = new GridLength(bytesW);
                    g.ColumnDefinitions[4].Width = new GridLength(mnemW);
                }
                foreach (var child in g.Children)
                {
                    if (child is Border cb)
                    {
                        int col = Grid.GetColumn(cb);
                        double w = col switch
                        {
                            0 => bpW, 2 => addrW, 3 => bytesW, 4 => mnemW, _ => double.NaN,
                        };
                        if (!double.IsNaN(w)) cb.Width = w;
                    }
                }
                foreach (var child in g.Children)
                {
                    TextBlock? tb = child switch
                    {
                        TextBlock direct => direct,
                        Border wrap when wrap.Child is TextBlock inner => inner,
                        _ => null,
                    };
                    if (tb != null)
                    {
                        tb.FontSize = fs;
                        foreach (var inl in tb.Inlines)
                        {
                            if (inl is Run r) r.FontSize = fs;
                            else if (inl is InlineUIContainer iuc && iuc.Child is TextBlock sym)
                                sym.FontSize = fs;
                        }
                        tb.InvalidateMeasure();
                        continue;
                    }
                    // Custom-rendered mnemonic cell
                    if (child is Border wrap2 && wrap2.Child is MnemonicCell mc)
                    {
                        TextElement.SetFontSize(mc, fs);
                        mc.InvalidateMeasure();
                        mc.InvalidateVisual();
                    }
                }
                b.InvalidateMeasure();
            }
        }
        if (ColumnHeader.ColumnDefinitions.Count >= 5)
        {
            ColumnHeader.ColumnDefinitions[0].Width = new GridLength(bpW);
            ColumnHeader.ColumnDefinitions[1].Width = new GridLength(jumpsW);
            ColumnHeader.ColumnDefinitions[2].Width = new GridLength(addrW);
            ColumnHeader.ColumnDefinitions[3].Width = new GridLength(bytesW);
            ColumnHeader.ColumnDefinitions[4].Width = new GridLength(mnemW);
        }
        InstructionList.InvalidateMeasure();
        Dispatcher.InvokeAsync(DrawJumpArrows, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Rebuilds the arrow list for the currently visible instructions and hands
    /// it to the JumpArrowsCanvas, which renders via DrawingContext (one pass).
    /// </summary>
    private void DrawJumpArrows()
    {
        if (JumpsCanvas == null) return;
        if (InstructionList.Items.Count == 0) { JumpsCanvas.Clear(); return; }

        // Build address → row-center Y map relative to the Jumps column itself.
        var addrToY = new Dictionary<ulong, double>();
        ulong? currentRip = null;
        for (int idx = 0; idx < InstructionList.Items.Count; idx++)
        {
            if (InstructionList.Items[idx] is not Border b) continue;
            if (b.DataContext is not Instruction instr) continue;
            var origin = b.TranslatePoint(new Point(0, 0), JumpsCanvas);
            double y = origin.Y + b.ActualHeight / 2.0;
            addrToY[instr.Address] = y;
            if (instr.IsCurrentInstruction) currentRip = instr.Address;
        }

        var list = new List<JumpArrowsCanvas.JumpArrow>(InstructionList.Items.Count / 4);
        foreach (var item in InstructionList.Items)
        {
            if (item is not Border b) continue;
            if (b.DataContext is not Instruction instr) continue;
            if (instr.BranchTargetAddress == 0) continue;
            if (!IsBranchMnemonic(instr.Mnemonic)) continue;
            if (!addrToY.TryGetValue(instr.Address, out double ySrc)) continue;
            bool taken = currentRip.HasValue && instr.Address == currentRip.Value;
            var kind = taken ? JumpArrowsCanvas.ArrowKind.Taken : JumpArrowsCanvas.ArrowKind.Normal;
            if (addrToY.TryGetValue(instr.BranchTargetAddress, out double yDst))
                list.Add(new JumpArrowsCanvas.JumpArrow(ySrc, yDst, false, kind));
            else
                list.Add(new JumpArrowsCanvas.JumpArrow(ySrc, null,
                    instr.BranchTargetAddress > instr.Address, kind));
        }

        if (currentRip.HasValue && addrToY.TryGetValue(currentRip.Value, out double yRip))
            list.Add(new JumpArrowsCanvas.JumpArrow(yRip, null, false, JumpArrowsCanvas.ArrowKind.Rip));

        JumpsCanvas.SetArrows(list);
    }

    private static bool IsBranchMnemonic(string m) =>
        JumpMnemonics.Contains(m) && m != "ret" && m != "retn" && m != "retf" &&
        m != "iret" && m != "iretd" && m != "iretq" && m != "syscall" &&
        m != "sysret" && m != "int" && m != "int3" && m != "into";

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && _selectedIndex >= 0)
        {
            GetViewModel()?.AssembleAtCursorCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Keep the splitter overlay aligned with horizontally-scrolled content
        ColumnHeaderXform.X = -e.HorizontalOffset;
        // Any scroll/resize → redraw jump arrows to match new Y positions
        Dispatcher.InvokeAsync(DrawJumpArrows, System.Windows.Threading.DispatcherPriority.Render);

        var vm = GetViewModel();
        if (vm == null) return;

        // Near bottom — load more down
        if (e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 50 && e.ExtentHeight > 0)
        {
            vm.DisassembleMoreDown();
        }
        // Near top while scrolling up — snap back to RIP instead of loading above
        else if (e.VerticalOffset <= 50 && e.ExtentHeight > 0 && e.VerticalChange < 0)
        {
            ScrollToRip();
        }
    }

    /// <summary>Scrolls the view so the current RIP/EIP instruction is at the top.</summary>
    public void ScrollToRip()
    {
        if (_currentRip == null) return;
        for (int i = 0; i < InstructionList.Items.Count; i++)
        {
            if (InstructionList.Items[i] is not Border b) continue;
            var idx = (int)b.Tag;
            if (idx < (_instructions?.Count ?? 0) && _instructions![idx].Address == _currentRip.Value)
            {
                Dispatcher.InvokeAsync(() =>
                {
                    var pos = b.TranslatePoint(new Point(0, 0), ScrollArea);
                    ScrollArea.ScrollToVerticalOffset(ScrollArea.VerticalOffset + pos.Y);
                }, System.Windows.Threading.DispatcherPriority.Loaded);
                return;
            }
        }
    }

    private MainViewModel? GetViewModel()
    {
        return Window.GetWindow(this)?.DataContext as MainViewModel;
    }

    private ulong? _currentRip;

    /// <summary>
    /// Renders a list of instructions with OllyDbg-style syntax highlighting.
    /// </summary>
    public void SetInstructions(ObservableCollection<Instruction> instructions, ulong? currentRip = null)
    {
        _instructions = instructions;
        _currentRip = currentRip;
        InstructionList.Items.Clear();
        _selectedIndex = -1;

        int ripIndex = -1;
        for (int i = 0; i < instructions.Count; i++)
        {
            var instr = instructions[i];
            var panel = CreateInstructionLine(instr, currentRip);
            panel.Tag = i;
            panel.MouseLeftButtonDown += OnLineClick;
            panel.MouseRightButtonDown += OnLineClick;
            InstructionList.Items.Add(panel);
            if (currentRip.HasValue && instr.Address == currentRip.Value)
                ripIndex = i;
        }

        if (ripIndex >= 0)
        {
            int idx = ripIndex;
            Dispatcher.InvokeAsync(() =>
            {
                if (idx < InstructionList.Items.Count &&
                    InstructionList.Items[idx] is Border ripBorder)
                {
                    var pos = ripBorder.TranslatePoint(new Point(0, 0), ScrollArea);
                    ScrollArea.ScrollToVerticalOffset(ScrollArea.VerticalOffset + pos.Y);
                }
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }
        Dispatcher.InvokeAsync(DrawJumpArrows, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Append instructions to the bottom of the view (called by scroll-down loading).</summary>
    public void AppendInstructions(IReadOnlyList<Instruction> newInstrs)
    {
        if (_instructions == null) return;
        int startIdx = InstructionList.Items.Count;
        for (int i = 0; i < newInstrs.Count; i++)
        {
            var panel = CreateInstructionLine(newInstrs[i], _currentRip);
            panel.Tag = startIdx + i;
            panel.MouseLeftButtonDown += OnLineClick;
            panel.MouseRightButtonDown += OnLineClick;
            InstructionList.Items.Add(panel);
        }
    }

    /// <summary>Prepend instructions to the top of the view (called by scroll-up loading).</summary>
    public void PrependInstructions(IReadOnlyList<Instruction> newInstrs)
    {
        if (_instructions == null) return;
        for (int i = newInstrs.Count - 1; i >= 0; i--)
        {
            var panel = CreateInstructionLine(newInstrs[i], _currentRip);
            panel.Tag = 0;
            panel.MouseLeftButtonDown += OnLineClick;
            panel.MouseRightButtonDown += OnLineClick;
            InstructionList.Items.Insert(0, panel);
        }
        // Reindex all tags
        for (int i = 0; i < InstructionList.Items.Count; i++)
            if (InstructionList.Items[i] is Border b) b.Tag = i;
    }

    /// <summary>Remove N items from the top.</summary>
    public void TrimTop(int count)
    {
        for (int i = 0; i < count && InstructionList.Items.Count > 0; i++)
            InstructionList.Items.RemoveAt(0);
        for (int i = 0; i < InstructionList.Items.Count; i++)
            if (InstructionList.Items[i] is Border b) b.Tag = i;
    }

    /// <summary>Remove N items from the bottom.</summary>
    public void TrimBottom(int count)
    {
        for (int i = 0; i < count && InstructionList.Items.Count > 0; i++)
            InstructionList.Items.RemoveAt(InstructionList.Items.Count - 1);
    }

    private Border CreateInstructionLine(Instruction instr, ulong? currentRip)
    {
        TextBlock MakeCellTb() => new()
        {
            FontFamily = new FontFamily("Lucida Console"),
            FontSize = LineFontSize,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        // BP column
        var bpTb = MakeCellTb();
        if (instr.HasBreakpoint)
            bpTb.Inlines.Add(new Run("●") { Foreground = BpMarkerColor, FontWeight = FontWeights.Bold });

        // Address column — always hex address (x64dbg-style).
        // Function label (if any) is surfaced as a separator row, not inside the address cell.
        var addrTb = MakeCellTb();
        addrTb.Inlines.Add(new Run(FormatAddress(instr.Address)) { Foreground = AddressColor });

        // Bytes column
        var bytesTb = MakeCellTb();
        bytesTb.Inlines.Add(new Run(instr.BytesHex) { Foreground = BytesColor });

        // Mnemonic + operands + comment — custom-rendered via MnemonicCell so
        // long symbol names get a proper "…" ellipsis while still being clickable.
        var mnemCell = new MnemonicCell { Instruction = instr };
        TextElement.SetFontSize(mnemCell, LineFontSize);

        // Live-hint column (x64dbg-style register/mem annotations) with per-value Copy.
        // LiveHint is produced synchronously (registers), MemHint asynchronously
        // (memory previews). Rendered together here — each pass owns its own
        // field so duplicate appends aren't possible.
        var hintTb = MakeCellTb();
        string combined = (instr.LiveHint, instr.MemHint) switch
        {
            (null or "", null or "") => "",
            (var a, null or "") => a!,
            (null or "", var b) => b!,
            (var a, var b) => $"{a}, {b}",
        };
        if (!string.IsNullOrEmpty(combined))
        {
            BuildHintInlines(hintTb, combined);
            hintTb.ContextMenu = new ContextMenu
            {
                Items =
                {
                    CreateSymMenuItem("Copy all hints", () => Clipboard.SetText(combined)),
                }
            };
        }

        // Each cell is wrapped in a Border with ClipToBounds so long content
        // (symbol names with InlineUIContainer break TextTrimming) can't spill
        // into neighbouring columns.
        // Each cell is a Border with an explicit Width equal to the current
        // column width, and ClipToBounds. Explicit Width (rather than Stretch)
        // prevents InlineUIContainer children from making Grid re-measure the
        // column wider — the Border cannot grow past its configured size.
        Border Wrap(TextBlock t, int col, double w)
        {
            t.HorizontalAlignment = HorizontalAlignment.Left;
            var b = new Border
            {
                Child = t,
                ClipToBounds = true,
                HorizontalAlignment = double.IsNaN(w) ? HorizontalAlignment.Stretch : HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Stretch,
                SnapsToDevicePixels = true,
            };
            if (!double.IsNaN(w)) b.Width = w;
            Grid.SetColumn(b, col);
            return b;
        }

        double scale = LineFontSize / 11.0;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(BpColWidth * scale) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(JumpsColWidth * scale) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(AddrColWidth * scale) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(BytesColWidth * scale) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(MnemColWidth * scale) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(Wrap(bpTb, 0, BpColWidth * scale));
        // column 1 reserved for JumpsCanvas overlay (drawn in code-behind)
        grid.Children.Add(Wrap(addrTb, 2, AddrColWidth * scale));
        grid.Children.Add(Wrap(bytesTb, 3, BytesColWidth * scale));
        // Mnemonic uses custom-rendered FrameworkElement — wrap it in a
        // fixed-width Border so ClipToBounds stops neighbours from being
        // pushed wider, just like the other cells.
        var mnemBorder = new Border
        {
            Child = mnemCell,
            Width = MnemColWidth * scale,
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch,
            SnapsToDevicePixels = true,
        };
        Grid.SetColumn(mnemBorder, 4);
        grid.Children.Add(mnemBorder);
        // Last (hint) column takes whatever's left — leave it flexible.
        grid.Children.Add(Wrap(hintTb, 5, double.NaN));

        Brush bgBrush;
        if (instr.IsCurrentInstruction || (currentRip.HasValue && instr.Address == currentRip.Value))
            bgBrush = CurrentLineColor;
        else if (instr.HasBreakpoint)
            bgBrush = BpLineColor;
        else
            bgBrush = Brushes.Transparent;

        var border = new Border
        {
            Child = grid,
            Background = bgBrush,
            Padding = new Thickness(4, 1, 4, 1),
            BorderThickness = new Thickness(0),
            DataContext = instr,
        };
        return border;
    }

    private static string FormatAddress(ulong addr)
    {
        // OllyDbg style: 00007FF6`12340000
        string hex = addr.ToString("X16");
        return hex[..8] + "`" + hex[8..];
    }

    /// <summary>
    /// Creates a clickable symbol name inline with right-click context menu
    /// (Go to, Set breakpoint, Copy symbol name).
    /// Uses InlineUIContainer wrapping a TextBlock since WPF Run has no ContextMenu.
    /// </summary>
    private InlineUIContainer CreateSymbolInline(string symbolName, ulong address)
    {
        var symText = new TextBlock
        {
            Text = symbolName,
            FontFamily = new FontFamily("Lucida Console"),
            FontSize = LineFontSize,
            Foreground = FunctionColor,
            Cursor = Cursors.Hand,
            TextDecorations = TextDecorations.Underline,
            ToolTip = $"{symbolName}\n{address:X16}",
        };

        // Double-click navigates to symbol
        symText.MouseLeftButtonDown += (s, e) =>
        {
            if (e.ClickCount == 2)
            {
                GetViewModel()?.NavigateDisasmTo(address);
                e.Handled = true;
            }
        };

        // Right-click context menu
        symText.ContextMenu = new ContextMenu
        {
            Items =
            {
                CreateSymMenuItem($"Go to {symbolName}", () => GetViewModel()?.NavigateDisasmTo(address)),
                CreateSymMenuItem($"Set breakpoint on {symbolName}", () => GetViewModel()?.SetBreakpointAtAddress(address)),
                new Separator(),
                CreateSymMenuItem("Copy symbol name", () => Clipboard.SetText(symbolName)),
                CreateSymMenuItem("Copy address", () => Clipboard.SetText($"{address:X16}")),
            }
        };

        return new InlineUIContainer(symText) { BaselineAlignment = BaselineAlignment.TextBottom };
    }

    private static MenuItem CreateSymMenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>
    /// Splits "rax:ntdll+1234, [rip+0x10]=0xDEAD" into "label : value" pairs and
    /// makes each value a clickable TextBlock with a Copy context menu.
    /// </summary>
    private void BuildHintInlines(TextBlock tb, string hint)
    {
        var tokens = hint.Split(',');
        for (int t = 0; t < tokens.Length; t++)
        {
            var tok = tokens[t].TrimStart();
            // Separator between labels (not before first)
            if (t > 0) tb.Inlines.Add(new Run(", ") { Foreground = CommentColor, FontStyle = FontStyles.Italic });

            // Find the boundary: "label:val" or "[expr]=val"
            int sep = -1;
            char sepCh = '\0';
            int bracket = 0;
            for (int i = 0; i < tok.Length; i++)
            {
                char c = tok[i];
                if (c == '[') bracket++;
                else if (c == ']') bracket--;
                else if (bracket == 0 && (c == ':' || c == '='))
                {
                    sep = i; sepCh = c; break;
                }
            }
            if (sep < 0)
            {
                tb.Inlines.Add(new Run(tok) { Foreground = CommentColor, FontStyle = FontStyles.Italic });
                continue;
            }
            var label = tok[..sep];
            var value = tok[(sep + 1)..];
            tb.Inlines.Add(new Run(label + sepCh)
            { Foreground = CommentColor, FontStyle = FontStyles.Italic });
            tb.Inlines.Add(CreateHintValueInline(value));
        }
    }

    private InlineUIContainer CreateHintValueInline(string rawValue)
    {
        var copyText = rawValue.Trim();
        // For pointer-style "→name" strip the arrow for clipboard clarity
        if (copyText.StartsWith("→")) copyText = copyText[1..];

        var valTb = new TextBlock
        {
            Text = rawValue,
            FontFamily = new FontFamily("Lucida Console"),
            FontSize = LineFontSize,
            FontStyle = FontStyles.Italic,
            Foreground = CommentColor,
            Cursor = Cursors.Hand,
        };
        valTb.ContextMenu = new ContextMenu
        {
            Items =
            {
                CreateSymMenuItem("Copy value", () => Clipboard.SetText(copyText)),
                CreateSymMenuItem("Copy full hint entry", () =>
                    Clipboard.SetText(rawValue)),
            }
        };
        return new InlineUIContainer(valTb) { BaselineAlignment = BaselineAlignment.TextBottom };
    }

    /// <summary>
    /// Add mnemonic + operands with syntax highlighting.
    /// For branch instructions with resolved symbols, replaces hex operand with clickable symbol name.
    /// </summary>
    private void AddHighlightedMnemonic(TextBlock tb, Instruction instr)
    {
        // Mnemonic
        Brush mnemonicBrush = JumpMnemonics.Contains(instr.Mnemonic) ? JumpColor : MnemonicColor;
        tb.Inlines.Add(new Run(instr.Mnemonic.PadRight(8)) { Foreground = mnemonicBrush, FontWeight = FontWeights.SemiBold });

        if (string.IsNullOrEmpty(instr.Operands))
            return;

        // For branch instructions with a resolved symbol, render a clickable
        // InlineUIContainer — preserves double-click navigation and the per-symbol
        // context menu (Go to / Copy name / Set breakpoint). Long names are
        // clipped by the cell's Border (ClipToBounds + fixed Width); they won't
        // get the "…" ellipsis WPF draws for plain Runs, but losing click
        // behaviour just to gain three dots isn't worth it.
        if (!string.IsNullOrEmpty(instr.BranchTargetSymbol) && instr.BranchTargetAddress != 0)
        {
            var symInline = CreateSymbolInline(instr.BranchTargetSymbol, instr.BranchTargetAddress);
            tb.Inlines.Add(symInline);
            return;
        }

        // Normal operands: tokenize and highlight
        var tokens = TokenizeOperands(instr.Operands);
        foreach (var (text, kind) in tokens)
        {
            Brush brush = kind switch
            {
                TokenKind.Register => RegisterColor,
                TokenKind.Number => NumberColor,
                TokenKind.Punctuation => PunctuationColor,
                TokenKind.SizePrefix => PunctuationColor,
                TokenKind.String => StringColor,
                TokenKind.Symbol => CommentColor,
                _ => MnemonicColor,
            };
            tb.Inlines.Add(new Run(text) { Foreground = brush });
        }
    }

    internal enum TokenKind { Text, Register, Number, Punctuation, SizePrefix, String, Symbol }

    internal static List<(string text, TokenKind kind)> TokenizeOperandsStatic(string operands)
        => TokenizeOperands(operands);

    private static List<(string text, TokenKind kind)> TokenizeOperands(string operands)
    {
        var result = new List<(string, TokenKind)>();
        int i = 0;

        while (i < operands.Length)
        {
            char c = operands[i];

            // Whitespace
            if (char.IsWhiteSpace(c))
            {
                int start = i;
                while (i < operands.Length && char.IsWhiteSpace(operands[i])) i++;
                result.Add((operands[start..i], TokenKind.Text));
                continue;
            }

            // Punctuation: , [ ] + - * :
            if (c is ',' or '[' or ']' or '+' or '-' or '*' or ':' or '(' or ')')
            {
                result.Add((c.ToString(), TokenKind.Punctuation));
                i++;
                continue;
            }

            // Hex number: 0x...
            if (c == '0' && i + 1 < operands.Length && operands[i + 1] == 'x')
            {
                int start = i;
                i += 2;
                while (i < operands.Length && IsHexChar(operands[i])) i++;
                result.Add((operands[start..i], TokenKind.Number));
                continue;
            }

            // Word token (identifier, register, number, size prefix)
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                int start = i;
                while (i < operands.Length && (char.IsLetterOrDigit(operands[i]) || operands[i] == '_')) i++;
                string word = operands[start..i];

                if (word is "byte" or "word" or "dword" or "qword" or "xmmword" or "ymmword"
                    or "ptr" or "BYTE" or "WORD" or "DWORD" or "QWORD" or "PTR")
                {
                    result.Add((word, TokenKind.SizePrefix));
                }
                else if (Registers.Contains(word))
                {
                    result.Add((word, TokenKind.Register));
                }
                else if (IsHexNumber(word))
                {
                    result.Add((word, TokenKind.Number));
                }
                else
                {
                    result.Add((word, TokenKind.Text));
                }
                continue;
            }

            // Anything else
            result.Add((c.ToString(), TokenKind.Text));
            i++;
        }

        return result;
    }

    private static bool IsHexChar(char c)
        => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');

    private static bool IsHexNumber(string s)
    {
        if (s.Length == 0) return false;
        if (s.All(c => c >= '0' && c <= '9')) return true;
        if (s[^1] is 'h' or 'H' && s[..^1].All(IsHexChar)) return true;
        if (s.All(IsHexChar) && s.Any(c => c >= '0' && c <= '9')) return true;
        return false;
    }

    private void OnLineClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border && border.Tag is int index)
        {
            // Deselect previous — restore BP color if needed
            if (_selectedIndex >= 0 && _selectedIndex < InstructionList.Items.Count)
            {
                if (InstructionList.Items[_selectedIndex] is Border prev)
                {
                    bool prevHasBp = _instructions != null && _selectedIndex < _instructions.Count
                        && _instructions[_selectedIndex].HasBreakpoint;
                    prev.Background = prevHasBp ? BpLineColor : Brushes.Transparent;
                }
            }

            border.Background = Res("SelectionBrush");
            _selectedIndex = index;

            // Update selected address for context menu operations
            if (_instructions != null && index < _instructions.Count)
            {
                SelectedAddress = _instructions[index].Address;
                var vm = GetViewModel();
                if (vm != null)
                    vm.SelectedDisasmAddress = SelectedAddress;
            }
        }
    }

    /* ================================================================== */
    /*  Context menu handlers                                              */
    /* ================================================================== */

    private void OnContextToggleBp(object sender, RoutedEventArgs e)
    {
        GetViewModel()?.ToggleBreakpointCommand.Execute(null);
    }

    private void OnContextToggleHwBp(object sender, RoutedEventArgs e)
    {
        GetViewModel()?.ToggleHwBreakpointCommand.Execute(null);
    }

    private void OnContextSetCondBp(object sender, RoutedEventArgs e)
    {
        GetViewModel()?.SetConditionalBreakpointCommand.Execute(null);
    }

    private void OnContextSetLogBp(object sender, RoutedEventArgs e)
    {
        GetViewModel()?.SetLogBreakpointCommand.Execute(null);
    }

    private void OnContextRunToCursor(object sender, RoutedEventArgs e)
    {
        GetViewModel()?.RunToCursorCommand.Execute(null);
    }

    private void OnContextSkipInstruction(object sender, RoutedEventArgs e)
    {
        GetViewModel()?.SkipInstructionCommand.Execute(null);
    }

    private void OnContextSetRipHere(object sender, RoutedEventArgs e)
    {
        if (SelectedAddress != 0)
            GetViewModel()?.SetInstructionPointer(SelectedAddress);
    }

    private void OnContextAddBookmark(object sender, RoutedEventArgs e)
    {
        GetViewModel()?.AddBookmarkCommand.Execute(null);
    }

    private void OnContextAddNote(object sender, RoutedEventArgs e)
    {
        if (SelectedAddress == 0) return;
        GetViewModel()?.AddNoteAtAddress(SelectedAddress);
    }

    private void OnContextEditNote(object sender, RoutedEventArgs e)
    {
        if (SelectedAddress == 0) return;
        GetViewModel()?.EditNoteAtAddress(SelectedAddress);
    }

    private void OnContextRemoveNote(object sender, RoutedEventArgs e)
    {
        if (SelectedAddress == 0) return;
        GetViewModel()?.RemoveNoteAtAddress(SelectedAddress);
    }

    private void OnContextFollowInDump(object sender, RoutedEventArgs e)
    {
        if (SelectedAddress != 0)
            GetViewModel()?.FollowInDumpCommand.Execute(SelectedAddress);
    }

    private void OnContextFollowInDisasm(object sender, RoutedEventArgs e)
    {
        // Try to parse the operand as an address for "follow" behavior
        if (_instructions != null && _selectedIndex >= 0 && _selectedIndex < _instructions.Count)
        {
            var instr = _instructions[_selectedIndex];
            if (!string.IsNullOrEmpty(instr.Operands) &&
                TryParseOperandAddress(instr.Operands, out ulong target))
            {
                GetViewModel()?.FollowInDisasmCommand.Execute(target);
                return;
            }
        }
        if (SelectedAddress != 0)
            GetViewModel()?.FollowInDisasmCommand.Execute(SelectedAddress);
    }

    private void OnContextCopyAddress(object sender, RoutedEventArgs e)
    {
        if (SelectedAddress != 0)
            Clipboard.SetText($"{SelectedAddress:X16}");
    }

    private void OnContextCopyLine(object sender, RoutedEventArgs e)
    {
        GetViewModel()?.CopyDisasmLineCommand.Execute(null);
    }

    private void OnContextCopyAll(object sender, RoutedEventArgs e)
    {
        GetViewModel()?.CopyAllDisasmCommand.Execute(null);
    }

    private void OnContextSearchBinary(object sender, RoutedEventArgs e)
    {
        GetViewModel()?.SearchBinaryCommand.Execute(null);
    }

    private void OnContextSearchStrings(object sender, RoutedEventArgs e)
    {
        GetViewModel()?.SearchStringsCommand.Execute(null);
    }

    private void OnContextDecompile(object sender, RoutedEventArgs e)
    {
        GetViewModel()?.DecompileAtCursorCommand.Execute(null);
    }

    private void OnContextAssemble(object sender, RoutedEventArgs e)
    {
        GetViewModel()?.AssembleAtCursorCommand.Execute(null);
    }

    private void OnContextNopInstruction(object sender, RoutedEventArgs e)
    {
        GetViewModel()?.NopInstructionCommand.Execute(null);
    }

    private void OnContextFillNops(object sender, RoutedEventArgs e)
    {
        GetViewModel()?.FillWithNopsCommand.Execute(null);
    }

    private void OnContextGoBack(object sender, RoutedEventArgs e)
    {
        GetViewModel()?.DisasmGoBackCommand.Execute(null);
    }

    private static bool TryParseOperandAddress(string operands, out ulong address)
    {
        address = 0;
        string s = operands.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ulong.TryParse(s[2..], System.Globalization.NumberStyles.HexNumber, null, out address);
        return ulong.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out address);
    }
}
