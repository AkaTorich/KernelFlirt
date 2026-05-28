using System.Buffers.Binary;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using KernelFlirt.SDK;

namespace ConverterPlugin;

// Плагин: конвертер систем счисления (DEC / HEX / OCT / BIN / ASCII) с
// настраиваемой разрядностью (8/16/32/64 бита), знаковостью, инверсией
// порядка байт и быстрым импортом значения из RIP или из памяти.
//
// Цвета элементов UI намеренно НЕ задаются — отрисовка делегируется
// активной теме оболочки.
public class Plugin : IKernelFlirtPlugin
{
    public string Name => "Converter";
    public string Description => "Number base converter: DEC / HEX / OCT / BIN / ASCII with size, signed mode, endian swap, and RIP / memory pickers.";
    public string Version => "1.0";

    private ConverterPanel? _panel;
    private IDebuggerApi? _api;

    public void Initialize(IDebuggerApi api)
    {
        _api = api;
        _panel = new ConverterPanel(api);
        // Регистрируем панель — UI сам построит вкладку (заголовок + иконка по имени плагина).
        api.UI.AddToolPanel("Converter", _panel);
        api.UI.AddMenuItem("Send RIP to _Converter", () => _panel?.LoadFromRip());
    }

    public void Shutdown() { /* no persistent state */ }
}

internal sealed class ConverterPanel : ScrollViewer
{
    private readonly IDebuggerApi _api;

    // ── Состояние ──────────────────────────────────────────────────────
    private ulong _value;        // текущее значение (внутреннее представление — 64 бита)
    private int   _bits = 64;    // ширина в битах: 8, 16, 32 или 64
    private bool  _syncing;      // защита от рекурсии при обновлении полей

    // ── UI-поля ────────────────────────────────────────────────────────
    private readonly TextBox _txtDec   = new();
    private readonly TextBox _txtHex   = new();
    private readonly TextBox _txtOct   = new();
    private readonly TextBox _txtBin   = new();
    private readonly TextBox _txtAscii = new() { IsReadOnly = true };
    private readonly TextBox _txtBytes = new() { IsReadOnly = true };

    private readonly RadioButton _rb8;
    private readonly RadioButton _rb16;
    private readonly RadioButton _rb32;
    private readonly RadioButton _rb64;
    private readonly CheckBox    _chkSigned = new() { Content = "Signed (DEC)" };

    private readonly TextBlock _status = new();

    public ConverterPanel(IDebuggerApi api)
    {
        _api = api;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;

        var root = new StackPanel { Margin = new Thickness(10) };

        // ── Заголовок ─────────────────────────────────────────────────
        root.Children.Add(new TextBlock
        {
            Text = "Number Base Converter",
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 8)
        });

        // ── Разрядность + знак ───────────────────────────────────────
        const string sizeGroup = "ConverterSizeGroup";
        _rb8  = new RadioButton { GroupName = sizeGroup, Content = "8",  Margin = new Thickness(0, 0, 8, 0) };
        _rb16 = new RadioButton { GroupName = sizeGroup, Content = "16", Margin = new Thickness(0, 0, 8, 0) };
        _rb32 = new RadioButton { GroupName = sizeGroup, Content = "32", Margin = new Thickness(0, 0, 8, 0) };
        _rb64 = new RadioButton { GroupName = sizeGroup, Content = "64", Margin = new Thickness(0, 0, 16, 0), IsChecked = true };

