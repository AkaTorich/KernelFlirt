using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KernelFlirt.SDK;

namespace StringDecryptorPlugin;

/// <summary>
/// String Decryptor Plugin — sets a breakpoint on the decryption function,
/// traces to return, reads the decrypted string from RAX or a buffer pointer.
///
/// Address formats:
///   0x140001000          — absolute hex address
///   rc4_strings.exe+0x172 — module base + offset
///   mod!FuncName         — symbol name
/// </summary>
public class StringDecryptorPlugin : IKernelFlirtPlugin
{
    public string Name => "String Decryptor";
    public string Description => "Trace decryption functions and collect decrypted strings";
    public string Version => "1.1";

    private IDebuggerApi? _api;
    private DecryptorPanel? _panel;

    public void Initialize(IDebuggerApi api)
    {
        _api = api;
        _panel = new DecryptorPanel(api);
        api.UI.AddToolPanel("String Decryptor", _panel);
        api.UI.AddMenuItem("String Decryptor: Start", () =>
            Application.Current.Dispatcher.Invoke(() => _panel.StartTracing()));
        api.UI.AddMenuItem("String Decryptor: Stop", () =>
            Application.Current.Dispatcher.Invoke(() => _panel.StopTracing()));
        api.Log.Info("String Decryptor v1.1 loaded. See 'String Decryptor' tab.");
    }

    public void Shutdown()
    {
        _panel?.StopTracing();
        _api?.Log.Info("String Decryptor unloaded");
    }
}

public class DecryptedString
{
    public int Index { get; set; }
    public string CallerAddress { get; set; } = "";
    public string CallerSymbol { get; set; } = "";
    public string ResultAddress { get; set; } = "";
    public string Value { get; set; } = "";
    public string Encoding { get; set; } = "";
}

public enum ResultLocation
{
    RAX,
    RCX_Arg1,
    RDX_Arg2,
    R8_Arg3,
    StackArg,
    FixedAddr
}

public class DecryptorPanel : ScrollViewer
{
    private readonly IDebuggerApi _api;

    private readonly TextBox _txtFuncAddr;
    private readonly ComboBox _cmbResultLoc;
    private readonly TextBox _txtExtraParam;
    private readonly CheckBox _chkUnicode;
    private readonly CheckBox _chkAutoRun;
    private readonly Button _btnStart;
    private readonly Button _btnStop;
    private readonly Button _btnClear;
    private readonly Button _btnCopy;
    private readonly DataGrid _grid;
    private readonly TextBlock _lblStatus;

    private readonly List<DecryptedString> _results = [];
    private bool _tracing;
    private ulong _funcAddress;
    private uint? _entryBpHandle;
    private uint? _retBpHandle;
    private ulong _savedRetAddr;
    private ulong _savedArgBuffer;
    private int _counter;

    // Cached UI values (read on UI thread at Start, used from debug thread)
    private ResultLocation _cachedLoc;
    private string _cachedExtraParam = "";
    private bool _cachedUnicode;
    private bool _cachedAutoRun;

    public DecryptorPanel(IDebuggerApi api)
    {
        _api = api;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;

        // DockPanel: controls docked to top, DataGrid fills remaining space
        var dock = new DockPanel { Margin = new Thickness(8), LastChildFill = true };

        // Title
        var title = new TextBlock
        {
            Text = "String Decryptor",
            FontSize = 16, FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 8)
        };
        DockPanel.SetDock(title, Dock.Top);
        dock.Children.Add(title);

        // Function address
        var row1 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        row1.Children.Add(MakeLabel("Decrypt function:", 120));
        _txtFuncAddr = MakeTextBox(320, "0x140001000 | module.exe+0x1234 | mod!FuncName");
        row1.Children.Add(_txtFuncAddr);
        DockPanel.SetDock(row1, Dock.Top);
        dock.Children.Add(row1);

