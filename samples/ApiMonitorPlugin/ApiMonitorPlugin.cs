using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using KernelFlirt.SDK;
using Microsoft.Win32;

namespace ApiMonitorPlugin;

public class ApiMonitorPlugin : IKernelFlirtPlugin
{
    public string Name => "API Monitor";
    public string Description => "Intercept and log WinAPI calls with arguments and return values";
    public string Version => "1.0";

    private IDebuggerApi? _api;
    private ApiMonitorPanel? _panel;

    public void Initialize(IDebuggerApi api)
    {
        _api = api;
        _panel = new ApiMonitorPanel(api);
        api.UI.AddToolPanel("API Monitor", _panel);
        api.UI.AddMenuItem("API Monitor: Start", () => _panel.StartMonitoring());
        api.UI.AddMenuItem("API Monitor: Stop", () => _panel.StopMonitoring());
        api.OnDebugEventFilter += OnDebugEventFilter;
        api.Log.Info("API Monitor v1.0 loaded. See 'API Monitor' tab.");
    }

    private bool OnDebugEventFilter(PluginDebugEvent evt)
    {
        if (_panel == null || !_panel.IsMonitoring) return false;
        return _panel.HandleDebugEvent(evt);
    }

    public void Shutdown()
    {
        _panel?.StopMonitoring();
        _api?.Log.Info("API Monitor plugin unloaded");
    }
}

// ═══════════════════════════════════════════════════════════════════════
//  API definition: name, module, parameter descriptors
// ═══════════════════════════════════════════════════════════════════════

public enum ParamType
{
    UInt,       // generic uint/ulong (hex)
    Int,        // signed int
    Pointer,    // pointer (hex)
    String,     // LPCSTR — read null-terminated ASCII
    WString,    // LPCWSTR — read null-terminated Unicode
    Handle,     // HANDLE (hex)
    Bool,       // BOOL (0/1)
    Flags,      // hex flags
    Buffer,     // pointer to buffer (show first N bytes)
    Void        // no parameter / ignore
}

public enum ApiCategory
{
    File, Registry, Process, Thread, Memory, Network, Sync, Library, Misc
}

public class ApiParamDef
{
    public string Name { get; init; } = "";
    public ParamType Type { get; init; }
}

public class ApiDef
{
    public string Module { get; init; } = "";
    public string Function { get; init; } = "";
    public ApiCategory Category { get; init; }
    public ApiParamDef[] Params { get; init; } = [];
    public ParamType ReturnType { get; init; } = ParamType.UInt;
    public string FullName => $"{Module}!{Function}";
}

// ═══════════════════════════════════════════════════════════════════════
//  Log entry for each captured API call
// ═══════════════════════════════════════════════════════════════════════

public class ApiCallEntry : INotifyPropertyChanged
{
    public int Index { get; set; }
    public string Time { get; set; } = "";
    public uint ThreadId { get; set; }
    public string Module { get; set; } = "";
    public string Function { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string ReturnValue { get; set; } = "...";
    public ApiCategory Category { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    public void NotifyReturnChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ReturnValue)));
    }
}

// ═══════════════════════════════════════════════════════════════════════
//  Predefined API list — most commonly monitored functions
// ═══════════════════════════════════════════════════════════════════════

