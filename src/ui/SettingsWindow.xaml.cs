using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KernelFlirt.UI;

public partial class SettingsWindow : Window
{
    private readonly Dictionary<string, string> _colors = new();
    private readonly Dictionary<string, (Button Btn, TextBlock Txt)> _ui = new();

    private static readonly string ThemesDir =
        Path.Combine(AppContext.BaseDirectory, "themes");

    public static readonly string[] TabNames =
    [
        "Disassembly", "Breakpoints", "Modules", "Kernel Modules",
        "Threads", "Call Stack", "Bookmarks", "Patches",
        "Exceptions", "Sections", "Strings", "Search",
        "Imports", "Functions", "Decompiler", "Log"
    ];

    // key -> default hex
    public static readonly Dictionary<string, string> Defaults = new()
    {
        // General
        ["Bg"]              = "#1E1E1E",
        ["BgLight"]         = "#2D2D30",
        ["BgPanel"]         = "#252526",
        ["Border"]          = "#3F3F46",
        ["Fg"]              = "#D4D4D4",
        ["FgDim"]           = "#808080",
        ["Accent"]          = "#007ACC",
        ["Selection"]       = "#264F78",
        ["Toolbar"]         = "#333337",
        ["StatusBar"]       = "#007ACC",
        ["ValueChanged"]    = "#FF6B6B",
        // Disassembly
        ["DsmAddress"]      = "#569CD6",
        ["DsmMnemonic"]     = "#DCDCAA",
        ["DsmRegister"]     = "#4EC9B0",
        ["DsmBytes"]        = "#CE9178",
        ["DsmNumber"]       = "#B5CEA8",
        ["DsmJump"]         = "#FF8080",
        ["DsmPunctuation"]  = "#808080",
        ["DsmString"]       = "#CE9178",
        ["DsmComment"]      = "#608B4E",
        ["DsmSymbol"]       = "#4EC9B0",
        ["DsmBpMarker"]     = "#E51400",
        ["DsmBpRow"]        = "#8B2020",
        ["DsmCurrentLine"]  = "#264F78",
        ["DsmFunction"]     = "#DCDCAA",
        // Stack
        ["StackOffset"]     = "#569CD6",
        ["StackAddress"]    = "#D4D4D4",
        ["StackAnnotation"] = "#4EC9B0",
        // Tab style
        ["TabBg"]           = "#2D2D30",
        ["TabFg"]           = "#808080",
        ["TabSelBg"]        = "#1E1E1E",
        ["TabSelFg"]        = "#D4D4D4",
        ["TabSelBorder"]    = "#007ACC",
        ["TabHoverBg"]      = "#3E3E42",
        // Plugin controls
        ["PluginBg"]          = "#1E1E1E",
        ["PluginFg"]          = "#D4D4D4",
        ["PluginFgDim"]       = "#808080",
        ["PluginBorder"]      = "#3F3F46",
        ["PluginAccent"]      = "#007ACC",
        ["PluginControlBg"]   = "#252526",
        ["PluginButtonBg"]    = "#2D2D30",
        ["PluginButtonHover"] = "#007ACC",
        ["PluginSelection"]   = "#264F78",
        ["PluginGridAltRow"]  = "#252526",
        ["PluginGroupHeader"] = "#2D2D30",
        ["PluginGroupBg"]     = "#252526",
    };

    // Which color keys belong to which settings tab
    private static readonly string[] GeneralKeys =
        ["Bg", "BgLight", "BgPanel", "Border", "Fg", "FgDim", "Accent", "Selection", "Toolbar", "StatusBar", "ValueChanged"];
    private static readonly string[] DisasmKeys =
        ["DsmAddress", "DsmMnemonic", "DsmRegister", "DsmBytes", "DsmNumber", "DsmJump",
         "DsmPunctuation", "DsmString", "DsmComment", "DsmSymbol", "DsmFunction",
         "DsmBpMarker", "DsmBpRow", "DsmCurrentLine"];
    private static readonly string[] StackKeys =
        ["StackOffset", "StackAddress", "StackAnnotation"];
    private static readonly string[] TabStyleKeys =
        ["TabBg", "TabFg", "TabSelBg", "TabSelFg", "TabSelBorder", "TabHoverBg"];
    private static readonly string[] PluginKeys =
        ["PluginBg", "PluginFg", "PluginFgDim", "PluginBorder", "PluginAccent",
         "PluginControlBg", "PluginButtonBg", "PluginButtonHover", "PluginSelection",
         "PluginGridAltRow", "PluginGroupHeader", "PluginGroupBg"];

