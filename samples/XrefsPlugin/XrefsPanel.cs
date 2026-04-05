using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using KernelFlirt.SDK;

namespace XrefsPlugin;

/// <summary>
/// WPF panel for cross-reference analysis.
/// Shows xrefs TO an address (who calls/references it) and FROM an address (what it calls).
/// </summary>
public sealed class XrefsPanel : Grid
{
    private readonly IDebuggerApi _api;
    private readonly XrefScanner _scanner;
    private readonly DataGrid _grid;
    private readonly TextBox _addressBox;
    private readonly TextBlock _statusText;
    private readonly ComboBox _directionBox;
    private readonly ComboBox _scopeBox;

    private CancellationTokenSource? _cts;

    public XrefsPanel(IDebuggerApi api)
    {
        _api = api;
        _scanner = new XrefScanner(api);

        Margin = new Thickness(4);
        SetResourceReference(BackgroundProperty, "PluginBgBrush");

        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });    // toolbar
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // results
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });    // status

        // -- Toolbar --
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 4)
        };

        toolbar.Children.Add(MakeLabel("Address:"));

        _addressBox = new TextBox
        {
            Width = 170,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(4, 2, 4, 2)
        };
        _addressBox.SetResourceReference(TextBox.BackgroundProperty, "PluginControlBgBrush");
        _addressBox.SetResourceReference(TextBox.ForegroundProperty, "PluginFgBrush");
        _addressBox.SetResourceReference(TextBox.BorderBrushProperty, "PluginBorderBrush");
        _addressBox.SetResourceReference(TextBox.CaretBrushProperty, "PluginFgBrush");
        _addressBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) RunScan(); };
        toolbar.Children.Add(_addressBox);

        toolbar.Children.Add(MakeButton("Xrefs at RIP", (_, _) => AnalyzeAtRip()));

        _directionBox = new ComboBox
        {
            Width = 100,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        _directionBox.Items.Add("Xrefs TO");
        _directionBox.Items.Add("Xrefs FROM");
        _directionBox.SelectedIndex = 0;
        toolbar.Children.Add(_directionBox);

        _scopeBox = new ComboBox
        {
            Width = 140,
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        _scopeBox.Items.Add("Current module");
        _scopeBox.Items.Add("All modules");
        _scopeBox.SelectedIndex = 0;
        toolbar.Children.Add(_scopeBox);

        toolbar.Children.Add(MakeButton("Scan", (_, _) => RunScan()));
        toolbar.Children.Add(MakeButton("Stop", (_, _) => _cts?.Cancel()));

        SetRow(toolbar, 0);
        Children.Add(toolbar);

        // -- Results DataGrid --
        _grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            SelectionMode = DataGridSelectionMode.Single,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.None,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            RowBackground = Brushes.Transparent,
            AlternatingRowBackground = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12
        };

        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Direction",
            Binding = new Binding("TypeStr"),
            Width = 80
        });
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Address",
            Binding = new Binding("FromHex"),
            Width = 150
        });
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Location",
            Binding = new Binding("Location"),
            Width = 200
        });
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Instruction",
            Binding = new Binding("Instruction"),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star)
        });

        _grid.MouseDoubleClick += OnGridDoubleClick;
        _grid.ContextMenu = BuildContextMenu();

        SetRow(_grid, 1);
        Children.Add(_grid);

        // -- Status --
        _statusText = new TextBlock
        {
            Text = "Enter address or click 'Xrefs at RIP' to scan.",
            Margin = new Thickness(0, 4, 0, 0),
            FontSize = 11
        };
        _statusText.SetResourceReference(TextBlock.ForegroundProperty, "PluginFgDimBrush");
        SetRow(_statusText, 2);
        Children.Add(_statusText);
    }

    public void AnalyzeAtRip()
    {
        if (!_api.IsConnected || !_api.IsBreakState)
        {
            _statusText.Text = "Not connected or not in break state.";
            return;
        }

        var regs = _api.Memory.ReadRegisters(_api.TargetPid, _api.SelectedThreadId);
        var rip = regs?.FirstOrDefault(r => r.Name == "RIP")?.Value
                ?? regs?.FirstOrDefault(r => r.Name == "EIP")?.Value
                ?? 0;
        if (rip == 0) { _statusText.Text = "Cannot read RIP."; return; }

        _addressBox.Text = $"0x{rip:X}";
        RunScan();
    }

    private void RunScan()
    {
        if (!_api.IsConnected || !_api.IsBreakState)
        {
            _statusText.Text = "Not connected or not in break state.";
            return;
        }

        ulong targetAddr = ParseAddress(_addressBox.Text);
        if (targetAddr == 0)
        {
            _statusText.Text = "Invalid address.";
            return;
        }

        bool xrefsTo = _directionBox.SelectedIndex == 0;
        bool allModules = _scopeBox.SelectedIndex == 1;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _grid.ItemsSource = null;
        _statusText.Text = "Scanning...";

        Task.Run(() =>
        {
            var results = new List<XrefResult>();

            if (xrefsTo)
            {
                // Find xrefs TO targetAddr
                var modules = _api.Symbols.GetModules();
                if (modules == null || modules.Count == 0)
                {
                    Dispatcher.InvokeAsync(() => _statusText.Text = "No modules loaded.");
                    return;
                }

                var targetModule = modules.FirstOrDefault(m =>
                    targetAddr >= m.BaseAddress && targetAddr < m.BaseAddress + m.Size);

                var scanModules = allModules
                    ? modules.ToList()
                    : (targetModule != null ? new List<PluginModuleInfo> { targetModule } : modules.Take(1).ToList());

                foreach (var mod in scanModules)
                {
                    if (ct.IsCancellationRequested) break;
                    var modResults = _scanner.FindXrefsTo(targetAddr, mod,
                        status => Dispatcher.InvokeAsync(() => _statusText.Text = status), ct);

                    // Fill in module/symbol info
                    foreach (var xr in modResults)
                    {
                        xr.FromModule = mod.Name;
                        var sym = _api.Symbols.ResolveAddress(xr.FromAddress);
                        if (sym != null) xr.FromSymbol = sym;
                    }

                    results.AddRange(modResults);
                }
            }
            else
            {
                // Find xrefs FROM targetAddr (what does this function reference?)
                results = _scanner.FindXrefsFrom(targetAddr,
                    status => Dispatcher.InvokeAsync(() => _statusText.Text = status), ct);
            }

            Dispatcher.InvokeAsync(() =>
            {
                _grid.ItemsSource = results;
                var targetSym = _api.Symbols.ResolveAddress(targetAddr);
                var label = targetSym ?? $"0x{targetAddr:X}";
                var dir = xrefsTo ? "TO" : "FROM";
                _statusText.Text = ct.IsCancellationRequested
                    ? $"Cancelled. {results.Count} xrefs {dir} {label} (partial)."
                    : $"{results.Count} xrefs {dir} {label}.";
            });
        }, ct);
    }

    private void OnGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_grid.SelectedItem is XrefResult xr)
            _api.UI.NavigateDisassembly(xr.FromAddress);
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();

        var goFrom = new MenuItem { Header = "Go to Source" };
        goFrom.Click += (_, _) =>
        {
            if (_grid.SelectedItem is XrefResult xr)
                _api.UI.NavigateDisassembly(xr.FromAddress);
        };
        menu.Items.Add(goFrom);

        var goTo = new MenuItem { Header = "Go to Target" };
        goTo.Click += (_, _) =>
        {
            if (_grid.SelectedItem is XrefResult xr)
                _api.UI.NavigateDisassembly(xr.ToAddress);
        };
        menu.Items.Add(goTo);

        menu.Items.Add(new Separator());

        var copyAddr = new MenuItem { Header = "Copy Source Address" };
        copyAddr.Click += (_, _) =>
        {
            if (_grid.SelectedItem is XrefResult xr)
                Clipboard.SetText($"0x{xr.FromAddress:X}");
        };
        menu.Items.Add(copyAddr);

        var copyAll = new MenuItem { Header = "Copy All Results" };
        copyAll.Click += (_, _) =>
        {
            if (_grid.ItemsSource is List<XrefResult> list && list.Count > 0)
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("Type\tAddress\tLocation\tInstruction");
                foreach (var xr in list)
                    sb.AppendLine($"{xr.TypeStr}\t{xr.FromHex}\t{xr.Location}\t{xr.Instruction}");
                Clipboard.SetText(sb.ToString());
            }
        };
        menu.Items.Add(copyAll);

        return menu;
    }

    private ulong ParseAddress(string text)
    {
        text = text.Trim();
        if (string.IsNullOrEmpty(text)) return 0;

        // Try as hex
        var hex = text.TrimStart('0').TrimStart('x', 'X');
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            hex = hex[2..];

        if (ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out ulong addr))
            return addr;

        // Try as symbol
        return _api.Symbols.ResolveNameToAddress(text);
    }

    private static TextBlock MakeLabel(string text)
    {
        var tb = new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 4, 0)
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "PluginFgBrush");
        return tb;
    }

    private static Button MakeButton(string text, RoutedEventHandler click)
    {
        var btn = new Button
        {
            Content = text,
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(4, 0, 0, 0),
            BorderThickness = new Thickness(0)
        };
        btn.SetResourceReference(Button.BackgroundProperty, "PluginButtonBgBrush");
        btn.SetResourceReference(Button.ForegroundProperty, "PluginFgBrush");
        btn.Click += click;
        return btn;
    }
}
