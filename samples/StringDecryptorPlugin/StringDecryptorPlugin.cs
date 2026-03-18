using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KernelFlirt.SDK;

namespace StringDecryptorPlugin;

/// <summary>
/// String Decryptor Plugin — sets a breakpoint on the decryption function's return,
/// reads the decrypted string from the return value (RAX/EAX) or a buffer pointer,
/// and collects all results into a table.
///
/// Usage:
///   1. Break at any point (e.g. entry point)
///   2. Open the "String Decryptor" tab
///   3. Enter the address of the decrypt function (or use symbol name)
///   4. Choose where the result string lives:
///      - RAX (return value is pointer to string)
///      - [RAX] (return value is pointer to struct, string at offset 0)
///      - Stack [RSP+N] (string pointer on stack after return)
///      - Fixed buffer address
///   5. Click "Start" — plugin sets BP on function entry, traces to RET,
///      reads the decrypted string, logs it, and auto-continues
///   6. All decrypted strings appear in the table below
/// </summary>
public class StringDecryptorPlugin : IKernelFlirtPlugin
{
    public string Name => "String Decryptor";
    public string Description => "Trace decryption functions and collect decrypted strings";
    public string Version => "1.0";

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
        api.Log.Info("String Decryptor v1.0 loaded. See 'String Decryptor' tab.");
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
    RAX,        // RAX points to the string
    RCX_Arg1,   // RCX on entry was the output buffer (captured at function entry)
    RDX_Arg2,   // RDX on entry was the output buffer
    R8_Arg3,    // R8 on entry was the output buffer
    StackArg,   // [RSP + offset] after return
    FixedAddr   // hardcoded buffer address
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
    private ulong _savedArgBuffer;  // captured arg register at function entry
    private int _counter;

    // Cached UI values (read on UI thread at Start, used from debug thread)
    private ResultLocation _cachedLoc;
    private string _cachedExtraParam = "";
    private bool _cachedUnicode;
    private bool _cachedAutoRun;

