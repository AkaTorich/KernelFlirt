using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using KernelFlirt.UI.Models;
using KernelFlirt.UI.ViewModels;

namespace KernelFlirt.UI.Controls;

/// <summary>
/// One disassembly-row cell that renders mnemonic + operands + optional
/// clickable symbol + trailing comment, with proper ellipsis when the column
/// is too narrow. Uses DrawingContext directly instead of inline Runs, so
/// WPF TextTrimming is no longer needed — the ellipsis is rendered by hand.
///
/// All colors come from the theme resource dictionary (same keys the rest of
/// the disasm view uses), so switching themes just invalidates the visual.
/// </summary>
public sealed class MnemonicCell : FrameworkElement
{
    private Instruction? _instr;
    private readonly List<(Rect rect, ulong target)> _clickable = new();

    public static readonly DependencyProperty InstructionProperty = DependencyProperty.Register(
        nameof(Instruction), typeof(Instruction), typeof(MnemonicCell),
        new PropertyMetadata(null, OnInstructionChanged));

    public Instruction? Instruction
    {
        get => (Instruction?)GetValue(InstructionProperty);
        set => SetValue(InstructionProperty, value);
    }

    private static void OnInstructionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var c = (MnemonicCell)d;
        c._instr = e.NewValue as Instruction;
        c.InvalidateVisual();
    }

    public MnemonicCell()
    {
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        Focusable = false;
        Cursor = Cursors.Arrow;
        System.Windows.Media.TextOptions.SetTextRenderingMode(this, TextRenderingMode.Aliased);
        System.Windows.Media.TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        System.Windows.Media.TextOptions.SetTextHintingMode(this, TextHintingMode.Fixed);
    }

    /// <summary>
    /// Force re-render. Call this after the theme resource dictionary has
    /// been replaced so all cells pick up new colors.
    /// </summary>
    public void InvalidateTheme() => InvalidateVisual();

    private static FontFamily? FindInheritedFontFamily(DependencyObject d)
    {
        var p = d;
        while (p != null)
        {
            if (p is Control c) return c.FontFamily;
            p = VisualTreeHelper.GetParent(p);
        }
        return null;
    }

    private SolidColorBrush Res(string key)
    {
        // First try the control's own resource lookup (respects dynamic theme
        // swaps); fall back to MergedDictionaries[0] where themes are written.
        try
        {
            if (TryFindResource(key) is SolidColorBrush b1) return b1;
        }
        catch { }
        var dicts = Application.Current.Resources.MergedDictionaries;
        foreach (var md in dicts)
            if (md.Contains(key) && md[key] is SolidColorBrush b2) return b2;
        if (Application.Current.Resources.Contains(key) &&
            Application.Current.Resources[key] is SolidColorBrush b3) return b3;
        return new SolidColorBrush(Colors.Magenta);
    }

    protected override void OnRender(DrawingContext dc)
    {
        _clickable.Clear();
        if (_instr == null) return;

        double fontSize = TextElement.GetFontSize(this);
        if (fontSize <= 0) fontSize = 11;
        // Walk up the visual tree to find the nearest Control whose FontFamily
        // was set (e.g. the enclosing DisasmView). TextElement.GetFontFamily
        // returns the attached-property value, not the inherited Control.FontFamily.
        FontFamily family = FindInheritedFontFamily(this) ?? new FontFamily("Lucida Console");
        var tf = new Typeface(family,
                              FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var tfNormal = new Typeface(family,
                              FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var tfItalic = new Typeface(family,
                              FontStyles.Italic, FontWeights.Normal, FontStretches.Normal);
        double pxPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double cellWidth = ActualWidth;
        if (cellWidth <= 0) return;

        // Collect tokens first (text + brush + typeface + optional click target)
        var tokens = new List<(string text, Brush brush, Typeface typeface, ulong? click)>();

        bool isJump = DisasmView.JumpMnemonics.Contains(_instr.Mnemonic);
        // mnemonic without padding; we add a fixed gap separately because
        // FormattedText trims trailing whitespace.
        tokens.Add((_instr.Mnemonic,
                    isJump ? Res("DsmJumpBrush") : Res("MnemonicBrush"),
                    tfNormal, null));
        tokens.Add(("\u00A0\u00A0", Res("FgBrush"), tfNormal, null));

        if (!string.IsNullOrEmpty(_instr.BranchTargetSymbol) && _instr.BranchTargetAddress != 0)
        {
            tokens.Add((_instr.BranchTargetSymbol!,
                        Res("DsmFunctionBrush"), tfNormal, _instr.BranchTargetAddress));
        }
        else if (!string.IsNullOrEmpty(_instr.Operands))
        {
            foreach (var (text, kind) in DisasmView.TokenizeOperandsStatic(_instr.Operands))
            {
                // Replace pure-whitespace tokens with a non-breaking sample so
                // FormattedText actually renders the gap between operands.
                string emit = string.IsNullOrWhiteSpace(text) ? text.Replace(' ', '\u00A0') : text;
                Brush b = kind switch
                {
                    DisasmView.TokenKind.Register => Res("RegisterBrush"),
                    DisasmView.TokenKind.Number => Res("DsmNumberBrush"),
                    DisasmView.TokenKind.Punctuation => Res("DsmPunctuationBrush"),
                    DisasmView.TokenKind.String => Res("DsmStringBrush"),
                    _ => Res("FgBrush"),
                };
                tokens.Add((emit, b, tfNormal, null));
            }
        }

        if (!string.IsNullOrEmpty(_instr.Comment))
        {
            string? dc2 = _instr.Comment;
            if (!string.IsNullOrEmpty(_instr.BranchTargetSymbol) && dc2.Contains(" | "))
                dc2 = dc2[(dc2.IndexOf(" | ") + 3)..];
            else if (!string.IsNullOrEmpty(_instr.BranchTargetSymbol))
                dc2 = null;
            if (!string.IsNullOrEmpty(dc2))
                tokens.Add(($"\u00A0\u00A0; {dc2}", Res("DsmCommentBrush"), tfItalic, null));
        }

        FormattedText MakeFt(string s, Typeface f, Brush b)
        {
            var ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                       f, fontSize, b, pxPerDip)
            { TextAlignment = TextAlignment.Left };
            ft.SetFontWeight(FontWeights.Normal);
            return ft;
        }

        // Ellipsis sample for "…"
        var ellFt = MakeFt("…", tfNormal, Res("FgDimBrush"));
        double ellW = ellFt.Width;

        // Pre-measure each token
        var measured = new List<FormattedText>(tokens.Count);
        double total = 0;
        foreach (var t in tokens)
        {
            var ft = MakeFt(t.text, t.typeface, t.brush);
            measured.Add(ft);
            total += ft.Width;
        }

        double x = 0;
        double yTop = (ActualHeight - measured[0].Height) / 2.0;
        if (yTop < 0) yTop = 0;

        if (total <= cellWidth - 2)
        {
            // Everything fits — draw and record clickable rects
            for (int i = 0; i < tokens.Count; i++)
            {
                var ft = measured[i];
                dc.DrawText(ft, new Point(x, yTop));
                if (tokens[i].click is ulong target)
                    _clickable.Add((new Rect(x, yTop, ft.Width, ft.Height), target));
                x += ft.Width;
            }
            return;
        }

        // Need to truncate — draw tokens whole while they fit, then fit the last
        // partial one with a "…" appended.
        double budget = cellWidth - 2 - ellW;
        for (int i = 0; i < tokens.Count; i++)
        {
            var ft = measured[i];
            if (x + ft.Width <= budget)
            {
                dc.DrawText(ft, new Point(x, yTop));
                if (tokens[i].click is ulong target)
                    _clickable.Add((new Rect(x, yTop, ft.Width, ft.Height), target));
                x += ft.Width;
                continue;
            }
            // partially fit — clip character-by-character
            string text = tokens[i].text;
            int keep = 0;
            while (keep < text.Length)
            {
                var probe = MakeFt(text[..(keep + 1)], tokens[i].typeface, tokens[i].brush);
                if (x + probe.Width > budget) break;
                keep++;
            }
            if (keep > 0)
            {
                var part = MakeFt(text[..keep], tokens[i].typeface, tokens[i].brush);
                dc.DrawText(part, new Point(x, yTop));
                if (tokens[i].click is ulong target)
                    _clickable.Add((new Rect(x, yTop, part.Width, part.Height), target));
                x += part.Width;
            }
            dc.DrawText(ellFt, new Point(x, yTop));
            return;
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            var p = e.GetPosition(this);
            foreach (var (rect, target) in _clickable)
            {
                if (rect.Contains(p))
                {
                    (Window.GetWindow(this)?.DataContext as MainViewModel)?.NavigateDisasmTo(target);
                    e.Handled = true;
                    return;
                }
            }
        }
        base.OnMouseLeftButtonDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        Cursor = _clickable.Any(c => c.rect.Contains(p)) ? Cursors.Hand : Cursors.Arrow;
        base.OnMouseMove(e);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // Take whatever height the parent gives (row height driven by font).
        double fs = TextElement.GetFontSize(this);
        if (fs <= 0) fs = 11;
        return new Size(Math.Min(availableSize.Width, 4000), fs * 1.4);
    }
}
