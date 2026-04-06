using System.Windows;
using System.Windows.Controls;
using KernelFlirt.SDK;
using KernelFlirt.UI.Models;

namespace KernelFlirt.UI.Services;

public class DebuggerApiAdapter : IDebuggerApi
{
    private readonly Func<bool> _getIsConnected;
    private readonly Func<bool> _getIsBreakState;
    private readonly Func<uint> _getTargetPid;
    private readonly Func<uint> _getSelectedThreadId;
    private readonly Func<bool> _getIs32Bit;

    /// <summary>When false, all event forwarding from PluginManager is suppressed.</summary>
    public bool Enabled { get; set; } = true;

    public IMemoryApi Memory { get; }
    public IBreakpointApi Breakpoints { get; }
    public ISymbolApi Symbols { get; }
    public IProcessApi Process { get; }
    public ILogApi Log { get; }
    public IUiApi UI { get; }

    public bool IsConnected => _getIsConnected();
    public bool IsBreakState => _getIsBreakState();
    public uint TargetPid => _getTargetPid();
    public uint SelectedThreadId => _getSelectedThreadId();
    public bool Is32Bit => _getIs32Bit();

    public event Action<PluginDebugEvent>? OnDebugEvent;
    public event Action? OnConnected;
    public event Action? OnDisconnected;
    public event Action? OnBreakStateEntered;
    public event Action? OnBreakStateExited;
    public event Action? OnBeforeRun;
    public event Func<PluginDebugEvent, bool>? OnDebugEventFilter;

    private readonly PluginManager _pluginManager;

    public DebuggerApiAdapter(
        DriverComm driver,
        SymbolService symbols,
        PluginManager pluginManager,
        Func<bool> getIsConnected,
        Func<bool> getIsBreakState,
        Func<uint> getTargetPid,
        Func<uint> getSelectedThreadId,
        Func<bool> getIs32Bit,
        Action<string> log,
        Func<System.Collections.ObjectModel.ObservableCollection<Breakpoint>> getBreakpoints,
        Func<RangeObservableCollection<ModuleInfo>> getModules,
        Func<RangeObservableCollection<KernelModuleInfo>> getKernelModules,
        Action<ulong> navigateDisasm,
        Action<string, Action> addMenuItem,
        Action<string, object> addToolPanel,
        Action<ulong, string> addUnpackedModule,
        Action refreshModulesAndSections,
        Action<string, IReadOnlyList<PluginSectionInfo>> addModuleSections,
        Action<ulong> decompileFunction,
        Func<string> getDecompiledCode,
        Action disasmGoBack,
        Action<ulong, string?> setAnnotation,
        Func<ulong, string?> getAnnotation,
        Func<IReadOnlyDictionary<ulong, string>> getAllAnnotations,
        Action refreshDisasm,
        Action<ulong, BreakpointType>? toggleBreakpoint = null)
    {
        _pluginManager = pluginManager;
        _getIsConnected = getIsConnected;
        _getIsBreakState = getIsBreakState;
        _getTargetPid = getTargetPid;
        _getSelectedThreadId = getSelectedThreadId;
        _getIs32Bit = getIs32Bit;

        Memory = new MemoryApiAdapter(driver);
        Breakpoints = new BreakpointApiAdapter(driver, getBreakpoints, toggleBreakpoint);
        Symbols = new SymbolApiAdapter(symbols, getTargetPid, getModules, getKernelModules);
        Process = new ProcessApiAdapter(driver);
        Log = new LogApiAdapter(log);
        UI = new UiApiAdapter(navigateDisasm, addMenuItem, addToolPanel, addUnpackedModule, refreshModulesAndSections, addModuleSections, decompileFunction, getDecompiledCode, disasmGoBack, setAnnotation, getAnnotation, getAllAnnotations, refreshDisasm);

        // Wire events from PluginManager — all gated by Enabled flag
        pluginManager.OnDebugEvent += evt => { if (Enabled) OnDebugEvent?.Invoke(evt); };
        pluginManager.OnConnected += () => { if (Enabled) OnConnected?.Invoke(); };
        pluginManager.OnDisconnected += () => { if (Enabled) OnDisconnected?.Invoke(); };
        pluginManager.OnBreakStateEntered += () => { if (Enabled) OnBreakStateEntered?.Invoke(); };
        pluginManager.OnBreakStateExited += () => { if (Enabled) OnBreakStateExited?.Invoke(); };
        pluginManager.OnBeforeRun += () => { if (Enabled) OnBeforeRun?.Invoke(); };
        pluginManager.OnDebugEventFilter += evt =>
        {
            if (!Enabled) return false;
            var filter = OnDebugEventFilter;
            if (filter == null) return false;
            foreach (var handler in filter.GetInvocationList().Cast<Func<PluginDebugEvent, bool>>())
            {
                try { if (handler(evt)) return true; }
                catch { /* ignore plugin errors */ }
            }
            return false;
        };
    }

