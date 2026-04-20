using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using KernelFlirt.UI.ViewModels;

namespace KernelFlirt.UI.Controls;

/// <summary>
/// OllyDbg-style hex dump view with separate Address / Hex / ASCII columns,
/// per-column context menus, selection, and memory breakpoint support.
/// </summary>
public partial class HexDumpView : UserControl
{
    private static SolidColorBrush AddressColor => (SolidColorBrush)Application.Current.Resources["AddressBrush"];
    private static SolidColorBrush HexColor => (SolidColorBrush)Application.Current.Resources["HexBrush"];
    private static SolidColorBrush AsciiColor => (SolidColorBrush)Application.Current.Resources["CommentBrush"];
    private static SolidColorBrush FgDimColor => (SolidColorBrush)Application.Current.Resources["FgDimBrush"];
    private static SolidColorBrush BpMarkerColor => (SolidColorBrush)Application.Current.Resources["BreakpointBrush"];
    private static SolidColorBrush SelectionColor => new(Color.FromRgb(0x26, 0x4F, 0x78));
    private static SolidColorBrush BpLineColor => new(Color.FromRgb(0x8B, 0x20, 0x20));

    private byte[]? _data;
    private ulong _baseAddress;
    private int _selectedIndex = -1;
    private HashSet<ulong> _bpAddresses = [];

    /// <summary>Address of the selected line (for context menu operations).</summary>
    public ulong SelectedLineAddress { get; private set; }


    public HexDumpView()
    {
        InitializeComponent();
    }

    public static readonly DependencyProperty LineFontSizeProperty = DependencyProperty.Register(
        nameof(LineFontSize), typeof(double), typeof(HexDumpView),
        new PropertyMetadata(11.0, OnLineFontSizeChanged));

    public double LineFontSize
    {
        get => (double)GetValue(LineFontSizeProperty);
        set => SetValue(LineFontSizeProperty, value);
    }

