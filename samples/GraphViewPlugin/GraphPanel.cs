using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KernelFlirt.SDK;

namespace GraphViewPlugin;

/// <summary>
/// WPF panel for the "Graph View" tab.
/// Shows the CFG of the current function with zoom/pan.
/// </summary>
public sealed class GraphPanel : Grid
{
    private readonly IDebuggerApi _api;
    private readonly CfgBuilder _builder;
    private readonly GraphRenderer _renderer;
    private readonly Canvas _canvas;
    private readonly ScaleTransform _scaleTransform;
    private readonly TranslateTransform _translateTransform;
    private readonly TextBlock _statusText;
    private readonly TextBox _addressBox;

    private readonly Border _container;
    private Dictionary<ulong, Rect> _hitMap = new();
    private List<BasicBlock> _blocks = new();

    // Block state (persists across re-renders)
    private readonly Dictionary<ulong, Color> _blockColors = new();
    private readonly HashSet<ulong> _collapsedBlocks = new();
    private readonly Stack<ulong> _navigationStack = new();
    private List<GraphRenderer.CallTargetHit> _callTargets = new();
    private ulong _lastFuncAddr;
    private ulong _lastRip;

    // Pan state
    private bool _isPanning;
    private Point _panStart;
    private double _panStartX, _panStartY;

