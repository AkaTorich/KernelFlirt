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
    [ObservableProperty] private ulong _selectedDisasmAddress;  // Cursor position in disasm

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

        // 3. Read PE header → AddressOfEntryPoint
        ulong entryPoint = 0;
        var peOffsetData = await Task.Run(() => _driver.ReadMemory(pid, imageBase + 0x3C, 4));
        if (peOffsetData != null && peOffsetData.Length == 4)
        {
            uint peOffset = BitConverter.ToUInt32(peOffsetData, 0);
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

        // 4. Set software breakpoint at entry point
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

        // 5. Resume the suspended thread — loader will run, then hit our BP
        await Task.Run(() => _driver.ResumeThread(tid));

        // 6. Wait for the debug event (BP hit at entry point)
        IsRunning = true;
        IsBreakState = false;

        var evt = await Task.Run(() => _driver.WaitDebugEvent());

        // 7. Remove the temp BP and fix RIP (INT3 advanced RIP by 1)
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
        // Driver already adjusted RIP back to BP address in its INT3 handler.

        // 8. Now the process is stopped at entry point with loader done.
        //    Enumerate modules, read registers, etc. — same as DoAttachAsync but
        //    we're already hooked and stopped on a debug event (not suspend).
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
        Modules.ReplaceAll(modules);

        // Read registers
        var regs = await Task.Run(() => _driver.ReadRegisters(pid, SelectedThreadId));
        Registers.ReplaceAll(regs);

        var rip = Registers.FirstOrDefault(r => r.Name == "RIP");
        if (rip != null && rip.Value != 0)
        {
            DisasmAddress = rip.Value;
            Log($"RIP = {rip.Value:X16}");
        }

        // Fetch disasm, stack, hex dump
        var rspReg = Registers.FirstOrDefault(r => r.Name == "RSP");
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
            for (int i = 0; i < stackData.Length; i += 8)
            {
                if (i + 8 > stackData.Length) break;
                ulong val = BitConverter.ToUInt64(stackData, i);
                stackItems.Add($"RSP+{i:X2}  {val:X16}");
            }
            StackEntries.ReplaceAll(stackItems);
        }

        var hexData = hexTask.Result;
        if (hexData != null) HexData = hexData;

        RefreshImports();
        RefreshCallStack();
        _ = RefreshFunctionsAsync();

        // We're stopped on a debug event — NOT via SuspendThread
        _isPausedViaSuspend = false;
        _hitSwBp = null;
        IsBreakState = true;
        IsRunning = false;
        StatusText = $"Entry point - PID {pid} TID {SelectedThreadId}";
        Log($"Stopped at entry point of {exePath}");
    }

    private async Task OpenAndDebugDriverAsync(string sysPath)
    {
        // 0. Unload previous driver if still loaded
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
        StatusText = "Installing debug hook for kernel...";

        // 1. Install debug hook with PID=4 (System) BEFORE loading the driver
        //    so the hook is ready when DriverEntry hits INT3
        TargetPid = 4; // System process
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

        // 2. Send LOAD_DRIVER — relay installs service, patches entry to INT3,
        //    starts driver in background. Returns immediately with info.
        StatusText = "Loading driver (waiting for DriverEntry)...";
        var loadResult = await Task.Run(() => _driver.LoadRemoteDriver(sysPath));
        if (loadResult == null)
        {
            Log("Failed to load driver on VM");
            StatusText = "Driver load failed";
            // Cancel the wait by removing hook
            await Task.Run(() => _driver.RemoveDebugHook());
            IsDebugHookActive = false;
            return;
        }

        var (serviceName, entryRva, originalByte) = loadResult.Value;
        _loadedDriverServiceName = serviceName;
        _driverOriginalByte = originalByte;
        _driverEntryRva = entryRva;
        Log($"Driver installed: service={serviceName} EntryRVA=0x{entryRva:X} OrigByte=0x{originalByte:X2}");

        // 3. Wait for DriverEntry INT3 — skip spurious kernel INT3s
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
            Log($"RIP = {evtRip:X16}");
        }

        // Fetch disasm, stack, hex dump
        var rspReg = regs.FirstOrDefault(r => r.Name == "RSP");
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
            for (int i = 0; i < stackData.Length; i += 8)
            {
                if (i + 8 > stackData.Length) break;
                ulong val = BitConverter.ToUInt64(stackData, i);
                var annotation = ResolveStackValue(TargetPid, val, sysModList, sysKmodList);
                if (annotation == null && val != 0)
                    annotation = await TryReadStringAtAsync(TargetPid, val);
                stackItems.Add(annotation != null
                    ? $"RSP+{i:X2}  {val:X16}  {annotation}"
                    : $"RSP+{i:X2}  {val:X16}");
            }
            StackEntries.ReplaceAll(stackItems);
        }

        var hexData = hexTask.Result;
        if (hexData != null) HexData = hexData;

        RefreshCallStack();
        RefreshImports();
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
        Modules.ReplaceAll(modules);

        if (Threads.Count > 0)
            SelectedThreadId = Threads[0].ThreadId;

        // Read registers
        Log("Reading registers...");
        var tid = SelectedThreadId;
        var regs = await Task.Run(() => _driver.ReadRegisters(pid, tid));
        Registers.ReplaceAll(regs);
        Log($"Got {regs.Count} registers");

        var rip = Registers.FirstOrDefault(r => r.Name == "RIP");
        if (rip != null && rip.Value != 0)
            Log($"RIP = {rip.Value:X16}");

        // Navigate disassembly to RIP
        if (rip != null && rip.Value != 0)
        {
            DisasmAddress = rip.Value;
            Log($"Disasm → RIP {rip.Value:X16}");
        }
        else if (Modules.Count > 0)
        {
            var ep = ResolveEntryPoint(Modules[0].BaseAddress);
            DisasmAddress = ep;
            Log($"Disasm → {Modules[0].Name} entry point {ep:X16}");
        }

        // Fetch disasm, stack, hex dump in parallel
        var rspReg = Registers.FirstOrDefault(r => r.Name == "RSP");
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
            for (int i = 0; i < stackData.Length; i += 8)
            {
                if (i + 8 > stackData.Length) break;
                ulong val = BitConverter.ToUInt64(stackData, i);
                var annotation = ResolveStackValue(pid, val, moduleList, kmodList);
                if (annotation == null && val != 0)
                    annotation = await TryReadStringAtAsync(pid, val);
                stackItems.Add(annotation != null
                    ? $"RSP+{i:X2}  {val:X16}  {annotation}"
                    : $"RSP+{i:X2}  {val:X16}");
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
        var rip = Registers.FirstOrDefault(r => r.Name == "RIP");
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

            // Resume all threads if suspended
            if (_isPausedViaSuspend)
            {
                var threads = Threads.ToList();
                await Task.Run(() =>
                {
                    foreach (var t in threads)
                        _driver.ResumeThread(t.ThreadId);
                });
                _isPausedViaSuspend = false;
                Log("Resumed all threads");
            }

            // If blocked on debug event, continue so thread unblocks
            if (IsBreakState && !_isPausedViaSuspend)
            {
                await Task.Run(() => _driver.ContinueDebugEvent(DriverComm.CONTINUE_RUN));
                Log("Continued blocked thread");
            }
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
        Instructions.Clear();
        Registers.Clear();
        Modules.Clear();
        Threads.Clear();
        StackEntries.Clear();
        CallStack.Clear();
        SehChain.Clear();
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

        // Start listener before continuing
        StartDebugListener();

        // Driver handles step-past internally for SW BPs:
        //   STEP_INTO on a BP → restore byte, step, re-arm, report SingleStep
        //   STEP_INTO on non-BP → just single step
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

        // Check if current instruction is a CALL — if so, set temp BP after it and run
        var instr = GetInstructionAtRip();
        if (instr != null && IsCallInstruction(instr.Mnemonic))
        {
            ulong nextAddr = instr.Address + (ulong)instr.Size;
            var tmpHandle = await Task.Run(() => _driver.SetBreakpoint(TargetPid, 0, nextAddr, BreakpointType.Software));
            if (tmpHandle.HasValue)
                _tempBpHandle = tmpHandle.Value;
            // Start listener before continuing
            StartDebugListener();
            await Task.Run(() => _driver.ContinueDebugEvent(
                _hitSwBp != null ? DriverComm.CONTINUE_STEP_PAST : DriverComm.CONTINUE_RUN));
        }
        else
        {
            // Start listener before continuing
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

        // Set temp breakpoint at return address (top of stack)
        var rsp = Registers.FirstOrDefault(r => r.Name == "RSP");
        if (rsp == null) return;

        var retData = await Task.Run(() => _driver.ReadMemory(TargetPid, rsp.Value, 8));
        if (retData == null || retData.Length < 8) return;
        ulong retAddr = BitConverter.ToUInt64(retData, 0);

        var tmpHandle = await Task.Run(() => _driver.SetBreakpoint(TargetPid, 0, retAddr, BreakpointType.Software));
        if (tmpHandle.HasValue)
            _tempBpHandle = tmpHandle.Value;

        IsBreakState = false;
        IsRunning = true;
        StatusText = "Stepping out...";
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

        // If paused via thread suspend, resume all threads
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

        // Start listener BEFORE continuing so WaitDebugEvent IRP is pending
        // when the thread resumes and hits the next breakpoint
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
        var regs = await Task.Run(() => _driver.ReadRegisters(pid, tid));
        Registers.ReplaceAll(regs);

        var rip = Registers.FirstOrDefault(r => r.Name == "RIP");
        if (rip != null && rip.Value != 0)
        {
            DisasmAddress = rip.Value;
            Log($"Paused at RIP = {rip.Value:X16}");
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
                    ModuleName = _symbols.ResolveAddress(TargetPid, address, Modules.ToList())
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
        var rip = Registers.FirstOrDefault(r => r.Name == "RIP");
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
                            Preview = BitConverter.ToString(data, i, Math.Min(16, data.Length - i)).Replace("-", " ")
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

        var results = await Task.Run(() =>
        {
            var found = new List<SearchResult>();
            foreach (var module in mods)
            {
                var data = _driver.ReadMemory(pid, module.BaseAddress,
                                               Math.Min(module.Size, 1048576u));
                if (data == null) continue;

                SearchInDataBg(found, data, asciiPattern, module.BaseAddress, module.Name, "ASCII");
                SearchInDataBg(found, data, unicodePattern, module.BaseAddress, module.Name, "Unicode");
                if (found.Count >= 1000) break;
            }
            return found;
        });

        SearchResults.ReplaceAll(results);
        Log($"String search: found {SearchResults.Count} results for \"{text}\"");
    }

    private static void SearchInDataBg(List<SearchResult> results, byte[] data, byte[] pattern,
        ulong baseAddr, string moduleName, string encoding)
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
                                    Preview = $"call {targetMod.Name}+{target - targetMod.BaseAddress:X}"
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
            regs.AddRange(Register.ExpandFlags(r.Rflags));
        }
        else
        {
            regs = await Task.Run(() => _driver.ReadRegisters(pid, tid));
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
        BreakpointMarkersChanged?.Invoke();
    }

    /// <summary>Raised when BP markers are synced — UI should refresh DataGrids.</summary>
    public event Action? BreakpointMarkersChanged;

    public async void RefreshRegisters()
    {
        if (!IsConnected || TargetPid == 0 || SelectedThreadId == 0) return;

        var pid = TargetPid;
        var tid = SelectedThreadId;
        var regs = await Task.Run(() => _driver.ReadRegisters(pid, tid));

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

    public async void RefreshImports(ulong moduleBase = 0)
    {
        if (!IsConnected || TargetPid == 0) return;

        uint moduleSize = 0;
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
                if (kernMod != null) moduleSize = kernMod.Size;
            }
        }

        if (moduleSize == 0) moduleSize = 2 * 1024 * 1024;
        // Cap at 4MB to avoid huge reads
        uint readSize = Math.Min(moduleSize, 4 * 1024 * 1024);

        var pid = TargetPid;
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
        var rsp = Registers.FirstOrDefault(r => r.Name == "RSP");
        if (rsp == null) return;

        var pid = TargetPid;
        var rspVal = rsp.Value;
        var data = await Task.Run(() => _driver.ReadMemory(pid, rspVal, 256));
        if (data == null) return;

        var moduleList = Modules.ToList();
        var kmodList = KernelModules.ToList();
        var items = new List<string>();
        for (int i = 0; i < data.Length; i += 8)
        {
            if (i + 8 > data.Length) break;
            ulong val = BitConverter.ToUInt64(data, i);
            var annotation = ResolveStackValue(pid, val, moduleList, kmodList);
            if (annotation == null && val != 0)
                annotation = await TryReadStringAtAsync(pid, val);
            items.Add(annotation != null
                ? $"RSP+{i:X2}  {val:X16}  {annotation}"
                : $"RSP+{i:X2}  {val:X16}");
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
        var rsp = Registers.FirstOrDefault(r => r.Name == "RSP");
        var rip = Registers.FirstOrDefault(r => r.Name == "RIP");
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
                ModuleName = _symbols.ResolveAddress(pid, rip.Value, Modules.ToList())
            });
        }

        var stackData = await Task.Run(() => _driver.ReadMemory(pid, rspVal, 2048));
        if (stackData == null) { CallStack.ReplaceAll(csFrames); return; }

        var moduleList = Modules.ToList();
        int frameIdx = 1;
        for (int i = 0; i < stackData.Length && frameIdx < 50; i += 8)
        {
            if (i + 8 > stackData.Length) break;
            ulong val = BitConverter.ToUInt64(stackData, i);
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
                    ModuleName = _symbols.ResolveAddress(pid, val, moduleList) ?? $"0x{val:X}"
                });
            }
        }
        CallStack.ReplaceAll(csFrames);
    }

    public async void RefreshSehChain()
    {
        if (!IsConnected || TargetPid == 0) return;

        var rsp = Registers.FirstOrDefault(r => r.Name == "RSP");
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
        var rspReg = Registers.FirstOrDefault(r => r.Name == "RSP");

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
            for (int i = 0; i < stackData.Length; i += 8)
            {
                if (i + 8 > stackData.Length) break;
                ulong val = BitConverter.ToUInt64(stackData, i);
                stackItems.Add($"RSP+{i:X2}  {val:X16}");
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
        var rip = Registers.FirstOrDefault(r => r.Name == "RIP");
        if (rip == null) return null;
        return Instructions.FirstOrDefault(i => i.Address == rip.Value);
    }

    private static bool IsCallInstruction(string mnemonic)
    {
        return mnemonic.Equals("call", StringComparison.OrdinalIgnoreCase);
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
