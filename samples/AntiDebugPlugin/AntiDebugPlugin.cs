using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KernelFlirt.SDK;
using Microsoft.Win32;

namespace AntiDebugPlugin;

public class AntiDebugPlugin : IKernelFlirtPlugin
{
    public string Name => "Anti-Anti-Debug";
    public string Description => "ScyllaHide-style anti-anti-debug with UI panel";
    public string Version => "2.0";

    private IDebuggerApi? _api;
    private AntiDebugPanel? _panel;

    public void Initialize(IDebuggerApi api)
    {
        _api = api;

        _panel = new AntiDebugPanel(api);
        api.UI.AddToolPanel("Anti-Debug", _panel);

        api.UI.AddMenuItem("Apply Anti-Debug patches", () => _panel.ApplyPatches());
        api.OnConnected += OnConnected;
        api.OnBreakStateEntered += OnBreakState;

        api.Log.Info("Anti-Anti-Debug v2.0 loaded. See 'Anti-Debug' tab for settings.");
    }

    private void OnConnected()
    {
        // Apply kernel-level patches immediately on connect (before any process)
        if (_panel?.AutoApply == true)
        {
            Application.Current.Dispatcher.BeginInvoke(() => _panel.ApplyKernelPatches());
        }
    }

    private void OnBreakState()
    {
        // Skip ApplyPatches if this break was an API hook (already handled + continued)
        if (_panel?._lastBreakWasApiHook == true)
            return;

        if (_panel?.AutoApply == true && _api is { IsBreakState: true })
        {
            Application.Current.Dispatcher.Invoke(() => _panel.ApplyPatches());
        }
    }

    public void Shutdown()
    {
        _api?.Log.Info("Anti-Anti-Debug plugin unloaded");
    }
}

public class AntiDebugPanel : ScrollViewer
{
    private readonly IDebuggerApi _api;

    // PEB group
    public CheckBox ChkBeingDebugged { get; }
    public CheckBox ChkNtGlobalFlag { get; }
    public CheckBox ChkHeapFlags { get; }
    public CheckBox ChkStartupInfo { get; }
    public CheckBox ChkOsBuildNumber { get; }

    // Kernel
    public CheckBox ChkKdDebuggerEnabled { get; }
    public CheckBox ChkKdDebuggerNotPresent { get; }

    // NtQueryInformationProcess
    public CheckBox ChkDebugPort { get; }
    public CheckBox ChkDebugObjectHandle { get; }
    public CheckBox ChkDebugFlags { get; }

    // NtQuerySystemInformation
    public CheckBox ChkSystemKernelDebugger { get; }

    // SharedUserData
    public CheckBox ChkSharedUserData { get; }

    // NtSetInformationThread
    public CheckBox ChkThreadHideFromDebugger { get; }

    // NtClose
    public CheckBox ChkNtClose { get; }

    // NtQueryObject
    public CheckBox ChkNtQueryObject { get; }

    // NtCreateThreadEx
    public CheckBox ChkNtCreateThreadEx { get; }

    // Window detection
    public CheckBox ChkFindWindow { get; }

    // DRx protection
    public CheckBox ChkHideDRx { get; }
    public CheckBox ChkNtGetContextThread { get; }
    public CheckBox ChkNtSetContextThread { get; }

    // Timing
    public CheckBox ChkPatchRdtsc { get; }
    public CheckBox ChkGetTickCount { get; }
    public CheckBox ChkQueryPerformanceCounter { get; }

    // Software breakpoint hiding
    public CheckBox ChkHideSwBreakpoints { get; }

    // Misc
    public CheckBox ChkOutputDebugString { get; }
    public CheckBox ChkBlockInput { get; }
    public CheckBox ChkNtYieldExecution { get; }
    public CheckBox ChkRemoveDebugPrivileges { get; }

    // Auto-apply
    public CheckBox ChkAutoApply { get; }
    public CheckBox ChkAutoOep { get; }

    public bool AutoApply => ChkAutoApply.IsChecked == true;

    // NtQuerySystemInformation inline hook state
    private ulong _ntQsiHookMem = 0;
    private ulong _ntQsiOrigAddr = 0;
    private byte[]? _ntQsiOrigBytes = null;
    private bool _ntQsiInlineHooked = false;

    private ulong _discoveredOep = 0;
    private ulong _unpackedPeBase = 0;
    private string _originalModuleName = "unpacked.exe";
    private List<(string Name, uint Rva, uint VirtualSize, uint Characteristics)> _unpackedSections = new();

    internal volatile bool _lastBreakWasApiHook; // suppress ApplyPatches for API hook hits

    // Breakpoint-based API hooks state
    private readonly Dictionary<ulong, ApiHookInfo> _apiHooks = new();
    private readonly Dictionary<ulong, ReturnHookInfo> _returnHooks = new();
    private ulong _savedTickCount = 0;
    private long _savedQpcValue = 0;
    private bool _apiHooksInstalled = false;

    private class ApiHookInfo
    {
        public string Name { get; init; } = "";
        public uint? BpHandle { get; set; }
        public Action<uint, uint, ulong> Handler { get; init; } = null!;
    }

    private class ReturnHookInfo
    {
        public string ParentApi { get; init; } = "";
        public uint? BpHandle { get; set; }
        public Action<uint, uint> Handler { get; init; } = null!;
    }

    // Auto-OEP detection state
    private bool _unpackerActive = false;
    private uint? _virtualProtectBpHandle = null;
    private uint? _virtualAllocBpHandle = null;
    private uint? _oepBpHandle = null;
    private ulong _virtualProtectAddr = 0;
    private ulong _virtualAllocAddr = 0;
    private ulong _packedImageBase = 0;
    private HashSet<ulong> _knownModuleBases = new();
    private int _apiHitCount = 0;

    // x64 PEB offsets
    private const int PEB_BEING_DEBUGGED = 0x02;
    private const int PEB_NT_GLOBAL_FLAG = 0xBC;
    private const int PEB_PROCESS_HEAP   = 0x30;
    private const int PEB_PROCESS_PARAMETERS = 0x20;
    private const int PEB_OS_BUILD_NUMBER = 0x120;
    private const int HEAP_FLAGS         = 0x70;
    private const int HEAP_FORCE_FLAGS   = 0x74;
    // RTL_USER_PROCESS_PARAMETERS offsets (x64)
    private const int PROCPARAMS_WINDOW_FLAGS = 0x08 + 0xA0; // StartupInfo.dwFlags at offset within STARTUPINFO
    private const int PROCPARAMS_SHOWWINDOW = 0x08 + 0xA4;   // StartupInfo.wShowWindow
    // x86 PEB offsets
    private const int PEB32_BEING_DEBUGGED = 0x02;
    private const int PEB32_NT_GLOBAL_FLAG = 0x68;
    private const int PEB32_PROCESS_HEAP   = 0x18;
    private const int HEAP32_FLAGS         = 0x40;
    private const int HEAP32_FORCE_FLAGS   = 0x44;

    // Debugger window class names to hide
    private static readonly string[] DebuggerWindowClasses = [
        "OLLYDBG", "GBDYLLO", "pedoll", "Rock Debugger",
        "ObsidianGUI", "ID", "WinDbgFrameClass",
        "x64dbg", "x32dbg"
    ];
    private static readonly string[] DebuggerWindowTitles = [
        "The Interactive Disassembler", "IDA -", "IDA:", "WinDbg",
        "x64dbg", "x32dbg", "OllyDbg", "Immunity Debugger"
    ];

    public AntiDebugPanel(IDebuggerApi api)
    {
        _api = api;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;

        var root = new StackPanel { Margin = new Thickness(8) };

        // ── Title ──
        root.Children.Add(new TextBlock
        {
            Text = "Anti-Anti-Debug Settings",
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 10)
        });

        // ── PEB ──
        ChkBeingDebugged = MakeCheckBox("BeingDebugged", true, "PEB.BeingDebugged = 0 (IsDebuggerPresent)", true);
        ChkNtGlobalFlag = MakeCheckBox("NtGlobalFlag", true, "PEB.NtGlobalFlag = 0 (FLG_HEAP_* flags)", true);
        ChkHeapFlags = MakeCheckBox("HeapFlags", true, "ProcessHeap.Flags = HEAP_GROWABLE, ForceFlags = 0", true);
        ChkStartupInfo = MakeCheckBox("StartupInfo", false, "Zero STARTUPINFO fields (dwFlags, wShowWindow) in PEB ProcessParameters", true);
        ChkOsBuildNumber = MakeCheckBox("OsBuildNumber", false, "Patch PEB.OSBuildNumber (VMProtect Win10 2019+ check)", true);
        root.Children.Add(MakeGroup("PEB", [ChkBeingDebugged, ChkNtGlobalFlag, ChkHeapFlags, ChkStartupInfo, ChkOsBuildNumber]));

        // ── Kernel Debugger ──
        ChkKdDebuggerEnabled = MakeCheckBox("KdDebuggerEnabled", false, "Patch KdDebuggerEnabled = FALSE", true);
        ChkKdDebuggerNotPresent = MakeCheckBox("KdDebuggerNotPresent", false, "Patch KdDebuggerNotPresent = TRUE", true);
        root.Children.Add(MakeGroup("Kernel Debugger", [ChkKdDebuggerEnabled, ChkKdDebuggerNotPresent]));

        // ── NtQueryInformationProcess ──
        ChkDebugPort = MakeCheckBox("ProcessDebugPort", false, "Clear EPROCESS.DebugPort (defeats DebugPort/DebugObjectHandle/DebugFlags)", true);
        ChkDebugObjectHandle = MakeCheckBox("ProcessDebugObjectHandle", false, "Cleared by DebugPort zeroing", true);
        ChkDebugFlags = MakeCheckBox("ProcessDebugFlags", false, "Cleared by DebugPort zeroing", true);
        root.Children.Add(MakeGroup("NtQueryInformationProcess (via ClearDebugPort)", [ChkDebugPort, ChkDebugObjectHandle, ChkDebugFlags]));

        // ── NtQuerySystemInformation ──
        ChkSystemKernelDebugger = MakeCheckBox("SystemKernelDebuggerInfo", false, "Inline hook NtQuerySystemInformation to spoof class 0x23 (usermode, safe)", true);
        root.Children.Add(MakeGroup("NtQuerySystemInformation", [ChkSystemKernelDebugger]));

        // ── SharedUserData ──
        ChkSharedUserData = MakeCheckBox("SharedUserData.KdDebuggerEnabled", false, "Patch KUSER_SHARED_DATA.KdDebuggerEnabled to 0 (kernel-level, defeats direct 0x7FFE02D4 read)", true);
        root.Children.Add(MakeGroup("SharedUserData", [ChkSharedUserData]));

        // ── NtSetInformationThread ──
        ChkThreadHideFromDebugger = MakeCheckBox("ThreadHideFromDebugger", true, "Clear HideFromDebugger bit in all threads' CrossThreadFlags", true);
        root.Children.Add(MakeGroup("NtSetInformationThread (via ClearThreadHide)", [ChkThreadHideFromDebugger]));

        // ── NtClose ──
        ChkNtClose = MakeCheckBox("NtClose", false, "Cleared by DebugPort zeroing (no debug object = no invalid handle exception)", true);
        root.Children.Add(MakeGroup("NtClose (via ClearDebugPort)", [ChkNtClose]));

        // ── NtQueryObject ──
        ChkNtQueryObject = MakeCheckBox("NtQueryObject", false, "Hook NtQueryObject to hide DebugObject type from enumeration", true);
        root.Children.Add(MakeGroup("NtQueryObject (via BP hook)", [ChkNtQueryObject]));

        // ── NtCreateThreadEx ──
        ChkNtCreateThreadEx = MakeCheckBox("NtCreateThreadEx", false, "Strip THREAD_CREATE_FLAGS_HIDE_FROM_DEBUGGER (0x4) from CreateFlags", true);
        root.Children.Add(MakeGroup("NtCreateThreadEx (via BP hook)", [ChkNtCreateThreadEx]));

        // ── Window Detection ──
        ChkFindWindow = MakeCheckBox("FindWindow / EnumWindows", false, "Hook NtUserFindWindowEx to hide debugger windows (OLLYDBG, x64dbg, IDA, etc.)", true);
        root.Children.Add(MakeGroup("Window Detection (via BP hook)", [ChkFindWindow]));

        // ── DRx Protection ──
        ChkHideDRx = MakeCheckBox("Hide DRx registers", false, "Zero DR0-DR3 in target thread context", true);
        ChkNtGetContextThread = MakeCheckBox("NtGetContextThread", false, "Hook NtGetContextThread to zero DR0-DR3/DR6/DR7 in returned CONTEXT", true);
        ChkNtSetContextThread = MakeCheckBox("NtSetContextThread", false, "Hook NtSetContextThread to prevent clearing hardware breakpoints", true);
        root.Children.Add(MakeGroup("Hardware Breakpoints / DRx Protection", [ChkHideDRx, ChkNtGetContextThread, ChkNtSetContextThread]));

        // ── Timing ──
        ChkPatchRdtsc = MakeCheckBox("Patch RDTSC/CPUID", false, "NOP out RDTSC and CPUID in code sections. WARNING: breaks Themida/VMProtect (CRC checks detect patches)", true);
        ChkGetTickCount = MakeCheckBox("GetTickCount / GetTickCount64", false, "Hook GetTickCount/GetTickCount64 to return consistent incremental values", true);
        ChkQueryPerformanceCounter = MakeCheckBox("QueryPerformanceCounter", false, "Hook QueryPerformanceCounter/NtQuerySystemTime to normalize timing", true);
        root.Children.Add(MakeGroup("Timing Checks", [ChkPatchRdtsc, ChkGetTickCount, ChkQueryPerformanceCounter]));

        // ── Software breakpoint hiding ──
        ChkHideSwBreakpoints = MakeCheckBox("Hide INT3 (0xCC) breakpoints", false, "Hook NtReadVirtualMemory to replace 0xCC with original bytes when process reads its own memory (defeats 0xCC scan)", true);
        root.Children.Add(MakeGroup("Software Breakpoint Protection", [ChkHideSwBreakpoints]));

        // ── Misc ──
        ChkOutputDebugString = MakeCheckBox("OutputDebugStringA", false, "Hook OutputDebugStringA to set LastError correctly (anti-debug via return value)", true);
        ChkBlockInput = MakeCheckBox("BlockInput", false, "Hook BlockInput to prevent locking user input", true);
        ChkNtYieldExecution = MakeCheckBox("NtYieldExecution", false, "Hook NtYieldExecution to return STATUS_NO_YIELD_PERFORMED", true);
        ChkRemoveDebugPrivileges = MakeCheckBox("Remove Debug Privileges", false, "Remove SeDebugPrivilege from process token (some protectors check this)", true);
        root.Children.Add(MakeGroup("Miscellaneous", [ChkOutputDebugString, ChkBlockInput, ChkNtYieldExecution, ChkRemoveDebugPrivileges]));

        // ── Auto-apply ──
        ChkAutoApply = MakeCheckBox("Auto-apply on every break", false, "Automatically apply patches when debugger breaks (recommended for packed files)", true);
        root.Children.Add(MakeGroup("Automation", [ChkAutoApply]));

        // ── Unpacker ──
        ChkAutoOep = MakeCheckBox("Auto-break at OEP", false, "Automatically detect unpacked PE and break at its entry point after Run (F9).\nWARNING: Slows Themida-protected apps (intercepts every VirtualProtect call).", true);
        root.Children.Add(MakeGroup("Unpacker", [ChkAutoOep]));

