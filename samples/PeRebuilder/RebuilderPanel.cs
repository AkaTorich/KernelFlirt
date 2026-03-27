using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using KernelFlirt.SDK;
using Microsoft.Win32;

namespace PeRebuilder;

/// <summary>
/// WPF panel for PE Rebuilder — shown in the "PE Rebuilder" tab.
/// All colors use SetResourceReference to theme brushes, no hardcoded colors.
/// </summary>
public sealed class RebuilderPanel : Grid
{
    private readonly IDebuggerApi _api;

    // ── Core components ───────────────────────────────────────────────────
    private ExportResolver? _resolver;
    private ImportReconstructor? _reconstructor;
    private PeDumper? _dumper;

    // ── UI controls ───────────────────────────────────────────────────────
    private readonly TextBox _oepBox;
    private readonly TextBox _iatBaseBox;
    private readonly TextBox _iatSizeBox;
    private readonly TextBox _imageBaseBox;
    private readonly TreeView _importsTree;
    private readonly TextBox _logBox;
    private readonly TextBlock _statusText;

    public RebuilderPanel(IDebuggerApi api)
    {
        _api = api;
        Margin = new Thickness(6);
        SetResourceReference(BackgroundProperty, "PluginBgBrush");

        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // row 0: fields
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // row 1: buttons
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // row 2: tree+log
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // row 3: status

        // ── Row 0: Address fields ─────────────────────────────────────────
        var fieldsPanel = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };

        _imageBaseBox = MakeField(fieldsPanel, "ImageBase:", 120);
        _oepBox       = MakeField(fieldsPanel, "OEP:", 120);
        _iatBaseBox   = MakeField(fieldsPanel, "IAT Base:", 120);
        _iatSizeBox   = MakeField(fieldsPanel, "IAT Size:", 80);

        var autoIatBtn = MakeButton("Auto IAT");
        autoIatBtn.Click += OnAutoIat;
        fieldsPanel.Children.Add(autoIatBtn);

        var getRipBtn = MakeButton("RIP → OEP");
        getRipBtn.Click += OnGetRip;
        fieldsPanel.Children.Add(getRipBtn);

        SetRow(fieldsPanel, 0);
        Children.Add(fieldsPanel);

