using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using KernelFlirt.SDK;
using Microsoft.Win32;

namespace VulnHunterPlugin;

public class VulnHunterPanel : DockPanel
{
    private readonly IDebuggerApi _api;
    private readonly ImportScanner _scanner;
    private readonly RuntimeMonitor _monitor;

    private readonly ObservableCollection<ScanResult> _scanResults = new();
    private readonly ObservableCollection<RuntimeHit> _runtimeHits = new();

    private readonly DataGrid _scanGrid;
    private readonly DataGrid _runtimeGrid;
    private readonly Button _btnMonStart;
    private readonly Button _btnMonStop;
    private readonly TextBlock _statusText;
    private readonly TextBox _filterBox;
    private ICollectionView? _scanView;
    private ICollectionView? _runtimeView;

    public bool IsMonitoring => _monitor.IsMonitoring;

    public VulnHunterPanel(IDebuggerApi api)
    {
        _api = api;
        _scanner = new ImportScanner(api);
        _monitor = new RuntimeMonitor(api);
        _monitor.OnHit += OnRuntimeHit;

        // ── Toolbar ──
        var toolbar = new WrapPanel { Margin = new Thickness(4) };

        var btnScanAll = MakeButton("Scan All Modules");
        btnScanAll.Click += (_, _) => RunScanAll();
        toolbar.Children.Add(btnScanAll);

        var btnScanMain = MakeButton("Scan Main Module");
        btnScanMain.Click += (_, _) => RunScanMain();
        toolbar.Children.Add(btnScanMain);

        var btnClearScan = MakeButton("Clear Scan");
        btnClearScan.Click += (_, _) => _scanResults.Clear();
        toolbar.Children.Add(btnClearScan);

        toolbar.Children.Add(new Separator { Width = 2, Margin = new Thickness(8, 0, 8, 0) });

        _btnMonStart = MakeButton("Start Monitor");
        _btnMonStart.Click += (_, _) => StartMonitoring();
        toolbar.Children.Add(_btnMonStart);

        _btnMonStop = MakeButton("Stop Monitor");
        _btnMonStop.IsEnabled = false;
        _btnMonStop.Click += (_, _) => StopMonitoring();
        toolbar.Children.Add(_btnMonStop);

        var btnClearLog = MakeButton("Clear Log");
        btnClearLog.Click += (_, _) => { _runtimeHits.Clear(); _monitor.ResetIndex(); };
        toolbar.Children.Add(btnClearLog);

        var btnExport = MakeButton("Export CSV");
        btnExport.Click += (_, _) => ExportCsv();
        toolbar.Children.Add(btnExport);

        toolbar.Children.Add(new TextBlock
        {
            Text = "Filter:", VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 4, 0)
        });
        _filterBox = new TextBox { Width = 120, Margin = new Thickness(0, 0, 8, 0) };
        _filterBox.TextChanged += (_, _) => { _scanView?.Refresh(); _runtimeView?.Refresh(); };
        toolbar.Children.Add(_filterBox);

        _statusText = new TextBlock
        {
            Text = "Ready", VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };
        toolbar.Children.Add(_statusText);

        SetDock(toolbar, Dock.Top);
        Children.Add(toolbar);

        // ── Tab control ──
        var tabs = new TabControl { FontFamily = new FontFamily("Consolas"), FontSize = 12 };

        // Tab 1: Static Scan
        _scanGrid = CreateScanGrid();
        tabs.Items.Add(new TabItem { Header = "Static Scan", Content = _scanGrid });

        // Tab 2: Runtime Monitor
        _runtimeGrid = CreateRuntimeGrid();
        tabs.Items.Add(new TabItem { Header = "Runtime Monitor", Content = _runtimeGrid });

        Children.Add(tabs);

        // Bind views
        _scanView = CollectionViewSource.GetDefaultView(_scanResults);
        _scanView.Filter = FilterScan;
        _scanGrid.ItemsSource = _scanView;