    public void Continue()
    {
        _pluginManager.ContinueAction?.Invoke();
    }

    public void SingleStep()
    {
        _pluginManager.SingleStepAction?.Invoke();
    }

    public void StepOver()
    {
        _pluginManager.StepOverAction?.Invoke();
    }

    public void StepOut()
    {
        _pluginManager.StepOutAction?.Invoke();
    }

    public void RunToCursor(ulong address)
    {
        _pluginManager.RunToCursorAction?.Invoke(address);
    }

    public void SkipInstruction()
    {
        _pluginManager.SkipInstructionAction?.Invoke();
    }

    public void Pause()
    {
        _pluginManager.PauseAction?.Invoke();
    }
}

public class MemoryApiAdapter : IMemoryApi
{
    private readonly DriverComm _driver;
    public MemoryApiAdapter(DriverComm driver) => _driver = driver;

    public byte[]? ReadMemory(uint pid, ulong address, uint size) =>
        _driver.ReadMemory(pid, address, size);

    public bool WriteMemory(uint pid, ulong address, byte[] data) =>
        _driver.WriteMemory(pid, address, data);

    public IReadOnlyList<PluginRegister> ReadRegisters(uint pid, uint tid)
    {
        var regs = _driver.ReadRegisters(pid, tid);
        return regs.Select(r => new PluginRegister
        {
            Name = r.Name,
            Value = r.Value,
            IsFlag = r.IsFlag
        }).ToList();
    }

    public bool WriteRip(uint pid, uint tid, ulong newRip) => _driver.WriteRip(pid, tid, newRip);
    public bool WriteRipAndRsp(uint tid, ulong newRip, ulong newRsp) => _driver.WriteRipAndRsp(tid, newRip, newRsp);

    public (bool ok, uint oldProtection) ProtectMemory(uint pid, ulong address, uint size, uint newProtection)
        => _driver.ProtectMemory(pid, address, size, newProtection);
    public ulong AllocateMemory(uint pid, ulong size) => _driver.AllocateMemory(pid, size);
    public bool FreeMemory(uint pid, ulong address) => _driver.FreeMemory(pid, address);
}

public class BreakpointApiAdapter : IBreakpointApi
{
    private readonly DriverComm _driver;
    private readonly Func<System.Collections.ObjectModel.ObservableCollection<Breakpoint>> _getBreakpoints;
    private readonly Action<ulong, BreakpointType>? _toggleBreakpoint;

    public BreakpointApiAdapter(DriverComm driver,
        Func<System.Collections.ObjectModel.ObservableCollection<Breakpoint>> getBreakpoints,
        Action<ulong, BreakpointType>? toggleBreakpoint = null)
    {
        _driver = driver;
        _getBreakpoints = getBreakpoints;
        _toggleBreakpoint = toggleBreakpoint;
    }

    public uint? SetBreakpoint(uint pid, uint tid, ulong address, PluginBreakpointType type, uint length = 1) =>
        _driver.SetBreakpoint(pid, tid, address, (BreakpointType)(int)type, length);

    public bool RemoveBreakpoint(uint handle) =>
        _driver.RemoveBreakpoint(handle);

    public void ToggleBreakpoint(ulong address, PluginBreakpointType type = PluginBreakpointType.Software)
    {
        if (_toggleBreakpoint != null)
            Application.Current.Dispatcher.Invoke(() => _toggleBreakpoint(address, (BreakpointType)(int)type));
    }

    public IReadOnlyList<PluginBreakpoint> GetAll() =>
        _getBreakpoints().Select(b => new PluginBreakpoint
        {
            Handle = b.Handle,
            Address = b.Address,
            Type = (PluginBreakpointType)(int)b.Type,
            Enabled = b.Enabled,
            Condition = b.Condition,
            HitCount = b.HitCount,
            OriginalByte = b.OriginalByte
        }).ToList();
}

public class SymbolApiAdapter : ISymbolApi
{
    private readonly SymbolService _symbols;
    private readonly Func<uint> _getTargetPid;
    private readonly Func<RangeObservableCollection<ModuleInfo>> _getModules;
    private readonly Func<RangeObservableCollection<KernelModuleInfo>> _getKernelModules;

