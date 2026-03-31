using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using KernelFlirt.SDK;

namespace MemoryScannerPlugin;

public class ScanResult
{
    public ulong Address { get; set; }
    public string Module { get; set; } = "";
    public string Offset { get; set; } = "";
    public string Preview { get; set; } = "";

    public string AddressHex => $"{Address:X16}";
    public string Display => string.IsNullOrEmpty(Module) ? AddressHex : $"{Module}+{Offset}";
}

public class Plugin : IKernelFlirtPlugin
{
    public string Name => "Memory Scanner";
    public string Description => "AOB pattern scanner — search byte patterns with wildcards in process memory (like Cheat Engine).";
    public string Version => "1.0";

    private IDebuggerApi _api = null!;
    private TextBox _patternBox = null!;
    private ComboBox _rangeCombo = null!;
    private TextBlock _statusText = null!;
    private DataGrid _grid = null!;
    private ProgressBar _progress = null!;
    private Button _scanBtn = null!;
    private Button _stopBtn = null!;
    private CheckBox _alignCheck = null!;
    private readonly List<ScanResult> _results = [];
    private CancellationTokenSource? _cts;

    public void Initialize(IDebuggerApi api)
    {
        _api = api;
        BuildUi();
    }

    public void Shutdown() => _cts?.Cancel();

