using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using KernelFlirt.UI.Models;
using KernelFlirt.UI.ViewModels;

namespace KernelFlirt.UI.Controls;

/// <summary>
/// OllyDbg-style disassembly view with per-token syntax highlighting.
/// </summary>
public partial class DisasmView : UserControl
{
    // x86-64 register names for highlighting
    private static readonly HashSet<string> Registers = new(StringComparer.OrdinalIgnoreCase)
    {
        "rax","rbx","rcx","rdx","rsi","rdi","rbp","rsp",
        "r8","r9","r10","r11","r12","r13","r14","r15","rip",
        "eax","ebx","ecx","edx","esi","edi","ebp","esp",
        "r8d","r9d","r10d","r11d","r12d","r13d","r14d","r15d",
        "ax","bx","cx","dx","si","di","bp","sp",
        "al","bl","cl","dl","sil","dil","bpl","spl",
        "ah","bh","ch","dh",
        "r8b","r9b","r10b","r11b","r12b","r13b","r14b","r15b",
        "r8w","r9w","r10w","r11w","r12w","r13w","r14w","r15w",
        "cs","ds","es","fs","gs","ss",
        "xmm0","xmm1","xmm2","xmm3","xmm4","xmm5","xmm6","xmm7",
        "xmm8","xmm9","xmm10","xmm11","xmm12","xmm13","xmm14","xmm15",
        "ymm0","ymm1","ymm2","ymm3","ymm4","ymm5","ymm6","ymm7",
        "ymm8","ymm9","ymm10","ymm11","ymm12","ymm13","ymm14","ymm15",
        "cr0","cr2","cr3","cr4","cr8",
        "dr0","dr1","dr2","dr3","dr6","dr7",
    };

    // Jump/call/ret instructions (highlighted differently like OllyDbg)
    private static readonly HashSet<string> JumpMnemonics = new(StringComparer.OrdinalIgnoreCase)
    {
        "jmp","je","jne","jz","jnz","jg","jge","jl","jle",
        "ja","jae","jb","jbe","jo","jno","js","jns","jp","jnp",
        "jcxz","jecxz","jrcxz",
        "call","ret","retn","retf","iret","iretd","iretq",
        "loop","loope","loopne","loopz","loopnz",
        "syscall","sysret","int","int3","into",
    };

    // Colors matching OllyDbg palette
    private static SolidColorBrush AddressColor => (SolidColorBrush)Application.Current.Resources["AddressBrush"];
    private static SolidColorBrush BytesColor => (SolidColorBrush)Application.Current.Resources["HexBrush"];
    private static SolidColorBrush MnemonicColor => (SolidColorBrush)Application.Current.Resources["MnemonicBrush"];
    private static SolidColorBrush RegisterColor => (SolidColorBrush)Application.Current.Resources["RegisterBrush"];
    private static SolidColorBrush NumberColor => new(Color.FromRgb(0xB5, 0xCE, 0xA8));    // green numbers
    private static SolidColorBrush JumpColor => new(Color.FromRgb(0xFF, 0x80, 0x80));       // red-ish jumps
    private static SolidColorBrush PunctuationColor => new(Color.FromRgb(0x80, 0x80, 0x80));// gray
    private static SolidColorBrush StringColor => new(Color.FromRgb(0xCE, 0x91, 0x78));     // orange strings
    private static SolidColorBrush CommentColor => new(Color.FromRgb(0x60, 0x8B, 0x4E));    // green comments
    private static SolidColorBrush SymbolColor => new(Color.FromRgb(0x4E, 0xC9, 0xB0));         // teal/cyan symbols
    private static SolidColorBrush BpMarkerColor => (SolidColorBrush)Application.Current.Resources["BreakpointBrush"];
    private static SolidColorBrush CurrentLineColor => new(Color.FromRgb(0x26, 0x4F, 0x78));
    private static SolidColorBrush BpLineColor => new(Color.FromRgb(0x64, 0x1E, 0x1E));

    private int _selectedIndex = -1;
    private ObservableCollection<Instruction>? _instructions;

    /// <summary>Selected instruction address — used by context menus and Run to Cursor.</summary>
    public ulong SelectedAddress { get; private set; }

    public DisasmView()
    {
        InitializeComponent();
    }

    private MainViewModel? GetViewModel()
    {
        return Window.GetWindow(this)?.DataContext as MainViewModel;
    }