    public SymbolApiAdapter(SymbolService symbols, Func<uint> getTargetPid,
        Func<RangeObservableCollection<ModuleInfo>> getModules,
        Func<RangeObservableCollection<KernelModuleInfo>> getKernelModules)
    {
        _symbols = symbols;
        _getTargetPid = getTargetPid;
        _getModules = getModules;
        _getKernelModules = getKernelModules;
    }

    public string? ResolveAddress(ulong address) =>
        _symbols.ResolveAddress(_getTargetPid(), address, _getModules().ToList());

    public ulong ResolveNameToAddress(string name) =>
        _symbols.ResolveNameToAddress(name);

    public IReadOnlyList<PluginModuleInfo> GetModules() =>
        _getModules().Select(m => new PluginModuleInfo
        {
            BaseAddress = m.BaseAddress,
            Size = m.Size,
            Name = m.Name
        }).ToList();

    public IReadOnlyList<PluginKernelModuleInfo> GetKernelModules() =>
        _getKernelModules().Select(m => new PluginKernelModuleInfo
        {
            BaseAddress = m.BaseAddress,
            Size = m.Size,
            LoadOrder = m.LoadOrder,
            Name = m.Name
        }).ToList();

    public void RegisterFunction(ulong address, string? name, uint size = 0) =>
        _symbols.RegisterFunction(address, name, size);

    public IReadOnlyList<PluginFunctionEntry> GetRegisteredFunctions() =>
        _symbols.GetRegisteredFunctions();
}

public class ProcessApiAdapter : IProcessApi
{
    private readonly DriverComm _driver;
    public ProcessApiAdapter(DriverComm driver) => _driver = driver;

    public IReadOnlyList<PluginProcessInfo> EnumProcesses() =>
        _driver.EnumProcesses().Select(p => new PluginProcessInfo
        {
            ProcessId = p.ProcessId,
            SessionId = p.SessionId,
            Name = p.Name
        }).ToList();

    public IReadOnlyList<PluginThreadInfo> EnumThreads(uint pid) =>
        _driver.EnumThreads(pid).Select(t => new PluginThreadInfo
        {
            ThreadId = t.ThreadId,
            StartAddress = t.StartAddress,
            State = t.State,
            Priority = t.Priority
        }).ToList();

    public bool SuspendThread(uint tid) => _driver.SuspendThread(tid);
    public bool ResumeThread(uint tid) => _driver.ResumeThread(tid);
    public (ulong PebAddress, ulong Peb32Address) GetPebAddress(uint pid) => _driver.GetPebAddress(pid);
    public bool ClearDebugPort(uint pid) => _driver.ClearDebugPort(pid);
    public bool ClearThreadHide(uint pid) => _driver.ClearThreadHide(pid);
    public bool InstallNtQsiHook() => _driver.InstallNtQsiHook();
    public bool RemoveNtQsiHook() => _driver.RemoveNtQsiHook();
    public bool SetSpoofSharedUserData(bool enable) => _driver.SetSpoofSharedUserData(enable);

    public string ProbeNtQsiHook()
    {
        var (ok, address, bytes, status, decodedLen, numInsns, hasRipRel) = _driver.ProbeNtQsi();
        if (!ok) return "Probe IOCTL failed";
        if (status == 1) return "NtQuerySystemInformation not found";
        if (status == 2)
            return $"Decode error at 0x{address:X}: {BitConverter.ToString(bytes, 0, 20).Replace("-", " ")} (decoded {decodedLen} bytes, {numInsns} insns)";

        return $"NtQSI at 0x{address:X}\n" +
               $"Bytes: {BitConverter.ToString(bytes, 0, 20).Replace("-", " ")}\n" +
               $"Decoded: {decodedLen} bytes, {numInsns} insns, RIP-relative: {(hasRipRel ? "YES" : "no")}";
    }
}

public class LogApiAdapter : ILogApi
{
    private readonly Action<string> _log;
    public LogApiAdapter(Action<string> log) => _log = log;

    public void Info(string message) => _log($"[Plugin] {message}");
    public void Warning(string message) => _log($"[Plugin] WARNING: {message}");
    public void Error(string message) => _log($"[Plugin] ERROR: {message}");
}

public class UiApiAdapter : IUiApi
{
    private readonly Action<ulong> _navigateDisasm;
    private readonly Action<string, Action> _addMenuItem;
    private readonly Action<string, object> _addToolPanel;
    private readonly Action<ulong, string> _addUnpackedModule;
    private readonly Action _refreshModulesAndSections;
    private readonly Action<string, IReadOnlyList<PluginSectionInfo>> _addModuleSections;
    private readonly Action<ulong> _decompileFunction;
    private readonly Func<string> _getDecompiledCode;
    private readonly Action _disasmGoBack;
    private readonly Action<ulong, string?> _setAnnotation;
    private readonly Func<ulong, string?> _getAnnotation;
    private readonly Func<IReadOnlyDictionary<ulong, string>> _getAllAnnotations;
    private readonly Action _refreshDisasm;

