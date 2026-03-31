using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KernelFlirt.SDK;

namespace BookmarksPlugin;

public class Bookmark
{
    public ulong Address { get; set; }
    public string Note { get; set; } = "";
    public string Module { get; set; } = "";
    public ulong Offset { get; set; }

    public string AddressHex => $"{Address:X16}";
    public string OffsetHex => Offset != 0 ? $"+{Offset:X}" : "";
    public string Display => string.IsNullOrEmpty(Module) ? AddressHex : $"{Module}{OffsetHex}";
}

public class Plugin : IKernelFlirtPlugin
{
    public string Name => "Bookmarks";
    public string Description => "Address bookmarks with notes, persisted between sessions. Annotations in disassembly.";
    public string Version => "1.0";

    private IDebuggerApi _api = null!;
    private readonly List<Bookmark> _bookmarks = [];
    private DataGrid _grid = null!;
    private string _savePath = "";
    private string _pluginsDir = "";
    private string _currentTarget = "";

    public void Initialize(IDebuggerApi api)
    {
        _api = api;
        _pluginsDir = Path.Combine(AppContext.BaseDirectory, "plugins");

        BuildUi();

        api.UI.AddMenuItem("Add _Bookmark at RIP", OnAddAtRip);
        api.OnBreakStateEntered += () =>
            Application.Current.Dispatcher.BeginInvoke(() => UpdateTarget());

        // Listen for notes added/edited/removed from disasm context menu
        api.UI.OnNoteAdded += (addr, note) =>
            Application.Current.Dispatcher.BeginInvoke(() => OnExternalNoteAdded(addr, note));
        api.UI.OnNoteEdited += (addr, note) =>
            Application.Current.Dispatcher.BeginInvoke(() => OnExternalNoteEdited(addr, note));
        api.UI.OnNoteRemoved += addr =>
            Application.Current.Dispatcher.BeginInvoke(() => OnExternalNoteRemoved(addr));
    }

    public void Shutdown()
    {
        SaveToDisk();
    }