    private readonly string[] _pluginTabNames;

    public Dictionary<string, string> ResultColors => _colors;

    public SettingsWindow(Dictionary<string, string>? existing = null, IEnumerable<string>? pluginTabNames = null)
    {
        InitializeComponent();
        _pluginTabNames = pluginTabNames?.ToArray() ?? [];

        // Init colors from existing or defaults
        foreach (var (key, def) in Defaults)
            _colors[key] = (existing != null && existing.TryGetValue(key, out var v)) ? v : def;

        // Per-tab colors
        foreach (var tab in TabNames)
        {
            foreach (var suffix in new[] { "Fg", "Bg" })
            {
                var key = $"Tab.{tab}.{suffix}";
                _colors[key] = (existing != null && existing.TryGetValue(key, out var val)) ? val : "";
            }
        }

        // Plugin tab colors
        foreach (var tab in _pluginTabNames)
        {
            foreach (var suffix in new[] { "Fg", "Bg" })
            {
                var key = $"Tab.{tab}.{suffix}";
                _colors[key] = (existing != null && existing.TryGetValue(key, out var val)) ? val : "";
            }
        }

        BuildGeneralPanel();
        BuildDisasmPanel();
        BuildStackPanel();
        BuildTabStylePanel();
        BuildPerTabPanel();
        BuildPluginsPanel();
    }

    // ---- Theme selector bar ----

