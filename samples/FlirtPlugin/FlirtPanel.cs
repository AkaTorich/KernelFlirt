using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KernelFlirt.SDK;

namespace FlirtPlugin;

/// <summary>
/// WPF panel for the "FLIRT Signatures" tab.
/// Shows scan results, allows applying/clearing function name annotations.
/// No XAML, no data binding (EnableDynamicLoading breaks reflection-based binding).
/// </summary>
public sealed class FlirtPanel : Grid
{
    private readonly IDebuggerApi _api;
    private readonly PatSignatureIndex _index;
    private readonly ListBox _resultsList;
    private readonly TextBlock _statusText;
    private readonly List<FlirtMatch> _scanResults = new();
    private readonly HashSet<ulong> _appliedAnnotations = new();

    // ── Sort state ──────────────────────────────────────────────────────────
    private enum SortColumn { Name, Address, Module, Length, Status }
    private SortColumn _sortCol = SortColumn.Name;
    private bool _sortAsc = true;
    private readonly TextBlock[] _headerTexts = new TextBlock[5];

    private static readonly string[] ColumnNames = { "Function", "Address", "Module", "Len", "Status" };
    private static readonly double[] ColumnWidths = { 320, 140, 150, 60, 100 };

    public FlirtPanel(IDebuggerApi api, PatSignatureIndex index)
    {
        _api = api;
        _index = index;

        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Margin = new Thickness(8);
        SetResourceReference(BackgroundProperty, "PluginBgBrush");

        // ── Row 0: toolbar ──────────────────────────────────────────────────
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 6)
        };

        toolbar.Children.Add(MakeButton("Scan Main Module", OnScanMain));
        toolbar.Children.Add(MakeButton("Scan All Modules", OnScanAll));
        toolbar.Children.Add(MakeSeparator());
        toolbar.Children.Add(MakeButton("Apply All", OnApplyAll));
        toolbar.Children.Add(MakeButton("Apply Selected", OnApplySelected));
        toolbar.Children.Add(MakeButton("Clear Annotations", OnClearAnnotations));
        toolbar.Children.Add(MakeSeparator());
        toolbar.Children.Add(MakeButton("Clear Results", OnClearResults));

        var sigInfo = new TextBlock
        {
            Text = $"{_index.Count} signatures loaded",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            FontSize = 11
        };
        sigInfo.SetResourceReference(TextBlock.ForegroundProperty, "PluginFgDimBrush");
        toolbar.Children.Add(sigInfo);

        SetRow(toolbar, 0);
        Children.Add(toolbar);

        // ── Row 1: column headers ───────────────────────────────────────────
        var headerGrid = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        for (int i = 0; i < ColumnWidths.Length; i++)
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColumnWidths[i]) });

        for (int i = 0; i < ColumnNames.Length; i++)
        {
            var colIdx = i;
            var tb = new TextBlock
            {
                Text = ColumnNames[i],
                FontWeight = FontWeights.Bold,
                Cursor = Cursors.Hand,
                Padding = new Thickness(2, 2, 4, 2)
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "PluginFgBrush");
            tb.MouseLeftButtonUp += (_, _) => OnHeaderClick((SortColumn)colIdx);
            SetColumn(tb, i);
            headerGrid.Children.Add(tb);
            _headerTexts[i] = tb;
        }

        UpdateHeaderArrows();
        SetRow(headerGrid, 1);
        Children.Add(headerGrid);

        // ── Row 2: results list ─────────────────────────────────────────────
        _resultsList = new ListBox
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0)
        };
        _resultsList.SetResourceReference(ListBox.BackgroundProperty, "PluginControlBgBrush");
        _resultsList.SetResourceReference(ListBox.ForegroundProperty, "PluginFgBrush");
        _resultsList.SetResourceReference(ListBox.BorderBrushProperty, "PluginBorderBrush");
        _resultsList.MouseDoubleClick += OnResultDoubleClick;

        SetRow(_resultsList, 2);
        Children.Add(_resultsList);

        // ── Row 3: status bar ───────────────────────────────────────────────
        _statusText = new TextBlock
        {
            Text = "Ready. Click 'Scan Main Module' to identify library functions.",
            Margin = new Thickness(0, 4, 0, 0),
            FontSize = 11
        };
        _statusText.SetResourceReference(TextBlock.ForegroundProperty, "PluginFgDimBrush");
        SetRow(_statusText, 3);
        Children.Add(_statusText);
    }

    // ── Scan handlers ────────────────────────────────────────────────────────

    private void OnScanMain(object sender, RoutedEventArgs e)
    {
        if (!CheckState()) return;
        _statusText.Text = "Scanning main module...";
        ClearResults();

        Task.Run(() =>
        {
            var scanner = new FlirtScanner(_api, _index);
            var results = scanner.ScanMainModule(
                (current, total) => Dispatcher.InvokeAsync(() =>
                    _statusText.Text = $"Scanning... {current}/{total} functions"));
            Dispatcher.InvokeAsync(() => ShowResults(results));
        });
    }

    private void OnScanAll(object sender, RoutedEventArgs e)
    {
        if (!CheckState()) return;
        _statusText.Text = "Scanning all modules...";
        ClearResults();

        Task.Run(() =>
        {
            var scanner = new FlirtScanner(_api, _index);
            var results = scanner.ScanAllModules(
                (current, total) => Dispatcher.InvokeAsync(() =>
                    _statusText.Text = $"Scanning... {current}/{total} functions"));
            Dispatcher.InvokeAsync(() => ShowResults(results));
        });
    }

    private void OnApplyAll(object sender, RoutedEventArgs e)
    {
        int applied = 0;
        foreach (var r in _scanResults)
        {
            if (r.AlreadyHasSymbol) continue;
            _api.UI.SetAddressAnnotation(r.Address, $"[FLIRT] {r.FunctionName}");
            _appliedAnnotations.Add(r.Address);
            applied++;
        }
        _api.UI.RefreshDisassembly();
        _statusText.Text = $"Applied {applied} annotation(s). ({_scanResults.Count - applied} skipped — already have symbols)";
    }

    private void OnApplySelected(object sender, RoutedEventArgs e)
    {
        var idx = _resultsList.SelectedIndex;
        if (idx < 0 || idx >= _scanResults.Count)
        {
            _statusText.Text = "No item selected.";
            return;
        }

        var r = _scanResults[idx];
        _api.UI.SetAddressAnnotation(r.Address, $"[FLIRT] {r.FunctionName}");
        _appliedAnnotations.Add(r.Address);
        _api.UI.RefreshDisassembly();
        _statusText.Text = $"Applied annotation: {r.FunctionName} at 0x{r.Address:X}";
    }

    private void OnClearAnnotations(object sender, RoutedEventArgs e)
    {
        int count = _appliedAnnotations.Count;
        foreach (var addr in _appliedAnnotations)
            _api.UI.SetAddressAnnotation(addr, null);
        _appliedAnnotations.Clear();
        _api.UI.RefreshDisassembly();
        _statusText.Text = $"Cleared {count} FLIRT annotation(s).";
    }

    private void OnClearResults(object sender, RoutedEventArgs e)
    {
        ClearResults();
        _statusText.Text = "Cleared.";
    }

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var idx = _resultsList.SelectedIndex;
        if (idx >= 0 && idx < _scanResults.Count)
            _api.UI.NavigateDisassembly(_scanResults[idx].Address);
    }

    // ── Sort ─────────────────────────────────────────────────────────────────

    private void OnHeaderClick(SortColumn col)
    {
        if (_scanResults.Count == 0) return;

        if (_sortCol == col)
            _sortAsc = !_sortAsc;
        else
        {
            _sortCol = col;
            _sortAsc = true;
        }

        UpdateHeaderArrows();
        ApplySort();
    }

    private void UpdateHeaderArrows()
    {
        for (int i = 0; i < ColumnNames.Length; i++)
        {
            var arrow = (SortColumn)i == _sortCol ? (_sortAsc ? " \u25B2" : " \u25BC") : "";
            _headerTexts[i].Text = ColumnNames[i] + arrow;
        }
    }

    private void ApplySort()
    {
        Comparison<FlirtMatch> cmp = _sortCol switch
        {
            SortColumn.Name    => (a, b) => string.Compare(a.FunctionName, b.FunctionName, StringComparison.OrdinalIgnoreCase),
            SortColumn.Address => (a, b) => a.Address.CompareTo(b.Address),
            SortColumn.Module  => (a, b) => string.Compare(a.ModuleName, b.ModuleName, StringComparison.OrdinalIgnoreCase),
            SortColumn.Length  => (a, b) => a.PatternLength.CompareTo(b.PatternLength),
            SortColumn.Status  => (a, b) => a.AlreadyHasSymbol.CompareTo(b.AlreadyHasSymbol),
            _                  => (a, b) => 0
        };

        _scanResults.Sort((a, b) => _sortAsc ? cmp(a, b) : cmp(b, a));
        RebuildListItems();
    }

    // ── Display ──────────────────────────────────────────────────────────────

    private bool CheckState()
    {
        if (!_api.IsConnected || !_api.IsBreakState)
        {
            _statusText.Text = "Error: Not connected or not in break state.";
            return false;
        }
        return true;
    }

    private void ClearResults()
    {
        _resultsList.Items.Clear();
        _scanResults.Clear();
    }

    private void ShowResults(List<FlirtMatch> results)
    {
        _scanResults.AddRange(results);
        ApplySort();

        int newCount = results.Count(r => !r.AlreadyHasSymbol);
        _statusText.Text = results.Count > 0
            ? $"Found {results.Count} match(es) ({newCount} new, {results.Count - newCount} already have symbols)."
            : "No signatures matched.";
    }

    private void RebuildListItems()
    {
        _resultsList.Items.Clear();
        foreach (var r in _scanResults)
        {
            var row = MakeRowGrid(
                r.FunctionName,
                $"0x{r.Address:X}",
                r.ModuleName,
                r.PatternLength.ToString(),
                r.AlreadyHasSymbol ? "Has Symbol" : "New"
            );
            _resultsList.Items.Add(new ListBoxItem { Content = row, Padding = new Thickness(0, 1, 0, 1) });
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Grid MakeRowGrid(string name, string addr, string module, string len, string status)
    {
        var grid = new Grid();
        for (int i = 0; i < ColumnWidths.Length; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColumnWidths[i]) });

        var tb0 = new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis };
        var tb1 = new TextBlock { Text = addr };
        var tb2 = new TextBlock { Text = module, TextTrimming = TextTrimming.CharacterEllipsis };
        var tb3 = new TextBlock { Text = len };
        var tb4 = new TextBlock { Text = status };

        SetColumn(tb0, 0); SetColumn(tb1, 1); SetColumn(tb2, 2); SetColumn(tb3, 3); SetColumn(tb4, 4);
        grid.Children.Add(tb0); grid.Children.Add(tb1); grid.Children.Add(tb2);
        grid.Children.Add(tb3); grid.Children.Add(tb4);

        return grid;
    }

    private static Button MakeButton(string text, RoutedEventHandler click)
    {
        var btn = new Button
        {
            Content = text,
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(0, 0, 6, 0),
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
