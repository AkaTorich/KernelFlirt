using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using KernelFlirt.SDK;
using Microsoft.Win32;

namespace NetworkMonitorPlugin;

/// <summary>
/// WPF panel for Network Monitor — live traffic view with filtering.
/// </summary>
public sealed class NetworkPanel : DockPanel
{
    private readonly IDebuggerApi _api;
    private readonly NetworkMonitorEngine _engine;
    private readonly ObservableCollection<NetEvent> _events = new();
    private readonly DataGrid _grid;
    private readonly TextBox _filterBox;
    private readonly ComboBox _dirFilter;
    private readonly Button _btnStart;
    private readonly Button _btnStop;
    private readonly TextBlock _statusText;
    private readonly TextBox _detailBox;
    private System.ComponentModel.ICollectionView? _view;

    public NetworkPanel(IDebuggerApi api)
    {
        _api = api;
        _engine = new NetworkMonitorEngine(api);
        _engine.OnNetEvent += OnNetEventReceived;

        // ── Row 0: Toolbar ──────────────────────────────────────────────────
        var toolbar = new WrapPanel { Margin = new Thickness(4) };

        _btnStart = MakeButton("Start", OnStart);
        toolbar.Children.Add(_btnStart);

        _btnStop = MakeButton("Stop", OnStop);
        _btnStop.IsEnabled = false;
        toolbar.Children.Add(_btnStop);

        toolbar.Children.Add(MakeButton("Clear", (_, _) => { _events.Clear(); }));
        toolbar.Children.Add(MakeButton("Export CSV", OnExport));

        toolbar.Children.Add(MakeSeparator());

        toolbar.Children.Add(new TextBlock
        {
            Text = "Filter:",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 4, 0)
        });
        _filterBox = new TextBox { Width = 150, Margin = new Thickness(0, 0, 8, 0) };
        _filterBox.SetResourceReference(TextBox.BackgroundProperty, "PluginControlBgBrush");
        _filterBox.SetResourceReference(TextBox.ForegroundProperty, "PluginFgBrush");
        _filterBox.TextChanged += (_, _) => _view?.Refresh();
        toolbar.Children.Add(_filterBox);