        _runtimeView = CollectionViewSource.GetDefaultView(_runtimeHits);
        _runtimeView.Filter = FilterRuntime;
        _runtimeGrid.ItemsSource = _runtimeView;
    }

    // ═════════════════════════════════════════════════════════════════
    //  Scan
    // ═════════════════════════════════════════════════════════════════

    private async void RunScanAll()
    {
        if (!_api.IsConnected || _api.TargetPid == 0)
        {
            _api.Log.Warning("[VulnHunter] Need connected target");
            return;
        }

        _scanResults.Clear();
        _statusText.Text = "Scanning all modules...";

        uint pid = _api.TargetPid;
        var results = await Task.Run(() => _scanner.ScanAllModules(pid));

        foreach (var r in results)
            _scanResults.Add(r);

        int critical = results.Count(r => r.Danger == DangerLevel.Critical);
        int high = results.Count(r => r.Danger == DangerLevel.High);
        _statusText.Text = $"Found {results.Count} imports ({critical} critical, {high} high)";
        _api.Log.Info($"[VulnHunter] Scan complete: {results.Count} dangerous imports found");
    }

    private async void RunScanMain()
    {
        if (!_api.IsConnected || _api.TargetPid == 0)
        {
            _api.Log.Warning("[VulnHunter] Need connected target");
            return;
        }

        _scanResults.Clear();
        _statusText.Text = "Scanning main module...";

        uint pid = _api.TargetPid;
        var results = await Task.Run(() => _scanner.ScanMainModule(pid));

        foreach (var r in results)
            _scanResults.Add(r);

        _statusText.Text = $"Found {results.Count} imports in main module";
        _api.Log.Info($"[VulnHunter] Main module scan: {results.Count} dangerous imports");
    }

    // ═════════════════════════════════════════════════════════════════
    //  Monitor
    // ═════════════════════════════════════════════════════════════════

    public void StartMonitoring()
    {
        int count = _monitor.Start();
        if (count == 0)
        {
            _api.Log.Warning("[VulnHunter] No hooks installed. Need break state.");
            return;
        }

        _btnMonStart.IsEnabled = false;
        _btnMonStop.IsEnabled = true;
        _statusText.Text = $"Monitoring {count} sinks...";
        _api.Log.Info($"[VulnHunter] Monitor started: {count} hooks");
    }

    public void StopMonitoring()
    {
        _monitor.Stop();
        _btnMonStart.IsEnabled = true;
        _btnMonStop.IsEnabled = false;
        _statusText.Text = "Monitor stopped";
        _api.Log.Info("[VulnHunter] Monitor stopped");
    }

    public bool HandleDebugEvent(PluginDebugEvent evt)
    {
        return _monitor.HandleDebugEvent(evt);
    }

    private void OnRuntimeHit(RuntimeHit hit)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            _runtimeHits.Add(hit);
            if (_runtimeHits.Count > 10000)
                _runtimeHits.RemoveAt(0);
            _runtimeGrid.ScrollIntoView(hit);

            if (hit.IsSuspicious)
            {
                _api.Log.Warning(
                    $"[VulnHunter] ⚠ SUSPICIOUS: {hit.Function}() — " +
                    $"size={hit.CopySize}, buffer~{hit.BufferEstimate} " +
                    $"at {hit.CallChain}");
            }
        });
    }

    // ═════════════════════════════════════════════════════════════════
    //  Grid factories
    // ═════════════════════════════════════════════════════════════════

    private DataGrid CreateScanGrid()
    {
        var grid = MakeBaseGrid();

        grid.Columns.Add(Col("Address", "AddressHex", 130));
        grid.Columns.Add(Col("Function", "Function", 140));
        grid.Columns.Add(Col("Caller Module", "CallerModule", 140));
        grid.Columns.Add(Col("Target DLL", "TargetModule", 130));
        grid.Columns.Add(Col("Danger", "DangerText", 70));
        grid.Columns.Add(Col("Description", "Description", new DataGridLength(1, DataGridLengthUnitType.Star)));

        grid.MouseDoubleClick += (_, _) =>
        {
            if (grid.SelectedItem is ScanResult r)
                _api.UI.NavigateDisassembly(r.Address);
        };

        var ctx = new ContextMenu();
        var miNav = new MenuItem { Header = "Go to in Disassembler" };
        miNav.Click += (_, _) => { if (grid.SelectedItem is ScanResult r) _api.UI.NavigateDisassembly(r.Address); };
        ctx.Items.Add(miNav);

        var miRip = new MenuItem { Header = "Set RIP Here (Directed Test)" };
        miRip.Click += (_, _) =>
        {
            if (grid.SelectedItem is ScanResult r && _api.IsBreakState)
            {
                _api.Memory.WriteRip(_api.TargetPid, _api.SelectedThreadId, r.Address);
                _api.Log.Info($"[VulnHunter] RIP set to {r.Address:X16} ({r.Function} in {r.CallerModule})");
            }
        };
        ctx.Items.Add(miRip);

        grid.ContextMenu = ctx;
        return grid;
    }

    private DataGrid CreateRuntimeGrid()
    {
        var grid = MakeBaseGrid();

        grid.Columns.Add(Col("#", "Index", 45));
        grid.Columns.Add(Col("Time", "Time", 85));
        grid.Columns.Add(Col("TID", "ThreadId", 55));
        grid.Columns.Add(Col("Function", "Function", 110));
        grid.Columns.Add(Col("Danger", "DangerText", 60));
        grid.Columns.Add(Col("Dest", "DestHex", 130));
        grid.Columns.Add(Col("Src", "SrcHex", 130));
        grid.Columns.Add(Col("Size", "SizeText", 60));
        grid.Columns.Add(Col("Buf Est.", "BufferText", 65));
        grid.Columns.Add(Col("Suspicious", "SuspiciousText", 80));
        grid.Columns.Add(Col("Call Chain", "CallChain", new DataGridLength(1, DataGridLengthUnitType.Star)));

        // Highlight suspicious rows
        var suspiciousStyle = new Style(typeof(DataGridRow));
        var trigger = new DataTrigger
        {
            Binding = new Binding("IsSuspicious"),
            Value = true,
        };
        trigger.Setters.Add(new Setter(DataGridRow.BackgroundProperty, new SolidColorBrush(Color.FromArgb(60, 255, 0, 0))));
        trigger.Setters.Add(new Setter(DataGridRow.FontWeightProperty, FontWeights.Bold));
        suspiciousStyle.Triggers.Add(trigger);
        grid.RowStyle = suspiciousStyle;

        grid.MouseDoubleClick += (_, _) =>
        {
            if (grid.SelectedItem is RuntimeHit h)
                _api.UI.NavigateDisassembly(h.DestAddress);
        };

        return grid;
    }

    // ═════════════════════════════════════════════════════════════════
    //  Filters
    // ═════════════════════════════════════════════════════════════════

    private bool FilterScan(object obj)
    {
        if (obj is not ScanResult r) return false;
        var f = _filterBox.Text.Trim();
        if (string.IsNullOrEmpty(f)) return true;
        return r.Function.Contains(f, StringComparison.OrdinalIgnoreCase) ||
               r.CallerModule.Contains(f, StringComparison.OrdinalIgnoreCase) ||
               r.Description.Contains(f, StringComparison.OrdinalIgnoreCase);
    }

    private bool FilterRuntime(object obj)
    {
        if (obj is not RuntimeHit h) return false;
        var f = _filterBox.Text.Trim();
        if (string.IsNullOrEmpty(f)) return true;
        return h.Function.Contains(f, StringComparison.OrdinalIgnoreCase) ||
               h.CallChain.Contains(f, StringComparison.OrdinalIgnoreCase);
    }

    // ═════════════════════════════════════════════════════════════════
    //  Export
    // ═════════════════════════════════════════════════════════════════

    private void ExportCsv()
    {
        var dlg = new SaveFileDialog
        {
            Filter = "CSV Files|*.csv",
            FileName = "vulnhunter_report.csv"
        };
        if (dlg.ShowDialog() != true) return;

        var sb = new StringBuilder();

        // Scan results
        sb.AppendLine("=== Static Scan Results ===");
        sb.AppendLine("Address,Function,CallerModule,TargetDLL,Danger,Description");
        foreach (var r in _scanResults)
            sb.AppendLine($"{r.AddressHex},{r.Function},{r.CallerModule},{r.TargetModule},{r.Danger},{r.Description}");

        sb.AppendLine();

        // Runtime hits
        sb.AppendLine("=== Runtime Hits ===");
        sb.AppendLine("#,Time,TID,Function,Danger,Dest,Src,Size,BufEst,Suspicious,CallChain");
        foreach (var h in _runtimeHits)
            sb.AppendLine($"{h.Index},{h.Time},{h.ThreadId:X},{h.Function},{h.Danger},{h.DestHex},{h.SrcHex},{h.CopySize},{h.BufferText},{h.IsSuspicious},{h.CallChain}");

        File.WriteAllText(dlg.FileName, sb.ToString());
        _api.Log.Info($"[VulnHunter] Report exported to {dlg.FileName}");
    }

    // ═════════════════════════════════════════════════════════════════
    //  Helpers
    // ═════════════════════════════════════════════════════════════════

    private static Button MakeButton(string text) => new()
    {
        Content = text,
        Padding = new Thickness(10, 4, 10, 4),
        Margin = new Thickness(0, 0, 4, 0)
    };

    private static DataGrid MakeBaseGrid() => new()
    {
        IsReadOnly = true,
        AutoGenerateColumns = false,
        SelectionMode = DataGridSelectionMode.Single,
        CanUserSortColumns = true,
        GridLinesVisibility = DataGridGridLinesVisibility.None,
        FontFamily = new FontFamily("Consolas"),
        FontSize = 12,
        HeadersVisibility = DataGridHeadersVisibility.Column
    };

    private static DataGridTextColumn Col(string header, string binding, double width) => new()
    {
        Header = header,
        Binding = new Binding(binding),
        Width = width
    };

    private static DataGridTextColumn Col(string header, string binding, DataGridLength width) => new()
    {
        Header = header,
        Binding = new Binding(binding),
        Width = width
    };
}