public static class ApiDatabase
{
    public static readonly ApiDef[] Apis =
    [
        // ── File I/O ──
        new() { Module = "kernelbase", Function = "CreateFileW", Category = ApiCategory.File,
            Params = [new(){Name="Path",Type=ParamType.WString}, new(){Name="Access",Type=ParamType.Flags},
                      new(){Name="Share",Type=ParamType.Flags}, new(){Name="Security",Type=ParamType.Pointer}],
            ReturnType = ParamType.Handle },
        new() { Module = "kernelbase", Function = "CreateFileA", Category = ApiCategory.File,
            Params = [new(){Name="Path",Type=ParamType.String}, new(){Name="Access",Type=ParamType.Flags},
                      new(){Name="Share",Type=ParamType.Flags}, new(){Name="Security",Type=ParamType.Pointer}],
            ReturnType = ParamType.Handle },
        new() { Module = "kernelbase", Function = "ReadFile", Category = ApiCategory.File,
            Params = [new(){Name="hFile",Type=ParamType.Handle}, new(){Name="Buffer",Type=ParamType.Pointer},
                      new(){Name="Size",Type=ParamType.UInt}, new(){Name="BytesRead",Type=ParamType.Pointer}],
            ReturnType = ParamType.Bool },
        new() { Module = "kernelbase", Function = "WriteFile", Category = ApiCategory.File,
            Params = [new(){Name="hFile",Type=ParamType.Handle}, new(){Name="Buffer",Type=ParamType.Pointer},
                      new(){Name="Size",Type=ParamType.UInt}, new(){Name="BytesWritten",Type=ParamType.Pointer}],
            ReturnType = ParamType.Bool },
        new() { Module = "kernelbase", Function = "DeleteFileW", Category = ApiCategory.File,
            Params = [new(){Name="Path",Type=ParamType.WString}],
            ReturnType = ParamType.Bool },
        new() { Module = "ntdll", Function = "NtCreateFile", Category = ApiCategory.File,
            Params = [new(){Name="Handle",Type=ParamType.Pointer}, new(){Name="Access",Type=ParamType.Flags},
                      new(){Name="ObjAttr",Type=ParamType.Pointer}, new(){Name="IoStatus",Type=ParamType.Pointer}],
            ReturnType = ParamType.UInt },
        new() { Module = "kernelbase", Function = "CloseHandle", Category = ApiCategory.File,
            Params = [new(){Name="hObject",Type=ParamType.Handle}],
            ReturnType = ParamType.Bool },

        // ── Registry ──
        new() { Module = "kernelbase", Function = "RegOpenKeyExW", Category = ApiCategory.Registry,
            Params = [new(){Name="hKey",Type=ParamType.Handle}, new(){Name="SubKey",Type=ParamType.WString},
                      new(){Name="Options",Type=ParamType.UInt}, new(){Name="Access",Type=ParamType.Flags}],
            ReturnType = ParamType.UInt },
        new() { Module = "kernelbase", Function = "RegQueryValueExW", Category = ApiCategory.Registry,
            Params = [new(){Name="hKey",Type=ParamType.Handle}, new(){Name="ValueName",Type=ParamType.WString},
                      new(){Name="Reserved",Type=ParamType.Pointer}, new(){Name="Type",Type=ParamType.Pointer}],
            ReturnType = ParamType.UInt },
        new() { Module = "kernelbase", Function = "RegSetValueExW", Category = ApiCategory.Registry,
            Params = [new(){Name="hKey",Type=ParamType.Handle}, new(){Name="ValueName",Type=ParamType.WString},
                      new(){Name="Reserved",Type=ParamType.UInt}, new(){Name="Type",Type=ParamType.UInt}],
            ReturnType = ParamType.UInt },
        new() { Module = "kernelbase", Function = "RegCloseKey", Category = ApiCategory.Registry,
            Params = [new(){Name="hKey",Type=ParamType.Handle}],
            ReturnType = ParamType.UInt },

        // ── Process / Thread ──
        new() { Module = "kernelbase", Function = "CreateProcessW", Category = ApiCategory.Process,
            Params = [new(){Name="AppName",Type=ParamType.WString}, new(){Name="CmdLine",Type=ParamType.WString},
                      new(){Name="ProcSec",Type=ParamType.Pointer}, new(){Name="ThreadSec",Type=ParamType.Pointer}],
            ReturnType = ParamType.Bool },
        new() { Module = "kernelbase", Function = "OpenProcess", Category = ApiCategory.Process,
            Params = [new(){Name="Access",Type=ParamType.Flags}, new(){Name="Inherit",Type=ParamType.Bool},
                      new(){Name="PID",Type=ParamType.UInt}],
            ReturnType = ParamType.Handle },
        new() { Module = "kernelbase", Function = "TerminateProcess", Category = ApiCategory.Process,
            Params = [new(){Name="hProcess",Type=ParamType.Handle}, new(){Name="ExitCode",Type=ParamType.UInt}],
            ReturnType = ParamType.Bool },
        new() { Module = "ntdll", Function = "NtTerminateProcess", Category = ApiCategory.Process,
            Params = [new(){Name="hProcess",Type=ParamType.Handle}, new(){Name="ExitCode",Type=ParamType.UInt}],
            ReturnType = ParamType.UInt },
        new() { Module = "kernelbase", Function = "CreateThread", Category = ApiCategory.Thread,
            Params = [new(){Name="Security",Type=ParamType.Pointer}, new(){Name="StackSize",Type=ParamType.UInt},
                      new(){Name="StartAddr",Type=ParamType.Pointer}, new(){Name="Param",Type=ParamType.Pointer}],
            ReturnType = ParamType.Handle },
        new() { Module = "ntdll", Function = "NtCreateThreadEx", Category = ApiCategory.Thread,
            Params = [new(){Name="Handle",Type=ParamType.Pointer}, new(){Name="Access",Type=ParamType.Flags},
                      new(){Name="ObjAttr",Type=ParamType.Pointer}, new(){Name="Process",Type=ParamType.Handle}],
            ReturnType = ParamType.UInt },

        // ── Memory ──
        new() { Module = "kernelbase", Function = "VirtualAlloc", Category = ApiCategory.Memory,
            Params = [new(){Name="Address",Type=ParamType.Pointer}, new(){Name="Size",Type=ParamType.UInt},
                      new(){Name="AllocType",Type=ParamType.Flags}, new(){Name="Protect",Type=ParamType.Flags}],
            ReturnType = ParamType.Pointer },
        new() { Module = "kernelbase", Function = "VirtualFree", Category = ApiCategory.Memory,
            Params = [new(){Name="Address",Type=ParamType.Pointer}, new(){Name="Size",Type=ParamType.UInt},
                      new(){Name="FreeType",Type=ParamType.Flags}],
            ReturnType = ParamType.Bool },
        new() { Module = "kernelbase", Function = "VirtualProtect", Category = ApiCategory.Memory,
            Params = [new(){Name="Address",Type=ParamType.Pointer}, new(){Name="Size",Type=ParamType.UInt},
                      new(){Name="NewProtect",Type=ParamType.Flags}, new(){Name="OldProtect",Type=ParamType.Pointer}],
            ReturnType = ParamType.Bool },
        new() { Module = "ntdll", Function = "NtAllocateVirtualMemory", Category = ApiCategory.Memory,
            Params = [new(){Name="Process",Type=ParamType.Handle}, new(){Name="BaseAddr",Type=ParamType.Pointer},
                      new(){Name="ZeroBits",Type=ParamType.UInt}, new(){Name="Size",Type=ParamType.Pointer}],
            ReturnType = ParamType.UInt },
        new() { Module = "ntdll", Function = "NtProtectVirtualMemory", Category = ApiCategory.Memory,
            Params = [new(){Name="Process",Type=ParamType.Handle}, new(){Name="BaseAddr",Type=ParamType.Pointer},
                      new(){Name="Size",Type=ParamType.Pointer}, new(){Name="NewProtect",Type=ParamType.Flags}],
            ReturnType = ParamType.UInt },

        // ── Library ──
        new() { Module = "kernelbase", Function = "LoadLibraryExW", Category = ApiCategory.Library,
            Params = [new(){Name="FileName",Type=ParamType.WString}, new(){Name="hFile",Type=ParamType.Handle},
                      new(){Name="Flags",Type=ParamType.Flags}],
            ReturnType = ParamType.Handle },
        new() { Module = "kernelbase", Function = "GetProcAddress", Category = ApiCategory.Library,
            Params = [new(){Name="hModule",Type=ParamType.Handle}, new(){Name="ProcName",Type=ParamType.String}],
            ReturnType = ParamType.Pointer },
        new() { Module = "ntdll", Function = "LdrLoadDll", Category = ApiCategory.Library,
            Params = [new(){Name="SearchPath",Type=ParamType.Pointer}, new(){Name="Flags",Type=ParamType.Pointer},
                      new(){Name="DllName",Type=ParamType.Pointer}, new(){Name="Handle",Type=ParamType.Pointer}],
            ReturnType = ParamType.UInt },

        // ── Network ──
        new() { Module = "ws2_32", Function = "connect", Category = ApiCategory.Network,
            Params = [new(){Name="Socket",Type=ParamType.Handle}, new(){Name="Addr",Type=ParamType.Pointer},
                      new(){Name="AddrLen",Type=ParamType.Int}],
            ReturnType = ParamType.Int },
        new() { Module = "ws2_32", Function = "send", Category = ApiCategory.Network,
            Params = [new(){Name="Socket",Type=ParamType.Handle}, new(){Name="Buffer",Type=ParamType.Pointer},
                      new(){Name="Len",Type=ParamType.Int}, new(){Name="Flags",Type=ParamType.Int}],
            ReturnType = ParamType.Int },
        new() { Module = "ws2_32", Function = "recv", Category = ApiCategory.Network,
            Params = [new(){Name="Socket",Type=ParamType.Handle}, new(){Name="Buffer",Type=ParamType.Pointer},
                      new(){Name="Len",Type=ParamType.Int}, new(){Name="Flags",Type=ParamType.Int}],
            ReturnType = ParamType.Int },
        new() { Module = "winhttp", Function = "WinHttpConnect", Category = ApiCategory.Network,
            Params = [new(){Name="hSession",Type=ParamType.Handle}, new(){Name="Server",Type=ParamType.WString},
                      new(){Name="Port",Type=ParamType.UInt}, new(){Name="Reserved",Type=ParamType.UInt}],
            ReturnType = ParamType.Handle },
        new() { Module = "winhttp", Function = "WinHttpOpenRequest", Category = ApiCategory.Network,
            Params = [new(){Name="hConnect",Type=ParamType.Handle}, new(){Name="Verb",Type=ParamType.WString},
                      new(){Name="Object",Type=ParamType.WString}, new(){Name="Version",Type=ParamType.WString}],
            ReturnType = ParamType.Handle },

        // ── Synchronization ──
        new() { Module = "kernelbase", Function = "WaitForSingleObject", Category = ApiCategory.Sync,
            Params = [new(){Name="hHandle",Type=ParamType.Handle}, new(){Name="Milliseconds",Type=ParamType.UInt}],
            ReturnType = ParamType.UInt },
        new() { Module = "kernelbase", Function = "CreateMutexW", Category = ApiCategory.Sync,
            Params = [new(){Name="Security",Type=ParamType.Pointer}, new(){Name="InitialOwner",Type=ParamType.Bool},
                      new(){Name="Name",Type=ParamType.WString}],
            ReturnType = ParamType.Handle },
        new() { Module = "kernelbase", Function = "Sleep", Category = ApiCategory.Sync,
            Params = [new(){Name="Milliseconds",Type=ParamType.UInt}],
            ReturnType = ParamType.Void },

        // ── Misc ──
        new() { Module = "user32", Function = "MessageBoxW", Category = ApiCategory.Misc,
            Params = [new(){Name="hWnd",Type=ParamType.Handle}, new(){Name="Text",Type=ParamType.WString},
                      new(){Name="Caption",Type=ParamType.WString}, new(){Name="Type",Type=ParamType.Flags}],
            ReturnType = ParamType.Int },
        new() { Module = "user32", Function = "MessageBoxA", Category = ApiCategory.Misc,
            Params = [new(){Name="hWnd",Type=ParamType.Handle}, new(){Name="Text",Type=ParamType.String},
                      new(){Name="Caption",Type=ParamType.String}, new(){Name="Type",Type=ParamType.Flags}],
            ReturnType = ParamType.Int },
        new() { Module = "kernelbase", Function = "GetLastError", Category = ApiCategory.Misc,
            Params = [],
            ReturnType = ParamType.UInt },
        new() { Module = "kernelbase", Function = "SetLastError", Category = ApiCategory.Misc,
            Params = [new(){Name="ErrorCode",Type=ParamType.UInt}],
            ReturnType = ParamType.Void },
        new() { Module = "kernelbase", Function = "IsDebuggerPresent", Category = ApiCategory.Misc,
            Params = [],
            ReturnType = ParamType.Bool },
        new() { Module = "kernelbase", Function = "OutputDebugStringA", Category = ApiCategory.Misc,
            Params = [new(){Name="String",Type=ParamType.String}],
            ReturnType = ParamType.Void },
        new() { Module = "kernelbase", Function = "OutputDebugStringW", Category = ApiCategory.Misc,
            Params = [new(){Name="String",Type=ParamType.WString}],
            ReturnType = ParamType.Void },
        new() { Module = "ntdll", Function = "NtQueryInformationProcess", Category = ApiCategory.Misc,
            Params = [new(){Name="hProcess",Type=ParamType.Handle}, new(){Name="InfoClass",Type=ParamType.UInt},
                      new(){Name="Buffer",Type=ParamType.Pointer}, new(){Name="BufLen",Type=ParamType.UInt}],
            ReturnType = ParamType.UInt },
    ];
}