    /// <summary>
    /// Renders a list of instructions with OllyDbg-style syntax highlighting.
    /// </summary>
    public void SetInstructions(ObservableCollection<Instruction> instructions, ulong? currentRip = null)
    {
        _instructions = instructions;
        InstructionList.Items.Clear();
        _selectedIndex = -1;

        for (int i = 0; i < instructions.Count; i++)
        {
            var instr = instructions[i];
            var panel = CreateInstructionLine(instr, currentRip);
            panel.Tag = i;
            panel.MouseLeftButtonDown += OnLineClick;
            InstructionList.Items.Add(panel);
        }
    }

    private Border CreateInstructionLine(Instruction instr, ulong? currentRip)
    {
        var textBlock = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 13 };

        // Breakpoint marker column (2 chars wide)
        if (instr.HasBreakpoint)
        {
            textBlock.Inlines.Add(new Run("● ") { Foreground = BpMarkerColor, FontWeight = FontWeights.Bold });
        }
        else
        {
            textBlock.Inlines.Add(new Run("  ") { Foreground = PunctuationColor });
        }

        // Address column: show symbol label if available, otherwise hex address
        if (!string.IsNullOrEmpty(instr.AddressLabel))
        {
            // Clickable symbol name with context menu
            var symInline = CreateSymbolInline(instr.AddressLabel, instr.Address);
            textBlock.Inlines.Add(symInline);
            // Pad to align with hex address width (17 chars + 2 spaces)
            int pad = 19 - Math.Min(instr.AddressLabel.Length, 19);
            if (pad > 0) textBlock.Inlines.Add(new Run(new string(' ', pad)));
        }
        else
        {
            string addrStr = FormatAddress(instr.Address);
            textBlock.Inlines.Add(new Run(addrStr + "  ") { Foreground = AddressColor });
        }

        // Bytes: 48 89 5C 24 08 (padded to 30 chars)
        string bytesStr = instr.BytesHex;
        if (bytesStr.Length < 30) bytesStr = bytesStr.PadRight(30);
        else if (bytesStr.Length > 30) bytesStr = bytesStr[..27] + "...";
        textBlock.Inlines.Add(new Run(bytesStr + " ") { Foreground = BytesColor });

        // Mnemonic with per-token highlighting (branch targets show symbol names)
        AddHighlightedMnemonic(textBlock, instr);

        // Symbol comment (like x64dbg/OllyDbg style) — skip if already shown as branch target
        if (!string.IsNullOrEmpty(instr.Comment) && string.IsNullOrEmpty(instr.BranchTargetSymbol))
        {
            textBlock.Inlines.Add(new Run($"  ; {instr.Comment}") { Foreground = CommentColor });
        }

        // Background color for breakpoint line or current instruction
        Brush bgBrush;
        if (instr.IsCurrentInstruction || (currentRip.HasValue && instr.Address == currentRip.Value))
            bgBrush = CurrentLineColor;
        else if (instr.HasBreakpoint)
            bgBrush = BpLineColor;
        else
            bgBrush = Brushes.Transparent;

        var border = new Border
        {
            Child = textBlock,
            Background = bgBrush,
            Padding = new Thickness(4, 1, 4, 1),
            BorderThickness = new Thickness(0),
        };