    private StackPanel CreateThemeBar(string[] keysFilter)
    {
        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 12)
        };

        var lbl = new TextBlock
        {
            Text = "Theme:",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };

        var combo = new ComboBox { Width = 180 };
        RefreshThemeCombo(combo);

        var loadAllBtn = new Button { Content = "Load All", Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(4, 0, 0, 0) };
        loadAllBtn.Click += (_, _) =>
        {
            if (combo.SelectedItem is not string themeName) return;
            LoadAllFromTheme(themeName);
        };

        var saveBtn = new Button { Content = "Save As...", Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(4, 0, 0, 0) };
        saveBtn.Click += (_, _) => SaveAllTheme(combo);

        bar.Children.Add(lbl);
        bar.Children.Add(combo);
        bar.Children.Add(loadAllBtn);
        bar.Children.Add(saveBtn);

        return bar;
    }

    private StackPanel CreatePerTabThemeBar()
    {
        return CreateThemeBar([]); // same UI, Load All loads everything
    }

    private void RefreshThemeCombo(ComboBox combo)
    {
        combo.Items.Clear();
        if (!Directory.Exists(ThemesDir)) return;
        foreach (var file in Directory.GetFiles(ThemesDir, "*.txt").OrderBy(f => f))
        {
            combo.Items.Add(Path.GetFileNameWithoutExtension(file));
        }
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private Dictionary<string, string> ReadThemeFile(string themeName)
    {
        var dict = new Dictionary<string, string>();
        var path = Path.Combine(ThemesDir, themeName + ".txt");
        if (!File.Exists(path)) return dict;
        foreach (var line in File.ReadAllLines(path))
        {
            if (!line.StartsWith("Color.", StringComparison.Ordinal)) continue;
            var eq = line.IndexOf('=');
            if (eq <= 6) continue;
            dict[line[6..eq]] = line[(eq + 1)..];
        }
        return dict;
    }

    private void LoadAllFromTheme(string themeName)
    {
        var theme = ReadThemeFile(themeName);
        foreach (var (tKey, tVal) in theme)
        {
            _colors[tKey] = tVal;
            if (_ui.TryGetValue(tKey, out var entry))
                ApplyColorToButton(entry.Btn, entry.Txt, tVal);
        }
    }

    private void SaveAllTheme(ComboBox combo)
    {
        var dlg = new InputDialog("Save Theme", "Theme name:") { Owner = this };
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.InputText)) return;

        var name = dlg.InputText.Trim();
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');

        if (!Directory.Exists(ThemesDir))
            Directory.CreateDirectory(ThemesDir);

        var path = Path.Combine(ThemesDir, name + ".txt");

        // Save all current colors
        var toSave = new Dictionary<string, string>();
        foreach (var (key, val) in _colors)
        {
            if (!string.IsNullOrWhiteSpace(val))
                toSave[key] = val;
        }

        var lines = toSave.OrderBy(kv => kv.Key).Select(kv => $"Color.{kv.Key}={kv.Value}");
        File.WriteAllLines(path, lines);

        RefreshThemeCombo(combo);
        MessageBox.Show($"Theme saved: {name}", "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ---- Build panels ----

    private void BuildGeneralPanel()
    {
        PanelGeneral.Children.Add(CreateThemeBar(GeneralKeys));

        var items = new (string Key, string Label)[]
        {
            ("Bg", "Background"),
            ("BgLight", "Panel Background"),
            ("BgPanel", "Alt Row Background"),
            ("Border", "Border"),
            ("Fg", "Foreground"),
            ("FgDim", "Dim Foreground"),
            ("Accent", "Accent"),
            ("Selection", "Selection"),
            ("Toolbar", "Toolbar Background"),
            ("StatusBar", "Status Bar"),
            ("ValueChanged", "Value Changed"),
        };

        foreach (var (key, label) in items)
            AddColorRow(PanelGeneral, key, label);
    }

    private void BuildDisasmPanel()
    {
        PanelDisasm.Children.Add(CreateThemeBar(DisasmKeys));

        var items = new (string Key, string Label)[]
        {
            ("DsmAddress", "Address"),
            ("DsmMnemonic", "Mnemonic"),
            ("DsmRegister", "Register"),
            ("DsmBytes", "Hex Bytes"),
            ("DsmNumber", "Number Literal"),
            ("DsmJump", "Jump / Call Target"),
            ("DsmPunctuation", "Punctuation ([ ] , +)"),
            ("DsmString", "String Literal"),
            ("DsmComment", "Comment"),
            ("DsmSymbol", "Symbol Name"),
            ("DsmFunction", "Function Name"),
            ("DsmBpMarker", "Breakpoint Marker"),
            ("DsmBpRow", "Breakpoint Row Background"),
            ("DsmCurrentLine", "Current Line Background"),
        };

        foreach (var (key, label) in items)
            AddColorRow(PanelDisasm, key, label);
    }

    private void BuildStackPanel()
    {
        PanelStack.Children.Add(CreateThemeBar(StackKeys));

        var items = new (string Key, string Label)[]
        {
            ("StackOffset", "Offset (RSP+XX)"),
            ("StackAddress", "Address Value"),
            ("StackAnnotation", "Annotation / Hint"),
        };

        foreach (var (key, label) in items)
            AddColorRow(PanelStack, key, label);
    }

    private void BuildTabStylePanel()
    {
        PanelTabStyle.Children.Add(CreateThemeBar(TabStyleKeys));

        var items = new (string Key, string Label)[]
        {
            ("TabBg", "Tab Background"),
            ("TabFg", "Tab Foreground"),
            ("TabSelBg", "Selected Background"),
            ("TabSelFg", "Selected Foreground"),
            ("TabSelBorder", "Selected Border"),
            ("TabHoverBg", "Hover Background"),
        };

        foreach (var (key, label) in items)
            AddColorRow(PanelTabStyle, key, label);
    }

    private void BuildPerTabPanel()
    {
        PanelPerTab.Children.Add(CreatePerTabThemeBar());

        var header = new TextBlock
        {
            Text = "Override foreground/background for individual tab headers.\nLeave empty to use global tab colors.",
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#808080")),
            Margin = new Thickness(0, 0, 0, 12),
            TextWrapping = TextWrapping.Wrap
        };
        PanelPerTab.Children.Add(header);

        foreach (var tab in TabNames)
        {
            var title = new TextBlock
            {
                Text = tab,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 8, 0, 4)
            };
            PanelPerTab.Children.Add(title);

            AddColorRow(PanelPerTab, $"Tab.{tab}.Fg", "  Text Color", allowEmpty: true);
            AddColorRow(PanelPerTab, $"Tab.{tab}.Bg", "  Background", allowEmpty: true);
        }
    }

    private void BuildPluginsPanel()
    {
        PanelPlugins.Children.Add(CreateThemeBar(PluginKeys));

        // ---- Plugin Control Colors ----
        var controlHeader = new TextBlock
        {
            Text = "Plugin Control Colors",
            FontSize = 14, FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 4)
        };
        PanelPlugins.Children.Add(controlHeader);

        var controlDesc = new TextBlock
        {
            Text = "These colors apply to all controls inside plugin panels.\nPlugins inherit these automatically — no code changes needed.",
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#808080")),
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap
        };
        PanelPlugins.Children.Add(controlDesc);

        var pluginItems = new (string Key, string Label)[]
        {
            ("PluginBg",          "Background"),
            ("PluginFg",          "Foreground"),
            ("PluginFgDim",       "Dim Text"),
            ("PluginBorder",      "Borders"),
            ("PluginAccent",      "Accent / Highlights"),
            ("PluginControlBg",   "Input Controls (TextBox, ComboBox)"),
            ("PluginButtonBg",    "Button Background"),
            ("PluginButtonHover", "Button Hover"),
            ("PluginSelection",   "Selection / Active Row"),
            ("PluginGridAltRow",  "DataGrid Alternating Row"),
            ("PluginGroupHeader", "GroupBox Header"),
            ("PluginGroupBg",     "GroupBox Content"),
        };

        foreach (var (key, label) in pluginItems)
            AddColorRow(PanelPlugins, key, label);

        // ---- Per-Plugin Tab Header Colors ----
        if (_pluginTabNames.Length > 0)
        {
            var tabHeader = new TextBlock
            {
                Text = "Plugin Tab Headers",
                FontSize = 14, FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 16, 0, 4)
            };
            PanelPlugins.Children.Add(tabHeader);

            var tabDesc = new TextBlock
            {
                Text = "Override foreground/background for individual plugin tab headers.\nLeave empty to use global tab colors.",
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#808080")),
                Margin = new Thickness(0, 0, 0, 8),
                TextWrapping = TextWrapping.Wrap
            };
            PanelPlugins.Children.Add(tabDesc);
        }

        foreach (var tab in _pluginTabNames)
        {
            var title = new TextBlock
            {
                Text = tab,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 8, 0, 4)
            };
            PanelPlugins.Children.Add(title);

            AddColorRow(PanelPlugins, $"Tab.{tab}.Fg", "  Text Color", allowEmpty: true);
            AddColorRow(PanelPlugins, $"Tab.{tab}.Bg", "  Background", allowEmpty: true);
        }
    }

    // ---- Color row ----

    private void AddColorRow(StackPanel parent, string key, string label, bool allowEmpty = false)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };

        var lbl = new TextBlock
        {
            Text = label,
            Width = 200,
            VerticalAlignment = VerticalAlignment.Center
        };

        var btn = new Button
        {
            Width = 80,
            Height = 24,
            Margin = new Thickness(8, 0, 0, 0),
            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3F3F46")),
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
            Tag = key
        };
        // Flat template
        var factory = new FrameworkElementFactory(typeof(Border));
        factory.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background")
            { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        factory.SetValue(Border.BorderBrushProperty, btn.BorderBrush);
        factory.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
        btn.Template = new ControlTemplate(typeof(Button)) { VisualTree = factory };
        btn.Click += OnPickColor;

        var txt = new TextBlock
        {
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#808080"))
        };

        if (allowEmpty)
        {
            var clearBtn = new Button
            {
                Content = "X",
                Width = 24, Height = 24,
                Margin = new Thickness(4, 0, 0, 0),
                Padding = new Thickness(0),
                Tag = key,
                ToolTip = "Clear (use global)"
            };
            clearBtn.Click += (s, _) =>
            {
                _colors[key] = "";
                btn.Background = Brushes.Transparent;
                txt.Text = "(global)";
            };
            row.Children.Add(lbl);
            row.Children.Add(btn);
            row.Children.Add(txt);
            row.Children.Add(clearBtn);
        }
        else
        {
            row.Children.Add(lbl);
            row.Children.Add(btn);
            row.Children.Add(txt);
        }

        _ui[key] = (btn, txt);
        parent.Children.Add(row);

        // Set initial color
        var hex = _colors.GetValueOrDefault(key, "");
        ApplyColorToButton(btn, txt, hex);
    }

    private void ApplyColorToButton(Button btn, TextBlock txt, string hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            btn.Background = Brushes.Transparent;
            txt.Text = "(global)";
            return;
        }
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            btn.Background = new SolidColorBrush(color);
            txt.Text = hex.ToUpperInvariant();
        }
        catch
        {
            btn.Background = Brushes.Magenta;
            txt.Text = "INVALID";
        }
    }

    private void OnPickColor(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string key) return;
        if (!_ui.TryGetValue(key, out var entry)) return;

        var current = _colors.GetValueOrDefault(key, "#808080");
        if (string.IsNullOrWhiteSpace(current)) current = "#808080";
        var dlg = new ColorPickerDialog(current) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _colors[key] = dlg.SelectedHex;
            ApplyColorToButton(entry.Btn, entry.Txt, dlg.SelectedHex);
        }
    }

    private void OnResetDefaults(object sender, RoutedEventArgs e)
    {
        foreach (var (key, entry) in _ui)
        {
            var def = Defaults.GetValueOrDefault(key, "");
            _colors[key] = def;
            ApplyColorToButton(entry.Btn, entry.Txt, def);
        }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
