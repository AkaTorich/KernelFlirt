using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
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
        };
        _editor.SetResourceReference(TextEditor.BackgroundProperty, "ScriptBgBrush");
        _editor.SetResourceReference(TextEditor.ForegroundProperty, "ScriptFgBrush");
        _editor.SetResourceReference(TextEditor.BorderBrushProperty, "PluginBorderBrush");
        // Apply syntax highlighting from theme colors (re-apply on theme change)
        ApplyHighlighting();
        _editor.Loaded += (_, _) => ApplyHighlighting();
        // Re-apply when theme changes (ScriptBgBrush resource updates)
        _editor.Resources = new ResourceDictionary();
        var dp = System.Windows.DependencyProperty.RegisterAttached(
            "_scriptThemeWatch" + GetHashCode(), typeof(Brush), typeof(ScriptPanel),
            new PropertyMetadata(null, (_, _) => Dispatcher.InvokeAsync(ApplyHighlighting)));
        _editor.SetResourceReference(dp, "ScriptKeywordBrush");

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
    /// Build syntax highlighting from theme resource brushes (ScriptKeywordBrush, etc.).
    /// </summary>
    private void ApplyHighlighting()
    {
        try
        {
            string Col(string resKey, string fallback)
            {
                if (TryFindResource(resKey) is SolidColorBrush b)
                    return $"#{b.Color.R:X2}{b.Color.G:X2}{b.Color.B:X2}";
                return fallback;
            }

            var keyword = Col("ScriptKeywordBrush", "#569CD6");
            var control = Col("ScriptControlBrush", "#C586C0");
            var type    = Col("ScriptTypeBrush",    "#4EC9B0");
            var str     = Col("ScriptStringBrush",  "#CE9178");
            var comment = Col("ScriptCommentBrush", "#6A9955");
            var number  = Col("ScriptNumberBrush",  "#B5CEA8");
            var method  = Col("ScriptMethodBrush",  "#DCDCAA");
            var punct   = Col("ScriptPunctuationBrush", "#DCDCDC");
            var fg      = Col("ScriptFgBrush",      "#DCDCDC");

            var xshd = $"""
                <?xml version="1.0"?>
                <SyntaxDefinition name="C#-Themed" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
                  <Color name="Comment"       foreground="{comment}" />
                  <Color name="String"        foreground="{str}" />
                  <Color name="Preprocessor"  foreground="{control}" />
                  <Color name="Punctuation"   foreground="{punct}" />
                  <Color name="NumberLiteral" foreground="{number}" />
                  <Color name="Keywords"      foreground="{keyword}" fontWeight="bold" />
                  <Color name="ControlFlow"   foreground="{control}" fontWeight="bold" />
                  <Color name="ValueTypes"    foreground="{keyword}" />
                  <Color name="TypeKeywords"  foreground="{type}" />
                  <Color name="Modifiers"     foreground="{keyword}" />
                  <Color name="Visibility"    foreground="{keyword}" />
                  <Color name="TrueFalse"     foreground="{keyword}" fontWeight="bold" />
                  <Color name="NullKeyword"   foreground="{keyword}" fontWeight="bold" />
                  <Color name="MethodCall"    foreground="{method}" />
                  <Color name="Default"       foreground="{fg}" />

                  <RuleSet ignoreCase="false">
                    <Span color="Comment" begin="//" />
                    <Span color="Comment" multiline="true" begin="/\*" end="\*/" />
                    <Span color="String" begin="@&quot;" end="&quot;" />
                    <Span color="String" begin="\$&quot;" end="&quot;" />
                    <Span color="String" begin="\$@&quot;" end="&quot;" />
                    <Span color="String">
                      <Begin>"</Begin>
                      <End>"</End>
                      <RuleSet><Span begin="\\" end="." /></RuleSet>
                    </Span>
                    <Span color="String">
                      <Begin>'</Begin>
                      <End>'</End>
                      <RuleSet><Span begin="\\" end="." /></RuleSet>
                    </Span>
                    <Span color="Preprocessor" begin="\#" />

                    <Rule color="NumberLiteral">\b0[xX][0-9a-fA-F_]+[uUlL]*\b</Rule>
                    <Rule color="NumberLiteral">\b[0-9][0-9_]*\.?[0-9_]*([eE][+-]?[0-9_]+)?[fFdDmM]?\b</Rule>

                    <Keywords color="ControlFlow">
                      <Word>if</Word><Word>else</Word><Word>switch</Word><Word>case</Word>
                      <Word>for</Word><Word>foreach</Word><Word>while</Word><Word>do</Word>
                      <Word>break</Word><Word>continue</Word><Word>return</Word><Word>yield</Word>
                      <Word>throw</Word><Word>try</Word><Word>catch</Word><Word>finally</Word>
                      <Word>goto</Word><Word>when</Word>
                    </Keywords>
                    <Keywords color="TrueFalse"><Word>true</Word><Word>false</Word></Keywords>
                    <Keywords color="NullKeyword"><Word>null</Word></Keywords>
                    <Keywords color="TypeKeywords">
                      <Word>class</Word><Word>struct</Word><Word>interface</Word><Word>enum</Word>
                      <Word>record</Word><Word>delegate</Word><Word>namespace</Word>
                    </Keywords>
                    <Keywords color="ValueTypes">
                      <Word>int</Word><Word>uint</Word><Word>long</Word><Word>ulong</Word>
                      <Word>short</Word><Word>ushort</Word><Word>byte</Word><Word>sbyte</Word>
                      <Word>float</Word><Word>double</Word><Word>decimal</Word>
                      <Word>bool</Word><Word>char</Word><Word>string</Word><Word>object</Word>
                      <Word>void</Word><Word>var</Word><Word>dynamic</Word>
                    </Keywords>
                    <Keywords color="Modifiers">
                      <Word>static</Word><Word>readonly</Word><Word>const</Word><Word>ref</Word>
                      <Word>out</Word><Word>in</Word><Word>params</Word><Word>abstract</Word>
                      <Word>sealed</Word><Word>virtual</Word><Word>override</Word><Word>async</Word>
                      <Word>await</Word><Word>partial</Word><Word>unsafe</Word><Word>fixed</Word>
                    </Keywords>
                    <Keywords color="Visibility">
                      <Word>public</Word><Word>private</Word><Word>protected</Word><Word>internal</Word>
                    </Keywords>
                    <Keywords color="Keywords">
                      <Word>using</Word><Word>new</Word><Word>this</Word><Word>base</Word>
                      <Word>is</Word><Word>as</Word><Word>typeof</Word><Word>sizeof</Word>
                      <Word>nameof</Word><Word>default</Word><Word>checked</Word><Word>unchecked</Word>
                      <Word>lock</Word><Word>event</Word><Word>implicit</Word><Word>explicit</Word>
                      <Word>operator</Word><Word>where</Word><Word>select</Word><Word>from</Word>
                    </Keywords>
                    <Rule color="MethodCall">[\w]+(?=\s*\()</Rule>
                    <Rule color="Punctuation">[()\[\];,.]</Rule>
                  </RuleSet>
                </SyntaxDefinition>
                """;

            using var reader = new XmlTextReader(new StringReader(xshd));
            _editor.SyntaxHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
        }
        catch (Exception ex)
        {
            _api.Log.Error($"[Scripting] Highlighting failed: {ex.Message}");
        }
    }
}
