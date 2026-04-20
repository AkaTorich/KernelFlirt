using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KernelFlirt.UI;

public partial class AppearanceWindow : Window
{
    // Mapping from FG color key → BG color key when the UI actually uses both.
    // Add entries here as new colour pairs appear in the theme system.
    private static readonly Dictionary<string, string> _fgToBg = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DsmBpMarker"]     = "DsmBpRow",
        ["DsmCurrentLine"]  = "DsmCurrentLine",  // shown as selection/current bg
    };

    public Dictionary<string, string> ResultColors { get; }

    // Category name → list of color keys. Keys are the plain name (without "Color." prefix)
    private static readonly Dictionary<string, Predicate<string>> _groups = new()
    {
        ["General"]       = k => !HasPrefix(k, "Dsm") && !HasPrefix(k, "Stack")
                              && !HasPrefix(k, "Plugin") && !k.StartsWith("Tab.", StringComparison.OrdinalIgnoreCase)
                              && !HasPrefix(k, "Script") && !HasPrefix(k, "ScrollBar")
                              && !HasPrefix(k, "JumpArrow") && !HasPrefix(k, "SplitterDash"),
        ["Disassembly"]   = k => HasPrefix(k, "Dsm"),
        ["Stack"]         = k => HasPrefix(k, "Stack"),
        ["Plugins"]       = k => HasPrefix(k, "Plugin"),
        ["Tabs"]          = k => k.StartsWith("Tab.", StringComparison.OrdinalIgnoreCase)
                               || k is "TabBg" or "TabFg" or "TabSelBg" or "TabSelFg"
                                      or "TabSelBorder" or "TabHoverBg",
        ["Script / Decompiler"] = k => HasPrefix(k, "Script"),
        ["Jump Arrows"]   = k => HasPrefix(k, "JumpArrow") || k == "SplitterDash",
        ["Scroll Bars"]   = k => HasPrefix(k, "ScrollBar"),
    };

    private static bool HasPrefix(string s, string p)
        => s.StartsWith(p, StringComparison.OrdinalIgnoreCase);

    // x64dbg-inspired swatch palette (10 cols × 3 rows)
    private static readonly string[] _palette =
    [
        "#000000","#1E1E1E","#2D2D30","#3F3F46","#808080","#C0C0C0","#E0E0E0","#FFFFFF","#FF0000","#C0392B",
        "#E67E22","#F39C12","#F1C40F","#27AE60","#2ECC71","#16A085","#1ABC9C","#2980B9","#3498DB","#5DADE2",
        "#8E44AD","#9B59B6","#E91E63","#FF5370","#FFEB3B","#FF9800","#4CAF50","#00BCD4","#607D8B","#795548",
    ];

    private string? _currentKey;
    private string? _currentBgKey;
    private bool _updatingFromUi;

    public record FontChoice(string Family, double Size);

    /// <summary>Embedded (shipped) font family names available at runtime.</summary>
    public static readonly string[] EmbeddedFontNames = new[] { "Intel One Mono" };

    /// <summary>Resolve a family name to a FontFamily that works for embedded fonts.</summary>
    public static FontFamily ResolveFontFamily(string name)
    {
        if (EmbeddedFontNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            // pack URI pointing at /fonts/ in the assembly, with family friendly name after '#'
            var uri = new Uri("pack://application:,,,/fonts/", UriKind.Absolute);
            return new FontFamily(uri, "./#" + name);
        }
        return new FontFamily(name);
    }
    public FontChoice DisasmFont { get; set; }
    public FontChoice HexFont { get; set; }
    public FontChoice StackFont { get; set; }
    public FontChoice RegistersFont { get; set; }

    public AppearanceWindow(Dictionary<string, string> currentColors,
                            FontChoice disasm, FontChoice hex, FontChoice stack, FontChoice registers)
    {
        InitializeComponent();
        ResultColors = new Dictionary<string, string>(currentColors);
        DisasmFont = disasm;
        HexFont = hex;
        StackFont = stack;
        RegistersFont = registers;
        BuildSwatches(FgSwatches, hex2 => { FgHex.Text = hex2; });
        BuildSwatches(BgSwatches, hex2 => { BgHex.Text = hex2; });
        BuildTree();
        BuildFontTab();
    }

    private void BuildFontTab()
    {
        var families = System.Windows.Media.Fonts.SystemFontFamilies
            .Select(f => f.Source)
            .ToList();
        // Embedded fonts shipped with the app (currently Intel One Mono).
        families.AddRange(EmbeddedFontNames);
        families = families
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        void Setup(ComboBox cb, TextBox sb, Border preview, TextBlock pt, FontChoice current,
                   Action<FontChoice> onChange)
        {
            cb.ItemsSource = families;
            cb.SelectedItem = current.Family;
            sb.Text = current.Size.ToString(System.Globalization.CultureInfo.InvariantCulture);

            void Update()
            {
                var fam = cb.SelectedItem as string ?? current.Family;
                double size = current.Size;
                double.TryParse(sb.Text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out size);
                if (size < 6) size = 6;
                if (size > 48) size = 48;
                pt.FontFamily = ResolveFontFamily(fam);
                pt.FontSize = size;
                onChange(new FontChoice(fam, size));
            }
            cb.SelectionChanged += (_, _) => Update();
            sb.TextChanged += (_, _) => Update();
            Update();
        }

        Setup(DisasmFontCombo, DisasmFontSize, DisasmPreview, DisasmPreviewText,
              DisasmFont, v => DisasmFont = v);
        Setup(HexFontCombo, HexFontSize, HexPreview, HexPreviewText,
              HexFont, v => HexFont = v);
        Setup(StackFontCombo, StackFontSize, StackPreview, StackPreviewText,
              StackFont, v => StackFont = v);
        Setup(RegistersFontCombo, RegistersFontSize, RegistersPreview, RegistersPreviewText,
              RegistersFont, v => RegistersFont = v);
    }

    private void BuildTree()
    {
        Tree.Items.Clear();
        foreach (var (group, pred) in _groups)
        {
            var keys = ResultColors.Keys.Where(k => pred(k))
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (keys.Count == 0) continue;

            var node = new TreeViewItem { Header = group, IsExpanded = false };
            foreach (var k in keys)
            {
                node.Items.Add(new TreeViewItem { Header = PrettyName(k), Tag = k });
            }
            Tree.Items.Add(node);
        }
    }

    private static string PrettyName(string k)
    {
        // Insert spaces in camelCase for readability
        var s = Regex.Replace(k, "(?<=[a-z])([A-Z])", " $1");
        return s;
    }

    private void BuildSwatches(Panel host, Action<string> onClick)
    {
        host.Children.Clear();
        foreach (var hex in _palette)
        {
            var btn = new Button
            {
                Background = new SolidColorBrush(ParseHex(hex, Colors.Transparent)),
                BorderBrush = Brushes.DimGray,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(1),
                MinWidth = 22, MinHeight = 22,
                ToolTip = hex,
            };
            btn.Click += (_, _) => onClick(hex);
            host.Children.Add(btn);
        }
    }

    private static Color ParseHex(string hex, Color fallback)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch { return fallback; }
    }

    private void OnTreeSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not TreeViewItem tvi || tvi.Tag is not string key)
        {
            _currentKey = null; _currentBgKey = null;
            FgGroupSetVisible(false);
            return;
        }
        _currentKey = key;
        _currentBgKey = _fgToBg.TryGetValue(key, out var bg) && ResultColors.ContainsKey(bg) ? bg : null;

        _updatingFromUi = true;
        FgHex.Text = ResultColors.TryGetValue(key, out var fg) ? fg : "#FFFFFF";
        ApplyToPreview(FgPreview, FgHex.Text);

        BgGroup.Visibility = _currentBgKey == null ? Visibility.Collapsed : Visibility.Visible;
        if (_currentBgKey != null)
        {
            BgHex.Text = ResultColors.TryGetValue(_currentBgKey, out var bv) ? bv : "#000000";
            ApplyToPreview(BgPreview, BgHex.Text);
        }
        UpdatePreviewText();
        _updatingFromUi = false;
    }

    private void FgGroupSetVisible(bool _) { /* FG always visible when a key is selected */ }

    private void OnFgHexChanged(object sender, TextChangedEventArgs e)
    {
        if (_currentKey == null) return;
        var hex = FgHex.Text.Trim();
        ApplyToPreview(FgPreview, hex);
        if (!_updatingFromUi) ResultColors[_currentKey] = hex;
        UpdatePreviewText();
    }

    private void OnBgHexChanged(object sender, TextChangedEventArgs e)
    {
        if (_currentBgKey == null) return;
        var hex = BgHex.Text.Trim();
        ApplyToPreview(BgPreview, hex);
        if (!_updatingFromUi) ResultColors[_currentBgKey] = hex;
        UpdatePreviewText();
    }

    private static void ApplyToPreview(Border b, string hex)
    {
        try
        {
            b.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }
        catch { /* ignore invalid */ }
    }

    private void UpdatePreviewText()
    {
        try { PreviewText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(FgHex.Text)); }
        catch { }
        if (_currentBgKey != null)
        {
            try { PreviewBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(BgHex.Text)); }
            catch { }
        }
        else
        {
            PreviewBorder.Background = (SolidColorBrush)Application.Current.Resources.MergedDictionaries[0]["BgBrush"];
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
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