    private void BuildUi()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Toolbar
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4) };

        var addBtn = new Button { Content = "+ Add", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 0) };
        addBtn.Click += (_, _) => OnAddBookmark();
        toolbar.Children.Add(addBtn);

        var removeBtn = new Button { Content = "- Remove", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 0) };
        removeBtn.Click += (_, _) => OnRemoveSelected();
        toolbar.Children.Add(removeBtn);

        var editBtn = new Button { Content = "Edit Note", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 0) };
        editBtn.Click += (_, _) => OnEditSelected();
        toolbar.Children.Add(editBtn);

        Grid.SetRow(toolbar, 0);
        root.Children.Add(toolbar);

        // DataGrid
        _grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            SelectionMode = DataGridSelectionMode.Single,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.None,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            RowBackground = Brushes.Transparent,
            AlternatingRowBackground = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12
        };

        _grid.Columns.Add(new DataGridTextColumn { Header = "Address", Binding = new System.Windows.Data.Binding("AddressHex"), Width = 150 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Module", Binding = new System.Windows.Data.Binding("Display"), Width = 160 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Note", Binding = new System.Windows.Data.Binding("Note"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });

        _grid.MouseDoubleClick += OnGridDoubleClick;

        // Context menu
        var ctx = new ContextMenu();
        var goToItem = new MenuItem { Header = "Go to Bookmark" };
        goToItem.Click += (_, _) => { if (_grid.SelectedItem is Bookmark bm) _api.UI.NavigateDisassembly(bm.Address); };
        ctx.Items.Add(goToItem);

        var editItem = new MenuItem { Header = "Edit Bookmark/Note..." };
        editItem.Click += (_, _) => OnEditSelected();
        ctx.Items.Add(editItem);

        var removeItem = new MenuItem { Header = "Remove Bookmark/Note" };
        removeItem.Click += (_, _) => OnRemoveSelected();
        ctx.Items.Add(removeItem);

        _grid.ContextMenu = ctx;

        Grid.SetRow(_grid, 1);
        root.Children.Add(_grid);

        // Status
        var status = new TextBlock
        {
            Margin = new Thickness(4),
            Foreground = Brushes.Gray,
            FontSize = 11
        };
        status.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Items.Count")
        {
            Source = _grid,
            StringFormat = "{0} bookmark(s)"
        });
        Grid.SetRow(status, 2);
        root.Children.Add(status);

        RefreshGrid();
        _api.UI.AddToolPanel("Bookmarks/Notes", root);
    }

    private void UpdateTarget()
    {
        // Determine target name from main module
        string target = "";
        var modules = _api.Symbols.GetModules();
        if (modules.Count > 0)
            target = Path.GetFileNameWithoutExtension(modules[0].Name);

        // Fallback to kernel module at current RIP (driver debugging)
        if (string.IsNullOrEmpty(target))
        {
            var kmods = _api.Symbols.GetKernelModules();
            var regs = _api.Memory.ReadRegisters(_api.TargetPid, _api.SelectedThreadId);
            var rip = regs.FirstOrDefault(r => r.Name == "RIP" || r.Name == "EIP");
            if (rip != null)
            {
                var km = kmods.FirstOrDefault(m =>
                    rip.Value >= m.BaseAddress && rip.Value < m.BaseAddress + m.Size);
                if (km != null)
                    target = Path.GetFileNameWithoutExtension(km.Name);
            }
        }

        if (string.IsNullOrEmpty(target) || target == _currentTarget)
            return;

        // Save current bookmarks before switching
        if (!string.IsNullOrEmpty(_currentTarget))
            SaveToDisk();

        // Clear old annotations
        foreach (var bm in _bookmarks)
            _api.UI.SetAddressAnnotation(bm.Address, null);
        _bookmarks.Clear();

        // Switch to new target
        _currentTarget = target;
        // Strip _kfdebug suffix for service debugging
        var cleanName = _currentTarget.Replace("_kfdebug", "");
        _savePath = Path.Combine(_pluginsDir, $"{cleanName}.bookmarks.json");

        LoadFromDisk();
        SyncAnnotations();
        RefreshGrid();
    }

    private void RefreshGrid()
    {
        _grid.ItemsSource = null;
        _grid.ItemsSource = _bookmarks;
    }

    private void OnGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_grid.SelectedItem is Bookmark bm)
            _api.UI.NavigateDisassembly(bm.Address);
    }

    // Context menu events from disasm view
    private void OnExternalNoteAdded(ulong addr, string note)
    {
        if (_bookmarks.Any(b => b.Address == addr)) { OnExternalNoteEdited(addr, note); return; }
        AddBookmark(addr, note);
    }

    private void OnExternalNoteEdited(ulong addr, string note)
    {
        var bm = _bookmarks.FirstOrDefault(b => b.Address == addr);
        if (bm == null) { AddBookmark(addr, note); return; }
        bm.Note = note;
        RefreshGrid();
        SaveToDisk();
    }

    private void OnExternalNoteRemoved(ulong addr)
    {
        var bm = _bookmarks.FirstOrDefault(b => b.Address == addr);
        if (bm == null) return;
        _bookmarks.Remove(bm);
        RefreshGrid();
        SaveToDisk();
    }

    private void OnAddAtRip()
    {
        if (!_api.IsBreakState) { _api.Log.Info("Must be in break state"); return; }
        var regs = _api.Memory.ReadRegisters(_api.TargetPid, _api.SelectedThreadId);
        var rip = regs.FirstOrDefault(r => r.Name == "RIP" || r.Name == "EIP");
        if (rip == null) return;

        var addr = rip.Value;
        if (_bookmarks.Any(b => b.Address == addr))
        {
            _api.Log.Info($"Bookmark already exists at {addr:X16}");
            return;
        }

        var note = PromptString("Bookmark Note", $"Note for {addr:X16}:", "");
        AddBookmark(addr, note ?? "");
    }

    private void OnAddBookmark()
    {
        var addrStr = PromptString("Add Bookmark", "Address (hex):", "");
        if (string.IsNullOrWhiteSpace(addrStr)) return;

        if (!ulong.TryParse(addrStr.Replace("0x", "").Replace("0X", ""),
                System.Globalization.NumberStyles.HexNumber, null, out ulong addr))
        {
            _api.Log.Warning($"Invalid address: {addrStr}");
            return;
        }

        if (_bookmarks.Any(b => b.Address == addr))
        {
            _api.Log.Info($"Bookmark already exists at {addr:X16}");
            return;
        }

        var note = PromptString("Bookmark Note", $"Note for {addr:X16}:", "");
        AddBookmark(addr, note ?? "");
    }

    private void AddBookmark(ulong addr, string note)
    {
        var bm = new Bookmark { Address = addr, Note = note };

        // Try to resolve module + offset
        var modules = _api.Symbols.GetModules();
        foreach (var mod in modules)
        {
            if (addr >= mod.BaseAddress && addr < mod.BaseAddress + mod.Size)
            {
                bm.Module = mod.Name;
                bm.Offset = addr - mod.BaseAddress;
                break;
            }
        }

        _bookmarks.Add(bm);
        RefreshGrid();
        SyncAnnotations();
        SaveToDisk();
        _api.Log.Info($"[Bookmark] Added: {addr:X16} — {note}");
    }

    private void OnRemoveSelected()
    {
        if (_grid.SelectedItem is not Bookmark bm) return;
        _bookmarks.Remove(bm);
        _api.UI.SetAddressAnnotation(bm.Address, null);
        RefreshGrid();
        _api.UI.RefreshDisassembly();
        SaveToDisk();
    }

    private void OnEditSelected()
    {
        if (_grid.SelectedItem is not Bookmark bm) return;
        var note = PromptString("Edit Note", $"Note for {bm.Address:X16}:", bm.Note);
        if (note == null) return;
        bm.Note = note;
        RefreshGrid();
        SyncAnnotations();
        SaveToDisk();
    }

    private void SyncAnnotations()
    {
        foreach (var bm in _bookmarks)
        {
            var text = string.IsNullOrEmpty(bm.Note) ? "[bookmark]" : bm.Note;
            _api.UI.SetAddressAnnotation(bm.Address, text);
        }
        _api.UI.RefreshDisassembly();
    }

    // ── Persistence ──

    private record BookmarkDto(string Address, string Note, string Module, string Offset);

    private void SaveToDisk()
    {
        try
        {
            var dtos = _bookmarks.Select(b => new BookmarkDto(
                $"{b.Address:X16}", b.Note, b.Module, $"{b.Offset:X}")).ToList();
            var json = JsonSerializer.Serialize(dtos, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_savePath, json);
        }
        catch (Exception ex)
        {
            _api.Log.Warning($"[Bookmark] Save failed: {ex.Message}");
        }
    }

    private void LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_savePath)) return;
            var json = File.ReadAllText(_savePath);
            var dtos = JsonSerializer.Deserialize<List<BookmarkDto>>(json);
            if (dtos == null) return;

            foreach (var dto in dtos)
            {
                if (!ulong.TryParse(dto.Address, System.Globalization.NumberStyles.HexNumber, null, out ulong addr))
                    continue;
                ulong.TryParse(dto.Offset, System.Globalization.NumberStyles.HexNumber, null, out ulong offset);
                _bookmarks.Add(new Bookmark
                {
                    Address = addr,
                    Note = dto.Note,
                    Module = dto.Module,
                    Offset = offset
                });
            }
        }
        catch (Exception ex)
        {
            _api.Log.Warning($"[Bookmark] Load failed: {ex.Message}");
        }
    }

    private static string? PromptString(string title, string prompt, string defaultValue)
    {
        var dlg = new Window
        {
            Title = title,
            Width = 400, Height = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Owner = Application.Current.MainWindow
        };

        var sp = new StackPanel { Margin = new Thickness(12) };
        sp.Children.Add(new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 6) });

        var tb = new TextBox { Text = defaultValue };
        sp.Children.Add(tb);

        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var okBtn = new Button { Content = "OK", Width = 70, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
        okBtn.Click += (_, _) => { dlg.DialogResult = true; dlg.Close(); };
        var cancelBtn = new Button { Content = "Cancel", Width = 70, IsCancel = true };
        btnPanel.Children.Add(okBtn);
        btnPanel.Children.Add(cancelBtn);
        sp.Children.Add(btnPanel);

        dlg.Content = sp;
        tb.Focus();
        tb.SelectAll();

        return dlg.ShowDialog() == true ? tb.Text : null;
    }
}
