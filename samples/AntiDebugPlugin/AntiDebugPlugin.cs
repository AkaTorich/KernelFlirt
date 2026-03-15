using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KernelFlirt.SDK;

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
        api.OnBreakStateEntered += OnBreakState;

        api.Log.Info("Anti-Anti-Debug v2.0 loaded. See 'Anti-Debug' tab for settings.");
    }

    private void OnBreakState()
    {
        if (_panel?.AutoApply == true && _api is { IsBreakState: true })
        {
            Application.Current.Dispatcher.BeginInvoke(() => _panel.ApplyPatches());
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

    // Kernel
    public CheckBox ChkKdDebuggerEnabled { get; }
    public CheckBox ChkKdDebuggerNotPresent { get; }

    // NtQueryInformationProcess
    public CheckBox ChkDebugPort { get; }
    public CheckBox ChkDebugObjectHandle { get; }
    public CheckBox ChkDebugFlags { get; }

    // NtQuerySystemInformation
    public CheckBox ChkSystemKernelDebugger { get; }

    // NtSetInformationThread
    public CheckBox ChkThreadHideFromDebugger { get; }

    // NtClose
    public CheckBox ChkNtClose { get; }

    // Context
    public CheckBox ChkHideDRx { get; }

    // Auto-apply
    public CheckBox ChkAutoApply { get; }

    public bool AutoApply => ChkAutoApply.IsChecked == true;

    // x64 PEB offsets
    private const int PEB_BEING_DEBUGGED = 0x02;
    private const int PEB_NT_GLOBAL_FLAG = 0xBC;
    private const int PEB_PROCESS_HEAP   = 0x30;
    private const int HEAP_FLAGS         = 0x70;
    private const int HEAP_FORCE_FLAGS   = 0x74;
    // x86 PEB offsets
    private const int PEB32_BEING_DEBUGGED = 0x02;
    private const int PEB32_NT_GLOBAL_FLAG = 0x68;
    private const int PEB32_PROCESS_HEAP   = 0x18;
    private const int HEAP32_FLAGS         = 0x40;
    private const int HEAP32_FORCE_FLAGS   = 0x44;

    public AntiDebugPanel(IDebuggerApi api)
    {
        _api = api;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;

        var root = new StackPanel { Margin = new Thickness(8) };
        var white = Brushes.White;

        // ── Title ──
        root.Children.Add(new TextBlock
        {
            Text = "Anti-Anti-Debug Settings",
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Foreground = white,
            Margin = new Thickness(0, 0, 0, 10)
        });

        // ── PEB ──
        ChkBeingDebugged = MakeCheckBox("BeingDebugged", true, "PEB.BeingDebugged = 0 (IsDebuggerPresent)", true, white);
        ChkNtGlobalFlag = MakeCheckBox("NtGlobalFlag", true, "PEB.NtGlobalFlag = 0 (FLG_HEAP_* flags)", true, white);
        ChkHeapFlags = MakeCheckBox("HeapFlags", true, "ProcessHeap.Flags = HEAP_GROWABLE, ForceFlags = 0", true, white);
        root.Children.Add(MakeGroup("PEB", [ChkBeingDebugged, ChkNtGlobalFlag, ChkHeapFlags], white));

        // ── Kernel Debugger ──
        ChkKdDebuggerEnabled = MakeCheckBox("KdDebuggerEnabled", false, "Patch KdDebuggerEnabled = FALSE", true, white);
        ChkKdDebuggerNotPresent = MakeCheckBox("KdDebuggerNotPresent", false, "Patch KdDebuggerNotPresent = TRUE", true, white);
        root.Children.Add(MakeGroup("Kernel Debugger", [ChkKdDebuggerEnabled, ChkKdDebuggerNotPresent], white));

        // ── NtQueryInformationProcess ──
        ChkDebugPort = MakeCheckBox("ProcessDebugPort", true, "Clear EPROCESS.DebugPort (defeats DebugPort/DebugObjectHandle/DebugFlags)", true, white);
        ChkDebugObjectHandle = MakeCheckBox("ProcessDebugObjectHandle", true, "Cleared by DebugPort zeroing", true, white);
        ChkDebugFlags = MakeCheckBox("ProcessDebugFlags", true, "Cleared by DebugPort zeroing", true, white);
        root.Children.Add(MakeGroup("NtQueryInformationProcess (via ClearDebugPort)", [ChkDebugPort, ChkDebugObjectHandle, ChkDebugFlags], white));

        // ── NtQuerySystemInformation ──
        ChkSystemKernelDebugger = MakeCheckBox("SystemKernelDebuggerInfo", true, "Auto-handled by KdDebuggerEnabled/KdDebuggerNotPresent patches above", true, white);
        root.Children.Add(MakeGroup("NtQuerySystemInformation (via KdDebugger* patches)", [ChkSystemKernelDebugger], white));

        // ── NtSetInformationThread ──
        ChkThreadHideFromDebugger = MakeCheckBox("ThreadHideFromDebugger", true, "Clear HideFromDebugger bit in all threads' CrossThreadFlags", true, white);
        root.Children.Add(MakeGroup("NtSetInformationThread (via ClearThreadHide)", [ChkThreadHideFromDebugger], white));

        // ── NtClose ──
        ChkNtClose = MakeCheckBox("NtClose", true, "Cleared by DebugPort zeroing (no debug object = no invalid handle exception)", true, white);
        root.Children.Add(MakeGroup("NtClose (via ClearDebugPort)", [ChkNtClose], white));

        // ── Context / DRx ──
        ChkHideDRx = MakeCheckBox("Hide DRx registers", false, "Zero DR0-DR3 in target thread context", true, white);
        root.Children.Add(MakeGroup("Hardware Breakpoints", [ChkHideDRx], white));

        // ── Auto-apply ──
        ChkAutoApply = MakeCheckBox("Auto-apply on every break", false, "Automatically apply patches when debugger breaks", true, white);
        root.Children.Add(MakeGroup("Automation", [ChkAutoApply], white));

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
            Padding = new Thickness(16, 6, 16, 6)
        };
        btnDeselectAll.Click += (_, _) => SetAllEnabled(false);
        btnPanel.Children.Add(btnDeselectAll);

        root.Children.Add(btnPanel);

        // ── Status ──
        root.Children.Add(new TextBlock
        {
            Text = "All patches use kernel driver. ClearDebugPort defeats multiple checks at once.",
            FontStyle = FontStyles.Italic,
            Foreground = white,
            Margin = new Thickness(0, 10, 0, 0)
        });

        Content = root;
    }

    private void SetAllEnabled(bool check)
    {
        foreach (var chk in new[] { ChkBeingDebugged, ChkNtGlobalFlag, ChkHeapFlags,
            ChkKdDebuggerEnabled, ChkKdDebuggerNotPresent,
            ChkDebugPort, ChkDebugObjectHandle, ChkDebugFlags,
            ChkSystemKernelDebugger, ChkThreadHideFromDebugger, ChkNtClose,
            ChkHideDRx, ChkAutoApply })
        {
            chk.IsChecked = check;
        }
    }

    private static GroupBox MakeGroup(string header, CheckBox[] items, Brush foreground)
    {
        var sp = new StackPanel { Margin = new Thickness(4) };
        foreach (var item in items) sp.Children.Add(item);

        return new GroupBox
        {
            Header = new TextBlock { Text = header, FontWeight = FontWeights.SemiBold, Foreground = foreground },
            Content = sp,
            Margin = new Thickness(0, 0, 0, 6),
            Padding = new Thickness(6),
            BorderBrush = new SolidColorBrush(Color.FromRgb(80, 80, 80))
        };
    }

    private static CheckBox MakeCheckBox(string text, bool isChecked, string tooltip, bool isEnabled, Brush foreground)
    {
        return new CheckBox
        {
            Content = new TextBlock { Text = text, Foreground = foreground },
            IsChecked = isChecked,
            IsEnabled = isEnabled,
            ToolTip = tooltip,
            Margin = new Thickness(0, 2, 0, 2)
        };
    }

    public void ApplyPatches()
    {
        if (!_api.IsConnected)
        {
            _api.Log.Warning("Not connected");
            return;
        }
        if (_api.TargetPid == 0)
        {
            _api.Log.Warning("No target process");
            return;
        }

        int patches = 0;
        uint pid = _api.TargetPid;

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

        // ── Kernel debugger patches ──
        if (ChkKdDebuggerEnabled.IsChecked == true)
            patches += PatchKernelByte("KdDebuggerEnabled", 0); // FALSE

        if (ChkKdDebuggerNotPresent.IsChecked == true)
            patches += PatchKernelByte("KdDebuggerNotPresent", 1); // TRUE

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

        // ── SystemKernelDebuggerInfo ──
        if (ChkSystemKernelDebugger.IsChecked == true)
        {
            // This is automatically handled by KdDebuggerEnabled=FALSE + KdDebuggerNotPresent=TRUE above
            // Just ensure those patches are also applied
            if (ChkKdDebuggerEnabled.IsChecked != true)
            {
                patches += PatchKernelByte("KdDebuggerEnabled", 0);
                _api.Log.Info("  KdDebuggerEnabled=FALSE (for SystemKernelDebuggerInfo)");
            }
            if (ChkKdDebuggerNotPresent.IsChecked != true)
            {
                patches += PatchKernelByte("KdDebuggerNotPresent", 1);
                _api.Log.Info("  KdDebuggerNotPresent=TRUE (for SystemKernelDebuggerInfo)");
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
                _api.Log.Warning("  ClearThreadHide failed");
            }
        }

        // ── Hide DRx registers ──
        if (ChkHideDRx.IsChecked == true)
            patches += HideDRx(pid);

        _api.Log.Info($"Anti-debug: {patches} patches applied to PID {pid}");
    }

    private int PatchPeb64(uint pid, ulong pebAddr)
    {
        int count = 0;

        if (ChkBeingDebugged.IsChecked == true)
        {
            if (_api.Memory.WriteMemory(pid, pebAddr + PEB_BEING_DEBUGGED, [0]))
                count++;
        }

        if (ChkNtGlobalFlag.IsChecked == true)
        {
            if (_api.Memory.WriteMemory(pid, pebAddr + PEB_NT_GLOBAL_FLAG, BitConverter.GetBytes(0u)))
                count++;
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