        return border;
    }

    private static string FormatAddress(ulong addr)
    {
        // OllyDbg style: 00007FF6`12340000
        string hex = addr.ToString("X16");
        return hex[..8] + "`" + hex[8..];
    }

    /// <summary>
    /// Creates a clickable symbol name inline with right-click context menu
    /// (Go to, Set breakpoint, Copy symbol name).
    /// Uses InlineUIContainer wrapping a TextBlock since WPF Run has no ContextMenu.
    /// </summary>
    private InlineUIContainer CreateSymbolInline(string symbolName, ulong address)
    {
        var symText = new TextBlock
        {
            Text = symbolName,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            Foreground = SymbolColor,
            Cursor = Cursors.Hand,
            TextDecorations = TextDecorations.Underline,
            ToolTip = $"{symbolName}\n{address:X16}",
        };

        // Double-click navigates to symbol
        symText.MouseLeftButtonDown += (s, e) =>
        {
            if (e.ClickCount == 2)
            {
                GetViewModel()?.NavigateDisasmTo(address);
                e.Handled = true;
            }
        };

        // Right-click context menu
        symText.ContextMenu = new ContextMenu
        {
            Items =
            {
                CreateSymMenuItem($"Go to {symbolName}", () => GetViewModel()?.NavigateDisasmTo(address)),
                CreateSymMenuItem($"Set breakpoint on {symbolName}", () => GetViewModel()?.SetBreakpointAtAddress(address)),
                new Separator(),
                CreateSymMenuItem("Copy symbol name", () => Clipboard.SetText(symbolName)),
                CreateSymMenuItem("Copy address", () => Clipboard.SetText($"{address:X16}")),
            }
        };

        return new InlineUIContainer(symText) { BaselineAlignment = BaselineAlignment.TextBottom };
    }

    private static MenuItem CreateSymMenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>
    /// Add mnemonic + operands with syntax highlighting.
    /// For branch instructions with resolved symbols, replaces hex operand with clickable symbol name.
    /// </summary>
    private void AddHighlightedMnemonic(TextBlock tb, Instruction instr)
    {
        // Mnemonic
        Brush mnemonicBrush = JumpMnemonics.Contains(instr.Mnemonic) ? JumpColor : MnemonicColor;
        tb.Inlines.Add(new Run(instr.Mnemonic.PadRight(8)) { Foreground = mnemonicBrush, FontWeight = FontWeights.SemiBold });

        if (string.IsNullOrEmpty(instr.Operands))
            return;

        // For branch instructions with a resolved symbol, show the symbol name instead of hex address
        if (!string.IsNullOrEmpty(instr.BranchTargetSymbol) && instr.BranchTargetAddress != 0)
        {
            var symInline = CreateSymbolInline(instr.BranchTargetSymbol, instr.BranchTargetAddress);
            tb.Inlines.Add(symInline);
            return;
        }

        // Normal operands: tokenize and highlight
        var tokens = TokenizeOperands(instr.Operands);
        foreach (var (text, kind) in tokens)
        {
            Brush brush = kind switch
            {
                TokenKind.Register => RegisterColor,
                TokenKind.Number => NumberColor,
                TokenKind.Punctuation => PunctuationColor,
                TokenKind.SizePrefix => PunctuationColor,
                TokenKind.String => StringColor,
                TokenKind.Symbol => CommentColor,
                _ => MnemonicColor,
            };
            tb.Inlines.Add(new Run(text) { Foreground = brush });
        }
    }

    private enum TokenKind { Text, Register, Number, Punctuation, SizePrefix, String, Symbol }

    private static List<(string text, TokenKind kind)> TokenizeOperands(string operands)
    {
        var result = new List<(string, TokenKind)>();
        int i = 0;

        while (i < operands.Length)
        {
            char c = operands[i];

            // Whitespace
            if (char.IsWhiteSpace(c))
            {
                int start = i;
                while (i < operands.Length && char.IsWhiteSpace(operands[i])) i++;
                result.Add((operands[start..i], TokenKind.Text));
                continue;
            }

            // Punctuation: , [ ] + - * :
            if (c is ',' or '[' or ']' or '+' or '-' or '*' or ':' or '(' or ')')
            {
                result.Add((c.ToString(), TokenKind.Punctuation));
                i++;
                continue;
            }

            // Hex number: 0x...
            if (c == '0' && i + 1 < operands.Length && operands[i + 1] == 'x')
            {
                int start = i;
                i += 2;
                while (i < operands.Length && IsHexChar(operands[i])) i++;
                result.Add((operands[start..i], TokenKind.Number));
                continue;
            }

            // Word token (identifier, register, number, size prefix)
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                int start = i;
                while (i < operands.Length && (char.IsLetterOrDigit(operands[i]) || operands[i] == '_')) i++;
                string word = operands[start..i];

                if (word is "byte" or "word" or "dword" or "qword" or "xmmword" or "ymmword"
                    or "ptr" or "BYTE" or "WORD" or "DWORD" or "QWORD" or "PTR")
                {
                    result.Add((word, TokenKind.SizePrefix));
                }
                else if (Registers.Contains(word))
                {
                    result.Add((word, TokenKind.Register));
                }
                else if (IsHexNumber(word))
                {
                    result.Add((word, TokenKind.Number));
                }
                else
                {
                    result.Add((word, TokenKind.Text));
                }
                continue;
            }

            // Anything else
            result.Add((c.ToString(), TokenKind.Text));
            i++;
        }

        return result;
    }

    private static bool IsHexChar(char c)
        => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');

    private static bool IsHexNumber(string s)
    {
        if (s.Length == 0) return false;
        if (s.All(c => c >= '0' && c <= '9')) return true;
        if (s[^1] is 'h' or 'H' && s[..^1].All(IsHexChar)) return true;
        if (s.All(IsHexChar) && s.Any(c => c >= '0' && c <= '9')) return true;
        return false;
    }

    private void OnLineClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border && border.Tag is int index)
        {
            // Deselect previous
            if (_selectedIndex >= 0 && _selectedIndex < InstructionList.Items.Count)
            {
                if (InstructionList.Items[_selectedIndex] is Border prev)
                    prev.Background = Brushes.Transparent;
            }

            border.Background = new SolidColorBrush(Color.FromRgb(0x26, 0x4F, 0x78));
            _selectedIndex = index;

            // Update selected address for context menu operations
            if (_instructions != null && index < _instructions.Count)
            {
                SelectedAddress = _instructions[index].Address;
                var vm = GetViewModel();
                if (vm != null)
                    vm.SelectedDisasmAddress = SelectedAddress;
            }
        }
    }

    /* ================================================================== */
    /*  Context menu handlers                                              */
    /* ================================================================== */

    private void OnContextToggleBp(object sender, RoutedEventArgs e)
    {
        GetViewModel()?.ToggleBreakpointCommand.Execute(null);
    }

    private void OnContextToggleHwBp(object sender, RoutedEventArgs e)
    {
        GetViewModel()?.ToggleHwBreakpointCommand.Execute(null);
    }

    private void OnContextSetCondBp(object sender, RoutedEventArgs e)
    {
        GetViewModel()?.SetConditionalBreakpointCommand.Execute(null);
    }

    private void OnContextSetLogBp(object sender, RoutedEventArgs e)
    {
        GetViewModel()?.SetLogBreakpointCommand.Execute(null);
    }

    private void OnContextRunToCursor(object sender, RoutedEventArgs e)
    {
        GetViewModel()?.RunToCursorCommand.Execute(null);
    }

    private void OnContextAddBookmark(object sender, RoutedEventArgs e)
    {
        GetViewModel()?.AddBookmarkCommand.Execute(null);
    }

    private void OnContextFollowInDump(object sender, RoutedEventArgs e)
    {
        if (SelectedAddress != 0)
            GetViewModel()?.FollowInDumpCommand.Execute(SelectedAddress);
    }

    private void OnContextFollowInDisasm(object sender, RoutedEventArgs e)
    {
        // Try to parse the operand as an address for "follow" behavior
        if (_instructions != null && _selectedIndex >= 0 && _selectedIndex < _instructions.Count)
        {
            var instr = _instructions[_selectedIndex];
            if (!string.IsNullOrEmpty(instr.Operands) &&
                TryParseOperandAddress(instr.Operands, out ulong target))
            {
                GetViewModel()?.FollowInDisasmCommand.Execute(target);
                return;
            }
        }
        if (SelectedAddress != 0)
            GetViewModel()?.FollowInDisasmCommand.Execute(SelectedAddress);
    }

    private void OnContextCopyAddress(object sender, RoutedEventArgs e)
    {
        if (SelectedAddress != 0)
            Clipboard.SetText($"{SelectedAddress:X16}");
    }

    private void OnContextCopyLine(object sender, RoutedEventArgs e)
    {
        GetViewModel()?.CopyDisasmLineCommand.Execute(null);
    }

    private void OnContextCopyAll(object sender, RoutedEventArgs e)
    {
        GetViewModel()?.CopyAllDisasmCommand.Execute(null);
    }

    private void OnContextSearchBinary(object sender, RoutedEventArgs e)
    {
        GetViewModel()?.SearchBinaryCommand.Execute(null);
    }

    private void OnContextSearchStrings(object sender, RoutedEventArgs e)
    {
        GetViewModel()?.SearchStringsCommand.Execute(null);
    }

    private static bool TryParseOperandAddress(string operands, out ulong address)
    {
        address = 0;
        string s = operands.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ulong.TryParse(s[2..], System.Globalization.NumberStyles.HexNumber, null, out address);
        return ulong.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out address);
    }
}