// ═══════════════════════════════════════════════════════════════════════
//  WPF Panel
// ═══════════════════════════════════════════════════════════════════════

public class ApiMonitorPanel : DockPanel
{
    private readonly IDebuggerApi _api;
    private readonly ObservableCollection<ApiCallEntry> _entries = new();
    private readonly DataGrid _grid;
    private readonly TextBox _filterBox;
    private readonly ComboBox _categoryFilter;
    private readonly Button _btnStart;
    private readonly Button _btnStop;
    private readonly TextBlock _statusText;
    private ICollectionView? _view;

    public bool IsMonitoring { get; private set; }
    private int _callIndex;

    // Active hooks: address → ApiDef
    private readonly Dictionary<ulong, ApiDef> _entryHooks = new();
    // Return hooks: return address → (ApiDef, entry, arg snapshot)
    private readonly Dictionary<ulong, (ApiDef Def, ApiCallEntry Entry, uint BpHandle)> _returnHooks = new();
    // BP handles for cleanup
    private readonly List<uint> _entryBpHandles = new();

    // Category checkboxes
    private readonly Dictionary<ApiCategory, CheckBox> _categoryChecks = new();


    public ApiMonitorPanel(IDebuggerApi api)
    {
        _api = api;

        // ── Toolbar ──
        var toolbar = new WrapPanel { Margin = new Thickness(4) };

        _btnStart = new Button { Content = "Start", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 4, 0) };
        _btnStart.Click += (_, _) => StartMonitoring();
        toolbar.Children.Add(_btnStart);

