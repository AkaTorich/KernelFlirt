using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KernelFlirt.SDK;

namespace SignatureDetector;

/// <summary>
/// WPF panel shown in the "Signature Detector" tab.
/// No WPF data binding — all items are built manually to avoid
/// EnableDynamicLoading reflection issues.
/// </summary>
public class SignaturePanel : Grid
{
    private readonly IDebuggerApi _api;
    private readonly List<PeidSignature> _db;
    private readonly ListBox _resultsList;
    private readonly TextBlock _statusText;
    private readonly List<ScanResult> _scanResults = new();

    // ── Sort state ──────────────────────────────────────────────────────────
    private enum SortColumn { Signature, Address, Module, Ep, Length }
    private SortColumn _sortCol = SortColumn.Signature;
    private bool _sortAsc = true;
    private readonly TextBlock[] _headerTexts = new TextBlock[5];

    private static readonly string[] ColumnNames = { "Signature", "Address", "Module", "EP", "Len" };
    private static readonly double[] ColumnWidths = { 350, 140, 150, 40, 60 };

    public SignaturePanel(IDebuggerApi api, List<PeidSignature> db)
    {
        _api = api;
        _db = db;

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
        toolbar.Children.Add(MakeButton("Clear", OnClear));

        var dbInfo = new TextBlock
        {
            Text = $"{_db.Count} signatures loaded",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            FontSize = 11
        };
        dbInfo.SetResourceReference(TextBlock.ForegroundProperty, "PluginFgDimBrush");
        toolbar.Children.Add(dbInfo);

        SetRow(toolbar, 0);
        Children.Add(toolbar);

        // ── Row 1: clickable column headers ─────────────────────────────────
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
            Text = "Ready. Click 'Scan Main Module' to detect packers/compilers.",
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
        if (!_api.IsConnected || !_api.IsBreakState)
        {
            _statusText.Text = "Error: Not connected or not in break state.";
            return;
        }
        _statusText.Text = "Scanning main module...";
        ClearResults();
        Task.Run(() =>
        {
            var scanner = new SignatureScanner(_api, _db);
            var results = scanner.ScanMainModule();
            Dispatcher.InvokeAsync(() => ShowResults(results));
        });
    }

    private void OnScanAll(object sender, RoutedEventArgs e)
    {
        if (!_api.IsConnected || !_api.IsBreakState)
        {
            _statusText.Text = "Error: Not connected or not in break state.";
            return;
        }
        _statusText.Text = "Scanning all modules...";
        ClearResults();
        Task.Run(() =>
        {
            var scanner = new SignatureScanner(_api, _db);
            var results = scanner.ScanAllModules();
            Dispatcher.InvokeAsync(() => ShowResults(results));
        });
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        ClearResults();
        _statusText.Text = "Cleared.";
    }

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var idx = _resultsList.SelectedIndex;
        if (idx >= 0 && idx < _scanResults.Count)
            _api.UI.NavigateDisassembly(_scanResults[idx].MatchAddress);
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
        Comparison<ScanResult> cmp = _sortCol switch
        {
            SortColumn.Signature => (a, b) => string.Compare(a.SignatureName, b.SignatureName, StringComparison.OrdinalIgnoreCase),
            SortColumn.Address   => (a, b) => a.MatchAddress.CompareTo(b.MatchAddress),
            SortColumn.Module    => (a, b) => string.Compare(a.ModuleName, b.ModuleName, StringComparison.OrdinalIgnoreCase),
            SortColumn.Ep        => (a, b) => a.AtEntryPoint.CompareTo(b.AtEntryPoint),
            SortColumn.Length    => (a, b) => a.PatternLength.CompareTo(b.PatternLength),
            _                    => (a, b) => 0
        };

        _scanResults.Sort((a, b) => _sortAsc ? cmp(a, b) : cmp(b, a));
        RebuildListItems();
    }

    // ── Display ──────────────────────────────────────────────────────────────

    private void ClearResults()
    {
        _resultsList.Items.Clear();
        _scanResults.Clear();
    }

    private void ShowResults(List<ScanResult> results)
    {
        _scanResults.AddRange(results);
        ApplySort();

        _statusText.Text = results.Count > 0
            ? $"Found {results.Count} match(es)."
            : "No signatures matched.";
    }

    private void RebuildListItems()
    {
        _resultsList.Items.Clear();
        foreach (var r in _scanResults)
        {
            var row = MakeRowGrid(
                r.SignatureName,
                $"0x{r.MatchAddress:X}",
                r.ModuleName,
                r.AtEntryPoint ? "EP" : "",
                r.PatternLength.ToString()
            );
            _resultsList.Items.Add(new ListBoxItem { Content = row, Padding = new Thickness(0, 1, 0, 1) });
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private Grid MakeRowGrid(string sig, string addr, string module, string ep, string len)
    {
        var grid = new Grid();
        for (int i = 0; i < ColumnWidths.Length; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColumnWidths[i]) });

        var tb0 = new TextBlock { Text = sig, TextTrimming = TextTrimming.CharacterEllipsis };
        var tb1 = new TextBlock { Text = addr };
        var tb2 = new TextBlock { Text = module, TextTrimming = TextTrimming.CharacterEllipsis };
        var tb3 = new TextBlock { Text = ep };
        var tb4 = new TextBlock { Text = len };

        SetColumn(tb0, 0); SetColumn(tb1, 1); SetColumn(tb2, 2); SetColumn(tb3, 3); SetColumn(tb4, 4);
        grid.Children.Add(tb0); grid.Children.Add(tb1); grid.Children.Add(tb2); grid.Children.Add(tb3); grid.Children.Add(tb4);

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
}