        var sizeRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        sizeRow.Children.Add(new TextBlock { Text = "Size (bits):", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        sizeRow.Children.Add(_rb8); sizeRow.Children.Add(_rb16); sizeRow.Children.Add(_rb32); sizeRow.Children.Add(_rb64);
        sizeRow.Children.Add(_chkSigned);
        root.Children.Add(sizeRow);

        foreach (var rb in new[] { _rb8, _rb16, _rb32, _rb64 })
            rb.Checked += OnSizeChanged;
        _chkSigned.Checked   += (_, _) => RefreshAllFields();
        _chkSigned.Unchecked += (_, _) => RefreshAllFields();

        // ── Поля ─────────────────────────────────────────────────────
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        AddRow(grid, 0, "DEC:",   _txtDec);
        AddRow(grid, 1, "HEX:",   _txtHex);
        AddRow(grid, 2, "OCT:",   _txtOct);
        AddRow(grid, 3, "BIN:",   _txtBin);
        AddRow(grid, 4, "ASCII:", _txtAscii);
        AddRow(grid, 5, "Bytes (LE):", _txtBytes);
        root.Children.Add(grid);

        // Моноширинный шрифт — это удобство для чтения чисел, не цвет.
        // Тема может переопределить FontFamily глобально, тогда наш Consolas
        // используется только как локальный fallback.
        foreach (var tb in new[] { _txtDec, _txtHex, _txtOct, _txtBin, _txtAscii, _txtBytes })
            tb.FontFamily = new System.Windows.Media.FontFamily("Consolas");

        // Подписки на ввод — каждый редактируемый TextBox обновляет _value
        // и пересчитывает остальные поля.
        _txtDec.TextChanged += (_, _) => HandleInput(_txtDec, ParseDec);
        _txtHex.TextChanged += (_, _) => HandleInput(_txtHex, ParseHex);
        _txtOct.TextChanged += (_, _) => HandleInput(_txtOct, ParseOct);
        _txtBin.TextChanged += (_, _) => HandleInput(_txtBin, ParseBin);

        // ── Кнопки ───────────────────────────────────────────────────
        var btns = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };

        var btnFromRip = new Button { Content = "From RIP", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 6, 0) };
        btnFromRip.Click += (_, _) => LoadFromRip();
        btns.Children.Add(btnFromRip);