    public GraphPanel(IDebuggerApi api)
    {
        _api = api;
        _builder = new CfgBuilder(api);
        _renderer = new GraphRenderer();

        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });    // toolbar
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // graph
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });    // status

        Margin = new Thickness(4);
        SetResourceReference(BackgroundProperty, "PluginBgBrush");

        // ── Row 0: Toolbar ──────────────────────────────────────────────────
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 4)
        };

        toolbar.Children.Add(MakeButton("Graph at RIP", OnGraphAtRip));

        var addrLabel = new TextBlock
        {
            Text = " Address: ",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };
        addrLabel.SetResourceReference(TextBlock.ForegroundProperty, "PluginFgBrush");
        toolbar.Children.Add(addrLabel);

        _addressBox = new TextBox
        {
            Width = 160,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(4, 2, 4, 2)
        };
        _addressBox.SetResourceReference(TextBox.BackgroundProperty, "PluginControlBgBrush");
        _addressBox.SetResourceReference(TextBox.ForegroundProperty, "PluginFgBrush");
        _addressBox.SetResourceReference(TextBox.BorderBrushProperty, "PluginBorderBrush");
        _addressBox.SetResourceReference(TextBox.CaretBrushProperty, "PluginFgBrush");
        _addressBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) OnGraphAtAddress(); };
        toolbar.Children.Add(_addressBox);

        toolbar.Children.Add(MakeButton("Go", (_, _) => OnGraphAtAddress()));
        toolbar.Children.Add(MakeButton("Back (Shift+Esc)", OnGoBack));
        toolbar.Children.Add(MakeSeparator());
        toolbar.Children.Add(MakeButton("Zoom In", (_, _) => Zoom(1.2)));
        toolbar.Children.Add(MakeButton("Zoom Out", (_, _) => Zoom(1 / 1.2)));
        toolbar.Children.Add(MakeButton("Fit", (_, _) => FitToView()));
        toolbar.Children.Add(MakeButton("1:1", (_, _) => ResetZoom()));

        SetRow(toolbar, 0);
        Children.Add(toolbar);

        // ── Row 1: Graph canvas with zoom + pan (no ScrollViewer) ────────────
        _scaleTransform = new ScaleTransform(1, 1);
        _translateTransform = new TranslateTransform(0, 0);

        _canvas = new Canvas
        {
            Background = Brushes.Transparent,
            ClipToBounds = false,
            CacheMode = new BitmapCache { RenderAtScale = 2 }
        };

        // TransformGroup: translate first, then scale — so zoom is around origin,
        // and we adjust translate to keep the mouse point stable
        _canvas.RenderTransform = new TransformGroup
        {
            Children = { _translateTransform, _scaleTransform }
        };

        // Container border clips the canvas and captures mouse events
        var container = new Border
        {
            ClipToBounds = true,
            Child = _canvas
        };
        container.SetResourceReference(Border.BackgroundProperty, "PluginControlBgBrush");
        container.PreviewMouseWheel += OnMouseWheel;
        container.MouseLeftButtonDown += OnMouseLeftDown;
        container.MouseLeftButtonUp += OnMouseLeftUp;
        container.MouseMove += OnMouseMove;
        container.MouseRightButtonUp += OnMouseRightUp;
        container.PreviewMouseLeftButtonDown += OnPreviewMouseLeftDown;
        container.PreviewKeyDown += OnKeyDown;
        container.Focusable = true;

        SetRow(container, 1);
        Children.Add(container);

        _container = container;

        // ── Row 2: Status bar ───────────────────────────────────────────────
        _statusText = new TextBlock
        {
            Text = "Click 'Graph at RIP' or enter an address to view the CFG.",
            Margin = new Thickness(0, 4, 0, 0),
            FontSize = 11
        };
        _statusText.SetResourceReference(TextBlock.ForegroundProperty, "PluginFgDimBrush");
        SetRow(_statusText, 2);
        Children.Add(_statusText);

        // Listen for break state to auto-refresh
        api.OnBreakStateEntered += () =>
            Dispatcher.InvokeAsync(() => _statusText.Text = "Break. Ready to graph.");
    }

    // ── Graph building ───────────────────────────────────────────────────────

    private void OnGraphAtRip(object sender, RoutedEventArgs e)
    {
        if (!CheckState()) return;

        var regs = _api.Memory.ReadRegisters(_api.TargetPid, _api.SelectedThreadId);
        if (regs == null || regs.Count == 0)
        {
            _statusText.Text = "Error: Could not read registers.";
            return;
        }
        var rip = regs.FirstOrDefault(r => r.Name == "RIP")?.Value
                ?? regs.FirstOrDefault(r => r.Name == "EIP")?.Value
                ?? 0;
        if (rip == 0)
        {
            _statusText.Text = $"Error: Could not read RIP. Regs: {string.Join(", ", regs.Take(5).Select(r => r.Name))}";
            return;
        }

        // Try to find function start (scan backwards for common prologue or use RIP directly)
        var funcStart = FindFunctionStart(rip);
        _addressBox.Text = $"0x{funcStart:X}";
        BuildAndRender(funcStart, rip);
    }

    private void OnGraphAtAddress()
    {
        if (!CheckState()) return;

        var text = _addressBox.Text.Trim().TrimStart('0').TrimStart('x', 'X');
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            text = text[2..];

        if (!ulong.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out ulong addr))
        {
            // Try symbol name
            addr = _api.Symbols.ResolveNameToAddress(_addressBox.Text.Trim());
        }

        if (addr == 0)
        {
            _statusText.Text = "Error: Invalid address or symbol.";
            return;
        }

        BuildAndRender(addr, 0);
    }

    private void BuildAndRender(ulong funcAddr, ulong currentRip)
    {
        _statusText.Text = "Building CFG...";
        _lastFuncAddr = funcAddr;
        _lastRip = currentRip;

        Task.Run(() =>
        {
            var blocks = _builder.Build(funcAddr);
            Dispatcher.InvokeAsync(() =>
            {
                _blocks = blocks;
                if (blocks.Count == 0)
                {
                    _canvas.Children.Clear();
                    _statusText.Text = "No code found at this address.";
                    return;
                }

                int totalInstrs = blocks.Sum(b => b.Instructions.Count);
                int totalEdges = blocks.Sum(b => b.Successors.Count);

                var annotations = _api.UI.GetAllAnnotations();
                _callTargets = new List<GraphRenderer.CallTargetHit>();
                _hitMap = _renderer.Render(_canvas, blocks, _api.Is32Bit, currentRip, _blockColors, _collapsedBlocks, annotations, _callTargets);
                ResetZoom();

                var sym = _api.Symbols.ResolveAddress(funcAddr);
                var funcName = sym ?? $"0x{funcAddr:X}";
                _statusText.Text = $"{funcName} — {blocks.Count} blocks, {totalInstrs} instructions, {totalEdges} edges";
            });
        });
    }

    /// <summary>
    /// Try to find the function start by scanning backwards for a common prologue.
    /// Falls back to the given address if no prologue found.
    /// </summary>
    private ulong FindFunctionStart(ulong rip)
    {
        // Read 256 bytes before RIP and scan for prologue
        ulong scanStart = rip > 256 ? rip - 256 : 0;
        uint scanSize = (uint)(rip - scanStart + 16);
        var data = _api.Memory.ReadMemory(_api.TargetPid, scanStart, scanSize);
        if (data == null) return rip;

        ulong bestMatch = rip;

        for (int i = (int)(rip - scanStart); i >= 0; i--)
        {
            // x64: push rbp; mov rbp, rsp (55 48 89 E5) or sub rsp (48 83 EC / 48 81 EC)
            // x64: push rbx; sub rsp (48 89 5C 24)
            // x86: push ebp; mov ebp, esp (55 8B EC)

            if (i + 3 < data.Length)
            {
                // push rbp; ... (common function start)
                if (data[i] == 0x55 && (data[i + 1] == 0x48 || data[i + 1] == 0x8B))
                {
                    bestMatch = scanStart + (ulong)i;
                    break;
                }

                // sub rsp, imm8
                if (data[i] == 0x48 && data[i + 1] == 0x83 && data[i + 2] == 0xEC)
                {
                    bestMatch = scanStart + (ulong)i;
                    break;
                }

                // mov [rsp+...], rbx (register save)
                if (data[i] == 0x48 && data[i + 1] == 0x89 && data[i + 2] == 0x5C)
                {
                    bestMatch = scanStart + (ulong)i;
                    break;
                }

                // mov [rsp+...], ... (40 53, 40 55, 40 56, 40 57 — push with REX)
                if (data[i] == 0x40 && data[i + 1] >= 0x53 && data[i + 1] <= 0x57)
                {
                    bestMatch = scanStart + (ulong)i;
                    break;
                }

                // INT3 or NOP padding before function (previous function's padding)
                if (i > 0 && (data[i - 1] == 0xCC || data[i - 1] == 0x90) &&
                    data[i] != 0xCC && data[i] != 0x90)
                {
                    bestMatch = scanStart + (ulong)i;
                    break;
                }
            }
        }

        return bestMatch;
    }

    // ── Mouse interaction ────────────────────────────────────────────────────

    private void OnPreviewMouseLeftDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return; // only double-click

        var canvasPos = e.GetPosition(_canvas);

        foreach (var ct in _callTargets)
        {
            if (ct.Rect.Contains(canvasPos))
            {
                NavigateToFunction(ct.TargetAddress, ct.Symbol);
                e.Handled = true;
                return;
            }
        }
    }

    private void NavigateToFunction(ulong targetAddr, string symbol)
    {
        // Push current function onto navigation stack
        _api.Log.Info($"[GraphView] Navigate: target=0x{targetAddr:X} lastFunc=0x{_lastFuncAddr:X} stackBefore={_navigationStack.Count}");
        if (_lastFuncAddr != 0)
            _navigationStack.Push(_lastFuncAddr);
        else
            _navigationStack.Push(targetAddr); // fallback: at least save something

        _addressBox.Text = $"0x{targetAddr:X}";
        BuildAndRender(targetAddr, 0);
        _statusText.Text = $"Navigated to {symbol}. Stack: {_navigationStack.Count}. Shift+Esc to go back.";
    }

    private void OnGoBack(object sender, RoutedEventArgs e) => GoBack();

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.Shift)
        {
            GoBack();
            e.Handled = true;
        }
    }

    private void GoBack()
    {
        if (_navigationStack.Count == 0)
        {
            _statusText.Text = "No previous function in history.";
            return;
        }
        var prevAddr = _navigationStack.Pop();
        _addressBox.Text = $"0x{prevAddr:X}";
        BuildAndRender(prevAddr, 0);
        _statusText.Text = $"Back to 0x{prevAddr:X}. ({_navigationStack.Count} in history)";
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Zoom relative to mouse position
        // Transform order: translate first, then scale
        // screenPos = (canvasPos + translate) * scale
        double factor = e.Delta > 0 ? 1.15 : 1.0 / 1.15;
        double oldScale = _scaleTransform.ScaleX;
        double newScale = Math.Clamp(oldScale * factor, 0.1, 5.0);
        if (Math.Abs(newScale - oldScale) < 0.001) { e.Handled = true; return; }

        var mousePos = e.GetPosition(_container);

        // Keep the canvas point under mouse fixed:
        // newTranslate = oldTranslate + mousePos * (1/newScale - 1/oldScale)
        _translateTransform.X += mousePos.X * (1.0 / newScale - 1.0 / oldScale);
        _translateTransform.Y += mousePos.Y * (1.0 / newScale - 1.0 / oldScale);

        _scaleTransform.ScaleX = newScale;
        _scaleTransform.ScaleY = newScale;

        e.Handled = true;
    }

    private void OnMouseLeftDown(object sender, MouseButtonEventArgs e)
    {
        _isPanning = true;
        _panStart = e.GetPosition(_container);
        _panStartX = _translateTransform.X;
        _panStartY = _translateTransform.Y;
        _container.CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseLeftUp(object sender, MouseButtonEventArgs e)
    {
        if (_isPanning)
        {
            _isPanning = false;
            _container.ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_isPanning)
        {
            var pos = e.GetPosition(_container);
            double scale = _scaleTransform.ScaleX;
            _translateTransform.X = _panStartX + (pos.X - _panStart.X) / scale;
            _translateTransform.Y = _panStartY + (pos.Y - _panStart.Y) / scale;
        }
    }

    private ulong? HitTestBlock(MouseEventArgs e)
    {
        // GetPosition(_canvas) gives position in canvas coordinate space
        // (WPF automatically applies inverse RenderTransform)
        var canvasPos = e.GetPosition(_canvas);

        foreach (var (addr, rect) in _hitMap)
            if (rect.Contains(canvasPos))
                return addr;
        return null;
    }

    private void OnMouseRightUp(object sender, MouseButtonEventArgs e)
    {
        var addr = HitTestBlock(e);

        var menu = new ContextMenu();

        // If clicked on empty space — show minimal menu with Go Back
        if (addr == null)
        {
            var miBack = new MenuItem { Header = $"Go Back ({_navigationStack.Count} in history) [Shift+Esc]" };
            miBack.Click += (_, _) => GoBack();
            menu.Items.Add(miBack);
            _container.ContextMenu = menu;
            menu.IsOpen = true;
            e.Handled = true;
            return;
        }

        var blockAddr = addr.Value;
        var block = _blocks.FirstOrDefault(b => b.StartAddress == blockAddr);
        if (block == null) return;

        // ── Navigate ────────────────────────────────────────────────────
        var miNav = new MenuItem { Header = $"Go to 0x{blockAddr:X} in Disassembly" };
        miNav.Click += (_, _) => {
            _api.UI.NavigateDisassembly(blockAddr);
            _statusText.Text = $"Navigated to 0x{blockAddr:X}";
        };
        menu.Items.Add(miNav);

        menu.Items.Add(new Separator());

        // ── Color ───────────────────────────────────────────────────────
        var miColor = new MenuItem { Header = "Set Color" };
        AddColorItem(miColor, "Red", Colors.DarkRed, blockAddr);
        AddColorItem(miColor, "Green", Color.FromRgb(0x26, 0x4F, 0x26), blockAddr);
        AddColorItem(miColor, "Blue", Color.FromRgb(0x1E, 0x3A, 0x5F), blockAddr);
        AddColorItem(miColor, "Yellow", Color.FromRgb(0x5F, 0x5A, 0x1E), blockAddr);
        AddColorItem(miColor, "Purple", Color.FromRgb(0x3A, 0x1E, 0x5F), blockAddr);
        AddColorItem(miColor, "Orange", Color.FromRgb(0x5F, 0x3A, 0x1E), blockAddr);
        AddColorItem(miColor, "Cyan", Color.FromRgb(0x1E, 0x4F, 0x5F), blockAddr);

        var miReset = new MenuItem { Header = "Reset" };
        miReset.Click += (_, _) => { _blockColors.Remove(blockAddr); Rerender(); };
        miColor.Items.Add(new Separator());
        miColor.Items.Add(miReset);
        menu.Items.Add(miColor);

        // ── Collapse / Expand ───────────────────────────────────────────
        bool isCollapsed = _collapsedBlocks.Contains(blockAddr);
        var miCollapse = new MenuItem { Header = isCollapsed ? "Expand Block" : "Collapse Block" };
        miCollapse.Click += (_, _) =>
        {
            if (isCollapsed) _collapsedBlocks.Remove(blockAddr);
            else _collapsedBlocks.Add(blockAddr);
            Rerender();
        };
        menu.Items.Add(miCollapse);

        // ── Graph called functions ──────────────────────────────────────
        var callInstrs = block.Instructions
            .Where(i => i.IsCall && i.BranchTarget != 0 && i.ResolvedSymbol != null)
            .ToList();
        if (callInstrs.Count > 0)
        {
            menu.Items.Add(new Separator());
            foreach (var ci in callInstrs)
            {
                var targetAddr = ci.BranchTarget;
                var sym = ci.ResolvedSymbol!;
                var mi = new MenuItem { Header = $"Graph: {sym}" };
                mi.Click += (_, _) => NavigateToFunction(targetAddr, sym);
                menu.Items.Add(mi);
            }
        }

        menu.Items.Add(new Separator());

        // ── Copy ────────────────────────────────────────────────────────
        var miCopyAddr = new MenuItem { Header = "Copy Address" };
        miCopyAddr.Click += (_, _) => Clipboard.SetText($"0x{blockAddr:X}");
        menu.Items.Add(miCopyAddr);

        var miCopyAsm = new MenuItem { Header = "Copy Assembly" };
        miCopyAsm.Click += (_, _) =>
        {
            var sb = new System.Text.StringBuilder();
            foreach (var instr in block.Instructions)
                sb.AppendLine($"{instr.AddressHex(_api.Is32Bit)}  {instr.Text}");
            Clipboard.SetText(sb.ToString());
        };
        menu.Items.Add(miCopyAsm);

        menu.Items.Add(new Separator());

        // ── Annotate ────────────────────────────────────────────────────
        var miAnnotate = new MenuItem { Header = "Add Comment" };
        miAnnotate.Click += (_, _) =>
        {
            var existing = _api.UI.GetAddressAnnotation(blockAddr) ?? "";
            var dlg = new Window
            {
                Title = "Block Comment", Width = 400, Height = 150,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize
            };
            var sp = new StackPanel { Margin = new Thickness(10) };
            sp.Children.Add(new TextBlock { Text = $"Comment for 0x{blockAddr:X}:" });
            var tb = new TextBox { Text = existing, Margin = new Thickness(0, 5, 0, 5) };
            sp.Children.Add(tb);
            var btnOk = new Button { Content = "OK", Width = 80, HorizontalAlignment = HorizontalAlignment.Right };
            btnOk.Click += (_, _) => { dlg.DialogResult = true; dlg.Close(); };
            sp.Children.Add(btnOk);
            dlg.Content = sp;
            tb.Focus();
            tb.SelectAll();
            if (dlg.ShowDialog() == true && !string.IsNullOrEmpty(tb.Text))
            {
                _api.UI.SetAddressAnnotation(blockAddr, tb.Text);
                _api.UI.RefreshDisassembly();
                _statusText.Text = $"Comment set at 0x{blockAddr:X}";
            }
        };
        menu.Items.Add(miAnnotate);

        // ── Breakpoint ──────────────────────────────────────────────────
        var miBp = new MenuItem { Header = "Toggle Breakpoint at Block Start" };
        miBp.Click += (_, _) =>
        {
            var bps = _api.Breakpoints.GetAll();
            var existing = bps.FirstOrDefault(bp => bp.Address == blockAddr);
            if (existing != null)
            {
                _api.Breakpoints.RemoveBreakpoint(existing.Handle);
                _statusText.Text = $"Breakpoint removed at 0x{blockAddr:X}";
            }
            else
            {
                _api.Breakpoints.SetBreakpoint(_api.TargetPid, 0, blockAddr, PluginBreakpointType.Software);
                _statusText.Text = $"Breakpoint set at 0x{blockAddr:X}";
            }
        };
        menu.Items.Add(miBp);

        // ── Expand/collapse all ─────────────────────────────────────────
        menu.Items.Add(new Separator());
        var miCollapseAll = new MenuItem { Header = "Collapse All Blocks" };
        miCollapseAll.Click += (_, _) =>
        {
            foreach (var b in _blocks) _collapsedBlocks.Add(b.StartAddress);
            Rerender();
        };
        menu.Items.Add(miCollapseAll);

        var miExpandAll = new MenuItem { Header = "Expand All Blocks" };
        miExpandAll.Click += (_, _) => { _collapsedBlocks.Clear(); Rerender(); };
        menu.Items.Add(miExpandAll);

        var miResetColors = new MenuItem { Header = "Reset All Colors" };
        miResetColors.Click += (_, _) => { _blockColors.Clear(); Rerender(); };
        menu.Items.Add(miResetColors);

        menu.Items.Add(new Separator());
        var miGoBack = new MenuItem
        {
            Header = $"Go Back ({_navigationStack.Count} in history) [Shift+Esc]"
        };
        miGoBack.Click += (_, _) => GoBack();
        menu.Items.Add(miGoBack);

        _container.ContextMenu = menu;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void AddColorItem(MenuItem parent, string name, Color color, ulong blockAddr)
    {
        var mi = new MenuItem
        {
            Header = name,
            Icon = new System.Windows.Shapes.Rectangle
            {
                Width = 14, Height = 14,
                Fill = new SolidColorBrush(color),
                RadiusX = 2, RadiusY = 2
            }
        };
        mi.Click += (_, _) => { _blockColors[blockAddr] = color; Rerender(); };
        parent.Items.Add(mi);
    }

    private void Rerender()
    {
        if (_blocks.Count == 0) return;
        var annotations = _api.UI.GetAllAnnotations();
        _callTargets = new List<GraphRenderer.CallTargetHit>();
        _hitMap = _renderer.Render(_canvas, _blocks, _api.Is32Bit, _lastRip, _blockColors, _collapsedBlocks, annotations, _callTargets);
    }

    // ── Zoom/Pan helpers ─────────────────────────────────────────────────────

    private void Zoom(double factor)
    {
        // Zoom relative to center of container
        // Transform order: translate first, then scale
        double oldScale = _scaleTransform.ScaleX;
        double newScale = Math.Clamp(oldScale * factor, 0.1, 5.0);

        double centerX = _container.ActualWidth / 2;
        double centerY = _container.ActualHeight / 2;

        _translateTransform.X += centerX * (1.0 / newScale - 1.0 / oldScale);
        _translateTransform.Y += centerY * (1.0 / newScale - 1.0 / oldScale);

        _scaleTransform.ScaleX = newScale;
        _scaleTransform.ScaleY = newScale;
    }

    private void ResetZoom()
    {
        _scaleTransform.ScaleX = 1;
        _scaleTransform.ScaleY = 1;
        _translateTransform.X = 0;
        _translateTransform.Y = 0;
    }

    private void FitToView()
    {
        if (_canvas.Width <= 0 || _canvas.Height <= 0) return;
        double scaleX = _container.ActualWidth / _canvas.Width;
        double scaleY = _container.ActualHeight / _canvas.Height;
        double scale = Math.Min(scaleX, scaleY) * 0.9;
        scale = Math.Clamp(scale, 0.1, 5.0);
        _scaleTransform.ScaleX = scale;
        _scaleTransform.ScaleY = scale;
        // Center the graph
        // screenPos = (canvasPos + tx) * scale, so tx = screenCenter/scale - canvasCenter
        _translateTransform.X = (_container.ActualWidth / scale - _canvas.Width) / 2;
        _translateTransform.Y = (_container.ActualHeight / scale - _canvas.Height) / 2;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private bool CheckState()
    {
        if (!_api.IsConnected || !_api.IsBreakState)
        {
            _statusText.Text = "Error: Not connected or not in break state.";
            return false;
        }
        return true;
    }

    private static Button MakeButton(string text, RoutedEventHandler click)
    {
        var btn = new Button
        {
            Content = text,
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(0, 0, 4, 0),
            BorderThickness = new Thickness(0)
        };
        btn.SetResourceReference(Button.BackgroundProperty, "PluginButtonBgBrush");
        btn.SetResourceReference(Button.ForegroundProperty, "PluginFgBrush");
        btn.SetResourceReference(Button.BorderBrushProperty, "PluginBorderBrush");
        btn.Click += click;
        return btn;
    }

    private static Border MakeSeparator()
    {
        var sep = new Border
        {
            Width = 1,
            Margin = new Thickness(4, 2, 4, 2),
            VerticalAlignment = VerticalAlignment.Stretch
        };
        sep.SetResourceReference(Border.BackgroundProperty, "PluginBorderBrush");
        return sep;
    }
}
