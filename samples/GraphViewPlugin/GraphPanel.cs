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

                _hitMap = _renderer.Render(_canvas, blocks, _api.Is32Bit, currentRip);
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

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Zoom relative to mouse position
        double factor = e.Delta > 0 ? 1.15 : 1.0 / 1.15;
        double oldScale = _scaleTransform.ScaleX;
        double newScale = Math.Clamp(oldScale * factor, 0.1, 5.0);
        if (Math.Abs(newScale - oldScale) < 0.001) { e.Handled = true; return; }

        // Mouse position in container (screen) space
        var mousePos = e.GetPosition(_container);

        // Point on canvas under the mouse before zoom:
        // canvasPoint = (mousePos - translate) / oldScale
        double cx = (mousePos.X - _translateTransform.X) / oldScale;
        double cy = (mousePos.Y - _translateTransform.Y) / oldScale;

        // Apply new scale
        _scaleTransform.ScaleX = newScale;
        _scaleTransform.ScaleY = newScale;

        // Adjust translate so the same canvas point stays under mouse:
        // mousePos = canvasPoint * newScale + newTranslate
        // newTranslate = mousePos - canvasPoint * newScale
        _translateTransform.X = mousePos.X - cx * newScale;
        _translateTransform.Y = mousePos.Y - cy * newScale;

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
            _translateTransform.X = _panStartX + (pos.X - _panStart.X);
            _translateTransform.Y = _panStartY + (pos.Y - _panStart.Y);
        }
    }

    private void OnMouseRightUp(object sender, MouseButtonEventArgs e)
    {
        // Right-click on a block → navigate to that address in disassembly
        // Convert mouse position to canvas coordinates
        var mousePos = e.GetPosition(_container);
        double scale = _scaleTransform.ScaleX;
        double canvasX = (mousePos.X - _translateTransform.X) / scale;
        double canvasY = (mousePos.Y - _translateTransform.Y) / scale;
        var canvasPos = new Point(canvasX, canvasY);

        foreach (var (addr, rect) in _hitMap)
        {
            if (rect.Contains(canvasPos))
            {
                _api.UI.NavigateDisassembly(addr);
                _statusText.Text = $"Navigated to 0x{addr:X}";
                e.Handled = true;
                return;
            }
        }
    }

    // ── Zoom/Pan helpers ─────────────────────────────────────────────────────

    private void Zoom(double factor)
    {
        // Zoom relative to center of container
        double oldScale = _scaleTransform.ScaleX;
        double newScale = Math.Clamp(oldScale * factor, 0.1, 5.0);

        double centerX = _container.ActualWidth / 2;
        double centerY = _container.ActualHeight / 2;

        double cx = (centerX - _translateTransform.X) / oldScale;
        double cy = (centerY - _translateTransform.Y) / oldScale;

        _scaleTransform.ScaleX = newScale;
        _scaleTransform.ScaleY = newScale;

        _translateTransform.X = centerX - cx * newScale;
        _translateTransform.Y = centerY - cy * newScale;
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
        _translateTransform.X = (_container.ActualWidth - _canvas.Width * scale) / 2;
        _translateTransform.Y = (_container.ActualHeight - _canvas.Height * scale) / 2;
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