        var btnFromAddr = new Button { Content = "From Address...", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 6, 0) };
        btnFromAddr.Click += (_, _) => LoadFromAddress();
        btns.Children.Add(btnFromAddr);

        var btnSwap = new Button { Content = "Byte Swap", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 6, 0), ToolTip = "Reverse byte order within the selected size" };
        btnSwap.Click += (_, _) => ByteSwap();
        btns.Children.Add(btnSwap);

        var btnNot = new Button { Content = "NOT", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 6, 0), ToolTip = "Bitwise complement within the selected size" };
        btnNot.Click += (_, _) => { SetValue(~_value); };
        btns.Children.Add(btnNot);

        var btnClear = new Button { Content = "Clear", Padding = new Thickness(12, 4, 12, 4) };
        btnClear.Click += (_, _) => SetValue(0);
        btns.Children.Add(btnClear);

        root.Children.Add(btns);

        // ── Статус ───────────────────────────────────────────────────
        _status.Margin = new Thickness(0, 8, 0, 0);
        _status.FontStyle = FontStyles.Italic;
        _status.TextWrapping = TextWrapping.Wrap;
        root.Children.Add(_status);

        Content = root;
        RefreshAllFields();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static void AddRow(Grid grid, int row, string label, FrameworkElement editor)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var lbl = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 4, 8, 4),
            MinWidth = 70
        };
        Grid.SetRow(lbl, row); Grid.SetColumn(lbl, 0);
        grid.Children.Add(lbl);

        editor.Margin = new Thickness(0, 4, 0, 4);
        Grid.SetRow(editor, row); Grid.SetColumn(editor, 1);
        grid.Children.Add(editor);
    }

    private void OnSizeChanged(object? sender, RoutedEventArgs e)
    {
        if (_rb8.IsChecked == true)       _bits = 8;
        else if (_rb16.IsChecked == true) _bits = 16;
        else if (_rb32.IsChecked == true) _bits = 32;
        else                              _bits = 64;
        // Усекаем значение до выбранной ширины — пользователь увидит маскированное значение.
        _value &= Mask(_bits);
        RefreshAllFields();
    }

    private static ulong Mask(int bits) => bits == 64 ? ulong.MaxValue : (1UL << bits) - 1;

    private void HandleInput(TextBox source, Func<string, ulong?> parser)
    {
        if (_syncing) return;
        var text = source.Text;
        var parsed = parser(text);
        if (parsed == null)
        {
            _status.Text = $"Parse error: '{text.Trim()}'";
            return;
        }
        _value = parsed.Value & Mask(_bits);
        _status.Text = $"value = 0x{_value:X} ({_bits}-bit)";
        RefreshAllFields(skip: source);
    }

    private void SetValue(ulong v)
    {
        _value = v & Mask(_bits);
        _status.Text = $"value = 0x{_value:X} ({_bits}-bit)";
        RefreshAllFields();
    }

    // ── Обновление всех полей из _value ──────────────────────────────

    private void RefreshAllFields(TextBox? skip = null)
    {
        _syncing = true;
        try
        {
            ulong masked = _value & Mask(_bits);

            if (skip != _txtDec) _txtDec.Text = FormatDec(masked);
            if (skip != _txtHex) _txtHex.Text = FormatHex(masked);
            if (skip != _txtOct) _txtOct.Text = FormatOct(masked);
            if (skip != _txtBin) _txtBin.Text = FormatBin(masked);

            _txtAscii.Text = FormatAscii(masked);
            _txtBytes.Text = FormatBytesLE(masked);
        }
        finally { _syncing = false; }
    }

    // ── Форматирование вывода ────────────────────────────────────────

    private string FormatDec(ulong v)
    {
        if (!_signedDecChecked()) return v.ToString(CultureInfo.InvariantCulture);
        long sv = ToSigned(v, _bits);
        return sv.ToString(CultureInfo.InvariantCulture);
    }

    private bool _signedDecChecked() => _chkSigned.IsChecked == true;

    private string FormatHex(ulong v) => "0x" + v.ToString("X" + (_bits / 4), CultureInfo.InvariantCulture);

    private string FormatOct(ulong v)
    {
        if (v == 0) return "0";
        // Ручной перевод — стандартный Convert.ToString не работает с ulong для основания 8.
        Span<char> buf = stackalloc char[24];   // 64 бита -> максимум 22 восьмеричных цифры
        int idx = buf.Length;
        while (v != 0)
        {
            buf[--idx] = (char)('0' + (int)(v & 0x7));
            v >>= 3;
        }
        return new string(buf[idx..]);
    }

    private string FormatBin(ulong v)
    {
        // Слева ведущие нули по ширине, группировка по 4 бита для читаемости.
        var raw = new char[_bits];
        for (int i = 0; i < _bits; i++)
            raw[_bits - 1 - i] = ((v >> i) & 1) == 1 ? '1' : '0';
        var groups = new List<string>();
        for (int i = 0; i < _bits; i += 4)
            groups.Add(new string(raw, i, Math.Min(4, _bits - i)));
        return string.Join(' ', groups);
    }

    private string FormatAscii(ulong v)
    {
        int n = _bits / 8;
        var chars = new char[n];
        // Little-endian: младший байт первым.
        for (int i = 0; i < n; i++)
        {
            byte b = (byte)((v >> (i * 8)) & 0xFF);
            chars[i] = (b >= 0x20 && b < 0x7F) ? (char)b : '.';
        }
        return new string(chars);
    }

    private string FormatBytesLE(ulong v)
    {
        int n = _bits / 8;
        var parts = new string[n];
        for (int i = 0; i < n; i++)
            parts[i] = ((byte)((v >> (i * 8)) & 0xFF)).ToString("X2", CultureInfo.InvariantCulture);
        return string.Join(' ', parts);
    }

    private static long ToSigned(ulong v, int bits)
    {
        ulong signBit = 1UL << (bits - 1);
        if ((v & signBit) == 0) return (long)v;
        // Знак установлен → расширяем единицами в верхние биты.
        return (long)(v | ~Mask(bits));
    }

    // ── Парсинг ввода ────────────────────────────────────────────────

    private ulong? ParseDec(string s)
    {
        s = s.Trim();
        if (s.Length == 0) return 0;
        if (_signedDecChecked())
        {
            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long lv))
                return unchecked((ulong)lv);
        }
        else
        {
            // Без знакового — сначала пробуем ulong, потом long (для '-1' и пр.).
            if (ulong.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong uv))
                return uv;
            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long lv))
                return unchecked((ulong)lv);
        }
        return null;
    }

    private ulong? ParseHex(string s)
    {
        s = s.Trim();
        if (s.Length == 0) return 0;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        if (s.EndsWith("h", StringComparison.OrdinalIgnoreCase)) s = s[..^1];
        if (s.Length == 0) return 0;
        return ulong.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong v) ? v : null;
    }

    private ulong? ParseOct(string s)
    {
        s = s.Trim();
        if (s.Length == 0) return 0;
        if (s.StartsWith("0o", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        ulong result = 0;
        foreach (char c in s)
        {
            if (c < '0' || c > '7') return null;
            // Защита от переполнения: если старшие 3 бита заняты, новая цифра не влезает.
            if ((result & (0x7UL << 61)) != 0) return null;
            result = (result << 3) | (uint)(c - '0');
        }
        return result;
    }

    private ulong? ParseBin(string s)
    {
        // Принимаем 0b-префикс, разрешаем пробелы и подчёркивания как разделители.
        s = s.Trim();
        if (s.StartsWith("0b", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        ulong result = 0;
        int count = 0;
        foreach (char c in s)
        {
            if (c == ' ' || c == '_') continue;
            if (c != '0' && c != '1') return null;
            if (++count > 64) return null;
            result = (result << 1) | (uint)(c - '0');
        }
        return result;
    }

    // ── Кнопки ───────────────────────────────────────────────────────

    public void LoadFromRip()
    {
        if (_api == null) return;
        if (!_api.IsConnected) { _status.Text = "Not connected"; return; }
        if (_api.TargetPid == 0 || _api.SelectedThreadId == 0)
        { _status.Text = "No target / thread"; return; }

        var regs = _api.Memory.ReadRegisters(_api.TargetPid, _api.SelectedThreadId);
        if (regs == null) { _status.Text = "ReadRegisters failed"; return; }

        var rip = regs.FirstOrDefault(r =>
            r.Name.Equals("RIP", StringComparison.OrdinalIgnoreCase) ||
            r.Name.Equals("EIP", StringComparison.OrdinalIgnoreCase));
        if (rip == null) { _status.Text = "RIP/EIP not found in register snapshot"; return; }

        // На WoW64-таргете автоматически переключаем разрядность на 32 бита.
        if (_api.Is32Bit && _bits == 64)
        {
            _bits = 32; _rb32.IsChecked = true;
        }
        SetValue(rip.Value);
        _status.Text = $"Loaded {rip.Name} = 0x{rip.Value:X}";
    }

    private void LoadFromAddress()
    {
        if (_api == null) return;
        if (!_api.IsConnected || _api.TargetPid == 0)
        { _status.Text = "Not connected / no target"; return; }

        string? addrStr = PromptString("Read From Address", "Address (hex):", "");
        if (string.IsNullOrWhiteSpace(addrStr)) return;
        if (!TryParseHexAddress(addrStr, out ulong addr))
        { _status.Text = $"Bad address: {addrStr}"; return; }

        uint size = (uint)(_bits / 8);
        var data = _api.Memory.ReadMemory(_api.TargetPid, addr, size);
        if (data == null || data.Length < size)
        { _status.Text = $"ReadMemory({addr:X}, {size}) failed"; return; }

        ulong v = 0;
        for (int i = 0; i < size; i++) v |= (ulong)data[i] << (i * 8);
        SetValue(v);
        _status.Text = $"Read {size} byte(s) at 0x{addr:X}";
    }

    private void ByteSwap()
    {
        ulong masked = _value & Mask(_bits);
        ulong swapped = _bits switch
        {
            8  => masked,
            16 => BinaryPrimitives.ReverseEndianness((ushort)masked),
            32 => BinaryPrimitives.ReverseEndianness((uint)masked),
            64 => BinaryPrimitives.ReverseEndianness(masked),
            _  => masked
        };
        SetValue(swapped);
        _status.Text = $"Byte swapped: 0x{masked:X} -> 0x{swapped & Mask(_bits):X}";
    }

    // ── Утилиты ──────────────────────────────────────────────────────

    private static bool TryParseHexAddress(string s, out ulong addr)
    {
        addr = 0;
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return ulong.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out addr);
    }

    private static string? PromptString(string title, string prompt, string defaultValue)
    {
        var dlg = new Window
        {
            Title = title,
            Width = 380, Height = 140,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Owner = Application.Current.MainWindow
        };

        var sp = new StackPanel { Margin = new Thickness(12) };
        sp.Children.Add(new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 6) });
        var tb = new TextBox { Text = defaultValue };
        sp.Children.Add(tb);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0)
        };
        var ok = new Button { Content = "OK", Width = 70, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
        ok.Click += (_, _) => { dlg.DialogResult = true; dlg.Close(); };
        var cancel = new Button { Content = "Cancel", Width = 70, IsCancel = true };
        row.Children.Add(ok);
        row.Children.Add(cancel);
        sp.Children.Add(row);

        dlg.Content = sp;
        tb.Focus(); tb.SelectAll();
        return dlg.ShowDialog() == true ? tb.Text : null;
    }
}