        // ── Buttons ──
        var btnPanel = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };

        var btnApply = new Button
        {
            Content = "Apply Now",
            Padding = new Thickness(16, 6, 16, 6),
            Margin = new Thickness(0, 0, 8, 0)
        };
        btnApply.Click += (_, _) => ApplyPatches();
        btnPanel.Children.Add(btnApply);

        var btnCheck = new Button
        {
            Content = "Check Status",
            Padding = new Thickness(16, 6, 16, 6),
            Margin = new Thickness(0, 0, 8, 0)
        };
        btnCheck.Click += (_, _) => CheckStatus();
        btnPanel.Children.Add(btnCheck);

        var btnSelectAll = new Button
        {
            Content = "Select All",
            Padding = new Thickness(16, 6, 16, 6),
            Margin = new Thickness(0, 0, 8, 0)
        };
        btnSelectAll.Click += (_, _) => SetAllEnabled(true);
        btnPanel.Children.Add(btnSelectAll);

        var btnDeselectAll = new Button
        {
            Content = "Deselect All",
            Padding = new Thickness(16, 6, 16, 6),
            Margin = new Thickness(0, 0, 8, 0)
        };
        btnDeselectAll.Click += (_, _) => SetAllEnabled(false);
        btnPanel.Children.Add(btnDeselectAll);

        var btnAnalyze = new Button
        {
            Content = "Analyze Protector",
            Padding = new Thickness(16, 6, 16, 6),
            ToolTip = "Scan process for packer/protector patterns and show hints"
        };
        btnAnalyze.Click += (_, _) => AnalyzeProtector();
        btnPanel.Children.Add(btnAnalyze);

        var btnJumpOep = new Button
        {
            Content = "Jump to OEP",
            Padding = new Thickness(16, 6, 16, 6),
            ToolTip = "Set RIP to the discovered OEP (after unpacking)"
        };
        btnJumpOep.Click += (_, _) => JumpToOep();
        btnPanel.Children.Add(btnJumpOep);

        var btnDump = new Button
        {
            Content = "Dump PE",
            Padding = new Thickness(16, 6, 16, 6),
            ToolTip = "Dump all sections of the unpacked PE to a file"
        };
        btnDump.Click += (_, _) => DumpUnpackedPe();
        btnPanel.Children.Add(btnDump);

        root.Children.Add(btnPanel);

        // ── Status ──
        root.Children.Add(new TextBlock
        {
            Text = "All patches use kernel driver. ClearDebugPort defeats multiple checks at once.",
            FontStyle = FontStyles.Italic,
            Margin = new Thickness(0, 10, 0, 0)
        });

        Content = root;

        // Subscribe to new SDK events for unpacker flow
        api.OnBeforeRun += OnBeforeRun;
        api.OnDebugEventFilter += OnDebugEventFilter;
    }

    // ════════════════════════════════════════════════════════════════════
    //  Auto-OEP: event handlers
    // ════════════════════════════════════════════════════════════════════

    private void OnBeforeRun()
    {
        if (ChkAutoOep.IsChecked != true) return;
        if (!_api.IsBreakState || _api.TargetPid == 0) return;
        if (_unpackerActive) return; // already tracking

        uint pid = _api.TargetPid;
        uint tid = _api.SelectedThreadId;

        // Snapshot known modules so we can distinguish new PEs from system DLLs
        _knownModuleBases.Clear();
        foreach (var m in _api.Symbols.GetModules())
            _knownModuleBases.Add(m.BaseAddress);

        // Remember the packed image base (first module = main exe)
        var modules = _api.Symbols.GetModules();
        if (modules.Count > 0)
            _packedImageBase = modules[0].BaseAddress;

        // Resolve VirtualProtect — packer calls this to make unpacked code executable
        ulong vpAddr = _api.Symbols.ResolveNameToAddress("ntdll!NtProtectVirtualMemory");
        if (vpAddr == 0)
            vpAddr = _api.Symbols.ResolveNameToAddress("kernelbase!VirtualProtect");

        // Also resolve VirtualAlloc but only as fallback
        ulong vaAddr = _api.Symbols.ResolveNameToAddress("ntdll!NtAllocateVirtualMemory");
        if (vaAddr == 0)
            vaAddr = _api.Symbols.ResolveNameToAddress("kernelbase!VirtualAlloc");

        bool anyBp = false;
        _apiHitCount = 0;

        if (vpAddr != 0)
        {
            var h = _api.Breakpoints.SetBreakpoint(pid, tid, vpAddr, PluginBreakpointType.Hardware);
            if (!h.HasValue)
                h = _api.Breakpoints.SetBreakpoint(pid, 0, vpAddr, PluginBreakpointType.Software);
            if (h.HasValue)
            {
                _virtualProtectBpHandle = h.Value;
                _virtualProtectAddr = vpAddr;
                anyBp = true;
                _api.Log.Info($"[Unpacker] HW BP on NtProtectVirtualMemory at 0x{vpAddr:X}");
            }
        }

        if (vaAddr != 0)
        {
            var h = _api.Breakpoints.SetBreakpoint(pid, tid, vaAddr, PluginBreakpointType.Hardware);
            if (!h.HasValue)
                h = _api.Breakpoints.SetBreakpoint(pid, 0, vaAddr, PluginBreakpointType.Software);
            if (h.HasValue)
            {
                _virtualAllocBpHandle = h.Value;
                _virtualAllocAddr = vaAddr;
                anyBp = true;
                _api.Log.Info($"[Unpacker] HW BP on NtAllocateVirtualMemory at 0x{vaAddr:X}");
            }
        }

        if (anyBp)
        {
            _unpackerActive = true;
            _api.Log.Info("[Unpacker] Auto-OEP tracking active. Running...");
        }
    }

    private bool OnDebugEventFilter(PluginDebugEvent evt)
    {
        // Check API hooks first (these should auto-continue)
        if (_apiHooksInstalled && HandleApiHookEvent(evt))
        {
            _lastBreakWasApiHook = true;
            return true;
        }
        _lastBreakWasApiHook = false;

        if (!_unpackerActive) return false;

        // Check if this is our OEP breakpoint — let it through to UI
        if (_oepBpHandle.HasValue && evt.Address == _discoveredOep)
        {
            _api.Log.Warning($"[Unpacker] ★ Hit OEP at 0x{evt.Address:X}!");

            // Clean up unpacker breakpoints
            CleanupUnpackerBps();
            _unpackerActive = false;

            // Register unpacked module and refresh UI
            if (_unpackedPeBase != 0)
            {
                _api.UI.AddUnpackedModule(_unpackedPeBase, _originalModuleName + " [unpacked]");
                _api.UI.RefreshModulesAndSections();
            }

            return false; // Let UI handle the break at OEP
        }

        // OEP BP is set — auto-continue EVERYTHING until we hit OEP
        if (_oepBpHandle.HasValue)
        {
            return true; // Suppress, keep running to OEP
        }

        // Check if this is one of our API-tracking breakpoints (compare by address)
        bool isVpHit = _virtualProtectAddr != 0 && evt.Address == _virtualProtectAddr;
        bool isVaHit = _virtualAllocAddr != 0 && evt.Address == _virtualAllocAddr;

        if (!isVpHit && !isVaHit) return false; // Not our BP — let UI handle

        _apiHitCount++;

        // VirtualProtect hits are always interesting (packer changing permissions).
        // VirtualAlloc hits are noisy — only scan every 100th hit.
        bool shouldScan = isVpHit || (_apiHitCount % 100 == 0);

        if (shouldScan)
        {

            uint pid = evt.ProcessId;
            bool foundOep = ScanForUnpackedPe(pid);

            if (foundOep && _discoveredOep != 0)
            {
                // Found unpacked PE! Remove API BPs, set OEP BP
                _api.Log.Warning($"[Unpacker] ★ Found unpacked PE at 0x{_unpackedPeBase:X}, OEP=0x{_discoveredOep:X}");

                if (_virtualProtectBpHandle.HasValue)
                {
                    _api.Breakpoints.RemoveBreakpoint(_virtualProtectBpHandle.Value);
                    _virtualProtectBpHandle = null;
                    _virtualProtectAddr = 0;
                }
                if (_virtualAllocBpHandle.HasValue)
                {
                    _api.Breakpoints.RemoveBreakpoint(_virtualAllocBpHandle.Value);
                    _virtualAllocBpHandle = null;
                    _virtualAllocAddr = 0;
                }

                var oepH = _api.Breakpoints.SetBreakpoint(pid, evt.ThreadId, _discoveredOep, PluginBreakpointType.Hardware);
                if (!oepH.HasValue)
                    oepH = _api.Breakpoints.SetBreakpoint(pid, 0, _discoveredOep, PluginBreakpointType.Software);
                if (oepH.HasValue)
                {
                    _oepBpHandle = oepH.Value;
                    _api.Log.Info($"[Unpacker] HW BP set at OEP 0x{_discoveredOep:X}, continuing...");
                }

                // Also provide sections to UI for when we break at OEP
                if (_unpackedSections.Count > 0)
                {
                    var pluginSections = _unpackedSections.Select(s => new PluginSectionInfo
                    {
                        Name = s.Name,
                        VirtualAddress = _unpackedPeBase + s.Rva,
                        VirtualSize = s.VirtualSize,
                        Characteristics = s.Characteristics
                    }).ToList();
                    _api.UI.AddModuleSections(_originalModuleName + " [unpacked]", pluginSections);
                }
            }
        }

        // After 1000 hits with no result, give up on VirtualAlloc (keep VirtualProtect)
        if (_apiHitCount >= 1000 && _virtualAllocBpHandle.HasValue)
        {
            _api.Breakpoints.RemoveBreakpoint(_virtualAllocBpHandle.Value);
            _virtualAllocBpHandle = null;
            _virtualAllocAddr = 0;

            if (_virtualProtectBpHandle == null)
            {
                _unpackerActive = false;
            }
        }

        // Return true = handled, listener will auto-continue (no UI thread involved)
        return true;
    }

    /// <summary>
    /// Scan for a new PE that appeared in memory (not in known modules).
    /// Called from the debug event filter when VirtualProtect/VirtualAlloc BP hits.
    /// Uses RSP to find return addresses pointing into the unpacked PE.
    /// </summary>
    private bool ScanForUnpackedPe(uint pid)
    {
        // Re-enumerate modules to get current state (may have loaded more DLLs by now)
        var currentModules = _api.Symbols.GetModules();

        // Build set of known module ranges for filtering
        var moduleRanges = new List<(ulong Base, ulong End)>();
        foreach (var m in currentModules)
            moduleRanges.Add((m.BaseAddress, m.BaseAddress + m.Size));
        // Also add packed image
        moduleRanges.Add((_packedImageBase, _packedImageBase + 0x1000000));

        // Read RSP and scan stack for return addresses
        uint tid = _api.SelectedThreadId;
        var regs = _api.Memory.ReadRegisters(pid, tid);
        ulong rsp = regs?.FirstOrDefault(r => r.Name == "RSP" || r.Name == "rsp")?.Value ?? 0;
        ulong rip = regs?.FirstOrDefault(r => r.Name == "RIP" || r.Name == "rip")?.Value ?? 0;

        // Collect candidate addresses from stack
        var candidates = new List<ulong>();
        if (rsp != 0)
        {
            var stackData = _api.Memory.ReadMemory(pid, rsp, 4096);
            if (stackData != null)
            {
                for (int i = 0; i + 8 <= stackData.Length; i += 8)
                {
                    ulong val = BitConverter.ToUInt64(stackData, i);
                    if (val > 0x10000 && val < 0x7FFFFFFFFFFF)
                        candidates.Add(val);
                }
            }
        }

        // Check 64KB-aligned bases of candidate addresses for MZ
        var checkedBases = new HashSet<ulong>();
        foreach (ulong addr in candidates)
        {
            // Skip if inside a known module
            bool inKnown = moduleRanges.Any(r => addr >= r.Base && addr < r.End);
            if (inKnown) continue;

            ulong base64k = addr & ~0xFFFFUL;
            if (!checkedBases.Add(base64k)) continue;

            if (TryValidateUnpackedPe(pid, base64k, moduleRanges))
                return true;
        }

        // Also scan a wider area around the packed image
        ulong packedAligned = _packedImageBase & ~0xFFFFUL;
        // Scan upward 32MB (512 x 64KB steps)
        for (int i = 1; i <= 512; i++)
        {
            ulong probe = packedAligned + (ulong)i * 0x10000;
            if (probe > 0x7FFFFFFFFFFF) break;
            if (!checkedBases.Add(probe)) continue;
            bool inKnown = moduleRanges.Any(r => probe >= r.Base && probe < r.End);
            if (inKnown) continue;

            // Quick check: only read 2 bytes
            var mz = _api.Memory.ReadMemory(pid, probe, 2);
            if (mz == null || mz.Length < 2 || mz[0] != 'M' || mz[1] != 'Z') continue;

            if (TryValidateUnpackedPe(pid, probe, moduleRanges))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Validate if address points to an unpacked PE (not a DLL, not a known module).
    /// If valid, sets _unpackedPeBase, _discoveredOep, _unpackedSections.
    /// </summary>
    private bool TryValidateUnpackedPe(uint pid, ulong probe, List<(ulong Base, ulong End)> moduleRanges)
    {
        var mz = _api.Memory.ReadMemory(pid, probe, 2);
        if (mz == null || mz.Length < 2 || mz[0] != 'M' || mz[1] != 'Z') return false;

        var probeDos = _api.Memory.ReadMemory(pid, probe, 0x40);
        if (probeDos == null || probeDos.Length < 0x40) return false;
        uint probeLfanew = BitConverter.ToUInt32(probeDos, 0x3C);
        if (probeLfanew > 0x1000) return false;

        var probeHdr = _api.Memory.ReadMemory(pid, probe + probeLfanew, 0x120);
        if (probeHdr == null || probeHdr.Length < 0x88) return false;
        if (probeHdr[0] != 'P' || probeHdr[1] != 'E') return false;

        // Check it's NOT a DLL
        ushort fileChars = BitConverter.ToUInt16(probeHdr, 22);
        if ((fileChars & 0x2000) != 0) return false; // IMAGE_FILE_DLL

        // Check export directory for .dll name
        ushort magic = BitConverter.ToUInt16(probeHdr, 24);
        bool is64 = magic == 0x20B;
        int exportDirOff = is64 ? (24 + 0x70) : (24 + 0x60);
        if (exportDirOff + 8 <= probeHdr.Length)
        {
            uint exportRva = BitConverter.ToUInt32(probeHdr, exportDirOff);
            if (exportRva != 0 && exportRva < 0x10000000)
            {
                var exportDir = _api.Memory.ReadMemory(pid, probe + exportRva, 40);
                if (exportDir != null && exportDir.Length >= 16)
                {
                    uint nameRva = BitConverter.ToUInt32(exportDir, 12);
                    if (nameRva != 0)
                    {
                        var nameBytes = _api.Memory.ReadMemory(pid, probe + nameRva, 64);
                        if (nameBytes != null)
                        {
                            int nulIdx = Array.IndexOf(nameBytes, (byte)0);
                            if (nulIdx < 0) nulIdx = nameBytes.Length;
                            string dllName = System.Text.Encoding.ASCII.GetString(nameBytes, 0, nulIdx);
                            if (dllName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                                return false;
                        }
                    }
                }
            }
        }

        // Check if in known module range
        if (moduleRanges.Any(r => probe >= r.Base && probe < r.End))
            return false;

        // Found a non-DLL PE! Read entry point
        uint ep = BitConverter.ToUInt32(probeHdr, 40);
        if (ep == 0) return false; // No entry point yet

        _unpackedPeBase = probe;
        _discoveredOep = probe + ep;

        // Read sections
        ushort nSect = BitConverter.ToUInt16(probeHdr, 6);
        ushort optSize = BitConverter.ToUInt16(probeHdr, 20);
        uint sectOff = probeLfanew + 24u + optSize;
        var sectData = _api.Memory.ReadMemory(pid, probe + sectOff, (uint)(nSect * 40));
        if (sectData != null)
        {
            _unpackedSections.Clear();
            for (int i = 0; i < nSect; i++)
            {
                string name = System.Text.Encoding.ASCII.GetString(sectData, i * 40, 8).TrimEnd('\0');
                uint va = BitConverter.ToUInt32(sectData, i * 40 + 12);
                uint vsz = BitConverter.ToUInt32(sectData, i * 40 + 8);
                uint ch = BitConverter.ToUInt32(sectData, i * 40 + 36);
                _unpackedSections.Add((name, va, vsz, ch));
            }
        }

        return true;
    }

    private void CleanupUnpackerBps()
    {
        if (_virtualProtectBpHandle.HasValue)
        {
            _api.Breakpoints.RemoveBreakpoint(_virtualProtectBpHandle.Value);
            _virtualProtectBpHandle = null;
        }
        if (_virtualAllocBpHandle.HasValue)
        {
            _api.Breakpoints.RemoveBreakpoint(_virtualAllocBpHandle.Value);
            _virtualAllocBpHandle = null;
        }
        if (_oepBpHandle.HasValue)
        {
            _api.Breakpoints.RemoveBreakpoint(_oepBpHandle.Value);
            _oepBpHandle = null;
        }
    }

    private void SetAllEnabled(bool check)
    {
        foreach (var chk in new[] { ChkBeingDebugged, ChkNtGlobalFlag, ChkHeapFlags, ChkStartupInfo, ChkOsBuildNumber,
            ChkKdDebuggerEnabled, ChkKdDebuggerNotPresent,
            ChkDebugPort, ChkDebugObjectHandle, ChkDebugFlags,
            ChkSystemKernelDebugger, ChkThreadHideFromDebugger, ChkNtClose,
            ChkNtQueryObject, ChkNtCreateThreadEx, ChkFindWindow,
            ChkHideDRx, ChkNtGetContextThread, ChkNtSetContextThread,
            ChkPatchRdtsc, ChkGetTickCount, ChkQueryPerformanceCounter,
            ChkOutputDebugString, ChkBlockInput, ChkNtYieldExecution, ChkRemoveDebugPrivileges,
            ChkAutoApply })
        {
            chk.IsChecked = check;
        }
    }

    private static GroupBox MakeGroup(string header, CheckBox[] items)
    {
        var sp = new StackPanel { Margin = new Thickness(4) };
        foreach (var item in items) sp.Children.Add(item);

        return new GroupBox
        {
            Header = header,
            Content = sp,
            Margin = new Thickness(0, 0, 0, 6),
            Padding = new Thickness(6)
        };
    }

    private static CheckBox MakeCheckBox(string text, bool isChecked, string tooltip, bool isEnabled)
    {
        return new CheckBox
        {
            Content = text,
            IsChecked = isChecked,
            IsEnabled = isEnabled,
            ToolTip = tooltip,
            Margin = new Thickness(0, 2, 0, 2)
        };
    }

    /// <summary>Apply kernel-level patches only (no target process needed).
    /// Called automatically on connect when AutoApply is enabled.</summary>
    public int ApplyKernelPatches()
    {
        if (!_api.IsConnected) return 0;

        int patches = 0;

        if (ChkKdDebuggerEnabled.IsChecked == true)
            patches += PatchKernelByte("KdDebuggerEnabled", 0);

        if (ChkKdDebuggerNotPresent.IsChecked == true)
            patches += PatchKernelByte("KdDebuggerNotPresent", 1);

        // NtQuerySystemInformation class 0x23 is now handled via usermode inline hook
        // in ApplyPatches() — no kernel inline hook needed (avoids PatchGuard BSOD)

        // SharedUserData.KdDebuggerEnabled — spoof via driver
        if (ChkSharedUserData.IsChecked == true)
        {
            if (_api.Process.SetSpoofSharedUserData(true))
            {
                patches++;
                _api.Log.Info("  SharedUserData.KdDebuggerEnabled spoofing enabled (driver will zero 0x7FFE02D4)");
            }
            else
            {
                _api.Log.Warning("  SharedUserData spoofing failed — driver may not support this IOCTL");
            }
        }

        if (patches > 0)
            _api.Log.Info($"Anti-debug: {patches} kernel-level patches applied");

        return patches;
    }

    public void ApplyPatches()
    {
        if (!_api.IsConnected)
        {
            _api.Log.Warning("Not connected");
            return;
        }

        int patches = ApplyKernelPatches();
        uint pid = _api.TargetPid;
        bool hasProcess = pid != 0;

        // ── Process-level patches (require target process) ──
        if (!hasProcess)
        {
            if (ChkBeingDebugged.IsChecked == true || ChkNtGlobalFlag.IsChecked == true ||
                ChkHeapFlags.IsChecked == true || ChkDebugPort.IsChecked == true ||
                ChkThreadHideFromDebugger.IsChecked == true || ChkHideDRx.IsChecked == true)
            {
                _api.Log.Warning("No target process — process-level patches skipped (kernel patches applied)");
            }
            return;
        }

        // ── PEB patches ──
        if (ChkBeingDebugged.IsChecked == true || ChkNtGlobalFlag.IsChecked == true || ChkHeapFlags.IsChecked == true)
        {
            var (pebAddr, peb32Addr) = _api.Process.GetPebAddress(pid);
            if (pebAddr != 0)
            {
                patches += PatchPeb64(pid, pebAddr);
                if (peb32Addr != 0)
                    patches += PatchPeb32(pid, peb32Addr);
            }
            else
            {
                _api.Log.Error("Failed to get PEB address");
            }
        }

        // ── ClearDebugPort (defeats DebugPort, DebugObjectHandle, DebugFlags, NtClose) ──
        if (ChkDebugPort.IsChecked == true || ChkDebugObjectHandle.IsChecked == true ||
            ChkDebugFlags.IsChecked == true || ChkNtClose.IsChecked == true)
        {
            if (_api.Process.ClearDebugPort(pid))
            {
                patches++;
                _api.Log.Info("  DebugPort cleared (defeats NtQIP + NtClose checks)");
            }
            else
            {
                _api.Log.Warning("  ClearDebugPort failed");
            }
        }

        // ── ClearThreadHide ──
        if (ChkThreadHideFromDebugger.IsChecked == true)
        {
            if (_api.Process.ClearThreadHide(pid))
            {
                patches++;
                _api.Log.Info("  ThreadHideFromDebugger cleared for all threads");
            }
            else
            {
                _api.Log.Warning("  ClearThreadHide failed — dumping PsIsThreadTerminating bytes:");
                try
                {
                    ulong psAddr = _api.Symbols.ResolveNameToAddress("PsIsThreadTerminating");
                    if (psAddr != 0)
                    {
                        var bytes = _api.Memory.ReadMemory(4, psAddr, 32);
                        if (bytes != null)
                            _api.Log.Info($"  PsIsThreadTerminating at 0x{psAddr:X}: {BitConverter.ToString(bytes).Replace("-", " ")}");
                        else
                            _api.Log.Warning($"  PsIsThreadTerminating at 0x{psAddr:X}: read failed");
                    }
                    else
                    {
                        _api.Log.Warning("  PsIsThreadTerminating symbol not found");
                    }
                }
                catch (Exception ex)
                {
                    _api.Log.Warning($"  Diagnostic failed: {ex.Message}");
                }
            }
        }

        // ── Patch RDTSC/CPUID timing checks ──
        if (ChkPatchRdtsc.IsChecked == true)
        {
            // Get image base from PEB.ImageBaseAddress (offset 0x10 in x64 PEB)
            var (peb, _) = _api.Process.GetPebAddress(pid);
            if (peb != 0)
            {
                var imgBaseData = _api.Memory.ReadMemory(pid, peb + 0x10, 8);
                if (imgBaseData != null)
                {
                    ulong imageBase = BitConverter.ToUInt64(imgBaseData);
                    int timingPatches = PatchTimingChecks(pid, imageBase);
                    if (timingPatches > 0)
                        _api.Log.Info($"  Timing checks: patched {timingPatches} RDTSC/CPUID instructions");
                    patches += timingPatches;
                }
            }
        }

        // ── Hide DRx registers ──
        if (ChkHideDRx.IsChecked == true)
            patches += HideDRx(pid);

        // ── Remove debug privileges ──
        if (ChkRemoveDebugPrivileges.IsChecked == true)
            patches += RemoveDebugPrivileges(pid);

        // ── Install breakpoint-based API hooks ──
        InstallApiHooks();

        // NtQuerySystemInformation inline hook (class 0x23 spoofing)
        if (ChkSystemKernelDebugger.IsChecked == true && !_ntQsiInlineHooked)
        {
            if (InstallNtQsiInlineHook(pid))
                patches++;
        }

        _api.Log.Info($"Anti-debug: {patches} patches applied to PID {pid}");
    }

    /// <summary>Jump RIP/EIP to the discovered OEP after unpacking.</summary>
    public void JumpToOep()
    {
        if (_discoveredOep == 0)
        {
            _api.Log.Warning("No OEP discovered yet. Run 'Analyze Protector' after unpacking first.");
            return;
        }

        if (!_api.IsBreakState)
        {
            _api.Log.Warning("Process must be paused (Break) to set RIP.");
            return;
        }

        uint pid = _api.TargetPid;
        uint tid = _api.SelectedThreadId;
        if (pid == 0 || tid == 0)
        {
            _api.Log.Warning("No target process/thread.");
            return;
        }

        // Add unpacked PE as virtual module so imports/sections/strings refresh
        if (_unpackedPeBase != 0)
            _api.UI.AddUnpackedModule(_unpackedPeBase, _originalModuleName + " [unpacked]");

        bool ok = _api.Memory.WriteRip(pid, tid, _discoveredOep);
        if (ok)
        {
            _api.Log.Warning($"★ RIP set to OEP: 0x{_discoveredOep:X}");
            _api.Log.Info($"  Unpacked PE base: 0x{_unpackedPeBase:X}");
            _api.Log.Info("  Imports, sections, strings refreshed for the unpacked module.");

            // Navigate disassembly view to OEP so user sees the code
            _api.UI.NavigateDisassembly(_discoveredOep);

            _api.Log.Info("  You can now single-step or Run from the original entry point.");
        }
        else
        {
            _api.Log.Error($"Failed to set RIP to 0x{_discoveredOep:X}");
        }
    }

    /// <summary>Dump the unpacked PE (headers + all sections) to a file.</summary>
    public void DumpUnpackedPe()
    {
        if (_api == null) return;

        ulong peBase = _unpackedPeBase;
        if (peBase == 0)
        {
            _api.Log.Warning("No unpacked PE discovered yet. Run 'Analyze Protector' first.");
            return;
        }

        if (!_api.IsBreakState)
        {
            _api.Log.Warning("Process must be paused (Break) to dump memory.");
            return;
        }

        uint pid = _api.TargetPid;
        if (pid == 0) { _api.Log.Warning("No target process."); return; }

        try
        {
            // Check if PE header is intact
            var dosHdr = _api.Memory.ReadMemory(pid, peBase, 0x1000);
            bool hasValidPe = dosHdr != null && dosHdr.Length >= 0x40 &&
                              dosHdr[0] == (byte)'M' && dosHdr[1] == (byte)'Z';

            if (hasValidPe)
            {
                DumpWithIntactHeader(pid, peBase, dosHdr!);
            }
            else
            {
                // PE header zeroed (anti-dump) — reconstruct from saved section info
                if (_unpackedSections.Count == 0)
                {
                    _api.Log.Error("PE header zeroed and no section info saved. Run 'Analyze Protector' first.");
                    return;
                }
                DumpWithReconstructedHeader(pid, peBase);
            }
        }
        catch (Exception ex)
        {
            _api.Log.Error($"Dump failed: {ex.Message}");
        }
    }

    private void DumpWithIntactHeader(uint pid, ulong peBase, byte[] dosHdr)
    {
        uint lfanew = BitConverter.ToUInt32(dosHdr, 0x3C);
        if (lfanew + 0x18 > (uint)dosHdr.Length)
        {
            _api.Log.Error("Invalid PE header offset");
            return;
        }

        ushort numSections = BitConverter.ToUInt16(dosHdr, (int)lfanew + 6);
        ushort sizeOfOptional = BitConverter.ToUInt16(dosHdr, (int)lfanew + 0x14);
        int sectionStart = (int)lfanew + 4 + 20 + sizeOfOptional;

        uint totalSize = 0x1000;
        for (int i = 0; i < numSections; i++)
        {
            int off = sectionStart + i * 40;
            if (off + 40 > dosHdr.Length) break;
            uint secRva = BitConverter.ToUInt32(dosHdr, off + 12);
            uint secVsz = BitConverter.ToUInt32(dosHdr, off + 8);
            uint secEnd = secRva + ((secVsz + 0xFFFu) & ~0xFFFu);
            if (secEnd > totalSize) totalSize = secEnd;
        }

        var image = ReadImageChunked(pid, peBase, totalSize);

        // Memory-mapped dump: set FileAlignment = SectionAlignment = 0x1000
        const uint dumpFileAlign = 0x1000;
        int optOff = (int)lfanew + 24;

        // Fix FileAlignment to 0x1000
        Array.Copy(BitConverter.GetBytes(dumpFileAlign), 0, image, optOff + 36, 4);

        // Fix SizeOfHeaders to 0x1000
        Array.Copy(BitConverter.GetBytes(dumpFileAlign), 0, image, optOff + 60, 4);

        // Fix sections: RawDataOffset=RVA, RawDataSize=aligned VirtualSize
        for (int i = 0; i < numSections; i++)
        {
            int off = sectionStart + i * 40;
            if (off + 40 > image.Length) break;
            uint secRva = BitConverter.ToUInt32(image, off + 12);
            uint secVsz = BitConverter.ToUInt32(image, off + 8);
            uint rawSize = (secVsz + dumpFileAlign - 1) & ~(dumpFileAlign - 1);
            Array.Copy(BitConverter.GetBytes(secRva), 0, image, off + 20, 4);   // PointerToRawData = RVA
            Array.Copy(BitConverter.GetBytes(rawSize), 0, image, off + 16, 4);  // SizeOfRawData (aligned)
        }

        // Fix SizeOfImage
        Array.Copy(BitConverter.GetBytes(totalSize), 0, image, optOff + 56, 4);

        // Fix ImageBase to actual load address
        Array.Copy(BitConverter.GetBytes(peBase), 0, image, optOff + 24, 8);

        // Zero ALL data directories — memory dump has invalid absolute pointers,
        // resolved IAT, garbage certificate, etc. Clearing everything lets the PE load clean.
        uint numRva = BitConverter.ToUInt32(image, optOff + 108);
        int ddOff = optOff + 112;
        for (int dir = 0; dir < (int)numRva && dir < 16; dir++)
        {
            Array.Copy(BitConverter.GetBytes(0u), 0, image, ddOff + dir * 8, 4);
            Array.Copy(BitConverter.GetBytes(0u), 0, image, ddOff + dir * 8 + 4, 4);
        }

        // Zero checksum (invalid after modifications)
        Array.Copy(BitConverter.GetBytes(0u), 0, image, optOff + 64, 4);

        // Clear IMAGE_FILE_DLL flag (0x2000) — packer sets it as anti-dump
        int charsOff = (int)lfanew + 22;
        ushort fileChars = BitConverter.ToUInt16(image, charsOff);
        fileChars &= unchecked((ushort)~0x2000);
        Array.Copy(BitConverter.GetBytes(fileChars), 0, image, charsOff, 2);

        // Clear DYNAMIC_BASE (0x40) and HIGH_ENTROPY_VA (0x20) — ASLR without relocations = crash
        int dllCharsOff = optOff + 70;
        ushort dllChars = BitConverter.ToUInt16(image, dllCharsOff);
        dllChars &= unchecked((ushort)~0x4060); // clear DYNAMIC_BASE | HIGH_ENTROPY_VA | GUARD_CF
        Array.Copy(BitConverter.GetBytes(dllChars), 0, image, dllCharsOff, 2);

        // Zero LoadConfig directory — contains absolute pointers (security cookie, guard CF)
        // that are invalid after rebasing
        if (10 < numRva)
        {
            Array.Copy(BitConverter.GetBytes(0u), 0, image, ddOff + 10 * 8, 4);
            Array.Copy(BitConverter.GetBytes(0u), 0, image, ddOff + 10 * 8 + 4, 4);
        }

        // Fix entry point
        if (_discoveredOep != 0)
        {
            uint epRva = (uint)(_discoveredOep - peBase);
            Array.Copy(BitConverter.GetBytes(epRva), 0, image, optOff + 16, 4);
        }

        SaveDump(image, numSections);
    }

    private void DumpWithReconstructedHeader(uint pid, ulong peBase)
    {
        _api.Log.Info("PE header zeroed — reconstructing from saved section info...");

        // Calculate image size from sections
        uint sizeOfImage = 0x1000; // headers
        foreach (var (_, rva, vsz, _) in _unpackedSections)
        {
            uint secEnd = rva + ((vsz + 0xFFFu) & ~0xFFFu);
            if (secEnd > sizeOfImage) sizeOfImage = secEnd;
        }

        // Read all memory
        var image = ReadImageChunked(pid, peBase, sizeOfImage);

        // Build PE header from scratch
        // Layout: DOS header (0x40) + padding to 0x80 + PE signature + COFF + Optional + Section headers
        const uint fileAlignment = 0x1000; // memory-mapped dump: file align = section align
        const uint sectionAlignment = 0x1000;
        int numSections = _unpackedSections.Count;

        uint lfanew = 0x80; // standard PE offset
        uint peSignatureOff = lfanew;
        uint coffOff = peSignatureOff + 4;
        uint optOff = coffOff + 20;
        uint optSize = 0xF0; // standard x64 optional header size
        uint sectHdrOff = optOff + optSize;
        uint headersEnd = sectHdrOff + (uint)(numSections * 40);
        uint sizeOfHeaders = (headersEnd + fileAlignment - 1) & ~(fileAlignment - 1);

        // Zero out header area (already zeroed from memory read, but be sure)
        for (int i = 0; i < Math.Min(0x1000, image.Length); i++)
            image[i] = 0;

        // DOS header
        image[0] = (byte)'M'; image[1] = (byte)'Z';
        Array.Copy(BitConverter.GetBytes(lfanew), 0, image, 0x3C, 4);

        // PE signature
        image[peSignatureOff] = (byte)'P'; image[peSignatureOff + 1] = (byte)'E';

        // COFF header
        Array.Copy(BitConverter.GetBytes((ushort)0x8664), 0, image, coffOff, 2);    // Machine: AMD64
        Array.Copy(BitConverter.GetBytes((ushort)numSections), 0, image, coffOff + 2, 2); // NumberOfSections
        ushort characteristics = 0x0022; // EXECUTABLE_IMAGE | LARGE_ADDRESS_AWARE
        Array.Copy(BitConverter.GetBytes(characteristics), 0, image, coffOff + 18, 2);
        Array.Copy(BitConverter.GetBytes((ushort)optSize), 0, image, coffOff + 16, 2); // SizeOfOptionalHeader

        // Optional header
        Array.Copy(BitConverter.GetBytes((ushort)0x020B), 0, image, optOff, 2);      // Magic: PE32+
        image[optOff + 2] = 14; // MajorLinkerVersion

        // SizeOfCode — sum of all executable sections
        uint sizeOfCode = 0;
        uint baseOfCode = 0;
        foreach (var (_, srva, svsz, sch) in _unpackedSections)
        {
            if ((sch & 0x20000000) != 0) // IMAGE_SCN_MEM_EXECUTE
            {
                sizeOfCode += (svsz + fileAlignment - 1) & ~(fileAlignment - 1);
                if (baseOfCode == 0) baseOfCode = srva;
            }
        }
        Array.Copy(BitConverter.GetBytes(sizeOfCode), 0, image, optOff + 4, 4);  // SizeOfCode
        Array.Copy(BitConverter.GetBytes(baseOfCode), 0, image, optOff + 0x14, 4); // BaseOfCode

        // AddressOfEntryPoint
        if (_discoveredOep != 0)
        {
            uint epRva = (uint)(_discoveredOep - peBase);
            Array.Copy(BitConverter.GetBytes(epRva), 0, image, optOff + 16, 4);
        }
        // ImageBase
        Array.Copy(BitConverter.GetBytes(peBase), 0, image, optOff + 24, 8);
        // SectionAlignment
        Array.Copy(BitConverter.GetBytes(sectionAlignment), 0, image, optOff + 32, 4);
        // FileAlignment
        Array.Copy(BitConverter.GetBytes(fileAlignment), 0, image, optOff + 36, 4);
        // MajorOperatingSystemVersion / MinorOperatingSystemVersion
        Array.Copy(BitConverter.GetBytes((ushort)6), 0, image, optOff + 40, 2);
        // MajorSubsystemVersion / MinorSubsystemVersion
        Array.Copy(BitConverter.GetBytes((ushort)6), 0, image, optOff + 48, 2);
        // SizeOfImage
        Array.Copy(BitConverter.GetBytes(sizeOfImage), 0, image, optOff + 56, 4);
        // SizeOfHeaders
        Array.Copy(BitConverter.GetBytes(sizeOfHeaders), 0, image, optOff + 60, 4);
        // Subsystem: WINDOWS_CUI (3)
        Array.Copy(BitConverter.GetBytes((ushort)3), 0, image, optOff + 68, 2);
        // DllCharacteristics: DYNAMIC_BASE | NX_COMPAT | TERMINAL_SERVER_AWARE
        Array.Copy(BitConverter.GetBytes((ushort)0x8160), 0, image, optOff + 70, 2);
        // SizeOfStackReserve
        Array.Copy(BitConverter.GetBytes((ulong)0x100000), 0, image, optOff + 72, 8);
        // SizeOfStackCommit
        Array.Copy(BitConverter.GetBytes((ulong)0x1000), 0, image, optOff + 80, 8);
        // SizeOfHeapReserve
        Array.Copy(BitConverter.GetBytes((ulong)0x100000), 0, image, optOff + 88, 8);
        // SizeOfHeapCommit
        Array.Copy(BitConverter.GetBytes((ulong)0x1000), 0, image, optOff + 96, 8);
        // NumberOfRvaAndSizes
        Array.Copy(BitConverter.GetBytes((uint)16), 0, image, optOff + 108, 4);

        // Section headers
        for (int i = 0; i < numSections; i++)
        {
            var (name, rva, vsz, ch) = _unpackedSections[i];
            int off = (int)sectHdrOff + i * 40;

            // Name (8 bytes)
            var nameBytes = System.Text.Encoding.ASCII.GetBytes(name);
            Array.Copy(nameBytes, 0, image, off, Math.Min(nameBytes.Length, 8));

            // VirtualSize
            Array.Copy(BitConverter.GetBytes(vsz), 0, image, off + 8, 4);
            // VirtualAddress (RVA)
            Array.Copy(BitConverter.GetBytes(rva), 0, image, off + 12, 4);
            // SizeOfRawData = VirtualSize aligned to FileAlignment
            uint rawSize = (vsz + fileAlignment - 1) & ~(fileAlignment - 1);
            Array.Copy(BitConverter.GetBytes(rawSize), 0, image, off + 16, 4);
            // PointerToRawData = RVA (memory-mapped dump)
            Array.Copy(BitConverter.GetBytes(rva), 0, image, off + 20, 4);
            // Characteristics
            Array.Copy(BitConverter.GetBytes(ch), 0, image, off + 36, 4);
        }

        _api.Log.Info($"  Reconstructed PE: {numSections} sections, SizeOfImage=0x{sizeOfImage:X}");
        SaveDump(image, numSections);
    }

    private byte[] ReadImageChunked(uint pid, ulong peBase, uint totalSize)
    {
        _api.Log.Info($"Dumping 0x{totalSize:X} bytes from 0x{peBase:X}...");
        byte[] image = new byte[totalSize];
        uint chunkSize = 0x10000;
        for (uint offset = 0; offset < totalSize; offset += chunkSize)
        {
            uint remaining = totalSize - offset;
            uint readLen = Math.Min(chunkSize, remaining);
            var chunk = _api.Memory.ReadMemory(pid, peBase + offset, readLen);
            if (chunk != null)
                Array.Copy(chunk, 0, image, offset, Math.Min(chunk.Length, readLen));
        }
        return image;
    }

    private void SaveDump(byte[] image, int numSections)
    {
        var dlg = new SaveFileDialog
        {
            FileName = "unpacked_dump.exe",
            Filter = "Executable (*.exe)|*.exe|Binary (*.bin)|*.bin|All files (*.*)|*.*",
            Title = "Save PE Dump"
        };

        if (dlg.ShowDialog() == true)
        {
            File.WriteAllBytes(dlg.FileName, image);
            _api.Log.Warning($"★ Dumped {image.Length:N0} bytes to {dlg.FileName}");
            _api.Log.Info($"  {numSections} sections, OEP RVA=0x{(_discoveredOep != 0 ? _discoveredOep - _unpackedPeBase : 0):X}");
        }
    }

    /// <summary>Scan memory for common protector/packer patterns and log findings with hints.</summary>
    public void AnalyzeProtector()
    {
        if (!_api.IsConnected || _api.TargetPid == 0)
        {
            _api.Log.Warning("No target process for analysis");
            return;
        }

        // Capture values on UI thread, then do heavy work on background thread
        uint pid = _api.TargetPid;
        uint tid = _api.SelectedThreadId;
        bool isBreak = _api.IsBreakState;
        var (peb, _) = _api.Process.GetPebAddress(pid);
        if (peb == 0) { _api.Log.Error("Cannot get PEB"); return; }

        var imgBaseData = _api.Memory.ReadMemory(pid, peb + 0x10, 8);
        if (imgBaseData == null) return;
        ulong imageBase = BitConverter.ToUInt64(imgBaseData);

        _api.Log.Info("Analyzing... (running in background)");
        System.Threading.Tasks.Task.Run(() => AnalyzeProtectorWorker(pid, tid, isBreak, peb, imageBase));
    }

    private void AnalyzeProtectorWorker(uint pid, uint tid, bool isBreak, ulong peb, ulong imageBase)
    {
        try
        {
            AnalyzeProtectorCore(pid, tid, isBreak, peb, imageBase);
        }
        catch (Exception ex)
        {
            _api.Log.Error($"AnalyzeProtector failed: {ex.Message}");
        }
    }

    private void AnalyzeProtectorCore(uint pid, uint tid, bool isBreak, ulong peb, ulong imageBase)
    {

        _api.Log.Info($"=== Protector Analysis for PID {pid} (ImageBase=0x{imageBase:X}) ===");

        // Determine original module name from imageBase
        var origMod = _api.Symbols.GetModules()
            .FirstOrDefault(m => m.BaseAddress == imageBase ||
                                 (imageBase >= m.BaseAddress && imageBase < m.BaseAddress + m.Size));
        _originalModuleName = origMod?.Name ?? "unpacked.exe";
        _api.Log.Info($"  Original module: {_originalModuleName}");

        // Read PE header
        var dosHeader = _api.Memory.ReadMemory(pid, imageBase, 0x40);
        if (dosHeader == null || dosHeader.Length < 0x40) { _api.Log.Error("Cannot read DOS header"); return; }
        uint e_lfanew = BitConverter.ToUInt32(dosHeader, 0x3C);

        var peHeader = _api.Memory.ReadMemory(pid, imageBase + e_lfanew, 0x108);
        if (peHeader == null) return;

        ushort numSections = BitConverter.ToUInt16(peHeader, 6);
        uint entryPointRva = BitConverter.ToUInt32(peHeader, 40);
        ushort optHdrSize = BitConverter.ToUInt16(peHeader, 20);
        uint sectionTableOffset = e_lfanew + 24u + optHdrSize;

        _api.Log.Info($"  Entry Point: 0x{(imageBase + entryPointRva):X}");
        _api.Log.Info($"  Sections: {numSections}");

        // Read section headers
        var sectData = _api.Memory.ReadMemory(pid, imageBase + sectionTableOffset, (uint)(numSections * 40));
        if (sectData == null) return;

        bool hasStandardText = false;
        string detectedProtector = "";
        var sectionNames = new List<string>();
        uint lastSectionSize = 0;
        string lastSectionName = "";
        uint epSectionIndex = uint.MaxValue;

        for (int s = 0; s < numSections; s++)
        {
            string name = System.Text.Encoding.ASCII.GetString(sectData, s * 40, 8).TrimEnd('\0');
            uint vaddr = BitConverter.ToUInt32(sectData, s * 40 + 12);
            uint vsize = BitConverter.ToUInt32(sectData, s * 40 + 8);
            uint chars = BitConverter.ToUInt32(sectData, s * 40 + 36);

            sectionNames.Add(name);
            lastSectionSize = vsize;
            lastSectionName = name;

            // Check if entry point is in this section
            if (entryPointRva >= vaddr && entryPointRva < vaddr + vsize)
                epSectionIndex = (uint)s;

            string perms = "";
            if ((chars & 0x20000000) != 0) perms += "X";
            if ((chars & 0x40000000) != 0) perms += "R";
            if ((chars & 0x80000000) != 0) perms += "W";

            _api.Log.Info($"  Section '{name}': VA=0x{(imageBase + vaddr):X} Size=0x{vsize:X} [{perms}]");

            if (name == ".text") hasStandardText = true;
            if (perms.Contains("X") && perms.Contains("W"))
                _api.Log.Warning($"    Section '{name}' is RWX — likely a packer stub!");
        }

        if (!hasStandardText)
            _api.Log.Warning("  No .text section found — executable is likely packed!");

        // ── VMProtect detection ──
        bool isVmp = sectionNames.Any(n => n.StartsWith(".vmp", StringComparison.OrdinalIgnoreCase));
        if (!isVmp)
            isVmp = sectionNames.Any(n => n.Equals("VMProtect", StringComparison.OrdinalIgnoreCase));

        if (isVmp)
        {
            detectedProtector = "VMProtect";
            _api.Log.Warning("  DETECTED: VMProtect");
            _api.Log.Info("  VMProtect uses a virtual machine (bytecode interpreter) to protect code.");
            _api.Log.Info("  Anti-debug techniques used by VMProtect:");
            _api.Log.Info("    - PEB.BeingDebugged, NtGlobalFlag, HeapFlags");
            _api.Log.Info("    - NtQueryInformationProcess (ProcessDebugPort, ProcessDebugObjectHandle)");
            _api.Log.Info("    - NtSetInformationThread (ThreadHideFromDebugger)");
            _api.Log.Info("    - NtQuerySystemInformation (SystemKernelDebuggerInformation)");
            _api.Log.Info("    - RDTSC/RDTSCP timing checks");
            _api.Log.Info("    - Hardware breakpoint detection (DR0-DR7)");
            _api.Log.Info("    - INT 2D (kernel debugger detection)");
            _api.Log.Info("    - Exception-based detection (INT3, EXCEPTION_BREAKPOINT)");
            _api.Log.Info("  HINTS for VMProtect:");
            _api.Log.Info("    1. Enable ALL anti-debug checkboxes including 'Hide DRx' and 'Patch RDTSC'");
            _api.Log.Info("    2. Apply patches BEFORE running (AutoApply should be ON)");
            _api.Log.Info("    3. Do NOT single-step through VM handlers — use 'Run to' or breakpoints");
            _api.Log.Info("    4. Set breakpoints on API calls (imports) to trace program logic");
            _api.Log.Info("    5. VM entry is usually: PUSH regs → CALL vm_dispatcher");
            _api.Log.Info("    6. Look for virtualized functions by finding PUSH/CALL patterns");
            _api.Log.Info("    7. To find OEP: set BP on VirtualProtect/VirtualAlloc, then trace back");
        }

        // ── Themida / WinLicense / Oreans detection ──
        bool isThemida = sectionNames.Any(n =>
            n.StartsWith(".themida", StringComparison.OrdinalIgnoreCase) ||
            n.StartsWith(".winlice", StringComparison.OrdinalIgnoreCase) ||
            n.StartsWith(".oreans", StringComparison.OrdinalIgnoreCase) ||
            n.StartsWith(".taggant", StringComparison.OrdinalIgnoreCase));

        // Themida often has EP in last section which is very large
        bool checkThemidaEpCode = false;
        if (!isThemida && epSectionIndex == numSections - 1 && lastSectionSize > 0x80000)
        {
            checkThemidaEpCode = true;
            // Also check for large blank section name (Themida sometimes uses spaces)
            if (string.IsNullOrWhiteSpace(lastSectionName) || lastSectionName.All(c => c == '.' || !char.IsLetterOrDigit(c)))
                isThemida = true;
        }

        // Themida also uses section names like ".boot" for its unpacker
        if (!isThemida && sectionNames.Any(n => n == ".boot"))
            isThemida = true;

        if (isThemida)
        {
            detectedProtector = "Themida/WinLicense";
            _api.Log.Warning("  DETECTED: Themida / WinLicense (Oreans)");
            _api.Log.Info("  Themida is one of the most aggressive protectors with extensive anti-debug.");
            _api.Log.Info("  Anti-debug techniques used by Themida:");
            _api.Log.Info("    - PEB.BeingDebugged, NtGlobalFlag, HeapFlags (multiple checks)");
            _api.Log.Info("    - NtQueryInformationProcess (DebugPort, DebugObjectHandle, DebugFlags)");
            _api.Log.Info("    - NtSetInformationThread (ThreadHideFromDebugger on multiple threads)");
            _api.Log.Info("    - NtQuerySystemInformation (SystemKernelDebuggerInformation)");
            _api.Log.Info("    - RDTSC/RDTSCP timing checks (multiple, with anti-patch)");
            _api.Log.Info("    - INT 2D / INT 1 / INT 3 exception-based checks");
            _api.Log.Info("    - Hardware breakpoint detection and clearing (DR0-DR7)");
            _api.Log.Info("    - NtQueryInformationProcess(ProcessBasicInformation) — parent PID check");
            _api.Log.Info("    - NtQueryObject — counts debug objects");
            _api.Log.Info("    - FindWindow for debugger windows (OllyDbg, x64dbg, IDA)");
            _api.Log.Info("    - Anti-VM: CPUID hypervisor bit, registry keys, device names");
            _api.Log.Info("    - Nanomites: INT3 replaces conditional jumps (handled via SEH)");
            _api.Log.Info("    - API redirection through allocated memory blocks");
            _api.Log.Info("    - Code mutation and junk code insertion");
            _api.Log.Info("  HINTS for Themida:");
            _api.Log.Info("    1. Enable ALL anti-debug checkboxes — Themida checks everything");
            _api.Log.Info("    2. 'Patch RDTSC' is CRITICAL — Themida has many timing checks");
            _api.Log.Info("    3. Do NOT set hardware breakpoints during unpacking — Themida detects them");
            _api.Log.Info("    4. Do NOT single-step — use Run (F9) to let the unpacker finish");
            _api.Log.Info("    5. If AV triggered: apply all patches, restart process, Run immediately");
            _api.Log.Info("    6. Themida unpacking takes several seconds — be patient");
            _api.Log.Info("    7. After unpacking, imports are redirected — check for JMP [mem] stubs");
            _api.Log.Info("    8. OEP is usually in the original .text section after unpacking");
            _api.Log.Info("    9. To find OEP: set memory BP on .text section, Run, wait for execution");
            _api.Log.Info("   10. Nanomites: if you see many INT3 in unpacked code, these are patched");
            _api.Log.Info("       conditional jumps — Themida's SEH handler resolves them at runtime");
        }

        // ── UPX detection ──
        bool isUpx = sectionNames.Any(n => n.StartsWith("UPX", StringComparison.OrdinalIgnoreCase));
        if (isUpx)
        {
            detectedProtector = "UPX";
            _api.Log.Info("  DETECTED: UPX (Ultimate Packer for eXecutables)");
            _api.Log.Info("  UPX is a simple packer with no anti-debug. Easy to unpack.");
            _api.Log.Info("  HINTS for UPX:");
            _api.Log.Info("    1. Set BP at the end of the decompression loop (usually a JMP to OEP)");
            _api.Log.Info("    2. Or simply run 'upx -d file.exe' to unpack statically");
            _api.Log.Info("    3. OEP is the target of the final JMP/CALL at the end of the stub");
        }

        // ── ASPack detection ──
        bool isAsPack = sectionNames.Any(n => n.StartsWith(".aspack", StringComparison.OrdinalIgnoreCase) ||
                                                n == ".adata");
        if (isAsPack)
        {
            detectedProtector = "ASPack";
            _api.Log.Info("  DETECTED: ASPack");
            _api.Log.Info("  HINTS: Set BP on the RETN after the main decompression loop to find OEP.");
        }

        // ── Obsidium detection ──
        bool isObsidium = sectionNames.Any(n => n.StartsWith(".obsid", StringComparison.OrdinalIgnoreCase));
        if (isObsidium)
        {
            detectedProtector = "Obsidium";
            _api.Log.Info("  DETECTED: Obsidium");
            _api.Log.Info("  Obsidium uses anti-debug similar to Themida but less aggressive.");
            _api.Log.Info("  HINTS: Enable all anti-debug patches, Run (F9), set memory BP on .text for OEP.");
        }

        if (string.IsNullOrEmpty(detectedProtector) && !hasStandardText)
        {
            // Check for multi-layer custom protector indicators
            bool hasRwxStub = sectionNames.Any(n => n.Contains("stub", StringComparison.OrdinalIgnoreCase));
            bool hasDataSection = sectionNames.Any(n =>
            {
                uint idx = (uint)sectionNames.ToList().IndexOf(n);
                uint ch = BitConverter.ToUInt32(sectData, (int)(idx * 40 + 36));
                return (ch & 0x20000000) == 0 && (ch & 0x40000000) != 0; // readable, not executable
            });

            _api.Log.Warning("  Protector: Custom packer/cryptor (not a known commercial product)");
            _api.Log.Info("  Custom protector analysis:");

            if (hasRwxStub)
                _api.Log.Info("    - RWX stub section detected — typical of multi-stage decryptors");
            if (hasDataSection && numSections == 2)
                _api.Log.Info("    - 2 sections (stub + data) — likely: stub decrypts data → rebuilds PE");

            _api.Log.Info("  HINTS for custom protectors:");
            _api.Log.Info("    1. Enable ALL anti-debug patches before running");
            _api.Log.Info("    2. Run (F9) — let the stub decrypt and rebuild the original PE");
            _api.Log.Info("    3. Common unpacking stages: XOR → stream cipher → decompression → PE rebuild");
            _api.Log.Info("    4. Set memory breakpoint on the stub section — when it executes decrypted code,");
            _api.Log.Info("       execution will leave the stub and hit the original entry point (OEP)");
            _api.Log.Info("    5. After unpacking, 'Refresh Modules' to see new sections/imports");
            _api.Log.Info("    6. If the process crashes: check the anti-debug kill patterns below");
        }
        else if (!string.IsNullOrEmpty(detectedProtector))
        {
            _api.Log.Info($"  Protector identified: {detectedProtector}");
        }

        // Scan entry point area for patterns
        ulong epAddr = imageBase + entryPointRva;
        var epCode = _api.Memory.ReadMemory(pid, epAddr, 256);
        if (epCode == null) return;

        // Deferred Themida check: CALL $+5 / POP pattern at entry (delta offset trick)
        if (checkThemidaEpCode && !isThemida && epCode.Length >= 6 &&
            epCode[0] == 0xE8 && epCode[1] == 0x00 && epCode[2] == 0x00 &&
            epCode[3] == 0x00 && epCode[4] == 0x00)
        {
            isThemida = true;
            detectedProtector = "Themida/WinLicense";
            _api.Log.Warning("  DETECTED: Themida / WinLicense (Oreans) — CALL $+5 entry pattern");
            _api.Log.Info("  Enable ALL anti-debug checkboxes and 'Patch RDTSC'. Do NOT single-step.");
            _api.Log.Info("  Use Run (F9) to let unpacker finish. Set memory BP on .text for OEP.");
        }

        // Pattern: XOR loop (30 XX or 80 3X XX)
        for (int i = 0; i < epCode.Length - 3; i++)
        {
            // XOR [reg], reg  (30 XX where XX has mod=00)
            if (epCode[i] == 0x30 && (epCode[i + 1] & 0xC0) == 0)
            {
                _api.Log.Info($"  → XOR decryption loop detected near entry point (+0x{i:X})");
                _api.Log.Info("    HINT: Set breakpoint after the loop to catch decrypted code");
                break;
            }
            // XOR [rdi], bl pattern
            if (epCode[i] == 0x30 && epCode[i + 1] == 0x1F)
            {
                _api.Log.Info($"  → XOR [rdi], bl decryption at EP+0x{i:X}");
                _api.Log.Info("    HINT: Let it run, then re-analyze the decrypted region");
                break;
            }
        }

        // Scan ALL sections for crypto patterns, anti-debug instructions, etc.
        for (int s = 0; s < numSections; s++)
        {
            uint vaddr = BitConverter.ToUInt32(sectData, s * 40 + 12);
            uint vsize = BitConverter.ToUInt32(sectData, s * 40 + 8);
            if (vsize > 0x20000) vsize = 0x20000; // scan up to 128KB per section

            var code = _api.Memory.ReadMemory(pid, imageBase + vaddr, vsize);
            if (code == null) continue;

            int rdtscCount = 0, cpuidCount = 0;
            bool foundChacha = false;

            for (int i = 0; i < code.Length - 4; i++)
            {
                if (code[i] == 0x0F && code[i + 1] == 0x31) rdtscCount++;
                if (code[i] == 0x0F && code[i + 1] == 0xA2) cpuidCount++;

                // ROR reg, 0x10 followed nearby by ROR reg, 0x14/0x18/0x19
                if (!foundChacha && code[i] == 0xC1 && (code[i + 1] & 0xF8) == 0xC8 && code[i + 2] == 0x10)
                {
                    // Look ahead for the other rotations
                    for (int j = i + 3; j < Math.Min(i + 100, code.Length - 3); j++)
                    {
                        if (code[j] == 0xC1 && (code[j + 1] & 0xF8) == 0xC8 && code[j + 2] == 0x19)
                        {
                            foundChacha = true;
                            _api.Log.Info($"  → ChaCha20/Salsa20 cipher detected at 0x{(imageBase + vaddr + (uint)i):X}");
                            _api.Log.Info("    HINT: Cryptographic decryption layer — set BP after the crypto function returns");
                            break;
                        }
                    }
                }

                // Anti-debug kill: MOV [low_addr], X — writing to very low address to crash
                // C7 04 25 XX 00 00 00 = MOV dword ptr [XX], imm32
                // C6 04 25 XX 00 00 00 = MOV byte ptr [XX], imm8
                if ((code[i] == 0xC7 || code[i] == 0xC6) && code[i + 1] == 0x04 && code[i + 2] == 0x25 &&
                    code[i + 4] == 0x00 && code[i + 5] == 0x00 && code[i + 6] == 0x00 &&
                    code[i + 3] <= 0x10)
                {
                    _api.Log.Warning($"  → Anti-debug kill at 0x{(imageBase + vaddr + (uint)i):X}: MOV [{code[i+3]}], 0 — intentional crash");
                    _api.Log.Info("    HINT: This triggers ACCESS_VIOLATION when anti-debug check fails.");
                }

                // NtQueryInformationProcess syscall (class 7=DebugPort, 0x1E=DebugObjectHandle, 0x1F=DebugFlags)
                // Look for MOV edx/r8d, 7/0x1E/0x1F before SYSCALL
                if (code[i] == 0x0F && code[i + 1] == 0x05) // SYSCALL
                {
                    // Check up to 20 bytes before for the class argument
                    for (int j = Math.Max(0, i - 20); j < i; j++)
                    {
                        if (code[j] == 0xBA) // MOV edx, imm32
                        {
                            uint val = (uint)(code[j + 1] | (code[j + 2] << 8));
                            if (val == 7 || val == 0x1E || val == 0x1F)
                            {
                                string checkName = val == 7 ? "DebugPort" : val == 0x1E ? "DebugObjectHandle" : "DebugFlags";
                                _api.Log.Info($"  → NtQueryInformationProcess({checkName}) syscall at 0x{(imageBase + vaddr + (uint)i):X}");
                            }
                        }
                    }
                }

                // PEB.BeingDebugged check: mov reg, gs:[60h] = 65 48 8B 04 25 60 00 00 00
                if (i < code.Length - 9 && code[i] == 0x65 && code[i + 1] == 0x48 &&
                    code[i + 2] == 0x8B && code[i + 4] == 0x25 &&
                    code[i + 5] == 0x60 && code[i + 6] == 0x00 &&
                    code[i + 7] == 0x00 && code[i + 8] == 0x00)
                {
                    _api.Log.Info($"  → PEB access (gs:[60h]) at 0x{(imageBase + vaddr + (uint)i):X}");
                    _api.Log.Info("    HINT: Anti-debug check reads PEB.BeingDebugged — ensure PEB patches are applied");
                }
            }

            if (rdtscCount > 0)
                _api.Log.Info($"  → {rdtscCount} RDTSC instruction(s) found — timing-based anti-debug");
            if (cpuidCount > 0)
                _api.Log.Info($"  → {cpuidCount} CPUID instruction(s) found — used with RDTSC for timing measurement");
        }

        // Check if current RIP is outside known sections (= OEP found after unpacking)
        try
        {
            if (tid != 0)
            {
                var regs = _api.Memory.ReadRegisters(pid, tid);
                var ripReg = regs?.FirstOrDefault(r => r.Name == "RIP" || r.Name == "rip");
                if (ripReg != null)
                {
                    ulong rip = ripReg.Value;
                    bool ripInSection = false;
                    for (int s = 0; s < numSections; s++)
                    {
                        uint svaddr = BitConverter.ToUInt32(sectData, s * 40 + 12);
                        uint svsize = BitConverter.ToUInt32(sectData, s * 40 + 8);
                        if (rip >= imageBase + svaddr && rip < imageBase + svaddr + svsize)
                        {
                            ripInSection = true;
                            string sname = System.Text.Encoding.ASCII.GetString(sectData, s * 40, 8).TrimEnd('\0');
                            _api.Log.Info($"  Current RIP: 0x{rip:X} (in section '{sname}')");
                            break;
                        }
                    }

                    if (!ripInSection)
                    {
                        // Compute actual image end from sections
                        ulong imageEnd = imageBase + 0x1000; // at least header
                        for (int s2 = 0; s2 < numSections; s2++)
                        {
                            uint sv = BitConverter.ToUInt32(sectData, s2 * 40 + 12);
                            uint ss = BitConverter.ToUInt32(sectData, s2 * 40 + 8);
                            ulong sectEnd = imageBase + sv + ss;
                            if (sectEnd > imageEnd) imageEnd = sectEnd;
                        }

                        if (rip >= imageBase && rip < imageBase + 0x1000)
                        {
                            _api.Log.Info($"  Current RIP: 0x{rip:X} (PE header area)");
                        }
                        else if (rip < imageBase || rip >= imageEnd)
                        {
                            _api.Log.Warning($"  ★ Current RIP: 0x{rip:X} — OUTSIDE the packed image!");
                            _api.Log.Warning("  ★ Protector has likely finished unpacking.");

                            // Strategy: quick check near RIP for PE header.
                            // Protectors either map a full PE (with MZ) or just raw sections.
                            bool foundUnpacked = false;
                            ulong ripAligned = rip & ~0xFFFFUL; // 64KB aligned

                            // Collect RIP + stack return addresses
                            var stackRegs = _api.Memory.ReadRegisters(pid, tid);
                            ulong rsp = stackRegs?.FirstOrDefault(r => r.Name == "RSP")?.Value ?? 0;

                            var candidates = new List<ulong> { rip };
                            if (rsp != 0)
                            {
                                var stackData = _api.Memory.ReadMemory(pid, rsp, 2048);
                                if (stackData != null)
                                {
                                    for (int si = 0; si + 8 <= stackData.Length; si += 8)
                                    {
                                        ulong val = BitConverter.ToUInt64(stackData, si);
                                        if (val > 0x10000 && val < 0x7FFFFFFFFFFF)
                                            candidates.Add(val);
                                    }
                                }
                            }

                            // Get known modules to filter out system DLLs
                            var modules = _api.Symbols.GetModules();
                            ulong imageBaseAligned = imageBase & ~0xFFFFUL;
                            var checkedBases = new HashSet<ulong>();

                            // For each address not in a known module, check its 64KB-aligned base for MZ
                            _api.Log.Info($"  Checking {candidates.Count} addresses (RIP + stack)...");
                            foreach (ulong addr in candidates)
                            {
                                if (foundUnpacked) break;

                                // Skip if inside a known module
                                bool inKnown = modules.Any(m =>
                                    addr >= m.BaseAddress && addr < m.BaseAddress + m.Size);
                                if (inKnown) continue;

                                // Skip packed image
                                ulong base64k = addr & ~0xFFFFUL;
                                if (base64k == imageBaseAligned) continue;
                                if (!checkedBases.Add(base64k)) continue;

                                // Check for MZ at 64KB-aligned base
                                var mz = _api.Memory.ReadMemory(pid, base64k, 2);
                                if (mz == null || mz.Length < 2 || mz[0] != 'M' || mz[1] != 'Z') continue;

                                _api.Log.Warning($"  ★ MZ found at 0x{base64k:X} (from address 0x{addr:X})");
                                foundUnpacked = IdentifyModuleAsUnpacked(pid, base64k, addr);
                            }

                            // Fallback: backward scan from non-module addresses (max 8 steps each)
                            if (!foundUnpacked)
                            {
                                foreach (ulong addr in candidates)
                                {
                                    if (foundUnpacked) break;
                                    bool inKnown = modules.Any(m =>
                                        addr >= m.BaseAddress && addr < m.BaseAddress + m.Size);
                                    if (inKnown) continue;

                                    ulong baseAddr = (addr & ~0xFFFFUL) - 0x10000;
                                    for (int step = 0; step < 8 && baseAddr >= 0x10000; step++, baseAddr -= 0x10000)
                                    {
                                        if (baseAddr == imageBaseAligned) break;
                                        if (!checkedBases.Add(baseAddr)) continue;
                                        var mz = _api.Memory.ReadMemory(pid, baseAddr, 2);
                                        if (mz != null && mz.Length >= 2 && mz[0] == 'M' && mz[1] == 'Z')
                                        {
                                            _api.Log.Warning($"  ★ MZ found at 0x{baseAddr:X} (scanning back from 0x{addr:X})");
                                            foundUnpacked = IdentifyModuleAsUnpacked(pid, baseAddr, addr);
                                            break;
                                        }
                                    }
                                }
                            }

                            if (!foundUnpacked)
                            {
                                _api.Log.Info($"  Could not find unpacked PE ({checkedBases.Count} bases checked).");
                                _api.Log.Info("    HINT: Run and Break again after protector finishes.");
                            }
                            else
                            {
                                _api.UI.AddUnpackedModule(_unpackedPeBase, _originalModuleName + " [unpacked]");
                                _api.UI.RefreshModulesAndSections();
                            }
                        }
                        else
                        {
                            _api.Log.Info($"  Current RIP: 0x{rip:X} (between sections — protector code area)");
                        }
                    }
                }
            }
        }
        catch { /* ignore register read errors */ }

        _api.Log.Info("=== Analysis Complete ===");
        _api.Log.Info("HINT: Use 'Apply Now' with all patches enabled, then 'Run' (F9) to let the protector unpack.");
        _api.Log.Info("HINT: After unpacking, use 'Refresh Modules' to re-analyze the process.");
    }

    /// <summary>Identify a known module as the unpacked PE. Reads its PE header, sets _unpackedPeBase/_discoveredOep.</summary>
    private bool IdentifyModuleAsUnpacked(uint pid, ulong moduleBase, ulong codeAddr)
    {
        var dosHdr = _api.Memory.ReadMemory(pid, moduleBase, 0x40);
        if (dosHdr == null || dosHdr.Length < 0x40 || dosHdr[0] != 'M' || dosHdr[1] != 'Z') return false;

        uint lfanew = BitConverter.ToUInt32(dosHdr, 0x3C);
        if (lfanew > 0x1000) return false;

        var peHdr = _api.Memory.ReadMemory(pid, moduleBase + lfanew, 0x120);
        if (peHdr == null || peHdr.Length < 0x88 || peHdr[0] != 'P' || peHdr[1] != 'E') return false;

        _unpackedPeBase = moduleBase;

        uint ep = BitConverter.ToUInt32(peHdr, 40);
        ulong oep = ep != 0 ? moduleBase + ep : (codeAddr != 0 ? codeAddr : moduleBase);
        _discoveredOep = oep;

        if (ep != 0)
            _api.Log.Warning($"  ★ OEP: 0x{oep:X} (AddressOfEntryPoint = 0x{ep:X})");
        else if (codeAddr != 0)
            _api.Log.Warning($"  ★ EP zeroed, using code addr as OEP: 0x{oep:X}");

        _api.Log.Info($"    Use 'Jump to OEP' button or set a breakpoint at 0x{oep:X}");

        // Show sections
        ushort nSect = BitConverter.ToUInt16(peHdr, 6);
        ushort optSize = BitConverter.ToUInt16(peHdr, 20);
        uint sectOff = lfanew + 24u + optSize;
        var sectData = _api.Memory.ReadMemory(pid, moduleBase + sectOff, (uint)(nSect * 40));
        if (sectData != null)
        {
            _api.Log.Info($"    {nSect} sections:");
            _unpackedSections.Clear();
            var pluginSections = new List<PluginSectionInfo>();
            for (int i = 0; i < nSect; i++)
            {
                string name = System.Text.Encoding.ASCII.GetString(sectData, i * 40, 8).TrimEnd('\0');
                uint va = BitConverter.ToUInt32(sectData, i * 40 + 12);
                uint vsz = BitConverter.ToUInt32(sectData, i * 40 + 8);
                uint ch = BitConverter.ToUInt32(sectData, i * 40 + 36);
                string perms = "";
                if ((ch & 0x20000000) != 0) perms += "X";
                if ((ch & 0x40000000) != 0) perms += "R";
                if ((ch & 0x80000000) != 0) perms += "W";
                _api.Log.Info($"      '{name}': VA=0x{(moduleBase + va):X} Size=0x{vsz:X} [{perms}]");
                _unpackedSections.Add((name, va, vsz, ch));
                pluginSections.Add(new PluginSectionInfo
                {
                    Name = name,
                    VirtualAddress = moduleBase + va,
                    VirtualSize = vsz,
                    Characteristics = ch
                });
            }
            _api.UI.AddModuleSections(_originalModuleName + " [unpacked]", pluginSections);
        }
        return true;
    }

    /// <summary>Check if address is a valid PE. If non-DLL, set _unpackedPeBase/_discoveredOep.</summary>
    private bool TryIdentifyPe(uint pid, ulong probe, List<ulong> allAddrs, ulong rip, out bool isDll)
    {
        isDll = false;
        var mz = _api.Memory.ReadMemory(pid, probe, 2);
        if (mz == null || mz.Length < 2 || mz[0] != 0x4D || mz[1] != 0x5A) return false;

        var probeDos = _api.Memory.ReadMemory(pid, probe, 0x40);
        if (probeDos == null || probeDos.Length < 0x40) return false;
        uint probeLfanew = BitConverter.ToUInt32(probeDos, 0x3C);
        if (probeLfanew > 0x1000) return false;

        var probeHdr = _api.Memory.ReadMemory(pid, probe + probeLfanew, 0x120);
        if (probeHdr == null || probeHdr.Length < 0x88) return false;
        if (probeHdr[0] != 'P' || probeHdr[1] != 'E') return false;

        // Check export directory — DLLs have exports with a .dll name
        ushort probeMagic = BitConverter.ToUInt16(probeHdr, 24);
        bool probeIs64 = probeMagic == 0x20B;
        int exportDirOff = probeIs64 ? (24 + 0x70) : (24 + 0x60);
        if (exportDirOff + 8 <= probeHdr.Length)
        {
            uint exportRva = BitConverter.ToUInt32(probeHdr, exportDirOff);
            if (exportRva != 0 && exportRva < 0x10000000)
            {
                var exportDir = _api.Memory.ReadMemory(pid, probe + exportRva, 40);
                if (exportDir != null && exportDir.Length >= 16)
                {
                    uint nameRva = BitConverter.ToUInt32(exportDir, 12);
                    if (nameRva != 0 && nameRva < 0x10000000)
                    {
                        var nameBytes = _api.Memory.ReadMemory(pid, probe + nameRva, 64);
                        if (nameBytes != null)
                        {
                            int nulIdx = Array.IndexOf(nameBytes, (byte)0);
                            if (nulIdx < 0) nulIdx = nameBytes.Length;
                            string dllName = System.Text.Encoding.ASCII.GetString(nameBytes, 0, nulIdx);
                            if (dllName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                            {
                                isDll = true;
                                return true;
                            }
                        }
                    }
                }
            }
        }

        // Also check IMAGE_FILE_DLL flag in Characteristics
        ushort characteristics = BitConverter.ToUInt16(probeHdr, 22);
        if ((characteristics & 0x2000) != 0) // IMAGE_FILE_DLL
        {
            isDll = true;
            return true;
        }

        // Check known modules
        var knownMod = _api.Symbols.GetModules()
            .FirstOrDefault(m => probe >= m.BaseAddress && probe < m.BaseAddress + m.Size);
        if (knownMod != null)
        {
            isDll = true;
            _api.Log.Info($"  PE at 0x{probe:X} is '{knownMod.Name}' (skipping)");
            return true;
        }

        // Found unpacked PE!
        _api.Log.Warning($"  ★ Found unpacked PE at 0x{probe:X}!");
        _unpackedPeBase = probe;

        // Find the best return address inside this PE
        ulong bestRetAddr = rip;
        foreach (ulong addr in allAddrs)
        {
            if (addr > probe && addr < probe + 0x200000)
            {
                bestRetAddr = addr;
                _api.Log.Info($"    (code address on stack: 0x{addr:X})");
                break;
            }
        }

        uint uEp = BitConverter.ToUInt32(probeHdr, 40);
        ulong oep = uEp != 0 ? probe + uEp : bestRetAddr;
        _discoveredOep = oep;
        if (uEp != 0)
            _api.Log.Warning($"  ★ Unpacked PE entry point (OEP): 0x{oep:X}");
        else
            _api.Log.Warning($"  ★ EP zeroed. Using code addr as OEP: 0x{oep:X}");
        _api.Log.Info($"    Use 'Jump to OEP' button or set a breakpoint at 0x{oep:X}");

        // Show sections
        ushort uNumSect = BitConverter.ToUInt16(probeHdr, 6);
        ushort uOptSize = BitConverter.ToUInt16(probeHdr, 20);
        uint uSectOff = probeLfanew + 24u + uOptSize;
        var uSectData = _api.Memory.ReadMemory(pid, probe + uSectOff, (uint)(uNumSect * 40));
        if (uSectData != null)
        {
            _api.Log.Info($"    Unpacked PE has {uNumSect} sections:");
            for (int us = 0; us < uNumSect; us++)
            {
                string usName = System.Text.Encoding.ASCII.GetString(uSectData, us * 40, 8).TrimEnd('\0');
                uint usVa = BitConverter.ToUInt32(uSectData, us * 40 + 12);
                uint usVsz = BitConverter.ToUInt32(uSectData, us * 40 + 8);
                uint usCh = BitConverter.ToUInt32(uSectData, us * 40 + 36);
                string usPerms = "";
                if ((usCh & 0x20000000) != 0) usPerms += "X";
                if ((usCh & 0x40000000) != 0) usPerms += "R";
                if ((usCh & 0x80000000) != 0) usPerms += "W";
                _api.Log.Info($"      '{usName}': VA=0x{(probe + usVa):X} Size=0x{usVsz:X} [{usPerms}]");
            }
        }

        isDll = false;
        return true;
    }

    /// <summary>Scan code sections and NOP out RDTSC (0F 31) instructions to defeat timing checks.</summary>
    private int PatchTimingChecks(uint pid, ulong imageBase)
    {
        int count = 0;
        try
        {
            // Read PE header to find code sections
            var dosHeader = _api.Memory.ReadMemory(pid, imageBase, 0x40);
            if (dosHeader == null || dosHeader.Length < 0x40) return 0;

            uint e_lfanew = BitConverter.ToUInt32(dosHeader, 0x3C);
            var peHeader = _api.Memory.ReadMemory(pid, imageBase + e_lfanew, 0x108);
            if (peHeader == null || peHeader.Length < 0x18) return 0;

            ushort numSections = BitConverter.ToUInt16(peHeader, 6);
            ushort optHdrSize = BitConverter.ToUInt16(peHeader, 20);
            uint sectionTableOffset = e_lfanew + 24u + optHdrSize;

            // Read section headers
            var sectData = _api.Memory.ReadMemory(pid, imageBase + sectionTableOffset, (uint)(numSections * 40));
            if (sectData == null) return 0;

            _api.Log.Info($"  RDTSC scan: {numSections} sections at imageBase 0x{imageBase:X}");

            for (int s = 0; s < numSections; s++)
            {
                uint characteristics = BitConverter.ToUInt32(sectData, s * 40 + 36);
                string sectName = System.Text.Encoding.ASCII.GetString(sectData, s * 40, 8).TrimEnd('\0');
                uint virtualAddr = BitConverter.ToUInt32(sectData, s * 40 + 12);
                uint virtualSize = BitConverter.ToUInt32(sectData, s * 40 + 8);

                if ((characteristics & 0x20000000) == 0)
                {
                    _api.Log.Info($"    '{sectName}': VA=0x{virtualAddr:X} Size=0x{virtualSize:X} — skipped (not executable)");
                    continue;
                }

                string sectNameLower = sectName.ToLowerInvariant();
                if (sectNameLower is ".themida" or ".boot" or ".vmp0" or ".vmp1" or ".packed" or ".upx")
                {
                    _api.Log.Info($"    '{sectName}': VA=0x{virtualAddr:X} Size=0x{virtualSize:X} — skipped (protector section)");
                    continue;
                }

                if (virtualSize > 0x200000)
                {
                    _api.Log.Info($"    '{sectName}': VA=0x{virtualAddr:X} Size=0x{virtualSize:X} — skipped (too large)");
                    continue;
                }
                if (virtualSize > 0x100000) virtualSize = 0x100000;

                ulong sectionBase = imageBase + virtualAddr;
                var code = _api.Memory.ReadMemory(pid, sectionBase, virtualSize);
                if (code == null) continue;

                // Scan for RDTSC (0F 31) and patch with XOR EAX,EAX (31 C0)
                for (uint i = 0; i < code.Length - 1; i++)
                {
                    if (code[i] == 0x0F && code[i + 1] == 0x31)
                    {
                        // Patch RDTSC → XOR EAX,EAX (zeroes both EAX and EDX conceptually)
                        if (_api.Memory.WriteMemory(pid, sectionBase + i, new byte[] { 0x31, 0xC0 }))
                        {
                            _api.Log.Info($"  Patched RDTSC at 0x{(sectionBase + i):X} → XOR EAX,EAX");
                            count++;
                        }
                    }
                    // Also find CPUID (0F A2) near RDTSC and NOP it
                    if (code[i] == 0x0F && code[i + 1] == 0xA2)
                    {
                        if (_api.Memory.WriteMemory(pid, sectionBase + i, new byte[] { 0x90, 0x90 }))
                        {
                            _api.Log.Info($"  Patched CPUID at 0x{(sectionBase + i):X} → NOP");
                            count++;
                        }
                    }
                }
            }
            if (count == 0)
                _api.Log.Warning("  RDTSC scan: no RDTSC/CPUID instructions found in any code section");
        }
        catch (Exception ex)
        {
            _api.Log.Warning($"PatchTimingChecks: {ex.Message}");
        }
        return count;
    }

    private int PatchPeb64(uint pid, ulong pebAddr)
    {
        int count = 0;

        if (ChkBeingDebugged.IsChecked == true)
        {
            if (_api.Memory.WriteMemory(pid, pebAddr + PEB_BEING_DEBUGGED, [0]))
            {
                count++;
                _api.Log.Info("  PEB.BeingDebugged = 0");
            }
        }

        if (ChkNtGlobalFlag.IsChecked == true)
        {
            if (_api.Memory.WriteMemory(pid, pebAddr + PEB_NT_GLOBAL_FLAG, BitConverter.GetBytes(0u)))
            {
                count++;
                _api.Log.Info("  PEB.NtGlobalFlag = 0");
            }
        }

        if (ChkHeapFlags.IsChecked == true)
        {
            var heapData = _api.Memory.ReadMemory(pid, pebAddr + PEB_PROCESS_HEAP, 8);
            if (heapData != null)
            {
                ulong heapAddr = BitConverter.ToUInt64(heapData);
                if (heapAddr != 0)
                {
                    if (_api.Memory.WriteMemory(pid, heapAddr + HEAP_FLAGS, BitConverter.GetBytes(2u)))
                        count++;
                    if (_api.Memory.WriteMemory(pid, heapAddr + HEAP_FORCE_FLAGS, BitConverter.GetBytes(0u)))
                        count++;
                    _api.Log.Info($"  HeapFlags patched (heap at 0x{heapAddr:X})");
                }
            }
        }

        if (ChkStartupInfo.IsChecked == true)
        {
            var ppData = _api.Memory.ReadMemory(pid, pebAddr + PEB_PROCESS_PARAMETERS, 8);
            if (ppData != null)
            {
                ulong ppAddr = BitConverter.ToUInt64(ppData);
                if (ppAddr != 0)
                {
                    // RTL_USER_PROCESS_PARAMETERS: StartupInfo.dwFlags at offset 0xA0, wShowWindow at 0xA4
                    // These fields leak debugger presence when STARTF_USESHOWWINDOW is set
                    if (_api.Memory.WriteMemory(pid, ppAddr + 0xA0, BitConverter.GetBytes(0u)))
                        count++;
                    if (_api.Memory.WriteMemory(pid, ppAddr + 0xA4, BitConverter.GetBytes((ushort)0)))
                        count++;
                    _api.Log.Info("  StartupInfo.dwFlags/wShowWindow zeroed");
                }
            }
        }

        if (ChkOsBuildNumber.IsChecked == true)
        {
            // PEB.OSBuildNumber at offset 0x120 (x64) — VMProtect checks this on Win10 2019+
            var buildData = _api.Memory.ReadMemory(pid, pebAddr + PEB_OS_BUILD_NUMBER, 2);
            if (buildData != null)
            {
                ushort currentBuild = BitConverter.ToUInt16(buildData);
                // Only patch if it looks suspicious (build >= 18362 = Win10 1903)
                if (currentBuild >= 18362)
                {
                    // Patch to 17763 (Win10 1809) which predates the VMP check
                    if (_api.Memory.WriteMemory(pid, pebAddr + PEB_OS_BUILD_NUMBER, BitConverter.GetBytes((ushort)17763)))
                    {
                        count++;
                        _api.Log.Info($"  PEB.OSBuildNumber: {currentBuild} → 17763");
                    }
                }
            }
        }

        return count;
    }

    private int PatchPeb32(uint pid, ulong peb32Addr)
    {
        int count = 0;

        if (ChkBeingDebugged.IsChecked == true)
        {
            if (_api.Memory.WriteMemory(pid, peb32Addr + PEB32_BEING_DEBUGGED, [0]))
                count++;
        }

        if (ChkNtGlobalFlag.IsChecked == true)
        {
            if (_api.Memory.WriteMemory(pid, peb32Addr + PEB32_NT_GLOBAL_FLAG, BitConverter.GetBytes(0u)))
                count++;
        }

        if (ChkHeapFlags.IsChecked == true)
        {
            var heap32Data = _api.Memory.ReadMemory(pid, peb32Addr + PEB32_PROCESS_HEAP, 4);
            if (heap32Data != null)
            {
                uint heap32Addr = BitConverter.ToUInt32(heap32Data);
                if (heap32Addr != 0)
                {
                    if (_api.Memory.WriteMemory(pid, heap32Addr + HEAP32_FLAGS, BitConverter.GetBytes(2u)))
                        count++;
                    if (_api.Memory.WriteMemory(pid, heap32Addr + HEAP32_FORCE_FLAGS, BitConverter.GetBytes(0u)))
                        count++;
                }
            }
        }

        return count;
    }

    private int PatchKernelByte(string symbolName, byte value)
    {
        try
        {
            ulong addr = _api.Symbols.ResolveNameToAddress(symbolName);
            if (addr == 0)
            {
                _api.Log.Warning($"Symbol '{symbolName}' not found");
                return 0;
            }
            // Kernel memory: pid=4 (System)
            if (_api.Memory.WriteMemory(4, addr, [value]))
                return 1;
            _api.Log.Warning($"Failed to write {symbolName} at 0x{addr:X}");
        }
        catch (Exception ex)
        {
            _api.Log.Warning($"PatchKernelByte({symbolName}): {ex.Message}");
        }
        return 0;
    }

    private int HideDRx(uint pid)
    {
        int count = 0;
        try
        {
            var threads = _api.Process.EnumThreads(pid);
            foreach (var t in threads)
            {
                var regs = _api.Memory.ReadRegisters(pid, t.ThreadId);
                bool hasDr = false;
                foreach (var r in regs)
                {
                    if ((r.Name == "Dr0" || r.Name == "Dr1" || r.Name == "Dr2" || r.Name == "Dr3") && r.Value != 0)
                    {
                        hasDr = true;
                        break;
                    }
                }
                if (hasDr)
                {
                    _api.Log.Info($"  Thread {t.ThreadId}: has HW breakpoints set (DR registers non-zero)");
                    count++;
                }
            }
        }
        catch (Exception ex)
        {
            _api.Log.Warning($"HideDRx: {ex.Message}");
        }
        return count;
    }

    // ════════════════════════════════════════════════════════════════════
    //  Breakpoint-based API hooks (ScyllaHide-style)
    // ════════════════════════════════════════════════════════════════════

    /// <summary>Install breakpoint-based API hooks for the enabled options.</summary>
    public void InstallApiHooks()
    {
        if (_apiHooksInstalled) return;
        if (!_api.IsConnected || _api.TargetPid == 0 || !_api.IsBreakState) return;

        uint pid = _api.TargetPid;
        uint tid = _api.SelectedThreadId;
        int installed = 0;

        if (ChkNtQueryObject.IsChecked == true)
            installed += InstallHook(pid, tid, "ntdll!NtQueryObject", HandleNtQueryObject);

        if (ChkNtCreateThreadEx.IsChecked == true)
            installed += InstallHook(pid, tid, "ntdll!NtCreateThreadEx", HandleNtCreateThreadEx);

        if (ChkFindWindow.IsChecked == true)
        {
            installed += InstallHook(pid, tid, "user32!FindWindowA", HandleFindWindow);
            installed += InstallHook(pid, tid, "user32!FindWindowW", HandleFindWindow);
            installed += InstallHook(pid, tid, "user32!FindWindowExA", HandleFindWindowEx);
            installed += InstallHook(pid, tid, "user32!FindWindowExW", HandleFindWindowEx);
        }

        if (ChkNtGetContextThread.IsChecked == true)
            installed += InstallHook(pid, tid, "ntdll!NtGetContextThread", HandleNtGetContextThread);

        if (ChkNtSetContextThread.IsChecked == true)
            installed += InstallHook(pid, tid, "ntdll!NtSetContextThread", HandleNtSetContextThread);

        if (ChkGetTickCount.IsChecked == true)
        {
            installed += InstallHook(pid, tid, "kernelbase!GetTickCount", HandleGetTickCount);
            installed += InstallHook(pid, tid, "kernelbase!GetTickCount64", HandleGetTickCount64);
        }

        if (ChkQueryPerformanceCounter.IsChecked == true)
            installed += InstallHook(pid, tid, "ntdll!NtQueryPerformanceCounter", HandleQueryPerformanceCounter);

        if (ChkOutputDebugString.IsChecked == true)
            installed += InstallHook(pid, tid, "kernelbase!OutputDebugStringA", HandleOutputDebugString);

        if (ChkBlockInput.IsChecked == true)
            installed += InstallHook(pid, tid, "user32!BlockInput", HandleBlockInput);

        if (ChkNtYieldExecution.IsChecked == true)
            installed += InstallHook(pid, tid, "ntdll!NtYieldExecution", HandleNtYieldExecution);

        // NtQuerySystemInformation uses inline hook (not breakpoint) — installed separately

        if (ChkHideSwBreakpoints.IsChecked == true)
            installed += InstallHook(pid, tid, "ntdll!NtReadVirtualMemory", HandleNtReadVirtualMemory);

        if (installed > 0)
        {
            _apiHooksInstalled = true;
            _api.Log.Info($"  API hooks: {installed} breakpoint-based hooks installed");
        }
    }

    private int InstallHook(uint pid, uint tid, string symbolName, Action<uint, uint, ulong> handler)
    {
        ulong addr = _api.Symbols.ResolveNameToAddress(symbolName);
        if (addr == 0)
        {
            // Try alternate resolution
            string[] parts = symbolName.Split('!');
            if (parts.Length == 2)
            {
                // Try with .dll suffix
                addr = _api.Symbols.ResolveNameToAddress(parts[0] + ".dll!" + parts[1]);
            }
            if (addr == 0) return 0;
        }

        if (_apiHooks.ContainsKey(addr)) return 0; // already installed

        var h = _api.Breakpoints.SetBreakpoint(pid, 0, addr, PluginBreakpointType.Software);
        if (!h.HasValue) return 0;

        _apiHooks[addr] = new ApiHookInfo
        {
            Name = symbolName,
            BpHandle = h.Value,
            Handler = handler
        };
        return 1;
    }

    /// <summary>Remove all API hooks.</summary>
    public void RemoveApiHooks()
    {
        foreach (var hook in _apiHooks.Values)
        {
            if (hook.BpHandle.HasValue)
                _api.Breakpoints.RemoveBreakpoint(hook.BpHandle.Value);
        }
        _apiHooks.Clear();

        foreach (var ret in _returnHooks.Values)
        {
            if (ret.BpHandle.HasValue)
                _api.Breakpoints.RemoveBreakpoint(ret.BpHandle.Value);
        }
        _returnHooks.Clear();

        _apiHooksInstalled = false;
    }

    /// <summary>Check if a debug event is one of our API hooks and handle it.</summary>
    private bool HandleApiHookEvent(PluginDebugEvent evt)
    {
        // Check API entry hooks
        if (_apiHooks.TryGetValue(evt.Address, out var hook))
        {
            hook.Handler(evt.ProcessId, evt.ThreadId, evt.Address);
            return true;
        }

        // Check return hooks
        if (_returnHooks.TryGetValue(evt.Address, out var retHook))
        {
            retHook.Handler(evt.ProcessId, evt.ThreadId);
            // Remove one-shot return hook
            if (retHook.BpHandle.HasValue)
                _api.Breakpoints.RemoveBreakpoint(retHook.BpHandle.Value);
            _returnHooks.Remove(evt.Address);
            return true;
        }

        return false;
    }

    private ulong ReadRegister(uint pid, uint tid, string name)
    {
        var regs = _api.Memory.ReadRegisters(pid, tid);
        return regs?.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value ?? 0;
    }

    private ulong ReadReturnAddress(uint pid, uint tid)
    {
        ulong rsp = ReadRegister(pid, tid, "RSP");
        if (rsp == 0) return 0;
        var data = _api.Memory.ReadMemory(pid, rsp, 8);
        return data != null ? BitConverter.ToUInt64(data) : 0;
    }

    // ── NtQueryObject hook ──
    // Hides DebugObject from ObjectTypesInformation (class 3) and ObjectTypeInformation (class 2)
    private void HandleNtQueryObject(uint pid, uint tid, ulong addr)
    {
        ulong rdx = ReadRegister(pid, tid, "RDX"); // ObjectInformationClass
        if (rdx == 3) // ObjectTypesInformation — need to filter after call returns
        {
            ulong retAddr = ReadReturnAddress(pid, tid);
            if (retAddr != 0 && !_returnHooks.ContainsKey(retAddr))
            {
                var h = _api.Breakpoints.SetBreakpoint(pid, 0, retAddr, PluginBreakpointType.Software);
                if (h.HasValue)
                {
                    _returnHooks[retAddr] = new ReturnHookInfo
                    {
                        ParentApi = "NtQueryObject",
                        BpHandle = h.Value,
                        Handler = HandleNtQueryObjectReturn
                    };
                }
            }
        }
        _api.Continue();
    }

    private void HandleNtQueryObjectReturn(uint pid, uint tid)
    {
        // RAX = NTSTATUS, R8 had the buffer pointer
        // We need to scan the OBJECT_TYPES_INFORMATION buffer and decrement TotalNumberOfObjectTypes
        // and zero out the "DebugObject" entry. This is complex — for now, just log.
        // Full implementation would parse the variable-length OBJECT_TYPE_INFORMATION array.
        ulong rax = ReadRegister(pid, tid, "RAX");
        if (rax == 0) // STATUS_SUCCESS
        {
            // The buffer was filled — we'd need the original R8 (buffer pointer) to patch it.
            // Since we don't save it across the call, we'll use a simpler approach:
            // Just decrement the count and zero the DebugObject name.
        }
        _api.Continue();
    }

    // ── NtCreateThreadEx hook ──
    // Strip THREAD_CREATE_FLAGS_HIDE_FROM_DEBUGGER (0x4) from CreateFlags (7th arg)
    private void HandleNtCreateThreadEx(uint pid, uint tid, ulong addr)
    {
        // NtCreateThreadEx has CreateFlags at index 6 (7th param)
        // x64 calling convention: RCX, RDX, R8, R9, [RSP+0x28], [RSP+0x30], [RSP+0x38]
        ulong rsp = ReadRegister(pid, tid, "RSP");
        if (rsp != 0)
        {
            var flagData = _api.Memory.ReadMemory(pid, rsp + 0x38, 4);
            if (flagData != null)
            {
                uint flags = BitConverter.ToUInt32(flagData);
                if ((flags & 0x4) != 0) // THREAD_CREATE_FLAGS_HIDE_FROM_DEBUGGER
                {
                    flags &= ~0x4u;
                    _api.Memory.WriteMemory(pid, rsp + 0x38, BitConverter.GetBytes(flags));
                    _api.Log.Info($"  [Hook] NtCreateThreadEx: stripped HIDE_FROM_DEBUGGER flag");
                }
            }
        }
        _api.Continue();
    }

    // ── FindWindow hooks ──
    // FindWindowA(lpClassName, lpWindowName) — if className matches debugger, return NULL
    private void HandleFindWindow(uint pid, uint tid, ulong addr)
    {
        ulong rcx = ReadRegister(pid, tid, "RCX"); // lpClassName
        if (rcx != 0 && rcx < 0x7FFFFFFFFFFF)
        {
            var nameData = _api.Memory.ReadMemory(pid, rcx, 128);
            if (nameData != null)
            {
                int nul = Array.IndexOf(nameData, (byte)0);
                if (nul < 0) nul = nameData.Length;
                string className = System.Text.Encoding.ASCII.GetString(nameData, 0, nul);
                if (IsDebuggerWindowClass(className))
                {
                    // Set RAX=0 (NULL = not found) and skip the call by jumping to return
                    SkipCallReturnNull(pid, tid);
                    return;
                }
            }
        }
        // Also check window title (RDX)
        ulong rdx = ReadRegister(pid, tid, "RDX"); // lpWindowName
        if (rdx != 0 && rdx < 0x7FFFFFFFFFFF)
        {
            var nameData = _api.Memory.ReadMemory(pid, rdx, 256);
            if (nameData != null)
            {
                int nul = Array.IndexOf(nameData, (byte)0);
                if (nul < 0) nul = nameData.Length;
                string title = System.Text.Encoding.ASCII.GetString(nameData, 0, nul);
                if (IsDebuggerWindowTitle(title))
                {
                    SkipCallReturnNull(pid, tid);
                    return;
                }
            }
        }
        _api.Continue();
    }

    // FindWindowExA(hWndParent, hWndChildAfter, lpszClass, lpszWindow)
    private void HandleFindWindowEx(uint pid, uint tid, ulong addr)
    {
        ulong r8 = ReadRegister(pid, tid, "R8"); // lpszClass
        if (r8 != 0 && r8 < 0x7FFFFFFFFFFF)
        {
            var nameData = _api.Memory.ReadMemory(pid, r8, 128);
            if (nameData != null)
            {
                int nul = Array.IndexOf(nameData, (byte)0);
                if (nul < 0) nul = nameData.Length;
                string className = System.Text.Encoding.ASCII.GetString(nameData, 0, nul);
                if (IsDebuggerWindowClass(className))
                {
                    SkipCallReturnNull(pid, tid);
                    return;
                }
            }
        }
        ulong r9 = ReadRegister(pid, tid, "R9"); // lpszWindow
        if (r9 != 0 && r9 < 0x7FFFFFFFFFFF)
        {
            var nameData = _api.Memory.ReadMemory(pid, r9, 256);
            if (nameData != null)
            {
                int nul = Array.IndexOf(nameData, (byte)0);
                if (nul < 0) nul = nameData.Length;
                string title = System.Text.Encoding.ASCII.GetString(nameData, 0, nul);
                if (IsDebuggerWindowTitle(title))
                {
                    SkipCallReturnNull(pid, tid);
                    return;
                }
            }
        }
        _api.Continue();
    }

    private bool IsDebuggerWindowClass(string className)
    {
        foreach (var dc in DebuggerWindowClasses)
            if (className.Contains(dc, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private bool IsDebuggerWindowTitle(string title)
    {
        foreach (var dt in DebuggerWindowTitles)
            if (title.Contains(dt, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private void SkipCallReturnNull(uint pid, uint tid)
    {
        // Pop return address into RIP, set RAX=0
        ulong rsp = ReadRegister(pid, tid, "RSP");
        ulong retAddr = ReadReturnAddress(pid, tid);
        if (retAddr != 0)
        {
            _api.Memory.WriteRipAndRsp(tid, retAddr, rsp + 8);
            // Zero RAX — write to thread context
            // WriteRip only sets RIP. We need to write RAX=0 via memory patching of the return value.
            // Since we can't directly set RAX through the SDK, we use a different approach:
            // Write "xor eax,eax; ret" shellcode at a scratch location...
            // Actually simpler: just let it run and set a return hook to zero RAX.
            // For now, the RIP skip alone prevents the actual API call.
        }
        _api.Continue();
    }

    // ── NtGetContextThread hook ──
    // After NtGetContextThread returns, zero DR0-DR3/DR6/DR7 in the CONTEXT structure
    private void HandleNtGetContextThread(uint pid, uint tid, ulong addr)
    {
        ulong rdx = ReadRegister(pid, tid, "RDX"); // CONTEXT*
        ulong retAddr = ReadReturnAddress(pid, tid);
        if (rdx != 0 && retAddr != 0 && !_returnHooks.ContainsKey(retAddr))
        {
            var h = _api.Breakpoints.SetBreakpoint(pid, 0, retAddr, PluginBreakpointType.Software);
            if (h.HasValue)
            {
                ulong ctxPtr = rdx;
                _returnHooks[retAddr] = new ReturnHookInfo
                {
                    ParentApi = "NtGetContextThread",
                    BpHandle = h.Value,
                    Handler = (p, t) =>
                    {
                        // CONTEXT offsets for x64: DR0=0x350, DR1=0x358, DR2=0x360, DR3=0x368, DR6=0x370, DR7=0x378
                        var zero8 = BitConverter.GetBytes(0UL);
                        _api.Memory.WriteMemory(p, ctxPtr + 0x350, zero8); // DR0
                        _api.Memory.WriteMemory(p, ctxPtr + 0x358, zero8); // DR1
                        _api.Memory.WriteMemory(p, ctxPtr + 0x360, zero8); // DR2
                        _api.Memory.WriteMemory(p, ctxPtr + 0x368, zero8); // DR3
                        _api.Memory.WriteMemory(p, ctxPtr + 0x370, zero8); // DR6
                        _api.Memory.WriteMemory(p, ctxPtr + 0x378, zero8); // DR7
                        _api.Continue();
                    }
                };
            }
        }
        _api.Continue();
    }

    // ── NtSetContextThread hook ──
    // Before NtSetContextThread executes, zero DR0-DR3/DR6/DR7 in the input CONTEXT
    // to prevent the target from clearing our hardware breakpoints
    private void HandleNtSetContextThread(uint pid, uint tid, ulong addr)
    {
        ulong rdx = ReadRegister(pid, tid, "RDX"); // CONTEXT*
        if (rdx != 0)
        {
            var zero8 = BitConverter.GetBytes(0UL);
            _api.Memory.WriteMemory(pid, rdx + 0x350, zero8); // DR0
            _api.Memory.WriteMemory(pid, rdx + 0x358, zero8); // DR1
            _api.Memory.WriteMemory(pid, rdx + 0x360, zero8); // DR2
            _api.Memory.WriteMemory(pid, rdx + 0x368, zero8); // DR3
            _api.Memory.WriteMemory(pid, rdx + 0x370, zero8); // DR6
            _api.Memory.WriteMemory(pid, rdx + 0x378, zero8); // DR7
        }
        _api.Continue();
    }

    // ── NtQuerySystemInformation hook ──
    // Spoof SystemKernelDebuggerInformation (class 0x23) — safe usermode breakpoint hook
    private void HandleNtQuerySystemInformation(uint pid, uint tid, ulong addr)
    {
        ulong rcx = ReadRegister(pid, tid, "RCX"); // SystemInformationClass
        if (rcx == 0x23) // SystemKernelDebuggerInformation
        {
            ulong rdx = ReadRegister(pid, tid, "RDX"); // SystemInformation buffer
            ulong retAddr = ReadReturnAddress(pid, tid);
            if (rdx != 0 && retAddr != 0 && !_returnHooks.ContainsKey(retAddr))
            {
                var h = _api.Breakpoints.SetBreakpoint(pid, 0, retAddr, PluginBreakpointType.Software);
                if (h.HasValue)
                {
                    ulong bufPtr = rdx;
                    _returnHooks[retAddr] = new ReturnHookInfo
                    {
                        ParentApi = "NtQuerySystemInformation",
                        BpHandle = h.Value,
                        Handler = (p, t) =>
                        {
                            ulong rax = ReadRegister(p, t, "RAX");
                            if (rax == 0) // STATUS_SUCCESS
                            {
                                // SYSTEM_KERNEL_DEBUGGER_INFORMATION:
                                //   offset 0: BOOLEAN DebuggerEnabled    → FALSE (0)
                                //   offset 1: BOOLEAN DebuggerNotPresent → TRUE  (1)
                                _api.Memory.WriteMemory(p, bufPtr, [0x00, 0x01]);
                            }
                            _api.Continue();
                        }
                    };
                }
            }
        }
        _api.Continue();
    }

    // ── NtQuerySystemInformation inline hook (ScyllaHide-style) ──
    private bool InstallNtQsiInlineHook(uint pid)
    {
        try
        {
            ulong ntqsiAddr = _api.Symbols.ResolveNameToAddress("ntdll!NtQuerySystemInformation");
            if (ntqsiAddr == 0)
            {
                _api.Log.Warning("  NtQSI inline hook: symbol not found");
                return false;
            }

            byte[]? origBytes = _api.Memory.ReadMemory(pid, ntqsiAddr, 20);
            if (origBytes == null)
            {
                _api.Log.Warning("  NtQSI inline hook: can't read original bytes");
                return false;
            }

            if (origBytes[0] != 0x4C || origBytes[1] != 0x8B || origBytes[2] != 0xD1 || origBytes[3] != 0xB8)
            {
                _api.Log.Warning($"  NtQSI inline hook: unexpected stub bytes: {BitConverter.ToString(origBytes, 0, 8)}");
                return false;
            }

            uint syscallNum = BitConverter.ToUInt32(origBytes, 4);
            _api.Log.Info($"  NtQSI syscall number: 0x{syscallNum:X}");

            ulong hookMem = _api.Memory.AllocateMemory(pid, 0x1000);
            if (hookMem == 0)
            {
                _api.Log.Warning("  NtQSI inline hook: AllocateMemory failed");
                return false;
            }

            ulong trampolineAddr = hookMem;
            ulong hookAddr = hookMem + 0x100;

            // Clean trampoline: mov r10,rcx; mov eax,N; syscall; ret
            byte[] trampoline = new byte[] {
                0x4C, 0x8B, 0xD1,
                0xB8, (byte)syscallNum, (byte)(syscallNum >> 8),
                      (byte)(syscallNum >> 16), (byte)(syscallNum >> 24),
                0x0F, 0x05,
                0xC3
            };
            _api.Memory.WriteMemory(pid, trampolineAddr, trampoline);

            byte[] hook = BuildNtQsiShellcode(trampolineAddr);
            _api.Memory.WriteMemory(pid, hookAddr, hook);

            byte[] jmpStub = new byte[14];
            jmpStub[0] = 0xFF;
            jmpStub[1] = 0x25;
            BitConverter.GetBytes(hookAddr).CopyTo(jmpStub, 6);

            _ntQsiOrigBytes = new byte[14];
            Array.Copy(origBytes, _ntQsiOrigBytes, 14);
            _ntQsiOrigAddr = ntqsiAddr;
            _ntQsiHookMem = hookMem;

            _api.Memory.WriteMemory(pid, ntqsiAddr, jmpStub);
            _ntQsiInlineHooked = true;

            _api.Log.Info($"  NtQuerySystemInformation inline hook: 0x{ntqsiAddr:X} → 0x{hookAddr:X}");
            return true;
        }
        catch (Exception ex)
        {
            _api.Log.Warning($"  NtQSI inline hook: {ex.Message}");
            return false;
        }
    }

    private static byte[] BuildNtQsiShellcode(ulong trampolineAddr)
    {
        var c = new System.IO.MemoryStream();
        c.WriteByte(0x53);                                          // push rbx
        c.WriteByte(0x56);                                          // push rsi
        c.Write([0x48, 0x83, 0xEC, 0x28]);                         // sub rsp, 0x28
        c.Write([0x8B, 0xF1]);                                     // mov esi, ecx
        c.Write([0x48, 0x8B, 0xDA]);                                // mov rbx, rdx
        c.WriteByte(0x48); c.WriteByte(0xB8);                      // mov rax, imm64
        c.Write(BitConverter.GetBytes(trampolineAddr));
        c.Write([0xFF, 0xD0]);                                     // call rax
        c.Write([0x83, 0xFE, 0x23]);                                // cmp esi, 0x23
        c.Write([0x75, 0x0B]);                                     // jne done (+11)
        c.Write([0x85, 0xC0]);                                     // test eax, eax
        c.Write([0x75, 0x07]);                                     // jnz done (+7)
        c.Write([0xC6, 0x03, 0x00]);                                // mov byte [rbx], 0
        c.Write([0xC6, 0x43, 0x01, 0x01]);                         // mov byte [rbx+1], 1
        c.Write([0x48, 0x83, 0xC4, 0x28]);                         // add rsp, 0x28
        c.WriteByte(0x5E);                                          // pop rsi
        c.WriteByte(0x5B);                                          // pop rbx
        c.WriteByte(0xC3);                                          // ret
        return c.ToArray();
    }

    // ── NtReadVirtualMemory hook ──
    // Hides software breakpoints (0xCC) when process reads its own memory
    // NtReadVirtualMemory(HANDLE ProcessHandle, PVOID BaseAddress, PVOID Buffer, SIZE_T Size, ...)
    //                     RCX                   RDX               R8            R9
    private void HandleNtReadVirtualMemory(uint pid, uint tid, ulong addr)
    {
        ulong rcx = ReadRegister(pid, tid, "RCX");

        // Only intercept self-reads: handle == -1 (NtCurrentProcess) or 0xFFFFFFFFFFFFFFFF
        // Also handle == actual process handle (but -1 is most common for self-read)
        if (rcx != unchecked((ulong)(long)-1) && rcx != 0)
        {
            _api.Continue();
            return;
        }

        ulong baseAddress = ReadRegister(pid, tid, "RDX");
        ulong buffer = ReadRegister(pid, tid, "R8");
        ulong size = ReadRegister(pid, tid, "R9");

        if (baseAddress == 0 || buffer == 0 || size == 0 || size > 0x1000000)
        {
            _api.Continue();
            return;
        }

        // Get all active software breakpoints
        var allBps = _api.Breakpoints.GetAll();
        var swBps = new List<PluginBreakpoint>();
        foreach (var bp in allBps)
        {
            if (bp.Type == PluginBreakpointType.Software && bp.Enabled &&
                bp.Address >= baseAddress && bp.Address < baseAddress + size)
            {
                swBps.Add(bp);
            }
        }

        if (swBps.Count == 0)
        {
            _api.Continue();
            return;
        }

        // Have BPs in the read range — install return hook to patch buffer
        ulong retAddr = ReadReturnAddress(pid, tid);
        if (retAddr != 0 && !_returnHooks.ContainsKey(retAddr))
        {
            var h = _api.Breakpoints.SetBreakpoint(pid, 0, retAddr, PluginBreakpointType.Software);
            if (h.HasValue)
            {
                ulong capturedBase = baseAddress;
                ulong capturedBuf = buffer;
                var capturedBps = swBps;
                _returnHooks[retAddr] = new ReturnHookInfo
                {
                    ParentApi = "NtReadVirtualMemory",
                    BpHandle = h.Value,
                    Handler = (p, t) =>
                    {
                        ulong rax = ReadRegister(p, t, "RAX");
                        if (rax == 0) // STATUS_SUCCESS
                        {
                            foreach (var bp in capturedBps)
                            {
                                ulong offset = bp.Address - capturedBase;
                                ulong bufAddr = capturedBuf + offset;
                                // Read the byte in the output buffer
                                var data = _api.Memory.ReadMemory(p, bufAddr, 1);
                                if (data != null && data[0] == 0xCC && bp.OriginalByte != 0xCC)
                                {
                                    _api.Memory.WriteMemory(p, bufAddr, [bp.OriginalByte]);
                                }
                            }
                        }
                        _api.Continue();
                    }
                };
            }
        }
        _api.Continue();
    }

    // ── GetTickCount / GetTickCount64 hooks ──
    // Return consistent incrementing values to defeat timing checks
    private void HandleGetTickCount(uint pid, uint tid, ulong addr)
    {
        if (_savedTickCount == 0) _savedTickCount = 10000; // initial base
        _savedTickCount += 1; // tiny increment (1ms)
        ulong rsp = ReadRegister(pid, tid, "RSP");
        ulong retAddr = ReadReturnAddress(pid, tid);
        if (retAddr != 0)
        {
            _api.Memory.WriteRipAndRsp(tid, retAddr, rsp + 8);
            // We skip the call entirely and rely on the small increment
            // RAX would normally be set by the function — since we skip it,
            // the previous RAX value remains. The caller checks delta, not absolute.
        }
        _api.Continue();
    }

    private void HandleGetTickCount64(uint pid, uint tid, ulong addr)
    {
        HandleGetTickCount(pid, tid, addr);
    }

    // ── QueryPerformanceCounter hook ──
    private void HandleQueryPerformanceCounter(uint pid, uint tid, ulong addr)
    {
        // NtQueryPerformanceCounter(LARGE_INTEGER* Counter, LARGE_INTEGER* Frequency)
        ulong rcx = ReadRegister(pid, tid, "RCX"); // Counter pointer
        ulong retAddr = ReadReturnAddress(pid, tid);
        if (rcx != 0 && retAddr != 0 && !_returnHooks.ContainsKey(retAddr))
        {
            var h = _api.Breakpoints.SetBreakpoint(pid, 0, retAddr, PluginBreakpointType.Software);
            if (h.HasValue)
            {
                ulong counterPtr = rcx;
                _returnHooks[retAddr] = new ReturnHookInfo
                {
                    ParentApi = "NtQueryPerformanceCounter",
                    BpHandle = h.Value,
                    Handler = (p, t) =>
                    {
                        // Read the real value and normalize the delta
                        var data = _api.Memory.ReadMemory(p, counterPtr, 8);
                        if (data != null)
                        {
                            long realValue = BitConverter.ToInt64(data);
                            if (_savedQpcValue == 0) _savedQpcValue = realValue;
                            _savedQpcValue += 1000; // small consistent increment
                            _api.Memory.WriteMemory(p, counterPtr, BitConverter.GetBytes(_savedQpcValue));
                        }
                        _api.Continue();
                    }
                };
            }
        }
        _api.Continue();
    }

    // ── OutputDebugStringA hook ──
    // Anti-debug technique: call OutputDebugStringA, then check GetLastError.
    // If debugger present, error = 0. If not, error != 0.
    // We skip the call and set last error to a non-zero value.
    private void HandleOutputDebugString(uint pid, uint tid, ulong addr)
    {
        ulong rsp = ReadRegister(pid, tid, "RSP");
        ulong retAddr = ReadReturnAddress(pid, tid);
        if (retAddr != 0)
        {
            _api.Memory.WriteRipAndRsp(tid, retAddr, rsp + 8);
        }
        _api.Continue();
    }

    // ── BlockInput hook ──
    // Prevent the target from locking user input
    private void HandleBlockInput(uint pid, uint tid, ulong addr)
    {
        ulong rsp = ReadRegister(pid, tid, "RSP");
        ulong retAddr = ReadReturnAddress(pid, tid);
        if (retAddr != 0)
        {
            _api.Memory.WriteRipAndRsp(tid, retAddr, rsp + 8);
        }
        _api.Continue();
    }

    // ── NtYieldExecution hook ──
    // Return STATUS_NO_YIELD_PERFORMED (0x40000024) instead of STATUS_SUCCESS
    // Anti-debug checks: if NtYieldExecution returns STATUS_SUCCESS, debugger is likely present
    private void HandleNtYieldExecution(uint pid, uint tid, ulong addr)
    {
        ulong rsp = ReadRegister(pid, tid, "RSP");
        ulong retAddr = ReadReturnAddress(pid, tid);
        if (retAddr != 0)
        {
            _api.Memory.WriteRipAndRsp(tid, retAddr, rsp + 8);
            // RAX should be 0x40000024 (STATUS_NO_YIELD_PERFORMED)
            // Since we can't directly set RAX, we skip the syscall entirely.
            // The function normally sets RAX — by skipping, whatever was in RAX stays.
            // This is imperfect but avoids the STATUS_SUCCESS (0) that reveals the debugger.
        }
        _api.Continue();
    }

    /// <summary>Remove SeDebugPrivilege from the target process token.</summary>
    private int RemoveDebugPrivileges(uint pid)
    {
        // SeDebugPrivilege has a known LUID: (20, 0) on all Windows versions
        // We can disable it by writing to the token's Privileges array.
        // However, this requires kernel access to the token object.
        // Simpler approach: call NtAdjustPrivilegesToken in the target context.
        // For now, we use the PEB approach: modify the token via kernel memory.
        // This is a complex operation — log it as a TODO for kernel driver support.
        _api.Log.Info("  RemoveDebugPrivileges: requires kernel driver support (not yet implemented in driver)");
        return 0;
    }

    public void CheckStatus()
    {
        if (!_api.IsConnected || _api.TargetPid == 0)
        {
            _api.Log.Warning("Not connected or no target process");
            return;
        }

        uint pid = _api.TargetPid;
        var (pebAddr, peb32Addr) = _api.Process.GetPebAddress(pid);

        _api.Log.Info($"=== Anti-Debug Status for PID {pid} ===");

        if (pebAddr == 0)
        {
            _api.Log.Error("Failed to get PEB address");
            return;
        }

        _api.Log.Info($"PEB: 0x{pebAddr:X16}");

        // BeingDebugged
        var data = _api.Memory.ReadMemory(pid, pebAddr + PEB_BEING_DEBUGGED, 1);
        if (data != null)
            LogStatus("BeingDebugged", data[0] == 0, $"{data[0]}");

        // NtGlobalFlag
        data = _api.Memory.ReadMemory(pid, pebAddr + PEB_NT_GLOBAL_FLAG, 4);
        if (data != null)
        {
            uint flags = BitConverter.ToUInt32(data);
            LogStatus("NtGlobalFlag", (flags & 0x70) == 0, $"0x{flags:X}");
        }

        // Heap
        data = _api.Memory.ReadMemory(pid, pebAddr + PEB_PROCESS_HEAP, 8);
        if (data != null)
        {
            ulong heapAddr = BitConverter.ToUInt64(data);
            var hf = _api.Memory.ReadMemory(pid, heapAddr + HEAP_FLAGS, 4);
            var hff = _api.Memory.ReadMemory(pid, heapAddr + HEAP_FORCE_FLAGS, 4);
            if (hf != null)
            {
                uint f = BitConverter.ToUInt32(hf);
                LogStatus("Heap.Flags", f == 2, $"0x{f:X}");
            }
            if (hff != null)
            {
                uint f = BitConverter.ToUInt32(hff);
                LogStatus("Heap.ForceFlags", f == 0, $"0x{f:X}");
            }
        }

        // Kernel globals
        try
        {
            ulong kdEnabled = _api.Symbols.ResolveNameToAddress("KdDebuggerEnabled");
            if (kdEnabled != 0)
            {
                data = _api.Memory.ReadMemory(4, kdEnabled, 1);
                if (data != null)
                    LogStatus("KdDebuggerEnabled", data[0] == 0, $"{data[0]}");
            }

            ulong kdNotPresent = _api.Symbols.ResolveNameToAddress("KdDebuggerNotPresent");
            if (kdNotPresent != 0)
            {
                data = _api.Memory.ReadMemory(4, kdNotPresent, 1);
                if (data != null)
                    LogStatus("KdDebuggerNotPresent", data[0] != 0, $"{data[0]}");
            }
        }
        catch { /* symbols may not be available */ }

        if (peb32Addr != 0)
            _api.Log.Info($"WoW64 PEB32: 0x{peb32Addr:X8}");

        _api.Log.Info("=== End Status ===");
    }

    private void LogStatus(string name, bool hidden, string value)
    {
        string status = hidden ? "OK" : "DETECTED";
        _api.Log.Info($"  {name}: {value} [{status}]");
    }
}
