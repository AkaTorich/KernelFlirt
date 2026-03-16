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
        Action<string, IReadOnlyList<PluginSectionInfo>> addModuleSections)
    {
        _pluginManager = pluginManager;
        _getIsConnected = getIsConnected;
        _getIsBreakState = getIsBreakState;
        _getTargetPid = getTargetPid;
        _getSelectedThreadId = getSelectedThreadId;
        _getIs32Bit = getIs32Bit;

        Memory = new MemoryApiAdapter(driver);
        Breakpoints = new BreakpointApiAdapter(driver, getBreakpoints);
        Symbols = new SymbolApiAdapter(symbols, getTargetPid, getModules, getKernelModules);
        Process = new ProcessApiAdapter(driver);
        Log = new LogApiAdapter(log);
        UI = new UiApiAdapter(navigateDisasm, addMenuItem, addToolPanel, addUnpackedModule, refreshModulesAndSections, addModuleSections);

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

    public (bool ok, uint oldProtection) ProtectMemory(uint pid, ulong address, uint size, uint newProtection)
        => _driver.ProtectMemory(pid, address, size, newProtection);
}

public class BreakpointApiAdapter : IBreakpointApi
{
    private readonly DriverComm _driver;
    private readonly Func<System.Collections.ObjectModel.ObservableCollection<Breakpoint>> _getBreakpoints;

    public BreakpointApiAdapter(DriverComm driver,
        Func<System.Collections.ObjectModel.ObservableCollection<Breakpoint>> getBreakpoints)
    {
        _driver = driver;
        _getBreakpoints = getBreakpoints;
    }

    public uint? SetBreakpoint(uint pid, uint tid, ulong address, PluginBreakpointType type, uint length = 1) =>
        _driver.SetBreakpoint(pid, tid, address, (BreakpointType)(int)type, length);

    public bool RemoveBreakpoint(uint handle) =>
        _driver.RemoveBreakpoint(handle);

    public IReadOnlyList<PluginBreakpoint> GetAll() =>
        _getBreakpoints().Select(b => new PluginBreakpoint
        {
            Handle = b.Handle,
            Address = b.Address,
            Type = (PluginBreakpointType)(int)b.Type,
            Enabled = b.Enabled,
            Condition = b.Condition,
            HitCount = b.HitCount
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

    public UiApiAdapter(Action<ulong> navigateDisasm, Action<string, Action> addMenuItem,
        Action<string, object> addToolPanel, Action<ulong, string> addUnpackedModule,
        Action refreshModulesAndSections,
        Action<string, IReadOnlyList<PluginSectionInfo>> addModuleSections)
    {
        _navigateDisasm = navigateDisasm;
        _addMenuItem = addMenuItem;
        _addToolPanel = addToolPanel;
        _addUnpackedModule = addUnpackedModule;
        _refreshModulesAndSections = refreshModulesAndSections;
        _addModuleSections = addModuleSections;
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
}
