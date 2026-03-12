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

    public ObservableCollection<Instruction> Instructions { get; } = [];
    public ObservableCollection<Register> Registers { get; } = [];
    public ObservableCollection<ModuleInfo> Modules { get; } = [];
    public ObservableCollection<ThreadInfo> Threads { get; } = [];
    public ObservableCollection<KernelModuleInfo> KernelModules { get; } = [];
    public ObservableCollection<Breakpoint> Breakpoints { get; } = [];
    public ObservableCollection<string> LogMessages { get; } = [];
    public ObservableCollection<string> StackEntries { get; } = [];
    public ObservableCollection<CallStackFrame> CallStack { get; } = [];
    public ObservableCollection<Bookmark> Bookmarks { get; } = [];
    public ObservableCollection<Patch> Patches { get; } = [];
    public ObservableCollection<SehEntry> SehChain { get; } = [];
    public ObservableCollection<SearchResult> SearchResults { get; } = [];
    public ObservableCollection<ImportEntry> Imports { get; } = [];
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
    private void DisconnectKernel()
    {
        _listenerCts?.Cancel();
        _driver.Disconnect();
        _symbols.Reset();
        IsConnected = false;
        IsDebugHookActive = false;
        IsBreakState = false;
        IsRunning = false;
        TargetPid = 0;
        SelectedThreadId = 0;
        KernelModules.Clear();
        Modules.Clear();
        Threads.Clear();
        Registers.Clear();
        Instructions.Clear();
        CallStack.Clear();
        StackEntries.Clear();
        SehChain.Clear();
        Imports.Clear();
        StatusText = "Disconnected";
        Log("Disconnected");
    }

    /// <summary>Called right after a successful connect — loads data that doesn't require a PID.</summary>
    private async Task PostConnectRefreshAsync()
    {
        Log("Loading kernel modules...");
        var mods = await Task.Run(() => _driver.EnumKernelModules());
        KernelModules.Clear();
        foreach (var m in mods) KernelModules.Add(m);
        Log($"Found {mods.Count} kernel modules");

        // Initialize symbol engine and load kernel module symbols
        Log("Initializing symbol engine...");
        var symErr = _symbols.Initialize();
        if (symErr == null)
        {
            Log($"Symbol engine OK, path: {_symbols.SymbolPath}");
            Log($"Loading symbols for {mods.Count} kernel modules...");
            int loaded = 0;
            await Task.Run(() =>
            {
                foreach (var m in mods)
                {
                    if (_symbols.LoadModule(0, m.Name, m.BaseAddress, m.Size))
                        loaded++;
                }
            });
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
        int symLoaded = 0;
        await Task.Run(() =>
        {
            foreach (var m in modules)
                if (_symbols.LoadModule(pid, m.Name, m.BaseAddress, m.Size))
                    symLoaded++;
        });
        Log($"Symbols: {symLoaded}/{modules.Count} user modules loaded");

        Threads.Clear();
        foreach (var t in threads) Threads.Add(t);
        Modules.Clear();
        foreach (var m in modules) Modules.Add(m);

        if (Threads.Count > 0)
            SelectedThreadId = Threads[0].ThreadId;

        // Read registers
        Log("Reading registers...");
        var tid = SelectedThreadId;
        var regs = await Task.Run(() => _driver.ReadRegisters(pid, tid));
        Registers.Clear();
        foreach (var reg in regs) Registers.Add(reg);
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
                var instrs = _disasm.Disassemble(disasmData, disasmAddr);
                Log($"Disassembled {instrs.Count} instructions");
                AnnotateInstructionsWithSymbols(instrs);
                Instructions.Clear();
                foreach (var instr in instrs)
                {
                    instr.HasBreakpoint = Breakpoints.Any(b => b.Address == instr.Address);
                    Instructions.Add(instr);
                }
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
            StackEntries.Clear();
            for (int i = 0; i < stackData.Length; i += 8)
            {
                if (i + 8 > stackData.Length) break;
                ulong val = BitConverter.ToUInt64(stackData, i);
                StackEntries.Add($"RSP+{i:X2}  {val:X16}");
            }
        }

        // Populate call stack
        var csData = callStackTask.Result;
        CallStack.Clear();
        if (rip != null && rip.Value != 0)
        {
            CallStack.Add(new CallStackFrame
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
                    CallStack.Add(new CallStackFrame
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

        StopDebugListener();
        if (IsDebugHookActive)
        {
            await Task.Run(() => _driver.RemoveDebugHook());
            IsDebugHookActive = false;
            Log("Debug hook removed");
        }

        _hitSwBp = null;
        TargetPid = 0;
        SelectedThreadId = 0;
        Instructions.Clear();
        Registers.Clear();
        Modules.Clear();
        Threads.Clear();
        StackEntries.Clear();
        CallStack.Clear();
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

        // If we hit a SW BP, step past it first, then single-step
        if (_hitSwBp != null)
        {
            await StepPastBreakpoint(_hitSwBp);
            _hitSwBp = null;
        }

        await Task.Run(() => _driver.ContinueDebugEvent(DriverComm.CONTINUE_STEP_INTO));
        StartDebugListener();
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

        if (_hitSwBp != null)
        {
            await StepPastBreakpoint(_hitSwBp);
            _hitSwBp = null;
        }

        // Check if current instruction is a CALL — if so, set temp BP after it
        var instr = GetInstructionAtRip();
        if (instr != null && IsCallInstruction(instr.Mnemonic))
        {
            ulong nextAddr = instr.Address + (ulong)instr.Size;
            var tmpHandle = await Task.Run(() => _driver.SetBreakpoint(TargetPid, 0, nextAddr, BreakpointType.Software));
            if (tmpHandle.HasValue)
                _tempBpHandle = tmpHandle.Value;
            await Task.Run(() => _driver.ContinueDebugEvent(DriverComm.CONTINUE_RUN));
        }
        else
        {
            await Task.Run(() => _driver.ContinueDebugEvent(DriverComm.CONTINUE_STEP_INTO));
        }
        StartDebugListener();
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

        if (_hitSwBp != null)
        {
            await StepPastBreakpoint(_hitSwBp);
            _hitSwBp = null;
        }

        IsBreakState = false;
        IsRunning = true;
        StatusText = "Stepping out...";
        await Task.Run(() => _driver.ContinueDebugEvent(DriverComm.CONTINUE_RUN));
        StartDebugListener();
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

            if (_hitSwBp != null)
            {
                await StepPastBreakpoint(_hitSwBp);
                _hitSwBp = null;
            }

            await Task.Run(() => _driver.ContinueDebugEvent(DriverComm.CONTINUE_RUN));
            IsBreakState = false;
            IsRunning = true;
            StatusText = $"Running to {addr:X16}...";
            StartDebugListener();
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

        if (_hitSwBp != null)
        {
            await StepPastBreakpoint(_hitSwBp);
            _hitSwBp = null;
        }

        await Task.Run(() => _driver.ContinueDebugEvent(DriverComm.CONTINUE_RUN));

        IsBreakState = false;
        IsRunning = true;
        StatusText = "Running...";
        Log("Run: target resumed");
        StartDebugListener();
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
        Threads.Clear();
        foreach (var t in threads) Threads.Add(t);
        if (Threads.Count > 0)
            SelectedThreadId = Threads[0].ThreadId;

        var tid = SelectedThreadId;
        var regs = await Task.Run(() => _driver.ReadRegisters(pid, tid));
        Registers.Clear();
        foreach (var reg in regs) Registers.Add(reg);

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

    private async Task StepPastBreakpoint(Breakpoint bp)
    {
        var pid = TargetPid;
        var tid = SelectedThreadId;

        // Remove the BP
        await Task.Run(() => _driver.RemoveBreakpoint(bp.Handle));

        // Single step past
        await Task.Run(() => _driver.ContinueDebugEvent(DriverComm.CONTINUE_STEP_INTO));

        // Wait for step to complete
        var evt = await Task.Run(() => _driver.WaitDebugEvent());

        // Re-arm the BP
        var newHandle = await Task.Run(() => _driver.SetBreakpoint(pid, 0, bp.Address, bp.Type));
        if (newHandle.HasValue)
            bp.Handle = newHandle.Value;
    }

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
                    ModuleName = _symbols.ResolveAddress(TargetPid, address, Modules.ToList())
                };
                Breakpoints.Add(bp);
                Log($"Set {bp.TypeName} breakpoint at {address:X16}");
            }
            else
            {
                Log($"Failed to set {type} BP at {address:X16}");
            }
        }
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

        var handle = await Task.Run(() => _driver.SetBreakpoint(pid, tid, addr, BreakpointType.Software));
        if (handle.HasValue)
        {
            Breakpoints.Add(new Breakpoint
            {
                Handle = handle.Value,
                Address = addr,
                Type = BreakpointType.Software,
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

        var handle = await Task.Run(() => _driver.SetBreakpoint(pid, tid, addr, BreakpointType.Software));
        if (handle.HasValue)
        {
            Breakpoints.Add(new Breakpoint
            {
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

        foreach (var r in results) SearchResults.Add(r);
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

        foreach (var r in results) SearchResults.Add(r);
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

        foreach (var r in results) SearchResults.Add(r);
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
            }
        }
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

        _listenerTask = Task.Run(() =>
        {
            while (!ct.IsCancellationRequested)
            {
                var evt = _driver.WaitDebugEvent();
                if (evt == null) continue;

                Application.Current?.Dispatcher.InvokeAsync(() => OnDebugEvent(evt));
                return; // One event at a time — UI decides what to do next
            }
        }, ct);
    }

    private void StopDebugListener()
    {
        _listenerCts?.Cancel();
        _listenerCts?.Dispose();
        _listenerCts = null;
        _listenerTask = null;
    }

    private void OnDebugEvent(DebugEvent evt)
    {
        TargetPid = evt.ProcessId;
        SelectedThreadId = evt.ThreadId;

        IsBreakState = true;
        IsRunning = false;

        // Clean up temp breakpoint if hit
        if (_tempBpHandle.HasValue)
        {
            var tmpH = _tempBpHandle.Value;
            Task.Run(() => _driver.RemoveBreakpoint(tmpH));
            Breakpoints.Remove(Breakpoints.FirstOrDefault(
                b => b.Handle == tmpH)!);
            _tempBpHandle = null;
        }

        Log($"Break at {evt.AddressHex} (PID={evt.ProcessId} TID={evt.ThreadId})");

        DisasmAddress = evt.Address;

        RefreshRegisters();
        NavigateToRip();
        RefreshDisassembly();
        RefreshStack();
        RefreshCallStack();

        // Track SW breakpoint hit for step-past logic
        var hitBp = Breakpoints.FirstOrDefault(b => b.Address == evt.Address);
        _hitSwBp = hitBp?.Type == BreakpointType.Software ? hitBp : null;

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

    public async void RefreshDisassembly()
    {
        if (!IsConnected || TargetPid == 0) return;
        var addr = DisasmAddress;
        var pid = TargetPid;
        var data = await Task.Run(() => _driver.ReadMemory(pid, addr, 4096));
        if (data == null) return;

        var instrs = _disasm.Disassemble(data, addr);
        AnnotateInstructionsWithSymbols(instrs);
        Instructions.Clear();
        foreach (var instr in instrs)
        {
            instr.HasBreakpoint = Breakpoints.Any(b => b.Address == instr.Address);
            Instructions.Add(instr);
        }
    }

    public async void RefreshRegisters()
    {
        if (!IsConnected || TargetPid == 0 || SelectedThreadId == 0) return;

        var pid = TargetPid;
        var tid = SelectedThreadId;
        var regs = await Task.Run(() => _driver.ReadRegisters(pid, tid));

        var oldRegs = Registers.ToDictionary(r => r.Name, r => r.Value);
        Registers.Clear();
        foreach (var reg in regs)
        {
            if (oldRegs.TryGetValue(reg.Name, out var prev))
                reg.PreviousValue = prev;
            Registers.Add(reg);
        }
    }

    public async void RefreshModules()
    {
        if (!IsConnected || TargetPid == 0) return;
        var pid = TargetPid;
        var mods = await Task.Run(() => _driver.EnumModules(pid));
        Modules.Clear();
        foreach (var m in mods) Modules.Add(m);
        Log($"Found {mods.Count} modules");
    }

    public async void RefreshImports(ulong moduleBase = 0)
    {
        if (!IsConnected || TargetPid == 0) return;

        if (moduleBase == 0)
        {
            var mainMod = Modules.FirstOrDefault();
            if (mainMod == null) return;
            moduleBase = mainMod.BaseAddress;
        }

        var pid = TargetPid;
        var modBase = moduleBase;

        var entries = await Task.Run(() =>
        {
            var result = new List<ImportEntry>();
            try
            {
                var dosHeader = _driver.ReadMemory(pid, modBase + 0x3C, 4);
                if (dosHeader == null) return result;
                uint peOffset = BitConverter.ToUInt32(dosHeader, 0);

                var peData = _driver.ReadMemory(pid, modBase + peOffset, 0x80);
                if (peData == null || peData.Length < 0x80) return result;
                if (peData[0] != 'P' || peData[1] != 'E') return result;

                ushort magic = BitConverter.ToUInt16(peData, 0x18);
                bool is64 = magic == 0x20B;
                int importDirOffset = is64 ? 0x90 : 0x80;

                var headerData = _driver.ReadMemory(pid, modBase + peOffset, (uint)(importDirOffset + 8));
                if (headerData == null || headerData.Length < importDirOffset + 8) return result;

                uint importRva = BitConverter.ToUInt32(headerData, importDirOffset);
                uint importSize = BitConverter.ToUInt32(headerData, importDirOffset + 4);
                if (importRva == 0 || importSize == 0) return result;

                var importTable = _driver.ReadMemory(pid, modBase + importRva, Math.Min(importSize, 8192));
                if (importTable == null) return result;

                int descriptorSize = 20;
                int count = importTable.Length / descriptorSize;

                for (int i = 0; i < count; i++)
                {
                    int off = i * descriptorSize;
                    uint iltRva = BitConverter.ToUInt32(importTable, off);
                    uint nameRva = BitConverter.ToUInt32(importTable, off + 12);
                    uint iatRva = BitConverter.ToUInt32(importTable, off + 16);
                    if (nameRva == 0) break;

                    var nameBytes = _driver.ReadMemory(pid, modBase + nameRva, 256);
                    if (nameBytes == null) continue;
                    int nameLen = Array.IndexOf(nameBytes, (byte)0);
                    if (nameLen < 0) nameLen = nameBytes.Length;
                    string dllName = System.Text.Encoding.ASCII.GetString(nameBytes, 0, nameLen);

                    uint thunkRva = iltRva != 0 ? iltRva : iatRva;
                    int entrySize = is64 ? 8 : 4;

                    var thunks = _driver.ReadMemory(pid, modBase + thunkRva, (uint)(entrySize * 512));
                    if (thunks == null) continue;

                    for (int j = 0; j < 512; j++)
                    {
                        ulong thunkValue = is64
                            ? BitConverter.ToUInt64(thunks, j * 8)
                            : BitConverter.ToUInt32(thunks, j * 4);
                        if (thunkValue == 0) break;

                        var entry = new ImportEntry
                        {
                            Module = dllName,
                            IatAddress = modBase + iatRva + (ulong)(j * entrySize)
                        };

                        var iatData = _driver.ReadMemory(pid, entry.IatAddress, (uint)entrySize);
                        if (iatData != null)
                            entry.ResolvedAddress = is64
                                ? BitConverter.ToUInt64(iatData, 0)
                                : BitConverter.ToUInt32(iatData, 0);

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
                            var hintName = _driver.ReadMemory(pid, modBase + hintNameRva, 256);
                            if (hintName != null && hintName.Length >= 3)
                            {
                                int fnLen = Array.IndexOf(hintName, (byte)0, 2);
                                if (fnLen < 2) fnLen = hintName.Length;
                                entry.Function = System.Text.Encoding.ASCII.GetString(hintName, 2, fnLen - 2);
                            }
                        }

                        result.Add(entry);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Import parse error: {ex.Message}");
            }
            return result;
        });

        Imports.Clear();
        foreach (var e in entries) Imports.Add(e);
        Log($"Found {entries.Count} imports");
    }

    public async void RefreshKernelModules()
    {
        if (!IsConnected) return;
        var mods = await Task.Run(() => _driver.EnumKernelModules());
        KernelModules.Clear();
        foreach (var m in mods) KernelModules.Add(m);
        Log($"Found {mods.Count} kernel modules");
    }

    public async void RefreshThreads()
    {
        if (!IsConnected || TargetPid == 0) return;
        var pid = TargetPid;
        var threads = await Task.Run(() => _driver.EnumThreads(pid));
        Threads.Clear();
        foreach (var t in threads) Threads.Add(t);
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

        StackEntries.Clear();
        for (int i = 0; i < data.Length; i += 8)
        {
            if (i + 8 > data.Length) break;
            ulong val = BitConverter.ToUInt64(data, i);
            StackEntries.Add($"RSP+{i:X2}  {val:X16}");
        }
    }

    public async void RefreshCallStack()
    {
        if (!IsConnected || TargetPid == 0) return;
        var rsp = Registers.FirstOrDefault(r => r.Name == "RSP");
        var rip = Registers.FirstOrDefault(r => r.Name == "RIP");
        if (rsp == null) return;

        var pid = TargetPid;
        var rspVal = rsp.Value;

        CallStack.Clear();

        if (rip != null && rip.Value != 0)
        {
            CallStack.Add(new CallStackFrame
            {
                Index = 0,
                ReturnAddress = rip.Value,
                StackAddress = rspVal,
                ModuleName = _symbols.ResolveAddress(pid, rip.Value, Modules.ToList())
            });
        }

        var stackData = await Task.Run(() => _driver.ReadMemory(pid, rspVal, 2048));
        if (stackData == null) return;

        int frameIdx = 1;
        for (int i = 0; i < stackData.Length && frameIdx < 50; i += 8)
        {
            if (i + 8 > stackData.Length) break;
            ulong val = BitConverter.ToUInt64(stackData, i);
            if (val == 0) continue;

            var mod = Modules.FirstOrDefault(m =>
                val >= m.BaseAddress && val < m.BaseAddress + m.Size);
            if (mod != null)
            {
                CallStack.Add(new CallStackFrame
                {
                    Index = frameIdx++,
                    ReturnAddress = val,
                    StackAddress = rspVal + (ulong)i,
                    ModuleName = $"{mod.Name}+0x{val - mod.BaseAddress:X}"
                });
            }
        }
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

        SehChain.Clear();
        foreach (var e in entries) SehChain.Add(e);
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
            var instrs = _disasm.Disassemble(disasmData, disasmAddr);
            AnnotateInstructionsWithSymbols(instrs);
            Instructions.Clear();
            foreach (var instr in instrs)
            {
                instr.HasBreakpoint = Breakpoints.Any(b => b.Address == instr.Address);
                Instructions.Add(instr);
            }
        }

        var stackData = stackTask.Result;
        if (stackData != null && rspReg != null)
        {
            StackEntries.Clear();
            for (int i = 0; i < stackData.Length; i += 8)
            {
                if (i + 8 > stackData.Length) break;
                ulong val = BitConverter.ToUInt64(stackData, i);
                StackEntries.Add($"RSP+{i:X2}  {val:X16}");
            }
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
