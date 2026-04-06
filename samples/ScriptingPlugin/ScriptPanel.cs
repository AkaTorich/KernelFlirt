using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using KernelFlirt.SDK;

namespace ScriptingPlugin;

/// <summary>
/// WPF panel for the "Scripting" tab.
/// Top: code editor (AvalonEdit with C# syntax highlighting).
/// Bottom: output log.
/// Toolbar: Run, Stop, Clear, Load, Save, Reset.
/// </summary>
public sealed class ScriptPanel : Grid
{
    private readonly IDebuggerApi _api;
    private readonly ScriptEngine _engine;
    private readonly TextEditor _editor;
    private readonly TextBox _output;
    private readonly TextBlock _statusText;
    private CancellationTokenSource? _cts;
    private bool _isRunning;

    // Script history for quick re-run
    private readonly List<string> _history = new();
    private int _historyIndex = -1;

    public ScriptPanel(IDebuggerApi api)
    {
        _api = api;
        _engine = new ScriptEngine(api, PrintToOutput);

        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });       // toolbar
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) }); // editor
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });       // splitter
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) }); // output
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });       // status

        Margin = new Thickness(4);
        SetResourceReference(BackgroundProperty, "PluginBgBrush");

        // ── Row 0: Toolbar ──────────────────────────────────────────────────
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 4)
        };

        toolbar.Children.Add(MakeButton("Run (Shift+F5)", OnRun));
        toolbar.Children.Add(MakeButton("Stop", OnStop));
        toolbar.Children.Add(MakeSeparator());
        toolbar.Children.Add(MakeButton("Clear Output", OnClearOutput));
        toolbar.Children.Add(MakeButton("Reset State", OnReset));
        toolbar.Children.Add(MakeSeparator());
        toolbar.Children.Add(MakeButton("Load...", OnLoad));
        toolbar.Children.Add(MakeButton("Save...", OnSave));

        var helpText = new TextBlock
        {
            Text = "Globals: api, print(), ReadMem(), WriteMem(), ReadString(), ReadPtr(), Reg(), RIP, RSP, Sym(), Addr()",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            FontSize = 10.5,
            TextWrapping = TextWrapping.NoWrap
        };
        helpText.SetResourceReference(TextBlock.ForegroundProperty, "PluginFgDimBrush");
        toolbar.Children.Add(helpText);

        SetRow(toolbar, 0);
        Children.Add(toolbar);

        // ── Row 1: Code editor (AvalonEdit) ─────────────────────────────────
        _editor = new TextEditor
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            ShowLineNumbers = true,
            WordWrap = false,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4),
            SyntaxHighlighting = LoadDarkHighlighting(),
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xDC)),
            LineNumbersForeground = new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x5A)),
        };
        _editor.TextArea.TextView.LinkTextForegroundBrush = new SolidColorBrush(Color.FromRgb(0x56, 0x9C, 0xD6));
        _editor.TextArea.SelectionBrush = new SolidColorBrush(Color.FromArgb(0x80, 0x26, 0x4F, 0x78));
        _editor.TextArea.SelectionForeground = null;
        _editor.TextArea.TextView.CurrentLineBackground = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));
        _editor.TextArea.TextView.CurrentLineBorder = new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), 1);
        _editor.SetResourceReference(TextEditor.BorderBrushProperty, "PluginBorderBrush");

        _editor.Text = "// C# scripting — full access to IDebuggerApi\n"
                      + "// Variables persist between runs (REPL)\n"
                      + "// Shift+F5 or Ctrl+Enter = Run\n"
                      + "\n"
                      + "var regs = api.Memory.ReadRegisters(api.TargetPid, api.SelectedThreadId);\n"
                      + "foreach (var r in regs.Where(r => !r.IsFlag))\n"
                      + "    print($\"{r.Name,-4} = 0x{r.Value:X016}\");\n";

        _editor.TextArea.PreviewKeyDown += OnEditorKeyDown;

        SetRow(_editor, 1);
        Children.Add(_editor);

        // ── Row 2: Splitter ─────────────────────────────────────────────────
        var splitter = new GridSplitter
        {
            Height = 4,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center
        };
        splitter.SetResourceReference(GridSplitter.BackgroundProperty, "PluginBorderBrush");
        SetRow(splitter, 2);
        Children.Add(splitter);

        // ── Row 3: Output ───────────────────────────────────────────────────
        _output = new TextBox
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4)
        };
        _output.SetResourceReference(TextBox.BackgroundProperty, "PluginControlBgBrush");
        _output.SetResourceReference(TextBox.ForegroundProperty, "PluginFgBrush");
        _output.SetResourceReference(TextBox.BorderBrushProperty, "PluginBorderBrush");

        SetRow(_output, 3);
        Children.Add(_output);

        // ── Row 4: Status bar ───────────────────────────────────────────────
        _statusText = new TextBlock
        {
            Text = "Ready",
            Margin = new Thickness(0, 4, 0, 0),
            FontSize = 11
        };
        _statusText.SetResourceReference(TextBlock.ForegroundProperty, "PluginFgDimBrush");
        SetRow(_statusText, 4);
        Children.Add(_statusText);
    }

    // ── Event handlers ───────────────────────────────────────────────────────

    private void OnEditorKeyDown(object sender, KeyEventArgs e)
    {
        // Shift+F5 or Ctrl+Enter = Run
        if ((e.Key == Key.F5 && Keyboard.Modifiers == ModifierKeys.Shift) ||
            (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control))
        {
            e.Handled = true;
            RunScript();
        }
    }

    private void OnRun(object sender, RoutedEventArgs e) => RunScript();

    private void OnStop(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        _statusText.Text = "Cancelled.";
    }

    private void OnClearOutput(object sender, RoutedEventArgs e)
    {
        _output.Clear();
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        _engine.Reset();
        _output.AppendText("--- State reset ---\n");
        _statusText.Text = "State cleared. All variables reset.";
    }

    private void OnLoad(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "C# Scripts (*.csx;*.cs)|*.csx;*.cs|All Files (*.*)|*.*",
            Title = "Load Script"
        };
        if (dlg.ShowDialog() == true)
        {
            _editor.Text = File.ReadAllText(dlg.FileName);
            _statusText.Text = $"Loaded: {dlg.FileName}";
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "C# Scripts (*.csx)|*.csx|C# Files (*.cs)|*.cs|All Files (*.*)|*.*",
            Title = "Save Script",
            DefaultExt = ".csx"
        };
        if (dlg.ShowDialog() == true)
        {
            File.WriteAllText(dlg.FileName, _editor.Text);
            _statusText.Text = $"Saved: {dlg.FileName}";
        }
    }

    // ── Script execution ─────────────────────────────────────────────────────

    private async void RunScript()
    {
        if (_isRunning) return;

        var code = _editor.TextArea.Selection.Length > 0
            ? _editor.TextArea.Selection.GetText()
            : _editor.Text;

        if (string.IsNullOrWhiteSpace(code)) return;

        _isRunning = true;
        _cts = new CancellationTokenSource();
        _statusText.Text = "Running...";

        // Add separator
        _output.AppendText($">>> {DateTime.Now:HH:mm:ss}\n");

        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var result = await Task.Run(() => _engine.ExecuteAsync(code, _cts.Token));
            sw.Stop();

            if (!string.IsNullOrEmpty(result))
            {
                _output.AppendText(result);
                if (!result.EndsWith('\n'))
                    _output.AppendText("\n");
            }

            _statusText.Text = $"Done ({sw.ElapsedMilliseconds} ms)";

            // Save to history
            _history.Add(code);
            _historyIndex = _history.Count;
        }
        catch (OperationCanceledException)
        {
            _output.AppendText("[Cancelled]\n");
            _statusText.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            _output.AppendText($"Error: {ex.Message}\n");
            _statusText.Text = "Error.";
        }
        finally
        {
            _isRunning = false;
            _cts = null;
            _output.ScrollToEnd();
        }
    }

    private void PrintToOutput(string text)
    {
        if (Dispatcher.CheckAccess())
        {
            _output.AppendText(text + "\n");
            _output.ScrollToEnd();
        }
        else
        {
            Dispatcher.InvokeAsync(() =>
            {
                _output.AppendText(text + "\n");
                _output.ScrollToEnd();
            });
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

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

    /// <summary>
    /// Load the built-in C# highlighting and recolor it for dark background.
    /// </summary>
    private static IHighlightingDefinition? LoadDarkHighlighting()
    {
        try
        {
            var def = HighlightingManager.Instance.GetDefinition("C#");
            if (def == null) return null;

            // Recolor named colors for dark theme (VS Code Dark+ palette)
            foreach (var color in def.NamedHighlightingColors)
            {
                var n = color.Name?.ToLowerInvariant() ?? "";
                if (n.Contains("comment"))
                    color.Foreground = MakeHighlightBrush(0x6A, 0x99, 0x55);
                else if (n.Contains("string") || n.Contains("char"))
                    color.Foreground = MakeHighlightBrush(0xCE, 0x91, 0x78);
                else if (n.Contains("number") || n.Contains("digit"))
                    color.Foreground = MakeHighlightBrush(0xB5, 0xCE, 0xA8);
                else if (n.Contains("preprocess") || n.Contains("region"))
                    color.Foreground = MakeHighlightBrush(0xC5, 0x86, 0xC0);
                else if (n.Contains("keyword") || n.Contains("modifier") || n.Contains("access")
                      || n.Contains("visibility") || n.Contains("type") || n.Contains("value")
                      || n.Contains("true") || n.Contains("false") || n.Contains("null")
                      || n.Contains("namespace") || n.Contains("reference"))
                    color.Foreground = MakeHighlightBrush(0x56, 0x9C, 0xD6);
                else if (n.Contains("method") || n.Contains("function"))
                    color.Foreground = MakeHighlightBrush(0xDC, 0xDC, 0xAA);
                else if (n.Contains("punctuation") || n.Contains("bracket") || n.Contains("operator"))
                    color.Foreground = MakeHighlightBrush(0xDC, 0xDC, 0xDC);
                else
                    color.Foreground = MakeHighlightBrush(0xDC, 0xDC, 0xDC);
            }

            return def;
        }
        catch
        {
            return HighlightingManager.Instance.GetDefinition("C#");
        }
    }

    private static ICSharpCode.AvalonEdit.Highlighting.SimpleHighlightingBrush MakeHighlightBrush(byte r, byte g, byte b)
        => new(Color.FromRgb(r, g, b));
}