        toolbar.Children.Add(new TextBlock
        {
            Text = "Direction:",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 4, 0)
        });
        _dirFilter = new ComboBox { Width = 80, Margin = new Thickness(0, 0, 8, 0) };
        _dirFilter.Items.Add("All");
        _dirFilter.Items.Add("SEND");
        _dirFilter.Items.Add("RECV");
        _dirFilter.Items.Add("CTRL");
        _dirFilter.Items.Add("HTTP");
        _dirFilter.SelectedIndex = 0;
        _dirFilter.SelectionChanged += (_, _) => _view?.Refresh();
        toolbar.Children.Add(_dirFilter);

        _statusText = new TextBlock
        {
            Text = "Stopped",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            FontSize = 11
        };
        _statusText.SetResourceReference(TextBlock.ForegroundProperty, "PluginFgDimBrush");
        toolbar.Children.Add(_statusText);

        SetDock(toolbar, Dock.Top);
        Children.Add(toolbar);

        // ── Bottom: Detail panel ────────────────────────────────────────────
        _detailBox = new TextBox
        {
            IsReadOnly = true,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Height = 80,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(4)
        };
        _detailBox.SetResourceReference(TextBox.BackgroundProperty, "PluginControlBgBrush");
        _detailBox.SetResourceReference(TextBox.ForegroundProperty, "PluginFgBrush");
        SetDock(_detailBox, Dock.Bottom);
        Children.Add(_detailBox);

        // ── Center: DataGrid ────────────────────────────────────────────────
        _grid = new DataGrid
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

        _grid.Columns.Add(Col("#", "Index", 45));
        _grid.Columns.Add(Col("Time", "Time", 90));
        _grid.Columns.Add(Col("TID", "ThreadId", 55, "X"));
        _grid.Columns.Add(Col("Dir", "Direction", 50));
        _grid.Columns.Add(Col("Function", "Function", 180));
        _grid.Columns.Add(Col("Size", "DataSize", 55));
        _grid.Columns.Add(Col("Details", "Details", new DataGridLength(1, DataGridLengthUnitType.Star)));
        _grid.Columns.Add(Col("Return", "ReturnValue", 90));

        _grid.SelectionChanged += (_, _) =>
        {
            if (_grid.SelectedItem is NetEvent evt)
            {
                var sb = new StringBuilder();
                sb.AppendLine($"[{evt.Time}] {evt.Function} (TID: {evt.ThreadId:X})");
                sb.AppendLine($"Direction: {evt.Direction}  Socket: {evt.Socket}  Size: {evt.DataSize}");
                sb.AppendLine($"Details: {evt.Details}");
                if (!string.IsNullOrEmpty(evt.Preview))
                    sb.AppendLine($"Data: {evt.Preview}");
                if (!string.IsNullOrEmpty(evt.ReturnValue))
                    sb.AppendLine($"Return: {evt.ReturnValue}");
                _detailBox.Text = sb.ToString();
            }
        };

        Children.Add(_grid);

        _view = CollectionViewSource.GetDefaultView(_events);
        _view.Filter = FilterEvent;
        _grid.ItemsSource = _view;
    }

    public NetworkMonitorEngine Engine => _engine;

    private void OnStart(object sender, RoutedEventArgs e)
    {
        if (!_api.IsConnected || !_api.IsBreakState)
        {
            _statusText.Text = "Error: Not connected or not in break state.";
            return;
        }
        int count = _engine.Start();
        _btnStart.IsEnabled = false;
        _btnStop.IsEnabled = true;
        _statusText.Text = $"Monitoring ({count} hooks)";
        _api.Log.Info($"[NetMon] Started: {count} network API hooks installed");
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        _engine.Stop();
        _btnStart.IsEnabled = true;
        _btnStop.IsEnabled = false;
        _statusText.Text = "Stopped";
        _api.Log.Info("[NetMon] Stopped");
    }

    private void OnNetEventReceived(NetEvent evt)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _events.Add(evt);
            if (_events.Count > 10000)
                _events.RemoveAt(0);
            _grid.ScrollIntoView(evt);
            _statusText.Text = $"Monitoring — {_events.Count} events";
        });
    }

    private bool FilterEvent(object obj)
    {
        if (obj is not NetEvent evt) return false;

        if (_dirFilter.SelectedIndex > 0)
        {
            var sel = (string)_dirFilter.SelectedItem;
            if (evt.Direction != sel) return false;
        }

        var filter = _filterBox.Text.Trim();
        if (string.IsNullOrEmpty(filter)) return true;
        return evt.Function.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               evt.Details.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               evt.Preview.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            FileName = "network_trace.csv",
            Filter = "CSV (*.csv)|*.csv|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        using var writer = new StreamWriter(dlg.FileName, false, Encoding.UTF8);
        writer.WriteLine("Index,Time,TID,Direction,Function,Socket,Size,Details,Preview,Return");
        foreach (var evt in _events)
        {
            writer.Write(evt.Index); writer.Write(',');
            writer.Write(evt.Time); writer.Write(',');
            writer.Write(evt.ThreadId.ToString("X")); writer.Write(',');
            writer.Write(evt.Direction); writer.Write(',');
            writer.Write(CsvEsc(evt.Function)); writer.Write(',');
            writer.Write(evt.Socket); writer.Write(',');
            writer.Write(evt.DataSize); writer.Write(',');
            writer.Write(CsvEsc(evt.Details)); writer.Write(',');
            writer.Write(CsvEsc(evt.Preview)); writer.Write(',');
            writer.WriteLine(CsvEsc(evt.ReturnValue));
        }
        _api.Log.Info($"[NetMon] Exported {_events.Count} events to {dlg.FileName}");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static DataGridTextColumn Col(string header, string binding, double width, string? fmt = null)
    {
        var b = new Binding(binding);
        if (fmt != null) b.StringFormat = fmt;
        return new DataGridTextColumn { Header = header, Binding = b, Width = width };
    }

    private static DataGridTextColumn Col(string header, string binding, DataGridLength width)
    {
        return new DataGridTextColumn { Header = header, Binding = new Binding(binding), Width = width };
    }

    private static string CsvEsc(string s)
    {
        if (s.Contains(',') || s.Contains('"') || s.Contains('\n'))
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
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