        // ── Row 1: Action buttons ─────────────────────────────────────────
        var buttonsPanel = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };

        var scanBtn = MakeButton("Scan IAT");
        scanBtn.Click += OnScanIat;
        buttonsPanel.Children.Add(scanBtn);

        var dumpBtn = MakeButton("Dump PE");
        dumpBtn.Click += OnDumpPe;
        buttonsPanel.Children.Add(dumpBtn);

        var dumpFixBtn = MakeButton("Dump + Fix Imports");
        dumpFixBtn.Click += OnDumpAndFix;
        buttonsPanel.Children.Add(dumpFixBtn);

        var clearBtn = MakeButton("Clear");
        clearBtn.Click += (_, _) => { _importsTree!.Items.Clear(); _logBox!.Clear(); };
        buttonsPanel.Children.Add(clearBtn);

        SetRow(buttonsPanel, 1);
        Children.Add(buttonsPanel);

        // ── Row 2: Tree + Log (side by side) ──────────────────────────────
        var splitGrid = new Grid();
        splitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        splitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) }); // splitter
        splitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _importsTree = new TreeView();
        _importsTree.SetResourceReference(TreeView.BackgroundProperty, "PluginControlBgBrush");
        _importsTree.SetResourceReference(TreeView.ForegroundProperty, "PluginFgBrush");
        _importsTree.SetResourceReference(TreeView.BorderBrushProperty, "PluginBorderBrush");
        Grid.SetColumn(_importsTree, 0);
        splitGrid.Children.Add(_importsTree);

        var splitter = new GridSplitter
        {
            Width = 5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        splitter.SetResourceReference(GridSplitter.BackgroundProperty, "PluginBorderBrush");
        Grid.SetColumn(splitter, 1);
        splitGrid.Children.Add(splitter);

        _logBox = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(4)
        };
        _logBox.SetResourceReference(TextBox.BackgroundProperty, "PluginControlBgBrush");
        _logBox.SetResourceReference(TextBox.ForegroundProperty, "PluginFgBrush");
        _logBox.SetResourceReference(TextBox.BorderBrushProperty, "PluginBorderBrush");
        Grid.SetColumn(_logBox, 2);
        splitGrid.Children.Add(_logBox);

        SetRow(splitGrid, 2);
        Children.Add(splitGrid);

        // ── Row 3: Status bar ─────────────────────────────────────────────
        _statusText = new TextBlock
        {
            Text = "Ready. Set OEP and click 'Auto IAT' or enter IAT address manually.",
            Margin = new Thickness(0, 4, 0, 0),
            FontSize = 11
        };
        _statusText.SetResourceReference(TextBlock.ForegroundProperty, "PluginFgDimBrush");
        SetRow(_statusText, 3);
        Children.Add(_statusText);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private TextBox MakeField(WrapPanel parent, string label, double width)
    {
        var lbl = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 3, 0)
        };
        lbl.SetResourceReference(TextBlock.ForegroundProperty, "PluginFgBrush");
        parent.Children.Add(lbl);

        var box = new TextBox
        {
            Width = width,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
            Padding = new Thickness(3, 2, 3, 2)
        };
        box.SetResourceReference(TextBox.BackgroundProperty, "PluginControlBgBrush");
        box.SetResourceReference(TextBox.ForegroundProperty, "PluginFgBrush");
        box.SetResourceReference(TextBox.BorderBrushProperty, "PluginBorderBrush");
        parent.Children.Add(box);
        return box;
    }

    private Button MakeButton(string text)
    {
        var btn = new Button
        {
            Content = text,
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(0, 0, 4, 0)
        };
        btn.SetResourceReference(Button.BackgroundProperty, "PluginButtonBgBrush");
        btn.SetResourceReference(Button.ForegroundProperty, "PluginFgBrush");
        btn.SetResourceReference(Button.BorderBrushProperty, "PluginBorderBrush");
        return btn;
    }

    private void Log(string msg)
    {
        var ts = DateTime.Now.ToString("HH:mm:ss");
        _logBox.AppendText($"[{ts}] {msg}\n");
        _logBox.ScrollToEnd();
    }

    private void SetStatus(string text) => _statusText.Text = text;

    private ulong ParseHex(TextBox box)
    {
        string text = box.Text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            text = text[2..];
        return ulong.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out ulong val) ? val : 0;
    }

    private void EnsureResolver()
    {
        if (_resolver == null)
        {
            _resolver = new ExportResolver(_api);
            _resolver.Initialize();
            Log($"ExportResolver initialized — {_resolver.Modules.Count} modules loaded");
        }
    }

    // ── Event handlers ────────────────────────────────────────────────────

    private void OnGetRip(object sender, RoutedEventArgs e)
    {
        if (!_api.IsConnected || !_api.IsBreakState) { Log("Not in break state"); return; }

        var regs = _api.Memory.ReadRegisters(_api.TargetPid, _api.SelectedThreadId);
        if (regs == null || regs.Count == 0) { Log("Failed to read registers"); return; }

        ulong rip = regs.FirstOrDefault(r => r.Name == "RIP")?.Value
                  ?? regs.FirstOrDefault(r => r.Name == "EIP")?.Value ?? 0;
        if (rip == 0) { Log("Could not find RIP/EIP register"); return; }

        _oepBox.Text = $"0x{rip:X}";

        // Also fill ImageBase from modules
        var modules = _api.Symbols.GetModules();
        foreach (var m in modules)
        {
            if (rip >= m.BaseAddress && rip < m.BaseAddress + m.Size)
            {
                _imageBaseBox.Text = $"0x{m.BaseAddress:X}";
                Log($"RIP=0x{rip:X} in {m.Name} (base 0x{m.BaseAddress:X})");
                break;
            }
        }

        SetStatus($"OEP set to 0x{rip:X}");
    }

    private void OnAutoIat(object sender, RoutedEventArgs e)
    {
        ulong oep = ParseHex(_oepBox);
        if (oep == 0) { Log("Set OEP first (use 'RIP → OEP')"); return; }

        EnsureResolver();
        _reconstructor = new ImportReconstructor(_api, _resolver!);

        SetStatus("Auto-detecting IAT...");
        Log($"Scanning from OEP 0x{oep:X}...");

        bool found = _reconstructor.AutoDetectIat(oep);
        if (found)
        {
            _iatBaseBox.Text = $"0x{_reconstructor.IatBase:X}";
            _iatSizeBox.Text = $"0x{_reconstructor.IatSize:X}";
            Log($"IAT found: base=0x{_reconstructor.IatBase:X}, size=0x{_reconstructor.IatSize:X} " +
                $"({_reconstructor.IatSize / (_api.Is32Bit ? 4 : 8)} entries)");
            SetStatus("IAT detected. Click 'Scan IAT' to resolve imports.");
        }
        else
        {
            Log("IAT auto-detection failed — enter manually");
            SetStatus("Auto-detect failed. Enter IAT Base and Size manually.");
        }
    }

    private void OnScanIat(object sender, RoutedEventArgs e)
    {
        ulong iatBase = ParseHex(_iatBaseBox);
        int iatSize = (int)ParseHex(_iatSizeBox);
        if (iatBase == 0 || iatSize == 0) { Log("Set IAT Base and Size first"); return; }

        EnsureResolver();
        _reconstructor ??= new ImportReconstructor(_api, _resolver!);
        _reconstructor.IatBase = iatBase;
        _reconstructor.IatSize = iatSize;

        SetStatus("Scanning IAT...");
        Log("Resolving imports...");

        int resolved = _reconstructor.ScanAndResolve();
        int total = _reconstructor.Imports.Count;
        int valid = _reconstructor.Imports.Count(i => i.Valid);
        int invalid = total - valid;

        Log($"Resolved {resolved}/{total} entries ({invalid} unresolved)");

        // Build tree
        _importsTree.Items.Clear();
        var groups = _reconstructor.GroupByDll();
        foreach (var (dll, funcs) in groups)
        {
            var dllNode = new TreeViewItem
            {
                Header = $"{dll} ({funcs.Count})",
                IsExpanded = false
            };
            dllNode.SetResourceReference(TreeViewItem.ForegroundProperty, "PluginAccentBrush");

            foreach (var f in funcs)
            {
                var funcNode = new TreeViewItem
                {
                    Header = f.ByOrdinal
                        ? $"  #{f.Ordinal}  @ 0x{f.IatAddress:X}"
                        : $"  {f.FuncName}  @ 0x{f.IatAddress:X}"
                };
                funcNode.SetResourceReference(TreeViewItem.ForegroundProperty, "PluginFgBrush");
                dllNode.Items.Add(funcNode);
            }

            _importsTree.Items.Add(dllNode);
        }

        SetStatus($"{groups.Count} DLLs, {valid} imports resolved, {invalid} unresolved.");
    }

    private void OnDumpPe(object sender, RoutedEventArgs e)
    {
        DoDump(fixImports: false);
    }

    private void OnDumpAndFix(object sender, RoutedEventArgs e)
    {
        DoDump(fixImports: true);
    }

    private void DoDump(bool fixImports)
    {
        ulong imageBase = ParseHex(_imageBaseBox);
        ulong oep = ParseHex(_oepBox);
        if (imageBase == 0 || oep == 0) { Log("Set ImageBase and OEP first"); return; }

        if (fixImports && (_reconstructor == null || _reconstructor.Imports.Count == 0))
        {
            Log("Scan IAT first before fixing imports");
            return;
        }

        _dumper = new PeDumper(_api, Log);

        var imports = fixImports ? _reconstructor!.GroupByDll() : null;

        SetStatus("Dumping...");
        byte[]? pe = _dumper.Dump(imageBase, oep, imports);
        if (pe == null) { SetStatus("Dump failed — see log."); return; }

        // Save dialog
        var dlg = new SaveFileDialog
        {
            Filter = "PE files (*.exe;*.dll)|*.exe;*.dll|All files|*.*",
            FileName = "dumped.exe"
        };
        if (dlg.ShowDialog() == true)
        {
            File.WriteAllBytes(dlg.FileName, pe);
            Log($"Saved: {dlg.FileName} ({pe.Length / 1024} KB)");
            SetStatus($"Saved to {dlg.FileName}");
            _api.Log.Info($"[PeRebuilder] Dumped {pe.Length / 1024} KB → {dlg.FileName}");
        }
    }
}