    private static readonly Brush DarkBg = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x2E));
    private static readonly Brush DarkFg = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xF0));
    private static readonly Brush DimFg = new SolidColorBrush(Color.FromRgb(0x78, 0x78, 0xA0));
    private static readonly Brush AccentBg = new SolidColorBrush(Color.FromRgb(0x4A, 0x9E, 0xFF));

    public DecryptorPanel(IDebuggerApi api)
    {
        _api = api;
        Background = DarkBg;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;

        var stack = new StackPanel { Margin = new Thickness(8) };

        // Title
        stack.Children.Add(new TextBlock
        {
            Text = "String Decryptor",
            FontSize = 16, FontWeight = FontWeights.Bold,
            Foreground = AccentBg, Margin = new Thickness(0, 0, 0, 8)
        });

        // Function address
        var row1 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        row1.Children.Add(MakeLabel("Decrypt function:", 120));
        _txtFuncAddr = MakeTextBox(280, "Address or symbol (e.g. 0x140001000 or mod!DecryptStr)");
        row1.Children.Add(_txtFuncAddr);
        stack.Children.Add(row1);

        // Result location
        var row2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        row2.Children.Add(MakeLabel("Result string at:", 120));
        _cmbResultLoc = new ComboBox
        {
            Width = 160,
            Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x3A)),
            Foreground = DarkFg, FontFamily = new FontFamily("Consolas"),
            FontSize = 12
        };
        _cmbResultLoc.Items.Add("RAX (return value)");
        _cmbResultLoc.Items.Add("RCX (arg1 buffer)");
        _cmbResultLoc.Items.Add("RDX (arg2 buffer)");
        _cmbResultLoc.Items.Add("R8 (arg3 buffer)");
        _cmbResultLoc.Items.Add("[RSP + offset]");
        _cmbResultLoc.Items.Add("Fixed address");
        _cmbResultLoc.SelectedIndex = 0;
        row2.Children.Add(_cmbResultLoc);
        _txtExtraParam = MakeTextBox(110, "offset / address");
        _txtExtraParam.Margin = new Thickness(4, 0, 0, 0);
        row2.Children.Add(_txtExtraParam);
        stack.Children.Add(row2);

        // Options
        var row3 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        _chkUnicode = new CheckBox { Content = "Unicode (UTF-16)", Foreground = DarkFg, Margin = new Thickness(0, 0, 12, 0) };
        _chkAutoRun = new CheckBox { Content = "Auto-continue after capture", Foreground = DarkFg, IsChecked = true };
        row3.Children.Add(_chkUnicode);
        row3.Children.Add(_chkAutoRun);
        stack.Children.Add(row3);

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
        stack.Children.Add(row4);

        // Status
        _lblStatus = new TextBlock { Text = "Idle", Foreground = DimFg, Margin = new Thickness(0, 0, 0, 8) };
        stack.Children.Add(_lblStatus);

        // Results grid
        _grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserReorderColumns = false,
            Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x2E)),
            Foreground = DarkFg,
            RowBackground = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x2E)),
            AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x3A)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x4A)),
            GridLinesVisibility = DataGridGridLinesVisibility.None,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            MinHeight = 200
        };

        _grid.Columns.Add(new DataGridTextColumn { Header = "#", Binding = new System.Windows.Data.Binding("Index"), Width = 40 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Caller", Binding = new System.Windows.Data.Binding("CallerAddress"), Width = 140 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Symbol", Binding = new System.Windows.Data.Binding("CallerSymbol"), Width = 200 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Ptr", Binding = new System.Windows.Data.Binding("ResultAddress"), Width = 140 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Enc", Binding = new System.Windows.Data.Binding("Encoding"), Width = 50 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Decrypted String", Binding = new System.Windows.Data.Binding("Value"), Width = 400 });

        stack.Children.Add(_grid);
        Content = stack;
    }

    public void StartTracing()
    {
        if (_tracing) return;
        if (!_api.IsConnected || !_api.IsBreakState)
        {
            _api.Log.Warning("[StrDecrypt] Must be connected and in break state to start");
            return;
        }

        // Resolve function address
        var addrText = _txtFuncAddr.Text.Trim();
        if (string.IsNullOrEmpty(addrText))
        {
            _api.Log.Warning("[StrDecrypt] Enter a function address or symbol name");
            return;
        }

        if (addrText.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            addrText = addrText[2..];

        if (ulong.TryParse(addrText, System.Globalization.NumberStyles.HexNumber, null, out var addr))
        {
            _funcAddress = addr;
        }
        else
        {
            // Try symbol resolution
            var resolved = _api.Symbols.ResolveNameToAddress(_txtFuncAddr.Text.Trim());
            if (resolved == 0)
            {
                _api.Log.Error($"[StrDecrypt] Cannot resolve '{_txtFuncAddr.Text.Trim()}'");
                return;
            }
            _funcAddress = resolved;
        }

        // Set BP on function entry
        _entryBpHandle = _api.Breakpoints.SetBreakpoint(_api.TargetPid, 0, _funcAddress, PluginBreakpointType.Software);
        if (_entryBpHandle == null)
        {
            _api.Log.Error($"[StrDecrypt] Failed to set breakpoint at {_funcAddress:X}");
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
        _lblStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x50, 0xFA, 0x7B));
        _api.Log.Info($"[StrDecrypt] Started tracing at 0x{_funcAddress:X}");

        // Auto-continue so the program runs
        if (_chkAutoRun.IsChecked == true)
            _api.Continue();
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
        _lblStatus.Foreground = DimFg;
        _api.Log.Info($"[StrDecrypt] Stopped. {_results.Count} strings captured.");
    }

    private bool OnDebugEvent(PluginDebugEvent evt)
    {
        if (!_tracing) return false;

        // Hit function entry BP
        if (evt.Type == PluginDebugEventType.Breakpoint && evt.Address == _funcAddress)
        {
            return HandleFunctionEntry(evt);
        }

        // Hit return BP
        if (evt.Type == PluginDebugEventType.Breakpoint && _retBpHandle.HasValue && evt.Address == _savedRetAddr)
        {
            return HandleFunctionReturn(evt);
        }

        return false;
    }

    private bool HandleFunctionEntry(PluginDebugEvent evt)
    {
        var regs = _api.Memory.ReadRegisters(_api.TargetPid, evt.ThreadId);
        var rsp = regs.FirstOrDefault(r => r.Name is "RSP" or "ESP");
        if (rsp == null) return false;

        // Capture argument registers for buffer-based modes
        _savedArgBuffer = _cachedLoc switch
        {
            ResultLocation.RCX_Arg1 => regs.FirstOrDefault(r => r.Name is "RCX" or "ECX")?.Value ?? 0,
            ResultLocation.RDX_Arg2 => regs.FirstOrDefault(r => r.Name is "RDX" or "EDX")?.Value ?? 0,
            ResultLocation.R8_Arg3 => regs.FirstOrDefault(r => r.Name == "R8")?.Value ?? 0,
            _ => 0
        };

        // Read return address from [RSP]
        int ptrSize = _api.Is32Bit ? 4 : 8;
        var retData = _api.Memory.ReadMemory(_api.TargetPid, rsp.Value, (uint)ptrSize);
        if (retData == null) return false;

        _savedRetAddr = _api.Is32Bit
            ? BitConverter.ToUInt32(retData, 0)
            : BitConverter.ToUInt64(retData, 0);

        // Set temporary BP on return address
        if (_retBpHandle.HasValue)
            _api.Breakpoints.RemoveBreakpoint(_retBpHandle.Value);

        _retBpHandle = _api.Breakpoints.SetBreakpoint(_api.TargetPid, 0, _savedRetAddr, PluginBreakpointType.Software);

        // Auto-continue to reach the return
        _api.Continue();
        return true; // suppress UI break
    }

    private bool HandleFunctionReturn(PluginDebugEvent evt)
    {
        // Remove return BP
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

        // Read string from memory
        var raw = _api.Memory.ReadMemory(_api.TargetPid, strPtr, 1024);
        string decoded = "";
        if (raw != null)
        {
            decoded = _cachedUnicode
                ? ReadUtf16(raw)
                : ReadAscii(raw);
        }

        // Resolve caller symbol
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

        return true; // suppress UI break
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
        Text = text, Width = width, Foreground = DarkFg,
        VerticalAlignment = VerticalAlignment.Center,
        FontFamily = new FontFamily("Consolas"), FontSize = 12
    };

    private static TextBox MakeTextBox(double width, string hint) => new()
    {
        Width = width,
        Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x3A)),
        Foreground = DarkFg,
        BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x4A)),
        CaretBrush = DarkFg,
        FontFamily = new FontFamily("Consolas"), FontSize = 12,
        ToolTip = hint, Padding = new Thickness(4, 2, 4, 2)
    };

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