        _btnStop = new Button { Content = "Stop", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 4, 0), IsEnabled = false };
        _btnStop.Click += (_, _) => StopMonitoring();
        toolbar.Children.Add(_btnStop);

        var btnClear = new Button { Content = "Clear", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 4, 0) };
        btnClear.Click += (_, _) => { _entries.Clear(); _callIndex = 0; };
        toolbar.Children.Add(btnClear);

        var btnExport = new Button { Content = "Export CSV", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
        btnExport.Click += (_, _) => ExportCsv();
        toolbar.Children.Add(btnExport);

        toolbar.Children.Add(new TextBlock { Text = "Filter:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) });
        _filterBox = new TextBox { Width = 150, Margin = new Thickness(0, 0, 8, 0) };
        _filterBox.TextChanged += (_, _) => _view?.Refresh();
        toolbar.Children.Add(_filterBox);

        toolbar.Children.Add(new TextBlock { Text = "Category:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) });
        _categoryFilter = new ComboBox { Width = 100, Margin = new Thickness(0, 0, 8, 0) };
        _categoryFilter.Items.Add("All");
        foreach (var cat in Enum.GetValues<ApiCategory>())
            _categoryFilter.Items.Add(cat.ToString());
        _categoryFilter.SelectedIndex = 0;
        _categoryFilter.SelectionChanged += (_, _) => _view?.Refresh();
        toolbar.Children.Add(_categoryFilter);

        _statusText = new TextBlock { Text = "Stopped", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        toolbar.Children.Add(_statusText);

        SetDock(toolbar, Dock.Top);
        Children.Add(toolbar);

        // ── Category selection panel ──
        var catPanel = new WrapPanel { Margin = new Thickness(4, 0, 4, 4) };
        catPanel.Children.Add(new TextBlock { Text = "APIs:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        foreach (var cat in Enum.GetValues<ApiCategory>())
        {
            var chk = new CheckBox
            {
                Content = new TextBlock { Text = cat.ToString() },
                IsChecked = cat is ApiCategory.File or ApiCategory.Registry or ApiCategory.Process
                    or ApiCategory.Memory or ApiCategory.Library or ApiCategory.Network or ApiCategory.Misc,
                Margin = new Thickness(0, 0, 8, 0)
            };
            _categoryChecks[cat] = chk;
            catPanel.Children.Add(chk);
        }
        SetDock(catPanel, Dock.Top);
        Children.Add(catPanel);

        // ── DataGrid ──
        _grid = new DataGrid
        {
            IsReadOnly = true,
            AutoGenerateColumns = false,
            SelectionMode = DataGridSelectionMode.Single,
            CanUserSortColumns = true,
            GridLinesVisibility = DataGridGridLinesVisibility.None,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            HeadersVisibility = DataGridHeadersVisibility.Column
        };

        _grid.Columns.Add(new DataGridTextColumn { Header = "#", Binding = new Binding("Index"), Width = 50 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Time", Binding = new Binding("Time"), Width = 80 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "TID", Binding = new Binding("ThreadId") { StringFormat = "X" }, Width = 55 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Module", Binding = new Binding("Module"), Width = 90 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Function", Binding = new Binding("Function"), Width = 180 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Arguments", Binding = new Binding("Arguments"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Return", Binding = new Binding("ReturnValue"), Width = 120 });

        Children.Add(_grid);

        _view = CollectionViewSource.GetDefaultView(_entries);
        _view.Filter = FilterEntry;
        _grid.ItemsSource = _view;
    }

    private bool FilterEntry(object obj)
    {
        if (obj is not ApiCallEntry entry) return false;

        // Category filter
        if (_categoryFilter.SelectedIndex > 0)
        {
            var selectedCat = (string)_categoryFilter.SelectedItem;
            if (!entry.Category.ToString().Equals(selectedCat, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // Text filter
        var filter = _filterBox.Text.Trim();
        if (string.IsNullOrEmpty(filter)) return true;
        return entry.Function.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               entry.Module.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               entry.Arguments.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    // ═════════════════════════════════════════════════════════════════
    //  Monitoring control
    // ═════════════════════════════════════════════════════════════════

    public void StartMonitoring()
    {
        if (IsMonitoring) return;
        if (!_api.IsConnected || _api.TargetPid == 0 || !_api.IsBreakState)
        {
            _api.Log.Warning("[ApiMon] Need connected + break state to install hooks");
            return;
        }

        uint pid = _api.TargetPid;
        int installed = 0;

        foreach (var apiDef in ApiDatabase.Apis)
        {
            if (!_categoryChecks.TryGetValue(apiDef.Category, out var chk) || chk.IsChecked != true)
                continue;

            ulong addr = _api.Symbols.ResolveNameToAddress($"{apiDef.Module}!{apiDef.Function}");
            if (addr == 0)
                addr = _api.Symbols.ResolveNameToAddress($"{apiDef.Module}.dll!{apiDef.Function}");
            if (addr == 0) continue;

            if (_entryHooks.ContainsKey(addr)) continue;

            var h = _api.Breakpoints.SetBreakpoint(pid, 0, addr, PluginBreakpointType.Software);
            if (!h.HasValue) continue;

            _entryHooks[addr] = apiDef;
            _entryBpHandles.Add(h.Value);
            installed++;
        }

        IsMonitoring = true;
        _btnStart.IsEnabled = false;
        _btnStop.IsEnabled = true;
        _statusText.Text = $"Monitoring ({installed} hooks)";
        _api.Log.Info($"[ApiMon] Started: {installed} API hooks installed");
    }

    public void StopMonitoring()
    {
        if (!IsMonitoring) return;

        foreach (var h in _entryBpHandles)
            _api.Breakpoints.RemoveBreakpoint(h);
        _entryBpHandles.Clear();
        _entryHooks.Clear();

        foreach (var ret in _returnHooks.Values)
            _api.Breakpoints.RemoveBreakpoint(ret.BpHandle);
        _returnHooks.Clear();

        IsMonitoring = false;
        _btnStart.IsEnabled = true;
        _btnStop.IsEnabled = false;
        _statusText.Text = "Stopped";
        _api.Log.Info("[ApiMon] Stopped");
    }

    // ═════════════════════════════════════════════════════════════════
    //  Debug event handler
    // ═════════════════════════════════════════════════════════════════

    public bool HandleDebugEvent(PluginDebugEvent evt)
    {
        // Entry hook hit
        if (_entryHooks.TryGetValue(evt.Address, out var apiDef))
        {
            HandleApiEntry(evt, apiDef);
            return true;
        }

        // Return hook hit
        if (_returnHooks.TryGetValue(evt.Address, out var retInfo))
        {
            HandleApiReturn(evt, retInfo.Def, retInfo.Entry, retInfo.BpHandle);
            return true;
        }

        return false;
    }

    private void HandleApiEntry(PluginDebugEvent evt, ApiDef apiDef)
    {
        uint pid = evt.ProcessId;
        uint tid = evt.ThreadId;

        // Read registers for arguments
        var regs = _api.Memory.ReadRegisters(pid, tid);
        ulong rcx = GetReg(regs, "RCX");
        ulong rdx = GetReg(regs, "RDX");
        ulong r8 = GetReg(regs, "R8");
        ulong r9 = GetReg(regs, "R9");
        ulong rsp = GetReg(regs, "RSP");

        ulong[] argValues = new ulong[Math.Max(apiDef.Params.Length, 4)];
        if (argValues.Length > 0) argValues[0] = rcx;
        if (argValues.Length > 1) argValues[1] = rdx;
        if (argValues.Length > 2) argValues[2] = r8;
        if (argValues.Length > 3) argValues[3] = r9;

        // Stack args (5th+)
        for (int i = 4; i < apiDef.Params.Length && rsp != 0; i++)
        {
            var data = _api.Memory.ReadMemory(pid, rsp + (uint)(0x28 + (i - 4) * 8), 8);
            if (data != null) argValues[i] = BitConverter.ToUInt64(data);
        }

        // Format arguments
        var args = FormatArguments(pid, apiDef.Params, argValues);

        var entry = new ApiCallEntry
        {
            Index = ++_callIndex,
            Time = DateTime.Now.ToString("HH:mm:ss.fff"),
            ThreadId = tid,
            Module = apiDef.Module,
            Function = apiDef.Function,
            Arguments = args,
            Category = apiDef.Category
        };

        // Add to UI
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            _entries.Add(entry);
            if (_entries.Count > 10000)
                _entries.RemoveAt(0);
            _grid.ScrollIntoView(entry);
        });

        // Set return hook to capture return value
        if (apiDef.ReturnType != ParamType.Void && rsp != 0)
        {
            var retAddrData = _api.Memory.ReadMemory(pid, rsp, 8);
            if (retAddrData != null)
            {
                ulong retAddr = BitConverter.ToUInt64(retAddrData);
                if (retAddr != 0 && !_returnHooks.ContainsKey(retAddr))
                {
                    var h = _api.Breakpoints.SetBreakpoint(pid, 0, retAddr, PluginBreakpointType.Software);
                    if (h.HasValue)
                        _returnHooks[retAddr] = (apiDef, entry, h.Value);
                }
            }
        }

        _api.Continue();
    }

    private void HandleApiReturn(PluginDebugEvent evt, ApiDef apiDef, ApiCallEntry entry, uint bpHandle)
    {
        // Read RAX
        var regs = _api.Memory.ReadRegisters(evt.ProcessId, evt.ThreadId);
        ulong rax = GetReg(regs, "RAX");

        string retStr = FormatValue(evt.ProcessId, apiDef.ReturnType, rax);

        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            entry.ReturnValue = retStr;
            entry.NotifyReturnChanged();
        });

        // Remove one-shot return BP
        _api.Breakpoints.RemoveBreakpoint(bpHandle);
        _returnHooks.Remove(evt.Address);

        _api.Continue();
    }

    // ═════════════════════════════════════════════════════════════════
    //  Argument formatting
    // ═════════════════════════════════════════════════════════════════

    private string FormatArguments(uint pid, ApiParamDef[] paramDefs, ulong[] values)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < paramDefs.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(paramDefs[i].Name);
            sb.Append('=');
            sb.Append(FormatValue(pid, paramDefs[i].Type, i < values.Length ? values[i] : 0));
        }
        return sb.ToString();
    }

    private string FormatValue(uint pid, ParamType type, ulong value)
    {
        switch (type)
        {
            case ParamType.String:
                if (value == 0) return "NULL";
                // Check if it's an ordinal (low word only, high bits zero)
                if (value < 0x10000) return $"#{value}";
                var strData = _api.Memory.ReadMemory(pid, value, 260);
                if (strData != null)
                {
                    int nul = Array.IndexOf(strData, (byte)0);
                    if (nul < 0) nul = Math.Min(strData.Length, 80);
                    return "\"" + Encoding.ASCII.GetString(strData, 0, nul) + "\"";
                }
                return $"0x{value:X}";

            case ParamType.WString:
                if (value == 0) return "NULL";
                var wstrData = _api.Memory.ReadMemory(pid, value, 520);
                if (wstrData != null)
                {
                    int nulPos = -1;
                    for (int j = 0; j + 1 < wstrData.Length; j += 2)
                        if (wstrData[j] == 0 && wstrData[j + 1] == 0) { nulPos = j; break; }
                    if (nulPos < 0) nulPos = Math.Min(wstrData.Length, 160);
                    return "L\"" + Encoding.Unicode.GetString(wstrData, 0, nulPos) + "\"";
                }
                return $"0x{value:X}";

            case ParamType.Handle:
                return value == 0 ? "NULL" : value == unchecked((ulong)-1) ? "INVALID" : $"0x{value:X}";

            case ParamType.Bool:
                return value == 0 ? "FALSE" : "TRUE";

            case ParamType.Pointer:
                return value == 0 ? "NULL" : $"0x{value:X}";

            case ParamType.Flags:
                return $"0x{value:X}";

            case ParamType.Int:
                return ((long)value).ToString();

            case ParamType.Void:
                return "";

            default:
                return $"0x{value:X}";
        }
    }

    private static ulong GetReg(IReadOnlyList<PluginRegister>? regs, string name)
    {
        if (regs == null) return 0;
        foreach (var r in regs)
            if (r.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return r.Value;
        return 0;
    }

    // ═════════════════════════════════════════════════════════════════
    //  Export
    // ═════════════════════════════════════════════════════════════════

    private void ExportCsv()
    {
        var dlg = new SaveFileDialog
        {
            FileName = "api_trace.csv",
            Filter = "CSV (*.csv)|*.csv|All files (*.*)|*.*",
            Title = "Export API Trace"
        };

        if (dlg.ShowDialog() != true) return;

        using var writer = new StreamWriter(dlg.FileName, false, Encoding.UTF8);
        writer.WriteLine("Index,Time,TID,Module,Function,Arguments,Return");
        foreach (var e in _entries)
        {
            writer.Write(e.Index); writer.Write(',');
            writer.Write(e.Time); writer.Write(',');
            writer.Write(e.ThreadId.ToString("X")); writer.Write(',');
            writer.Write(CsvEscape(e.Module)); writer.Write(',');
            writer.Write(CsvEscape(e.Function)); writer.Write(',');
            writer.Write(CsvEscape(e.Arguments)); writer.Write(',');
            writer.WriteLine(CsvEscape(e.ReturnValue));
        }
        _api.Log.Info($"[ApiMon] Exported {_entries.Count} entries to {dlg.FileName}");
    }

    private static string CsvEscape(string s)
    {
        if (s.Contains(',') || s.Contains('"') || s.Contains('\n'))
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }
}