    public UiApiAdapter(Action<ulong> navigateDisasm, Action<string, Action> addMenuItem,
        Action<string, object> addToolPanel, Action<ulong, string> addUnpackedModule,
        Action refreshModulesAndSections,
        Action<string, IReadOnlyList<PluginSectionInfo>> addModuleSections,
        Action<ulong> decompileFunction,
        Func<string> getDecompiledCode,
        Action disasmGoBack,
        Action<ulong, string?> setAnnotation,
        Func<ulong, string?> getAnnotation,
        Func<IReadOnlyDictionary<ulong, string>> getAllAnnotations,
        Action refreshDisasm)
    {
        _navigateDisasm = navigateDisasm;
        _addMenuItem = addMenuItem;
        _addToolPanel = addToolPanel;
        _addUnpackedModule = addUnpackedModule;
        _refreshModulesAndSections = refreshModulesAndSections;
        _addModuleSections = addModuleSections;
        _decompileFunction = decompileFunction;
        _getDecompiledCode = getDecompiledCode;
        _disasmGoBack = disasmGoBack;
        _setAnnotation = setAnnotation;
        _getAnnotation = getAnnotation;
        _getAllAnnotations = getAllAnnotations;
        _refreshDisasm = refreshDisasm;
    }

    public void NavigateDisassembly(ulong address)
    {
        Application.Current.Dispatcher.Invoke(() => _navigateDisasm(address));
    }

    public void AddMenuItem(string header, Action callback)
    {
        Application.Current.Dispatcher.Invoke(() => _addMenuItem(header, callback));
    }

    public void AddToolPanel(string title, object wpfContent)
    {
        Application.Current.Dispatcher.Invoke(() => _addToolPanel(title, wpfContent));
    }

    public void AddUnpackedModule(ulong peBase, string name)
    {
        Application.Current.Dispatcher.Invoke(() => _addUnpackedModule(peBase, name));
    }

    public void RefreshModulesAndSections()
    {
        Application.Current.Dispatcher.Invoke(() => _refreshModulesAndSections());
    }

    public void AddModuleSections(string moduleName, IReadOnlyList<PluginSectionInfo> sections)
    {
        Application.Current.Dispatcher.Invoke(() => _addModuleSections(moduleName, sections));
    }

    public void DecompileFunction(ulong address)
    {
        Application.Current.Dispatcher.Invoke(() => _decompileFunction(address));
    }

    public string GetDecompiledCode()
    {
        return Application.Current.Dispatcher.Invoke(() => _getDecompiledCode());
    }

    public void DisasmGoBack()
    {
        Application.Current.Dispatcher.Invoke(() => _disasmGoBack());
    }

    public void SetAddressAnnotation(ulong address, string? annotation)
    {
        Application.Current.Dispatcher.Invoke(() => _setAnnotation(address, annotation));
    }

    public string? GetAddressAnnotation(ulong address)
    {
        return Application.Current.Dispatcher.Invoke(() => _getAnnotation(address));
    }

    public IReadOnlyDictionary<ulong, string> GetAllAnnotations()
    {
        return Application.Current.Dispatcher.Invoke(() => _getAllAnnotations());
    }

    public void RefreshDisassembly()
    {
        Application.Current.Dispatcher.Invoke(() => _refreshDisasm());
    }

    // Plugin data store (cross-plugin communication)
    private static readonly Dictionary<string, object?> _pluginData = new();

    public void SetPluginData(string key, object? value)
    {
        lock (_pluginData)
        {
            if (value == null) _pluginData.Remove(key);
            else _pluginData[key] = value;
        }
    }

    public object? GetPluginData(string key)
    {
        lock (_pluginData) { return _pluginData.TryGetValue(key, out var val) ? val : null; }
    }

    public event Action<ulong, string>? OnNoteAdded;
    public event Action<ulong, string>? OnNoteEdited;
    public event Action<ulong>? OnNoteRemoved;

    public void FireNoteAdded(ulong addr, string note) => OnNoteAdded?.Invoke(addr, note);
    public void FireNoteEdited(ulong addr, string note) => OnNoteEdited?.Invoke(addr, note);
    public void FireNoteRemoved(ulong addr) => OnNoteRemoved?.Invoke(addr);
}
