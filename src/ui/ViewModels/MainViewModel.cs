using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KernelFlirt.UI.Models;
using KernelFlirt.UI.Services;
using KernelFlirt.UI.Views;

namespace KernelFlirt.UI.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly DriverComm _driver = new();
    private readonly Disassembler _disasm = new();
    private readonly SymbolService _symbols;
    private readonly PluginManager _pluginManager;
    public PluginManager PluginManager => _pluginManager;

    private uint? _tempBpHandle;  // For Step Over / Run to Cursor temp breakpoint
    private CancellationTokenSource? _listenerCts;
    private Task? _listenerTask;
    // SW breakpoint we just hit — need step-past before continuing
    private Breakpoint? _hitSwBp;
    // True if paused via thread suspend (not debug event)
    private bool _isPausedViaSuspend;
    // Driver debugging state
    private string? _loadedDriverServiceName;
    private byte _driverOriginalByte;
    private uint _driverEntryRva;

    // Plugin address annotations — shown as "; comment" in disassembly
    private readonly Dictionary<ulong, string> _addressAnnotations = new();
    public IReadOnlyDictionary<ulong, string> AddressAnnotations => _addressAnnotations;

    public void SetAddressAnnotation(ulong address, string? annotation)
    {
        if (string.IsNullOrEmpty(annotation))
            _addressAnnotations.Remove(address);
        else
            _addressAnnotations[address] = annotation;
    }

    // Called from disasm context menu
    public event Action<ulong, string>? OnNoteAdded;
    public event Action<ulong, string>? OnNoteEdited;
    public event Action<ulong>? OnNoteRemoved;

    public void AddNoteAtAddress(ulong address)
    {
        if (_addressAnnotations.ContainsKey(address))
        {
            EditNoteAtAddress(address);
            return;
        }
        string note = PromptInput("Add Note", $"Note for {address:X16}:");
        if (string.IsNullOrWhiteSpace(note)) return;
        SetAddressAnnotation(address, note);
        RefreshDisasmAnnotations();
        OnNoteAdded?.Invoke(address, note);
        Log($"Note added at {address:X16}: {note}");
    }

    public void EditNoteAtAddress(ulong address)
    {
        _addressAnnotations.TryGetValue(address, out var existing);
        if (existing == null) { AddNoteAtAddress(address); return; }
        string? note = PromptInput("Edit Note", $"Note for {address:X16}:", existing);
        if (note == null) return;
        if (string.IsNullOrWhiteSpace(note))
        {
            RemoveNoteAtAddress(address);
            return;
        }
        SetAddressAnnotation(address, note);
        RefreshDisasmAnnotations();
        OnNoteEdited?.Invoke(address, note);
        Log($"Note edited at {address:X16}: {note}");
    }

    public void RemoveNoteAtAddress(ulong address)
    {
        if (!_addressAnnotations.ContainsKey(address)) return;
        SetAddressAnnotation(address, null);
        RefreshDisasmAnnotations();
        OnNoteRemoved?.Invoke(address);
        Log($"Note removed at {address:X16}");
    }

    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private uint _targetPid;
    [ObservableProperty] private uint _selectedThreadId;
    [ObservableProperty] private string _statusText = "Not connected";
    [ObservableProperty] private ulong _disasmAddress;
    [ObservableProperty] private ulong _hexAddress;
    [ObservableProperty] private bool _isDebugHookActive;
    [ObservableProperty] private bool _isBreakState;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _is32Bit;  // True when debugging a WoW64 (32-bit) process
    [ObservableProperty] private ulong _selectedDisasmAddress;  // Cursor position in disasm

    // Bitness-aware helpers
    public string IpRegName => Is32Bit ? "EIP" : "RIP";
    public string SpRegName => Is32Bit ? "ESP" : "RSP";
    public int PointerSize => Is32Bit ? 4 : 8;
    public string FormatAddr(ulong addr) => Is32Bit ? $"{addr:X8}" : $"{addr:X16}";

    public RangeObservableCollection<Instruction> Instructions { get; } = [];
    public RangeObservableCollection<Register> Registers { get; } = [];
    public RangeObservableCollection<ModuleInfo> Modules { get; } = [];
    public RangeObservableCollection<ThreadInfo> Threads { get; } = [];
    public RangeObservableCollection<KernelModuleInfo> KernelModules { get; } = [];
    public ObservableCollection<Breakpoint> Breakpoints { get; } = [];
    public ObservableCollection<string> LogMessages { get; } = [];
    public RangeObservableCollection<StackEntry> StackEntries { get; } = [];
    public RangeObservableCollection<CallStackFrame> CallStack { get; } = [];
    public ObservableCollection<Bookmark> Bookmarks { get; } = [];
    public ObservableCollection<Patch> Patches { get; } = [];
    public RangeObservableCollection<SehEntry> SehChain { get; } = [];
    public RangeObservableCollection<SearchResult> SearchResults { get; } = [];
    public RangeObservableCollection<ImportEntry> Imports { get; } = [];
    public RangeObservableCollection<ImportEntry> FilteredImports { get; } = [];
    private List<ImportEntry> _allImports = [];
    [ObservableProperty] private string _importFilter = "";
    public RangeObservableCollection<ExportEntry> Exports { get; } = [];
    public RangeObservableCollection<ExportEntry> FilteredExports { get; } = [];
    private List<ExportEntry> _allExports = [];
    [ObservableProperty] private string _exportFilter = "";
    public RangeObservableCollection<FunctionEntry> Functions { get; } = [];
    public RangeObservableCollection<FunctionEntry> FilteredFunctions { get; } = [];
    private List<FunctionEntry> _allFunctions = [];
    [ObservableProperty] private string _functionFilter = "";
    public RangeObservableCollection<ExceptionEntry> FilteredExceptions { get; } = [];
    private List<ExceptionEntry> _allExceptions = [];
    [ObservableProperty] private string _exceptionFilter = "";
    public RangeObservableCollection<SectionEntry> FilteredSections { get; } = [];
    private List<SectionEntry> _allSections = [];
    private readonly Dictionary<string, List<SectionEntry>> _pluginSections = new();
    [ObservableProperty] private string _sectionFilter = "";

    public RangeObservableCollection<StringEntry> FilteredStrings { get; } = [];
    private List<StringEntry> _allStrings = [];
    [ObservableProperty] private string _stringFilter = "";
    [ObservableProperty] private byte[] _hexData = [];
    [ObservableProperty] private string _decompiledCode = "";
    [ObservableProperty] private bool _isDecompiling;
    private string _disabledPlugins = "";

    private static readonly string SettingsFile =
        Path.Combine(AppContext.BaseDirectory, "kf_settings.txt");

    // Plugin UI integration - set by MainWindow
    public Action<string, Action>? AddPluginMenuItem { get; set; }
    public Action<string, object>? AddPluginToolPanel { get; set; }
    public Action<string>? OnPluginInitializing { get; set; }
    public Action? SwitchToDisasmTab { get; set; }

    public MainViewModel()
    {
        _symbols = new SymbolService(_driver);
        _symbols.LogMessage += msg => Application.Current.Dispatcher.Invoke(() => Log(msg));
        _pluginManager = new PluginManager(msg => Application.Current.Dispatcher.Invoke(() => Log(msg)));
        LoadSettings();
    }

    public void LoadPlugins()
    {
        var pluginsDir = Path.Combine(AppContext.BaseDirectory, "plugins");

        // Factory creates a per-plugin adapter so each can be enabled/disabled independently
        DebuggerApiAdapter AdapterFactory() => new DebuggerApiAdapter(
            _driver, _symbols, _pluginManager,
            () => IsConnected,
            () => IsBreakState,
            () => TargetPid,
            () => SelectedThreadId,
            () => Is32Bit,
            msg => Application.Current.Dispatcher.Invoke(() => Log(msg)),
            () => Breakpoints,
            () => Modules,
            () => KernelModules,
            addr => NavigateDisasmTo(addr),
            (header, callback) => AddPluginMenuItem?.Invoke(header, callback),
            (title, content) => AddPluginToolPanel?.Invoke(title, content),
            (peBase, name) => AddUnpackedModule(peBase, name),
            () => { _ = RefreshModulesAndSectionsAsync(); },
            (modName, sections) => AddModuleSections(modName, sections),
            addr => DecompileFunction(addr, 0),
            () => DecompiledCode,
            () => { if (CanDisasmGoBack) DisasmGoBackCommand.Execute(null); },
            (addr, note) => SetAddressAnnotation(addr, note),
            addr => _addressAnnotations.TryGetValue(addr, out var n) ? n : null,
            () => (IReadOnlyDictionary<ulong, string>)AddressAnnotations,
            () => RefreshDisasmAnnotations());

        // Wire Continue/SingleStep callbacks so plugins can resume execution
        _pluginManager.ContinueAction = () =>
            Application.Current.Dispatcher.InvokeAsync(async () => await PluginContinue());
        _pluginManager.SingleStepAction = () =>
            Application.Current.Dispatcher.InvokeAsync(async () => await PluginSingleStep());
        _pluginManager.StepOverAction = () =>
            Application.Current.Dispatcher.InvokeAsync(async () => await StepOver());
        _pluginManager.StepOutAction = () =>
            Application.Current.Dispatcher.InvokeAsync(async () => await StepOut());
        _pluginManager.RunToCursorAction = (addr) =>
            Application.Current.Dispatcher.InvokeAsync(async () => await PluginRunToCursor(addr));
        _pluginManager.SkipInstructionAction = () =>
            Application.Current.Dispatcher.Invoke(() => SkipInstruction());
        _pluginManager.PauseAction = () =>
            Application.Current.Dispatcher.InvokeAsync(async () => await Pause());

        _pluginManager.OnSettingsChanged = () =>
            Application.Current.Dispatcher.Invoke(() => SaveSettings());
        _pluginManager.OnPluginInitializing = name =>
            Application.Current.Dispatcher.Invoke(() => OnPluginInitializing?.Invoke(name));

        _pluginManager.LoadPlugins(pluginsDir, AdapterFactory);

        // Wire note events from context menu to plugin adapters
        OnNoteAdded += (addr, note) =>
        {
            foreach (var p in _pluginManager.Plugins)
                if (p.Adapter is { Enabled: true } a && a.UI is UiApiAdapter ua)
                    ua.FireNoteAdded(addr, note);
        };
        OnNoteEdited += (addr, note) =>
        {
            foreach (var p in _pluginManager.Plugins)
                if (p.Adapter is { Enabled: true } a && a.UI is UiApiAdapter ua)
                    ua.FireNoteEdited(addr, note);
        };
        OnNoteRemoved += addr =>
        {
            foreach (var p in _pluginManager.Plugins)
                if (p.Adapter is { Enabled: true } a && a.UI is UiApiAdapter ua)
                    ua.FireNoteRemoved(addr);
        };

        // Apply persisted disabled state (hides tabs + disables events)
        _pluginManager.ApplyPersistedState(_disabledPlugins);
    }

    /// <summary>
    /// Called by plugin via Continue() — resumes process with minimal UI state changes.
    /// Avoids triggering disassembly/register refreshes.
    /// </summary>
    private async Task PluginContinue()
    {
        if (!IsConnected || TargetPid == 0) return;

        // Minimal state: just restart listener and continue, no UI property changes
        StartDebugListener();

        var mode = _hitSwBp != null ? DriverComm.CONTINUE_STEP_PAST : DriverComm.CONTINUE_RUN;
        await Task.Run(() => _driver.ContinueDebugEvent(mode));
        _hitSwBp = null;
    }

    /// <summary>
    /// Called by plugin via SingleStep() — executes one instruction.
    /// </summary>
    private async Task PluginSingleStep()
    {
        if (!IsConnected || TargetPid == 0) return;

        IsBreakState = false;
        IsRunning = true;

        var waitTask = Task.Run(() => _driver.WaitDebugEvent());
        _driver.ContinueDebugEvent(DriverComm.CONTINUE_STEP_INTO);
        _hitSwBp = null;

        var stepEvt = await waitTask;
        if (stepEvt != null)
            OnDebugEvent(stepEvt);
    }

    private string _lastConnectAddress = "";
    public Dictionary<string, string> ThemeColors { get; set; } = new();

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsFile)) return;
            foreach (var line in File.ReadAllLines(SettingsFile))
            {
                if (line.StartsWith("SymbolPath=", StringComparison.Ordinal))
                    _symbols.SymbolPath = line["SymbolPath=".Length..];
                else if (line.StartsWith("LastConnect=", StringComparison.Ordinal))
                    _lastConnectAddress = line["LastConnect=".Length..];
                else if (line.StartsWith("DisabledPlugins=", StringComparison.Ordinal))
                    _disabledPlugins = line["DisabledPlugins=".Length..];
                else if (line.StartsWith("Color.", StringComparison.Ordinal))
                {
                    var eq = line.IndexOf('=');
                    if (eq > 6)
                    {
                        var key = line[6..eq];
                        var val = line[(eq + 1)..];
                        ThemeColors[key] = val;
                    }
                }
            }
        }
        catch { /* ignore */ }
    }

    private void SaveSettings()
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"SymbolPath={_symbols.SymbolPath}");
            sb.AppendLine($"LastConnect={_lastConnectAddress}");
            var disabled = string.Join(",", _pluginManager.Plugins
                .Where(p => !p.Enabled).Select(p => p.Plugin.Name));
            if (!string.IsNullOrEmpty(disabled))
                sb.AppendLine($"DisabledPlugins={disabled}");
            foreach (var (key, val) in ThemeColors)
                sb.AppendLine($"Color.{key}={val}");
            File.WriteAllText(SettingsFile, sb.ToString());
        }
        catch { /* ignore */ }
    }

    public void SaveThemeColors() => SaveSettings();

    /* ================================================================== */
    /*  Connection                                                         */
    /* ================================================================== */

    [RelayCommand]
    private async Task ConnectKernelAsync()
    {
        string input = PromptInput("Connect",
            "Enter host:port for remote, or leave blank for local driver:",
            _lastConnectAddress);
        if (input == null) return; // cancelled

        StatusText = "Connecting...";

        try
        {
            bool connected;
            if (string.IsNullOrWhiteSpace(input))
            {
                Log("Connecting to local driver...");
                connected = await Task.Run(() => _driver.Connect());
            }
            else
            {
                // Parse host:port
                string host = input.Trim();
                int port = 31337;
                int colonIdx = host.LastIndexOf(':');
                if (colonIdx > 0 && int.TryParse(host[(colonIdx + 1)..], out int p))
                {
                    port = p;
                    host = host[..colonIdx];
                }
                Log($"Connecting to {host}:{port}...");
                connected = await Task.Run(() => _driver.ConnectRemote(host, port));
            }

            if (connected)
            {
                var (version, ok) = _driver.Ping();
                if (ok)
                {
                    IsConnected = true;
                    StatusText = $"Connected (v{version:X})";
                    Log($"Connected, driver version 0x{version:X8}");
                    _lastConnectAddress = input.Trim();
                    SaveSettings();
                    _pluginManager.NotifyConnected();
                    await PostConnectRefreshAsync();
                }
                else
                {
                    StatusText = "Connection failed (Ping failed)";
                    Log("Ping failed after connect");
                }
            }
            else
            {
                StatusText = "Cannot connect to driver";
                Log("Connection failed");
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Connection error: {ex.Message}";
            Log($"Connect exception: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task DisconnectKernel()
    {
        // Detach first — unblocks threads, removes BPs/hook, cancels pending IRP
        if (TargetPid != 0 || IsDebugHookActive)
            await DetachProcess();

        // Unload driver before disconnecting
        if (!string.IsNullOrEmpty(_loadedDriverServiceName))
        {
            try { _driver.UnloadRemoteDriver(_loadedDriverServiceName); } catch { }
            _loadedDriverServiceName = null;
        }

        _driver.Disconnect();
        _symbols.Reset();
        IsConnected = false;
        _pluginManager.NotifyDisconnected();

        // Clear all tabs
        KernelModules.Clear();
        Bookmarks.Clear();
        Patches.Clear();
        SearchResults.Clear();
        LogMessages.Clear();

        StatusText = "Disconnected";
        Log("Disconnected");
    }

    /// <summary>Called right after a successful connect — loads data that doesn't require a PID.</summary>
    private async Task PostConnectRefreshAsync()
    {
        Log("Loading kernel modules...");
        var mods = await Task.Run(() => _driver.EnumKernelModules());
        KernelModules.ReplaceAll(mods);
        Log($"Found {mods.Count} kernel modules");

        // Initialize symbol engine and load kernel module symbols
        Log("Initializing symbol engine...");
        var symErr = _symbols.Initialize();
        if (symErr == null)
        {
            Log($"Symbol engine OK, path: {_symbols.SymbolPath}");
            Log($"Loading symbols for {mods.Count} kernel modules...");
            StatusText = $"Loading symbols (0%)...";
            int loaded = 0;
            int total = mods.Count;
            int done = 0;
            await Task.Run(() =>
            {
                foreach (var m in mods)
                {
                    if (_symbols.LoadModule(0, m.Name, m.BaseAddress, m.Size))
                        loaded++;
                    int d = Interlocked.Increment(ref done);
                    int pct = total > 0 ? d * 100 / total : 0;
                    Application.Current?.Dispatcher.InvokeAsync(
                        () => StatusText = $"Loading symbols ({pct}%)...");
                }
            });
            // Flush dispatcher queue so "100%" doesn't overwrite next StatusText
            await Application.Current.Dispatcher.InvokeAsync(() => { });
            Log($"Symbols: {loaded}/{mods.Count} kernel modules loaded");

            // Test resolve on first kernel module base
            if (mods.Count > 0)
            {
                var testMod = mods[0];
                var sym = _symbols.ResolveViaDbgHelp(testMod.BaseAddress);
                Log($"Symbol test: {testMod.Name}+0 = {sym ?? "(no symbol)"}");
            }
        }
        else
        {
            Log($"Symbol engine FAILED: {symErr}");
        }

        Log("Debugger ready");
        StatusText = "Debugger ready";
    }

    /* ================================================================== */
    /*  Process Attach / Detach (OllyDbg-style)                            */
    /* ================================================================== */

    [RelayCommand]
    private async Task OpenProcessAsync()
    {
        var dialog = new ProcessPickerDialog(_driver);
        dialog.Owner = Application.Current.MainWindow;
        if (dialog.ShowDialog() == true && dialog.SelectedPid != 0)
        {
            // Detach previous process first (clean up hooks, BPs, listener)
            if (TargetPid != 0)
                await DetachProcess();

            TargetPid = dialog.SelectedPid;
            await DoAttachAsync();
        }
    }

    [RelayCommand]
    private async Task OpenAndDebugAsync()
    {
        if (!IsConnected || !_driver.IsRemote)
        {
            Log("Open & Debug requires a remote connection (via relay)");
            return;
        }

        var dialog = new RemoteFileBrowserDialog(_driver);
        dialog.Owner = Application.Current.MainWindow;
        if (dialog.ShowDialog() != true || string.IsNullOrEmpty(dialog.SelectedExePath))
            return;

        if (dialog.IsDriverFile)
        {
            await OpenAndDebugDriverAsync(dialog.SelectedExePath);
            return;
        }

        // Detach previous process first
        if (TargetPid != 0)
            await DetachProcess();

        var exePath = dialog.SelectedExePath;
        Log($"Creating process: {exePath}");
        StatusText = "Creating remote process...";

        var result = await Task.Run(() => _driver.CreateRemoteProcess(exePath));
        if (result == null)
        {
            Log("Failed to create remote process");
            StatusText = "Create process failed";
            return;
        }

        var (pid, tid, imageBase) = result.Value;
        Log($"Process created: PID={pid} TID={tid} ImageBase={imageBase:X16} (suspended)");

        // With CREATE_SUSPENDED the exe is mapped but the loader hasn't run yet.
        // Strategy: install hook, set BP at PE entry point, resume, catch BP.
        // At that point the loader has run and all modules are available.

        TargetPid = pid;

        // 1. Install debug hook so we can catch breakpoints
        Log("Installing debug hook...");
        var hookOk = await Task.Run(() => _driver.InstallDebugHook(pid));
        if (hookOk)
        {
            IsDebugHookActive = true;
            Log("Debug hook installed");
        }
        else
        {
            Log("Warning: debug hook install failed — falling back to poll attach");
            await Task.Run(() => _driver.ResumeThread(tid));
            await Task.Delay(500);
            await DoAttachAsync();
            return;
        }

        if (imageBase == 0)
        {
            Log("Relay did not return ImageBase — falling back to poll attach");
            await Task.Run(() => _driver.ResumeThread(tid));
            await Task.Delay(500);
            await DoAttachAsync();
            return;
        }

        // 3. Read PE header → detect bitness + AddressOfEntryPoint
        ulong entryPoint = 0;
        var peOffsetData = await Task.Run(() => _driver.ReadMemory(pid, imageBase + 0x3C, 4));
        if (peOffsetData != null && peOffsetData.Length == 4)
        {
            uint peOffset = BitConverter.ToUInt32(peOffsetData, 0);

            // Detect 32-bit from PE Optional Header magic
            var magicData = await Task.Run(() => _driver.ReadMemory(pid, imageBase + peOffset + 0x18, 2));
            if (magicData != null && magicData.Length == 2)
            {
                ushort magic = BitConverter.ToUInt16(magicData, 0);
                Is32Bit = magic == 0x10B;
                _disasm.SetMode(Is32Bit);
                if (Is32Bit) Log("Detected 32-bit (WoW64) process");
            }

            var epData = await Task.Run(() => _driver.ReadMemory(pid, imageBase + peOffset + 0x28, 4));
            if (epData != null && epData.Length == 4)
            {
                uint entryRva = BitConverter.ToUInt32(epData, 0);
                if (entryRva != 0)
                    entryPoint = imageBase + entryRva;
            }
        }

        if (entryPoint == 0)
        {
            Log("Could not resolve PE entry point — falling back to poll attach");
            await Task.Run(() => _driver.ResumeThread(tid));
            await Task.Delay(500);
            await DoAttachAsync();
            return;
        }

        Log($"PE entry point: {entryPoint:X16}");

        if (Is32Bit)
        {
            // WoW64 processes: Windows 10 has optimized exception dispatch for
            // WoW64 that bypasses KiDebugRoutine, so INT3/HW BP are not caught.
            // Instead, patch entry point with infinite loop (EB FE = JMP $),
            // let loader run, then suspend thread at entry point and restore.
            Log("WoW64: patching entry point with spin loop (EB FE)...");

            // Save original 2 bytes
            var origBytes = await Task.Run(() => _driver.ReadMemory(pid, entryPoint, 2));
            if (origBytes == null || origBytes.Length < 2)
            {
                Log("Failed to read entry point bytes — falling back to poll attach");
                await Task.Run(() => _driver.ResumeThread(tid));
                await Task.Delay(1500);
                await DoAttachAsync();
                return;
            }

            // Write EB FE (JMP $) at entry point
            var spinLoop = new byte[] { 0xEB, 0xFE };
            var writeOk = await Task.Run(() => _driver.WriteMemory(pid, entryPoint, spinLoop));
            if (!writeOk)
            {
                Log("Failed to write spin loop — falling back to poll attach");
                await Task.Run(() => _driver.ResumeThread(tid));
                await Task.Delay(1500);
                await DoAttachAsync();
                return;
            }

            Log("Resuming thread (will spin at entry point)...");
            StatusText = "Running to entry point...";
            await Task.Run(() => _driver.ResumeThread(tid));

            // Wait for loader to finish and thread to reach entry point
            await Task.Delay(2000);

            // Suspend main thread
            await Task.Run(() => _driver.SuspendThread(tid));

            // Verify EIP is at entry point
            var regs32 = await Task.Run(() => _driver.ReadRegisters(pid, tid, true));
            var eip = regs32.FirstOrDefault(r => r.Name == "EIP");
            if (eip != null)
                Log($"Thread suspended: EIP = {eip.Value:X8} (expect {entryPoint:X8})");

            // Restore original bytes
            await Task.Run(() => _driver.WriteMemory(pid, entryPoint, origBytes));
            Log("Entry point restored");

            SelectedThreadId = tid;
            _isPausedViaSuspend = true;

            // Now continue with module enumeration (same as 64-bit path below)
            goto enumModules;
        }

        // Native 64-bit: set software breakpoint at entry point
        var bpHandle = await Task.Run(() => _driver.SetBreakpoint(pid, 0, entryPoint, BreakpointType.Software));
        if (!bpHandle.HasValue)
        {
            Log("Failed to set BP at entry point — falling back to poll attach");
            await Task.Run(() => _driver.ResumeThread(tid));
            await Task.Delay(500);
            await DoAttachAsync();
            return;
        }

        _tempBpHandle = bpHandle.Value;
        Log($"BP set at entry point {entryPoint:X16}, resuming thread...");
        StatusText = "Running to entry point...";

        // Start waiting for debug event FIRST (pends IRP in driver,
        // also re-asserts KdDebuggerEnabled=TRUE via IOCTL dispatch).
        IsRunning = true;
        IsBreakState = false;

        var waitTask = Task.Run(() => _driver.WaitDebugEvent());
        await Task.Delay(50);

        // Resume the suspended thread — loader will run, then hit our BP
        await Task.Run(() => _driver.ResumeThread(tid));

        var evt = await waitTask;

        // Remove the temp BP
        await Task.Run(() => _driver.RemoveBreakpoint(_tempBpHandle.Value));
        _tempBpHandle = null;

        if (evt == null)
        {
            Log("No debug event received — process may have exited");
            StatusText = "Debug failed";
            IsRunning = false;
            return;
        }

        Log($"Hit entry point at {evt.Address:X16} (PID={evt.ProcessId} TID={evt.ThreadId})");
        SelectedThreadId = evt.ThreadId;

        // 8. Now the process is stopped at entry point with loader done.
        //    Enumerate modules, read registers, etc. — same as DoAttachAsync but
        //    we're already hooked and stopped on a debug event (not suspend).
    enumModules:
        HexAddress = 0;
        DisasmAddress = 0;

        // Enumerate modules (loader has run, all DLLs are mapped)
        Log("Enumerating modules...");
        var modules = await Task.Run(() => _driver.EnumModules(pid));
        Log($"Found {modules.Count} modules");

        // Load symbols
        Log($"Loading symbols for {modules.Count} user modules...");
        StatusText = $"Loading symbols (0%)...";
        int symLoaded = 0;
        int symDone = 0;
        int symTotal = modules.Count;
        await Task.Run(() =>
        {
            foreach (var m in modules)
            {
                if (_symbols.LoadModule(pid, m.Name, m.BaseAddress, m.Size))
                    symLoaded++;
                int d = Interlocked.Increment(ref symDone);
                int pct = symTotal > 0 ? d * 100 / symTotal : 0;
                Application.Current?.Dispatcher.InvokeAsync(
                    () => StatusText = $"Loading symbols ({pct}%)...");
            }
        });
        Log($"Symbols: {symLoaded}/{modules.Count} user modules loaded");

        // Enumerate threads
        var threads = await Task.Run(() => _driver.EnumThreads(pid));
        Log($"Found {threads.Count} threads");

        Threads.ReplaceAll(threads);
        if (Is32Bit)
            foreach (var m in modules) m.Is32Bit = true;
        Modules.ReplaceAll(modules);

        // Read registers
        var regs = await Task.Run(() => _driver.ReadRegisters(pid, SelectedThreadId, Is32Bit));
        Registers.ReplaceAll(regs);

        var rip = Registers.FirstOrDefault(r => r.Name == IpRegName);
        if (rip != null && rip.Value != 0)
        {
            DisasmAddress = rip.Value;
            Log($"{IpRegName} = {FormatAddr(rip.Value)}");
        }

        // Fetch disasm, stack, hex dump
        var rspReg = Registers.FirstOrDefault(r => r.Name == SpRegName);
        var disasmAddr = DisasmAddress;
        var hexAddr = HexAddress != 0 ? HexAddress : disasmAddr;
        HexAddress = hexAddr;

        Log("Reading memory...");
        var disasmTask = Task.Run(() => _driver.ReadMemory(pid, disasmAddr, 4096));
        var stackTask = rspReg != null ? Task.Run(() => _driver.ReadMemory(pid, rspReg.Value, 256)) : Task.FromResult<byte[]?>(null);
        var hexTask = Task.Run(() => _driver.ReadMemory(pid, hexAddr, 4096));
        await Task.WhenAll(disasmTask, stackTask, hexTask);

        var disasmData = disasmTask.Result;
        if (disasmData != null)
        {
            PatchBpBytesForDisasm(disasmData, disasmAddr);
            var instrs = _disasm.Disassemble(disasmData, disasmAddr);
            AnnotateInstructionsWithSymbols(instrs);
            Instructions.ReplaceAll(instrs);
        }

        var stackData = stackTask.Result;
        if (stackData != null && rspReg != null)
        {
            var stackItems = new List<StackEntry>();
            int sp = PointerSize;
            string spName = SpRegName;
            for (int i = 0; i < stackData.Length; i += sp)
            {
                if (i + sp > stackData.Length) break;
                ulong val = Is32Bit ? BitConverter.ToUInt32(stackData, i) : BitConverter.ToUInt64(stackData, i);
                stackItems.Add(new StackEntry { Offset = $"{spName}+{i:X2}", Address = FormatAddr(val) });
            }
            StackEntries.ReplaceAll(stackItems);
        }

        var hexData = hexTask.Result;
        if (hexData != null) HexData = hexData;

        RefreshImports();
        RefreshExceptions();
        RefreshSections();
        RefreshStrings();
        RefreshCallStack();
        _ = RefreshFunctionsAsync();

        // For 64-bit: stopped on debug event (not via SuspendThread).
        // For WoW64: stopped via SuspendThread (_isPausedViaSuspend already set at line 439).
        if (!Is32Bit) _isPausedViaSuspend = false;
        _hitSwBp = null;
        IsBreakState = true;
        IsRunning = false;
        StatusText = $"Entry point - PID {pid} TID {SelectedThreadId}";
        Log($"Stopped at entry point of {exePath}");
    }

    private async Task OpenAndDebugDriverAsync(string sysPath)
    {
        // 0a. Detach previous process first
        if (TargetPid != 0)
            await DetachProcess();

        // 0b. Unload previous driver if still loaded
        if (!string.IsNullOrEmpty(_loadedDriverServiceName))
        {
            Log($"Unloading previous driver: {_loadedDriverServiceName}");
            StatusText = "Unloading previous driver...";
            var unloadOk = await Task.Run(() => _driver.UnloadRemoteDriver(_loadedDriverServiceName));
            Log(unloadOk
                ? $"Previous driver '{_loadedDriverServiceName}' unloaded"
                : $"Warning: failed to unload '{_loadedDriverServiceName}' (may already be stopped)");
            _loadedDriverServiceName = null;
        }

        Log($"Loading driver: {sysPath}");

        // 1. Prepare driver on relay — stops old service, copies, patches INT3,
        //    signs, creates service. Does NOT start it yet.
        //    Hook is NOT active during this to avoid catching spurious kernel
        //    exceptions (e.g. DriverUnload of old service).
        StatusText = "Preparing driver on VM...";
        var loadResult = await Task.Run(() => _driver.LoadRemoteDriver(sysPath));
        if (loadResult == null)
        {
            Log("Failed to prepare driver on VM");
            StatusText = "Driver load failed";
            return;
        }

        var (serviceName, entryRva, originalByte) = loadResult.Value;
        _loadedDriverServiceName = serviceName;
        _driverOriginalByte = originalByte;
        _driverEntryRva = entryRva;
        Log($"Driver prepared: service={serviceName} EntryRVA=0x{entryRva:X} OrigByte=0x{originalByte:X2}");

        // 2. Install debug hook BEFORE starting the driver —
        //    hook must be active when DriverEntry hits INT3.
        TargetPid = 4;
        StatusText = "Installing debug hook...";
        var hookOk = await Task.Run(() => _driver.InstallDebugHook(4));
        if (hookOk)
        {
            IsDebugHookActive = true;
            Log("Debug hook installed (target: System PID=4)");
        }
        else
        {
            Log("Failed to install debug hook — cannot debug driver");
            StatusText = "Hook install failed";
            return;
        }

        // 3. Start the driver — relay calls StartService in background thread.
        //    DriverEntry will hit INT3, hook will catch it.
        StatusText = "Starting driver (waiting for DriverEntry)...";
        var startOk = await Task.Run(() => _driver.StartRemoteDriver(serviceName));
        if (!startOk)
        {
            Log("Failed to start driver service");
            StatusText = "Driver start failed";
            await Task.Run(() => _driver.RemoveDebugHook());
            IsDebugHookActive = false;
            return;
        }
        Log("StartService dispatched — waiting for DriverEntry INT3...");

        // 4. Wait for DriverEntry INT3 — skip spurious kernel INT3s
        //    The hook catches ALL kernel-space INT3s (PID=4), but we only want
        //    the one at our driver's DriverEntry. Loop until we find it.
        IsRunning = true;
        IsBreakState = false;

        DebugEvent? evt = null;
        ulong driverBase = 0;
        List<KernelModuleInfo> kmodules = new();
        const int maxEventRetries = 30;

        for (int attempt = 0; attempt < maxEventRetries; attempt++)
        {
            evt = await Task.Run(() => _driver.WaitDebugEvent());
            if (evt == null)
            {
                Log("No debug event — driver may have failed to load");
                StatusText = "No debug event";
                IsRunning = false;
                return;
            }

            // Discover driver base if not yet known
            if (driverBase == 0)
            {
                kmodules = await Task.Run(() => _driver.EnumKernelModules());
                foreach (var km in kmodules)
                {
                    if (km.Name.Contains(serviceName, StringComparison.OrdinalIgnoreCase))
                    {
                        driverBase = km.BaseAddress;
                        Log($"Driver module: {km.Name} base=0x{km.BaseAddress:X16} size=0x{km.Size:X}");
                        break;
                    }
                }
            }

            // Check if this event is at our DriverEntry
            ulong expectedAddr = driverBase != 0 ? driverBase + entryRva : 0;
            if (expectedAddr != 0 && evt.Address == expectedAddr)
            {
                Log($"DriverEntry hit at {evt.Address:X16} (PID={evt.ProcessId} TID={evt.ThreadId})");
                break;
            }

            // Also accept if the event is within the driver module range
            // (in case of slight RIP adjustment)
            if (driverBase != 0 && evt.Address >= driverBase &&
                evt.Address < driverBase + 0x10000 &&
                Math.Abs((long)(evt.Address - expectedAddr)) <= 2)
            {
                Log($"DriverEntry hit at {evt.Address:X16} (near expected {expectedAddr:X16})");
                break;
            }

            // Not our event — skip it and wait for the next one
            Log($"Skipping spurious event at {evt.Address:X16} Type={evt.Type} (expected ~{expectedAddr:X16})");
            await Task.Run(() => _driver.ContinueDebugEvent(DriverComm.CONTINUE_RUN));
            evt = null;
        }

        if (evt == null)
        {
            Log("Failed to catch DriverEntry INT3 after multiple attempts");
            StatusText = "DriverEntry not reached";
            IsRunning = false;
            return;
        }

        SelectedThreadId = evt.ThreadId;
        TargetPid = evt.ProcessId;
        Log($"Found {kmodules.Count} kernel modules");

        // 4. Restore original byte at DriverEntry (replace INT3 with real byte)
        if (driverBase != 0 && entryRva != 0)
        {
            ulong entryVA = driverBase + entryRva;
            var writeOk = await Task.Run(() =>
                _driver.WriteMemory(TargetPid, entryVA, new byte[] { originalByte }));
            if (writeOk)
                Log($"Restored original byte 0x{originalByte:X2} at DriverEntry 0x{entryVA:X16}");
            else
                Log($"Warning: failed to restore byte at 0x{entryVA:X16}");
        }

        // 5. Use registers from debug event (thread is blocked in KeWaitForSingleObject,
        //    so KTRAP_FRAME-based ReadRegisters would return wrong values)
        HexAddress = 0;
        DisasmAddress = 0;

        KernelModules.ReplaceAll(kmodules);

        // Load symbols for kernel modules
        Log("Loading symbols for kernel modules...");
        StatusText = "Loading symbols...";
        int symLoaded = 0;
        int symDone = 0;
        int symTotal = kmodules.Count;
        await Task.Run(() =>
        {
            foreach (var km in kmodules)
            {
                if (_symbols.LoadModule(TargetPid, km.Name, km.BaseAddress, (uint)km.Size))
                    symLoaded++;
                int d = Interlocked.Increment(ref symDone);
                int pct = symTotal > 0 ? d * 100 / symTotal : 0;
                Application.Current?.Dispatcher.InvokeAsync(
                    () => StatusText = $"Loading symbols ({pct}%)...");
            }
        });
        Log($"Symbols: {symLoaded}/{kmodules.Count} kernel modules loaded");

        // Use registers from debug event context (captured at INT3 moment)
        var evtRegs = evt.Registers;
        ulong evtRip = evtRegs?.Rip ?? evt.Address;
        var regs = new List<Register>();
        if (evtRegs != null)
        {
            regs.AddRange(new[]
            {
                new Register { Name = "RAX", Value = evtRegs.Rax },
                new Register { Name = "RBX", Value = evtRegs.Rbx },
                new Register { Name = "RCX", Value = evtRegs.Rcx },
                new Register { Name = "RDX", Value = evtRegs.Rdx },
                new Register { Name = "RSI", Value = evtRegs.Rsi },
                new Register { Name = "RDI", Value = evtRegs.Rdi },
                new Register { Name = "RBP", Value = evtRegs.Rbp },
                new Register { Name = "RSP", Value = evtRegs.Rsp },
                new Register { Name = "R8",  Value = evtRegs.R8 },
                new Register { Name = "R9",  Value = evtRegs.R9 },
                new Register { Name = "R10", Value = evtRegs.R10 },
                new Register { Name = "R11", Value = evtRegs.R11 },
                new Register { Name = "R12", Value = evtRegs.R12 },
                new Register { Name = "R13", Value = evtRegs.R13 },
                new Register { Name = "R14", Value = evtRegs.R14 },
                new Register { Name = "R15", Value = evtRegs.R15 },
                new Register { Name = "RIP", Value = evtRip },
                new Register { Name = "RFLAGS", Value = evtRegs.Rflags },
            });
            regs.AddRange(Register.ExpandFlags(evtRegs.Rflags));
        }
        Registers.ReplaceAll(regs);

        if (evtRip != 0)
        {
            DisasmAddress = evtRip;
            Log($"{IpRegName} = {FormatAddr(evtRip)}");
        }

        // Fetch disasm, stack, hex dump
        var rspReg = regs.FirstOrDefault(r => r.Name == SpRegName);
        var disasmAddr = DisasmAddress;
        var hexAddr = HexAddress != 0 ? HexAddress : disasmAddr;
        HexAddress = hexAddr;

        Log($"Reading memory: disasm=0x{disasmAddr:X16} hex=0x{hexAddr:X16} rsp={rspReg?.Value:X16}");
        var disasmTask = Task.Run(() => _driver.ReadMemory(TargetPid, disasmAddr, 4096));
        var stackTask = rspReg != null && rspReg.Value != 0
            ? Task.Run(() => _driver.ReadMemory(TargetPid, rspReg.Value, 256))
            : Task.FromResult<byte[]?>(null);
        var hexTask = Task.Run(() => _driver.ReadMemory(TargetPid, hexAddr, 4096));
        await Task.WhenAll(disasmTask, stackTask, hexTask);

        var disasmData = disasmTask.Result;
        Log($"Disasm data: {(disasmData != null ? $"{disasmData.Length}b" : "null")}");
        if (disasmData != null)
        {
            PatchBpBytesForDisasm(disasmData, disasmAddr);
            var instrs = _disasm.Disassemble(disasmData, disasmAddr);
            Log($"Disassembled {instrs.Count} instructions");
            AnnotateInstructionsWithSymbols(instrs);
            Instructions.ReplaceAll(instrs);
        }

        var stackData = stackTask.Result;
        Log($"Stack data: {(stackData != null ? $"{stackData.Length}b" : "null")}");
        if (stackData != null && rspReg != null)
        {
            var sysModList = Modules.ToList();
            var sysKmodList = KernelModules.ToList();
            var stackItems = new List<StackEntry>();
            int sp = PointerSize;
            string spName = SpRegName;
            for (int i = 0; i < stackData.Length; i += sp)
            {
                if (i + sp > stackData.Length) break;
                ulong val = Is32Bit ? BitConverter.ToUInt32(stackData, i) : BitConverter.ToUInt64(stackData, i);
                var annotation = ResolveStackValue(TargetPid, val, sysModList, sysKmodList);
                if (annotation == null && val != 0)
                    annotation = await TryReadStringAtAsync(TargetPid, val);
                stackItems.Add(new StackEntry { Offset = $"{spName}+{i:X2}", Address = FormatAddr(val), Annotation = annotation });
            }
            StackEntries.ReplaceAll(stackItems);
        }

        var hexData = hexTask.Result;
        if (hexData != null) HexData = hexData;

        RefreshCallStack();
        RefreshImports();
        RefreshExceptions();
        RefreshSections();
        RefreshStrings();
        _ = RefreshFunctionsAsync();

        _isPausedViaSuspend = false;
        _hitSwBp = null;
        IsBreakState = true;
        IsRunning = false;
        StatusText = $"DriverEntry - {serviceName} PID {TargetPid} TID {SelectedThreadId}";
        Log($"Stopped at DriverEntry of {sysPath}");
    }

    [RelayCommand]
    private async Task ToggleAttachAsync()
    {
        if (IsDebugHookActive)
            await DetachProcess();
        else if (IsConnected && TargetPid != 0)
            await DoAttachAsync();
    }

    [RelayCommand]
    private async Task AttachProcessAsync()
    {
        if (!IsConnected || TargetPid == 0) return;
        await DoAttachAsync();
    }

    /// <summary>
    /// Debug a Windows service: stop it, restart it, catch at ServiceMain.
    /// Uses relay to control SCM, then attaches with BP on StartServiceCtrlDispatcherW.
    /// </summary>
    [RelayCommand]
    private async Task DebugServiceAsync()
    {
        if (!IsConnected || !_driver.IsRemote)
        {
            Log("Debug Service requires a remote connection (via relay)");
            return;
        }

        string serviceName = PromptInput("Debug Service",
            "Enter service name (e.g. Spooler, wuauserv, BITS):");
        if (string.IsNullOrWhiteSpace(serviceName)) return;
        serviceName = serviceName.Trim();

        Log($"[Service] Debugging service: {serviceName}");

        // 1. Query binary path
        StatusText = $"Querying {serviceName}...";
        var (_, _, binaryPath) = await Task.Run(() => _driver.QueryServiceInfo(serviceName));
        if (string.IsNullOrWhiteSpace(binaryPath))
        {
            Log("[Service] Could not query service");
            StatusText = "Query failed";
            return;
        }

        string exePath = binaryPath.Trim('"');
        if (exePath.Contains("svchost.exe", StringComparison.OrdinalIgnoreCase))
        {
            Log($"[Service] {serviceName} runs in svchost — use Attach + Show Exports");
            StatusText = "Svchost — use Attach";
            return;
        }
        Log($"[Service] Binary: {exePath}");

        // 2. Pre-resolve StartServiceCtrlDispatcherW BEFORE stopping the service.
        //    ASLR is per-boot — address is the same in all processes.
        //    Read it from the currently running service process (or any other).
        ulong dispatcherAddr = 0;
        {
            var (curPid, curState, _) = await Task.Run(() => _driver.QueryServiceInfo(serviceName));
            uint probePid = (curPid != 0 && curState != 1) ? curPid : 0;

            // If service is running, use its PID to find sechost.dll exports
            if (probePid != 0)
            {
                Log($"[Service] Probing sechost.dll from running PID {probePid}...");
                var probeMods = await Task.Run(() => _driver.EnumModules(probePid));
                var sechost = probeMods.FirstOrDefault(m =>
                    m.Name.Equals("sechost.dll", StringComparison.OrdinalIgnoreCase));
                if (sechost != null)
                    dispatcherAddr = await FindExportByNameAsync(probePid, sechost.BaseAddress, "StartServiceCtrlDispatcherW", $"sechost.dll@{probePid}");
            }

            // Fallback: try any process that has sechost.dll loaded
            if (dispatcherAddr == 0)
            {
                Log("[Service] Probing sechost.dll from process list...");
                var procs = await Task.Run(() => _driver.EnumProcesses());
                foreach (var proc in procs)
                {
                    if (proc.ProcessId <= 4) continue;
                    var mods = await Task.Run(() => _driver.EnumModules(proc.ProcessId));
                    var sec = mods.FirstOrDefault(m =>
                        m.Name.Equals("sechost.dll", StringComparison.OrdinalIgnoreCase));
                    if (sec != null)
                    {
                        Log($"[Service] Trying PID {proc.ProcessId} ({proc.Name}) sechost @ {sec.BaseAddress:X16}...");
                        dispatcherAddr = await FindExportByNameAsync(proc.ProcessId, sec.BaseAddress, "StartServiceCtrlDispatcherW", $"sechost.dll@{proc.ProcessId}");
                        if (dispatcherAddr != 0)
                        {
                            Log($"[Service] Found via PID {proc.ProcessId}");
                            break;
                        }
                    }
                    if (dispatcherAddr != 0) break;
                }
            }

            if (dispatcherAddr != 0)
                Log($"[Service] StartServiceCtrlDispatcherW = {dispatcherAddr:X16}");
            else
            {
                Log("[Service] Can't pre-resolve StartServiceCtrlDispatcherW");
                StatusText = "Failed";
                return;
            }
        }

        // 3. Stop the service
        StatusText = $"Stopping {serviceName}...";
        await Task.Run(() => _driver.StopService(serviceName));
        for (int i = 0; i < 40; i++)
        {
            await Task.Delay(500);
            var (_, st, _) = await Task.Run(() => _driver.QueryServiceInfo(serviceName));
            if (st == 1) break;
        }
        Log("[Service] Service stopped");

        // 4. Detach previous
        if (TargetPid != 0)
            await DetachProcess();

        // 5. Prepare service — relay copies binary, patches EP to EB FE,
        //    changes ImagePath.  Does NOT start the service yet.
        StatusText = $"Preparing {serviceName}...";
        var (prepared, _, entryRva, origBytes) = await Task.Run(() => _driver.StartService(serviceName));
        if (!prepared || origBytes.Length < 2)
        {
            Log("[Service] Prepare failed");
            StatusText = "Prepare failed";
            return;
        }
        Log($"[Service] Prepared: EP RVA=0x{entryRva:X}  origBytes={origBytes[0]:X2} {origBytes[1]:X2}");

        // 6. Start service via SCM (background thread) — process will spin at EB FE
        StatusText = $"Starting {serviceName}...";
        var startOk = await Task.Run(() => _driver.StartRemoteDriver(serviceName));
        if (!startOk)
        {
            Log("[Service] StartService dispatch failed");
            StatusText = "Start failed";
            return;
        }

        // 7. Poll for PID — relay's QueryServiceInfo does SCM query + fallback
        //    by _kfdebug image name via CreateToolhelp32Snapshot.
        uint svcPid = 0;
        for (int i = 0; i < 50; i++)
        {
            await Task.Delay(100);
            var (pid, st, _) = await Task.Run(() => _driver.QueryServiceInfo(serviceName));
            if (pid != 0)
            {
                svcPid = pid;
                Log($"[Service] PID={svcPid} (state={st}, spinning at EB FE)");
                break;
            }
        }
        if (svcPid == 0)
        {
            Log("[Service] Timeout waiting for service PID");
            StatusText = "PID timeout";
            return;
        }
        TargetPid = svcPid;

        // 8. Give loader time to map DLLs (process spins at EB FE entry point)
        await Task.Delay(1500);

        // 9. Install debug hook targeting this PID
        await Task.Run(() => _driver.InstallDebugHook(svcPid));
        IsDebugHookActive = true;

        // Enumerate modules + threads (process alive, spinning at EP)
        var threads = await Task.Run(() => _driver.EnumThreads(svcPid));
        Threads.ReplaceAll(threads);
        if (threads.Count > 0) SelectedThreadId = threads[0].ThreadId;

        var modules = await Task.Run(() => _driver.EnumModules(svcPid));
        Modules.ReplaceAll(modules);
        Log($"[Service] {modules.Count} modules, {threads.Count} threads");

        int symLoaded = 0;
        await Task.Run(() => { foreach (var m in modules) if (_symbols.LoadModule(svcPid, m.Name, m.BaseAddress, m.Size)) symLoaded++; });
        Log($"[Service] Symbols: {symLoaded}/{modules.Count}");

        // 10. Set a software breakpoint at entry point (handles read-only .text pages
        //     via CR0.WP trick in the driver — WriteMemory can't write to RX pages).
        //     The process is spinning at EB FE; the BP replaces EB with CC.
        ulong epAddr = 0;
        uint? epBpHandle = null;
        if (modules.Count > 0 && entryRva != 0)
        {
            epAddr = modules[0].BaseAddress + entryRva;
            epBpHandle = await Task.Run(() => _driver.SetBreakpoint(svcPid, 0, epAddr, BreakpointType.Software));
            Log($"[Service] Set entry point BP at {epAddr:X16}");
        }

        StatusText = "Waiting for entry point INT3...";
        var epEvt = await Task.Run(() => _driver.WaitDebugEvent());
        if (epEvt == null)
        {
            Log("[Service] No debug event at entry point");
            StatusText = "Failed";
            return;
        }

        Log($"[Service] Caught at entry point: {epEvt.Address:X16} (TID {epEvt.ThreadId})");
        SelectedThreadId = epEvt.ThreadId;

        // 11. Remove entry point BP, then overwrite EB FE with the real original bytes.
        //     RemoveBreakpoint restores the BP's saved byte (EB from EB FE) — wrong.
        //     We must write the real original bytes (e.g. 48 83) over the EB FE.
        //     The .text page is RX so we need ProtectMemory → write → restore.
        if (epBpHandle.HasValue)
            await Task.Run(() => _driver.RemoveBreakpoint(epBpHandle.Value));
        if (epAddr != 0)
        {
            const uint PAGE_EXECUTE_READWRITE = 0x40;
            var (protOk, oldProt) = await Task.Run(() =>
                _driver.ProtectMemory(svcPid, epAddr, 4096, PAGE_EXECUTE_READWRITE));
            Log($"[Service] ProtectMemory(RWX): ok={protOk} oldProt=0x{oldProt:X}");

            var writeOk = await Task.Run(() => _driver.WriteMemory(svcPid, epAddr, origBytes));
            Log($"[Service] WriteMemory({origBytes[0]:X2} {origBytes[1]:X2}): ok={writeOk}");

            if (protOk)
                await Task.Run(() => _driver.ProtectMemory(svcPid, epAddr, 4096, oldProt));

            // Verify the write
            var verify = await Task.Run(() => _driver.ReadMemory(svcPid, epAddr, 2));
            if (verify != null && verify.Length >= 2)
                Log($"[Service] Verify EP bytes: {verify[0]:X2} {verify[1]:X2} (expected {origBytes[0]:X2} {origBytes[1]:X2})");
            else
                Log("[Service] WARNING: could not verify EP bytes");
        }

        // 12. Set BP on StartServiceCtrlDispatcherW, continue from entry point
        Log($"[Service] BP at StartServiceCtrlDispatcherW ({dispatcherAddr:X16})");
        var dispBp = await Task.Run(() => _driver.SetBreakpoint(svcPid, 0, dispatcherAddr, BreakpointType.Software));
        Log($"[Service] Dispatcher BP handle: {(dispBp.HasValue ? dispBp.Value.ToString() : "FAILED")}");

        IsBreakState = false;
        IsRunning = true;
        StatusText = "Running to StartServiceCtrlDispatcher...";

        // STEP_PAST sets TF — first event will be a single-step, not our dispatcher BP.
        // Loop until we hit the dispatcher BP (skip single-step and other spurious events).
        DebugEvent? dispEvt = null;
        {
            var waitTask = Task.Run(() => _driver.WaitDebugEvent());
            await Task.Delay(50);
            await Task.Run(() => _driver.ContinueDebugEvent(DriverComm.CONTINUE_STEP_PAST));
            dispEvt = await waitTask;

            const int maxRetries = 30;
            for (int retry = 0; retry < maxRetries && dispEvt != null; retry++)
            {
                if (dispEvt.Type == 0 && dispEvt.Address == dispatcherAddr)
                    break;  // got the dispatcher BP
                Log($"[Service] Skipping event: type={dispEvt.Type} addr={dispEvt.Address:X16}");
                waitTask = Task.Run(() => _driver.WaitDebugEvent());
                await Task.Delay(50);
                await Task.Run(() => _driver.ContinueDebugEvent(DriverComm.CONTINUE_RUN));
                dispEvt = await waitTask;
            }
        }
        if (dispBp.HasValue)
            await Task.Run(() => _driver.RemoveBreakpoint(dispBp.Value));

        if (dispEvt == null) { Log("[Service] No event"); IsRunning = false; return; }

        Log($"[Service] Hit StartServiceCtrlDispatcher at {dispEvt.Address:X16}");
        SelectedThreadId = dispEvt.ThreadId;

        // 8. Read RCX → SERVICE_TABLE_ENTRY[0].lpServiceProc
        var regs = _driver.ReadRegisters(svcPid, SelectedThreadId, Is32Bit);
        Registers.ReplaceAll(regs);
        var rcxReg = regs.FirstOrDefault(r => r.Name == (Is32Bit ? "ECX" : "RCX"));

        if (rcxReg != null && rcxReg.Value != 0)
        {
            int ptrSize = Is32Bit ? 4 : 8;
            var procData = _driver.ReadMemory(svcPid, rcxReg.Value + (ulong)ptrSize, (uint)ptrSize);
            ulong smAddr = procData != null ? (Is32Bit ? BitConverter.ToUInt32(procData, 0) : BitConverter.ToUInt64(procData, 0)) : 0;
            if (smAddr != 0)
            {
                var symName = _symbols.ResolveAddress(svcPid, smAddr, Modules.ToList()) ?? $"{smAddr:X16}";
                Log($"[Service] ServiceMain = {symName} ({smAddr:X16})");

                // Set BP on ServiceMain, continue
                var smBp = await Task.Run(() => _driver.SetBreakpoint(svcPid, 0, smAddr, BreakpointType.Software));
                if (smBp.HasValue)
                {
                    _tempBpHandle = smBp.Value;
                    StatusText = $"Running to {symName}...";

                    var waitTask2 = Task.Run(() => _driver.WaitDebugEvent());
                    await Task.Delay(50);
                    await Task.Run(() => _driver.ContinueDebugEvent());
                    var smEvt = await waitTask2;
                    await Task.Run(() => _driver.RemoveBreakpoint(_tempBpHandle!.Value));
                    _tempBpHandle = null;

                    if (smEvt != null)
                    {
                        Log($"[Service] Stopped at ServiceMain ({smEvt.Address:X16})");
                        SelectedThreadId = smEvt.ThreadId;
                        IsRunning = false;
                        IsBreakState = true;
                        StatusText = $"ServiceMain - PID {svcPid}";
                        RefreshRegisters();

                        // Navigate disasm to ServiceMain
                        DisasmAddress = smEvt.Address;
                        var smCode = await Task.Run(() => _driver.ReadMemory(svcPid, smEvt.Address, 4096));
                        if (smCode != null) { PatchBpBytesForDisasm(smCode, smEvt.Address); Instructions.ReplaceAll(_disasm.Disassemble(smCode, smEvt.Address)); }

                        RefreshImports();
                        RefreshSections();
                        RefreshExceptions();
                        return;
                    }
                }
            }
        }

        // Fallback — stopped at dispatcher
        IsRunning = false;
        IsBreakState = true;
        StatusText = $"StartServiceCtrlDispatcher - PID {svcPid}";
        Log("[Service] Stopped at dispatcher. Read RCX for ServiceMain address.");
        RefreshRegisters();
        DisasmAddress = dispEvt.Address;
        var dCode = await Task.Run(() => _driver.ReadMemory(svcPid, dispEvt.Address, 4096));
        if (dCode != null) { PatchBpBytesForDisasm(dCode, dispEvt.Address); Instructions.ReplaceAll(_disasm.Disassemble(dCode, dispEvt.Address)); }

        RefreshImports();
        RefreshSections();
        RefreshExceptions();
    }

    private async Task DoAttachAsync()
    {
        if (!IsConnected || TargetPid == 0) return;

        StatusText = $"Attaching to PID {TargetPid}...";
        Log($"Attaching to PID {TargetPid}...");

        // Reset addresses so hex dump / disasm use the new process's RIP
        HexAddress = 0;
        DisasmAddress = 0;

        var pid = TargetPid;

        // Install debug hook for this process
        Log("Installing debug hook...");
        var hookOk = await Task.Run(() => _driver.InstallDebugHook(pid));
        if (hookOk)
        {
            IsDebugHookActive = true;
            Log("Debug hook installed");
        }
        else
        {
            Log("Warning: debug hook install failed");
        }

        // Enumerate threads & suspend them all
        Log("Enumerating threads...");
        List<ThreadInfo> threads;
        try
        {
            threads = await Task.Run(() => _driver.EnumThreads(pid));
        }
        catch (Exception ex) { Log($"EnumThreads error: {ex.Message}"); StatusText = "Attach failed"; return; }
        Log($"Found {threads.Count} threads");

        // Suspend all threads so we get a consistent snapshot
        Log("Suspending process...");
        await Task.Run(() =>
        {
            foreach (var t in threads)
                _driver.SuspendThread(t.ThreadId);
        });
        _isPausedViaSuspend = true;

        Log("Enumerating modules...");
        List<ModuleInfo> modules;
        try
        {
            modules = await Task.Run(() => _driver.EnumModules(pid));
        }
        catch (Exception ex) { Log($"EnumModules error: {ex.Message}"); StatusText = "Attach failed"; return; }
        Log($"Found {modules.Count} modules");

        // Detect 32-bit process by reading PE magic of first module
        if (modules.Count > 0 && pid != 4)
        {
            Is32Bit = await DetectIs32BitAsync(pid, modules[0].BaseAddress);
            _disasm.SetMode(Is32Bit);
            if (Is32Bit) Log("Detected 32-bit (WoW64) process");
        }

        // Load module symbols
        Log($"Loading symbols for {modules.Count} user modules...");
        StatusText = $"Loading symbols (0%)...";
        int symLoaded = 0;
        int symDone = 0;
        int symTotal = modules.Count;
        await Task.Run(() =>
        {
            foreach (var m in modules)
            {
                if (_symbols.LoadModule(pid, m.Name, m.BaseAddress, m.Size))
                    symLoaded++;
                int d = Interlocked.Increment(ref symDone);
                int pct = symTotal > 0 ? d * 100 / symTotal : 0;
                Application.Current?.Dispatcher.InvokeAsync(
                    () => StatusText = $"Loading symbols ({pct}%)...");
            }
        });
        Log($"Symbols: {symLoaded}/{modules.Count} user modules loaded");

        Threads.ReplaceAll(threads);
        if (Is32Bit)
            foreach (var m in modules) m.Is32Bit = true;
        Modules.ReplaceAll(modules);

        if (Threads.Count > 0)
            SelectedThreadId = Threads[0].ThreadId;

        // Read registers
        Log("Reading registers...");
        var tid = SelectedThreadId;
        var regs = await Task.Run(() => _driver.ReadRegisters(pid, tid, Is32Bit));
        Registers.ReplaceAll(regs);
        Log($"Got {regs.Count} registers");

        var rip = Registers.FirstOrDefault(r => r.Name == IpRegName);
        if (rip != null && rip.Value != 0)
            Log($"{IpRegName} = {FormatAddr(rip.Value)}");

        // Navigate disassembly to RIP
        if (rip != null && rip.Value != 0)
        {
            DisasmAddress = rip.Value;
            Log($"Disasm → {IpRegName} {FormatAddr(rip.Value)}");
        }
        else if (Modules.Count > 0)
        {
            var ep = ResolveEntryPoint(Modules[0].BaseAddress);
            DisasmAddress = ep;
            Log($"Disasm → {Modules[0].Name} entry point {ep:X16}");
        }

        // Fetch disasm, stack, hex dump in parallel
        var rspReg = Registers.FirstOrDefault(r => r.Name == SpRegName);
        var disasmAddr = DisasmAddress;
        var hexAddr = HexAddress != 0 ? HexAddress : disasmAddr;
        HexAddress = hexAddr;

        Log("Reading memory...");
        var disasmTask = Task.Run(() => _driver.ReadMemory(pid, disasmAddr, 4096));
        var stackTask = rspReg != null ? Task.Run(() => _driver.ReadMemory(pid, rspReg.Value, 256)) : Task.FromResult<byte[]?>(null);
        var callStackTask = rspReg != null ? Task.Run(() => _driver.ReadMemory(pid, rspReg.Value, 2048)) : Task.FromResult<byte[]?>(null);
        var hexTask = Task.Run(() => _driver.ReadMemory(pid, hexAddr, 4096));

        await Task.WhenAll(disasmTask, stackTask, callStackTask, hexTask);
        Log("Memory read complete");

        // Populate disassembly
        var disasmData = disasmTask.Result;
        Log($"Disasm data: {(disasmData != null ? $"{disasmData.Length} bytes at {disasmAddr:X16}" : "null")}");
        if (disasmData != null)
        {
            try
            {
                PatchBpBytesForDisasm(disasmData, disasmAddr);
                var instrs = _disasm.Disassemble(disasmData, disasmAddr);
                Log($"Disassembled {instrs.Count} instructions");
                AnnotateInstructionsWithSymbols(instrs);
                foreach (var instr in instrs)
                    instr.HasBreakpoint = Breakpoints.Any(b => b.Address == instr.Address);
                Instructions.ReplaceAll(instrs);
            }
            catch (Exception ex)
            {
                Log($"Disasm error: {ex.Message}");
            }
        }

        // Populate stack
        var stackData = stackTask.Result;
        if (stackData != null && rspReg != null)
        {
            var moduleList = Modules.ToList();
            var kmodList = KernelModules.ToList();
            var stackItems = new List<StackEntry>();
            int sp = PointerSize;
            string spName = SpRegName;
            for (int i = 0; i < stackData.Length; i += sp)
            {
                if (i + sp > stackData.Length) break;
                ulong val = Is32Bit ? BitConverter.ToUInt32(stackData, i) : BitConverter.ToUInt64(stackData, i);
                var annotation = ResolveStackValue(pid, val, moduleList, kmodList);
                if (annotation == null && val != 0)
                    annotation = await TryReadStringAtAsync(pid, val);
                stackItems.Add(new StackEntry { Offset = $"{spName}+{i:X2}", Address = FormatAddr(val), Annotation = annotation });
            }
            StackEntries.ReplaceAll(stackItems);
        }

        // Populate call stack
        var csData = callStackTask.Result;
        var csFrames = new List<CallStackFrame>();
        if (rip != null && rip.Value != 0)
        {
            csFrames.Add(new CallStackFrame
            {
                Index = 0,
                ReturnAddress = rip.Value,
                StackAddress = rspReg?.Value ?? 0,
                ModuleName = _symbols.ResolveAddress(pid, rip.Value, Modules.ToList())
            });
        }
        if (csData != null && rspReg != null)
        {
            int frameIdx = 1;
            for (int i = 0; i < csData.Length && frameIdx < 50; i += 8)
            {
                if (i + 8 > csData.Length) break;
                ulong val = BitConverter.ToUInt64(csData, i);
                if (val == 0) continue;
                var mod = Modules.FirstOrDefault(m =>
                    val >= m.BaseAddress && val < m.BaseAddress + m.Size);
                if (mod != null)
                {
                    csFrames.Add(new CallStackFrame
                    {
                        Index = frameIdx++,
                        ReturnAddress = val,
                        StackAddress = rspReg.Value + (ulong)i,
                        ModuleName = _symbols.ResolveAddress(pid, val, Modules.ToList())
                                     ?? $"{mod.Name}+0x{val - mod.BaseAddress:X}"
                    });
                }
            }
        }
        CallStack.ReplaceAll(csFrames);

        // Populate hex dump
        var hexData = hexTask.Result;
        if (hexData != null)
        {
            HexData = hexData;
            Log($"Hex dump: {hexData.Length} bytes at {hexAddr:X16}");
        }
        else
        {
            Log($"Hex dump: read failed at {hexAddr:X16}");
        }

        // Parse imports from main exe (exports loaded on demand via Show Exports)
        RefreshImports();
        RefreshExceptions();
        RefreshSections();
        RefreshStrings();
        _ = RefreshFunctionsAsync();

        // Auto-set breakpoint at real entry point and run
        var autoRan = await TryAutoBreakAtEntryPoint(pid, modules);

        if (!autoRan)
        {
            IsBreakState = true;
            IsRunning = false;
            StatusText = $"Paused - PID {TargetPid} TID {SelectedThreadId}";
        }
        Log($"Attached to PID {TargetPid}");
    }

    /// <summary>
    /// Try to find main/WinMain/wWinMain, set BP there and auto-run.
    /// Returns true if auto-run was started (UI will get debug event later).
    /// </summary>
    private async Task<bool> TryAutoBreakAtEntryPoint(uint pid, List<ModuleInfo> modules)
    {
        if (modules.Count == 0) return false;

        // Only auto-break if RIP is inside the main module (CRT startup).
        // If RIP is in ntdll/kernel32/etc., the process already passed main — skip.
        // If registers unavailable (paged-out trap frame on attach), treat as already running.
        var rip = Registers.FirstOrDefault(r => r.Name == IpRegName);
        if (rip != null && rip.Value != 0 && modules.Count > 0)
        {
            var mainMod = modules[0];
            bool ripInMainModule = rip.Value >= mainMod.BaseAddress &&
                                   rip.Value < mainMod.BaseAddress + mainMod.Size;
            if (!ripInMainModule)
            {
                Log("Auto-break: process already running (RIP outside main module), pausing here");
                return false;
            }
        }
        else if (_isPausedViaSuspend && (rip == null || rip.Value == 0))
        {
            Log("Auto-break: registers unavailable (trap frame paged out), pausing here");
            return false;
        }

        // Try common entry point symbol names
        string[] entryNames = ["main", "wmain", "WinMain", "wWinMain",
            $"{Path.GetFileNameWithoutExtension(modules[0].Name)}!main",
            $"{Path.GetFileNameWithoutExtension(modules[0].Name)}!wmain",
            $"{Path.GetFileNameWithoutExtension(modules[0].Name)}!WinMain",
            $"{Path.GetFileNameWithoutExtension(modules[0].Name)}!wWinMain"];

        ulong entryAddr = 0;
        string? foundName = null;

        foreach (var name in entryNames)
        {
            var addr = _symbols.ResolveNameToAddress(name);
            if (addr != 0)
            {
                entryAddr = addr;
                foundName = name;
                break;
            }
        }

        if (entryAddr == 0)
        {
            // No main/WinMain — try service detection.
            // If the process calls StartServiceCtrlDispatcherW, it's a Windows service.
            // Break there, read SERVICE_TABLE_ENTRY from RCX, then break at ServiceMain.
            var serviceResult = await TryAutoBreakAtServiceMain(pid);
            if (serviceResult) return true;

            Log("Auto-break: no main/WinMain/ServiceMain found, staying at CRT startup");
            return false;
        }

        // Set temp BP at entry point and run
        return await RunToAddress(pid, entryAddr, foundName!);
    }

    /// <summary>
    /// Detect Windows service process: set BP on StartServiceCtrlDispatcherW,
    /// read SERVICE_TABLE_ENTRY.lpServiceProc from RCX, then break at ServiceMain.
    /// </summary>
    private async Task<bool> TryAutoBreakAtServiceMain(uint pid)
    {
        // Find StartServiceCtrlDispatcherW by parsing export table of sechost/advapi32.
        // SymFromNameW often fails for system DLLs without PDBs, so we read exports directly.
        string[] targetModules = ["sechost.dll", "advapi32.dll"];
        string[] targetExports = ["StartServiceCtrlDispatcherW", "StartServiceCtrlDispatcherA"];

        ulong dispatcherAddr = 0;
        string? dispatcherName = null;

        foreach (var modName in targetModules)
        {
            var mod = Modules.FirstOrDefault(m =>
                m.Name.Equals(modName, StringComparison.OrdinalIgnoreCase));
            if (mod == null) continue;

            foreach (var exportName in targetExports)
            {
                var addr = await Task.Run(() => FindExportByName(pid, mod.BaseAddress, exportName));
                if (addr != 0)
                {
                    dispatcherAddr = addr;
                    dispatcherName = $"{modName}!{exportName}";
                    break;
                }
            }
            if (dispatcherAddr != 0) break;
        }

        // Fallback to symbol resolution
        if (dispatcherAddr == 0)
        {
            string[] symNames = [
                "sechost!StartServiceCtrlDispatcherW", "sechost.dll!StartServiceCtrlDispatcherW",
                "advapi32!StartServiceCtrlDispatcherW", "advapi32.dll!StartServiceCtrlDispatcherW",
            ];
            foreach (var name in symNames)
            {
                var addr = _symbols.ResolveNameToAddress(name);
                if (addr != 0) { dispatcherAddr = addr; dispatcherName = name; break; }
            }
        }

        if (dispatcherAddr == 0) return false;

        Log($"Auto-break: service detected, BP at {dispatcherName} ({dispatcherAddr:X16})");

        // Set BP on StartServiceCtrlDispatcher
        var h = await Task.Run(() => _driver.SetBreakpoint(pid, 0, dispatcherAddr, BreakpointType.Software));
        if (!h.HasValue) return false;
        _tempBpHandle = h.Value;

        // Direct WaitDebugEvent — no listener, no timeout polling
        IsBreakState = false;
        IsRunning = true;
        StatusText = "Running to StartServiceCtrlDispatcher...";

        var waitTask = Task.Run(() => _driver.WaitDebugEvent());
        await Task.Delay(50);

        // Resume all threads
        var threads = Threads.ToList();
        await Task.Run(() => { foreach (var t in threads) _driver.ResumeThread(t.ThreadId); });
        _isPausedViaSuspend = false;

        var dispEvt = await waitTask;

        await Task.Run(() => _driver.RemoveBreakpoint(_tempBpHandle!.Value));
        _tempBpHandle = null;

        if (dispEvt == null)
        {
            Log("Auto-break: no event from StartServiceCtrlDispatcher");
            IsRunning = false;
            return false;
        }

        Log($"Auto-break: hit StartServiceCtrlDispatcher at {dispEvt.Address:X16}");
        SelectedThreadId = dispEvt.ThreadId;

        // Read RCX → SERVICE_TABLE_ENTRY[0].lpServiceProc
        var regs = _driver.ReadRegisters(pid, SelectedThreadId, Is32Bit);
        var rcxReg = regs.FirstOrDefault(r => r.Name == (Is32Bit ? "ECX" : "RCX"));
        if (rcxReg == null || rcxReg.Value == 0)
        {
            Log("Auto-break: couldn't read RCX");
            await Task.Run(() => _driver.ContinueDebugEvent());
            IsRunning = false;
            IsBreakState = true;
            return true;
        }

        int ptrSize = Is32Bit ? 4 : 8;
        var procData = _driver.ReadMemory(pid, rcxReg.Value + (ulong)ptrSize, (uint)ptrSize);
        if (procData == null)
        {
            Log("Auto-break: couldn't read SERVICE_TABLE_ENTRY");
            await Task.Run(() => _driver.ContinueDebugEvent());
            IsRunning = false;
            IsBreakState = true;
            return true;
        }

        ulong serviceMainAddr = Is32Bit
            ? BitConverter.ToUInt32(procData, 0)
            : BitConverter.ToUInt64(procData, 0);

        if (serviceMainAddr == 0)
        {
            Log("Auto-break: ServiceMain address is null");
            await Task.Run(() => _driver.ContinueDebugEvent());
            IsRunning = false;
            IsBreakState = true;
            return true;
        }

        var symName = _symbols.ResolveAddress(pid, serviceMainAddr, Modules.ToList()) ?? $"{serviceMainAddr:X16}";
        Log($"Auto-break: ServiceMain at {symName} ({serviceMainAddr:X16})");

        // Set BP on ServiceMain
        var smBp = await Task.Run(() => _driver.SetBreakpoint(pid, 0, serviceMainAddr, BreakpointType.Software));
        if (!smBp.HasValue)
        {
            Log("Auto-break: failed to set BP on ServiceMain");
            await Task.Run(() => _driver.ContinueDebugEvent());
            IsRunning = false;
            IsBreakState = true;
            return true;
        }
        _tempBpHandle = smBp.Value;

        // Continue from dispatcher → run to ServiceMain
        StatusText = $"Running to ServiceMain...";
        var waitTask2 = Task.Run(() => _driver.WaitDebugEvent());
        await Task.Delay(50);
        await Task.Run(() => _driver.ContinueDebugEvent());

        var smEvt = await waitTask2;

        await Task.Run(() => _driver.RemoveBreakpoint(_tempBpHandle!.Value));
        _tempBpHandle = null;

        if (smEvt == null)
        {
            Log("Auto-break: no event from ServiceMain");
            IsRunning = false;
            return false;
        }

        Log($"Auto-break: hit ServiceMain at {smEvt.Address:X16}");
        SelectedThreadId = smEvt.ThreadId;
        IsRunning = false;
        IsBreakState = true;
        StatusText = $"ServiceMain - PID {pid}";

        // Refresh UI
        RefreshRegisters();
        return true;
    }

    /// <summary>
    /// Like TryAutoBreakAtServiceMain, but resumes via ContinueDebugEvent
    /// (process is stopped on a debug event, not suspended).
    /// </summary>
    private async Task<bool> TryAutoBreakAtServiceMainFromDebugEvent(uint pid)
    {
        // Find StartServiceCtrlDispatcherW via export table (runs on UI thread for logging)
        string[] targetModules = ["sechost.dll", "advapi32.dll"];
        string[] targetExports = ["StartServiceCtrlDispatcherW", "StartServiceCtrlDispatcherA"];

        ulong dispatcherAddr = 0;
        string? dispatcherName = null;

        foreach (var modName in targetModules)
        {
            var mod = Modules.FirstOrDefault(m =>
                m.Name.Equals(modName, StringComparison.OrdinalIgnoreCase));
            if (mod == null) { Log($"[Service] Module {modName} not found"); continue; }

            foreach (var exportName in targetExports)
            {
                var addr = await FindExportByNameAsync(pid, mod.BaseAddress, exportName, modName);
                if (addr != 0)
                {
                    dispatcherAddr = addr;
                    dispatcherName = $"{modName}!{exportName}";
                    break;
                }
            }
            if (dispatcherAddr != 0) break;
        }

        if (dispatcherAddr == 0)
        {
            Log("[Service] StartServiceCtrlDispatcherW not found");
            await Task.Run(() => _driver.ContinueDebugEvent());
            IsBreakState = false;
            return false;
        }

        Log($"[Service] BP at {dispatcherName} ({dispatcherAddr:X16})");

        // Set BP
        var h = await Task.Run(() => _driver.SetBreakpoint(pid, 0, dispatcherAddr, BreakpointType.Software));
        if (!h.HasValue)
        {
            await Task.Run(() => _driver.ContinueDebugEvent());
            return false;
        }
        _tempBpHandle = h.Value;

        // WaitDebugEvent FIRST, then ContinueDebugEvent to resume from entry point
        IsBreakState = false;
        IsRunning = true;
        StatusText = "Running to StartServiceCtrlDispatcher...";

        var waitTask = Task.Run(() => _driver.WaitDebugEvent());
        await Task.Delay(50);
        await Task.Run(() => _driver.ContinueDebugEvent());

        var dispEvt = await waitTask;

        await Task.Run(() => _driver.RemoveBreakpoint(_tempBpHandle!.Value));
        _tempBpHandle = null;

        if (dispEvt == null)
        {
            Log("[Service] No event at StartServiceCtrlDispatcher");
            IsRunning = false;
            return false;
        }

        Log($"[Service] Hit StartServiceCtrlDispatcher at {dispEvt.Address:X16}");
        SelectedThreadId = dispEvt.ThreadId;

        // Read RCX → SERVICE_TABLE_ENTRY[0].lpServiceProc
        var regs = _driver.ReadRegisters(pid, SelectedThreadId, Is32Bit);
        var rcxReg = regs.FirstOrDefault(r => r.Name == (Is32Bit ? "ECX" : "RCX"));
        if (rcxReg == null || rcxReg.Value == 0)
        {
            Log("[Service] RCX is null");
            await Task.Run(() => _driver.ContinueDebugEvent());
            IsRunning = false;
            IsBreakState = true;
            return true;
        }

        int ptrSize = Is32Bit ? 4 : 8;
        var procData = _driver.ReadMemory(pid, rcxReg.Value + (ulong)ptrSize, (uint)ptrSize);
        if (procData == null)
        {
            Log("[Service] Can't read SERVICE_TABLE_ENTRY");
            await Task.Run(() => _driver.ContinueDebugEvent());
            IsRunning = false;
            IsBreakState = true;
            return true;
        }

        ulong serviceMainAddr = Is32Bit
            ? BitConverter.ToUInt32(procData, 0)
            : BitConverter.ToUInt64(procData, 0);

        if (serviceMainAddr == 0)
        {
            Log("[Service] ServiceMain address is 0");
            await Task.Run(() => _driver.ContinueDebugEvent());
            IsRunning = false;
            IsBreakState = true;
            return true;
        }

        var symName = _symbols.ResolveAddress(pid, serviceMainAddr, Modules.ToList()) ?? $"{serviceMainAddr:X16}";
        Log($"[Service] ServiceMain at {symName} ({serviceMainAddr:X16})");

        // BP on ServiceMain, continue from dispatcher
        var smBp = await Task.Run(() => _driver.SetBreakpoint(pid, 0, serviceMainAddr, BreakpointType.Software));
        if (!smBp.HasValue)
        {
            await Task.Run(() => _driver.ContinueDebugEvent());
            IsRunning = false;
            IsBreakState = true;
            return true;
        }
        _tempBpHandle = smBp.Value;

        StatusText = $"Running to ServiceMain...";
        var waitTask2 = Task.Run(() => _driver.WaitDebugEvent());
        await Task.Delay(50);
        await Task.Run(() => _driver.ContinueDebugEvent());

        var smEvt = await waitTask2;

        await Task.Run(() => _driver.RemoveBreakpoint(_tempBpHandle!.Value));
        _tempBpHandle = null;

        if (smEvt == null)
        {
            Log("[Service] No event at ServiceMain");
            IsRunning = false;
            return false;
        }

        Log($"[Service] Hit ServiceMain at {smEvt.Address:X16}");
        SelectedThreadId = smEvt.ThreadId;
        IsRunning = false;
        IsBreakState = true;
        StatusText = $"ServiceMain - PID {pid}";
        RefreshRegisters();
        return true;
    }

    /// <summary>
    /// Set a temp BP at the given address, resume all threads, and start listening.
    /// </summary>
    private async Task<bool> RunToAddress(uint pid, ulong address, string label)
    {
        var handle = await Task.Run(() => _driver.SetBreakpoint(pid, 0, address, BreakpointType.Software));
        if (!handle.HasValue)
        {
            Log($"Auto-break: failed to set BP at {label} ({address:X16})");
            return false;
        }

        _tempBpHandle = handle.Value;
        Log($"Auto-break: BP at {label} ({address:X16}), running...");

        var threads = Threads.ToList();
        await Task.Run(() =>
        {
            foreach (var t in threads)
                _driver.ResumeThread(t.ThreadId);
        });
        _isPausedViaSuspend = false;
        IsBreakState = false;
        IsRunning = true;
        StatusText = $"Running to {label}...";
        StartDebugListener();
        return true;
    }

    /// <summary>
    /// Parse PE export table from memory to find a function address by name.
    /// Reads the module image in one shot to avoid thousands of small TCP reads.
    /// </summary>
    private ulong FindExportByName(uint pid, ulong moduleBase, string funcName)
    {
        try
        {
            // Find module size from Modules list
            uint modSize = 0;
            foreach (var m in Modules)
                if (m.BaseAddress == moduleBase) { modSize = m.Size; break; }
            if (modSize == 0) modSize = 2 * 1024 * 1024;
            uint readSize = Math.Min(modSize, 4 * 1024 * 1024);

            var image = _driver.ReadMemory(pid, moduleBase, readSize);
            if (image == null || image.Length < 0x40) return 0;

            var exports = ParseExportsFromBuffer(image, moduleBase, "");
            foreach (var exp in exports)
                if (exp.Function.Equals(funcName, StringComparison.Ordinal))
                    return exp.Address;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FindExport] Exception: {ex.Message}");
        }

        return 0;
    }

    /// <summary>
    /// Same as FindExportByName but runs on UI thread so Log() works. For diagnostics.
    /// </summary>
    private async Task<ulong> FindExportByNameAsync(uint pid, ulong moduleBase, string funcName, string modLabel)
    {
        if (moduleBase == 0) return 0;

        uint modSize = 0;
        foreach (var m in Modules)
            if (m.BaseAddress == moduleBase) { modSize = m.Size; break; }

        // If size unknown, read PE header first to get SizeOfImage
        if (modSize == 0)
        {
            var hdr = await Task.Run(() => _driver.ReadMemory(pid, moduleBase, 0x1000));
            if (hdr != null && hdr.Length >= 0x40 && hdr[0] == 0x4D && hdr[1] == 0x5A)
            {
                uint peOff = BitConverter.ToUInt32(hdr, 0x3C);
                if (peOff + 0x60 <= hdr.Length)
                {
                    ushort hdrMag = BitConverter.ToUInt16(hdr, (int)peOff + 0x18);
                    int sizeOfImageOff = hdrMag == 0x20B ? (int)peOff + 0x50 : (int)peOff + 0x50;
                    if (sizeOfImageOff + 4 <= hdr.Length)
                        modSize = BitConverter.ToUInt32(hdr, sizeOfImageOff);
                }
            }
        }

        if (modSize == 0) modSize = 0x100000; // 1MB fallback
        uint readSize = Math.Min(modSize, 4 * 1024 * 1024);

        Log($"[FindExport] {modLabel}: reading 0x{readSize:X} bytes...");
        var image = await Task.Run(() => _driver.ReadMemory(pid, moduleBase, readSize));
        if (image == null || image.Length < 0x40)
        {
            Log($"[FindExport] {modLabel}: read failed ({image?.Length ?? 0} bytes)");
            return 0;
        }
        Log($"[FindExport] {modLabel}: got 0x{image.Length:X} bytes");

        // PE diagnostics
        if (image[0] != 0x4D || image[1] != 0x5A) { Log($"[FindExport] {modLabel}: no MZ"); return 0; }
        uint pOff = BitConverter.ToUInt32(image, 0x3C);
        if (pOff + 0x18 > image.Length) { Log($"[FindExport] {modLabel}: PE offset too large"); return 0; }
        ushort mag = BitConverter.ToUInt16(image, (int)pOff + 0x18);
        bool x64 = mag == 0x20B;
        int edOff = x64 ? (int)pOff + 0x88 : (int)pOff + 0x78;
        uint eRva = (edOff + 8 <= image.Length) ? BitConverter.ToUInt32(image, edOff) : 0;
        uint eSz = (edOff + 8 <= image.Length) ? BitConverter.ToUInt32(image, edOff + 4) : 0;
        Log($"[FindExport] {modLabel}: PE=0x{pOff:X} magic=0x{mag:X} exportRva=0x{eRva:X} size=0x{eSz:X}");

        if (eRva == 0 || eSz == 0) { Log($"[FindExport] {modLabel}: no export directory"); return 0; }
        if (eRva + 40 > (uint)image.Length) { Log($"[FindExport] {modLabel}: export dir at 0x{eRva:X} beyond image 0x{image.Length:X}"); return 0; }

        uint nFuncs = BitConverter.ToUInt32(image, (int)eRva + 20);
        uint nNames = BitConverter.ToUInt32(image, (int)eRva + 24);
        uint addrTbl = BitConverter.ToUInt32(image, (int)eRva + 28);
        Log($"[FindExport] {modLabel}: funcs={nFuncs} names={nNames} addrTbl=0x{addrTbl:X}");

        if (addrTbl + nFuncs * 4 > (uint)image.Length)
        {
            Log($"[FindExport] {modLabel}: addrTbl 0x{addrTbl:X}+{nFuncs * 4} > image 0x{image.Length:X}");
            return 0;
        }

        var exports = ParseExportsFromBuffer(image, moduleBase, modLabel);
        Log($"[FindExport] {modLabel}: parsed {exports.Count} exports");

        foreach (var exp in exports)
            if (exp.Function.Equals(funcName, StringComparison.Ordinal))
                return exp.Address;

        return 0;
    }

    [RelayCommand]
    private async Task DetachProcess()
    {
        if (TargetPid != 0 && IsConnected)
        {
            // Remove temp breakpoint first
            if (_tempBpHandle.HasValue)
            {
                await Task.Run(() => _driver.RemoveBreakpoint(_tempBpHandle.Value));
                Log($"Removed temp BP handle={_tempBpHandle.Value}");
                _tempBpHandle = null;
            }

            // Remove all user breakpoints
            var bpList = Breakpoints.ToList();
            await Task.Run(() =>
            {
                foreach (var bp in bpList)
                    _driver.RemoveBreakpoint(bp.Handle);
            });

            foreach (var bp in bpList)
                Log($"Removed {bp.TypeName} BP at {bp.AddressHex}");
            Breakpoints.Clear();

            // Deactivate hook target BEFORE continuing — prevents the thread from
            // immediately re-entering KfReportAndBlock when Themida fires INT3/AV.
            await Task.Run(() => _driver.SetTargetPid(0xFFFFFFFF));

            // Resume all threads if suspended via SuspendThread (attach path)
            if (_isPausedViaSuspend)
            {
                var threads = Threads.ToList();
                await Task.Run(() =>
                {
                    foreach (var t in threads)
                        _driver.ResumeThread(t.ThreadId);
                });
                Log("Resumed all threads");
            }
            // If blocked on debug event (not suspended), continue so thread unblocks
            else if (IsBreakState)
            {
                await Task.Run(() => _driver.ContinueDebugEvent(DriverComm.CONTINUE_RUN));
                Log("Continued blocked thread");
            }
            _isPausedViaSuspend = false;
        }

        // Send RESET — deactivates hook (PID=invalid), removes BPs, cancels pending WAIT IRP.
        // Hook stays installed but returns FALSE for everything.
        await Task.Run(() => _driver.ResetDriver());
        IsDebugHookActive = false;
        Log("Driver reset (hook deactivated, pending WAIT cancelled)");

        StopDebugListener();

        _hitSwBp = null;
        _tempBpHandle = null;
        _allFunctions = [];
        _allImports = [];
        _allExports = [];
        TargetPid = 0;
        SelectedThreadId = 0;
        Is32Bit = false;
        _disasm.SetMode(false);
        Instructions.Clear();
        Registers.Clear();
        Modules.Clear();
        Threads.Clear();
        StackEntries.Clear();
        CallStack.Clear();
        SehChain.Clear();
        _allExceptions.Clear();
        FilteredExceptions.Clear();
        _allSections.Clear();
        FilteredSections.Clear();
        _allStrings.Clear();
        FilteredStrings.Clear();
        Imports.Clear();
        FilteredImports.Clear();
        Exports.Clear();
        FilteredExports.Clear();
        Functions.Clear();
        FilteredFunctions.Clear();
        HexData = [];
        IsBreakState = false;
        IsRunning = false;
        StatusText = IsConnected ? "Connected - No target" : "Not connected";
        Log("Detached from process");
    }

    /* ================================================================== */
    /*  Debugging: Step In (F7)                                            */
    /* ================================================================== */

    [RelayCommand]
    private async Task StepIn()
    {
        if (!IsConnected || TargetPid == 0 || SelectedThreadId == 0) return;
        if (!IsBreakState) return;

        IsBreakState = false;
        IsRunning = true;
        StatusText = "Stepping...";

        // WoW64: use EB FE spin loop (KdTrap hook doesn't catch WoW64 exceptions)
        if (_isPausedViaSuspend && Is32Bit)
        {
            var instr = GetInstructionAtRip();
            if (instr == null) { IsBreakState = true; IsRunning = false; return; }

            var targets = new List<ulong>();
            var mn = instr.Mnemonic.ToLowerInvariant();

            if (IsRetInstruction(mn))
            {
                // RET: target is [ESP]
                var espReg = Registers.FirstOrDefault(r => r.Name == "ESP");
                if (espReg != null)
                {
                    var retData = await Task.Run(() => _driver.ReadMemory(TargetPid, espReg.Value, 4));
                    if (retData != null && retData.Length >= 4)
                        targets.Add(BitConverter.ToUInt32(retData, 0));
                }
            }
            else if (IsCallInstruction(mn) || IsUnconditionalJmp(mn))
            {
                // CALL/JMP: step into target
                if (instr.BranchTargetAddress != 0)
                    targets.Add(instr.BranchTargetAddress);
                else
                    targets.Add(instr.Address + (ulong)instr.Size); // indirect — fallback to next
            }
            else if (IsConditionalJump(mn))
            {
                // Jcc: two possible targets
                targets.Add(instr.Address + (ulong)instr.Size); // fallthrough
                if (instr.BranchTargetAddress != 0)
                    targets.Add(instr.BranchTargetAddress);
            }
            else
            {
                // Normal instruction: next = IP + size
                targets.Add(instr.Address + (ulong)instr.Size);
            }

            if (targets.Count == 0) { IsBreakState = true; IsRunning = false; return; }

            var ok = await Wow64SpinStep(TargetPid, SelectedThreadId, targets.ToArray());
            await Wow64RefreshAfterStep();
            IsBreakState = true;
            IsRunning = false;
            StatusText = ok ? $"Step - PID {TargetPid} TID {SelectedThreadId}" : "Step failed";
            _hitSwBp = null;
            return;
        }

        // Native 64-bit: use debug hook mechanism.
        var statsBefore = _driver.GetHookStats();
        Log($"Step: hookCalls={statsBefore?.hookCalls} targetCalls={statsBefore?.targetCalls} " +
            $"blocked={statsBefore?.threadBlocked} kdEnabled={statsBefore?.kdEnabled}");

        Log("Step: sending WAIT + Continue(STEP_INTO)...");
        var waitTask = Task.Run(() => _driver.WaitDebugEvent());
        var contOk = _driver.ContinueDebugEvent(DriverComm.CONTINUE_STEP_INTO);
        Log($"Step: Continue sent, ok={contOk}");
        _hitSwBp = null;

        // Wait with timeout — if no event in 5s, something is wrong
        var completed = await Task.WhenAny(waitTask, Task.Delay(5000));
        if (completed == waitTask)
        {
            var stepEvt = await waitTask;
            if (stepEvt != null)
            {
                Log($"Step: event received at {stepEvt.Address:X16}");
                OnDebugEvent(stepEvt);
            }
            else
                Log("Step: WaitDebugEvent returned null");
        }
        else
        {
            var statsAfter = _driver.GetHookStats();
            Log($"Step: TIMEOUT 5s! hookCalls={statsAfter?.hookCalls} targetCalls={statsAfter?.targetCalls} " +
                $"blocked={statsAfter?.threadBlocked} mode={statsAfter?.continueMode} " +
                $"lastCode=0x{statsAfter?.lastTargetCode:X} lastAddr=0x{statsAfter?.lastTargetAddr:X}");
            IsBreakState = true;
            IsRunning = false;
            StatusText = "Step timed out";
        }
    }

    /* ================================================================== */
    /*  Debugging: Step Over (F8)                                          */
    /* ================================================================== */

    [RelayCommand]
    private async Task StepOver()
    {
        if (!IsConnected || TargetPid == 0 || SelectedThreadId == 0) return;
        if (!IsBreakState) return;

        IsBreakState = false;
        IsRunning = true;
        StatusText = "Stepping over...";

        // WoW64: use EB FE spin loop
        if (_isPausedViaSuspend && Is32Bit)
        {
            var instr = GetInstructionAtRip();
            if (instr == null) { IsBreakState = true; IsRunning = false; return; }

            var targets = new List<ulong>();
            var mn = instr.Mnemonic.ToLowerInvariant();

            if (IsRetInstruction(mn))
            {
                var espReg = Registers.FirstOrDefault(r => r.Name == "ESP");
                if (espReg != null)
                {
                    var retData = await Task.Run(() => _driver.ReadMemory(TargetPid, espReg.Value, 4));
                    if (retData != null && retData.Length >= 4)
                        targets.Add(BitConverter.ToUInt32(retData, 0));
                }
            }
            else if (IsCallInstruction(mn))
            {
                // Step OVER call: go to next instruction (skip call)
                targets.Add(instr.Address + (ulong)instr.Size);
            }
            else if (IsUnconditionalJmp(mn))
            {
                if (instr.BranchTargetAddress != 0)
                    targets.Add(instr.BranchTargetAddress);
                else
                    targets.Add(instr.Address + (ulong)instr.Size);
            }
            else if (IsConditionalJump(mn))
            {
                targets.Add(instr.Address + (ulong)instr.Size);
                if (instr.BranchTargetAddress != 0)
                    targets.Add(instr.BranchTargetAddress);
            }
            else
            {
                targets.Add(instr.Address + (ulong)instr.Size);
            }

            if (targets.Count == 0) { IsBreakState = true; IsRunning = false; return; }

            var ok = await Wow64SpinStep(TargetPid, SelectedThreadId, targets.ToArray());
            await Wow64RefreshAfterStep();
            IsBreakState = true;
            IsRunning = false;
            StatusText = ok ? $"Step over - PID {TargetPid} TID {SelectedThreadId}" : "Step over failed";
            _hitSwBp = null;
            return;
        }

        // Native 64-bit path
        var instr64 = GetInstructionAtRip();
        if (instr64 != null && IsCallInstruction(instr64.Mnemonic))
        {
            ulong nextAddr = instr64.Address + (ulong)instr64.Size;
            var tmpHandle = await Task.Run(() => _driver.SetBreakpoint(TargetPid, 0, nextAddr, BreakpointType.Software));
            if (tmpHandle.HasValue)
                _tempBpHandle = tmpHandle.Value;
            StartDebugListener();
            await Task.Run(() => _driver.ContinueDebugEvent(
                _hitSwBp != null ? DriverComm.CONTINUE_STEP_PAST : DriverComm.CONTINUE_RUN));
        }
        else
        {
            StartDebugListener();
            await Task.Run(() => _driver.ContinueDebugEvent(DriverComm.CONTINUE_STEP_INTO));
        }
        _hitSwBp = null;
    }

    /* ================================================================== */
    /*  Debugging: Step Out (Ctrl+F9 / Execute till Return)                */
    /* ================================================================== */

    [RelayCommand]
    private async Task StepOut()
    {
        if (!IsConnected || TargetPid == 0 || SelectedThreadId == 0) return;
        if (!IsBreakState) return;

        // Read return address from top of stack
        var rsp = Registers.FirstOrDefault(r => r.Name == SpRegName);
        if (rsp == null) return;

        int ptrSize = Is32Bit ? 4 : 8;
        var retData = await Task.Run(() => _driver.ReadMemory(TargetPid, rsp.Value, (uint)ptrSize));
        if (retData == null || retData.Length < ptrSize) return;
        ulong retAddr = Is32Bit ? BitConverter.ToUInt32(retData, 0) : BitConverter.ToUInt64(retData, 0);

        IsBreakState = false;
        IsRunning = true;
        StatusText = "Stepping out...";

        // WoW64: use EB FE spin loop at return address
        if (_isPausedViaSuspend && Is32Bit)
        {
            Log($"WoW64 step out: target = {retAddr:X8}");
            var ok = await Wow64SpinStep(TargetPid, SelectedThreadId, retAddr);
            await Wow64RefreshAfterStep();
            IsBreakState = true;
            IsRunning = false;
            StatusText = ok ? $"Step out - PID {TargetPid} TID {SelectedThreadId}" : "Step out failed";
            _hitSwBp = null;
            return;
        }

        // Native 64-bit path
        var tmpHandle = await Task.Run(() => _driver.SetBreakpoint(TargetPid, 0, retAddr, BreakpointType.Software));
        if (tmpHandle.HasValue)
            _tempBpHandle = tmpHandle.Value;

        StartDebugListener();
        await Task.Run(() => _driver.ContinueDebugEvent(
            _hitSwBp != null ? DriverComm.CONTINUE_STEP_PAST : DriverComm.CONTINUE_RUN));
        _hitSwBp = null;
    }

    /* ================================================================== */
    /*  Debugging: Skip Instruction (Ctrl+F8) — move RIP past current     */
    /* ================================================================== */

    [RelayCommand]
    private void SkipInstruction()
    {
        if (!IsConnected || TargetPid == 0 || SelectedThreadId == 0) return;
        if (!IsBreakState) return;

        var instr = GetInstructionAtRip();
        if (instr == null)
        {
            Log("Skip: no instruction at RIP");
            return;
        }

        ulong nextAddr = instr.Address + (ulong)instr.Size;
        bool ok = _driver.WriteRip(TargetPid, SelectedThreadId, nextAddr);
        if (ok)
        {
            Log($"Skip: {FormatAddr(instr.Address)} {instr.Mnemonic} {instr.Operands} → RIP = {FormatAddr(nextAddr)}");
            RefreshRegisters();
            RefreshDisassembly();
        }
        else
        {
            Log($"Skip: failed to set RIP to {FormatAddr(nextAddr)}");
        }
    }

    /* ================================================================== */
    /*  Debugging: Run to Cursor (F4)                                      */
    /* ================================================================== */

    [RelayCommand]
    private async Task RunToCursor()
    {
        if (!IsConnected || TargetPid == 0 || SelectedThreadId == 0) return;
        if (SelectedDisasmAddress == 0) return;

        var pid = TargetPid;
        var tid = SelectedThreadId;
        var addr = SelectedDisasmAddress;

        // WoW64: use EB FE spin loop at cursor address
        if (_isPausedViaSuspend && Is32Bit)
        {
            Log($"WoW64 run to cursor: {addr:X8}");
            IsBreakState = false;
            IsRunning = true;
            StatusText = $"Running to {FormatAddr(addr)}...";

            var ok = await Wow64SpinStep(pid, tid, addr);
            await Wow64RefreshAfterStep();
            IsBreakState = true;
            IsRunning = false;
            StatusText = ok ? $"Cursor - PID {pid} TID {tid}" : "Run to cursor failed";
            _hitSwBp = null;
            return;
        }

        // Native 64-bit path
        var handle = await Task.Run(() => _driver.SetBreakpoint(pid, tid, addr, BreakpointType.Software));
        if (handle.HasValue)
        {
            _tempBpHandle = handle.Value;
            Log($"Run to cursor: temp BP at {addr:X16}");

            IsBreakState = false;
            IsRunning = true;
            StatusText = $"Running to {addr:X16}...";
            StartDebugListener();
            await Task.Run(() => _driver.ContinueDebugEvent(
                _hitSwBp != null ? DriverComm.CONTINUE_STEP_PAST : DriverComm.CONTINUE_RUN));
            _hitSwBp = null;
        }
        else
        {
            Log("Run to cursor: failed to set temp breakpoint");
        }
    }

    /// <summary>
    /// Called by plugin via RunToCursor(address) — same as RunToCursor but with explicit address.
    /// </summary>
    private async Task PluginRunToCursor(ulong addr)
    {
        if (!IsConnected || TargetPid == 0 || SelectedThreadId == 0) return;
        if (addr == 0) return;

        var pid = TargetPid;
        var tid = SelectedThreadId;

        if (_isPausedViaSuspend && Is32Bit)
        {
            Log($"WoW64 run to cursor (plugin): {addr:X8}");
            IsBreakState = false;
            IsRunning = true;
            StatusText = $"Running to {FormatAddr(addr)}...";

            var ok = await Wow64SpinStep(pid, tid, addr);
            await Wow64RefreshAfterStep();
            IsBreakState = true;
            IsRunning = false;
            StatusText = ok ? $"Cursor - PID {pid} TID {tid}" : "Run to cursor failed";
            _hitSwBp = null;
            return;
        }

        var handle = await Task.Run(() => _driver.SetBreakpoint(pid, tid, addr, BreakpointType.Software));
        if (handle.HasValue)
        {
            _tempBpHandle = handle.Value;
            Log($"Run to cursor (plugin): temp BP at {addr:X16}");

            IsBreakState = false;
            IsRunning = true;
            StatusText = $"Running to {addr:X16}...";
            StartDebugListener();
            await Task.Run(() => _driver.ContinueDebugEvent(
                _hitSwBp != null ? DriverComm.CONTINUE_STEP_PAST : DriverComm.CONTINUE_RUN));
            _hitSwBp = null;
        }
    }

    /* ================================================================== */
    /*  Debugging: Run / Continue (F9 / F5)                                */
    /* ================================================================== */

    [RelayCommand]
    private async Task Run()
    {
        if (!IsConnected || TargetPid == 0) return;
        if (IsRunning) return;

        // Notify plugins before resuming — they can set breakpoints here
        _pluginManager.NotifyBeforeRun();

        // WoW64: use EB FE spin traps instead of 0xCC (INT3 bypasses KdTrap hook)
        if (_isPausedViaSuspend && Is32Bit)
        {
            var pid = TargetPid;
            var swBps = Breakpoints.Where(b => b.Type == BreakpointType.Software && b.Enabled).ToList();

            if (swBps.Count == 0)
            {
                // No breakpoints — just resume
                var threads = Threads.ToList();
                await Task.Run(() =>
                {
                    foreach (var t in threads)
                        _driver.ResumeThread(t.ThreadId);
                });
                _isPausedViaSuspend = false;
                IsBreakState = false;
                IsRunning = true;
                StatusText = "Running...";
                Log("WoW64 Run: no BPs, threads resumed");
                return;
            }

            // Convert 0xCC breakpoints to EB FE spin traps
            var spinLoop = new byte[] { 0xEB, 0xFE };
            var savedBytes = new Dictionary<ulong, byte[]>();

            foreach (var bp in swBps)
            {
                // Read current 2 bytes (first is 0xCC from driver BP)
                var cur = await Task.Run(() => _driver.ReadMemory(pid, bp.Address, 2));
                if (cur != null && cur.Length >= 2)
                {
                    savedBytes[bp.Address] = cur;
                    await Task.Run(() => _driver.WriteMemory(pid, bp.Address, spinLoop));
                }
            }

            Log($"WoW64 Run: {savedBytes.Count} BPs converted to EB FE spin traps");

            // Resume all threads
            var threadList = Threads.ToList();
            await Task.Run(() =>
            {
                foreach (var t in threadList)
                    _driver.ResumeThread(t.ThreadId);
            });

            IsBreakState = false;
            IsRunning = true;
            StatusText = "Running...";
            _hitSwBp = null;

            // Background poll for EIP hitting any BP address
            var bpAddrs = new HashSet<ulong>(savedBytes.Keys);
            _ = Task.Run(async () =>
            {
                ulong hitAddr = 0;
                for (int i = 0; i < 6000; i++) // 5 minutes max
                {
                    await Task.Delay(50);
                    if (!IsRunning || !IsConnected || TargetPid == 0) break;

                    var regs = _driver.ReadRegisters(pid, SelectedThreadId, true);
                    var eip = regs.FirstOrDefault(r => r.Name == "EIP");
                    if (eip != null && bpAddrs.Contains(eip.Value))
                    {
                        hitAddr = eip.Value;
                        break;
                    }
                }

                // Back on UI thread
                await Application.Current.Dispatcher.InvokeAsync(async () =>
                {
                    if (!IsConnected || TargetPid == 0) return;

                    // Suspend all threads
                    var ths = Threads.ToList();
                    await Task.Run(() =>
                    {
                        foreach (var t in ths)
                            _driver.SuspendThread(t.ThreadId);
                    });

                    // Restore saved bytes at all BP addresses (puts back 0xCC + next byte)
                    foreach (var kv in savedBytes)
                        await Task.Run(() => _driver.WriteMemory(pid, kv.Key, kv.Value));

                    if (hitAddr != 0)
                    {
                        Log($"WoW64 Run: hit BP at {hitAddr:X8}");
                        _isPausedViaSuspend = true;
                        _hitSwBp = swBps.FirstOrDefault(b => b.Address == hitAddr);
                        if (_hitSwBp != null) _hitSwBp.HitCount++;

                        DisasmAddress = hitAddr;
                        await Wow64RefreshAfterStep();
                        IsBreakState = true;
                        IsRunning = false;
                        StatusText = $"Breakpoint - PID {pid} TID {SelectedThreadId}";
                    }
                    else
                    {
                        // Timeout or stopped by user
                        _isPausedViaSuspend = true;
                        IsBreakState = true;
                        IsRunning = false;
                    }
                });
            });
            return;
        }

        // If paused via thread suspend (non-WoW64), resume all threads
        if (_isPausedViaSuspend)
        {
            var pid = TargetPid;
            var threads = Threads.ToList();
            await Task.Run(() =>
            {
                foreach (var t in threads)
                    _driver.ResumeThread(t.ThreadId);
            });
            _isPausedViaSuspend = false;
            IsBreakState = false;
            IsRunning = true;
            StatusText = "Running...";
            Log("Run: threads resumed");
            StartDebugListener();
            return;
        }

        // Native 64-bit: start listener BEFORE continuing
        IsBreakState = false;
        IsRunning = true;
        StatusText = "Running...";
        StartDebugListener();

        var mode = _hitSwBp != null ? DriverComm.CONTINUE_STEP_PAST : DriverComm.CONTINUE_RUN;
        Log($"Run: sending ContinueDebugEvent(mode={mode})");
        var ok = await Task.Run(() => _driver.ContinueDebugEvent(mode));
        Log($"Run: ContinueDebugEvent returned {ok}");
        _hitSwBp = null;

        // Deferred BP verification: check 0xCC is still present after process runs
        _ = VerifyBreakpointsAfterDelay();
    }

    private async Task VerifyBreakpointsAfterDelay()
    {
        await Task.Delay(2000);
        if (!IsRunning || !IsConnected || TargetPid == 0) return;
        var pid = TargetPid;
        var swBps = Breakpoints.Where(b => b.Type == BreakpointType.Software).ToList();
        if (swBps.Count == 0) return;
        Log($"[BP Verify] Checking {swBps.Count} SW breakpoints...");
        foreach (var bp in swBps)
        {
            var data = await Task.Run(() => _driver.ReadMemory(pid, bp.Address, 16));
            if (data != null && data.Length >= 1)
            {
                string hexDump = BitConverter.ToString(data.Take(8).ToArray()).Replace("-", " ");
                Log($"[BP Verify] {bp.Address:X16}: first byte=0x{data[0]:X2} {(data[0] == 0xCC ? "OK" : "MISSING!")} [{hexDump}]");
            }
            else
                Log($"[BP Verify] {bp.Address:X16}: read FAILED");
        }

        // Poll hook stats every 3s while running (up to 30s) to see
        // if calls/bpHit changes after user triggers the function
        for (int i = 0; i < 10; i++)
        {
            var stats = await Task.Run(() => _driver.GetHookStats());
            if (stats.HasValue)
            {
                var s = stats.Value;
                Log($"[Hook Stats #{i}] calls={s.hookCalls} target={s.targetCalls} bpHit={s.bpHits} bpSkip={s.bpNotFound} KdE={s.kdEnabled} lastAddr={s.lastTargetAddr:X} lastCode={s.lastTargetCode:X8}");
                Log($"  KiDbgR={s.kiDebugAddr:X}|orig={s.kiDebugOrig:X}|now={s.kiDebugNow:X} hooked={s.hookedFunc:X} KdTrap={s.kdTrap:X}");
            }
            await Task.Delay(3000);
            if (!IsRunning || !IsConnected) break;
        }
    }

    [RelayCommand]
    private async Task ContinueExecution()
    {
        if (!IsConnected) return;
        await Run();
    }

    /* ================================================================== */
    /*  Debugging: Pause (F12)                                             */
    /* ================================================================== */

    [RelayCommand]
    private async Task Pause()
    {
        if (!IsConnected || TargetPid == 0) return;
        if (IsBreakState) return; // already paused

        Log("Pausing process...");

        // Suspend all threads
        var pid = TargetPid;
        var threads = await Task.Run(() => _driver.EnumThreads(pid));
        await Task.Run(() =>
        {
            foreach (var t in threads)
                _driver.SuspendThread(t.ThreadId);
        });

        StopDebugListener();

        // Pick first thread and read its state
        Threads.ReplaceAll(threads);
        if (Threads.Count > 0)
            SelectedThreadId = Threads[0].ThreadId;

        var tid = SelectedThreadId;
        var regs = await Task.Run(() => _driver.ReadRegisters(pid, tid, Is32Bit));
        Registers.ReplaceAll(regs);

        var rip = Registers.FirstOrDefault(r => r.Name == IpRegName);
        if (rip != null && rip.Value != 0)
        {
            DisasmAddress = rip.Value;
            Log($"Paused at {IpRegName} = {FormatAddr(rip.Value)}");
        }

        // Refresh disasm + hex dump + stack
        await RefreshAllViews();

        IsBreakState = true;
        IsRunning = false;
        _isPausedViaSuspend = true;
        StatusText = $"Paused - PID {TargetPid} TID {SelectedThreadId}";
    }

    /* ================================================================== */
    /*  Step-past: temporarily remove BP, single step, re-arm             */
    /* ================================================================== */

    // NOTE: Step-past is handled internally by the driver via CONTINUE_STEP_PAST
    // and CONTINUE_STEP_INTO modes. This method is kept only as a fallback
    // but should not be needed in normal operation.

    /* ================================================================== */
    /*  Breakpoints                                                        */
    /* ================================================================== */

    [RelayCommand]
    private void ToggleBreakpoint()
    {
        ToggleBreakpointAtAddress(SelectedDisasmAddress != 0 ? SelectedDisasmAddress : DisasmAddress,
                                  BreakpointType.Software);
    }

    [RelayCommand]
    private void ToggleHwBreakpoint()
    {
        ToggleBreakpointAtAddress(SelectedDisasmAddress != 0 ? SelectedDisasmAddress : DisasmAddress,
                                  BreakpointType.Hardware);
    }

    [RelayCommand]
    private void ToggleHwWriteBreakpoint()
    {
        ToggleBreakpointAtAddress(SelectedDisasmAddress != 0 ? SelectedDisasmAddress : DisasmAddress,
                                  BreakpointType.HwWrite, 8);
    }

    [RelayCommand]
    private void ToggleHwRwBreakpoint()
    {
        ToggleBreakpointAtAddress(SelectedDisasmAddress != 0 ? SelectedDisasmAddress : DisasmAddress,
                                  BreakpointType.HwReadWrite, 8);
    }

    [RelayCommand]
    private void ToggleMemoryBreakpoint()
    {
        ToggleBreakpointAtAddress(SelectedDisasmAddress != 0 ? SelectedDisasmAddress : DisasmAddress,
                                  BreakpointType.Memory);
    }

    /// <summary>Toggle a software breakpoint at a specific address (used by disasm context menus).</summary>
    public void SetBreakpointAtAddress(ulong address)
        => ToggleBreakpointAtAddress(address, BreakpointType.Software);

    /// <summary>Toggle a breakpoint of given type at a specific address (used by hex dump context menus).</summary>
    public void SetBreakpointAtAddressWithType(ulong address, BreakpointType type, uint length = 1)
        => ToggleBreakpointAtAddress(address, type, length);

    /* ================================================================== */
    /*  Disasm navigation history (Go Back)                                */
    /* ================================================================== */

    private readonly Stack<ulong> _disasmBackStack = new();

    public void PushDisasmHistory()
    {
        if (DisasmAddress != 0)
            _disasmBackStack.Push(DisasmAddress);
    }

    [RelayCommand]
    private void DisasmGoBack()
    {
        if (_disasmBackStack.Count == 0) return;
        var addr = _disasmBackStack.Pop();
        DisasmAddress = addr;
        RefreshDisassembly();
    }

    public bool CanDisasmGoBack => _disasmBackStack.Count > 0;

    /// <summary>Navigate disassembly to a specific address (used by disasm context menus).</summary>
    public void NavigateDisasmTo(ulong address)
    {
        if (address == 0) return;
        PushDisasmHistory();
        DisasmAddress = address;
        RefreshDisassembly();
        SwitchToDisasmTab?.Invoke();
    }

    private async void ToggleBreakpointAtAddress(ulong address, BreakpointType type, uint length = 1)
    {
        if (!IsConnected || TargetPid == 0 || address == 0)
        {
            Log($"BP skipped: Connected={IsConnected} PID={TargetPid} Addr={address:X16}");
            return;
        }

        var existing = Breakpoints.FirstOrDefault(b => b.Address == address && b.Type == type);
        if (existing != null)
        {
            await Task.Run(() => _driver.RemoveBreakpoint(existing.Handle));
            Breakpoints.Remove(existing);
            Log($"Removed {type} breakpoint at {address:X16}");
        }
        else
        {
            uint tid = type is BreakpointType.Hardware or BreakpointType.HwWrite or BreakpointType.HwReadWrite
                        ? SelectedThreadId : 0;

            // Read original byte before 0xCC is written (for SW BP display)
            byte origByte = 0;
            if (type == BreakpointType.Software)
            {
                var orig = await Task.Run(() => _driver.ReadMemory(TargetPid, address, 1));
                if (orig != null && orig.Length >= 1)
                    origByte = orig[0];
            }

            Log($"Setting {type} BP at {address:X16} PID={TargetPid} TID={tid}...");
            var handle = await Task.Run(() => _driver.SetBreakpoint(TargetPid, tid, address, type, length));
            Log($"SetBreakpoint result: {(handle.HasValue ? $"handle={handle.Value}" : "null")}");
            if (handle.HasValue)
            {
                var bp = new Breakpoint
                {
                    Handle = handle.Value,
                    Address = address,
                    Type = type,
                    OriginalByte = origByte,
                    ModuleName = _symbols.ResolveAddress(TargetPid, address, Modules.ToList()),
                    Is32Bit = Is32Bit
                };
                Breakpoints.Add(bp);
                Log($"Set {bp.TypeName} breakpoint at {address:X16}");

                // Readback verification: check that 0xCC was actually written
                if (type == BreakpointType.Software)
                {
                    var readback = await Task.Run(() => _driver.ReadMemory(TargetPid, address, 1));
                    if (readback != null && readback.Length >= 1)
                        Log($"BP readback at {address:X16}: 0x{readback[0]:X2} {(readback[0] == 0xCC ? "(OK)" : "(MISMATCH! expected 0xCC)")}");
                    else
                        Log($"BP readback at {address:X16}: FAILED to read");
                }
            }
            else
            {
                Log($"Failed to set {type} BP at {address:X16}");
            }
        }
        SyncBreakpointMarkers();
        RefreshDisassembly();
    }

    [RelayCommand]
    private async Task SetConditionalBreakpoint()
    {
        if (!IsConnected || TargetPid == 0) return;
        ulong addr = SelectedDisasmAddress != 0 ? SelectedDisasmAddress : DisasmAddress;
        if (addr == 0) return;

        string condition = PromptInput("Conditional Breakpoint",
            "Enter condition (e.g. RAX==0, RCX!=0, RDX>100):");
        if (string.IsNullOrWhiteSpace(condition)) return;

        var pid = TargetPid;
        var tid = SelectedThreadId;

        byte origByte = 0;
        var orig = await Task.Run(() => _driver.ReadMemory(pid, addr, 1));
        if (orig != null && orig.Length >= 1) origByte = orig[0];

        var handle = await Task.Run(() => _driver.SetBreakpoint(pid, tid, addr, BreakpointType.Software));
        if (handle.HasValue)
        {
            Breakpoints.Add(new Breakpoint
            {
                Handle = handle.Value,
                Address = addr,
                Type = BreakpointType.Software,
                OriginalByte = origByte,
                Condition = condition,
                ModuleName = _symbols.ResolveAddress(pid, addr, Modules.ToList())
            });
            Log($"Set conditional breakpoint at {addr:X16} [{condition}]");
            RefreshDisassembly();
        }
    }

    [RelayCommand]
    private async Task SetLogBreakpoint()
    {
        if (!IsConnected || TargetPid == 0) return;
        ulong addr = SelectedDisasmAddress != 0 ? SelectedDisasmAddress : DisasmAddress;
        if (addr == 0) return;

        string expr = PromptInput("Log Breakpoint",
            "Enter log expression (e.g. RAX, \"called func\", RCX+RDX):");
        if (string.IsNullOrWhiteSpace(expr)) return;

        var pid = TargetPid;
        var tid = SelectedThreadId;

        byte origByte = 0;
        var origData = await Task.Run(() => _driver.ReadMemory(pid, addr, 1));
        if (origData != null && origData.Length >= 1) origByte = origData[0];

        var handle = await Task.Run(() => _driver.SetBreakpoint(pid, tid, addr, BreakpointType.Software));
        if (handle.HasValue)
        {
            Breakpoints.Add(new Breakpoint
            {
                OriginalByte = origByte,
                Handle = handle.Value,
                Address = addr,
                Type = BreakpointType.Software,
                LogExpression = expr,
                Condition = "false",  // never break, just log
                ModuleName = _symbols.ResolveAddress(pid, addr, Modules.ToList())
            });
            Log($"Set log breakpoint at {addr:X16} [log: {expr}]");
            RefreshDisassembly();
        }
    }

    [RelayCommand]
    private async Task RemoveAllBreakpoints()
    {
        var bpList = Breakpoints.ToList();
        await Task.Run(() =>
        {
            foreach (var bp in bpList)
                _driver.RemoveBreakpoint(bp.Handle);
        });
        Breakpoints.Clear();
        Log("Removed all breakpoints");
        RefreshDisassembly();
    }

    /* ================================================================== */
    /*  Conditional breakpoint evaluation                                  */
    /* ================================================================== */

    private bool EvaluateCondition(string condition)
    {
        try
        {
            condition = condition.Trim();
            if (condition == "false") return false;
            if (condition == "true") return true;

            string op;
            string[] parts;
            if (condition.Contains("!="))      { op = "!="; parts = condition.Split("!=", 2); }
            else if (condition.Contains("==")) { op = "=="; parts = condition.Split("==", 2); }
            else if (condition.Contains(">=")) { op = ">="; parts = condition.Split(">=", 2); }
            else if (condition.Contains("<=")) { op = "<="; parts = condition.Split("<=", 2); }
            else if (condition.Contains('>'))  { op = ">";  parts = condition.Split('>', 2); }
            else if (condition.Contains('<'))  { op = "<";  parts = condition.Split('<', 2); }
            else return true;

            string regName = parts[0].Trim().ToUpperInvariant();
            string valStr = parts[1].Trim();

            var reg = Registers.FirstOrDefault(r => r.Name == regName);
            if (reg == null) return true;

            ulong compareVal;
            if (valStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                compareVal = Convert.ToUInt64(valStr[2..], 16);
            else if (ulong.TryParse(valStr, out var dec))
                compareVal = dec;
            else
                return true;

            return op switch
            {
                "==" => reg.Value == compareVal,
                "!=" => reg.Value != compareVal,
                ">"  => reg.Value > compareVal,
                "<"  => reg.Value < compareVal,
                ">=" => reg.Value >= compareVal,
                "<=" => reg.Value <= compareVal,
                _ => true
            };
        }
        catch
        {
            return true;
        }
    }

    private string EvaluateLogExpression(string expr)
    {
        var sb = new StringBuilder(expr);
        foreach (var reg in Registers)
            sb.Replace(reg.Name, $"0x{reg.Value:X}");
        return sb.ToString();
    }

    /* ================================================================== */
    /*  Navigation                                                         */
    /* ================================================================== */

    [RelayCommand]
    private void GoToAddress(string? addressText)
    {
        if (string.IsNullOrWhiteSpace(addressText)) return;

        // Try hex address first
        var trimmed = addressText.Trim();
        if (ulong.TryParse(trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? trimmed[2..] : trimmed,
                System.Globalization.NumberStyles.HexNumber, null, out var addr))
        {
            PushDisasmHistory();
            DisasmAddress = addr;
            RefreshDisassembly();
            Log($"Navigate to {addr:X16}");
            return;
        }

        // Try symbol name (e.g. "WinMain", "ntdll!NtClose", "main")
        var resolved = _symbols.ResolveNameToAddress(trimmed);
        if (resolved != 0)
        {
            PushDisasmHistory();
            DisasmAddress = resolved;
            RefreshDisassembly();
            Log($"Navigate to {trimmed} = {resolved:X16}");
        }
        else
        {
            Log($"Symbol not found: {trimmed}");
        }
    }

    [RelayCommand]
    private void GoToHexAddress(string? addressText)
    {
        if (string.IsNullOrWhiteSpace(addressText)) return;
        if (ulong.TryParse(addressText.TrimStart('0', 'x', 'X'),
                System.Globalization.NumberStyles.HexNumber, null, out var addr))
        {
            HexAddress = addr;
            RefreshHexDump();
        }
    }

    [RelayCommand]
    private void GoToRip()
    {
        PushDisasmHistory();
        NavigateToRip();
        RefreshDisassembly();
    }

    private void NavigateToRip()
    {
        var rip = Registers.FirstOrDefault(r => r.Name == IpRegName);
        if (rip != null && rip.Value != 0)
            DisasmAddress = rip.Value;
    }

    /* ================================================================== */
    /*  Follow in Dump / Follow in Disassembler (OllyDbg context menu)     */
    /* ================================================================== */

    [RelayCommand]
    private void FollowInDump(ulong address)
    {
        if (address == 0) return;
        HexAddress = address;
        RefreshHexDump();
        Log($"Follow in dump: {address:X16}");
    }

    /// <summary>
    /// Reads and displays UNWIND_INFO for an exception entry.
    /// </summary>
    public async void ShowUnwindInfo(ExceptionEntry entry)
    {
        if (!IsConnected || entry.UnwindInfoAddr == 0) return;

        // Determine pid: kernel module or user module
        uint pid = KernelModules.Any(m => m.Name == entry.ModuleName) ? 4u : TargetPid;
        var data = await Task.Run(() => _driver.ReadMemory(pid, entry.UnwindInfoAddr, 64));
        if (data == null || data.Length < 4)
        {
            Log($"Unwind info: failed to read at {entry.UnwindInfoAddr:X16}");
            return;
        }

        // UNWIND_INFO structure:
        // Byte 0: Version (3 bits) | Flags (5 bits)
        // Byte 1: Size of prolog
        // Byte 2: Count of unwind codes
        // Byte 3: Frame register (4 bits) | Frame register offset (4 bits)
        byte versionFlags = data[0];
        int version = versionFlags & 0x7;
        int flags = (versionFlags >> 3) & 0x1F;
        byte prologSize = data[1];
        byte codeCount = data[2];
        byte frameInfo = data[3];
        int frameReg = frameInfo & 0xF;
        int frameOff = (frameInfo >> 4) & 0xF;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"=== Unwind Info for {entry.Display} ===");
        sb.AppendLine($"Address:      {entry.UnwindInfoAddr:X16}");
        sb.AppendLine($"Version:      {version}");
        sb.AppendLine($"Flags:        0x{flags:X} ({FormatUnwindFlags(flags)})");
        sb.AppendLine($"Prolog Size:  {prologSize} bytes");
        sb.AppendLine($"Code Count:   {codeCount}");
        if (frameReg != 0)
            sb.AppendLine($"Frame Reg:    {GetRegName(frameReg)} (offset 0x{frameOff * 16:X})");

        // Parse unwind codes
        int codesOffset = 4;
        for (int i = 0; i < codeCount && codesOffset + 1 < data.Length; i++)
        {
            byte offsetInProlog = data[codesOffset];
            byte opInfo = data[codesOffset + 1];
            int opCode = opInfo & 0xF;
            int info = (opInfo >> 4) & 0xF;
            sb.AppendLine($"  [{i}] Prolog+{offsetInProlog:X2}: {FormatUnwindOp(opCode, info)}");
            codesOffset += 2;
            // Some ops use extra slots
            if (opCode == 0 || opCode == 1 || opCode == 2 || opCode == 4 || opCode == 6 || opCode == 8)
            { /* 1 slot */ }
            else if (opCode == 3 || opCode == 7 || opCode == 9)
            { codesOffset += 2; i++; /* 2 slots */ }
            else if (opCode == 5 || opCode == 10)
            { codesOffset += 4; i += 2; /* 3 slots */ }
        }

        // Chained handler
        if ((flags & 0x04) != 0) // UNW_FLAG_CHAININFO
            sb.AppendLine("  [Chained to another RUNTIME_FUNCTION]");
        if ((flags & 0x01) != 0) // UNW_FLAG_EHANDLER
            sb.AppendLine("  [Has exception handler (__C_specific_handler)]");
        if ((flags & 0x02) != 0) // UNW_FLAG_UHANDLER
            sb.AppendLine("  [Has unwind handler]");

        Log(sb.ToString());
    }

    private static string FormatUnwindFlags(int flags)
    {
        var parts = new List<string>();
        if ((flags & 1) != 0) parts.Add("EHANDLER");
        if ((flags & 2) != 0) parts.Add("UHANDLER");
        if ((flags & 4) != 0) parts.Add("CHAININFO");
        return parts.Count > 0 ? string.Join(" | ", parts) : "none";
    }

    private static string GetRegName(int reg) => reg switch
    {
        0 => "RAX", 1 => "RCX", 2 => "RDX", 3 => "RBX",
        4 => "RSP", 5 => "RBP", 6 => "RSI", 7 => "RDI",
        8 => "R8", 9 => "R9", 10 => "R10", 11 => "R11",
        12 => "R12", 13 => "R13", 14 => "R14", 15 => "R15",
        _ => $"Reg{reg}"
    };

    private static string FormatUnwindOp(int opCode, int info) => opCode switch
    {
        0 => $"PUSH_NONVOL {GetRegName(info)}",
        1 => $"ALLOC_LARGE (info={info})",
        2 => $"ALLOC_SMALL {(info + 1) * 8} bytes",
        3 => $"SET_FPREG {GetRegName(info)}",
        4 => $"SAVE_NONVOL {GetRegName(info)}",
        5 => $"SAVE_NONVOL_FAR {GetRegName(info)}",
        6 => "EPILOG",
        7 => "SPARE",
        8 => $"SAVE_XMM128 XMM{info}",
        9 => $"SAVE_XMM128_FAR XMM{info}",
        10 => $"PUSH_MACHFRAME (info={info})",
        _ => $"UNKNOWN_OP({opCode}, info={info})"
    };

    [RelayCommand]
    private void FollowInDisasm(ulong address)
    {
        if (address == 0) return;
        PushDisasmHistory();
        DisasmAddress = address;
        RefreshDisassembly();
        Log($"Follow in disassembler: {address:X16}");
    }

    /* ================================================================== */
    /*  Copy operations (OllyDbg context menu)                             */
    /* ================================================================== */

    [RelayCommand]
    private void CopyAddress()
    {
        ulong addr = SelectedDisasmAddress != 0 ? SelectedDisasmAddress : DisasmAddress;
        if (addr != 0)
            Clipboard.SetText($"{addr:X16}");
    }

    [RelayCommand]
    private void CopyDisasmLine()
    {
        var instr = Instructions.FirstOrDefault(i => i.Address == SelectedDisasmAddress);
        if (instr != null)
            Clipboard.SetText($"{instr.AddressHex}  {instr.BytesHex}  {instr.FullText}");
    }

    [RelayCommand]
    private void CopyAllDisasm()
    {
        var sb = new StringBuilder();
        foreach (var instr in Instructions)
            sb.AppendLine($"{instr.AddressHex}  {instr.BytesHex,-30}  {instr.FullText}");
        Clipboard.SetText(sb.ToString());
    }

    [RelayCommand]
    private void CopyRegisterValue()
    {
        var sb = new StringBuilder();
        foreach (var reg in Registers)
            sb.AppendLine($"{reg.Name,-8} = {reg.ValueHex}");
        Clipboard.SetText(sb.ToString());
    }

    /* ================================================================== */
    /*  Register editing                                                   */
    /* ================================================================== */

    /// <summary>
    /// Modify a general-purpose register, RIP, or RFLAGS/EFLAGS by name.
    /// Uses read-modify-write via the full WRITE_REGISTERS IOCTL.
    /// </summary>
    public void EditRegister(Register reg)
    {
        if (!IsConnected || TargetPid == 0 || !IsBreakState) return;
        if (reg.IsFlag) { ToggleFlag(reg); return; }

        string currentHex = reg.Is32Bit ? $"{reg.Value:X8}" : $"{reg.Value:X16}";
        string input = PromptInput("Modify Register", $"New value for {reg.Name} (hex):", currentHex);
        if (string.IsNullOrWhiteSpace(input)) return;

        input = input.Trim();
        if (input.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            input = input[2..];

        if (!ulong.TryParse(input, System.Globalization.NumberStyles.HexNumber, null, out ulong newValue))
        {
            MessageBox.Show("Invalid hex value.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        WriteRegisterValue(reg.Name, newValue);
    }

    /// <summary>
    /// Toggle a single CPU flag (CF, ZF, SF, etc.) by flipping the corresponding bit in RFLAGS.
    /// </summary>
    public void ToggleFlag(Register flag)
    {
        if (!IsConnected || TargetPid == 0 || !IsBreakState) return;
        if (!flag.IsFlag) return;

        int bitIndex = flag.Name switch
        {
            "CF" => 0, "PF" => 2, "AF" => 4, "ZF" => 6,
            "SF" => 7, "TF" => 8, "IF" => 9, "DF" => 10, "OF" => 11,
            _ => -1
        };
        if (bitIndex < 0) return;

        var rflagsReg = Registers.FirstOrDefault(r => r.Name == "RFLAGS" || r.Name == "EFLAGS");
        if (rflagsReg == null) return;

        ulong newRflags = rflagsReg.Value ^ (1UL << bitIndex);
        WriteRegisterValue(rflagsReg.Name, newRflags);
    }

    /// <summary>
    /// Zero out a register.
    /// </summary>
    public void ZeroRegister(Register reg)
    {
        if (!IsConnected || TargetPid == 0 || !IsBreakState) return;
        if (reg.IsFlag) return;
        WriteRegisterValue(reg.Name, 0);
    }

    /// <summary>
    /// Increment a register by 1.
    /// </summary>
    public void IncrementRegister(Register reg)
    {
        if (!IsConnected || TargetPid == 0 || !IsBreakState) return;
        if (reg.IsFlag) return;
        WriteRegisterValue(reg.Name, reg.Value + 1);
    }

    /// <summary>
    /// Decrement a register by 1.
    /// </summary>
    public void DecrementRegister(Register reg)
    {
        if (!IsConnected || TargetPid == 0 || !IsBreakState) return;
        if (reg.IsFlag) return;
        WriteRegisterValue(reg.Name, reg.Value - 1);
    }

    private void WriteRegisterValue(string regName, ulong newValue)
    {
        var pid = TargetPid;
        var tid = SelectedThreadId;

        if (!_driver.WriteRegisterByName(pid, tid, regName, newValue))
        {
            MessageBox.Show("Failed to write register.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        RefreshRegisters();
    }

    /* ================================================================== */
    /*  Search (OllyDbg: Ctrl+F — binary pattern / string search)          */
    /* ================================================================== */

    [RelayCommand]
    private async Task SearchBinary()
    {
        if (!IsConnected || TargetPid == 0) return;

        string pattern = PromptInput("Binary Search",
            "Enter hex bytes (e.g. 48 89 5C 24 or 488B??):");
        if (string.IsNullOrWhiteSpace(pattern)) return;

        SearchResults.Clear();
        var patternBytes = ParseSearchPattern(pattern);
        if (patternBytes.Count == 0) return;

        var pid = TargetPid;
        var mods = Modules.ToList();

        var results = await Task.Run(() =>
        {
            var found = new List<SearchResult>();
            foreach (var module in mods)
            {
                var data = _driver.ReadMemory(pid, module.BaseAddress,
                                               Math.Min(module.Size, 1048576u));
                if (data == null) continue;

                for (int i = 0; i <= data.Length - patternBytes.Count; i++)
                {
                    bool match = true;
                    for (int j = 0; j < patternBytes.Count; j++)
                    {
                        if (patternBytes[j] is { } expected && data[i + j] != expected)
                        { match = false; break; }
                    }
                    if (match)
                    {
                        found.Add(new SearchResult
                        {
                            Address = module.BaseAddress + (ulong)i,
                            ModuleName = module.Name,
                            Preview = BitConverter.ToString(data, i, Math.Min(16, data.Length - i)).Replace("-", " "),
                            Is32Bit = Is32Bit
                        });
                        if (found.Count >= 1000) break;
                    }
                }
                if (found.Count >= 1000) break;
            }
            return found;
        });

        SearchResults.ReplaceAll(results);
        Log($"Binary search: found {SearchResults.Count} results for [{pattern}]");
    }

    [RelayCommand]
    private async Task SearchStrings()
    {
        if (!IsConnected || TargetPid == 0) return;

        string text = PromptInput("String Search", "Enter string to find:");
        if (string.IsNullOrWhiteSpace(text)) return;

        SearchResults.Clear();
        byte[] asciiPattern = Encoding.ASCII.GetBytes(text);
        byte[] unicodePattern = Encoding.Unicode.GetBytes(text);

        var pid = TargetPid;
        var mods = Modules.ToList();
        var is32 = Is32Bit;

        var results = await Task.Run(() =>
        {
            var found = new List<SearchResult>();
            foreach (var module in mods)
            {
                var data = _driver.ReadMemory(pid, module.BaseAddress,
                                               Math.Min(module.Size, 1048576u));
                if (data == null) continue;

                SearchInDataBg(found, data, asciiPattern, module.BaseAddress, module.Name, "ASCII", is32);
                SearchInDataBg(found, data, unicodePattern, module.BaseAddress, module.Name, "Unicode", is32);
                if (found.Count >= 1000) break;
            }
            return found;
        });

        SearchResults.ReplaceAll(results);
        Log($"String search: found {SearchResults.Count} results for \"{text}\"");
    }

    private static void SearchInDataBg(List<SearchResult> results, byte[] data, byte[] pattern,
        ulong baseAddr, string moduleName, string encoding, bool is32Bit = false)
    {
        for (int i = 0; i <= data.Length - pattern.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                if (data[i + j] != pattern[j]) { match = false; break; }
            }
            if (match)
            {
                results.Add(new SearchResult
                {
                    Address = baseAddr + (ulong)i,
                    ModuleName = moduleName,
                    Is32Bit = is32Bit,
                    Preview = $"[{encoding}] \"{TruncateString(Encoding.ASCII.GetString(data, i, Math.Min(64, data.Length - i)), 60)}\""
                });
                if (results.Count >= 1000) return;
            }
        }
    }

    [RelayCommand]
    private async Task SearchIntermodularCalls()
    {
        if (!IsConnected || TargetPid == 0) return;

        SearchResults.Clear();

        var pid = TargetPid;
        var mods = Modules.ToList();

        var results = await Task.Run(() =>
        {
            var found = new List<SearchResult>();
            foreach (var module in mods.Take(5))
            {
                var data = _driver.ReadMemory(pid, module.BaseAddress,
                                               Math.Min(module.Size, 1048576u));
                if (data == null) continue;

                PatchBpBytesForDisasm(data, module.BaseAddress);
                var instrs = _disasm.Disassemble(data, module.BaseAddress, 10000);
                foreach (var instr in instrs)
                {
                    if (instr.Mnemonic.Equals("call", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrEmpty(instr.Operands))
                    {
                        if (TryParseAddress(instr.Operands, out ulong target))
                        {
                            var targetMod = mods.FirstOrDefault(m =>
                                target >= m.BaseAddress && target < m.BaseAddress + m.Size);
                            if (targetMod != null && targetMod.Name != module.Name)
                            {
                                found.Add(new SearchResult
                                {
                                    Address = instr.Address,
                                    ModuleName = module.Name,
                                    Preview = $"call {targetMod.Name}+{target - targetMod.BaseAddress:X}",
                                    Is32Bit = Is32Bit
                                });
                            }
                        }
                    }
                    if (found.Count >= 1000) break;
                }
            }
            return found;
        });

        SearchResults.ReplaceAll(results);
        Log($"Intermodular calls: found {SearchResults.Count} results");
    }

    private static List<byte?> ParseSearchPattern(string pattern)
    {
        var result = new List<byte?>();
        var parts = pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (part is "??" or "?")
                result.Add(null);
            else if (byte.TryParse(part, System.Globalization.NumberStyles.HexNumber, null, out var b))
                result.Add(b);
        }
        return result;
    }

    private static bool TryParseAddress(string operands, out ulong address)
    {
        address = 0;
        string s = operands.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ulong.TryParse(s[2..], System.Globalization.NumberStyles.HexNumber, null, out address);
        return ulong.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out address);
    }

    private void RefreshDisasmAnnotations()
    {
        var instrs = Instructions.ToList();
        AnnotateInstructionsWithSymbols(instrs);
        foreach (var instr in instrs)
            instr.HasBreakpoint = Breakpoints.Any(b => b.Address == instr.Address);
        Instructions.ReplaceAll(instrs);
    }

    /// <summary>
    /// Annotate disassembly instructions with symbol comments for call/jmp targets
    /// and for the instruction's own address (function start).
    /// </summary>
    private void AnnotateInstructionsWithSymbols(List<Instruction> instrs)
    {
        var moduleList = Modules.ToList();
        var pid = TargetPid;

        // Build IAT lookup: IatAddress → "module!Function"
        Dictionary<ulong, (string sym, ulong resolved)>? iatLookup = null;
        if (Imports.Count > 0)
        {
            iatLookup = new Dictionary<ulong, (string, ulong)>();
            foreach (var imp in Imports)
            {
                var name = !string.IsNullOrEmpty(imp.Function) ? imp.Function : $"#{imp.Ordinal}";
                iatLookup[imp.IatAddress] = ($"{imp.Module}!{name}", imp.ResolvedAddress);
            }
        }

        foreach (var instr in instrs)
        {
            // Resolve the instruction's own address to show function name
            var addrSym = _symbols.ResolveViaDbgHelp(instr.Address);
            if (addrSym != null && !addrSym.Contains("+0x"))
            {
                // Exact function start — show as label in address column
                instr.AddressLabel = addrSym;
                instr.Comment = addrSym;
            }

            // For call/jmp, resolve the target operand address
            if (!string.IsNullOrEmpty(instr.Operands) && IsBranchMnemonic(instr.Mnemonic))
            {
                if (TryParseAddress(instr.Operands, out ulong target))
                {
                    instr.BranchTargetAddress = target;
                    var sym = _symbols.ResolveAddress(pid, target, moduleList);
                    if (sym != null)
                    {
                        instr.BranchTargetSymbol = sym;
                        instr.Comment = sym;
                    }
                }
                // Resolve indirect RIP-relative calls/jmps: call [rip + 0x...]
                else if (iatLookup != null && TryParseRipRelative(instr, out ulong iatAddr))
                {
                    if (iatLookup.TryGetValue(iatAddr, out var imp))
                    {
                        instr.BranchTargetAddress = imp.resolved;
                        instr.BranchTargetSymbol = imp.sym;
                        instr.Comment = imp.sym;
                    }
                }
                // Resolve 32-bit absolute indirect: call/jmp dword ptr [0xADDRESS]
                else if (iatLookup != null && TryParseAbsoluteIndirect(instr.Operands, out ulong absIatAddr))
                {
                    if (iatLookup.TryGetValue(absIatAddr, out var imp))
                    {
                        instr.BranchTargetAddress = imp.resolved;
                        instr.BranchTargetSymbol = imp.sym;
                        instr.Comment = imp.sym;
                    }
                }
            }

            // Plugin address annotations — override/append to symbol comments
            if (_addressAnnotations.TryGetValue(instr.Address, out var annotation))
            {
                instr.Comment = string.IsNullOrEmpty(instr.Comment)
                    ? annotation
                    : $"{instr.Comment} | {annotation}";
            }
        }
    }

    /// <summary>
    /// Parse "qword ptr [rip + 0xNNNN]" / "[rip - 0xNNNN]" and compute effective address.
    /// Effective address = instruction address + instruction size + signed offset.
    /// </summary>
    private static bool TryParseRipRelative(Instruction instr, out ulong effectiveAddr)
    {
        effectiveAddr = 0;
        var ops = instr.Operands;
        // Find [rip + 0x...] or [rip - 0x...]
        int ripIdx = ops.IndexOf("rip", StringComparison.OrdinalIgnoreCase);
        if (ripIdx < 0) return false;

        int bracketStart = ops.LastIndexOf('[', ripIdx);
        int bracketEnd = ops.IndexOf(']', ripIdx);
        if (bracketStart < 0 || bracketEnd < 0) return false;

        var inner = ops[(ripIdx + 3)..bracketEnd].Trim();
        if (inner.Length < 2) return false;

        char sign = inner[0];
        if (sign != '+' && sign != '-') return false;

        var hexStr = inner[1..].Trim().TrimStart('0');
        if (hexStr.StartsWith("x", StringComparison.OrdinalIgnoreCase))
            hexStr = hexStr[1..];
        if (hexStr.Length == 0) hexStr = "0";

        if (!ulong.TryParse(hexStr, System.Globalization.NumberStyles.HexNumber, null, out ulong offset))
            return false;

        if (sign == '+')
            effectiveAddr = instr.Address + (ulong)instr.Size + offset;
        else
            effectiveAddr = instr.Address + (ulong)instr.Size - offset;

        return true;
    }

    /// <summary>
    /// Parse 32-bit absolute indirect operand: "dword ptr [0xADDRESS]"
    /// Used for IAT calls in 32-bit code: call dword ptr [0xd71440]
    /// </summary>
    private static bool TryParseAbsoluteIndirect(string operands, out ulong address)
    {
        address = 0;
        int bracketStart = operands.IndexOf('[');
        int bracketEnd = operands.IndexOf(']');
        if (bracketStart < 0 || bracketEnd <= bracketStart) return false;

        var inner = operands[(bracketStart + 1)..bracketEnd].Trim();

        // Must be a plain hex address (no register involved)
        // e.g. "0xd71440" — reject "ebx + 0x10", "rip + 0x..."
        if (inner.Any(c => char.IsLetter(c) && c != 'x' && c != 'X'
                        && !((c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))))
            return false;

        if (inner.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ulong.TryParse(inner[2..], System.Globalization.NumberStyles.HexNumber, null, out address);
        return ulong.TryParse(inner, System.Globalization.NumberStyles.HexNumber, null, out address);
    }

    private static bool IsBranchMnemonic(string mnemonic) => mnemonic switch
    {
        "call" or "jmp" or "je" or "jne" or "jz" or "jnz" or
        "jg" or "jge" or "jl" or "jle" or "ja" or "jae" or
        "jb" or "jbe" or "jo" or "jno" or "js" or "jns" or
        "jp" or "jnp" or "jcxz" or "jecxz" or "jrcxz" or
        "loop" or "loope" or "loopne" => true,
        _ => false,
    };

    private static string TruncateString(string s, int maxLen)
    {
        var sb = new StringBuilder();
        foreach (char c in s)
        {
            if (c >= 0x20 && c < 0x7F) sb.Append(c);
            else break;
        }
        string clean = sb.ToString();
        return clean.Length > maxLen ? clean[..maxLen] + "..." : clean;
    }

    /* ================================================================== */
    /*  Bookmarks                                                          */
    /* ================================================================== */

    [RelayCommand]
    private void AddBookmark()
    {
        ulong addr = SelectedDisasmAddress != 0 ? SelectedDisasmAddress : DisasmAddress;
        if (addr == 0) return;

        string label = PromptInput("Add Bookmark", $"Label for {addr:X16}:");
        if (string.IsNullOrWhiteSpace(label)) label = $"Bookmark_{Bookmarks.Count + 1}";

        if (Bookmarks.Any(b => b.Address == addr))
        {
            Log($"Bookmark already exists at {addr:X16}");
            return;
        }

        Bookmarks.Add(new Bookmark
        {
            Address = addr,
            Label = label,
            ModuleName = _symbols.ResolveAddress(TargetPid, addr, Modules.ToList())
        });
        Log($"Bookmark added: {label} at {addr:X16}");
    }

    [RelayCommand]
    private void RemoveBookmark(Bookmark? bookmark)
    {
        if (bookmark != null)
        {
            Bookmarks.Remove(bookmark);
            Log($"Bookmark removed: {bookmark.Label}");
        }
    }

    [RelayCommand]
    private void GoToBookmark(Bookmark? bookmark)
    {
        if (bookmark != null)
        {
            PushDisasmHistory();
            DisasmAddress = bookmark.Address;
            RefreshDisassembly();
            Log($"Go to bookmark: {bookmark.Label}");
        }
    }

    /* ================================================================== */
    /*  Inline assembler / patching                                        */
    /* ================================================================== */

    [RelayCommand]
    private async Task AssembleAtCursor()
    {
        try
        {
            if (!IsConnected || TargetPid == 0) { Log("Assemble: not connected"); return; }
            ulong addr = SelectedDisasmAddress != 0 ? SelectedDisasmAddress : DisasmAddress;
            if (addr == 0) { Log("Assemble: no address selected"); return; }

            var instr = Instructions.FirstOrDefault(i => i.Address == addr);
            if (instr == null) { Log($"Assemble: instruction not found at {FormatAddr(addr)}"); return; }

            var dlg = new AssembleDialog(instr, Is32Bit) { Owner = Application.Current.MainWindow };
            if (dlg.ShowDialog() != true || dlg.ResultBytes == null) return;

            var pid = TargetPid;
            var newBytes = dlg.ResultBytes;

            var origBytes = await Task.Run(() => _driver.ReadMemory(pid, addr, (uint)newBytes.Length));
            if (origBytes == null) { Log("Assemble: failed to read original bytes"); return; }

            var ok = await Task.Run(() => _driver.WriteMemory(pid, addr, newBytes));
            if (ok)
            {
                TrackPatch(addr, origBytes, newBytes);
                Log($"Assembled at {FormatAddr(addr)}: {BitConverter.ToString(newBytes).Replace("-", " ")}");
                RefreshDisassembly();
            }
            else
                Log($"Assemble: WriteMemory failed at {FormatAddr(addr)}");
        }
        catch (Exception ex)
        {
            Log($"Assemble error: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task NopInstruction()
    {
        if (!IsConnected || TargetPid == 0) return;
        ulong addr = SelectedDisasmAddress != 0 ? SelectedDisasmAddress : DisasmAddress;
        if (addr == 0) return;

        var instr = Instructions.FirstOrDefault(i => i.Address == addr);
        if (instr == null) return;

        var pid = TargetPid;
        int size = instr.Size;

        var origBytes = await Task.Run(() => _driver.ReadMemory(pid, addr, (uint)size));
        if (origBytes == null) return;

        var nops = new byte[size];
        Array.Fill(nops, (byte)0x90);

        var ok = await Task.Run(() => _driver.WriteMemory(pid, addr, nops));
        if (ok)
        {
            TrackPatch(addr, origBytes, nops);
            Log($"NOP'd {size} byte(s) at {FormatAddr(addr)}");
            RefreshDisassembly();
        }
    }

    [RelayCommand]
    private async Task FillWithNops()
    {
        if (!IsConnected || TargetPid == 0) return;
        ulong addr = SelectedDisasmAddress != 0 ? SelectedDisasmAddress : DisasmAddress;
        if (addr == 0) return;

        var dlg = new InputDialog("Fill with NOPs", "Byte count to fill with NOPs:")
        { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;

        if (!int.TryParse(dlg.InputText.Trim(), out int count) || count <= 0 || count > 4096)
        {
            Log("Fill NOPs: invalid size (1..4096)");
            return;
        }

        var pid = TargetPid;
        var origBytes = await Task.Run(() => _driver.ReadMemory(pid, addr, (uint)count));
        if (origBytes == null) return;

        var nops = new byte[count];
        Array.Fill(nops, (byte)0x90);

        var ok = await Task.Run(() => _driver.WriteMemory(pid, addr, nops));
        if (ok)
        {
            TrackPatch(addr, origBytes, nops);
            Log($"Filled {count} NOP(s) at {FormatAddr(addr)}");
            RefreshDisassembly();
        }
    }

    /* ================================================================== */
    /*  Patches tracking                                                   */
    /* ================================================================== */

    public void TrackPatch(ulong address, byte[] originalBytes, byte[] patchedBytes)
    {
        Patches.Add(new Patch
        {
            Address = address,
            OriginalBytes = originalBytes,
            PatchedBytes = patchedBytes,
            ModuleName = _symbols.ResolveAddress(TargetPid, address, Modules.ToList())
        });
        Log($"Patch tracked at {address:X16} ({patchedBytes.Length} bytes)");
    }

    [RelayCommand]
    private async Task RestorePatch(Patch? patch)
    {
        if (patch == null || !IsConnected || TargetPid == 0) return;
        var pid = TargetPid;

        var ok = await Task.Run(() => _driver.WriteMemory(pid, patch.Address, patch.OriginalBytes));
        if (ok)
        {
            Patches.Remove(patch);
            Log($"Restored original bytes at {patch.AddressHex}");
            RefreshDisassembly();
        }
    }

    [RelayCommand]
    private async Task RestoreAllPatches()
    {
        var pid = TargetPid;
        var patchList = Patches.ToList();

        await Task.Run(() =>
        {
            foreach (var patch in patchList)
                _driver.WriteMemory(pid, patch.Address, patch.OriginalBytes);
        });

        Patches.Clear();
        Log("Restored all patches");
        RefreshDisassembly();
    }

    /* ================================================================== */
    /*  Thread management                                                  */
    /* ================================================================== */

    [RelayCommand]
    private async Task SuspendThread(uint tid)
    {
        var ok = await Task.Run(() => _driver.SuspendThread(tid));
        if (ok) Log($"Suspended TID {tid}");
    }

    [RelayCommand]
    private async Task ResumeThread(uint tid)
    {
        var ok = await Task.Run(() => _driver.ResumeThread(tid));
        if (ok) Log($"Resumed TID {tid}");
    }

    [RelayCommand]
    private void SwitchThread(uint tid)
    {
        SelectedThreadId = tid;
        RefreshRegisters();
        NavigateToRip();
        RefreshDisassembly();
        RefreshStack();
        RefreshCallStack();
        Log($"Switched to TID {tid}");
    }

    /* ================================================================== */
    /*  Debug Event Listener                                               */
    /* ================================================================== */

    private void StartDebugListener()
    {
        StopDebugListener();
        _listenerCts = new CancellationTokenSource();
        var ct = _listenerCts.Token;

        _listenerTask = Task.Run(() =>
        {
            int nullCount = 0;
            while (!ct.IsCancellationRequested)
            {
                var evt = _driver.WaitDebugEvent();
                if (evt == null)
                {
                    nullCount++;
                    Application.Current?.Dispatcher.InvokeAsync(() =>
                        Log($"DebugListener: WaitDebugEvent returned null (#{nullCount})"));
                    // Exit immediately on null — do NOT send another WAIT.
                    // Sending another WAIT after the IRP was cancelled creates a
                    // stale pending IRP that corrupts the DBG TCP stream.
                    return;
                }

                // Run plugin filters on THIS background thread — UI is not touched.
                var pluginEvt = new KernelFlirt.SDK.PluginDebugEvent
                {
                    Type = (KernelFlirt.SDK.PluginDebugEventType)(int)evt.Type,
                    ProcessId = evt.ProcessId,
                    ThreadId = evt.ThreadId,
                    Address = evt.Address,
                    IsKernelMode = evt.IsKernelMode,
                    ExceptionCode = evt.ExceptionCode,
                    FaultAddress = evt.FaultAddress,
                    AccessType = evt.AccessType
                };

                if (_pluginManager.RunDebugEventFilters(pluginEvt))
                {
                    // Plugin handled — continue process without touching UI thread.
                    // Use plugin's ContinueMode if set, otherwise default logic.
                    uint mode;
                    if (pluginEvt.ContinueMode != 0)
                        mode = pluginEvt.ContinueMode;
                    else if (evt.Type == DebugEventType.Breakpoint)
                        mode = DriverComm.CONTINUE_STEP_PAST;
                    else
                        mode = DriverComm.CONTINUE_RUN;
                    var contOk = _driver.ContinueDebugEvent(mode, pluginEvt.NewRip, pluginEvt.NewRsp,
                        pluginEvt.TraceRangeBase, pluginEvt.TraceRangeEnd, pluginEvt.TraceMaxSteps);

                    if (mode == DriverComm.CONTINUE_TRACE)
                    {
                        Application.Current?.Dispatcher.InvokeAsync(() =>
                            Log($"[TraceStart] ContinueDebugEvent(mode=4) ok={contOk} " +
                                $"newRip=0x{pluginEvt.NewRip:X} newRsp=0x{pluginEvt.NewRsp:X} " +
                                $"range=[0x{pluginEvt.TraceRangeBase:X}..0x{pluginEvt.TraceRangeEnd:X}) " +
                                $"maxSteps={pluginEvt.TraceMaxSteps}"));
                    }

                    // Trace diagnostics: poll stats while trace is active
                    if (mode == DriverComm.CONTINUE_TRACE)
                    {
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(300);
                            for (int poll = 0; poll < 60; poll++) // up to 30s
                            {
                                var stats = _driver.GetHookStats();
                                if (stats == null) break;
                                var s = stats.Value;
                                Application.Current?.Dispatcher.InvokeAsync(() =>
                                {
                                    Log($"[TraceDiag] steps={s.traceSteps} active={s.traceActive} " +
                                        $"AV={s.traceAvCount} INT3={s.traceInt3Count} UNK={s.traceUnkCount} " +
                                        $"lastExc=0x{s.traceLastExcCode:X} lastAddr=0x{s.traceLastExcAddr:X} " +
                                        $"blocked={s.threadBlocked} mode={s.continueMode} " +
                                        $"irql=0x{s.diagIrql:X} waitCnt={s.diagWaitCount} reportCnt={s.diagReportCount} " +
                                        $"targetCalls={s.targetCalls} lastTgtAddr=0x{s.lastTargetAddr:X} lastTgtCode=0x{s.lastTargetCode:X}");
                                });
                                if (s.traceActive == 0) break;
                                await Task.Delay(500);
                            }
                        });
                    }

                    continue; // Loop back to WaitDebugEvent
                }

                // Not handled by plugin — dispatch to UI thread
                Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    OnDebugEvent(evt);
                });
                return; // Stop listener, UI takes over
            }
        }, ct);
    }

    private void StopDebugListener()
    {
        _listenerCts?.Cancel();

        // Force-interrupt the DBG channel TCP read so the listener unblocks.
        _driver.InterruptDbgChannel();

        if (_listenerTask != null)
        {
            try { _listenerTask.Wait(3000); } catch { /* timeout or cancelled — ok */ }
        }
        _listenerCts?.Dispose();
        _listenerCts = null;
        _listenerTask = null;

        _driver.ResetDbgTimeout();
    }

    private async void OnDebugEvent(DebugEvent evt)
    {
        // Plugin filters already ran on the listener thread.
        // If we're here, the event was NOT handled by any plugin.
        TargetPid = evt.ProcessId;
        SelectedThreadId = evt.ThreadId;
        IsBreakState = true;
        IsRunning = false;

        // Clean up temp breakpoint if hit
        if (_tempBpHandle.HasValue)
        {
            var tmpH = _tempBpHandle.Value;
            await Task.Run(() => _driver.RemoveBreakpoint(tmpH));
            Breakpoints.Remove(Breakpoints.FirstOrDefault(
                b => b.Handle == tmpH)!);
            _tempBpHandle = null;
        }

        // Driver already adjusts RIP back to BP address for INT3.
        // Match BP at evt.Address (driver-adjusted) or evt.Address-1 (fallback).
        var hitBp = Breakpoints.FirstOrDefault(b => b.Address == evt.Address)
                 ?? Breakpoints.FirstOrDefault(b => b.Address == evt.Address - 1);
        _hitSwBp = hitBp?.Type == BreakpointType.Software ? hitBp : null;

        if (evt.Type == DebugEventType.AccessViolation)
        {
            Log($"ACCESS VIOLATION at {evt.Address:X16} → target address 0x{evt.FaultAddress:X} (PID={evt.ProcessId} TID={evt.ThreadId})");
            if (evt.FaultAddress < 0x10000)
                Log("Anti-debug protection triggered! The protector detected the debugger and crashed intentionally. Apply anti-debug patches and restart the process.");
            else
                Log($"Access violation: exception code 0x{evt.ExceptionCode:X8}, fault address 0x{evt.FaultAddress:X16}");
            StatusText = $"Access Violation at {evt.Address:X16} → 0x{evt.FaultAddress:X}";
        }
        else
        {
            Log($"Break at {evt.Address:X16} (PID={evt.ProcessId} TID={evt.ThreadId})");
        }
        DisasmAddress = evt.Address;

        // Use registers from debug event (captured at exception time) when available.
        // ReadRegisters reads KTRAP_FRAME which is invalid for kernel threads
        // blocked in KeWaitForSingleObject.
        var pid = TargetPid;
        var tid = SelectedThreadId;
        List<Register> regs;
        if (evt.Registers != null)
        {
            var r = evt.Registers;
            if (Is32Bit)
            {
                regs = new List<Register>
                {
                    new() { Name = "EAX", Value = (uint)r.Rax, Is32Bit = true },
                    new() { Name = "EBX", Value = (uint)r.Rbx, Is32Bit = true },
                    new() { Name = "ECX", Value = (uint)r.Rcx, Is32Bit = true },
                    new() { Name = "EDX", Value = (uint)r.Rdx, Is32Bit = true },
                    new() { Name = "ESI", Value = (uint)r.Rsi, Is32Bit = true },
                    new() { Name = "EDI", Value = (uint)r.Rdi, Is32Bit = true },
                    new() { Name = "EBP", Value = (uint)r.Rbp, Is32Bit = true },
                    new() { Name = "ESP", Value = (uint)r.Rsp, Is32Bit = true },
                    new() { Name = "EIP", Value = (uint)r.Rip, Is32Bit = true },
                    new() { Name = "EFLAGS", Value = (uint)r.Rflags, Is32Bit = true },
                };
            }
            else
            {
                regs = new List<Register>
                {
                    new() { Name = "RAX", Value = r.Rax },
                    new() { Name = "RBX", Value = r.Rbx },
                    new() { Name = "RCX", Value = r.Rcx },
                    new() { Name = "RDX", Value = r.Rdx },
                    new() { Name = "RSI", Value = r.Rsi },
                    new() { Name = "RDI", Value = r.Rdi },
                    new() { Name = "RBP", Value = r.Rbp },
                    new() { Name = "RSP", Value = r.Rsp },
                    new() { Name = "R8",  Value = r.R8 },
                    new() { Name = "R9",  Value = r.R9 },
                    new() { Name = "R10", Value = r.R10 },
                    new() { Name = "R11", Value = r.R11 },
                    new() { Name = "R12", Value = r.R12 },
                    new() { Name = "R13", Value = r.R13 },
                    new() { Name = "R14", Value = r.R14 },
                    new() { Name = "R15", Value = r.R15 },
                    new() { Name = "RIP", Value = r.Rip },
                    new() { Name = "RFLAGS", Value = r.Rflags },
                };
            }
            regs.AddRange(Register.ExpandFlags(r.Rflags));
        }
        else
        {
            regs = await Task.Run(() => _driver.ReadRegisters(pid, tid, Is32Bit));
        }
        var oldRegs = Registers.ToDictionary(r => r.Name, r => r.Value);
        foreach (var reg in regs)
        {
            if (oldRegs.TryGetValue(reg.Name, out var prev))
                reg.PreviousValue = prev;
        }
        Registers.ReplaceAll(regs);

        NavigateToRip();
        RefreshDisassembly();
        RefreshStack();
        RefreshCallStack();

        if (hitBp != null)
        {
            hitBp.HitCount++;
            if (hitBp.IsConditional && !EvaluateCondition(hitBp.Condition!))
            {
                if (!string.IsNullOrEmpty(hitBp.LogExpression))
                    Log($"[Log BP] {hitBp.AddressHex}: {EvaluateLogExpression(hitBp.LogExpression)}");
                _ = Run();
                return;
            }
            if (!string.IsNullOrEmpty(hitBp.LogExpression))
                Log($"[Log BP] {hitBp.AddressHex}: {EvaluateLogExpression(hitBp.LogExpression)}");
        }

        StatusText = $"Break at {DisasmAddress:X16}";
    }

    /* ================================================================== */
    /*  Refresh methods                                                    */
    /* ================================================================== */

    /// <summary>
    /// Replace 0xCC bytes at SW breakpoint addresses with the original byte
    /// so the disassembly shows the real instruction instead of INT3.
    /// </summary>
    private void PatchBpBytesForDisasm(byte[] data, ulong baseAddr)
    {
        foreach (var bp in Breakpoints)
        {
            if (bp.Type == BreakpointType.Software && bp.OriginalByte != 0
                && bp.Address >= baseAddr && bp.Address < baseAddr + (ulong)data.Length)
            {
                var offset = (int)(bp.Address - baseAddr);
                if (data[offset] == 0xCC)
                    data[offset] = bp.OriginalByte;
            }
        }
    }

    public async void RefreshDisassembly()
    {
        if (!IsConnected || TargetPid == 0) return;
        var addr = DisasmAddress;
        var pid = TargetPid;
        var data = await Task.Run(() => _driver.ReadMemory(pid, addr, 8192));
        if (data == null) return;

        PatchBpBytesForDisasm(data, addr);
        var instrs = _disasm.Disassemble(data, addr, 512);
        AnnotateInstructionsWithSymbols(instrs);
        foreach (var instr in instrs)
            instr.HasBreakpoint = Breakpoints.Any(b => b.Address == instr.Address);
        Instructions.ReplaceAll(instrs);
        SyncBreakpointMarkers();
        _disasmLoadingMore = false;
    }

    // ── Dynamic disassembly loading ─────────────────────────────────────
    private bool _disasmLoadingMore;
    private const int DisasmMaxInstructions = 2000;

    /// <summary>Fired when new instructions should be appended to the disasm view.</summary>
    public event Action<List<Instruction>, int>? DisasmAppend;   // (newInstrs, trimTopCount)
    /// <summary>Fired when new instructions should be prepended to the disasm view.</summary>
    public event Action<List<Instruction>, int>? DisasmPrepend;  // (newInstrs, trimBottomCount)

    public async void DisassembleMoreDown()
    {
        if (_disasmLoadingMore || !IsConnected || TargetPid == 0) return;
        if (Instructions.Count == 0) return;
        _disasmLoadingMore = true;

        var lastInstr = Instructions[Instructions.Count - 1];
        ulong nextAddr = lastInstr.Address + (ulong)lastInstr.Size;
        var pid = TargetPid;

        var data = await Task.Run(() => _driver.ReadMemory(pid, nextAddr, 1024));
        if (data == null || data.Length == 0) { _disasmLoadingMore = false; return; }

        PatchBpBytesForDisasm(data, nextAddr);
        var newInstrs = _disasm.Disassemble(data, nextAddr, 64);
        if (newInstrs.Count == 0) { _disasmLoadingMore = false; return; }

        AnnotateInstructionsWithSymbols(newInstrs);
        foreach (var instr in newInstrs)
            instr.HasBreakpoint = Breakpoints.Any(b => b.Address == instr.Address);

        // Add to model without triggering full rebuild
        foreach (var instr in newInstrs)
            Instructions.AddSilent(instr);

        int trimTop = 0;
        if (Instructions.Count > DisasmMaxInstructions)
        {
            trimTop = Instructions.Count - DisasmMaxInstructions;
            Instructions.RemoveRangeSilent(0, trimTop);
            if (Instructions.Count > 0)
                DisasmAddress = Instructions[0].Address;
        }

        DisasmAppend?.Invoke(newInstrs, trimTop);
        _disasmLoadingMore = false;
    }

    public async void DisassembleMoreUp()
    {
        if (_disasmLoadingMore || !IsConnected || TargetPid == 0) return;
        if (Instructions.Count == 0) return;
        _disasmLoadingMore = true;

        ulong firstAddr = Instructions[0].Address;
        ulong readSize = 1024;
        ulong readAddr = firstAddr > readSize ? firstAddr - readSize : 0;
        if (readAddr == 0) { _disasmLoadingMore = false; return; }

        var pid = TargetPid;
        var data = await Task.Run(() => _driver.ReadMemory(pid, readAddr, (uint)readSize));
        if (data == null || data.Length == 0) { _disasmLoadingMore = false; return; }

        PatchBpBytesForDisasm(data, readAddr);
        var allInstrs = _disasm.Disassemble(data, readAddr, 128);
        var prepend = allInstrs.Where(i => i.Address < firstAddr).ToList();
        // Take only last 64 to avoid too many at once
        if (prepend.Count > 64)
            prepend = prepend.Skip(prepend.Count - 64).ToList();
        if (prepend.Count == 0) { _disasmLoadingMore = false; return; }

        AnnotateInstructionsWithSymbols(prepend);
        foreach (var instr in prepend)
            instr.HasBreakpoint = Breakpoints.Any(b => b.Address == instr.Address);

        Instructions.InsertRangeSilent(0, prepend);

        int trimBottom = 0;
        if (Instructions.Count > DisasmMaxInstructions)
        {
            trimBottom = Instructions.Count - DisasmMaxInstructions;
            Instructions.RemoveRangeSilent(Instructions.Count - trimBottom, trimBottom);
        }

        if (Instructions.Count > 0)
            DisasmAddress = Instructions[0].Address;

        DisasmPrepend?.Invoke(prepend, trimBottom);
        _disasmLoadingMore = false;
    }

    /// <summary>
    /// Syncs HasBreakpoint flag on Imports, Functions, and FilteredFunctions
    /// so that DataGrid rows with breakpoints are highlighted.
    /// </summary>
    public void SyncBreakpointMarkers()
    {
        var bpAddrs = new HashSet<ulong>(Breakpoints.Select(b => b.Address));
        foreach (var imp in _allImports)
            imp.HasBreakpoint = bpAddrs.Contains(imp.ResolvedAddress);
        foreach (var imp in FilteredImports)
            imp.HasBreakpoint = bpAddrs.Contains(imp.ResolvedAddress);
        foreach (var fn in _allFunctions)
            fn.HasBreakpoint = bpAddrs.Contains(fn.Address);
        foreach (var fn in FilteredFunctions)
            fn.HasBreakpoint = bpAddrs.Contains(fn.Address);
        foreach (var sr in SearchResults)
            sr.HasBreakpoint = bpAddrs.Contains(sr.Address);
        foreach (var ex in _allExceptions)
            ex.HasBreakpoint = bpAddrs.Contains(ex.FunctionStart);
        foreach (var ex in FilteredExceptions)
            ex.HasBreakpoint = bpAddrs.Contains(ex.FunctionStart);
        foreach (var sec in _allSections)
            sec.HasBreakpoint = bpAddrs.Contains(sec.VirtualAddress);
        foreach (var sec in FilteredSections)
            sec.HasBreakpoint = bpAddrs.Contains(sec.VirtualAddress);
        foreach (var str in _allStrings)
            str.HasBreakpoint = bpAddrs.Contains(str.Address);
        foreach (var str in FilteredStrings)
            str.HasBreakpoint = bpAddrs.Contains(str.Address);
        BreakpointMarkersChanged?.Invoke();
    }

    /// <summary>Raised when BP markers are synced — UI should refresh DataGrids.</summary>
    public event Action? BreakpointMarkersChanged;

    public async void RefreshRegisters()
    {
        if (!IsConnected || TargetPid == 0 || SelectedThreadId == 0) return;

        var pid = TargetPid;
        var tid = SelectedThreadId;
        var regs = await Task.Run(() => _driver.ReadRegisters(pid, tid, Is32Bit));

        var oldRegs = Registers.ToDictionary(r => r.Name, r => r.Value);
        foreach (var reg in regs)
        {
            if (oldRegs.TryGetValue(reg.Name, out var prev))
                reg.PreviousValue = prev;
        }
        Registers.ReplaceAll(regs);
    }

    public async Task RefreshModulesAndSectionsAsync()
    {
        await RefreshModulesAsync();
        RefreshSections();
    }

    public async void RefreshModules() => await RefreshModulesAsync();

    public async Task RefreshModulesAsync()
    {
        if (!IsConnected || TargetPid == 0) return;
        var pid = TargetPid;
        var mods = await Task.Run(() => _driver.EnumModules(pid));
        if (Is32Bit)
            foreach (var m in mods) m.Is32Bit = true;

        // Preserve modules from the old list that aren't in the new one
        // (protectors may unlink the exe from PEB LDR list as anti-debug)
        foreach (var old in Modules)
        {
            if (!mods.Any(m => m.BaseAddress == old.BaseAddress))
            {
                mods.Add(old);
                Log($"  Kept unlisted module '{old.Name}' at 0x{old.BaseAddress:X}");
            }
        }

        Modules.ReplaceAll(mods);
        Log($"Found {mods.Count} modules");
    }

    [RelayCommand]
    private async Task RefreshFunctionsAsync()
    {
        if (!_symbols.IsInitialized) return;

        // Find module containing current RIP
        var rip = DisasmAddress;
        ulong targetBase = 0;
        string targetName = "";

        // Check user modules first
        var userMod = Modules.FirstOrDefault(m =>
            rip >= m.BaseAddress && rip < m.BaseAddress + m.Size);
        if (userMod != null) { targetBase = userMod.BaseAddress; targetName = userMod.Name; }

        // Fall back to kernel modules
        if (targetBase == 0)
        {
            var kernMod = KernelModules.FirstOrDefault(m =>
                rip >= m.BaseAddress && rip < m.BaseAddress + m.Size);
            if (kernMod != null) { targetBase = kernMod.BaseAddress; targetName = kernMod.Name; }
        }

        // Fall back to main exe
        if (targetBase == 0)
        {
            var exeMod = Modules.FirstOrDefault(m =>
                m.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            if (exeMod != null) { targetBase = exeMod.BaseAddress; targetName = exeMod.Name; }
        }

        if (targetBase == 0) return;

        Log($"Enumerating functions from {targetName}...");
        var funcs = await Task.Run(() => _symbols.EnumFunctions(targetBase));
        _allFunctions = funcs
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(f => new FunctionEntry { Name = f.Name, Address = f.Address, Size = f.Size })
            .ToList();
        Log($"Found {_allFunctions.Count} functions in {targetName}");
        ApplyFunctionFilter();
    }

    public async void RefreshFunctionsForModule(ulong moduleBase, string moduleName)
    {
        if (!_symbols.IsInitialized) return;
        if (moduleBase == 0) return;

        Log($"Enumerating functions from {moduleName}...");
        var funcs = await Task.Run(() => _symbols.EnumFunctions(moduleBase));
        _allFunctions = funcs
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(f => new FunctionEntry { Name = f.Name, Address = f.Address, Size = f.Size })
            .ToList();
        Log($"Found {_allFunctions.Count} functions in {moduleName}");
        ApplyFunctionFilter();
    }

    partial void OnIsBreakStateChanged(bool value)
    {
        if (value) _pluginManager.NotifyBreakStateEntered();
        else _pluginManager.NotifyBreakStateExited();
    }

    partial void OnImportFilterChanged(string value) => ApplyImportFilter();

    private void ApplyImportFilter()
    {
        if (string.IsNullOrWhiteSpace(ImportFilter))
        {
            FilteredImports.ReplaceAll(_allImports);
        }
        else
        {
            var filter = ImportFilter;
            var filtered = _allImports
                .Where(i => i.Function.Contains(filter, StringComparison.OrdinalIgnoreCase)
                         || i.Module.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();
            FilteredImports.ReplaceAll(filtered);
        }
    }

    partial void OnExportFilterChanged(string value) => ApplyExportFilter();

    private void ApplyExportFilter()
    {
        if (string.IsNullOrWhiteSpace(ExportFilter))
        {
            FilteredExports.ReplaceAll(_allExports);
        }
        else
        {
            var filter = ExportFilter;
            var filtered = _allExports
                .Where(e => e.Function.Contains(filter, StringComparison.OrdinalIgnoreCase)
                         || e.Module.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();
            FilteredExports.ReplaceAll(filtered);
        }
    }

    partial void OnFunctionFilterChanged(string value) => ApplyFunctionFilter();

    private void ApplyFunctionFilter()
    {
        if (string.IsNullOrWhiteSpace(FunctionFilter))
        {
            FilteredFunctions.ReplaceAll(_allFunctions);
        }
        else
        {
            var filter = FunctionFilter;
            var filtered = _allFunctions
                .Where(f => f.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();
            FilteredFunctions.ReplaceAll(filtered);
        }
    }

    partial void OnExceptionFilterChanged(string value) => ApplyExceptionFilter();

    private void ApplyExceptionFilter()
    {
        if (string.IsNullOrWhiteSpace(ExceptionFilter))
        {
            FilteredExceptions.ReplaceAll(_allExceptions);
        }
        else
        {
            var filter = ExceptionFilter;
            var filtered = _allExceptions
                .Where(e => (e.Symbol ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase)
                         || e.ModuleName.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();
            FilteredExceptions.ReplaceAll(filtered);
        }
    }

    private void AddModuleSections(string moduleName, IReadOnlyList<KernelFlirt.SDK.PluginSectionInfo> sections)
    {
        // Build section entries from plugin data
        var pluginEntries = new List<SectionEntry>();
        foreach (var s in sections)
        {
            pluginEntries.Add(new SectionEntry
            {
                ModuleName = moduleName,
                Name = s.Name,
                VirtualAddress = s.VirtualAddress,
                VirtualSize = s.VirtualSize,
                RawDataOffset = 0,
                RawDataSize = s.VirtualSize,
                Characteristics = s.Characteristics,
                Is32Bit = Is32Bit
            });
        }

        // Store for re-application after RefreshSections
        _pluginSections[moduleName] = pluginEntries;

        // Apply now
        _allSections.RemoveAll(s => s.ModuleName == moduleName);
        int idx = _allSections.Count > 0 ? _allSections.Max(s => s.Index) + 1 : 0;
        foreach (var e in pluginEntries)
            e.Index = idx++;
        _allSections.AddRange(pluginEntries);
        ApplySectionFilter();
        Log($"Sections: plugin provided {sections.Count} sections for '{moduleName}'");
    }

    partial void OnSectionFilterChanged(string value) => ApplySectionFilter();

    private void ApplySectionFilter()
    {
        if (string.IsNullOrWhiteSpace(SectionFilter))
        {
            FilteredSections.ReplaceAll(_allSections);
        }
        else
        {
            var filter = SectionFilter;
            var filtered = _allSections
                .Where(s => s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                         || s.ModuleName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                         || s.Flags.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();
            FilteredSections.ReplaceAll(filtered);
        }
    }

    /// <summary>
    /// Parses IMAGE_SECTION_HEADER entries from all loaded modules.
    /// </summary>
    public async void RefreshSections()
    {
        if (!IsConnected || TargetPid == 0) return;

        var pid = TargetPid;
        var mods = Modules.ToList();
        var kmods = KernelModules.ToList();

        var entries = new List<SectionEntry>();
        int idx = 0;

        foreach (var mod in mods)
        {
            // Only need headers — read first 4KB
            var header = await Task.Run(() => _driver.ReadMemory(pid, mod.BaseAddress,
                Math.Min(mod.Size, 4096u)));
            if (header == null || header.Length < 0x40) continue;

            var parsed = ParseSectionsFromBuffer(header, mod.BaseAddress, mod.Name, ref idx);
            entries.AddRange(parsed);
        }

        foreach (var kmod in kmods)
        {
            var header = await Task.Run(() => _driver.ReadMemory(4, kmod.BaseAddress,
                Math.Min(kmod.Size, 4096u)));
            if (header == null || header.Length < 0x40) continue;

            var parsed = ParseSectionsFromBuffer(header, kmod.BaseAddress, kmod.Name, ref idx);
            entries.AddRange(parsed);
        }

        // Re-apply plugin-provided sections (survive refresh)
        foreach (var kv in _pluginSections)
        {
            entries.RemoveAll(s => s.ModuleName == kv.Key);
            foreach (var e in kv.Value)
                e.Index = idx++;
            entries.AddRange(kv.Value);
        }

        _allSections = entries;
        ApplySectionFilter();
        Log($"Sections: {entries.Count} sections from {mods.Count + kmods.Count} modules");
    }

    private List<SectionEntry> ParseSectionsFromBuffer(byte[] image, ulong modBase, string modName, ref int idx)
    {
        var result = new List<SectionEntry>();
        try
        {
            // Try MZ first; if zeroed (anti-dump), fall back to e_lfanew → PE check
            bool hasMz = image[0] == 'M' && image[1] == 'Z';
            uint peOffset = 0;
            bool hasPe = false;

            if (hasMz)
            {
                peOffset = BitConverter.ToUInt32(image, 0x3C);
                if (peOffset + 0x18 <= image.Length &&
                    image[peOffset] == 'P' && image[peOffset + 1] == 'E')
                    hasPe = true;
            }

            // MZ zeroed but e_lfanew might still point to valid PE (packer anti-dump)
            if (!hasPe && image.Length >= 0x44)
            {
                peOffset = BitConverter.ToUInt32(image, 0x3C);
                if (peOffset >= 0x40 && peOffset < 0x400 && peOffset + 0x18 <= image.Length &&
                    image[peOffset] == 'P' && image[peOffset + 1] == 'E')
                    hasPe = true;
            }

            if (!hasPe)
            {
                // No PE header at all — create a synthetic section.
                var mod = Modules.FirstOrDefault(m => m.BaseAddress == modBase);
                uint size = mod?.Size ?? (uint)image.Length;
                result.Add(new SectionEntry
                {
                    Index = idx++,
                    ModuleName = modName,
                    Name = "[mapped]",
                    VirtualAddress = modBase,
                    VirtualSize = size,
                    RawDataOffset = 0,
                    RawDataSize = size,
                    Characteristics = 0x60000020, // CODE | X | R
                    Is32Bit = Is32Bit
                });
                return result;
            }

            ushort magic = BitConverter.ToUInt16(image, (int)peOffset + 0x18);
            // Size of optional header
            ushort sizeOfOptional = BitConverter.ToUInt16(image, (int)peOffset + 0x14);
            ushort numberOfSections = BitConverter.ToUInt16(image, (int)peOffset + 0x06);

            // Section headers start right after optional header
            // PE signature (4) + COFF header (20) + optional header
            int sectionStart = (int)peOffset + 4 + 20 + sizeOfOptional;

            for (int i = 0; i < numberOfSections; i++)
            {
                int off = sectionStart + i * 40; // IMAGE_SECTION_HEADER is 40 bytes
                if (off + 40 > image.Length) break;

                // Name: 8 bytes, null-terminated ASCII
                string name = System.Text.Encoding.ASCII.GetString(image, off, 8).TrimEnd('\0');
                uint virtualSize = BitConverter.ToUInt32(image, off + 8);
                uint virtualRva = BitConverter.ToUInt32(image, off + 12);
                uint rawSize = BitConverter.ToUInt32(image, off + 16);
                uint rawOffset = BitConverter.ToUInt32(image, off + 20);
                uint characteristics = BitConverter.ToUInt32(image, off + 36);

                result.Add(new SectionEntry
                {
                    Index = idx++,
                    ModuleName = modName,
                    Name = name,
                    VirtualAddress = modBase + virtualRva,
                    VirtualSize = virtualSize,
                    RawDataOffset = rawOffset,
                    RawDataSize = rawSize,
                    Characteristics = characteristics,
                    Is32Bit = Is32Bit
                });
            }
        }
        catch { }
        return result;
    }

    /* ================================================================== */
    /*  Strings                                                            */
    /* ================================================================== */

    partial void OnStringFilterChanged(string value) => ApplyStringFilter();

    private void ApplyStringFilter()
    {
        if (string.IsNullOrWhiteSpace(StringFilter))
        {
            FilteredStrings.ReplaceAll(_allStrings);
        }
        else
        {
            var filter = StringFilter;
            var filtered = _allStrings
                .Where(s => s.Value.Contains(filter, StringComparison.OrdinalIgnoreCase)
                         || s.ModuleName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                         || s.SectionName.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();
            FilteredStrings.ReplaceAll(filtered);
        }
    }

    /// <summary>
    /// Extract printable strings from all loaded user-mode modules.
    /// Scans .rdata, .data, and any readable section for ASCII and Unicode strings.
    /// </summary>
    public async void RefreshStrings()
    {
        if (!IsConnected || TargetPid == 0) return;

        var pid = TargetPid;
        var mods = Modules.ToList();
        var entries = new List<StringEntry>();
        int idx = 0;

        foreach (var mod in mods)
        {
            // Read PE header to find section table
            var header = await Task.Run(() => _driver.ReadMemory(pid, mod.BaseAddress,
                Math.Min(mod.Size, 4096u)));
            if (header == null || header.Length < 0x40) continue;
            if (header[0] != 'M' || header[1] != 'Z') continue;

            uint peOffset = BitConverter.ToUInt32(header, 0x3C);
            if (peOffset + 0x18 > header.Length) continue;
            if (header[peOffset] != 'P' || header[peOffset + 1] != 'E') continue;

            ushort numSections = BitConverter.ToUInt16(header, (int)peOffset + 6);
            ushort optHdrSize = BitConverter.ToUInt16(header, (int)peOffset + 20);
            uint sectTableOff = peOffset + 24u + optHdrSize;

            if (sectTableOff + numSections * 40 > header.Length) continue;

            for (int s = 0; s < numSections; s++)
            {
                int off = (int)(sectTableOff + s * 40);
                string sectName = Encoding.ASCII.GetString(header, off, 8).TrimEnd('\0');
                uint vaddr = BitConverter.ToUInt32(header, off + 12);
                uint vsize = BitConverter.ToUInt32(header, off + 8);
                uint chars = BitConverter.ToUInt32(header, off + 36);

                // Only scan readable sections (skip code-only sections)
                if ((chars & 0x40000000) == 0) continue; // not readable
                if (vsize == 0 || vsize > 0x400000) continue; // skip empty or huge

                var data = await Task.Run(() => _driver.ReadMemory(pid, mod.BaseAddress + vaddr, vsize));
                if (data == null) continue;

                // Extract ASCII strings (min length 4)
                ExtractAsciiStrings(data, mod.BaseAddress + vaddr, mod.Name, sectName, ref idx, entries);
                // Extract Unicode strings (min length 4)
                ExtractUnicodeStrings(data, mod.BaseAddress + vaddr, mod.Name, sectName, ref idx, entries);
            }
        }

        _allStrings = entries;
        ApplyStringFilter();
        Log($"Strings: {entries.Count} strings from {mods.Count} modules");
    }

    private void ExtractAsciiStrings(byte[] data, ulong baseAddr, string modName, string sectName,
        ref int idx, List<StringEntry> results)
    {
        int start = -1;
        for (int i = 0; i <= data.Length; i++)
        {
            bool printable = i < data.Length && data[i] >= 0x20 && data[i] < 0x7F;
            if (printable)
            {
                if (start < 0) start = i;
            }
            else
            {
                if (start >= 0)
                {
                    int len = i - start;
                    // Require null terminator and minimum length
                    if (len >= 4 && i < data.Length && data[i] == 0)
                    {
                        string val = Encoding.ASCII.GetString(data, start, len);
                        results.Add(new StringEntry
                        {
                            Index = idx++,
                            ModuleName = modName,
                            SectionName = sectName,
                            Address = baseAddr + (ulong)start,
                            Value = val,
                            Type = StringType.ASCII,
                            Length = len,
                            Is32Bit = Is32Bit
                        });
                    }
                    start = -1;
                }
            }
        }
    }

    private void ExtractUnicodeStrings(byte[] data, ulong baseAddr, string modName, string sectName,
        ref int idx, List<StringEntry> results)
    {
        int start = -1;
        for (int i = 0; i <= data.Length - 1; i += 2)
        {
            bool printable = i + 1 < data.Length && data[i] >= 0x20 && data[i] < 0x7F && data[i + 1] == 0;
            if (printable)
            {
                if (start < 0) start = i;
            }
            else
            {
                if (start >= 0)
                {
                    int charCount = (i - start) / 2;
                    // Require null terminator and minimum length
                    if (charCount >= 4 && i + 1 < data.Length && data[i] == 0 && data[i + 1] == 0)
                    {
                        string val = Encoding.Unicode.GetString(data, start, i - start);
                        // Skip if this looks like it was already captured as ASCII
                        if (!results.Any(r => r.Address == baseAddr + (ulong)start && r.Type == StringType.ASCII))
                        {
                            results.Add(new StringEntry
                            {
                                Index = idx++,
                                ModuleName = modName,
                                SectionName = sectName,
                                Address = baseAddr + (ulong)start,
                                Value = val,
                                Type = StringType.Unicode,
                                Length = charCount,
                                Is32Bit = Is32Bit
                            });
                        }
                    }
                    start = -1;
                }
            }
        }
    }

    /// <summary>
    /// Add a dynamically unpacked PE as a virtual module.
    /// Reads PE header, adds to Modules, refreshes all views.
    /// </summary>
    public void AddUnpackedModule(ulong peBase, string name)
    {
        if (!IsConnected || TargetPid == 0) return;

        var pid = TargetPid;
        uint sizeOfImage = 0;

        // Try reading PE header
        var dosHeader = _driver.ReadMemory(pid, peBase, 0x40);
        bool hasPeHeader = dosHeader != null && dosHeader.Length >= 0x40 &&
                           dosHeader[0] == 'M' && dosHeader[1] == 'Z';

        if (hasPeHeader)
        {
            uint lfanew = BitConverter.ToUInt32(dosHeader!, 0x3C);
            var peHeader = _driver.ReadMemory(pid, peBase + lfanew, 0x78);
            if (peHeader != null && peHeader.Length >= 0x58)
            {
                sizeOfImage = BitConverter.ToUInt32(peHeader, 24 + 56);
            }
        }
        else
        {
            // No PE header — scan pages to find committed memory regions.
            Log($"No PE header at 0x{peBase:X} — scanning committed pages...");

            // Scan in 64KB blocks, then refine boundaries with 4KB pages
            ulong firstReadable = 0;
            ulong lastReadable = 0;

            // Coarse scan: 64KB steps
            for (uint off = 0; off < 0x1000000; off += 0x10000)
            {
                var probe = _driver.ReadMemory(pid, peBase + off, 1);
                if (probe != null && probe.Length > 0)
                {
                    if (firstReadable == 0) firstReadable = peBase + off;
                    lastReadable = peBase + off + 0x10000;
                }
                else if (lastReadable != 0 && off > (lastReadable - peBase) + 0x40000)
                {
                    break; // 4 consecutive empty 64KB blocks after data — stop
                }
            }

            if (firstReadable != 0)
            {
                peBase = firstReadable;
                sizeOfImage = (uint)(lastReadable - firstReadable);
                Log($"  Committed range: 0x{firstReadable:X} – 0x{lastReadable:X} (0x{sizeOfImage:X} bytes)");
            }
        }

        if (sizeOfImage == 0) sizeOfImage = 0x100000; // fallback 1MB

        // Check if already in the module list
        bool exists = Modules.Any(m => m.BaseAddress == peBase);
        if (!exists)
        {
            Modules.Add(new ModuleInfo
            {
                BaseAddress = peBase,
                Size = sizeOfImage,
                Name = name,
                Is32Bit = Is32Bit
            });
        }

        Log($"Added unpacked module: {name} at 0x{peBase:X} (size 0x{sizeOfImage:X})");

        // Try to load symbols (may find PDB via debug directory if protector didn't strip it)
        if (_symbols.LoadModule(TargetPid, name, peBase, sizeOfImage))
            Log($"Symbols loaded for {name}");

        Log("Refreshing imports, sections, strings, functions...");

        // Refresh all views — imports specifically from the unpacked PE
        RefreshImports(peBase);
        RefreshSections();
        RefreshStrings();
        RefreshExceptions();
        RefreshRegisters(); // show updated RIP after WriteRip
    }

    /// <summary>
    /// Set RIP/EIP to a new address (e.g., jump to unpacked OEP).
    /// </summary>
    public void SetInstructionPointer(ulong newAddress)
    {
        if (!IsConnected || !IsBreakState || TargetPid == 0) return;

        var tid = SelectedThreadId;
        if (tid == 0) { Log("No thread selected"); return; }

        bool ok = _driver.WriteRip(TargetPid, tid, newAddress);
        if (ok)
        {
            Log($"RIP set to 0x{newAddress:X}");
            // Refresh registers to show the new value
            RefreshRegisters();
        }
        else
        {
            Log($"Failed to set RIP to 0x{newAddress:X}");
        }
    }

    /// <summary>
    /// Dump section contents to a file.
    /// </summary>
    public async void DumpSectionToFile(SectionEntry sec)
    {
        if (!IsConnected || TargetPid == 0) return;

        uint size = sec.VirtualSize > 0 ? sec.VirtualSize : sec.RawDataSize;
        if (size == 0) { Log("Section size is 0"); return; }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"{sec.ModuleName}_{sec.Name}.bin",
            Filter = "Binary files (*.bin)|*.bin|All files (*.*)|*.*",
            Title = $"Dump {sec.ModuleName}:{sec.Name}"
        };

        if (dlg.ShowDialog() != true) return;

        var pid = TargetPid;
        Log($"Dumping {sec.ModuleName}:{sec.Name} ({size} bytes) to {dlg.FileName}...");

        var data = await Task.Run(() => _driver.ReadMemory(pid, sec.VirtualAddress,
            Math.Min(size, 16777216u))); // cap at 16MB

        if (data == null || data.Length == 0)
        {
            Log("Failed to read section memory");
            return;
        }

        await System.IO.File.WriteAllBytesAsync(dlg.FileName, data);
        Log($"Dumped {data.Length} bytes to {dlg.FileName}");
    }

    /// <summary>
    /// Fill entire section with a specific byte value.
    /// </summary>
    public async void FillSection(SectionEntry sec, byte fillByte)
    {
        if (!IsConnected || TargetPid == 0) return;

        uint size = sec.VirtualSize > 0 ? sec.VirtualSize : sec.RawDataSize;
        if (size == 0) { Log("Section size is 0"); return; }

        var pid = TargetPid;
        Log($"Filling {sec.ModuleName}:{sec.Name} with 0x{fillByte:X2} ({size} bytes)...");

        // Write in 64KB chunks
        const uint chunkSize = 65536;
        uint written = 0;
        bool ok = true;

        await Task.Run(() =>
        {
            while (written < size && ok)
            {
                uint len = Math.Min(chunkSize, size - written);
                var chunk = new byte[len];
                if (fillByte != 0) Array.Fill(chunk, fillByte);

                ok = _driver.WriteMemory(pid, sec.VirtualAddress + written, chunk);
                written += len;
            }
        });

        if (ok)
            Log($"Filled {sec.ModuleName}:{sec.Name} with 0x{fillByte:X2} ({written} bytes)");
        else
            Log($"Fill failed after {written} bytes");
    }

    /// <summary>
    /// Search for binary pattern within a specific section.
    /// </summary>
    public async void SearchBinaryInSection(SectionEntry sec)
    {
        if (!IsConnected || TargetPid == 0) return;

        string pattern = PromptInput("Binary Search in Section",
            $"Search in {sec.ModuleName}:{sec.Name}\nEnter hex bytes (e.g. 48 89 5C 24 or 488B??):");
        if (string.IsNullOrWhiteSpace(pattern)) return;

        var patternBytes = ParseSearchPattern(pattern);
        if (patternBytes.Count == 0) return;

        uint size = sec.VirtualSize > 0 ? sec.VirtualSize : sec.RawDataSize;
        if (size == 0) { Log("Section size is 0"); return; }

        var pid = TargetPid;
        var secName = $"{sec.ModuleName}:{sec.Name}";

        var results = await Task.Run(() =>
        {
            var found = new List<SearchResult>();
            var data = _driver.ReadMemory(pid, sec.VirtualAddress, Math.Min(size, 16777216u));
            if (data == null) return found;

            for (int i = 0; i <= data.Length - patternBytes.Count; i++)
            {
                bool match = true;
                for (int j = 0; j < patternBytes.Count; j++)
                {
                    if (patternBytes[j] is { } expected && data[i + j] != expected)
                    { match = false; break; }
                }
                if (match)
                {
                    found.Add(new SearchResult
                    {
                        Address = sec.VirtualAddress + (ulong)i,
                        ModuleName = secName,
                        Preview = BitConverter.ToString(data, i, Math.Min(16, data.Length - i)).Replace("-", " "),
                        Is32Bit = Is32Bit
                    });
                    if (found.Count >= 1000) break;
                }
            }
            return found;
        });

        SearchResults.ReplaceAll(results);
        Log($"Binary search in {secName}: found {results.Count} results for [{pattern}]");
    }

    /// <summary>
    /// Search for ASCII/Unicode string within a specific section.
    /// </summary>
    public async void SearchStringInSection(SectionEntry sec)
    {
        if (!IsConnected || TargetPid == 0) return;

        string text = PromptInput("String Search in Section",
            $"Search in {sec.ModuleName}:{sec.Name}\nEnter string to find:");
        if (string.IsNullOrWhiteSpace(text)) return;

        uint size = sec.VirtualSize > 0 ? sec.VirtualSize : sec.RawDataSize;
        if (size == 0) { Log("Section size is 0"); return; }

        var pid = TargetPid;
        var secName = $"{sec.ModuleName}:{sec.Name}";
        byte[] asciiPattern = Encoding.ASCII.GetBytes(text);
        byte[] unicodePattern = Encoding.Unicode.GetBytes(text);
        var is32 = Is32Bit;

        var results = await Task.Run(() =>
        {
            var found = new List<SearchResult>();
            var data = _driver.ReadMemory(pid, sec.VirtualAddress, Math.Min(size, 16777216u));
            if (data == null) return found;

            SearchInDataBg(found, data, asciiPattern, sec.VirtualAddress, secName, "ASCII", is32);
            SearchInDataBg(found, data, unicodePattern, sec.VirtualAddress, secName, "Unicode", is32);
            return found;
        });

        SearchResults.ReplaceAll(results);
        Log($"String search in {secName}: found {results.Count} results for \"{text}\"");
    }

    /// <summary>
    /// Parses .pdata (RUNTIME_FUNCTION) from all loaded modules.
    /// Works for both user-mode PE and kernel-mode SYS.
    /// </summary>
    public async void RefreshExceptions()
    {
        if (!IsConnected || TargetPid == 0) return;

        var pid = TargetPid;
        var mods = Modules.ToList();
        var kmods = KernelModules.ToList();

        var entries = new List<ExceptionEntry>();
        int idx = 0;

        // Parse user-mode modules
        foreach (var mod in mods)
        {
            var image = await Task.Run(() => _driver.ReadMemory(pid, mod.BaseAddress,
                Math.Min(mod.Size, 4194304u)));
            if (image == null || image.Length < 0x40) continue;

            var parsed = ParsePdataFromBuffer(image, mod.BaseAddress, mod.Name, ref idx, pid, mods);
            entries.AddRange(parsed);
        }

        // Parse kernel modules
        foreach (var kmod in kmods)
        {
            var image = await Task.Run(() => _driver.ReadMemory(4, kmod.BaseAddress,
                Math.Min(kmod.Size, 4194304u)));
            if (image == null || image.Length < 0x40) continue;

            var parsed = ParsePdataFromKernelBuffer(image, kmod.BaseAddress, kmod.Name, ref idx);
            entries.AddRange(parsed);
        }

        _allExceptions = entries;
        ApplyExceptionFilter();
        Log($"Exception handlers: {entries.Count} RUNTIME_FUNCTION entries from {mods.Count + kmods.Count} modules");
    }

    private List<ExceptionEntry> ParsePdataFromBuffer(byte[] image, ulong modBase, string modName,
        ref int idx, uint pid, List<ModuleInfo> mods)
    {
        var result = new List<ExceptionEntry>();
        try
        {
            if (image[0] != 'M' || image[1] != 'Z') return result;
            uint peOffset = BitConverter.ToUInt32(image, 0x3C);
            if (peOffset + 0x18 > image.Length) return result;
            if (image[peOffset] != 'P' || image[peOffset + 1] != 'E') return result;

            ushort magic = BitConverter.ToUInt16(image, (int)peOffset + 0x18);
            bool is64 = magic == 0x20B;
            if (!is64) return result; // .pdata only for x64

            // Exception directory is entry #3 in Data Directory
            // x64 optional header starts at PE+0x18, data dirs at PE+0x18+0x70 = PE+0x88
            // Entry #3 offset: PE+0x88 + 3*8 = PE+0xA0
            int exceptDirOffset = (int)peOffset + 0x88 + 3 * 8;
            if (exceptDirOffset + 8 > image.Length) return result;

            uint exceptRva = BitConverter.ToUInt32(image, exceptDirOffset);
            uint exceptSize = BitConverter.ToUInt32(image, exceptDirOffset + 4);
            if (exceptRva == 0 || exceptSize == 0) return result;
            if (exceptRva + exceptSize > image.Length) return result;

            int entryCount = (int)(exceptSize / 12); // RUNTIME_FUNCTION is 12 bytes
            for (int i = 0; i < entryCount; i++)
            {
                int off = (int)exceptRva + i * 12;
                if (off + 12 > image.Length) break;

                uint beginRva = BitConverter.ToUInt32(image, off);
                uint endRva = BitConverter.ToUInt32(image, off + 4);
                uint unwindRva = BitConverter.ToUInt32(image, off + 8);

                ulong funcStart = modBase + beginRva;
                ulong funcEnd = modBase + endRva;

                var entry = new ExceptionEntry
                {
                    Index = idx++,
                    ModuleName = modName,
                    FunctionStart = funcStart,
                    FunctionEnd = funcEnd,
                    UnwindInfoAddr = modBase + unwindRva,
                    Symbol = _symbols.ResolveAddress(pid, funcStart, mods)
                };
                result.Add(entry);
            }
        }
        catch { }
        return result;
    }

    private List<ExceptionEntry> ParsePdataFromKernelBuffer(byte[] image, ulong modBase, string modName, ref int idx)
    {
        var result = new List<ExceptionEntry>();
        try
        {
            if (image[0] != 'M' || image[1] != 'Z') return result;
            uint peOffset = BitConverter.ToUInt32(image, 0x3C);
            if (peOffset + 0x18 > image.Length) return result;
            if (image[peOffset] != 'P' || image[peOffset + 1] != 'E') return result;

            ushort magic = BitConverter.ToUInt16(image, (int)peOffset + 0x18);
            if (magic != 0x20B) return result;

            int exceptDirOffset = (int)peOffset + 0x88 + 3 * 8;
            if (exceptDirOffset + 8 > image.Length) return result;

            uint exceptRva = BitConverter.ToUInt32(image, exceptDirOffset);
            uint exceptSize = BitConverter.ToUInt32(image, exceptDirOffset + 4);
            if (exceptRva == 0 || exceptSize == 0) return result;
            if (exceptRva + exceptSize > image.Length) return result;

            int entryCount = (int)(exceptSize / 12);
            for (int i = 0; i < entryCount; i++)
            {
                int off = (int)exceptRva + i * 12;
                if (off + 12 > image.Length) break;

                uint beginRva = BitConverter.ToUInt32(image, off);
                uint endRva = BitConverter.ToUInt32(image, off + 4);
                uint unwindRva = BitConverter.ToUInt32(image, off + 8);

                result.Add(new ExceptionEntry
                {
                    Index = idx++,
                    ModuleName = modName,
                    FunctionStart = modBase + beginRva,
                    FunctionEnd = modBase + endRva,
                    UnwindInfoAddr = modBase + unwindRva,
                    Symbol = $"{modName}+0x{beginRva:X}"
                });
            }
        }
        catch { }
        return result;
    }

    public async void RefreshImports(ulong moduleBase = 0, uint overridePid = 0)
    {
        if (!IsConnected) return;

        uint moduleSize = 0;
        uint effectivePid = overridePid != 0 ? overridePid : TargetPid;
        if (effectivePid == 0) return;

        if (moduleBase == 0)
        {
            // Try user-mode modules first, then kernel modules
            var mainMod = Modules.FirstOrDefault();
            if (mainMod != null)
            {
                moduleBase = mainMod.BaseAddress;
                moduleSize = mainMod.Size;
            }
            else
            {
                // For driver debugging: find the module containing current RIP
                var rip = DisasmAddress;
                var kernMod = KernelModules.FirstOrDefault(m =>
                    rip >= m.BaseAddress && rip < m.BaseAddress + m.Size);
                if (kernMod == null) return;
                moduleBase = kernMod.BaseAddress;
                moduleSize = kernMod.Size;
                effectivePid = 4;
            }
        }
        else
        {
            var mod = Modules.FirstOrDefault(m => m.BaseAddress == moduleBase);
            if (mod != null)
                moduleSize = mod.Size;
            else
            {
                var kernMod = KernelModules.FirstOrDefault(m => m.BaseAddress == moduleBase);
                if (kernMod != null)
                {
                    moduleSize = kernMod.Size;
                    if (effectivePid != 4) effectivePid = 4;
                }
            }
        }

        if (moduleSize == 0) moduleSize = 2 * 1024 * 1024;
        // Cap at 4MB to avoid huge reads
        uint readSize = Math.Min(moduleSize, 4 * 1024 * 1024);

        var pid = effectivePid;
        var modBase = moduleBase;

        // Single large read — all PE parsing from local buffer, zero extra network calls
        var image = await Task.Run(() => _driver.ReadMemory(pid, modBase, readSize));
        if (image == null || image.Length < 0x40)
        {
            Log("Import parse: failed to read module image");
            return;
        }

        var entries = await Task.Run(() => ParseImportsFromBuffer(image, modBase));

        _allImports = entries;
        Imports.ReplaceAll(entries);
        ApplyImportFilter();
        Log($"Found {entries.Count} imports");
        Log("Process loaded");
        StatusText = $"Process loaded - PID {TargetPid}";

        // Re-annotate existing disassembly with IAT symbols (no memory read)
        if (entries.Count > 0 && Instructions.Count > 0)
        {
            var instrs = Instructions.ToList();
            AnnotateInstructionsWithSymbols(instrs);
            foreach (var instr in instrs)
                instr.HasBreakpoint = Breakpoints.Any(b => b.Address == instr.Address);
            Instructions.ReplaceAll(instrs);
        }
    }

    private List<ImportEntry> ParseImportsFromBuffer(byte[] image, ulong modBase)
    {
        var result = new List<ImportEntry>();
        try
        {
            uint peOffset = BitConverter.ToUInt32(image, 0x3C);
            if (peOffset + 0x18 > image.Length) return result;
            if (image[peOffset] != 'P' || image[peOffset + 1] != 'E') return result;

            ushort magic = BitConverter.ToUInt16(image, (int)peOffset + 0x18);
            bool is64 = magic == 0x20B;
            int importDirOffset = is64 ? 0x90 : 0x80;

            if (peOffset + importDirOffset + 8 > image.Length) return result;

            uint importRva = BitConverter.ToUInt32(image, (int)peOffset + importDirOffset);
            uint importSize = BitConverter.ToUInt32(image, (int)peOffset + importDirOffset + 4);

            // If import directory is zeroed (common in packed PEs), try to find it by scanning
            if (importRva == 0 || importSize == 0)
            {
                (importRva, importSize) = FindImportDirectoryByScan(image, peOffset, is64);
                if (importRva == 0) return result;
            }
            if (importRva + importSize > image.Length) return result;

            int descriptorSize = 20;
            int count = (int)Math.Min(importSize / descriptorSize, 256);
            int entrySize = is64 ? 8 : 4;

            for (int i = 0; i < count; i++)
            {
                int off = (int)importRva + i * descriptorSize;
                if (off + descriptorSize > image.Length) break;

                uint iltRva = BitConverter.ToUInt32(image, off);
                uint nameRva = BitConverter.ToUInt32(image, off + 12);
                uint iatRva = BitConverter.ToUInt32(image, off + 16);
                if (nameRva == 0) break;

                // Read DLL name from buffer
                if (nameRva >= image.Length) continue;
                int nameEnd = Array.IndexOf(image, (byte)0, (int)nameRva);
                if (nameEnd < 0 || nameEnd > nameRva + 256) nameEnd = (int)Math.Min(nameRva + 256, image.Length);
                string dllName = Encoding.ASCII.GetString(image, (int)nameRva, nameEnd - (int)nameRva);

                uint thunkRva = iltRva != 0 ? iltRva : iatRva;
                if (thunkRva >= image.Length) continue;

                for (int j = 0; j < 2048; j++)
                {
                    int thunkOff = (int)thunkRva + j * entrySize;
                    if (thunkOff + entrySize > image.Length) break;

                    ulong thunkValue = is64
                        ? BitConverter.ToUInt64(image, thunkOff)
                        : BitConverter.ToUInt32(image, thunkOff);
                    if (thunkValue == 0) break;

                    var entry = new ImportEntry
                    {
                        Module = dllName,
                        IatAddress = modBase + iatRva + (ulong)(j * entrySize)
                    };

                    // Read resolved address from IAT in buffer
                    int iatOff = (int)iatRva + j * entrySize;
                    if (iatOff + entrySize <= image.Length)
                        entry.ResolvedAddress = is64
                            ? BitConverter.ToUInt64(image, iatOff)
                            : BitConverter.ToUInt32(image, iatOff);

                    bool byOrdinal = is64
                        ? (thunkValue & 0x8000000000000000UL) != 0
                        : (thunkValue & 0x80000000UL) != 0;

                    if (byOrdinal)
                    {
                        entry.Ordinal = (ushort)(thunkValue & 0xFFFF);
                    }
                    else
                    {
                        uint hintNameRva = (uint)(thunkValue & 0x7FFFFFFFUL);
                        if (hintNameRva + 3 <= image.Length)
                        {
                            int fnEnd = Array.IndexOf(image, (byte)0, (int)hintNameRva + 2);
                            if (fnEnd < 0 || fnEnd > hintNameRva + 258)
                                fnEnd = (int)Math.Min(hintNameRva + 258, image.Length);
                            if (fnEnd > hintNameRva + 2)
                                entry.Function = Encoding.ASCII.GetString(
                                    image, (int)hintNameRva + 2, fnEnd - (int)hintNameRva - 2);
                        }
                    }

                    result.Add(entry);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Import parse error: {ex.Message}");
        }
        return result;
    }

    /// <summary>
    /// Scans the PE image for IMAGE_IMPORT_DESCRIPTOR chain when the PE header's
    /// Import Directory RVA has been zeroed by a protector.
    /// Searches readable sections (.rdata, .idata, etc.) for valid descriptor chains.
    /// </summary>
    private (uint rva, uint size) FindImportDirectoryByScan(byte[] image, uint peOffset, bool is64)
    {
        try
        {
            ushort numberOfSections = BitConverter.ToUInt16(image, (int)peOffset + 0x06);
            ushort sizeOfOptional = BitConverter.ToUInt16(image, (int)peOffset + 0x14);
            int sectionStart = (int)peOffset + 4 + 20 + sizeOfOptional;

            // Collect candidate sections: .rdata, .idata, or any readable non-executable section
            // NOTE: image buffer is a memory dump (mapped PE), so sections are at their VirtualAddress,
            // NOT at RawDataOffset. Use RVA as buffer offset and VirtualSize as length.
            var candidates = new List<(uint rva, uint virtualSize, string name)>();
            for (int i = 0; i < numberOfSections; i++)
            {
                int off = sectionStart + i * 40;
                if (off + 40 > image.Length) break;
                string secName = Encoding.ASCII.GetString(image, off, 8).TrimEnd('\0');
                uint secVirtualSize = BitConverter.ToUInt32(image, off + 8);
                uint secRva = BitConverter.ToUInt32(image, off + 12);
                uint chars = BitConverter.ToUInt32(image, off + 36);

                // Prefer .rdata/.idata; also try any readable section
                bool isReadable = (chars & 0x40000000) != 0; // IMAGE_SCN_MEM_READ
                bool isCode = (chars & 0x20000000) != 0;     // IMAGE_SCN_MEM_EXECUTE
                if (secName == ".rdata" || secName == ".idata")
                    candidates.Insert(0, (secRva, secVirtualSize, secName)); // prioritize
                else if (isReadable && !isCode && secVirtualSize > 0)
                    candidates.Add((secRva, secVirtualSize, secName));
            }

            foreach (var (secRva, secVirtualSize, secName) in candidates)
            {
                if (secRva + secVirtualSize > image.Length) continue;

                // Scan for chains of valid IMAGE_IMPORT_DESCRIPTORs (20 bytes each)
                int limit = (int)Math.Min(secVirtualSize, 0x100000); // 1MB max scan
                for (int pos = 0; pos + 20 <= limit; pos += 4) // align to DWORD
                {
                    int absPos = (int)secRva + pos;
                    if (absPos + 20 > image.Length) break;

                    // Read first descriptor candidate
                    uint iltRva = BitConverter.ToUInt32(image, absPos);
                    uint nameRva = BitConverter.ToUInt32(image, absPos + 12);
                    uint iatRva = BitConverter.ToUInt32(image, absPos + 16);

                    if (nameRva == 0 || iatRva == 0) continue;
                    if (nameRva >= image.Length || iatRva >= image.Length) continue;

                    // Validate: nameRva should point to a DLL name
                    if (!IsValidDllName(image, nameRva)) continue;

                    // Count consecutive valid descriptors
                    int count = 0;
                    for (int d = 0; d < 256; d++)
                    {
                        int dOff = absPos + d * 20;
                        if (dOff + 20 > image.Length) break;

                        uint dNameRva = BitConverter.ToUInt32(image, dOff + 12);
                        if (dNameRva == 0) { count = d; break; } // null terminator
                        if (dNameRva >= image.Length) break;
                        if (!IsValidDllName(image, dNameRva)) break;

                        uint dIatRva = BitConverter.ToUInt32(image, dOff + 16);
                        if (dIatRva == 0 || dIatRva >= image.Length) break;
                    }

                    // Need at least 2 imports to be confident
                    if (count < 2) continue;

                    // Convert file offset back to RVA
                    uint foundRva = secRva + (uint)pos;
                    uint foundSize = (uint)((count + 1) * 20); // +1 for null terminator

                    Debug.WriteLine($"Found import directory by scan at RVA 0x{foundRva:X} in {secName}: {count} DLLs");
                    return (foundRva, foundSize);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"FindImportDirectoryByScan error: {ex.Message}");
        }

        return (0, 0);
    }

    /// <summary>
    /// Checks if the RVA points to a valid DLL name: ASCII printable, ends with .dll (case insensitive).
    /// </summary>
    private static bool IsValidDllName(byte[] image, uint rva)
    {
        if (rva + 5 >= image.Length) return false; // minimum "a.dll"

        int end = (int)rva;
        int maxLen = (int)Math.Min(rva + 260, image.Length);
        while (end < maxLen && image[end] != 0)
        {
            byte b = image[end];
            if (b < 0x20 || b > 0x7E) return false; // not printable ASCII
            end++;
        }

        int len = end - (int)rva;
        if (len < 5) return false; // minimum "a.dll"

        // Check .dll or .DLL suffix (case insensitive)
        string name = Encoding.ASCII.GetString(image, (int)rva, len);
        return name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
    }

    /* ================================================================== */
    /*  Exports                                                            */
    /* ================================================================== */

    public async void RefreshExports(ulong moduleBase = 0)
    {
        if (!IsConnected || TargetPid == 0) return;

        var pid = TargetPid;
        var allExports = new List<ExportEntry>();
        List<ModuleInfo> modulesToScan;

        if (moduleBase == 0)
        {
            // All loaded modules
            modulesToScan = Modules.ToList();
        }
        else
        {
            // Single module
            var mod = Modules.FirstOrDefault(m => m.BaseAddress == moduleBase);
            if (mod == null) return;
            modulesToScan = [mod];
        }

        foreach (var mod in modulesToScan)
        {
            uint modSize = mod.Size != 0 ? mod.Size : 2 * 1024 * 1024;
            uint readSize = Math.Min(modSize, 4 * 1024 * 1024);

            var modBase = mod.BaseAddress;
            var modName = mod.Name;
            var image = await Task.Run(() => _driver.ReadMemory(pid, modBase, readSize));
            if (image == null || image.Length < 0x40)
            {
                Log($"Export parse: {modName} read failed (requested {readSize}, got {image?.Length ?? 0})");
                continue;
            }

            var exports = ParseExportsFromBuffer(image, modBase, modName);
            if (exports.Count > 0)
                Log($"  {modName}: {exports.Count} exports");
            allExports.AddRange(exports);
        }

        _allExports = allExports;
        Exports.ReplaceAll(allExports);
        ApplyExportFilter();
        Log($"Found {allExports.Count} exports in {modulesToScan.Count} module(s)");
    }

    private List<ExportEntry> ParseExportsFromBuffer(byte[] image, ulong modBase, string moduleName)
    {
        var result = new List<ExportEntry>();
        try
        {
            if (image.Length < 0x40) return result;
            if (image[0] != 0x4D || image[1] != 0x5A) return result;

            uint peOffset = BitConverter.ToUInt32(image, 0x3C);
            if (peOffset + 0x18 > image.Length) return result;
            if (image[peOffset] != 'P' || image[peOffset + 1] != 'E') return result;

            ushort magic = BitConverter.ToUInt16(image, (int)peOffset + 0x18);
            bool is64 = magic == 0x20B;

            // Export directory is DataDirectory[0]
            int exportDirOffset = is64 ? (int)peOffset + 0x88 : (int)peOffset + 0x78;
            if (exportDirOffset + 8 > image.Length) return result;

            uint exportRva = BitConverter.ToUInt32(image, exportDirOffset);
            uint exportSize = BitConverter.ToUInt32(image, exportDirOffset + 4);
            Debug.WriteLine($"[ExportParse] {moduleName}: exportRva=0x{exportRva:X} size=0x{exportSize:X} imageLen=0x{image.Length:X}");
            if (exportRva == 0 || exportSize == 0) { Debug.WriteLine($"[ExportParse] {moduleName}: no export dir"); return result; }
            if (exportRva + 40 > (uint)image.Length) { Debug.WriteLine($"[ExportParse] {moduleName}: export dir beyond image (0x{exportRva:X}+40 > 0x{image.Length:X})"); return result; }

            uint numberOfFunctions = BitConverter.ToUInt32(image, (int)exportRva + 20);
            uint numberOfNames = BitConverter.ToUInt32(image, (int)exportRva + 24);
            uint addressTableRva = BitConverter.ToUInt32(image, (int)exportRva + 28);
            uint nameTableRva = BitConverter.ToUInt32(image, (int)exportRva + 32);
            uint ordinalTableRva = BitConverter.ToUInt32(image, (int)exportRva + 36);
            uint ordinalBase = BitConverter.ToUInt32(image, (int)exportRva + 16);

            Debug.WriteLine($"[ExportParse] {moduleName}: funcs={numberOfFunctions} names={numberOfNames} addrTbl=0x{addressTableRva:X} nameTbl=0x{nameTableRva:X}");

            if (numberOfFunctions == 0 || numberOfFunctions > 0x10000) { Debug.WriteLine($"[ExportParse] {moduleName}: bad numberOfFunctions"); return result; }
            if (addressTableRva == 0 || addressTableRva + numberOfFunctions * 4 > (uint)image.Length) { Debug.WriteLine($"[ExportParse] {moduleName}: address table beyond image (0x{addressTableRva:X}+{numberOfFunctions*4} > 0x{image.Length:X})"); return result; }

            // Build name→ordinal map
            var nameMap = new Dictionary<ushort, string>();
            if (numberOfNames > 0 &&
                nameTableRva + numberOfNames * 4 <= image.Length &&
                ordinalTableRva + numberOfNames * 2 <= image.Length)
            {
                for (uint i = 0; i < numberOfNames; i++)
                {
                    uint nameRva = BitConverter.ToUInt32(image, (int)(nameTableRva + i * 4));
                    ushort ordIndex = BitConverter.ToUInt16(image, (int)(ordinalTableRva + i * 2));

                    if (nameRva < image.Length)
                    {
                        int nameEnd = Array.IndexOf(image, (byte)0, (int)nameRva);
                        if (nameEnd < 0 || nameEnd > nameRva + 256)
                            nameEnd = (int)Math.Min(nameRva + 256, image.Length);
                        if (nameEnd > nameRva)
                        {
                            string name = Encoding.ASCII.GetString(image, (int)nameRva, nameEnd - (int)nameRva);
                            nameMap[ordIndex] = name;
                        }
                    }
                }
            }

            // Walk address table
            for (uint i = 0; i < numberOfFunctions; i++)
            {
                uint funcRva = BitConverter.ToUInt32(image, (int)(addressTableRva + i * 4));
                if (funcRva == 0) continue;

                // Skip forwarded exports (RVA inside export directory)
                if (funcRva >= exportRva && funcRva < exportRva + exportSize)
                    continue;

                var entry = new ExportEntry
                {
                    Module = moduleName,
                    Ordinal = (ushort)(i + ordinalBase),
                    Address = modBase + funcRva,
                };

                if (nameMap.TryGetValue((ushort)i, out var funcName))
                    entry.Function = funcName;

                result.Add(entry);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Export parse error: {ex.Message}");
        }
        return result;
    }

    public async void RefreshKernelModules()
    {
        if (!IsConnected) return;
        var mods = await Task.Run(() => _driver.EnumKernelModules());
        KernelModules.ReplaceAll(mods);
        Log($"Found {mods.Count} kernel modules");
    }

    public async void RefreshThreads()
    {
        if (!IsConnected || TargetPid == 0) return;
        var pid = TargetPid;
        var threads = await Task.Run(() => _driver.EnumThreads(pid));
        Threads.ReplaceAll(threads);
        Log($"Found {threads.Count} threads");
    }

    public async void RefreshStack()
    {
        if (!IsConnected || TargetPid == 0) return;
        var rsp = Registers.FirstOrDefault(r => r.Name == SpRegName);
        if (rsp == null) return;

        var pid = TargetPid;
        var rspVal = rsp.Value;
        var data = await Task.Run(() => _driver.ReadMemory(pid, rspVal, 256));
        if (data == null) return;

        var moduleList = Modules.ToList();
        var kmodList = KernelModules.ToList();
        var items = new List<StackEntry>();
        int sp = PointerSize;
        string spName = SpRegName;
        for (int i = 0; i < data.Length; i += sp)
        {
            if (i + sp > data.Length) break;
            ulong val = Is32Bit ? BitConverter.ToUInt32(data, i) : BitConverter.ToUInt64(data, i);
            var annotation = ResolveStackValue(pid, val, moduleList, kmodList);
            if (annotation == null && val != 0)
                annotation = await TryReadStringAtAsync(pid, val);
            items.Add(new StackEntry { Offset = $"{spName}+{i:X2}", Address = FormatAddr(val), Annotation = annotation });
        }
        StackEntries.ReplaceAll(items);
    }

    private string? ResolveStackValue(uint pid, ulong val, List<ModuleInfo> modules, List<KernelModuleInfo> kmodules)
    {
        if (val == 0) return null;
        // Check user-mode modules
        var mod = modules.FirstOrDefault(m => val >= m.BaseAddress && val < m.BaseAddress + m.Size);
        if (mod != null)
            return _symbols.ResolveAddress(pid, val, modules) ?? $"{mod.Name}+0x{val - mod.BaseAddress:X}";
        // Check kernel modules
        var kmod = kmodules.FirstOrDefault(m => val >= m.BaseAddress && val < m.BaseAddress + m.Size);
        if (kmod != null)
            return $"{kmod.Name}+0x{val - kmod.BaseAddress:X}";
        return null;
    }

    private async Task<string?> TryReadStringAtAsync(uint pid, ulong addr)
    {
        var buf = await Task.Run(() => _driver.ReadMemory(pid, addr, 128));
        if (buf == null || buf.Length < 2) return null;

        // Try Unicode (UTF-16LE) first
        var uniStr = TryExtractString(buf, unicode: true);
        if (uniStr != null && uniStr.Length >= 3)
            return $"\"{uniStr}\"";

        // Try ASCII
        var ascStr = TryExtractString(buf, unicode: false);
        if (ascStr != null && ascStr.Length >= 3)
            return $"\"{ascStr}\"";

        return null;
    }

    private static string? TryExtractString(byte[] buf, bool unicode)
    {
        var sb = new System.Text.StringBuilder();
        if (unicode)
        {
            for (int i = 0; i + 1 < buf.Length && sb.Length < 60; i += 2)
            {
                char c = (char)(buf[i] | (buf[i + 1] << 8));
                if (c == '\0') break;
                if (c < 0x20 || c > 0x7E) return null; // not printable ASCII range
                sb.Append(c);
            }
        }
        else
        {
            for (int i = 0; i < buf.Length && sb.Length < 60; i++)
            {
                byte b = buf[i];
                if (b == 0) break;
                if (b < 0x20 || b > 0x7E) return null;
                sb.Append((char)b);
            }
        }
        return sb.Length > 0 ? sb.ToString() : null;
    }

    public async void RefreshCallStack()
    {
        if (!IsConnected || TargetPid == 0) return;
        var rsp = Registers.FirstOrDefault(r => r.Name == SpRegName);
        var rip = Registers.FirstOrDefault(r => r.Name == IpRegName);
        if (rsp == null) return;

        var pid = TargetPid;
        var rspVal = rsp.Value;

        var csFrames = new List<CallStackFrame>();

        if (rip != null && rip.Value != 0)
        {
            csFrames.Add(new CallStackFrame
            {
                Index = 0,
                ReturnAddress = rip.Value,
                StackAddress = rspVal,
                ModuleName = _symbols.ResolveAddress(pid, rip.Value, Modules.ToList()),
                Is32Bit = Is32Bit
            });
        }

        var stackData = await Task.Run(() => _driver.ReadMemory(pid, rspVal, 2048));
        if (stackData == null) { CallStack.ReplaceAll(csFrames); return; }

        var moduleList = Modules.ToList();
        int frameIdx = 1;
        int ptrSize = PointerSize;
        for (int i = 0; i < stackData.Length && frameIdx < 50; i += ptrSize)
        {
            if (i + ptrSize > stackData.Length) break;
            ulong val = Is32Bit
                ? BitConverter.ToUInt32(stackData, i)
                : BitConverter.ToUInt64(stackData, i);
            if (val == 0) continue;

            // Check user-mode modules first, then kernel modules
            bool inModule = Modules.Any(m =>
                val >= m.BaseAddress && val < m.BaseAddress + m.Size)
                || KernelModules.Any(m =>
                val >= m.BaseAddress && val < m.BaseAddress + m.Size);
            if (inModule)
            {
                csFrames.Add(new CallStackFrame
                {
                    Index = frameIdx++,
                    ReturnAddress = val,
                    StackAddress = rspVal + (ulong)i,
                    ModuleName = _symbols.ResolveAddress(pid, val, moduleList) ?? $"0x{val:X}",
                    Is32Bit = Is32Bit
                });
            }
        }
        CallStack.ReplaceAll(csFrames);
    }

    public async void RefreshSehChain()
    {
        if (!IsConnected || TargetPid == 0) return;

        var rsp = Registers.FirstOrDefault(r => r.Name == SpRegName);
        if (rsp == null) return;

        var pid = TargetPid;
        var rspVal = rsp.Value;
        var mods = Modules.ToList();

        var entries = await Task.Run(() =>
        {
            var result = new List<SehEntry>();
            var data = _driver.ReadMemory(pid, rspVal, 4096);
            if (data == null) return result;

            int idx = 0;
            for (int i = 0; i < data.Length - 16 && idx < 20; i += 8)
            {
                ulong next = BitConverter.ToUInt64(data, i);
                ulong handler = BitConverter.ToUInt64(data, i + 8);

                if (handler == 0) continue;
                var handlerMod = mods.FirstOrDefault(m =>
                    handler >= m.BaseAddress && handler < m.BaseAddress + m.Size);
                if (handlerMod == null) continue;

                bool nextOnStack = (next >= rspVal && next < rspVal + 0x10000) ||
                                   next == ulong.MaxValue;
                if (!nextOnStack) continue;

                result.Add(new SehEntry
                {
                    Index = idx++,
                    HandlerAddress = handler,
                    NextRecord = next,
                    ModuleName = $"{handlerMod.Name}+0x{handler - handlerMod.BaseAddress:X}"
                });

                if (next == ulong.MaxValue) break;
            }
            return result;
        });

        SehChain.ReplaceAll(entries);
    }

    public async void RefreshHexDump()
    {
        if (!IsConnected || TargetPid == 0) return;
        var pid = TargetPid;
        var addr = HexAddress;
        var data = await Task.Run(() => _driver.ReadMemory(pid, addr, 4096));
        if (data != null) HexData = data;
    }

    private async Task RefreshAllViews()
    {
        var pid = TargetPid;
        var disasmAddr = DisasmAddress;
        var hexAddr = HexAddress != 0 ? HexAddress : disasmAddr;
        HexAddress = hexAddr;
        var rspReg = Registers.FirstOrDefault(r => r.Name == SpRegName);

        var disasmTask = Task.Run(() => _driver.ReadMemory(pid, disasmAddr, 4096));
        var stackTask = rspReg != null ? Task.Run(() => _driver.ReadMemory(pid, rspReg.Value, 256)) : Task.FromResult<byte[]?>(null);
        var hexTask = Task.Run(() => _driver.ReadMemory(pid, hexAddr, 4096));
        await Task.WhenAll(disasmTask, stackTask, hexTask);

        var disasmData = disasmTask.Result;
        if (disasmData != null)
        {
            PatchBpBytesForDisasm(disasmData, disasmAddr);
            var instrs = _disasm.Disassemble(disasmData, disasmAddr);
            AnnotateInstructionsWithSymbols(instrs);
            foreach (var instr in instrs)
                instr.HasBreakpoint = Breakpoints.Any(b => b.Address == instr.Address);
            Instructions.ReplaceAll(instrs);
        }

        var stackData = stackTask.Result;
        if (stackData != null && rspReg != null)
        {
            var stackItems = new List<StackEntry>();
            int sp = PointerSize;
            string spName = SpRegName;
            for (int i = 0; i < stackData.Length; i += sp)
            {
                if (i + sp > stackData.Length) break;
                ulong val = Is32Bit ? BitConverter.ToUInt32(stackData, i) : BitConverter.ToUInt64(stackData, i);
                stackItems.Add(new StackEntry { Offset = $"{spName}+{i:X2}", Address = FormatAddr(val) });
            }
            StackEntries.ReplaceAll(stackItems);
        }

        var hexData = hexTask.Result;
        if (hexData != null) HexData = hexData;
    }

    /* ================================================================== */
    /*  Helpers                                                             */
    /* ================================================================== */

    private Instruction? GetInstructionAtRip()
    {
        var rip = Registers.FirstOrDefault(r => r.Name == IpRegName);
        if (rip == null) return null;
        return Instructions.FirstOrDefault(i => i.Address == rip.Value);
    }

    private static bool IsCallInstruction(string mnemonic)
    {
        return mnemonic.Equals("call", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRetInstruction(string mnemonic)
    {
        var m = mnemonic.ToLowerInvariant();
        return m == "ret" || m == "retn" || m == "retf";
    }

    private static bool IsUnconditionalJmp(string mnemonic)
    {
        return mnemonic.Equals("jmp", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsConditionalJump(string mnemonic)
    {
        var m = mnemonic.ToLowerInvariant();
        return m.StartsWith("j") && m != "jmp" && m != "jmpe";
    }

    /// <summary>
    /// WoW64 step helper: patches target address(es) with EB FE (JMP $),
    /// resumes thread, polls EIP until it reaches a target, suspends, restores.
    /// Returns true if step succeeded.
    /// </summary>
    private async Task<bool> Wow64SpinStep(uint pid, uint tid, params ulong[] targetAddrs)
    {
        if (targetAddrs.Length == 0) return false;

        // Save original bytes and write EB FE at each target
        var spinLoop = new byte[] { 0xEB, 0xFE };
        var saved = new Dictionary<ulong, byte[]>();

        foreach (var addr in targetAddrs)
        {
            var orig = await Task.Run(() => _driver.ReadMemory(pid, addr, 2));
            if (orig == null || orig.Length < 2)
            {
                Log($"WoW64 step: failed to read bytes at {addr:X8}");
                // Restore already-written targets
                foreach (var kv in saved)
                    await Task.Run(() => _driver.WriteMemory(pid, kv.Key, kv.Value));
                return false;
            }
            saved[addr] = orig;

            var ok = await Task.Run(() => _driver.WriteMemory(pid, addr, spinLoop));
            if (!ok)
            {
                Log($"WoW64 step: failed to write EB FE at {addr:X8}");
                foreach (var kv in saved)
                    await Task.Run(() => _driver.WriteMemory(pid, kv.Key, kv.Value));
                return false;
            }
        }

        // Resume thread
        await Task.Run(() => _driver.ResumeThread(tid));

        // Poll EIP until it hits one of our targets (timeout 5s)
        var targetSet = new HashSet<ulong>(targetAddrs);
        bool hit = false;
        for (int i = 0; i < 100; i++)
        {
            await Task.Delay(50);
            var regs = await Task.Run(() => _driver.ReadRegisters(pid, tid, true));
            var eip = regs.FirstOrDefault(r => r.Name == "EIP");
            if (eip != null && targetSet.Contains(eip.Value))
            {
                hit = true;
                break;
            }
        }

        // Suspend thread
        await Task.Run(() => _driver.SuspendThread(tid));

        // Restore original bytes at all targets
        foreach (var kv in saved)
            await Task.Run(() => _driver.WriteMemory(pid, kv.Key, kv.Value));

        if (!hit)
            Log("WoW64 step: timeout waiting for EIP (5s)");

        return hit;
    }

    /// <summary>
    /// Refreshes registers, disassembly, stack after a WoW64 step.
    /// </summary>
    private async Task Wow64RefreshAfterStep()
    {
        var pid = TargetPid;
        var tid = SelectedThreadId;

        var regs = await Task.Run(() => _driver.ReadRegisters(pid, tid, true));
        var oldRegs = Registers.ToDictionary(r => r.Name, r => r.Value);
        foreach (var reg in regs)
        {
            if (oldRegs.TryGetValue(reg.Name, out var prev))
                reg.PreviousValue = prev;
        }
        Registers.ReplaceAll(regs);

        NavigateToRip();
        RefreshDisassembly();
        RefreshStack();
        RefreshCallStack();
    }

    private static string PromptInput(string title, string prompt, string defaultValue = "")
    {
        var dialog = new Window
        {
            Title = title,
            Width = 600,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current.MainWindow,
            Background = Application.Current.Resources["BgBrush"] as System.Windows.Media.Brush,
            Foreground = Application.Current.Resources["FgBrush"] as System.Windows.Media.Brush,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            ResizeMode = ResizeMode.CanResizeWithGrip
        };

        var stack = new StackPanel { Margin = new Thickness(12) };
        stack.Children.Add(new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap });

        var textBox = new System.Windows.Controls.TextBox
        {
            Text = defaultValue ?? "",
            Background = Application.Current.Resources["BgBrush"] as System.Windows.Media.Brush,
            Foreground = Application.Current.Resources["FgBrush"] as System.Windows.Media.Brush,
            BorderBrush = Application.Current.Resources["BorderBrush"] as System.Windows.Media.Brush,
            CaretBrush = Application.Current.Resources["FgBrush"] as System.Windows.Media.Brush,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
        };
        textBox.SelectAll();

        var buttonPanel = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0)
        };

        string result = "";
        var okBtn = new System.Windows.Controls.Button { Content = "OK", Width = 70, Margin = new Thickness(4, 0, 0, 0) };
        var cancelBtn = new System.Windows.Controls.Button { Content = "Cancel", Width = 70, Margin = new Thickness(4, 0, 0, 0) };

        okBtn.Click += (_, _) => { result = textBox.Text; dialog.DialogResult = true; dialog.Close(); };
        cancelBtn.Click += (_, _) => { dialog.DialogResult = false; dialog.Close(); };

        textBox.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter) { result = textBox.Text; dialog.DialogResult = true; dialog.Close(); }
            if (e.Key == System.Windows.Input.Key.Escape) { dialog.DialogResult = false; dialog.Close(); }
        };

        buttonPanel.Children.Add(okBtn);
        buttonPanel.Children.Add(cancelBtn);
        stack.Children.Add(textBox);
        stack.Children.Add(buttonPanel);
        dialog.Content = stack;

        dialog.ShowDialog();
        return result;
    }

    /// <summary>Detect if process is 32-bit by reading PE Optional Header magic from first module.</summary>
    private async Task<bool> DetectIs32BitAsync(uint pid, ulong baseAddress)
    {
        try
        {
            // Read e_lfanew from DOS header
            var dosData = await Task.Run(() => _driver.ReadMemory(pid, baseAddress + 0x3C, 4));
            if (dosData == null || dosData.Length < 4) return false;
            uint peOffset = BitConverter.ToUInt32(dosData, 0);

            // Read PE Optional Header magic (offset PE+0x18)
            var magicData = await Task.Run(() => _driver.ReadMemory(pid, baseAddress + peOffset + 0x18, 2));
            if (magicData == null || magicData.Length < 2) return false;
            ushort magic = BitConverter.ToUInt16(magicData, 0);

            // 0x10B = PE32 (32-bit), 0x20B = PE32+ (64-bit)
            return magic == 0x10B;
        }
        catch { return false; }
    }

    public ulong ResolveEntryPoint(ulong baseAddress)
    {
        if (!IsConnected || TargetPid == 0) return baseAddress;
        try
        {
            var dosHeader = _driver.ReadMemory(TargetPid, baseAddress + 0x3C, 4);
            if (dosHeader == null || dosHeader.Length < 4) return baseAddress;
            uint peOffset = BitConverter.ToUInt32(dosHeader, 0);

            var epData = _driver.ReadMemory(TargetPid, baseAddress + peOffset + 0x28, 4);
            if (epData == null || epData.Length < 4) return baseAddress;
            uint entryRva = BitConverter.ToUInt32(epData, 0);

            if (entryRva == 0) return baseAddress;
            return baseAddress + entryRva;
        }
        catch
        {
            return baseAddress;
        }
    }

    public ulong ResolveKernelEntryPoint(ulong baseAddress)
    {
        if (!IsConnected) return baseAddress;
        try
        {
            var dosHeader = _driver.ReadMemory(4, baseAddress + 0x3C, 4);
            if (dosHeader == null || dosHeader.Length < 4) return baseAddress;
            uint peOffset = BitConverter.ToUInt32(dosHeader, 0);

            var epData = _driver.ReadMemory(4, baseAddress + peOffset + 0x28, 4);
            if (epData == null || epData.Length < 4) return baseAddress;
            uint entryRva = BitConverter.ToUInt32(epData, 0);

            if (entryRva == 0) return baseAddress;
            return baseAddress + entryRva;
        }
        catch
        {
            return baseAddress;
        }
    }

    /* ================================================================== */
    /*  Symbols                                                            */
    /* ================================================================== */

    [RelayCommand]
    private async Task LoadAllSymbolsAsync()
    {
        Log("Initializing symbol engine...");
        var err = _symbols.Initialize();
        if (err != null)
        {
            Log($"Symbol engine FAILED: {err}");
            return;
        }
        Log($"Symbol path: {_symbols.SymbolPath}");

        int total = 0, loaded = 0;

        // Load kernel modules
        var kMods = KernelModules.ToList();
        if (kMods.Count > 0)
        {
            Log($"Loading symbols for {kMods.Count} kernel modules...");
            await Task.Run(() =>
            {
                foreach (var m in kMods)
                {
                    total++;
                    if (_symbols.LoadModule(0, m.Name, m.BaseAddress, m.Size))
                        loaded++;
                }
            });
            Log($"Kernel symbols: {loaded}/{kMods.Count}");
        }

        // Load user modules
        var uMods = Modules.ToList();
        var curPid = TargetPid;
        if (uMods.Count > 0)
        {
            int uLoaded = 0;
            Log($"Loading symbols for {uMods.Count} user modules...");
            await Task.Run(() =>
            {
                foreach (var m in uMods)
                {
                    total++;
                    if (_symbols.LoadModule(curPid, m.Name, m.BaseAddress, m.Size))
                    {
                        loaded++;
                        uLoaded++;
                    }
                }
            });
            Log($"User symbols: {uLoaded}/{uMods.Count}");
        }

        if (total == 0)
            Log("No modules loaded yet — connect and attach first");

        _symbols.ClearCache();
        Log($"Symbols: {loaded}/{total} modules loaded");
        StatusText = $"Symbols: {loaded}/{total} modules loaded";
    }

    [RelayCommand]
    private void SetSymbolPath()
    {
        var current = _symbols.SymbolPath;
        var result = PromptInput("Symbol Path",
            "Enter symbol path. Use ';' to separate paths.\n" +
            "Local PDB folders: C:\\MyPDBs;D:\\Build\\Output\n" +
            "Symbol server: srv*C:\\Symbols*https://msdl.microsoft.com/download/symbols",
            current);
        if (!string.IsNullOrEmpty(result) && result != current)
        {
            _symbols.SymbolPath = result;
            _symbols.ClearCache();
            SaveSettings();
            Log($"Symbol path changed to: {result}");
        }
    }

    [RelayCommand]
    private void ClearSymbolCache()
    {
        _symbols.Reset();
        Log("Symbol cache cleared — modules unloaded");
    }

    /* ================================================================== */
    /*  RetDec decompiler integration                                     */
    /* ================================================================== */

    private static readonly string RetDecExe = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "retdec", "retdec-decompiler.exe");

    /// <summary>
    /// Decompile a function at the given address with the given size.
    /// Dumps the containing PE module from memory, writes it as a temp file,
    /// then invokes retdec-decompiler.exe with --select-ranges.
    /// </summary>
    public async void DecompileFunction(ulong address, uint size)
    {
        if (!IsConnected || TargetPid == 0) return;

        // Try to resolve to containing function for accurate boundaries
        if (size == 0)
        {
            // 1. Check PDB functions (current module)
            var fn = _allFunctions.FirstOrDefault(f =>
                f.Address <= address && address < f.Address + f.Size && f.Size > 0);
            if (fn != null)
            {
                address = fn.Address;
                size = fn.Size;
            }
            else
            {
                // 2. Check exception entries (RUNTIME_FUNCTION — always has exact boundaries)
                var ex = _allExceptions.FirstOrDefault(e =>
                    e.FunctionStart <= address && address < e.FunctionEnd);
                if (ex != null)
                {
                    address = ex.FunctionStart;
                    size = ex.FunctionSize;
                }
                else
                {
                    // 3. Use dbghelp SymFromAddr — works for ALL loaded modules
                    var (symAddr, symSize) = _symbols.GetFunctionBounds(address);
                    if (symAddr != 0 && symSize > 0)
                    {
                        address = symAddr;
                        size = symSize;
                    }
                    else
                        size = 1; // minimal: RetDec finds function boundary by start address
                }
            }
        }

        // Follow thunk stubs (JMP/CALL wrappers in kernel32 → kernelbase etc.)
        ulong originalAddress = address;
        uint originalSize = size;
        ulong resolved = await ResolveThunkTarget(address, 5);
        if (resolved != address)
        {
            address = resolved;
            size = 0;
            var (symAddr, symSize) = _symbols.GetFunctionBounds(address);
            if (symAddr != 0 && symSize > 0) { address = symAddr; size = symSize; }
            else size = 1;
        }

        if (!File.Exists(RetDecExe))
        {
            Log($"RetDec not found: {RetDecExe}");
            DecompiledCode = $"// RetDec not found at:\n// {RetDecExe}\n// Place retdec-decompiler.exe in the 'retdec' subfolder next to KernelFlirt.exe";
            return;
        }

        // Find the module containing this address (search both user-mode and kernel modules)
        (ulong ModBase, uint ModSize, string ModName, uint ReadPid) FindModule(ulong addr)
        {
            var um = Modules.FirstOrDefault(m => addr >= m.BaseAddress && addr < m.BaseAddress + m.Size);
            if (um != null) return (um.BaseAddress, um.Size, um.Name, TargetPid);
            var km = KernelModules.FirstOrDefault(m => addr >= m.BaseAddress && addr < m.BaseAddress + m.Size);
            if (km != null) return (km.BaseAddress, km.Size, km.Name, 4);
            return (0, 0, "", 0);
        }

        var mod = FindModule(address);
        if (mod.ModBase == 0)
        {
            if (address != originalAddress)
            {
                Log($"Decompile: thunk target {FormatAddr(address)} not in any module, falling back to stub");
                address = originalAddress;
                size = originalSize;
                mod = FindModule(address);
            }
            if (mod.ModBase == 0)
            {
                DecompiledCode = "// Cannot decompile: function address is not inside any loaded module.\n// Load modules first.";
                Log($"Decompile: no module contains {FormatAddr(address)}");
                return;
            }
        }

        IsDecompiling = true;
        DecompiledCode = "// Decompiling...";
        ulong endAddr = address + size;
        Log($"Decompiling {FormatAddr(address)}-{FormatAddr(endAddr)} in {mod.ModName} (modSize=0x{mod.ModSize:X})...");

        try
        {
            // 1. Dump the PE module from process memory (page-by-page to handle paged-out pages)
            uint readSize = mod.ModSize > 0 ? mod.ModSize : 0x400000;
            if (readSize > 0x2000000) readSize = 0x2000000; // cap 32MB
            Log($"Decompile: reading 0x{readSize:X} bytes from {mod.ModName} at {FormatAddr(mod.ModBase)} (pid={mod.ReadPid})");
            var image = await Task.Run(() => ReadModulePageByPage(mod.ReadPid, mod.ModBase, readSize));
            if (image == null)
            {
                // If thunk target module unreadable, fall back to original stub
                if (address != originalAddress)
                {
                    Log($"Decompile: can't read {mod.ModName}, falling back to original stub");
                    address = originalAddress;
                    size = originalSize;
                    endAddr = address + size;
                    mod = FindModule(address);
                    if (mod.ModBase == 0) { DecompiledCode = "// Failed to read module"; return; }
                    readSize = mod.ModSize > 0 ? mod.ModSize : 0x400000;
                    if (readSize > 0x2000000) readSize = 0x2000000;
                    image = ReadModulePageByPage(mod.ReadPid, mod.ModBase, readSize);
                }
                if (image == null)
                {
                    DecompiledCode = $"// Failed to read module image from {mod.ModName} (0x{readSize:X} bytes)";
                    Log($"Decompile: ReadMemory failed for {mod.ModName} (0x{readSize:X} bytes)");
                    return;
                }
            }

            // 2. Fix memory-dumped PE for RetDec
            FixMemoryDumpPE(image, mod.ModBase);

            // 3. Write fixed PE image to temp file
            var tempDir = Path.Combine(Path.GetTempPath(), "KernelFlirt");
            Directory.CreateDirectory(tempDir);
            var inputFile = Path.Combine(tempDir, $"mod_{mod.ModBase:X}.exe");
            var outputFile = Path.Combine(tempDir, $"func_{address:X}.c");
            await File.WriteAllBytesAsync(inputFile, image);
            string rangeArg = $"0x{address:X}-0x{endAddr:X}";
            Log($"Decompile: range={rangeArg} (module {mod.ModName} base=0x{mod.ModBase:X})");
            string pdbArg = "";
            string selectArg;
            string decompileMethod;
            var pdbPath = _symbols.GetPdbPath(mod.ModBase);
            if (pdbPath != null && File.Exists(pdbPath))
            {
                pdbArg = $"-p \"{pdbPath}\"";
                Log($"Decompile: using PDB {pdbPath}");

                // If we have PDB + exact symbol name, use --select-functions (better quality)
                var symName = _symbols.ResolveExact(address);
                if (symName != null)
                {
                    selectArg = $"--select-functions \"{symName}\"";
                    decompileMethod = $"select-functions \"{symName}\" + PDB";
                    Log($"Decompile: method={decompileMethod}");
                }
                else
                {
                    selectArg = $"--select-ranges {rangeArg}";
                    decompileMethod = $"select-ranges {rangeArg} + PDB";
                    Log($"Decompile: method={decompileMethod}");
                }
            }
            else
            {
                selectArg = $"--select-ranges {rangeArg}";
                decompileMethod = $"select-ranges {rangeArg} (no PDB)";
                Log($"Decompile: method={decompileMethod}");
            }
            var psi = new ProcessStartInfo
            {
                FileName = RetDecExe,
                Arguments = $"{selectArg} --disable-static-code-detection {pdbArg} -o \"{outputFile}\" -s \"{inputFile}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(RetDecExe)!
            };

            var result = await Task.Run(() =>
            {
                using var proc = Process.Start(psi);
                if (proc == null) return (code: -1, stdout: "", stderr: "Failed to start process");
                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit(120_000);
                if (!proc.HasExited) { proc.Kill(); return (code: -1, stdout, stderr: "Timeout (120s)"); }
                return (code: proc.ExitCode, stdout, stderr);
            });

            // 4. Read decompiled output and resolve symbols
            if (File.Exists(outputFile))
            {
                var code = await File.ReadAllTextAsync(outputFile);
                code = CleanRetDecOutput(code);
                code = ResolveDecompiledSymbols(code);
                code = ResolveDecompiledTypes(code, address);
                code = $"// Decompiled: {mod.ModName} @ {FormatAddr(address)} [{decompileMethod}]\n\n{code}";
                DecompiledCode = code;
                Log($"Decompilation complete: {FormatAddr(address)} [{decompileMethod}] ({code.Split('\n').Length} lines)");
            }
            else
            {
                DecompiledCode = $"// Decompilation failed (exit code {result.code})\n// {result.stderr}";
                Log($"Decompile failed: exit={result.code} {result.stderr}");
            }

            // 5. Cleanup temp files
            try
            {
                if (File.Exists(inputFile)) File.Delete(inputFile);
                foreach (var f in Directory.GetFiles(tempDir, $"func_{address:X}.*"))
                    File.Delete(f);
                foreach (var f in Directory.GetFiles(tempDir, $"mod_{mod.ModBase:X}.*"))
                    File.Delete(f);
            }
            catch { }
        }
        catch (Exception ex)
        {
            DecompiledCode = $"// Exception: {ex.Message}";
            Log($"Decompile exception: {ex.Message}");
        }
        finally
        {
            IsDecompiling = false;
        }
    }

    /// <summary>
    /// <summary>
    /// Read a module from process memory page-by-page.
    /// Pages that fail to read are filled with zeros (handles paged-out pages).
    /// Returns null only if the PE header (first page) can't be read.
    /// </summary>
    private byte[]? ReadModulePageByPage(uint pid, ulong baseAddress, uint totalSize)
    {
        const uint PAGE_SIZE = 0x1000;

        // Try a single read first (fast path)
        var full = _driver.ReadMemory(pid, baseAddress, totalSize);
        if (full != null && full.Length > 0) return full;

        // Fallback: read page by page
        var result = new byte[totalSize];
        uint pages = (totalSize + PAGE_SIZE - 1) / PAGE_SIZE;
        int readOk = 0;

        for (uint i = 0; i < pages; i++)
        {
            uint offset = i * PAGE_SIZE;
            uint chunkSize = Math.Min(PAGE_SIZE, totalSize - offset);
            var page = _driver.ReadMemory(pid, baseAddress + offset, chunkSize);
            if (page != null && page.Length > 0)
            {
                Array.Copy(page, 0, result, offset, page.Length);
                readOk++;
            }
            // else: leave zeros (paged out / inaccessible)
        }

        // If we couldn't even read the PE header, fail
        if (readOk == 0) return null;

        return result;
    }

    /// <summary>
    /// <summary>
    /// Fix a memory-dumped PE so RetDec can parse it correctly:
    /// 1. Patch ImageBase to match runtime base address
    /// <summary>
    /// Follow thunk/stub chains (JMP [rip+X], JMP rel32, CALL rel32) to reach the real function.
    /// Returns the final target address, or the original address if not a thunk.
    /// maxDepth limits how many levels of indirection to follow.
    /// </summary>
    private async Task<ulong> ResolveThunkTarget(ulong address, int maxDepth)
    {
        ulong original = address;
        for (int i = 0; i < maxDepth; i++)
        {
            var code = await Task.Run(() => _driver.ReadMemory(TargetPid, address, 16));
            if (code == null || code.Length < 6) break;

            // FF 25 XX XX XX XX — JMP [rip + disp32] (6 bytes, indirect jump through IAT)
            if (code[0] == 0xFF && code[1] == 0x25)
            {
                int disp = BitConverter.ToInt32(code, 2);
                ulong ptrAddr = address + 6 + (ulong)disp;
                var ptr = await Task.Run(() => _driver.ReadMemory(TargetPid, ptrAddr, 8));
                if (ptr == null || ptr.Length < 8) break;
                ulong target = BitConverter.ToUInt64(ptr, 0);
                Log($"Decompile: thunk JMP [rip] at {FormatAddr(address)} → {FormatAddr(target)}");
                address = target;
                continue;
            }

            // E9 XX XX XX XX — JMP rel32 (5 bytes, direct relative jump)
            if (code[0] == 0xE9)
            {
                int disp = BitConverter.ToInt32(code, 1);
                ulong target = address + 5 + (ulong)disp;
                Log($"Decompile: thunk JMP rel32 at {FormatAddr(address)} → {FormatAddr(target)}");
                address = target;
                continue;
            }

            // 48 FF 25 XX XX XX XX — REX.W JMP [rip + disp32] (7 bytes)
            if (code[0] == 0x48 && code[1] == 0xFF && code[2] == 0x25)
            {
                int disp = BitConverter.ToInt32(code, 3);
                ulong ptrAddr = address + 7 + (ulong)disp;
                var ptr = await Task.Run(() => _driver.ReadMemory(TargetPid, ptrAddr, 8));
                if (ptr == null || ptr.Length < 8) break;
                ulong target = BitConverter.ToUInt64(ptr, 0);
                Log($"Decompile: thunk REX JMP [rip] at {FormatAddr(address)} → {FormatAddr(target)}");
                address = target;
                continue;
            }

            // Not a thunk — stop following
            break;
        }

        if (address != original)
        {
            var name = _symbols.ResolveExact(address);
            Log($"Decompile: resolved thunk chain → {FormatAddr(address)}{(name != null ? $" ({name})" : "")}");
        }

        return address;
    }

    /// <summary>
    /// Fix a memory-dumped PE so RetDec can parse it correctly:
    /// 1. Patch ImageBase to match runtime base address
    /// 2. Fix section headers: PointerToRawData=VirtualAddress, SizeOfRawData=VirtualSize
    /// </summary>
    private static void FixMemoryDumpPE(byte[] image, ulong runtimeBase)
    {
        if (image.Length < 0x40) return;
        if (image[0] != 0x4D || image[1] != 0x5A) return; // MZ check

        uint lfanew = BitConverter.ToUInt32(image, 0x3C);
        if (lfanew + 0x18 >= (uint)image.Length) return;
        if (BitConverter.ToUInt32(image, (int)lfanew) != 0x4550) return; // PE\0\0

        ushort magic = BitConverter.ToUInt16(image, (int)lfanew + 0x18);

        // Patch ImageBase
        if (magic == 0x20B) // PE32+
        {
            BitConverter.TryWriteBytes(image.AsSpan((int)lfanew + 0x30), runtimeBase);
        }
        else if (magic == 0x10B) // PE32
        {
            BitConverter.TryWriteBytes(image.AsSpan((int)lfanew + 0x34), (uint)runtimeBase);
        }
        else return;

        // Read number of sections and optional header size
        ushort numSections = BitConverter.ToUInt16(image, (int)lfanew + 0x06);
        ushort sizeOfOptHeader = BitConverter.ToUInt16(image, (int)lfanew + 0x14);

        // Section headers start after: PE sig (4) + COFF header (20) + optional header
        int sectionStart = (int)lfanew + 4 + 20 + sizeOfOptHeader;

        // Each section header is 40 bytes:
        //   +12: VirtualSize (4), +16: VirtualAddress (4)
        //   +20: SizeOfRawData (4), +24: PointerToRawData (4)
        for (int i = 0; i < numSections; i++)
        {
            int off = sectionStart + i * 40;
            if (off + 40 > image.Length) break;

            uint virtualSize = BitConverter.ToUInt32(image, off + 8);
            uint virtualAddr = BitConverter.ToUInt32(image, off + 12);

            // Set PointerToRawData = VirtualAddress (data is at VA offset in memory dump)
            BitConverter.TryWriteBytes(image.AsSpan(off + 20), virtualAddr);
            // Set SizeOfRawData = VirtualSize
            BitConverter.TryWriteBytes(image.AsSpan(off + 16), virtualSize);
        }
    }

    /// <summary>
    /// Remove RetDec boilerplate: header comment, section separators, meta-information.
    /// </summary>
    private static string CleanRetDecOutput(string code)
    {
        var sb = new System.Text.StringBuilder();
        bool inHeader = true;
        bool inMeta = false;

        foreach (var line in code.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');

            // Skip the "This file was generated by" header block
            if (inHeader)
            {
                if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("//"))
                {
                    if (trimmed.Contains("#include"))
                        { inHeader = false; sb.AppendLine(trimmed); }
                    continue;
                }
                inHeader = false;
            }

            // Skip meta-information at the end
            if (trimmed.StartsWith("// ---") && trimmed.Contains("Meta-Information"))
                { inMeta = true; continue; }
            if (inMeta) continue;

            // Skip section separator comments like "// --- Function Prototypes ---"
            if (trimmed.StartsWith("// ---") && trimmed.EndsWith("---"))
                continue;

            // Skip empty lines that were around separators
            sb.AppendLine(trimmed);
        }

        // Trim leading/trailing blank lines
        return sb.ToString().Trim('\r', '\n') + "\n";
    }

    /// <summary>
    /// Post-process RetDec output: replace function_XXXX / entry_point / unknown_XXXX with real symbol names.
    /// Uses dbghelp ResolveExact (displacement=0 only) for function names.
    /// Addresses in the output are runtime addresses (PE ImageBase patched to match module base).
    /// </summary>
    private string ResolveDecompiledSymbols(string code)
    {
        // Cache resolved addresses to avoid repeated dbghelp calls
        var cache = new Dictionary<ulong, string?>();

        string? ResolveAddr(ulong addr)
        {
            if (cache.TryGetValue(addr, out var cached))
                return cached;

            // Use exact match only — no displacement names like "Foo+0x118"
            string? name = _symbols.ResolveExact(addr);
            cache[addr] = name;
            return name;
        }

        // Replace function_HEXADDR, entry_point_HEXADDR, unknown_HEXADDR
        var result = System.Text.RegularExpressions.Regex.Replace(code,
            @"\b(function_|entry_point_?|unknown_)([0-9a-fA-F]{6,16})\b",
            match =>
            {
                string hexStr = match.Groups[2].Value;
                if (ulong.TryParse(hexStr, System.Globalization.NumberStyles.HexNumber, null, out ulong addr))
                {
                    var name = ResolveAddr(addr);
                    if (name != null) return name;
                }
                return match.Value;
            });

        // Standalone "entry_point" → resolve via Address range comment
        if (result.Contains("entry_point"))
        {
            var firstRange = System.Text.RegularExpressions.Regex.Match(result,
                @"// Address range: 0x([0-9a-fA-F]+)");
            if (firstRange.Success && ulong.TryParse(firstRange.Groups[1].Value,
                System.Globalization.NumberStyles.HexNumber, null, out ulong epAddr))
            {
                var epName = ResolveAddr(epAddr);
                if (epName != null)
                    result = result.Replace("entry_point", epName);
            }
        }

        return result;
    }

    // Regex matching C type declarations used by RetDec
    private static readonly System.Text.RegularExpressions.Regex TypeDeclPattern = new(
        @"\b(u?int(?:8|16|32|64|128)_t|char|void|float|double|float64_t|int|long|short|bool|unsigned\s+int|unsigned\s+long)\s*(\*{0,3})",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Post-process RetDec output: replace generic C types (int32_t, int32_t*, etc.)
    /// with proper PDB type names (HWND, LPARAM, BOOL, etc.) for known parameters/locals.
    /// Matches parameters by position (RetDec renames params to a1,a2...).
    /// Also renames parameters to their real PDB names throughout the code.
    /// </summary>
    private string ResolveDecompiledTypes(string code, ulong funcAddress)
    {
        SymbolService.FunctionTypeInfo typeInfo;
        try
        {
            typeInfo = _symbols.GetFunctionTypeInfo(funcAddress);
        }
        catch (Exception ex)
        {
            Log($"ResolveDecompiledTypes: {ex.Message}");
            return code;
        }

        if (typeInfo.Params.Count == 0 && typeInfo.Locals.Count == 0) return code;

        Log($"PDB params: {string.Join(", ", typeInfo.Params.Select(p => $"{p.Type} {p.Name}"))}");
        if (typeInfo.Locals.Count > 0)
            Log($"PDB locals: {string.Join(", ", typeInfo.Locals.Select(kv => $"{kv.Value} {kv.Key}"))}");

        int totalReplacements = 0;

        // 1. Match parameters by position in function signature
        if (typeInfo.Params.Count > 0)
        {
            // Find the function definition: starts at column 0 (no indentation),
            // has return type + function name + params + opening brace.
            // RetDec function definitions are never indented; if/while/for are always indented.
            var funcSigPattern = new System.Text.RegularExpressions.Regex(
                @"^(?=\S).*?\b(\w+)\s*\(([^)]*)\)\s*\{",
                System.Text.RegularExpressions.RegexOptions.Multiline);

            System.Text.RegularExpressions.Match? sigMatch = null;
            foreach (System.Text.RegularExpressions.Match m in funcSigPattern.Matches(code))
            {
                sigMatch = m; // take the last one (actual definition, not forward decl)
            }

            if (sigMatch != null)
            {
                string funcName = sigMatch.Groups[1].Value;
                string paramList = sigMatch.Groups[2].Value;
                Log($"Found function definition: {funcName}({paramList.Substring(0, Math.Min(60, paramList.Length))}...)");

                string[] retdecParams = paramList.Split(',');

                // Extract RetDec parameter names
                var retdecParamNames = new List<string>();
                var paramNamePattern = new System.Text.RegularExpressions.Regex(@"\b(\w+)\s*$");
                foreach (string rp in retdecParams)
                {
                    var m = paramNamePattern.Match(rp.Trim());
                    retdecParamNames.Add(m.Success ? m.Groups[1].Value : "");
                }

                Log($"RetDec params: [{string.Join(", ", retdecParamNames)}]");
                Log($"PDB   params: [{string.Join(", ", typeInfo.Params.Select(p => $"{p.Type} {p.Name}"))}]");

                // Match by position and build rename map
                int matchCount = Math.Min(typeInfo.Params.Count, retdecParamNames.Count);
                // First pass: replace types (before renaming variables)
                for (int i = 0; i < matchCount; i++)
                {
                    string retdecName = retdecParamNames[i];
                    var (_, pdbType) = typeInfo.Params[i];
                    if (string.IsNullOrEmpty(retdecName)) continue;

                    // Replace type in all declarations of this variable
                    var typePattern = $@"\b(u?int(?:8|16|32|64|128)_t|char|void|float|double|float64_t|int|long|short|bool|unsigned\s+int|unsigned\s+long)\s*(\*{{0,3}})\s+({System.Text.RegularExpressions.Regex.Escape(retdecName)})\b";
                    code = System.Text.RegularExpressions.Regex.Replace(code, typePattern,
                        match =>
                        {
                            totalReplacements++;
                            return $"{pdbType} {match.Groups[3].Value}";
                        });
                }

                // Second pass: rename variables (after types are replaced)
                // Build rename map first, then resolve conflicts
                var renames = new List<(string From, string To)>();
                for (int i = 0; i < matchCount; i++)
                {
                    string retdecName = retdecParamNames[i];
                    var (pdbName, _) = typeInfo.Params[i];
                    if (string.IsNullOrEmpty(retdecName)) continue;
                    if (string.Equals(retdecName, pdbName, StringComparison.Ordinal)) continue;
                    renames.Add((retdecName, pdbName));
                }

                // Collect all names used in code (params + any variables that might conflict)
                var allRetdecNames = new HashSet<string>(retdecParamNames.Where(n => !string.IsNullOrEmpty(n)));

                // Pre-pass: rename away any existing variables that would collide with incoming PDB names
                foreach (var (from, to) in renames)
                {
                    // If target name already exists as a different variable, rename it first
                    if (allRetdecNames.Contains(to) && !renames.Any(r => r.From == to))
                    {
                        // This name exists but isn't being renamed itself — move it out of the way
                        string safeName = to + "_";
                        while (allRetdecNames.Contains(safeName)) safeName += "_";
                        var conflictPattern = $@"\b{System.Text.RegularExpressions.Regex.Escape(to)}\b";
                        code = System.Text.RegularExpressions.Regex.Replace(code, conflictPattern, safeName);
                        allRetdecNames.Remove(to);
                        allRetdecNames.Add(safeName);
                        Log($"Rename conflict: moved '{to}' → '{safeName}' to avoid collision");
                    }
                }

                // Now apply the actual renames
                foreach (var (from, to) in renames)
                {
                    var renamePattern = $@"\b{System.Text.RegularExpressions.Regex.Escape(from)}\b";
                    code = System.Text.RegularExpressions.Regex.Replace(code, renamePattern, to);
                    totalReplacements++;
                }
            }
            else
            {
                Log("ResolveDecompiledTypes: could not find function definition in code");
            }
        }

        // 2. Match locals by name (they sometimes keep PDB names)
        foreach (var (varName, pdbType) in typeInfo.Locals)
        {
            var pattern = $@"\b(u?int(?:8|16|32|64|128)_t|char|void|float|double|float64_t|int|long|short|bool|unsigned\s+int|unsigned\s+long)\s*(\*{{0,3}})\s+({System.Text.RegularExpressions.Regex.Escape(varName)})\b";
            code = System.Text.RegularExpressions.Regex.Replace(code, pattern,
                match =>
                {
                    string oldType = match.Groups[1].Value + match.Groups[2].Value.Trim();
                    if (string.Equals(oldType, pdbType, StringComparison.OrdinalIgnoreCase))
                        return match.Value;
                    totalReplacements++;
                    return $"{pdbType} {match.Groups[3].Value}";
                });
        }

        Log($"ResolveDecompiledTypes: {totalReplacements} replacements made");
        return code;
    }

    [RelayCommand]
    private void DecompileAtCursor()
    {
        var addr = SelectedDisasmAddress != 0 ? SelectedDisasmAddress : DisasmAddress;
        if (addr == 0) return;

        // Try to find function containing this address for accurate size
        uint size = 0;
        var fn = _allFunctions.FirstOrDefault(f => f.Address <= addr && addr < f.Address + f.Size && f.Size > 0);
        if (fn != null)
        {
            addr = fn.Address;  // decompile from function start
            size = fn.Size;
        }

        DecompileFunction(addr, size);
    }

    public void Log(string message)
    {
        string entry = $"[{DateTime.Now:HH:mm:ss}] {message}";
        LogMessages.Add(entry);
        while (LogMessages.Count > 500)
            LogMessages.RemoveAt(0);
    }

    public void Dispose()
    {
        _pluginManager.UnloadAll();
        StopDebugListener();
        _symbols.Dispose();
        _driver.Dispose();
        _disasm.Dispose();
        GC.SuppressFinalize(this);
    }
}