        // Result location
        var row2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        row2.Children.Add(MakeLabel("Result string at:", 120));
        _cmbResultLoc = MakeStyledComboBox(160,
            "RAX (return value)", "RCX (arg1 buffer)", "RDX (arg2 buffer)",
            "R8 (arg3 buffer)", "[RSP + offset]", "Fixed address");
        row2.Children.Add(_cmbResultLoc);
        _txtExtraParam = MakeTextBox(110, "offset / address");
        _txtExtraParam.Margin = new Thickness(4, 0, 0, 0);
        row2.Children.Add(_txtExtraParam);
        DockPanel.SetDock(row2, Dock.Top);
        dock.Children.Add(row2);

        // Options
        var row3 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        _chkUnicode = new CheckBox { Content = "Unicode (UTF-16)", Margin = new Thickness(0, 0, 12, 0) };
        _chkAutoRun = new CheckBox { Content = "Auto-continue after capture", IsChecked = true };
        row3.Children.Add(_chkUnicode);
        row3.Children.Add(_chkAutoRun);
        DockPanel.SetDock(row3, Dock.Top);
        dock.Children.Add(row3);

        // Buttons
        var row4 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 8) };
        _btnStart = MakeButton("Start", StartTracing);
        _btnStop = MakeButton("Stop", StopTracing);
        _btnClear = MakeButton("Clear", ClearResults);
        _btnCopy = MakeButton("Copy All", CopyResults);
        _btnStop.IsEnabled = false;
        row4.Children.Add(_btnStart);
        row4.Children.Add(_btnStop);
        row4.Children.Add(_btnClear);
        row4.Children.Add(_btnCopy);
        DockPanel.SetDock(row4, Dock.Top);
        dock.Children.Add(row4);

        // Status
        _lblStatus = new TextBlock { Text = "Idle", Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(_lblStatus, Dock.Top);
        dock.Children.Add(_lblStatus);

        // Results grid — last child, fills all remaining space
        _grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserReorderColumns = false,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        _grid.Columns.Add(new DataGridTextColumn { Header = "#", Binding = new System.Windows.Data.Binding("Index"), Width = 40 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Caller", Binding = new System.Windows.Data.Binding("CallerAddress"), Width = 140 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Symbol", Binding = new System.Windows.Data.Binding("CallerSymbol"), Width = 200 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Ptr", Binding = new System.Windows.Data.Binding("ResultAddress"), Width = 140 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Enc", Binding = new System.Windows.Data.Binding("Encoding"), Width = 50 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Decrypted String", Binding = new System.Windows.Data.Binding("Value"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });

        dock.Children.Add(_grid); // last child = fills remaining space

        Content = dock;
    }

    public void StartTracing()
    {
        if (_tracing) return;
        if (!_api.IsConnected || !_api.IsBreakState)
        {
            _api.Log.Warning("[StrDecrypt] Must be connected and in break state to start");
            return;
        }

        var addrText = _txtFuncAddr.Text.Trim();
        if (string.IsNullOrEmpty(addrText))
        {
            _api.Log.Warning("[StrDecrypt] Enter a function address or symbol name");
            return;
        }

        // Resolve address from various formats
        if (!ResolveAddress(addrText, out var addr))
            return;

        _funcAddress = addr;

        // Set BP on function entry
        _entryBpHandle = _api.Breakpoints.SetBreakpoint(_api.TargetPid, 0, _funcAddress, PluginBreakpointType.Software);
        if (_entryBpHandle == null)
        {
            _api.Log.Error($"[StrDecrypt] Failed to set breakpoint at 0x{_funcAddress:X}");
            return;
        }

        // Cache UI values for use from debug thread
        _cachedLoc = (ResultLocation)(_cmbResultLoc.SelectedIndex);
        _cachedExtraParam = _txtExtraParam.Text.Trim();
        _cachedUnicode = _chkUnicode.IsChecked == true;
        _cachedAutoRun = _chkAutoRun.IsChecked == true;

        _tracing = true;
        _api.OnDebugEventFilter += OnDebugEvent;
        _btnStart.IsEnabled = false;
        _btnStop.IsEnabled = true;
        _lblStatus.Text = $"Tracing decrypt function at 0x{_funcAddress:X}...";
        _lblStatus.ClearValue(TextBlock.ForegroundProperty);
        _api.Log.Info($"[StrDecrypt] Started tracing at 0x{_funcAddress:X}");

        if (_chkAutoRun.IsChecked == true)
            _api.Continue();
    }

    /// <summary>
    /// Resolve address from user input. Supports:
    ///   0x140001000         — absolute hex
    ///   module.exe+0x1234   — module base + hex offset
    ///   mod!FuncName        — symbol name
    /// </summary>
    private bool ResolveAddress(string input, out ulong addr)
    {
        addr = 0;

        // Format: module.exe+0x1234 or module.exe+1234
        int plusIdx = input.IndexOf('+');
        if (plusIdx > 0)
        {
            string modName = input[..plusIdx].Trim();
            string offsetStr = input[(plusIdx + 1)..].Trim();
            if (offsetStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                offsetStr = offsetStr[2..];

            if (!ulong.TryParse(offsetStr, System.Globalization.NumberStyles.HexNumber, null, out var offset))
            {
                _api.Log.Error($"[StrDecrypt] Bad offset in '{input}'");
                return false;
            }

            // Find module by name
            var modules = _api.Symbols.GetModules();
            PluginModuleInfo? found = null;
            foreach (var m in modules)
            {
                if (m.Name.Equals(modName, StringComparison.OrdinalIgnoreCase))
                { found = m; break; }
                // Also match without extension
                string nameNoExt = m.Name;
                int dot = nameNoExt.LastIndexOf('.');
                if (dot > 0) nameNoExt = nameNoExt[..dot];
                if (nameNoExt.Equals(modName, StringComparison.OrdinalIgnoreCase))
                { found = m; break; }
            }

            if (found == null)
            {
                _api.Log.Error($"[StrDecrypt] Module '{modName}' not found. Loaded modules:");
                foreach (var m in modules)
                    _api.Log.Info($"  {m.Name} @ 0x{m.BaseAddress:X}");
                return false;
            }

            addr = found.BaseAddress + offset;
            _api.Log.Info($"[StrDecrypt] Resolved {input} → {found.Name} (0x{found.BaseAddress:X}) + 0x{offset:X} = 0x{addr:X}");
            return true;
        }

        // Format: 0x140001000 or 140001000 (plain hex)
        string hex = input;
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            hex = hex[2..];

        if (ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out addr))
            return true;

        // Format: mod!FuncName (symbol)
        var resolved = _api.Symbols.ResolveNameToAddress(input);
        if (resolved != 0)
        {
            addr = resolved;
            _api.Log.Info($"[StrDecrypt] Resolved symbol '{input}' → 0x{addr:X}");
            return true;
        }

        _api.Log.Error($"[StrDecrypt] Cannot resolve '{input}'. Use: 0xADDR | module.exe+0xOFFSET | mod!Symbol");
        return false;
    }

    public void StopTracing()
    {
        if (!_tracing) return;
        _tracing = false;
        _api.OnDebugEventFilter -= OnDebugEvent;

        if (_entryBpHandle.HasValue)
        {
            _api.Breakpoints.RemoveBreakpoint(_entryBpHandle.Value);
            _entryBpHandle = null;
        }
        if (_retBpHandle.HasValue)
        {
            _api.Breakpoints.RemoveBreakpoint(_retBpHandle.Value);
            _retBpHandle = null;
        }

        _btnStart.IsEnabled = true;
        _btnStop.IsEnabled = false;
        _lblStatus.Text = $"Stopped. {_results.Count} strings captured.";
        _lblStatus.ClearValue(TextBlock.ForegroundProperty);
        _api.Log.Info($"[StrDecrypt] Stopped. {_results.Count} strings captured.");
    }

    private bool OnDebugEvent(PluginDebugEvent evt)
    {
        if (!_tracing) return false;

        if (evt.Type == PluginDebugEventType.Breakpoint && evt.Address == _funcAddress)
            return HandleFunctionEntry(evt);

        if (evt.Type == PluginDebugEventType.Breakpoint && _retBpHandle.HasValue && evt.Address == _savedRetAddr)
            return HandleFunctionReturn(evt);

        return false;
    }

    private bool HandleFunctionEntry(PluginDebugEvent evt)
    {
        var regs = _api.Memory.ReadRegisters(_api.TargetPid, evt.ThreadId);
        var rsp = regs.FirstOrDefault(r => r.Name is "RSP" or "ESP");
        if (rsp == null) return false;

        _savedArgBuffer = _cachedLoc switch
        {
            ResultLocation.RCX_Arg1 => regs.FirstOrDefault(r => r.Name is "RCX" or "ECX")?.Value ?? 0,
            ResultLocation.RDX_Arg2 => regs.FirstOrDefault(r => r.Name is "RDX" or "EDX")?.Value ?? 0,
            ResultLocation.R8_Arg3 => regs.FirstOrDefault(r => r.Name == "R8")?.Value ?? 0,
            _ => 0
        };

        int ptrSize = _api.Is32Bit ? 4 : 8;
        var retData = _api.Memory.ReadMemory(_api.TargetPid, rsp.Value, (uint)ptrSize);
        if (retData == null) return false;

        _savedRetAddr = _api.Is32Bit
            ? BitConverter.ToUInt32(retData, 0)
            : BitConverter.ToUInt64(retData, 0);

        if (_retBpHandle.HasValue)
            _api.Breakpoints.RemoveBreakpoint(_retBpHandle.Value);

        _retBpHandle = _api.Breakpoints.SetBreakpoint(_api.TargetPid, 0, _savedRetAddr, PluginBreakpointType.Software);

        _api.Continue();
        return true;
    }

    private bool HandleFunctionReturn(PluginDebugEvent evt)
    {
        if (_retBpHandle.HasValue)
        {
            _api.Breakpoints.RemoveBreakpoint(_retBpHandle.Value);
            _retBpHandle = null;
        }

        var regs = _api.Memory.ReadRegisters(_api.TargetPid, evt.ThreadId);
        var rax = regs.FirstOrDefault(r => r.Name is "RAX" or "EAX");
        var rsp = regs.FirstOrDefault(r => r.Name is "RSP" or "ESP");

        ulong strPtr = 0;

        switch (_cachedLoc)
        {
            case ResultLocation.RAX:
                strPtr = rax?.Value ?? 0;
                break;
            case ResultLocation.RCX_Arg1:
            case ResultLocation.RDX_Arg2:
            case ResultLocation.R8_Arg3:
                strPtr = _savedArgBuffer;
                break;
            case ResultLocation.StackArg:
                if (rsp != null && ulong.TryParse(_cachedExtraParam.Replace("0x", ""),
                    System.Globalization.NumberStyles.HexNumber, null, out var off))
                {
                    int ptrSize = _api.Is32Bit ? 4 : 8;
                    var ptrData = _api.Memory.ReadMemory(_api.TargetPid, rsp.Value + off, (uint)ptrSize);
                    if (ptrData != null)
                        strPtr = _api.Is32Bit ? BitConverter.ToUInt32(ptrData, 0) : BitConverter.ToUInt64(ptrData, 0);
                }
                break;
            case ResultLocation.FixedAddr:
                ulong.TryParse(_cachedExtraParam.Replace("0x", ""),
                    System.Globalization.NumberStyles.HexNumber, null, out strPtr);
                break;
        }

        if (strPtr == 0)
        {
            _api.Log.Warning($"[StrDecrypt] Return #{_counter}: NULL pointer, skipping");
            if (_cachedAutoRun)
                _api.Continue();
            return true;
        }

        var raw = _api.Memory.ReadMemory(_api.TargetPid, strPtr, 1024);
        string decoded = "";
        if (raw != null)
        {
            decoded = _cachedUnicode
                ? ReadUtf16(raw)
                : ReadAscii(raw);
        }

        var callerSym = _api.Symbols.ResolveAddress(_savedRetAddr) ?? "";

        _counter++;
        var entry = new DecryptedString
        {
            Index = _counter,
            CallerAddress = $"0x{_savedRetAddr:X}",
            CallerSymbol = callerSym,
            ResultAddress = $"0x{strPtr:X}",
            Value = decoded,
            Encoding = _cachedUnicode ? "UTF16" : "ASCII"
        };

        Application.Current.Dispatcher.Invoke(() =>
        {
            _results.Add(entry);
            _grid.ItemsSource = null;
            _grid.ItemsSource = _results;
            _lblStatus.Text = $"Tracing... {_results.Count} strings captured";
        });

        _api.Log.Info($"[StrDecrypt] #{_counter}: \"{decoded}\" @ 0x{strPtr:X} (caller: {callerSym})");

        if (_cachedAutoRun)
            _api.Continue();

        return true;
    }

    private void ClearResults()
    {
        _results.Clear();
        _counter = 0;
        _grid.ItemsSource = null;
        _grid.ItemsSource = _results;
        _lblStatus.Text = _tracing ? "Tracing..." : "Idle";
    }

    private void CopyResults()
    {
        if (_results.Count == 0) return;
        var sb = new StringBuilder();
        sb.AppendLine("#\tCaller\tSymbol\tPtr\tEnc\tDecrypted String");
        foreach (var r in _results)
            sb.AppendLine($"{r.Index}\t{r.CallerAddress}\t{r.CallerSymbol}\t{r.ResultAddress}\t{r.Encoding}\t{r.Value}");
        Clipboard.SetText(sb.ToString());
        _api.Log.Info($"[StrDecrypt] {_results.Count} entries copied to clipboard");
    }

    private static string ReadAscii(byte[] buf)
    {
        int len = Array.IndexOf(buf, (byte)0);
        if (len < 0) len = buf.Length;
        return System.Text.Encoding.ASCII.GetString(buf, 0, len);
    }

    private static string ReadUtf16(byte[] buf)
    {
        int len = buf.Length;
        for (int i = 0; i + 1 < buf.Length; i += 2)
        {
            if (buf[i] == 0 && buf[i + 1] == 0) { len = i; break; }
        }
        return System.Text.Encoding.Unicode.GetString(buf, 0, len);
    }

    private static TextBlock MakeLabel(string text, double width) => new()
    {
        Text = text, Width = width,
        VerticalAlignment = VerticalAlignment.Center,
        FontFamily = new FontFamily("Consolas"), FontSize = 12
    };

    private static TextBox MakeTextBox(double width, string hint) => new()
    {
        Width = width,
        FontFamily = new FontFamily("Consolas"), FontSize = 12,
        ToolTip = hint, Padding = new Thickness(4, 2, 4, 2)
    };

    private static ComboBox MakeStyledComboBox(double width, params string[] items)
    {
        var cmb = new ComboBox
        {
            Width = width,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            SelectedIndex = 0
        };
        foreach (var item in items) cmb.Items.Add(item);
        return cmb;
    }

    private static Button MakeButton(string text, Action onClick)
    {
        var btn = new Button
        {
            Content = text, Padding = new Thickness(12, 4, 12, 4),
            Margin = new Thickness(0, 0, 6, 0),
            FontFamily = new FontFamily("Consolas"), FontSize = 12
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }
}