    private void BuildUi()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Row 0: pattern input
        var row0 = new DockPanel { Margin = new Thickness(4, 4, 4, 2) };
        row0.Children.Add(new TextBlock
        {
            Text = "Pattern:",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            Foreground = Brushes.LightGray
        });
        _patternBox = new TextBox
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            ToolTip = "Hex bytes with ?? wildcards:  48 8B ?? 48 83 C4 ?? C3\nOr text: \"Hello\"\nOr hex string: #48656C6C6F",
            Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x3A)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x5A)),
            Padding = new Thickness(4, 2, 4, 2)
        };
        DockPanel.SetDock(_patternBox, Dock.Top);
        row0.Children.Add(_patternBox);
        Grid.SetRow(row0, 0);
        root.Children.Add(row0);

        // Row 1: options
        var row1 = new WrapPanel { Margin = new Thickness(4, 2, 4, 2) };

        row1.Children.Add(new TextBlock { Text = "Range:", VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0), Foreground = Brushes.LightGray });
        _rangeCombo = new ComboBox { Width = 180, Margin = new Thickness(0, 0, 8, 0) };
        _rangeCombo.Items.Add("Main Module");
        _rangeCombo.Items.Add("All Modules");
        _rangeCombo.Items.Add("Full Process Memory");
        _rangeCombo.SelectedIndex = 0;
        row1.Children.Add(_rangeCombo);

        _alignCheck = new CheckBox
        {
            Content = "Align 16",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Foreground = Brushes.LightGray
        };
        row1.Children.Add(_alignCheck);

        _scanBtn = new Button { Content = "Scan", Width = 70, Padding = new Thickness(4, 2, 4, 2), Margin = new Thickness(0, 0, 4, 0) };
        _scanBtn.Click += (_, _) => StartScan();
        row1.Children.Add(_scanBtn);

        _stopBtn = new Button { Content = "Stop", Width = 70, Padding = new Thickness(4, 2, 4, 2), IsEnabled = false };
        _stopBtn.Click += (_, _) => _cts?.Cancel();
        row1.Children.Add(_stopBtn);

        Grid.SetRow(row1, 1);
        root.Children.Add(row1);

        // Row 2: progress
        _progress = new ProgressBar { Height = 4, Margin = new Thickness(4, 0, 4, 2), Visibility = Visibility.Collapsed };
        Grid.SetRow(_progress, 2);
        root.Children.Add(_progress);

        // Row 3: results grid
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

        _grid.Columns.Add(new DataGridTextColumn { Header = "Address", Binding = new Binding("AddressHex"), Width = 160 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Module", Binding = new Binding("Display"), Width = 200 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Preview", Binding = new Binding("Preview"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });

        _grid.MouseDoubleClick += (_, _) =>
        {
            if (_grid.SelectedItem is ScanResult r)
                _api.UI.NavigateDisassembly(r.Address);
        };

        var ctx = new ContextMenu();
        var goTo = new MenuItem { Header = "Go to Address" };
        goTo.Click += (_, _) => { if (_grid.SelectedItem is ScanResult r) _api.UI.NavigateDisassembly(r.Address); };
        ctx.Items.Add(goTo);

        var copyAddr = new MenuItem { Header = "Copy Address" };
        copyAddr.Click += (_, _) => { if (_grid.SelectedItem is ScanResult r) Clipboard.SetText(r.AddressHex); };
        ctx.Items.Add(copyAddr);

        var copyAll = new MenuItem { Header = "Copy All Results" };
        copyAll.Click += (_, _) =>
        {
            var sb = new StringBuilder();
            foreach (var r in _results)
                sb.AppendLine($"{r.AddressHex}\t{r.Display}\t{r.Preview}");
            Clipboard.SetText(sb.ToString());
        };
        ctx.Items.Add(copyAll);
        _grid.ContextMenu = ctx;

        Grid.SetRow(_grid, 3);
        root.Children.Add(_grid);

        // Row 4: status
        _statusText = new TextBlock { Margin = new Thickness(4), Foreground = Brushes.Gray, FontSize = 11 };
        Grid.SetRow(_statusText, 4);
        root.Children.Add(_statusText);

        _api.UI.AddToolPanel("Memory Scanner", root);
    }

    // ── Pattern parsing ──

    private static short[]? ParsePattern(string input)
    {
        input = input.Trim();

        // Text search: "Hello World"
        if (input.StartsWith('"') && input.EndsWith('"') && input.Length >= 2)
        {
            var text = input[1..^1];
            return Encoding.ASCII.GetBytes(text).Select(b => (short)b).ToArray();
        }

        // Hex string: #48656C6C6F
        if (input.StartsWith('#'))
        {
            var hex = input[1..].Replace(" ", "");
            if (hex.Length % 2 != 0) return null;
            var result = new short[hex.Length / 2];
            for (int i = 0; i < result.Length; i++)
            {
                if (!byte.TryParse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, null, out byte b))
                    return null;
                result[i] = b;
            }
            return result;
        }

        // AOB pattern: 48 8B ?? 48 83 C4 ?? C3
        var tokens = input.Split([' ', ',', '-'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return null;

        var pattern = new short[tokens.Length];
        for (int i = 0; i < tokens.Length; i++)
        {
            var t = tokens[i];
            if (t == "??" || t == "?" || t == "**")
                pattern[i] = -1; // wildcard
            else if (byte.TryParse(t, NumberStyles.HexNumber, null, out byte b))
                pattern[i] = b;
            else
                return null;
        }
        return pattern;
    }

    // ── Scanning ──

    private async void StartScan()
    {
        if (!_api.IsConnected || _api.TargetPid == 0)
        {
            _api.Log.Info("Not connected or no target process");
            return;
        }

        var pattern = ParsePattern(_patternBox.Text);
        if (pattern == null || pattern.Length == 0)
        {
            _api.Log.Warning("Invalid pattern. Use: 48 8B ?? C3, \"text\", or #hex");
            return;
        }

        _results.Clear();
        _grid.ItemsSource = null;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var pid = _api.TargetPid;
        var rangeIdx = _rangeCombo.SelectedIndex;
        var align16 = _alignCheck.IsChecked == true;
        int alignment = align16 ? 16 : 1;

        _scanBtn.IsEnabled = false;
        _stopBtn.IsEnabled = true;
        _progress.Visibility = Visibility.Visible;
        _progress.IsIndeterminate = false;
        _progress.Value = 0;

        var modules = _api.Symbols.GetModules();
        var regions = BuildScanRegions(modules, rangeIdx, pid);

        _statusText.Text = $"Scanning {regions.Count} region(s) for {pattern.Length}-byte pattern...";
        _api.Log.Info($"[Scanner] Pattern: {FormatPattern(pattern)} ({pattern.Length} bytes, {regions.Count} regions)");

        int totalFound = 0;
        int regionsDone = 0;

        await Task.Run(() =>
        {
            foreach (var (baseAddr, size, modName) in regions)
            {
                if (ct.IsCancellationRequested) break;

                // Read in chunks of 256KB
                const uint CHUNK = 256 * 1024;
                uint overlap = (uint)pattern.Length - 1;

                for (ulong offset = 0; offset < size && !ct.IsCancellationRequested; )
                {
                    uint readSize = (uint)Math.Min(CHUNK, size - offset);
                    var data = _api.Memory.ReadMemory(pid, baseAddr + offset, readSize);

                    if (data != null && data.Length > 0)
                    {
                        int limit = data.Length - pattern.Length;
                        for (int i = 0; i <= limit; i += alignment)
                        {
                            if (ct.IsCancellationRequested) break;
                            if (!MatchAt(data, i, pattern)) continue;

                            ulong hitAddr = baseAddr + offset + (ulong)i;
                            var preview = FormatPreview(data, i, Math.Min(16, data.Length - i));

                            // Resolve module
                            string mod = modName;
                            string off = "";
                            if (string.IsNullOrEmpty(mod))
                            {
                                foreach (var m in modules)
                                {
                                    if (hitAddr >= m.BaseAddress && hitAddr < m.BaseAddress + m.Size)
                                    {
                                        mod = m.Name;
                                        off = $"{hitAddr - m.BaseAddress:X}";
                                        break;
                                    }
                                }
                            }
                            else
                            {
                                var m = modules.FirstOrDefault(x => x.Name == mod);
                                if (m != null) off = $"{hitAddr - m.BaseAddress:X}";
                            }

                            var result = new ScanResult
                            {
                                Address = hitAddr,
                                Module = mod,
                                Offset = off,
                                Preview = preview
                            };

                            totalFound++;
                            Application.Current?.Dispatcher.BeginInvoke(() =>
                            {
                                _results.Add(result);
                                if (_results.Count <= 200)
                                    _statusText.Text = $"Found {_results.Count} result(s)...";
                            });

                            if (totalFound >= 10000)
                            {
                                Application.Current?.Dispatcher.BeginInvoke(() =>
                                    _api.Log.Warning("[Scanner] Limit reached (10,000 results)"));
                                goto done;
                            }
                        }
                    }

                    // Advance with overlap to catch patterns spanning chunks
                    offset += readSize > overlap ? readSize - overlap : readSize;
                }

                regionsDone++;
                var pct = regions.Count > 0 ? regionsDone * 100 / regions.Count : 0;
                Application.Current?.Dispatcher.BeginInvoke(() => _progress.Value = pct);
            }
            done:;
        }, ct);

        _grid.ItemsSource = _results;
        _scanBtn.IsEnabled = true;
        _stopBtn.IsEnabled = false;
        _progress.Visibility = Visibility.Collapsed;
        _statusText.Text = $"Done — {_results.Count} result(s)";
        _api.Log.Info($"[Scanner] Scan complete: {_results.Count} result(s)");
    }

    private List<(ulong baseAddr, ulong size, string modName)> BuildScanRegions(
        IReadOnlyList<PluginModuleInfo> modules, int rangeIdx, uint pid)
    {
        var regions = new List<(ulong, ulong, string)>();

        if (rangeIdx == 0 && modules.Count > 0)
        {
            // Main module only
            var m = modules[0];
            regions.Add((m.BaseAddress, m.Size, m.Name));
        }
        else if (rangeIdx == 1)
        {
            // All modules
            foreach (var m in modules)
                regions.Add((m.BaseAddress, m.Size, m.Name));
        }
        else
        {
            // Full process memory — probe 64KB blocks from 0x10000 to 0x7FFFFFFFFFFF
            ulong start = _api.Is32Bit ? 0x10000UL : 0x10000UL;
            ulong end = _api.Is32Bit ? 0x7FFF0000UL : 0x7FFFFFFFUL * 0x10000;
            if (end > 0x7FFFFFFFFFFUL) end = 0x7FFFFFFFFFFUL;

            // Use modules as known regions first
            foreach (var m in modules)
                regions.Add((m.BaseAddress, m.Size, m.Name));

            // Probe gaps between modules for heap/stack/mapped memory
            var sorted = modules.OrderBy(m => m.BaseAddress).ToList();
            ulong lastEnd = start;

            foreach (var m in sorted)
            {
                if (m.BaseAddress > lastEnd)
                    ProbeAndAddRegions(regions, pid, lastEnd, m.BaseAddress - lastEnd);
                lastEnd = m.BaseAddress + m.Size;
            }

            // After last module, probe up to 256MB more
            ulong probeEnd = Math.Min(lastEnd + 256 * 1024 * 1024, end);
            if (probeEnd > lastEnd)
                ProbeAndAddRegions(regions, pid, lastEnd, probeEnd - lastEnd);
        }

        return regions;
    }

    private void ProbeAndAddRegions(List<(ulong, ulong, string)> regions,
        uint pid, ulong start, ulong size)
    {
        const ulong PROBE_STEP = 0x10000; // 64KB
        ulong regionStart = 0;

        for (ulong off = 0; off < size; off += PROBE_STEP)
        {
            var probe = _api.Memory.ReadMemory(pid, start + off, 1);
            if (probe != null && probe.Length > 0)
            {
                if (regionStart == 0) regionStart = start + off;
            }
            else
            {
                if (regionStart != 0)
                {
                    regions.Add((regionStart, start + off - regionStart, ""));
                    regionStart = 0;
                }
            }
        }
        if (regionStart != 0)
            regions.Add((regionStart, start + size - regionStart, ""));
    }

    // ── Pattern matching ──

    private static bool MatchAt(byte[] data, int offset, short[] pattern)
    {
        if (offset + pattern.Length > data.Length) return false;
        for (int i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] < 0) continue; // wildcard
            if (data[offset + i] != (byte)pattern[i]) return false;
        }
        return true;
    }

    // ── Formatting ──

    private static string FormatPattern(short[] pattern)
    {
        var sb = new StringBuilder();
        foreach (var b in pattern)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(b < 0 ? "??" : $"{b:X2}");
        }
        return sb.ToString();
    }

    private static string FormatPreview(byte[] data, int offset, int count)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < count && offset + i < data.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append($"{data[offset + i]:X2}");
        }
        return sb.ToString();
    }
}
