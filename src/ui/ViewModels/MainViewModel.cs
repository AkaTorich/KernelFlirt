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
    public RangeObservableCollection<string> StackEntries { get; } = [];
    public RangeObservableCollection<CallStackFrame> CallStack { get; } = [];
    public ObservableCollection<Bookmark> Bookmarks { get; } = [];
    public ObservableCollection<Patch> Patches { get; } = [];
    public RangeObservableCollection<SehEntry> SehChain { get; } = [];
    public RangeObservableCollection<SearchResult> SearchResults { get; } = [];
    public RangeObservableCollection<ImportEntry> Imports { get; } = [];
    public RangeObservableCollection<ImportEntry> FilteredImports { get; } = [];
    private List<ImportEntry> _allImports = [];
    [ObservableProperty] private string _importFilter = "";
    public RangeObservableCollection<FunctionEntry> Functions { get; } = [];
    public RangeObservableCollection<FunctionEntry> FilteredFunctions { get; } = [];
    private List<FunctionEntry> _allFunctions = [];
    [ObservableProperty] private string _functionFilter = "";
    public RangeObservableCollection<ExceptionEntry> FilteredExceptions { get; } = [];
    private List<ExceptionEntry> _allExceptions = [];
    [ObservableProperty] private string _exceptionFilter = "";
    public RangeObservableCollection<SectionEntry> FilteredSections { get; } = [];
    private List<SectionEntry> _allSections = [];
    [ObservableProperty] private string _sectionFilter = "";
    [ObservableProperty] private byte[] _hexData = [];

    private static readonly string SettingsFile =
        Path.Combine(AppContext.BaseDirectory, "kf_settings.txt");

    public MainViewModel()
    {
        _symbols = new SymbolService(_driver);
        _symbols.LogMessage += msg => Application.Current.Dispatcher.Invoke(() => Log(msg));
        LoadSettings();
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsFile)) return;
            foreach (var line in File.ReadAllLines(SettingsFile))
            {
                if (line.StartsWith("SymbolPath=", StringComparison.Ordinal))
                    _symbols.SymbolPath = line["SymbolPath=".Length..];
            }
        }
        catch { /* ignore */ }
    }

    private void SaveSettings()
    {
        try
        {
            File.WriteAllText(SettingsFile, $"SymbolPath={_symbols.SymbolPath}\n");
        }
        catch { /* ignore */ }
    }

    /* ================================================================== */
    /*  Connection                                                         */
    /* ================================================================== */

    [RelayCommand]
    private async Task ConnectKernelAsync()
    {
        string input = PromptInput("Connect",
            "Enter host:port for remote, or leave blank for local driver:");
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
            var stackItems = new List<string>();
            int sp = PointerSize;
            string spName = SpRegName;
            for (int i = 0; i < stackData.Length; i += sp)
            {
                if (i + sp > stackData.Length) break;
                ulong val = Is32Bit ? BitConverter.ToUInt32(stackData, i) : BitConverter.ToUInt64(stackData, i);
                stackItems.Add($"{spName}+{i:X2}  {FormatAddr(val)}");
            }
            StackEntries.ReplaceAll(stackItems);
        }

        var hexData = hexTask.Result;
        if (hexData != null) HexData = hexData;

        RefreshImports();
        RefreshExceptions();
        RefreshSections();
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
            var stackItems = new List<string>();
            int sp = PointerSize;
            string spName = SpRegName;
            for (int i = 0; i < stackData.Length; i += sp)
            {
                if (i + sp > stackData.Length) break;
                ulong val = Is32Bit ? BitConverter.ToUInt32(stackData, i) : BitConverter.ToUInt64(stackData, i);
                var annotation = ResolveStackValue(TargetPid, val, sysModList, sysKmodList);
                if (annotation == null && val != 0)
                    annotation = await TryReadStringAtAsync(TargetPid, val);
                stackItems.Add(annotation != null
                    ? $"{spName}+{i:X2}  {FormatAddr(val)}  {annotation}"
                    : $"{spName}+{i:X2}  {FormatAddr(val)}");
            }
            StackEntries.ReplaceAll(stackItems);
        }

        var hexData = hexTask.Result;
        if (hexData != null) HexData = hexData;

        RefreshCallStack();
        RefreshImports();
        RefreshExceptions();
        RefreshSections();
        _ = RefreshFunctionsAsync();

        _isPausedViaSuspend = false;
        _hitSwBp = null;
        IsBreakState = true;
        IsRunning = false;
        StatusText = $"DriverEntry - {serviceName} PID {TargetPid} TID {SelectedThreadId}";
        Log($"Stopped at DriverEntry of {sysPath}");
    }

    [RelayCommand]
    private async Task AttachProcessAsync()
    {
        if (!IsConnected || TargetPid == 0) return;
        await DoAttachAsync();
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
            var stackItems = new List<string>();
            int sp = PointerSize;
            string spName = SpRegName;
            for (int i = 0; i < stackData.Length; i += sp)
            {
                if (i + sp > stackData.Length) break;
                ulong val = Is32Bit ? BitConverter.ToUInt32(stackData, i) : BitConverter.ToUInt64(stackData, i);
                var annotation = ResolveStackValue(pid, val, moduleList, kmodList);
                if (annotation == null && val != 0)
                    annotation = await TryReadStringAtAsync(pid, val);
                stackItems.Add(annotation != null
                    ? $"{spName}+{i:X2}  {FormatAddr(val)}  {annotation}"
                    : $"{spName}+{i:X2}  {FormatAddr(val)}");
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

        // Parse imports from main exe
        RefreshImports();
        RefreshExceptions();
        RefreshSections();
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
            Log("Auto-break: no main/WinMain found, staying at CRT startup");
            return false;
        }

        // Set temp BP at entry point
        var handle = await Task.Run(() => _driver.SetBreakpoint(pid, 0, entryAddr, BreakpointType.Software));
        if (!handle.HasValue)
        {
            Log($"Auto-break: failed to set BP at {foundName} ({entryAddr:X16})");
            return false;
        }

        _tempBpHandle = handle.Value;
        Log($"Auto-break: BP at {foundName} ({entryAddr:X16}), running...");

        // Resume all threads — they're suspended, not in debug event
        var threads = Threads.ToList();
        await Task.Run(() =>
        {
            foreach (var t in threads)
                _driver.ResumeThread(t.ThreadId);
        });
        _isPausedViaSuspend = false;
        IsBreakState = false;
        IsRunning = true;
        StatusText = $"Running to {foundName}...";
        StartDebugListener();
        return true;
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

        // Send RESET — removes hook, all BPs, AND cancels pending WAIT IRP in driver.
        // This unblocks the listener task's WaitDebugEvent call.
        await Task.Run(() => _driver.ResetDriver());
        IsDebugHookActive = false;
        Log("Driver reset (hook removed, pending WAIT cancelled)");

        StopDebugListener();

        _hitSwBp = null;
        _tempBpHandle = null;
        _allFunctions = [];
        _allImports = [];
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
        Imports.Clear();
        FilteredImports.Clear();
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

        // Native 64-bit: use debug hook mechanism
        StartDebugListener();
        await Task.Run(() => _driver.ContinueDebugEvent(DriverComm.CONTINUE_STEP_INTO));
        _hitSwBp = null;
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

    /* ================================================================== */
    /*  Debugging: Run / Continue (F9 / F5)                                */
    /* ================================================================== */

    [RelayCommand]
    private async Task Run()
    {
        if (!IsConnected || TargetPid == 0) return;
        if (IsRunning) return;

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

    /// <summary>Navigate disassembly to a specific address (used by disasm context menus).</summary>
    public void NavigateDisasmTo(ulong address)
    {
        if (address == 0) return;
        DisasmAddress = address;
        RefreshDisassembly();
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
            DisasmAddress = addr;
            RefreshDisassembly();
            Log($"Navigate to {addr:X16}");
            return;
        }

        // Try symbol name (e.g. "WinMain", "ntdll!NtClose", "main")
        var resolved = _symbols.ResolveNameToAddress(trimmed);
        if (resolved != 0)
        {
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
            DisasmAddress = bookmark.Address;
            RefreshDisassembly();
            Log($"Go to bookmark: {bookmark.Label}");
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
        Log("DebugListener: starting WaitDebugEvent...");

        _listenerTask = Task.Run(() =>
        {
            int nullCount = 0;
            while (!ct.IsCancellationRequested)
            {
                var evt = _driver.WaitDebugEvent();
                if (evt == null)
                {
                    nullCount++;
                    if (nullCount <= 3)
                        Application.Current?.Dispatcher.InvokeAsync(() =>
                            Log($"DebugListener: WaitDebugEvent returned null (#{nullCount})"));
                    continue;
                }

                Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    Log($"DebugListener: got event Type={evt.Type} Addr={evt.Address:X16} PID={evt.ProcessId} TID={evt.ThreadId}");
                    OnDebugEvent(evt);
                });
                return; // One event at a time — UI decides what to do next
            }
        }, ct);
    }

    private void StopDebugListener()
    {
        _listenerCts?.Cancel();
        // Wait for the listener task to finish (RESET should have cancelled the pending IRP,
        // so WaitDebugEvent will return null and the task will exit).
        if (_listenerTask != null)
        {
            try { _listenerTask.Wait(3000); } catch { /* timeout or cancelled — ok */ }
        }
        _listenerCts?.Dispose();
        _listenerCts = null;
        _listenerTask = null;
    }

    private async void OnDebugEvent(DebugEvent evt)
    {
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

        Log($"Break at {evt.Address:X16} (PID={evt.ProcessId} TID={evt.ThreadId})");
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
        var data = await Task.Run(() => _driver.ReadMemory(pid, addr, 4096));
        if (data == null) return;

        PatchBpBytesForDisasm(data, addr);
        var instrs = _disasm.Disassemble(data, addr);
        AnnotateInstructionsWithSymbols(instrs);
        foreach (var instr in instrs)
            instr.HasBreakpoint = Breakpoints.Any(b => b.Address == instr.Address);
        Instructions.ReplaceAll(instrs);
        SyncBreakpointMarkers();
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

    public async void RefreshModules()
    {
        if (!IsConnected || TargetPid == 0) return;
        var pid = TargetPid;
        var mods = await Task.Run(() => _driver.EnumModules(pid));
        if (Is32Bit)
            foreach (var m in mods) m.Is32Bit = true;
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

        _allSections = entries;
        ApplySectionFilter();
        Log($"Sections: {entries.Count} sections from {mods.Count + kmods.Count} modules");
    }

    private List<SectionEntry> ParseSectionsFromBuffer(byte[] image, ulong modBase, string modName, ref int idx)
    {
        var result = new List<SectionEntry>();
        try
        {
            if (image[0] != 'M' || image[1] != 'Z') return result;
            uint peOffset = BitConverter.ToUInt32(image, 0x3C);
            if (peOffset + 0x18 > image.Length) return result;
            if (image[peOffset] != 'P' || image[peOffset + 1] != 'E') return result;

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

    private static List<ImportEntry> ParseImportsFromBuffer(byte[] image, ulong modBase)
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
            if (importRva == 0 || importSize == 0) return result;
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
        var items = new List<string>();
        int sp = PointerSize;
        string spName = SpRegName;
        for (int i = 0; i < data.Length; i += sp)
        {
            if (i + sp > data.Length) break;
            ulong val = Is32Bit ? BitConverter.ToUInt32(data, i) : BitConverter.ToUInt64(data, i);
            var annotation = ResolveStackValue(pid, val, moduleList, kmodList);
            if (annotation == null && val != 0)
                annotation = await TryReadStringAtAsync(pid, val);
            items.Add(annotation != null
                ? $"{spName}+{i:X2}  {FormatAddr(val)}  {annotation}"
                : $"{spName}+{i:X2}  {FormatAddr(val)}");
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
            var stackItems = new List<string>();
            int sp = PointerSize;
            string spName = SpRegName;
            for (int i = 0; i < stackData.Length; i += sp)
            {
                if (i + sp > stackData.Length) break;
                ulong val = Is32Bit ? BitConverter.ToUInt32(stackData, i) : BitConverter.ToUInt64(stackData, i);
                stackItems.Add($"{spName}+{i:X2}  {FormatAddr(val)}");
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

    private static string PromptInput(string title, string prompt)
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
            Background = Application.Current.Resources["BgBrush"] as System.Windows.Media.Brush,
            Foreground = Application.Current.Resources["FgBrush"] as System.Windows.Media.Brush,
            BorderBrush = Application.Current.Resources["BorderBrush"] as System.Windows.Media.Brush,
            CaretBrush = Application.Current.Resources["FgBrush"] as System.Windows.Media.Brush,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
        };

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
            "Symbol server: srv*C:\\Symbols*https://msdl.microsoft.com/download/symbols\n\n" +
            "Current: " + current);
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

    public void Log(string message)
    {
        string entry = $"[{DateTime.Now:HH:mm:ss}] {message}";
        LogMessages.Add(entry);
    }

    public void Dispose()
    {
        StopDebugListener();
        _symbols.Dispose();
        _driver.Dispose();
        _disasm.Dispose();
        GC.SuppressFinalize(this);
    }
}