    private static void OnLineFontSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is HexDumpView v) v.ApplyLineFontSize();
    }

    private void ApplyLineFontSize()
    {
        double fs = LineFontSize;
        foreach (var item in LineList.Items)
        {
            if (item is Border b && b.Child is Grid g)
            {
                foreach (var child in g.Children)
                {
                    if (child is TextBlock tb)
                    {
                        tb.FontSize = fs;
                        foreach (var inl in tb.Inlines)
                            if (inl is Run r) r.FontSize = fs;
                        tb.InvalidateMeasure();
                    }
                }
                b.InvalidateMeasure();
            }
        }
        LineList.InvalidateMeasure();
    }

    private MainViewModel? GetViewModel()
        => Window.GetWindow(this)?.DataContext as MainViewModel;

    /// <summary>
    /// Sets hex dump data and renders the view.
    /// </summary>
    public void SetData(byte[] data, ulong baseAddress, HashSet<ulong>? bpAddresses = null)
    {
        _data = data;
        _baseAddress = baseAddress;
        _bpAddresses = bpAddresses ?? [];
        _selectedIndex = -1;
        Render();
    }

    /// <summary>
    /// Clears the hex dump display.
    /// </summary>
    public void Clear()
    {
        _data = null;
        _selectedIndex = -1;
        LineList.Items.Clear();
    }

    private void Render()
    {
        LineList.Items.Clear();
        if (_data == null || _data.Length == 0) return;

        int lineCount = (_data.Length + 15) / 16;
        for (int lineIdx = 0; lineIdx < lineCount; lineIdx++)
        {
            int offset = lineIdx * 16;
            ulong lineAddr = _baseAddress + (ulong)offset;
            bool hasBp = HasBreakpointInRange(lineAddr, 16);

            var border = CreateLine(lineIdx, offset, lineAddr, hasBp);
            border.Tag = lineIdx;
            border.MouseLeftButtonDown += OnLineClick;
            LineList.Items.Add(border);
        }
    }

    private bool HasBreakpointInRange(ulong addr, int len)
    {
        for (int i = 0; i < len; i++)
            if (_bpAddresses.Contains(addr + (ulong)i)) return true;
        return false;
    }

    private Border CreateLine(int lineIdx, int offset, ulong lineAddr, bool hasBp)
    {
        TextBlock MakeTb() => new()
        {
            FontFamily = new FontFamily("Lucida Console"),
            FontSize = LineFontSize,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var addrTb = MakeTb();
        var addrRun = new Run($"{lineAddr:X16}") { Foreground = AddressColor };
        addrRun.MouseRightButtonDown += (s, e) => { SelectLine(lineIdx); };
        addrTb.Inlines.Add(addrRun);

        var hexTb = MakeTb();
        int bytesInLine = Math.Min(16, _data!.Length - offset);
        for (int j = 0; j < 16; j++)
        {
            Run hexRun;
            if (j < bytesInLine)
            {
                byte b = _data[offset + j];
                bool isBp = _bpAddresses.Contains(lineAddr + (ulong)j);
                hexRun = new Run($"{b:X2} ")
                {
                    Foreground = isBp ? BpMarkerColor : (b == 0 ? FgDimColor : HexColor)
                };
            }
            else
            {
                hexRun = new Run("   ");
            }
            hexRun.MouseRightButtonDown += (s, e) => { SelectLine(lineIdx); };
            hexTb.Inlines.Add(hexRun);
            if (j == 7) hexTb.Inlines.Add(new Run(" "));
        }

        var asciiTb = MakeTb();
        var asciiSb = new StringBuilder(16);
        for (int j = 0; j < bytesInLine; j++)
        {
            byte b = _data[offset + j];
            asciiSb.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
        }
        var asciiRun = new Run(asciiSb.ToString()) { Foreground = AsciiColor };
        asciiRun.MouseRightButtonDown += (s, e) => { SelectLine(lineIdx); };
        asciiTb.Inlines.Add(asciiRun);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(AddressColWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(HexColWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(addrTb, 0);
        Grid.SetColumn(hexTb, 1);
        Grid.SetColumn(asciiTb, 2);
        grid.Children.Add(addrTb);
        grid.Children.Add(hexTb);
        grid.Children.Add(asciiTb);

        Brush bgBrush = hasBp ? BpLineColor : Brushes.Transparent;

        var border = new Border
        {
            Child = grid,
            Background = bgBrush,
            Padding = new Thickness(4, 1, 4, 1),
            BorderThickness = new Thickness(0),
        };
        border.ContextMenu = BuildContextMenu();
        return border;
    }

    public double AddressColWidth { get; set; } = 150;
    public double HexColWidth { get; set; } = 340;

    private void OnHexSplitterDrag0(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e) => DragHexCol(0, e.HorizontalChange);
    private void OnHexSplitterDrag1(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e) => DragHexCol(1, e.HorizontalChange);

    private void DragHexCol(int idx, double delta)
    {
        var cols = HexColumnOverlay.ColumnDefinitions;
        double newW = Math.Max(30, cols[idx].Width.Value + delta);
        cols[idx].Width = new GridLength(newW);
        if (idx == 0) AddressColWidth = newW; else HexColWidth = newW;
        foreach (var item in LineList.Items)
        {
            if (item is Border b && b.Child is Grid g && g.ColumnDefinitions.Count >= 3)
            {
                g.ColumnDefinitions[0].Width = new GridLength(AddressColWidth);
                g.ColumnDefinitions[1].Width = new GridLength(HexColWidth);
            }
        }
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();

        // Address submenu
        var addrItem = new MenuItem { Header = "Copy Address" };
        addrItem.Click += OnCopyAddress;
        menu.Items.Add(addrItem);

        // Hex submenu
        var hexLineItem = new MenuItem { Header = "Copy Hex (Line)" };
        hexLineItem.Click += OnCopyHexLine;
        menu.Items.Add(hexLineItem);

        var hexAllItem = new MenuItem { Header = "Copy Hex (All)" };
        hexAllItem.Click += OnCopyHexAll;
        menu.Items.Add(hexAllItem);

        // ASCII
        var asciiItem = new MenuItem { Header = "Copy ASCII (Line)" };
        asciiItem.Click += OnCopyAsciiLine;
        menu.Items.Add(asciiItem);

        var asciiAllItem = new MenuItem { Header = "Copy ASCII (All)" };
        asciiAllItem.Click += OnCopyAsciiAll;
        menu.Items.Add(asciiAllItem);

        menu.Items.Add(new Separator());

        // Copy full line
        var copyLineItem = new MenuItem { Header = "Copy Line" };
        copyLineItem.Click += OnCopyFullLine;
        menu.Items.Add(copyLineItem);

        var copyAllItem = new MenuItem { Header = "Copy All" };
        copyAllItem.Click += OnCopyFullAll;
        menu.Items.Add(copyAllItem);

        menu.Items.Add(new Separator());

        // Navigation
        var followDisasm = new MenuItem { Header = "Follow in Disassembler" };
        followDisasm.Click += OnFollowInDisasm;
        menu.Items.Add(followDisasm);

        menu.Items.Add(new Separator());

        // Memory breakpoints
        var memBpItem = new MenuItem { Header = "Set Memory Breakpoint (PAGE_GUARD)" };
        memBpItem.Click += OnSetMemoryBp;
        menu.Items.Add(memBpItem);

        var hwWriteBp = new MenuItem { Header = "Set HW Write Watchpoint" };
        hwWriteBp.Click += OnSetHwWriteBp;
        menu.Items.Add(hwWriteBp);

        var hwRwBp = new MenuItem { Header = "Set HW Read/Write Watchpoint" };
        hwRwBp.Click += OnSetHwRwBp;
        menu.Items.Add(hwRwBp);

        menu.Items.Add(new Separator());

        var searchBin = new MenuItem { Header = "Search Binary..." };
        searchBin.Click += (s, e) => GetViewModel()?.SearchBinaryCommand.Execute(null);
        menu.Items.Add(searchBin);

        var searchStr = new MenuItem { Header = "Search String..." };
        searchStr.Click += (s, e) => GetViewModel()?.SearchStringsCommand.Execute(null);
        menu.Items.Add(searchStr);

        return menu;
    }

    private void OnLineClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border && border.Tag is int idx)
            SelectLine(idx);
    }

    private void SelectLine(int idx)
    {
        // Deselect old
        if (_selectedIndex >= 0 && _selectedIndex < LineList.Items.Count)
        {
            var oldBorder = (Border)LineList.Items[_selectedIndex];
            int oldOffset = _selectedIndex * 16;
            ulong oldAddr = _baseAddress + (ulong)oldOffset;
            bool oldHasBp = HasBreakpointInRange(oldAddr, 16);
            oldBorder.Background = oldHasBp ? BpLineColor : Brushes.Transparent;
        }

        _selectedIndex = idx;
        SelectedLineAddress = _baseAddress + (ulong)(idx * 16);

        // Highlight new
        if (idx >= 0 && idx < LineList.Items.Count)
        {
            var border = (Border)LineList.Items[idx];
            border.Background = SelectionColor;
        }
    }

    // === Copy handlers ===

    private int GetLineOffset() => _selectedIndex >= 0 ? _selectedIndex * 16 : 0;

    private void OnCopyAddress(object sender, RoutedEventArgs e)
    {
        if (_selectedIndex < 0) return;
        Clipboard.SetText($"{SelectedLineAddress:X16}");
    }

    private void OnCopyHexLine(object sender, RoutedEventArgs e)
    {
        if (_data == null || _selectedIndex < 0) return;
        int off = GetLineOffset();
        int len = Math.Min(16, _data.Length - off);
        if (len <= 0) return;
        Clipboard.SetText(BitConverter.ToString(_data, off, len).Replace("-", " "));
    }

    private void OnCopyHexAll(object sender, RoutedEventArgs e)
    {
        if (_data == null) return;
        Clipboard.SetText(BitConverter.ToString(_data).Replace("-", " "));
    }

    private void OnCopyAsciiLine(object sender, RoutedEventArgs e)
    {
        if (_data == null || _selectedIndex < 0) return;
        int off = GetLineOffset();
        int len = Math.Min(16, _data.Length - off);
        var sb = new StringBuilder(len);
        for (int i = 0; i < len; i++)
        {
            byte b = _data[off + i];
            sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
        }
        Clipboard.SetText(sb.ToString());
    }

    private void OnCopyAsciiAll(object sender, RoutedEventArgs e)
    {
        if (_data == null) return;
        var sb = new StringBuilder();
        for (int i = 0; i < _data.Length; i += 16)
        {
            int len = Math.Min(16, _data.Length - i);
            for (int j = 0; j < len; j++)
            {
                byte b = _data[i + j];
                sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
            }
            sb.AppendLine();
        }
        Clipboard.SetText(sb.ToString());
    }

    private void OnCopyFullLine(object sender, RoutedEventArgs e)
    {
        if (_data == null || _selectedIndex < 0) return;
        Clipboard.SetText(FormatLine(GetLineOffset()));
    }

    private void OnCopyFullAll(object sender, RoutedEventArgs e)
    {
        if (_data == null) return;
        var sb = new StringBuilder();
        for (int i = 0; i < _data.Length; i += 16)
            sb.AppendLine(FormatLine(i));
        Clipboard.SetText(sb.ToString());
    }

    private string FormatLine(int offset)
    {
        ulong addr = _baseAddress + (ulong)offset;
        int len = Math.Min(16, _data!.Length - offset);
        var sb = new StringBuilder();
        sb.Append($"{addr:X16}  ");
        for (int j = 0; j < 16; j++)
        {
            if (j < len)
                sb.Append($"{_data[offset + j]:X2} ");
            else
                sb.Append("   ");
            if (j == 7) sb.Append(' ');
        }
        sb.Append(' ');
        for (int j = 0; j < len; j++)
        {
            byte b = _data[offset + j];
            sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
        }
        return sb.ToString();
    }

    // === Navigation ===

    private void OnFollowInDisasm(object sender, RoutedEventArgs e)
    {
        if (_selectedIndex < 0) return;
        GetViewModel()?.FollowInDisasmCommand.Execute(SelectedLineAddress);
    }

    // === Breakpoint handlers ===

    private void OnSetMemoryBp(object sender, RoutedEventArgs e)
    {
        if (_selectedIndex < 0) return;
        var vm = GetViewModel();
        if (vm == null) return;
        vm.SetBreakpointAtAddressWithType(SelectedLineAddress, Models.BreakpointType.Memory);
    }

    private void OnSetHwWriteBp(object sender, RoutedEventArgs e)
    {
        if (_selectedIndex < 0) return;
        var vm = GetViewModel();
        if (vm == null) return;
        vm.SetBreakpointAtAddressWithType(SelectedLineAddress, Models.BreakpointType.HwWrite);
    }

    private void OnSetHwRwBp(object sender, RoutedEventArgs e)
    {
        if (_selectedIndex < 0) return;
        var vm = GetViewModel();
        if (vm == null) return;
        vm.SetBreakpointAtAddressWithType(SelectedLineAddress, Models.BreakpointType.HwReadWrite);
    }
}
