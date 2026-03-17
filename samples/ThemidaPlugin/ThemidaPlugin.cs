// Themida Unpacker v5 — full Magicmida port for KernelFlirt
// Ported from MagicmidaCSharp: OEP via PAGE_NOACCESS, IAT trace via ContinueMode=4,
// full Dumper with import directory rebuild and forward resolution.

using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KernelFlirt.SDK;
using Iced.Intel;

namespace ThemidaPlugin;

// ════════════════════════════════════════════════════════════════════════════
//  Plugin entry point
// ════════════════════════════════════════════════════════════════════════════

public class ThemidaPlugin : IKernelFlirtPlugin
{
    public string Name => "Themida Unpacker";
    public string Description => "Magicmida-based Themida/WinLicense unpacker: OEP + IAT trace + dump";
    public string Version => "5.0";

    private IDebuggerApi? _api;
    private ThemidaPanel? _panel;

    public void Initialize(IDebuggerApi api)
    {
        _api = api;
        _panel = new ThemidaPanel(api);
        api.UI.AddToolPanel("Themida", _panel);
        api.UI.AddMenuItem("Themida: Detect", () => _panel.DetectProtector());
        api.UI.AddMenuItem("Themida: Unpack", () => _panel.StartUnpacking());
        api.Log.Info("Themida Unpacker v5.0 loaded (Magicmida engine).");
    }

    public void Shutdown() { }
}

// ════════════════════════════════════════════════════════════════════════════
//  Main panel — state machine, OEP detection, IAT tracing
// ════════════════════════════════════════════════════════════════════════════

public class ThemidaPanel : ScrollViewer
{
    private readonly IDebuggerApi _api;

    // ── PE info ──
    private bool _detected;
    private bool _is64;
    private int _ptrSize;
    private ulong _imageBase;
    private uint _imageSize;
    private ulong _imageBoundary;
    private ushort _majorLinkerVersion;
    private ulong _entryPointRva;

    // Sections — from PE header
    private ulong _textBase;        // first code section VA
    private uint _textRva;          // first code section RVA
    private uint _textVSize;        // first code section virtual size
    private ulong _baseOfData;      // textBase + SizeOfCode (end of code region)
    private ulong _tmBase, _tmEnd;  // .themida/.winlice combined range (for trace predicate)
    private bool _isVmOep;
    private List<PeSect> _sections = new();
    private List<ulong> _guardAddrs = new(); // addresses accessed during guard phase (from Magicmida FGuardAddrs)
    internal record PeSect(string Name, uint Rva, uint VirtualSize, uint Chars);

    // ── State machine ──
    private enum Phase { Idle, TextGuarded, TextStepRearm, OepStepThrough, IatTracing, Done }
    private Phase _phase = Phase.Idle;

    // OEP finding
    private int _guardHitCount;
    private uint _guardOldProt;
    private ulong _firstTextExecAddr;
    private ulong _oepAddr;
    private uint? _oepBpHandle;

    // TLS callback tracking (Magicmida _tlsTotal/_tlsCounter/_tmGuard)
    private int _tlsTotal;
    private int _tlsCounter;
    private bool _tmGuard;

    // MSVC virtualized OEP (Magicmida _traceMSVCOEP/_msvcInitCookie/_msvcOEP)
    private bool _traceMsvcOep;
    private ulong _msvcInitCookie;
    private ulong _msvcOep;

    // IAT tracing (replaces Magicmida Tracer + TraceImports)
    private RemoteDumper? _dumper;
    private ulong _iatBase;
    private int _iatCount;
    private ulong[] _iatData = [];
    private int _iatIdx;
    private int _iatResolvedCount, _iatFailedCount;
    private ulong _savedRip, _savedRsp;
    private List<uint> _suspendedTids = new();


    // TraceIsAtAPI state (Magicmida)
    private ulong _sleepApi, _lstrlenApi;
    private ulong _traceStartSP;
    private bool _vmProbePhase; // true = first phase (5K steps to detect VM), false = full trace
    private int _traceMaxSteps;
    private int _antiTraceSkips; // count of anti-trace fake call skips per wrapper

    // UI
    private TextBlock _statusText;
    private CheckBox _chkAutoIat, _chkAutoDump;

    public ThemidaPanel(IDebuggerApi api)
    {
        _api = api;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;

        var root = new StackPanel { Margin = new Thickness(8) };
        var fg = Brushes.White;

        root.Children.Add(new TextBlock
        {
            Text = "Themida Unpacker v5 (Magicmida)",
            FontSize = 16, FontWeight = FontWeights.Bold,
            Foreground = fg, Margin = new Thickness(0, 0, 0, 10)
        });

        _chkAutoIat = MakeCb("Auto-fix IAT after OEP", true, fg);
        _chkAutoDump = MakeCb("Auto-dump PE after IAT fix", true, fg);
        root.Children.Add(Grp("Settings", [_chkAutoIat, _chkAutoDump], fg));

        _statusText = new TextBlock
        {
            Text = "Idle — Detect first",
            Foreground = Brushes.LightGreen,
            FontFamily = new FontFamily("Consolas"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(4)
        };
        root.Children.Add(Grp("Status", [_statusText], fg));

        var btns = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        Btn(btns, "Detect", DetectProtector);
        Btn(btns, "Unpack", StartUnpacking);
        Btn(btns, "Fix IAT", () => ManualFixIat());
        Btn(btns, "Dump PE", () => DumpPe());
        Btn(btns, "Stop", StopUnpacking);
        root.Children.Add(btns);

        Content = root;
        api.OnBeforeRun += OnBeforeRun;
        api.OnDebugEventFilter += OnDebugEventFilter;
    }

    // ════════════════════════════════════════════════════════════════
    //  Detection — scan PE for .themida/.boot sections
    // ════════════════════════════════════════════════════════════════

    public void DetectProtector()
    {
        if (!_api.IsConnected || !_api.IsBreakState)
        { _api.Log.Warning("Must be connected and in Break state."); return; }

        uint pid = _api.TargetPid;
        var modules = _api.Symbols.GetModules();
        if (modules.Count == 0) { _api.Log.Warning("No modules."); return; }

        var main = modules[0];
        _imageBase = main.BaseAddress;
        _imageSize = main.Size;
        _imageBoundary = _imageBase + _imageSize;

        var hdr = _api.Memory.ReadMemory(pid, _imageBase, 0x1000);
        if (hdr == null || hdr.Length < 0x40 || hdr[0] != 'M' || hdr[1] != 'Z')
        { _api.Log.Error("Bad PE header."); return; }

        uint lfanew = BitConverter.ToUInt32(hdr, 0x3C);
        if (lfanew + 0x18 > hdr.Length) { _api.Log.Error("Bad PE."); return; }

        ushort numSect = BitConverter.ToUInt16(hdr, (int)lfanew + 6);
        ushort optSize = BitConverter.ToUInt16(hdr, (int)lfanew + 0x14);
        ushort magic = BitConverter.ToUInt16(hdr, (int)lfanew + 0x18);
        _is64 = magic == 0x20B;
        _ptrSize = _is64 ? 8 : 4;
        _majorLinkerVersion = hdr[(int)lfanew + 0x1A];
        _entryPointRva = BitConverter.ToUInt32(hdr, (int)lfanew + 0x28);

        // SizeOfCode from optional header
        uint sizeOfCode = BitConverter.ToUInt32(hdr, (int)lfanew + 0x1C);

        int sectOff = (int)lfanew + 4 + 20 + optSize;
        _sections.Clear();
        _textBase = 0; _tmBase = 0; _tmEnd = 0; _baseOfData = 0;
        _textRva = 0; _textVSize = 0;
        bool foundTm = false;

        var sb = new StringBuilder();
        sb.AppendLine($"PE: {(_is64 ? "x64" : "x86")}, Linker {_majorLinkerVersion}.x, {numSect} sections");

        for (int i = 0; i < numSect; i++)
        {
            int o = sectOff + i * 40;
            if (o + 40 > hdr.Length) break;

            string name = Encoding.ASCII.GetString(hdr, o, 8).TrimEnd('\0');
            uint rva = BitConverter.ToUInt32(hdr, o + 12);
            uint vsz = BitConverter.ToUInt32(hdr, o + 8);
            uint ch = BitConverter.ToUInt32(hdr, o + 36);
            _sections.Add(new PeSect(name, rva, vsz, ch));

            string nl = name.ToLowerInvariant().Trim();
            ulong sBase = _imageBase + rva;
            ulong sEnd = sBase + vsz;

            if (nl is ".themida" or "themida" or ".winlice" or "winlice")
            {
                foundTm = true;
                if (_tmBase == 0) { _tmBase = sBase; _tmEnd = sEnd; }
                else { _tmBase = Math.Min(_tmBase, sBase); _tmEnd = Math.Max(_tmEnd, sEnd); }
            }
            else if (nl is ".boot" or "boot")
            {
                foundTm = true;
                if (_tmBase == 0) { _tmBase = sBase; _tmEnd = sEnd; }
                else { _tmBase = Math.Min(_tmBase, sBase); _tmEnd = Math.Max(_tmEnd, sEnd); }
            }

            // First code section = .text
            if (_textBase == 0 && (ch & 0x20000000) != 0 &&
                nl is not (".themida" or "themida" or ".winlice" or "winlice" or ".boot" or "boot"))
            {
                _textBase = sBase;
                _textRva = rva;
                _textVSize = vsz;
                // BaseOfData from Magicmida: sect[0].VirtualAddress + SizeOfCode
                _baseOfData = _imageBase + rva + sizeOfCode;
            }

            string perm = ((ch & 0x20000000) != 0 ? "X" : "") +
                          ((ch & 0x40000000) != 0 ? "R" : "") +
                          ((ch & 0x80000000) != 0 ? "W" : "");
            sb.AppendLine($"  [{i}] {name,-10} 0x{rva:X8} sz=0x{vsz:X8} {perm}");
        }

        _api.Log.Info(sb.ToString());

        if (!foundTm)
        {
            _api.Log.Warning("No .themida/.boot sections found.");
            _detected = false;
            SetStatus("Not Themida");
            return;
        }

        // Resolve Sleep/lstrlen addresses for anti-trace skip (Magicmida uses GetProcAddress)
        _sleepApi = _api.Symbols.ResolveNameToAddress("kernel32!Sleep");
        if (_sleepApi == 0) _sleepApi = _api.Symbols.ResolveNameToAddress("kernel32.dll!Sleep");
        // Original uses "lstrlen" (not "lstrlenA") — try both
        _lstrlenApi = _api.Symbols.ResolveNameToAddress("kernel32!lstrlen");
        if (_lstrlenApi == 0) _lstrlenApi = _api.Symbols.ResolveNameToAddress("kernel32!lstrlenA");
        if (_lstrlenApi == 0) _lstrlenApi = _api.Symbols.ResolveNameToAddress("kernel32.dll!lstrlenA");

        // TLS callback tracking (Magicmida TMInit: parse TLS directory)
        _tlsTotal = 0;
        _tlsCounter = 0;
        _tmGuard = false;
        _traceMsvcOep = false;
        int tlsDirOff = _is64 ? (int)lfanew + 4 + 20 + 0x90 : (int)lfanew + 4 + 20 + 0x80; // IMAGE_DIRECTORY_ENTRY_TLS = 9
        if (tlsDirOff + 8 <= hdr.Length)
        {
            uint tlsRva = BitConverter.ToUInt32(hdr, tlsDirOff);
            uint tlsSize = BitConverter.ToUInt32(hdr, tlsDirOff + 4);
            if (tlsSize > 0 && tlsRva > 0)
            {
                int tlsStructSize = _is64 ? 40 : 24; // sizeof(IMAGE_TLS_DIRECTORY64/32)
                var tlsData = _api.Memory.ReadMemory(pid, _imageBase + tlsRva, (uint)Math.Min(tlsSize, (uint)tlsStructSize));
                if (tlsData != null && tlsData.Length >= (_is64 ? 32 : 16))
                {
                    ulong addrOfCallbacks = _is64
                        ? BitConverter.ToUInt64(tlsData, 24)
                        : BitConverter.ToUInt32(tlsData, 12);
                    long tlsDist = (long)(_imageBase + tlsRva) - (long)addrOfCallbacks;
                    if (tlsDist > 0 && tlsDist <= (long)(_ptrSize * 5))
                    {
                        _tlsTotal = (int)(tlsDist / _ptrSize) - 1;
                        _api.Log.Info($"[MSVC] Expecting {_tlsTotal} TLS callback(s)");
                    }
                }
            }
        }

        // PE antidump patching (Magicmida TMInit: patch .idata → .pdata)
        if (numSect >= 3)
        {
            int sect2Off = sectOff + 2 * 40; // 3rd section
            if (sect2Off + 8 <= hdr.Length && hdr[sect2Off + 1] == (byte)'i')
            {
                ulong patchAddr = _imageBase + (ulong)sect2Off + 1;
                _api.Memory.WriteMemory(pid, patchAddr, new byte[] { (byte)'p' });
                _api.Log.Info("[Antidump] Patched section name byte 'i' → 'p'");
            }
        }

        _detected = true;
        _guardAddrs.Clear();
        _isVmOep = false;
        _api.Log.Warning($"TMSect: {_tmBase} ({_tmEnd - _tmBase} bytes)");
        _api.Log.Warning($"Text base: 0x{_textBase:X}, code size: 0x{_textVSize:X}");
        SetStatus($"Detected. .text=0x{_textBase:X}");
    }

    // ════════════════════════════════════════════════════════════════
    //  Unpacking
    // ════════════════════════════════════════════════════════════════

    public void StartUnpacking()
    {
        if (!_detected) { DetectProtector(); if (!_detected) return; }
        if (_phase != Phase.Idle && _phase != Phase.Done) { _api.Log.Warning("Already unpacking."); return; }

        uint pid = _api.TargetPid;
        uint guardSize = (uint)(_baseOfData - _textBase);
        if (guardSize == 0 || guardSize > 0x10000000) guardSize = _textVSize;

        var (ok, oldProt) = _api.Memory.ProtectMemory(pid, _textBase, guardSize, 0x01 /* PAGE_NOACCESS */);
        if (!ok) { _api.Log.Error("Failed to set PAGE_NOACCESS on .text"); return; }
        _guardOldProt = oldProt;
        _guardHitCount = 0;
        _firstTextExecAddr = 0;
        _guardAddrs.Clear();

        _phase = Phase.TextGuarded;
        _api.Log.Warning("[Unpack] PAGE_NOACCESS on .text — press F9.");
        SetStatus("Guarding .text — F9");
    }

    public void StopUnpacking()
    {
        if (_phase == Phase.Idle) return;
        uint pid = _api.TargetPid;

        uint guardSize = (uint)(_baseOfData - _textBase);
        if (guardSize == 0 || guardSize > 0x10000000) guardSize = _textVSize;
        if (_guardOldProt != 0)
            _api.Memory.ProtectMemory(pid, _textBase, guardSize, _guardOldProt);

        foreach (var tid in _suspendedTids) _api.Process.ResumeThread(tid);
        _suspendedTids.Clear();

        _phase = Phase.Idle;
        _tmGuard = false;
        _traceMsvcOep = false;
        _tlsCounter = 0;
        _guardHitCount = 0;
        _api.Log.Info("[Unpack] Stopped.");
        SetStatus("Idle");
    }

    private void OnBeforeRun()
    {
        if (_phase == Phase.Idle && _detected)
            StartUnpacking();
    }

    private uint GuardSize()
    {
        uint sz = (uint)(_baseOfData - _textBase);
        if (sz == 0 || sz > 0x10000000) sz = _textVSize;
        return sz;
    }

    // ════════════════════════════════════════════════════════════════
    //  Debug event filter — core state machine
    // ════════════════════════════════════════════════════════════════

    private bool OnDebugEventFilter(PluginDebugEvent evt)
    {
        if (_phase == Phase.Idle || _phase == Phase.Done) return false;

        uint pid = evt.ProcessId;
        ulong rip = evt.Address;

        // ── TextGuarded: PAGE_NOACCESS AV on .text ──
        if (_phase == Phase.TextGuarded && evt.Type == PluginDebugEventType.AccessViolation)
        {
            ulong fault = evt.FaultAddress;
            ulong guardEnd = _textBase + GuardSize();
            if (fault < _textBase || fault >= guardEnd) return false;

            _guardHitCount++;
            uint access = evt.AccessType;

            // Unprotect .text for inspection
            _api.Memory.ProtectMemory(pid, _textBase, GuardSize(), _guardOldProt);

            // Branch 1: _tmGuard — suppress Themida's re-entry after TLS callback (Magicmida _tmGuard)
            if (_tmGuard)
            {
                _tmGuard = false;
                _api.Memory.ProtectMemory(pid, _textBase, GuardSize(), 0x01); // re-arm guard
                evt.ContinueMode = 3;
                return true;
            }

            // Branch 2: RIP outside image bounds — library code reading .text
            bool ripInImage = rip >= _imageBase && rip < _imageBoundary;
            if (!ripInImage)
            {
                _phase = Phase.TextStepRearm;
                evt.ContinueMode = 3;
                return true;
            }

            // Branch 3: RIP in Themida section — TM decrypting .text, track access
            if (IsInTmRange(rip))
            {
                _guardAddrs.Add(fault);
                if (_guardHitCount <= 5 || _guardHitCount % 1000 == 0)
                    _api.Log.Info($"[Guard] {(access == 8 ? "Execute" : access == 0 ? "Read" : "Write")} {fault}");
                _phase = Phase.TextStepRearm;
                evt.ContinueMode = 3;
                return true;
            }

            // Branch 4: Execute + TLS callbacks remaining (Magicmida _tlsTotal/_tlsCounter)
            if (access == 8 && _tlsTotal > 0 && _tlsCounter < _tlsTotal)
            {
                _tlsCounter++;
                _api.Log.Warning($"[TLS] Callback {_tlsCounter}/{_tlsTotal}: 0x{fault:X}");
                // Switch guard to TM section, let TLS callback execute in .text
                if (_tmBase > 0)
                    _api.Memory.ProtectMemory(pid, _tmBase, (uint)(_tmEnd - _tmBase), 0x04); // PAGE_READWRITE on TM
                _tmGuard = true;
                evt.ContinueMode = 3;
                return true;
            }

            // Branch 5: MSVC virtualized OEP tracing — second pass
            // fault = __scrt_startup address (the execute target that hit guard)
            if (_traceMsvcOep && access == 8)
            {
                WriteMsvcOep(fault); // writes stub at _msvcOep: call initcookie + jmp fault
                _api.Log.Warning($"Virtualized MSVC9+ OEP restored: {_msvcOep}");
                _oepAddr = _msvcOep;
                _traceMsvcOep = false;
                SetStatus($"OEP = 0x{_oepAddr:X}");


                // Suppress AV, redirect to restored OEP
                evt.ContinueMode = 3; // HANDLED: suppress AV + set TF
                evt.NewRip = _msvcOep;

                bool autoIat = false;
                try { Application.Current?.Dispatcher.Invoke(() => autoIat = _chkAutoIat.IsChecked == true); }
                catch { autoIat = true; }

                if (autoIat)
                {
                    // Single-step lands at OEP, then start IAT trace
                    _phase = Phase.OepStepThrough;
                    return true;
                }

                // Break at OEP for user — single-step to OEP first
                _phase = Phase.Done;
                return true; // suppress AV, will single-step to OEP
            }

            // Branch 6: Execute access → real OEP!
            if (access == 8)
            {
                _firstTextExecAddr = fault;
                _api.Log.Warning($"[Guard] Execute {fault}");
                _phase = Phase.OepStepThrough;
                evt.ContinueMode = 3;
                return true;
            }

            // Read/write from non-TM image code
            if (_guardHitCount <= 5 || _guardHitCount % 1000 == 0)
                _api.Log.Info($"[Guard] {(access == 8 ? "Execute" : access == 0 ? "Read" : "Write")} {fault}");

            if (_guardHitCount > 500000)
            {
                _api.Log.Error("[Guard] 500K hits — aborting.");
                _phase = Phase.Idle;
                return false;
            }

            _phase = Phase.TextStepRearm;
            evt.ContinueMode = 3;
            return true;
        }

        // ── TextStepRearm: re-arm PAGE_NOACCESS ──
        if (_phase == Phase.TextStepRearm && evt.Type == PluginDebugEventType.SingleStep)
        {
            _api.Memory.ProtectMemory(pid, _textBase, GuardSize(), 0x01);
            _phase = Phase.TextGuarded;

            if (rip >= _textBase && rip < _textBase + GuardSize())
            {
                _firstTextExecAddr = rip;
                _api.Memory.ProtectMemory(pid, _textBase, GuardSize(), _guardOldProt);
                return HandleOepFound(pid, rip, evt);
            }
            return true;
        }

        // ── OepStepThrough: SingleStep at first .text instruction ──
        if (_phase == Phase.OepStepThrough && evt.Type == PluginDebugEventType.SingleStep)
            return HandleOepFound(pid, rip, evt);

        // ── IatTracing: driver-side trace result ──
        if (_phase == Phase.IatTracing)
        {
            if (evt.Type == PluginDebugEventType.SingleStep)
            {
                HandleTraceResult(pid, rip, evt);
                return true;
            }
            // Unexpected events during tracing
            evt.ContinueMode = 0;
            return true;
        }

        // ── Done: OEP breakpoint hit after IAT trace ──
        if (_phase == Phase.Done && evt.Type == PluginDebugEventType.Breakpoint && rip == _oepAddr)
        {
            _api.Log.Warning($"Stopped at OEP 0x{_oepAddr:X}");
            bool autoDump = false;
            try { Application.Current?.Dispatcher.Invoke(() => autoDump = _chkAutoDump.IsChecked == true); }
            catch { }
            if (autoDump) DumpPe();
            return false; // let UI handle the break
        }

        return false;
    }

    // ════════════════════════════════════════════════════════════════
    //  OEP found — Magicmida ProcessGuardedAccess execute path
    // ════════════════════════════════════════════════════════════════

    private bool HandleOepFound(uint pid, ulong rip, PluginDebugEvent evt)
    {
        // Check virtualized OEP (Magicmida CheckVirtualizedOEP)
        var oepCode = _api.Memory.ReadMemory(pid, rip, 16);
        if (oepCode != null && oepCode.Length >= 5 && oepCode[0] == 0xE9)
        {
            int disp = BitConverter.ToInt32(oepCode, 1);
            ulong target = (ulong)((long)rip + 5 + disp);
            if (IsInTmRange(target))
            {
                _isVmOep = true;
                _api.Log.Warning($"Virtualized OEP: jmp 0x{target:X} (.themida)");
            }
        }

        // Check return addr for MSVC virtualized OEP (Magicmida TryFindCorrectOEP)
        var regs = _api.Memory.ReadRegisters(pid, evt.ThreadId);
        ulong rsp = GetReg(regs, _is64 ? "RSP" : "ESP");
        if (rsp != 0)
        {
            var retData = _api.Memory.ReadMemory(pid, rsp, (uint)_ptrSize);
            if (retData != null)
            {
                ulong retAddr = _is64 ? BitConverter.ToUInt64(retData) : BitConverter.ToUInt32(retData);
                if (IsInTmRange(retAddr))
                {
                    _api.Log.Info($"Return address points into Themida section: {retAddr}");
                    ulong realOep = TryFindMsvcOep(pid, _firstTextExecAddr);

                    if (realOep != 0 && _traceMsvcOep)
                    {
                        // Guard fallback path: TryFindMsvcOep set _traceMsvcOep=true
                        // Need to skip to retAddr and re-enter guard mode for WriteMSVCOEP on next .text hit
                        _msvcOep = realOep;
                        _api.Log.Info($"MSVC: will trace to next .text exec for OEP stub write (OEP=0x{realOep:X})");
                        // Redirect RIP to return address, pop stack
                        evt.NewRip = retAddr;
                        evt.NewRsp = rsp + (ulong)_ptrSize;
                        // Re-arm guard and wait for next .text execute
                        _api.Memory.ProtectMemory(pid, _textBase, GuardSize(), 0x01);
                        _phase = Phase.TextGuarded;
                        evt.ContinueMode = 3;
                        return true;
                    }
                    else if (realOep != 0)
                    {
                        _api.Log.Warning($"Real MSVC OEP: 0x{realOep:X}");
                        rip = realOep;
                        evt.NewRip = rip; // Applied by driver via g_ContinueFlags
                    }
                }
            }
        }

        // Check for Themida stolen bytes: "sub rsp, 28h" (48 83 EC 28) right before detected OEP.
        // Themida steals the first instruction and executes it in VM, then jumps to the next one.
        if (_is64)
        {
            var pre = _api.Memory.ReadMemory(pid, rip - 4, 4);
            if (pre != null && pre.Length == 4 &&
                pre[0] == 0x48 && pre[1] == 0x83 && pre[2] == 0xEC && pre[3] == 0x28)
            {
                rip -= 4;
                _api.Log.Info($"[OEP] Adjusted for stolen 'sub rsp,28h': 0x{rip:X}");
            }
        }

        _oepAddr = rip;
        _api.Log.Warning($"OEP = 0x{_oepAddr:X}");
        SetStatus($"OEP = 0x{_oepAddr:X}");

        bool autoIat = false;
        try { Application.Current?.Dispatcher.Invoke(() => autoIat = _chkAutoIat.IsChecked == true); }
        catch { autoIat = true; }

        if (autoIat)
        {
            StartIatTrace(pid, evt);
            if (_phase == Phase.IatTracing) return true;
        }

        _phase = Phase.Done;
        return false; // break at OEP
    }

    // Magicmida TryFindCorrectOEP — MSVC pattern: E8 call __security_init_cookie + E9 jmp __scrt_startup
    private ulong TryFindMsvcOep(uint pid, ulong hitAddr)
    {
        if (_majorLinkerVersion is not (9 or 10 or 11 or 12 or 14)) return 0;

        uint len = (uint)(_baseOfData - _textBase);
        if (len == 0 || len > 0x200000) len = Math.Min(_textVSize, 0x200000);

        var text = _api.Memory.ReadMemory(pid, _textBase, len);
        if (text == null) return 0;

        uint scanFor = (uint)(hitAddr - _textBase);
        for (int i = 0; i + 10 <= text.Length; i++)
        {
            if (text[i] == 0xE8 && text[i + 5] == 0xE9)
            {
                uint callDisp = BitConverter.ToUInt32(text, i + 1);
                if ((callDisp + (uint)i + 5) == scanFor)
                    return _textBase + (ulong)i;
            }
        }

        // Magicmida fallback: two consecutive guard addresses → need MSVC OEP trace
        if (_guardAddrs.Count >= 2 &&
            _guardAddrs[_guardAddrs.Count - 1] == _guardAddrs[_guardAddrs.Count - 2] + 1)
        {
            _traceMsvcOep = true;
            _msvcInitCookie = hitAddr; // the __security_init_cookie address
            return _guardAddrs[_guardAddrs.Count - 2];
        }

        return 0;
    }

    // WriteMsvcOep — Magicmida WriteMSVCOEP: writes 18-byte shellcode at _msvcOep
    // sub rsp,28h / call __security_init_cookie / add rsp,28h / jmp __scrt_startup
    private void WriteMsvcOep(ulong crtStartup)
    {
        uint pid = _api.TargetPid;
        _api.Memory.ProtectMemory(pid, _msvcOep, 18, 0x40); // PAGE_EXECUTE_READWRITE

        var code = new byte[18];
        // sub rsp, 28h = 48 83 EC 28
        code[0] = 0x48; code[1] = 0x83; code[2] = 0xEC; code[3] = 0x28;
        // call __security_init_cookie (rel32)
        code[4] = 0xE8;
        int callRel = (int)((long)_msvcInitCookie - (long)(_msvcOep + 4) - 5);
        Array.Copy(BitConverter.GetBytes(callRel), 0, code, 5, 4);
        // add rsp, 28h = 48 83 C4 28
        code[9] = 0x48; code[10] = 0x83; code[11] = 0xC4; code[12] = 0x28;
        // jmp __scrt_startup (rel32)
        code[13] = 0xE9;
        int jmpRel = (int)((long)crtStartup - (long)(_msvcOep + 13) - 5);
        Array.Copy(BitConverter.GetBytes(jmpRel), 0, code, 14, 4);

        _api.Memory.WriteMemory(pid, _msvcOep, code);
        _api.Log.Warning($"Virtualized MSVC9+ OEP restored: {_msvcOep}");
    }

    // ════════════════════════════════════════════════════════════════
    //  IAT — full Magicmida DetermineIATAddress + TraceImports
    // ════════════════════════════════════════════════════════════════

    private void StartIatTrace(uint pid, PluginDebugEvent evt)
    {
        // Create dumper for IsAPIAddress checks
        _dumper = new RemoteDumper(_api, pid, _imageBase, _imageBoundary, _oepAddr, _is64);

        // Find IAT address (Magicmida DetermineIATAddress)
        ulong iatAddr = DetermineIATAddress(pid);
        if (iatAddr == 0)
        {
            _api.Log.Error("[IAT] Cannot find IAT. Break at OEP.");
            _phase = Phase.Done;
            return;
        }

        _iatBase = iatAddr;
        _api.Log.Warning($"IAT: {_iatBase}");

        // Read IAT (Magicmida TraceImports)
        int maxSlots = RemoteDumper.MAX_IAT_SLOTS;
        int readSize = maxSlots * _ptrSize;
        var raw = _api.Memory.ReadMemory(pid, _iatBase, (uint)readSize);
        if (raw == null) { _api.Log.Error("Cannot read IAT"); _phase = Phase.Done; return; }

        _iatData = new ulong[maxSlots];
        for (int i = 0; i < maxSlots; i++)
        {
            int off = i * _ptrSize;
            if (off + _ptrSize > raw.Length) break;
            _iatData[i] = _is64 ? BitConverter.ToUInt64(raw, off) : BitConverter.ToUInt32(raw, off);
        }
        _iatCount = maxSlots;

        // Count wrapped entries
        int wrappedCount = 0;
        uint trashCount = 0;
        int effectiveCount = 0;
        for (int i = 0; i < _iatCount; i++)
        {
            if (IsInTmRange(_iatData[i]))
            {
                wrappedCount++;
                trashCount = 0;
                effectiveCount = i + 1;
            }
            else if (_iatData[i] == 0 || !_dumper.IsAPIAddress(_iatData[i]))
            {
                trashCount++;
                if (trashCount > 64) { effectiveCount = i; break; }
            }
            else
            {
                trashCount = 0;
                effectiveCount = i + 1;
            }
        }
        _iatCount = effectiveCount;

        _api.Log.Warning($"Determined IAT size: {_iatCount}");

        if (wrappedCount == 0)
        {
            _api.Log.Info("[IAT] No wrapped imports — IAT is clean.");
            FinishIatTrace(pid);
            return;
        }

        // Save state
        var regsNow = _api.Memory.ReadRegisters(pid, evt.ThreadId);
        _savedRip = _oepAddr;
        _savedRsp = GetReg(regsNow, _is64 ? "RSP" : "ESP");
        _traceStartSP = _savedRsp;

        // NOTE: Do NOT suspend threads here — driver's SuspendThread uses
        // PsSuspendProcess which suspends the ENTIRE process (including our
        // target thread), causing the fast trace to hang.
        // Thread suspension will be done after trace completes if needed.
        _suspendedTids.Clear();

        _iatIdx = -1;
        _iatResolvedCount = 0;
        _iatFailedCount = 0;
        _phase = Phase.IatTracing;

        if (!AdvanceToNextWrapper(evt))
        {
            WriteIatBack(pid);
            FinishIatTrace(pid);
        }
    }

    private bool AdvanceToNextWrapper(PluginDebugEvent evt)
    {
        _iatIdx++;
        while (_iatIdx < _iatCount)
        {
            if (IsInTmRange(_iatData[_iatIdx]))
                break;
            _iatIdx++;
        }
        if (_iatIdx >= _iatCount) return false;

        ulong wrapperAddr = _iatData[_iatIdx];

        ulong slotAddr = _iatBase + (ulong)(_iatIdx * _ptrSize);
        _api.Log.Info($"Trace: {wrapperAddr} [{slotAddr}]");

        // Reset per-wrapper state
        _vmProbePhase = true;
        _traceMaxSteps = 5000; // Phase 1: quick VM probe
        _antiTraceSkips = 0;

        // Driver-side fast trace: driver single-steps internally within TM range,
        // reports only when RIP exits range or max steps reached.
        evt.NewRip = wrapperAddr;
        evt.NewRsp = _savedRsp;
        evt.ContinueMode = 4; // Trace — driver-side fast trace
        evt.TraceRangeBase = _tmBase;
        evt.TraceRangeEnd = _tmEnd;
        evt.TraceMaxSteps = (uint)_traceMaxSteps;

        return true;
    }

    private void HandleTraceResult(uint pid, ulong rip, PluginDebugEvent evt)
    {
        // RIP still in TM range — driver hit max steps (wrapper didn't exit TM range)
        if (IsInTmRange(rip))
        {
            if (_vmProbePhase)
            {
                // Phase 1 (5K steps) done, still in TM — retry with full 500K steps
                _vmProbePhase = false;
                _traceMaxSteps = 500000;
                _api.Log.Info($"Phase 1 done, retrying with 500K steps");
                evt.NewRip = _iatData[_iatIdx];
                evt.NewRsp = _savedRsp;
                evt.ContinueMode = 4; // Trace
                evt.TraceRangeBase = _tmBase;
                evt.TraceRangeEnd = _tmEnd;
                evt.TraceMaxSteps = 500000;
                return;
            }

            // Phase 2 exhausted — VM wrapper, leave original wrapper in place
            _api.Log.Warning($"Trace limit at 0x{rip:X} — VM detected, keeping wrapper [{_iatIdx}]");
            _iatFailedCount++;

            AdvanceOrFinish(pid, evt);
            return;
        }

        // RIP exited TM range — Magicmida TraceIsAtAPI logic
        var traceRegs = _api.Memory.ReadRegisters(pid, evt.ThreadId);
        ulong rsp = GetReg(traceRegs, _is64 ? "RSP" : "ESP");

        // SP < start: anti-trace fake call — simulate return and continue tracing
        if (rsp < _traceStartSP)
        {
            _antiTraceSkips++;
            if (_antiTraceSkips > 15)
            {
                // Too many fake calls — VM wrapper, keep original wrapper in place
                _api.Log.Warning($"Anti-trace skip limit at [{_iatIdx}] — keeping wrapper");
                _iatFailedCount++;
                AdvanceOrFinish(pid, evt);
                return;
            }

            var retData = _api.Memory.ReadMemory(pid, rsp, (uint)_ptrSize);
            if (retData != null)
            {
                ulong retAddr = _is64 ? BitConverter.ToUInt64(retData) : BitConverter.ToUInt32(retData);
                rsp += (ulong)_ptrSize;
                _api.Log.Info($"Skipping anti-trace API at {rip}");
                evt.NewRip = retAddr;
                evt.NewRsp = rsp;
                evt.ContinueMode = 4; // Trace — continue driver-side trace
                evt.TraceRangeBase = _tmBase;
                evt.TraceRangeEnd = _tmEnd;
                evt.TraceMaxSteps = (uint)_traceMaxSteps;
                return;
            }
        }

        // SP >= start — this is the real resolved API
        // Kernel RIP = trace aborted due to kernel exception — skip this wrapper
        if (rip >= 0xFFFF800000000000UL)
        {
            _iatFailedCount++;
            _api.Log.Warning($"Kernel RIP 0x{rip:X} at [{_iatIdx}] — keeping wrapper");
            AdvanceOrFinish(pid, evt);
            return;
        }

        // Filter out results pointing back into our image
        if (rip >= 0x10000 && !(rip >= _imageBase && rip < _imageBoundary))
        {
            _iatData[_iatIdx] = rip;
            _iatResolvedCount++;
            _api.Log.Info($"-> {rip}");
        }
        else
        {
            _iatFailedCount++;
            _api.Log.Warning($"Bad result at [{_iatIdx}] → 0x{rip:X}, aborting trace");
            WriteIatBack(pid);
            FinishIatTrace(pid);
            evt.ContinueMode = 0;
            evt.NewRip = _savedRip;
            evt.NewRsp = _savedRsp;
            return;
        }

        AdvanceOrFinish(pid, evt);
    }

    private void AdvanceOrFinish(uint pid, PluginDebugEvent evt)
    {
        if (!AdvanceToNextWrapper(evt))
        {
            WriteIatBack(pid);
            FinishIatTrace(pid);
            evt.ContinueMode = 0;
            evt.NewRip = _savedRip;
            evt.NewRsp = _savedRsp;
        }
    }

    private void WriteIatBack(uint pid)
    {
        // Write resolved IAT back to process (Magicmida TraceImports ending)
        _api.Memory.ProtectMemory(pid, _iatBase, (uint)(_iatCount * _ptrSize), 0x04 /* PAGE_READWRITE */);
        for (int i = 0; i < _iatCount; i++)
        {
            byte[] val = _is64 ? BitConverter.GetBytes(_iatData[i]) : BitConverter.GetBytes((uint)_iatData[i]);
            _api.Memory.WriteMemory(pid, _iatBase + (ulong)(i * _ptrSize), val);
        }
    }

    private void FinishIatTrace(uint pid)
    {
        // Note: RIP/RSP restoration is done via evt.NewRip/NewRsp at call sites,
        // not via WriteRipAndRsp (which writes to wrong trap frame while thread is blocked).

        foreach (var tid in _suspendedTids) _api.Process.ResumeThread(tid);
        _suspendedTids.Clear();

        _api.Log.Warning($"IAT Done: {_iatResolvedCount} resolved, {_iatFailedCount} failed.");
        SetStatus($"IAT: {_iatResolvedCount} resolved. OEP=0x{_oepAddr:X}");

        // Set BP on OEP so process breaks there after Run
        _oepBpHandle = _api.Breakpoints.SetBreakpoint(pid, 0, _oepAddr, PluginBreakpointType.Software);
        _api.Log.Info($"BP set on OEP 0x{_oepAddr:X} handle={_oepBpHandle}");

        _phase = Phase.Done;
    }

    // ════════════════════════════════════════════════════════════════
    //  DetermineIATAddress — full Magicmida port
    // ════════════════════════════════════════════════════════════════

    private ulong DetermineIATAddress(uint pid)
    {
        ulong codeSize = _baseOfData - _textBase;
        if (codeSize == 0 || codeSize > 0x10000000)
            codeSize = _textVSize;

        // Find data section for ScanForPointer fallback
        ulong dataBase = _baseOfData;
        ulong dataSize = 0;
        foreach (var s in _sections)
        {
            ulong sBase = _imageBase + s.Rva;
            if (sBase >= _baseOfData && (s.Chars & 0x20000000) == 0)
            {
                dataSize = s.VirtualSize - (_baseOfData - sBase);
                break;
            }
        }

        var codeDump = _api.Memory.ReadMemory(pid, _textBase, (uint)codeSize);
        if (codeDump == null) { _api.Log.Error("[IAT] Cannot read .text"); return 0; }

        ulong iatRef = 0;
        uint numInstr = 0;

        if (!_isVmOep)
        {
            iatRef = FindCallOrJmpPtr(codeDump, _textBase, codeSize, _oepAddr, ref numInstr, false, pid);
        }
        else
        {
            // Check Delphi marker (Magicmida)
            bool isDelphi = false;
            if (codeDump.Length > 12)
            {
                uint marker = BitConverter.ToUInt32(codeDump, _is64 ? 10 : 6);
                uint marker2 = BitConverter.ToUInt32(codeDump, 6);
                if (marker == 0x6C6F6F42 || marker2 == 0x65747942) // "Bool" / "Byte"
                {
                    isDelphi = true;
                    uint dOff = FindDelphiCall(codeDump);
                    if (dOff > 0)
                        iatRef = FindCallOrJmpPtr(codeDump, _textBase, codeSize, _textBase + dOff, ref numInstr, true, pid);
                }
            }
            if (!isDelphi)
                iatRef = FindCallOrJmpPtr(codeDump, _textBase, codeSize, _textBase, ref numInstr, true, pid);
        }

        if (iatRef == 0)
        {
            _api.Log.Info("[IAT] No code ref found, trying guard addrs");
            if (_guardAddrs.Count > 0)
            {
                // Read call/jmp at first guarded address
                var site = _api.Memory.ReadMemory(pid, _guardAddrs[0], 6);
                if (site != null && site.Length >= 6)
                {
                    ulong target = 0;
                    if (site[0] == 0xE8 || site[0] == 0xE9)
                        target = (ulong)((long)BitConverter.ToInt32(site, 1) + (long)_guardAddrs[0] + 5);
                    else if (site[1] == 0xE8 || site[1] == 0xE9)
                        target = (ulong)((long)BitConverter.ToInt32(site, 2) + (long)_guardAddrs[0] + 6);

                    if (target != 0)
                    {
                        _api.Log.Info($"[IAT] Guard[0] 0x{_guardAddrs[0]:X} → target 0x{target:X}");
                        iatRef = ScanForPointer(pid, target, _textBase, codeSize, dataBase, dataSize, false);
                    }
                }
            }

            if (iatRef == 0)
            {
                // Last resort: scan data sections for TM pointers
                iatRef = ScanDataForTmPointers(pid);
            }
        }

        if (iatRef == 0) { _api.Log.Error("[IAT] No IAT reference found."); return 0; }
        _api.Log.Info($"First IAT ref: {iatRef}");

        // Walk backward to find IAT start (Magicmida)
        int maxSlots = RemoteDumper.MAX_IAT_SLOTS;
        int readBack = (maxSlots - 1) * _ptrSize;
        ulong readStart = iatRef > (ulong)readBack ? iatRef - (ulong)readBack : _imageBase;
        uint readSize = (uint)(iatRef - readStart) + (uint)(_ptrSize * 64);

        var data = _api.Memory.ReadMemory(pid, readStart, readSize);
        if (data == null) return iatRef;

        ulong result = 0;
        int refIdx = (int)(iatRef - readStart);
        int consec0 = 0;

        for (int off = refIdx - _ptrSize; off >= 0; off -= _ptrSize)
        {
            ulong val = _is64 ? BitConverter.ToUInt64(data, off) : BitConverter.ToUInt32(data, off);
            ulong seekAddr = readStart + (ulong)off;

            if (val == 0)
            {
                consec0++;
                if (consec0 > 64) break;
            }
            else if (_dumper!.IsAPIAddress(val) || IsInTmRange(val))
            {
                result = seekAddr;
                consec0 = 0;
            }
            else
            {
                _api.Log.Info($"[IAT] End walkback at 0x{seekAddr:X}, ptr=0x{val:X}");
                break;
            }
        }

        return result != 0 ? result : iatRef;
    }

    // Magicmida FindCallOrJmpPtr — disassemble from address, find call/jmp [rip+disp]
    private ulong FindCallOrJmpPtr(byte[] code, ulong textBase, ulong codeSize,
        ulong address, ref uint numInstr, bool ignoreMethodBoundary, uint pid)
    {
        int offset = (int)(address - textBase);
        if (offset < 0 || offset >= code.Length) return 0;

        while (offset >= 0 && offset < code.Length - 15 &&
               (numInstr < 200 || (ignoreMethodBoundary && address < textBase + codeSize)))
        {
            int remaining = code.Length - offset;
            int len = Math.Min(remaining, 15);
            var instrBytes = new byte[len];
            Array.Copy(code, offset, instrBytes, 0, len);

            var reader = new ByteArrayCodeReader(instrBytes);
            var decoder = Iced.Intel.Decoder.Create(_is64 ? 64 : 32, reader, address);
            var instr = decoder.Decode();
            if (instr.IsInvalid) { offset++; address++; continue; }

            // Check for call/jmp [mem] (indirect through pointer — IAT reference)
            if ((instr.FlowControl == FlowControl.IndirectCall || instr.FlowControl == FlowControl.IndirectBranch) &&
                instr.Op0Kind == OpKind.Memory)
            {
                ulong memAddr = instr.IsIPRelativeMemoryOperand ? instr.IPRelativeMemoryAddress : instr.MemoryDisplacement64;
                if (memAddr != 0)
                {
                    var ptrData = _api.Memory.ReadMemory(pid, memAddr, (uint)_ptrSize);
                    if (ptrData != null)
                    {
                        ulong target = _is64 ? BitConverter.ToUInt64(ptrData) : BitConverter.ToUInt32(ptrData);
                        // If pointer target is outside .text → IAT ref
                        if (target > textBase + codeSize || target < textBase)
                        {
                            _api.Log.Info($"Found {address} : {instr}");
                            return memAddr;
                        }
                    }
                }
            }

            // Follow calls (Magicmida recursive)
            if (instr.FlowControl == FlowControl.Call && !ignoreMethodBoundary)
            {
                if (instr.Op0Kind is OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64)
                {
                    ulong brTarget = instr.NearBranchTarget;
                    if (brTarget > textBase + codeSize) break;
                    var r = FindCallOrJmpPtr(code, textBase, codeSize, brTarget, ref numInstr, false, pid);
                    if (r != 0) return r;
                }
            }

            if (instr.FlowControl == FlowControl.Return && !ignoreMethodBoundary) break;

            numInstr++;
            int ilen = instr.Length > 0 ? instr.Length : 1;
            offset += ilen;
            address += (ulong)ilen;
        }
        return 0;
    }

    // Magicmida FindDelphiCall — find 3rd jmp [addr] (FF 25)
    private static uint FindDelphiCall(byte[] code)
    {
        int counter = 0;
        for (int i = 0; i < code.Length - 6; i++)
        {
            if (code[i] == 0xFF && code[i + 1] == 0x25)
            {
                counter++;
                if (counter == 3) return (uint)i;
            }
        }
        return 0;
    }

    // Magicmida ScanForPointer
    private ulong ScanForPointer(uint pid, ulong toFind, ulong textBase, ulong codeSize,
        ulong dataBase, ulong dataSize, bool scanCode)
    {
        ulong startAddr = scanCode ? textBase : dataBase;
        ulong scanSize = scanCode ? codeSize : dataSize;
        if (scanSize == 0 || scanSize > 0x200000) return 0;

        var data = _api.Memory.ReadMemory(pid, startAddr, (uint)scanSize);
        if (data == null) return 0;

        for (int i = 0; i + _ptrSize <= data.Length; i += _ptrSize)
        {
            ulong val = _is64 ? BitConverter.ToUInt64(data, i) : BitConverter.ToUInt32(data, i);
            if (val == toFind)
                return startAddr + (ulong)i;
        }

        if (!scanCode)
            return ScanForPointer(pid, toFind, textBase, codeSize, 0, 0, true);
        return 0;
    }

    private ulong ScanDataForTmPointers(uint pid)
    {
        foreach (var sect in _sections)
        {
            if ((sect.Chars & 0x20000000) != 0) continue;
            string nl = sect.Name.Trim().ToLowerInvariant();
            if (nl is ".themida" or ".boot" or "themida" or "boot") continue;

            uint readSz = Math.Min(sect.VirtualSize, 0x20000);
            var data = _api.Memory.ReadMemory(pid, _imageBase + sect.Rva, readSz);
            if (data == null) continue;

            for (int i = 0; i + _ptrSize <= data.Length; i += _ptrSize)
            {
                ulong val = _is64 ? BitConverter.ToUInt64(data, i) : BitConverter.ToUInt32(data, i);
                if (IsInTmRange(val))
                    return _imageBase + sect.Rva + (ulong)i;
            }
        }
        return 0;
    }

    // ════════════════════════════════════════════════════════════════
    //  Manual IAT fix
    // ════════════════════════════════════════════════════════════════

    private void ManualFixIat()
    {
        if (!_api.IsConnected || !_api.IsBreakState)
        { _api.Log.Warning("Must be in Break state."); return; }
        if (_oepAddr == 0) { _api.Log.Warning("OEP not found."); return; }

        _phase = Phase.OepStepThrough;
        _api.SingleStep();
    }

    // ════════════════════════════════════════════════════════════════
    //  PE dump — full Magicmida Dumper.Process() + DumpToFile
    // ════════════════════════════════════════════════════════════════

    public void DumpPe()
    {
        if (!_api.IsConnected || !_api.IsBreakState)
        { _api.Log.Warning("Must be in Break state."); return; }

        uint pid = _api.TargetPid;
        if (_imageBase == 0) { _api.Log.Warning("Detect first."); return; }

        // Remove OEP breakpoint before dump so CC doesn't end up in the file
        if (_oepBpHandle.HasValue)
        {
            _api.Breakpoints.RemoveBreakpoint(_oepBpHandle.Value);
            _oepBpHandle = null;
        }

        if (_dumper == null)
            _dumper = new RemoteDumper(_api, pid, _imageBase, _imageBoundary, _oepAddr, _is64);

        _dumper.IAT = _iatBase;
        var dumpResult = _dumper.ProcessAndDump(pid, _iatBase, _iatCount, _iatData, _sections);

        if (dumpResult == null) { _api.Log.Error("[Dump] Failed."); return; }

        Application.Current?.Dispatcher.Invoke(() =>
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Executable|*.exe|All|*.*",
                FileName = "unpacked.exe"
            };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    File.WriteAllBytes(dlg.FileName, dumpResult);
                    _api.Log.Warning($"[Dump] Saved {dlg.FileName} ({dumpResult.Length} bytes)");
                    SetStatus($"Dumped: {dlg.FileName}");
                }
                catch (Exception ex) { _api.Log.Error($"[Dump] {ex.Message}"); }
            }
        });
    }

    // ════════════════════════════════════════════════════════════════
    //  Helpers
    // ════════════════════════════════════════════════════════════════

    private bool IsInTmRange(ulong addr) => addr >= _tmBase && addr < _tmEnd;

    private static ulong GetReg(IReadOnlyList<PluginRegister> regs, string name)
        => regs.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value ?? 0;

    private void SetStatus(string text)
    {
        try { Application.Current?.Dispatcher.BeginInvoke(() => _statusText.Text = text); }
        catch { }
    }

    private static CheckBox MakeCb(string text, bool on, Brush fg) => new()
    { Content = text, IsChecked = on, Foreground = fg, Margin = new Thickness(0, 2, 0, 2) };

    private static GroupBox Grp(string hdr, UIElement[] items, Brush fg)
    {
        var sp = new StackPanel();
        foreach (var item in items) sp.Children.Add(item);
        return new GroupBox
        {
            Header = new TextBlock { Text = hdr, Foreground = fg, FontWeight = FontWeights.SemiBold },
            Content = sp, Margin = new Thickness(0, 5, 0, 5),
            BorderBrush = Brushes.Gray, Foreground = fg
        };
    }

    private static void Btn(WrapPanel panel, string text, Action click)
    {
        var b = new Button { Content = text, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 6, 0) };
        b.Click += (_, _) => click();
        panel.Children.Add(b);
    }
}

// ════════════════════════════════════════════════════════════════════════════
//  RemoteDumper — port of Magicmida Dumper.cs
//  Module snapshot, export table parsing, forward resolution, import rebuild
// ════════════════════════════════════════════════════════════════════════════

internal class RemoteModule
{
    public ulong Base;
    public ulong End;
    public string Name = "";
    public Dictionary<ulong, string>? ExportTbl;
}

internal record ImportEntry(int IATOffset, string ExportName);

internal class ImportThunk
{
    public string Name;
    public List<ImportEntry> Entries = new();
    public ImportThunk(string dllName) { Name = dllName; }
}

internal class RemoteDumper
{
    public const int MAX_IAT_SLOTS = 5120;

    private readonly IDebuggerApi _api;
    private readonly uint _pid;
    private readonly ulong _imageBase, _imageBoundary, _oep;
    private readonly bool _is64;
    private readonly int _ptrSize;

    private List<RemoteModule>? _allModules;
    public ulong IAT { get; set; }

    // Forward name map: "ntdll.RtlInitializeSListHead" → ("kernel32.dll", "InitializeSListHead")
    // Maps target module.function → (source DLL name, source export name)
    private Dictionary<string, (string DllName, string ExportName)> _forwardNameMap
        = new(StringComparer.OrdinalIgnoreCase);

    // All kernel32 export names (for fallback: if kernel32 exports the same name, use kernel32)
    private HashSet<string> _kernel32ExportNames = new(StringComparer.OrdinalIgnoreCase);

    public RemoteDumper(IDebuggerApi api, uint pid, ulong imageBase, ulong imageBoundary, ulong oep, bool is64)
    {
        _api = api;
        _pid = pid;
        _imageBase = imageBase;
        _imageBoundary = imageBoundary;
        _oep = oep;
        _is64 = is64;
        _ptrSize = is64 ? 8 : 4;

        CollectForwards();
    }

    // ── Forward resolution (name-based) ──
    // Builds _forwardNameMap: "ntdll.RtlInitializeSListHead" → ("kernel32.dll", "InitializeSListHead")
    // No address manipulation — works regardless of DLL base differences between host and target.
    private unsafe void CollectForwards()
    {
        try
        {
            BuildExportNameSet("kernel32.dll", _kernel32ExportNames);
            _api.Log.Info($"[Fwd] kernel32 has {_kernel32ExportNames.Count} export names");
            CollectForwardsFromDll("kernel32.dll");
            CollectForwardsFromDll("user32.dll");
            CollectForwardsFromDll("ole32.dll");
            CollectForwardsFromDll("advapi32.dll");
            CollectForwardsFromDll("kernelbase.dll");
            LoadAndCollectFwd("netapi32.dll", "srvcli.dll", "samcli.dll");
            if (Environment.OSVersion.Version.Major >= 6)
                LoadAndCollectFwd("crypt32.dll", "dpapi.dll");
            LoadAndCollectFwd("dbghelp.dll", "dbgcore.dll");
            LoadAndCollectFwd("setupapi.dll", "cfgmgr32.dll");
            LoadAndCollectFwd("wsock32.dll", "ws2_32.dll");
        }
        catch (Exception ex)
        {
            _api.Log.Error($"[Fwd] CollectForwards exception: {ex.GetType().Name}: {ex.Message}");
        }
        _api.Log.Info($"[Fwd] Collected {_forwardNameMap.Count} name-based forwards");
        int shown = 0;
        foreach (var kv in _forwardNameMap)
        {
            if (shown++ >= 5) break;
            _api.Log.Info($"[Fwd]   '{kv.Key}' -> ({kv.Value.DllName}, {kv.Value.ExportName})");
        }
    }

    private unsafe void LoadAndCollectFwd(string mainDll, params string[] deps)
    {
        var handles = new List<IntPtr>();
        foreach (var dep in deps)
        {
            var h = NativeMethods.LoadLibraryW(dep);
            if (h != IntPtr.Zero) handles.Add(h);
        }
        var hMain = NativeMethods.LoadLibraryW(mainDll);
        if (hMain != IntPtr.Zero)
        {
            CollectForwardsFromHandle(hMain, mainDll);
            NativeMethods.FreeLibrary(hMain);
        }
        foreach (var h in handles) NativeMethods.FreeLibrary(h);
    }

    private unsafe void CollectForwardsFromDll(string dllName)
    {
        var hMod = NativeMethods.GetModuleHandleA(dllName);
        if (hMod == IntPtr.Zero) hMod = NativeMethods.LoadLibraryW(dllName);
        if (hMod == IntPtr.Zero) return;
        try { CollectForwardsFromHandle(hMod, dllName); } catch { }
    }

    /// <summary>
    /// Build set of ALL export names from a DLL (forwarded + direct).
    /// Used as fallback: if kernelbase exports "FlsAlloc" and kernel32 also exports "FlsAlloc",
    /// we can safely map it to kernel32 (kernel32 forwards to kernelbase anyway).
    /// </summary>
    private unsafe void BuildExportNameSet(string dllName, HashSet<string> nameSet)
    {
        var hMod = NativeMethods.GetModuleHandleA(dllName);
        if (hMod == IntPtr.Zero) return;
        byte* modBase = (byte*)hMod;
        int lfanew = *(int*)(modBase + 0x3C);
        int ddOff = _is64 ? lfanew + 4 + 20 + 0x70 : lfanew + 4 + 20 + 0x60;
        uint expRva = *(uint*)(modBase + ddOff);
        if (expRva == 0) return;
        byte* expDir = modBase + expRva;
        uint numNames = *(uint*)(expDir + 24);
        uint* addrNames = (uint*)(modBase + *(uint*)(expDir + 32));
        for (uint j = 0; j < numNames; j++)
        {
            string name = Marshal.PtrToStringAnsi((IntPtr)(modBase + addrNames[j])) ?? "";
            if (name.Length > 0) nameSet.Add(name);
        }
    }

    private unsafe void CollectForwardsFromHandle(IntPtr hMod, string srcDllName)
    {
        byte* modBase = (byte*)hMod;
        int lfanew = *(int*)(modBase + 0x3C);
        int ddOffset = _is64 ? lfanew + 4 + 20 + 0x70 : lfanew + 4 + 20 + 0x60;
        uint expRva = *(uint*)(modBase + ddOffset);
        uint expSize = *(uint*)(modBase + ddOffset + 4);
        if (expRva == 0 || expSize == 0) return;

        byte* expDir = modBase + expRva;
        uint numFuncs = *(uint*)(expDir + 20);
        uint numNames = *(uint*)(expDir + 24);
        uint* addrFuncs = (uint*)(modBase + *(uint*)(expDir + 28));
        uint* addrNames = (uint*)(modBase + *(uint*)(expDir + 32));
        ushort* addrOrdinals = (ushort*)(modBase + *(uint*)(expDir + 36));

        // Build ordinal → export name map
        var ordToName = new Dictionary<uint, string>();
        for (uint j = 0; j < numNames; j++)
        {
            string name = Marshal.PtrToStringAnsi((IntPtr)(modBase + addrNames[j])) ?? "";
            ordToName[addrOrdinals[j]] = name;
        }

        for (uint i = 0; i < numFuncs; i++)
        {
            byte* fwdPtr = modBase + addrFuncs[i];
            if (fwdPtr >= expDir && fwdPtr < expDir + expSize)
            {
                string fwdStr = Marshal.PtrToStringAnsi((IntPtr)fwdPtr) ?? "";
                int dot = fwdStr.IndexOf('.');
                if (fwdStr.Length >= 10 && fwdStr.Length <= 90 &&
                    ((dot > 0 && dot < 15) || fwdStr.Contains("api-ms-win")) &&
                    !fwdStr.Contains(".#"))
                {
                    string targetMod = fwdStr.Substring(0, dot);
                    string targetProc = fwdStr.Substring(dot + 1);

                    // Get source export name for this ordinal
                    if (!ordToName.TryGetValue(i, out string? srcExportName))
                        continue; // no name → skip

                    // Store direct: "ntdll.RtlInitializeSListHead" → ("kernel32.dll", "InitializeSListHead")
                    _forwardNameMap[$"{targetMod}.{targetProc}"] = (srcDllName, srcExportName);

                    // Chase forward chain for api-ms-win → kernelbase → ntdll etc.
                    var hTarget = NativeMethods.GetModuleHandleA(targetMod);
                    if (hTarget == IntPtr.Zero) hTarget = NativeMethods.LoadLibraryW(targetMod + ".dll");
                    if (hTarget != IntPtr.Zero)
                        ChaseForwardChain(hTarget, targetProc, srcDllName, srcExportName);
                }
            }
        }
    }

    /// <summary>
    /// Follows forward chains: kernel32→api-ms-win.Foo→kernelbase.Foo→ntdll.Bar
    /// Stores all intermediate and final mappings back to the original source.
    /// </summary>
    private unsafe void ChaseForwardChain(IntPtr hMod, string procName, string srcDllName, string srcExportName)
    {
        for (int hop = 0; hop < 4; hop++)
        {
            byte* modBase = (byte*)hMod;
            int lfanew = *(int*)(modBase + 0x3C);
            int ddOff = _is64 ? lfanew + 4 + 20 + 0x70 : lfanew + 4 + 20 + 0x60;
            uint eRva = *(uint*)(modBase + ddOff);
            uint eSize = *(uint*)(modBase + ddOff + 4);
            if (eRva == 0 || eSize == 0) return;

            byte* eDir = modBase + eRva;
            uint nNames = *(uint*)(eDir + 24);
            uint* aFuncs = (uint*)(modBase + *(uint*)(eDir + 28));
            uint* aNames = (uint*)(modBase + *(uint*)(eDir + 32));
            ushort* aOrds = (ushort*)(modBase + *(uint*)(eDir + 36));

            bool found = false;
            for (uint j = 0; j < nNames; j++)
            {
                string name = Marshal.PtrToStringAnsi((IntPtr)(modBase + aNames[j])) ?? "";
                if (!string.Equals(name, procName, StringComparison.Ordinal)) continue;

                uint funcRva = aFuncs[aOrds[j]];
                byte* funcPtr = modBase + funcRva;
                if (funcPtr >= eDir && funcPtr < eDir + eSize)
                {
                    string chainStr = Marshal.PtrToStringAnsi((IntPtr)funcPtr) ?? "";
                    int chainDot = chainStr.IndexOf('.');
                    if (chainDot > 0 && chainStr.Length > chainDot + 1)
                    {
                        string chainMod = chainStr.Substring(0, chainDot);
                        string chainProc = chainStr.Substring(chainDot + 1);
                        _forwardNameMap[$"{chainMod}.{chainProc}"] = (srcDllName, srcExportName);

                        var hNext = NativeMethods.GetModuleHandleA(chainMod);
                        if (hNext == IntPtr.Zero) hNext = NativeMethods.LoadLibraryW(chainMod + ".dll");
                        if (hNext != IntPtr.Zero)
                        {
                            hMod = hNext;
                            procName = chainProc;
                            found = true;
                        }
                    }
                }
                break;
            }
            if (!found) return;
        }
    }

    // ── Module snapshot and export table parsing ──

    private void TakeModuleSnapshot()
    {
        _allModules = new List<RemoteModule>();
        var modules = _api.Symbols.GetModules();
        foreach (var m in modules)
        {
            if (m.BaseAddress == _imageBase) continue;
            _allModules.Add(new RemoteModule
            {
                Base = m.BaseAddress,
                End = m.BaseAddress + m.Size,
                Name = m.Name.ToLowerInvariant()
            });
        }
    }

    public bool IsAPIAddress(ulong address)
    {
        if (_allModules == null) TakeModuleSnapshot();
        foreach (var rm in _allModules!)
        {
            if (address >= rm.Base && address < rm.End)
            {
                if (rm.ExportTbl == null) GatherExports(rm);
                return rm.ExportTbl!.ContainsKey(address);
            }
        }
        return false;
    }

    private string? LookupExportName(ulong address)
    {
        if (_allModules == null) TakeModuleSnapshot();
        foreach (var rm in _allModules!)
        {
            if (address >= rm.Base && address < rm.End)
            {
                if (rm.ExportTbl == null) GatherExports(rm);
                return rm.ExportTbl!.TryGetValue(address, out var name) ? name : null;
            }
        }
        return null;
    }

    /// <summary>
    /// Follow JMP/CALL trampolines (aclayers shim hooks, Themida wrappers) to find real target.
    /// </summary>
    private ulong TryUnwrapTrampoline(ulong addr, int maxHops = 5)
    {
        for (int hop = 0; hop < maxHops; hop++)
        {
            var code = _api.Memory.ReadMemory(_pid, addr, 16);
            if (code == null || code.Length < 6) return addr;

            // E9 rel32 — JMP rel32
            if (code[0] == 0xE9)
            {
                int disp = BitConverter.ToInt32(code, 1);
                addr = (ulong)((long)addr + 5 + disp);
                continue;
            }
            // FF 25 disp32 — JMP [RIP+disp32]
            if (code[0] == 0xFF && code[1] == 0x25)
            {
                int disp = BitConverter.ToInt32(code, 2);
                ulong ptr = (ulong)((long)addr + 6 + disp);
                var ptrData = _api.Memory.ReadMemory(_pid, ptr, (uint)_ptrSize);
                if (ptrData == null) return addr;
                addr = _is64 ? BitConverter.ToUInt64(ptrData) : BitConverter.ToUInt32(ptrData);
                continue;
            }
            // 48 FF 25 disp32 — REX.W JMP [RIP+disp32]
            if (_is64 && code[0] == 0x48 && code[1] == 0xFF && code[2] == 0x25)
            {
                int disp = BitConverter.ToInt32(code, 3);
                ulong ptr = (ulong)((long)addr + 7 + disp);
                var ptrData = _api.Memory.ReadMemory(_pid, ptr, 8);
                if (ptrData == null) return addr;
                addr = BitConverter.ToUInt64(ptrData);
                continue;
            }
            // 48 B8 imm64; FF E0 — mov rax, imm64; jmp rax
            if (_is64 && code.Length >= 12 && code[0] == 0x48 && code[1] == 0xB8
                && code[10] == 0xFF && code[11] == 0xE0)
            {
                addr = BitConverter.ToUInt64(code, 2);
                continue;
            }
            // 48 B8 imm64; FF D0 — mov rax, imm64; call rax (tail-call style)
            if (_is64 && code.Length >= 12 && code[0] == 0x48 && code[1] == 0xB8
                && code[10] == 0xFF && code[11] == 0xD0)
            {
                addr = BitConverter.ToUInt64(code, 2);
                continue;
            }
            break;
        }
        return addr;
    }

    private RemoteModule? FindModule(ulong address)
    {
        if (_allModules == null) TakeModuleSnapshot();
        foreach (var rm in _allModules!)
            if (address >= rm.Base && address < rm.End) return rm;
        return null;
    }

    public bool TargetHasModule(string name)
    {
        if (_allModules == null) TakeModuleSnapshot();
        foreach (var rm in _allModules!)
            if (rm.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // Parse export table from remote process memory (Magicmida GatherModuleExportsFromRemoteProcess)
    private void GatherExports(RemoteModule m)
    {
        m.ExportTbl = new Dictionary<ulong, string>();

        var head = _api.Memory.ReadMemory(_pid, m.Base, 0x1000);
        if (head == null || head.Length < 0x40) return;

        uint lfanew = BitConverter.ToUInt32(head, 0x3C);
        if (lfanew + 0x18 > head.Length) return;

        ushort magic = BitConverter.ToUInt16(head, (int)lfanew + 0x18);
        bool pe64 = magic == 0x20B;

        int ddBase = (int)lfanew + 0x18 + (pe64 ? 0x70 : 0x60);
        if (ddBase + 8 > head.Length) return;

        uint expRva = BitConverter.ToUInt32(head, ddBase);
        uint expSize = BitConverter.ToUInt32(head, ddBase + 4);
        if (expRva == 0 || expSize == 0) return;

        var expBuf = _api.Memory.ReadMemory(_pid, m.Base + expRva, expSize);
        if (expBuf == null || expBuf.Length < 40) return;

        uint numFuncs = BitConverter.ToUInt32(expBuf, 20);
        uint numNames = BitConverter.ToUInt32(expBuf, 24);
        uint addrOfFuncs = BitConverter.ToUInt32(expBuf, 28) - expRva;
        uint addrOfNames = BitConverter.ToUInt32(expBuf, 32) - expRva;
        uint addrOfOrdinals = BitConverter.ToUInt32(expBuf, 36) - expRva;
        uint baseOrdinal = BitConverter.ToUInt32(expBuf, 16);

        if (addrOfFuncs + numFuncs * 4 > expBuf.Length) return;
        if (addrOfNames + numNames * 4 > expBuf.Length) return;
        if (addrOfOrdinals + numNames * 2 > expBuf.Length) return;

        var named = new bool[numFuncs];
        for (int i = 0; i < numNames; i++)
        {
            ushort ordIdx = BitConverter.ToUInt16(expBuf, (int)addrOfOrdinals + i * 2);
            if (ordIdx >= numFuncs) continue;
            named[ordIdx] = true;

            uint funcRva = BitConverter.ToUInt32(expBuf, (int)addrOfFuncs + ordIdx * 4);
            uint nameRva = BitConverter.ToUInt32(expBuf, (int)addrOfNames + i * 4);
            uint nameOff = nameRva - expRva;

            string funcName = "";
            if (nameOff < expBuf.Length)
            {
                int end = (int)nameOff;
                while (end < expBuf.Length && expBuf[end] != 0) end++;
                funcName = Encoding.ASCII.GetString(expBuf, (int)nameOff, end - (int)nameOff);
            }

            m.ExportTbl[m.Base + funcRva] = funcName;
        }

        for (int i = 0; i < numFuncs; i++)
        {
            if (!named[i])
            {
                uint funcRva = BitConverter.ToUInt32(expBuf, (int)addrOfFuncs + i * 4);
                m.ExportTbl[m.Base + funcRva] = "#" + (baseOrdinal + (uint)i);
            }
        }
    }

    // ── TrimHugeSections (Magicmida PEHeader.TrimHugeSections) ──
    // Trims trailing zeros from sections >1MB to reduce dump size

    private uint TrimHugeSections(byte[] buf, int numSect, int sectStart, ref uint iatRawAddr)
    {
        uint sectionAlign = 0x1000u;
        uint totalDelta = 0;
        for (int i = 0; i < numSect; i++)
        {
            int o = sectStart + i * 40;
            if (o + 40 > buf.Length) break;
            uint ptrRaw = BitConverter.ToUInt32(buf, o + 20);
            uint sizeRaw = BitConverter.ToUInt32(buf, o + 16);
            if (sizeRaw == 0 || ptrRaw + sizeRaw > buf.Length) continue;

            // Scan backward for trailing zeros (DWORD granularity)
            int zeroStart = -1;
            for (int j = (int)(sizeRaw / 4) - 1; j >= 0; j--)
            {
                if (BitConverter.ToUInt32(buf, (int)(ptrRaw + (uint)j * 4)) == 0)
                    zeroStart = j * 4;
                else
                    break;
            }

            if (zeroStart != -1 && sizeRaw - (uint)zeroStart > 1024 * 1024)
            {
                uint oldSize = sizeRaw;
                uint newSize = (uint)zeroStart;
                // Align to section alignment
                newSize = (newSize + sectionAlign - 1) & ~(sectionAlign - 1);
                if (newSize >= oldSize) continue;

                uint delta = oldSize - newSize;
                totalDelta += delta;
                // Update section header
                Array.Copy(BitConverter.GetBytes(newSize), 0, buf, o + 16, 4);

                // Shift subsequent data
                if (i < numSect - 1)
                {
                    int remaining = buf.Length - (int)(ptrRaw + oldSize);
                    if (remaining > 0)
                        Array.Copy(buf, (int)(ptrRaw + oldSize), buf, (int)(ptrRaw + newSize), remaining);

                    for (int j = i + 1; j < numSect; j++)
                    {
                        int oj = sectStart + j * 40;
                        uint pr = BitConverter.ToUInt32(buf, oj + 20);
                        Array.Copy(BitConverter.GetBytes(pr - delta), 0, buf, oj + 20, 4);
                    }
                }

                if (iatRawAddr >= ptrRaw + oldSize)
                    iatRawAddr -= delta;

                _api.Log.Info($"[Trim] Section {i}: 0x{oldSize:X} → 0x{newSize:X} (saved {delta} bytes)");
            }
        }
        return totalDelta;
    }

    private byte[]? ReadMemoryChunked(uint pid, ulong address, uint totalSize)
    {
        const uint chunkSize = 0x100000; // 1MB
        var result = new byte[totalSize];
        uint offset = 0;
        while (offset < totalSize)
        {
            uint sz = Math.Min(chunkSize, totalSize - offset);
            var chunk = _api.Memory.ReadMemory(pid, address + offset, sz);
            if (chunk == null)
            {
                _api.Log.Error($"[Dump] Chunk read failed at 0x{address + offset:X} size=0x{sz:X}");
                return null;
            }
            Array.Copy(chunk, 0, result, offset, chunk.Length);
            offset += sz;
        }
        return result;
    }

    // ── Process and Dump (Magicmida Dumper.Process + DumpToFile) ──

    public byte[]? ProcessAndDump(uint pid, ulong iat, int iatCount, ulong[] iatData,
        List<ThemidaPanel.PeSect> sections)
    {
        if (iat == 0 || iatCount == 0) return SimpleDump(pid);

        // Read full image in 1MB chunks (driver METHOD_BUFFERED can't handle 10MB+ at once)
        uint imageSize = (uint)(_imageBoundary - _imageBase);
        _api.Log.Info($"[Dump] Reading image: base=0x{_imageBase:X} size=0x{imageSize:X} ({imageSize} bytes)");
        var pe = ReadMemoryChunked(pid, _imageBase, imageSize);
        if (pe == null)
        {
            _api.Log.Error($"[Dump] ReadMemory failed at 0x{_imageBase:X}");
            return null;
        }

        uint lfanew = BitConverter.ToUInt32(pe, 0x3C);

        // Sanitize sections: PointerToRawData = VirtualAddress, SizeOfRawData = VirtualSize (Magicmida Sanitize)
        ushort numSect = BitConverter.ToUInt16(pe, (int)lfanew + 6);
        ushort optSize = BitConverter.ToUInt16(pe, (int)lfanew + 0x14);
        int sectStart = (int)lfanew + 4 + 20 + optSize;
        for (int i = 0; i < numSect; i++)
        {
            int o = sectStart + i * 40;
            if (o + 40 > pe.Length) break;
            uint rva = BitConverter.ToUInt32(pe, o + 12);
            uint vsz = BitConverter.ToUInt32(pe, o + 8);
            // PointerToRawData = VirtualAddress
            Array.Copy(BitConverter.GetBytes(rva), 0, pe, o + 20, 4);
            // SizeOfRawData = VirtualSize
            Array.Copy(BitConverter.GetBytes(vsz), 0, pe, o + 16, 4);
        }

        // SizeOfHeaders = first section's RVA (offset 0x3C from OptionalHeader for both x86/x64)
        if (numSect > 0)
        {
            uint firstRva = BitConverter.ToUInt32(pe, sectStart + 12);
            int headersOff = (int)lfanew + 0x18 + 0x3C;
            Array.Copy(BitConverter.GetBytes(firstRva), 0, pe, headersOff, 4);
        }

        // Make .text writable (Magicmida Sanitize sets IMAGE_SCN_MEM_WRITE on first section)
        if (numSect > 0)
        {
            int firstSectCharsOff = sectStart + 36;
            if (firstSectCharsOff + 4 <= pe.Length)
            {
                uint chars = BitConverter.ToUInt32(pe, firstSectCharsOff);
                chars |= 0x80000000; // IMAGE_SCN_MEM_WRITE
                Array.Copy(BitConverter.GetBytes(chars), 0, pe, firstSectCharsOff, 4);
            }
        }

        // Write resolved IAT
        uint iatRva = (uint)(iat - _imageBase);
        for (int i = 0; i < iatCount; i++)
        {
            int off = (int)iatRva + i * _ptrSize;
            if (off + _ptrSize > pe.Length) break;
            if (_is64)
                Array.Copy(BitConverter.GetBytes(iatData[i]), 0, pe, off, 8);
            else
                Array.Copy(BitConverter.GetBytes((uint)iatData[i]), 0, pe, off, 4);
        }

        // Save original IAT RVA before trimming changes it to file offset
        uint iatRvaOrig = iatRva;

        // TrimHugeSections — returns total bytes saved; iatRva becomes FILE offset after this
        uint trimDelta = TrimHugeSections(pe, numSect, sectStart, ref iatRva);
        uint iatFileOff = iatRva; // file offset for pe[] buffer access
        uint actualDataEnd = (uint)pe.Length - trimDelta;

        // Determine IAT size (Magicmida Dumper.DetermineIATSize — scan with 0x100 lookahead)
        uint lastValid = 0;
        uint iatI = 0;
        while (iatI < (uint)(iatCount * _ptrSize) && (lastValid == 0 || iatI < lastValid + 0x100))
        {
            int slot = (int)(iatI / (uint)_ptrSize);
            if (slot < iatCount && IsAPIAddress(iatData[slot]))
                lastValid = iatI;
            iatI += (uint)_ptrSize;
        }
        uint iatSize = lastValid + (uint)_ptrSize;

        // Build import directory with forward resolution by NAME
        // No IAT address modification — resolves module/name during thunk building
        if (_allModules == null) TakeModuleSnapshot();

        var thunks = new List<ImportThunk>();
        bool needNewThunk = false;
        int fwdHits = 0;

        int iatSlotCount = (int)(iatSize / (uint)_ptrSize);
        for (int i = 0; i < iatSlotCount; i++)
        {
            ulong val = iatData[i];
            if (val == 0) { needNewThunk = true; continue; }

            var rm = FindModule(val);
            if (rm != null && rm.ExportTbl == null) GatherExports(rm);

            // If not found or not in export table, try unwrapping trampolines
            // (aclayers shim hooks, Themida wrappers that JMP to real functions)
            if (rm == null || !rm.ExportTbl!.ContainsKey(val))
            {
                ulong unwrapped = TryUnwrapTrampoline(val);
                if (unwrapped != val)
                {
                    var rm2 = FindModule(unwrapped);
                    if (rm2 != null)
                    {
                        if (rm2.ExportTbl == null) GatherExports(rm2);
                        if (rm2.ExportTbl!.ContainsKey(unwrapped))
                        {
                            _api.Log.Info($"[IAT] Slot {i}: unwrapped 0x{val:X} → 0x{unwrapped:X} ({rm2.Name})");
                            val = unwrapped;
                            rm = rm2;
                        }
                    }
                }
            }

            if (rm == null)
            {
                _api.Log.Warning($"[IAT] Slot {i}: 0x{val:X} — no module found");
                needNewThunk = true; continue;
            }
            if (!rm.ExportTbl!.ContainsKey(val))
            {
                _api.Log.Warning($"[IAT] Slot {i}: 0x{val:X} in {rm.Name} — not in export table");
                needNewThunk = true; continue;
            }

            string exportName = rm.ExportTbl[val];
            string dllName = rm.Name;

            // Forward resolution: if this is e.g. ntdll!RtlInitializeSListHead,
            // map it to kernel32!InitializeSListHead
            string modShort = rm.Name.Replace(".dll", "");
            string nameKey = $"{modShort}.{exportName}";
            if (_forwardNameMap.TryGetValue(nameKey, out var fwdInfo))
            {
                dllName = fwdInfo.DllName;
                exportName = fwdInfo.ExportName;
                fwdHits++;
            }
            else if (!dllName.Equals("kernel32.dll", StringComparison.OrdinalIgnoreCase)
                     && _kernel32ExportNames.Contains(exportName))
            {
                // Fallback: kernel32 exports the same name (via forwarding to kernelbase/ntdll).
                // api-ms-win virtual DLLs prevent ChaseForwardChain from following the full chain,
                // so "kernelbase.FlsAlloc" etc. aren't in the forward map. But kernel32 exports them.
                dllName = "kernel32.dll";
                fwdHits++;
            }

            // Start new thunk only on gap/skip — NOT on DLL name change.
            // Within a contiguous IAT group, all entries were imported from the same DLL.
            if (thunks.Count == 0 || needNewThunk)
            {
                thunks.Add(new ImportThunk(dllName));
                needNewThunk = false;
            }
            thunks[thunks.Count - 1].Entries.Add(new ImportEntry(i * _ptrSize, exportName));
        }
        _api.Log.Info($"[Fwd] {fwdHits} forwards resolved by name");

        // Zero out entire IAT region so skipped/unresolved entries become null terminators.
        // This prevents the PE loader from reading stale runtime addresses (aclayers, internal ptrs)
        // past the end of each import descriptor's thunk list.
        {
            int iatZeroStart = (int)iatFileOff;
            int iatZeroLen = (int)iatSize + _ptrSize; // include trailing null
            if (iatZeroStart >= 0 && iatZeroStart + iatZeroLen <= pe.Length)
            {
                Array.Clear(pe, iatZeroStart, iatZeroLen);
                _api.Log.Info($"[IAT] Zeroed IAT region: file offset 0x{iatZeroStart:X} len 0x{iatZeroLen:X}");
            }
        }

        // Build .import section data
        int descSize = 20; // sizeof(IMAGE_IMPORT_DESCRIPTOR)
        int strOffset = (thunks.Count + 1) * descSize;
        var importData = new byte[0x2000];
        int descOff = 0;

        int sectAlignOff = (int)lfanew + 0x18 + 0x20;
        uint sectAlign = BitConverter.ToUInt32(pe, sectAlignOff);
        if (sectAlign == 0) sectAlign = 0x1000;

        // Import section VA: after last section in VIRTUAL space (from original SizeOfImage)
        int sizeOfImageOff = (int)lfanew + 0x18 + 0x38;
        uint origSizeOfImage = BitConverter.ToUInt32(pe, sizeOfImageOff);
        uint importSectVA = origSizeOfImage;
        { uint rem = importSectVA % sectAlign; if (rem > 0) importSectVA += sectAlign - rem; }

        // Import section file offset: after actual trimmed data
        uint importSectFileOff = actualDataEnd;

        for (int ti = 0; ti < thunks.Count; ti++)
        {
            var thunk = thunks[ti];

            // IMAGE_IMPORT_DESCRIPTOR: OriginalFirstThunk (offset 0), Name (offset 12), FirstThunk (offset 16)
            uint firstThunk = iatRvaOrig + (uint)thunk.Entries[0].IATOffset;
            uint nameRva = importSectVA + (uint)strOffset;

            // OriginalFirstThunk = 0 (like MagicmidaCSharp — loader uses FirstThunk for lookup)
            Array.Copy(BitConverter.GetBytes(nameRva), 0, importData, descOff + 12, 4);    // Name
            Array.Copy(BitConverter.GetBytes(firstThunk), 0, importData, descOff + 16, 4); // FirstThunk
            descOff += descSize;

            var nameBytes = Encoding.ASCII.GetBytes(thunk.Name);
            if (strOffset + nameBytes.Length + 1 < importData.Length)
                Array.Copy(nameBytes, 0, importData, strOffset, nameBytes.Length);
            strOffset += nameBytes.Length + 1;

            foreach (var entry in thunk.Entries)
            {
                int peOff = (int)iatFileOff + entry.IATOffset;
                if (peOff + _ptrSize > pe.Length) continue;

                string funcName = entry.ExportName;

                if (funcName.StartsWith("#"))
                {
                    // Ordinal import
                    ulong ordFlag = _is64 ? 0x8000000000000000UL : 0x80000000UL;
                    ulong ordVal = ordFlag | uint.Parse(funcName.Substring(1));
                    if (_is64) Array.Copy(BitConverter.GetBytes(ordVal), 0, pe, peOff, 8);
                    else Array.Copy(BitConverter.GetBytes((uint)ordVal), 0, pe, peOff, 4);
                    continue;
                }

                // Name import: write RVA to hint/name entry in import section
                strOffset += 2; // hint (2 bytes, zero)
                uint hintNameRva = importSectVA + (uint)(strOffset - 2);
                if (_is64) Array.Copy(BitConverter.GetBytes((ulong)hintNameRva), 0, pe, peOff, 8);
                else Array.Copy(BitConverter.GetBytes(hintNameRva), 0, pe, peOff, 4);

                var fnBytes = Encoding.ASCII.GetBytes(funcName);
                if (strOffset + fnBytes.Length + 1 >= importData.Length - 0x100)
                    Array.Resize(ref importData, importData.Length + 0x2000);
                Array.Copy(fnBytes, 0, importData, strOffset, fnBytes.Length);
                strOffset += fnBytes.Length + 1;
            }
        }

        uint importSectSize = (uint)strOffset;
        { uint r = importSectSize % sectAlign; if (r > 0) importSectSize += sectAlign - r; }

        // Fix PE headers
        // EP
        Array.Copy(BitConverter.GetBytes((uint)(_oep - _imageBase)), 0, pe, (int)lfanew + 0x28, 4);

        // Disable ASLR (DllCharacteristics offset is 0x46 from OptionalHeader for both x86/x64)
        int dllCharsOff = (int)lfanew + 0x18 + 0x46;
        if (dllCharsOff + 2 <= pe.Length)
        {
            ushort dc = BitConverter.ToUInt16(pe, dllCharsOff);
            if ((dc & 0x40) != 0)
            {
                dc &= unchecked((ushort)~0x40);
                Array.Copy(BitConverter.GetBytes(dc), 0, pe, dllCharsOff, 2);
            }
        }

        // Data directories base
        int ddBaseOff = (int)lfanew + 0x18 + (_is64 ? 0x70 : 0x60);

        // IAT data directory (entry 12)
        int iatDirOff = ddBaseOff + 12 * 8;
        if (iatDirOff + 8 <= pe.Length)
        {
            Array.Copy(BitConverter.GetBytes(iatRvaOrig), 0, pe, iatDirOff, 4);
            Array.Copy(BitConverter.GetBytes(iatSize + (uint)_ptrSize), 0, pe, iatDirOff + 4, 4);
        }

        // Import data directory (entry 1)
        int importDirOff = ddBaseOff + 1 * 8;
        if (importDirOff + 8 <= pe.Length)
        {
            Array.Copy(BitConverter.GetBytes(importSectVA), 0, pe, importDirOff, 4);
            Array.Copy(BitConverter.GetBytes((uint)(thunks.Count * descSize)), 0, pe, importDirOff + 4, 4);
        }

        // Update SizeOfImage
        uint newSizeOfImage = importSectVA + importSectSize;
        Array.Copy(BitConverter.GetBytes(newSizeOfImage), 0, pe, sizeOfImageOff, 4);

        // Add .import section header
        int newSectOff = sectStart + numSect * 40;
        if (newSectOff + 40 <= pe.Length)
        {
            var impName = Encoding.ASCII.GetBytes(".import\0");
            Array.Copy(impName, 0, pe, newSectOff, 8);
            Array.Copy(BitConverter.GetBytes(importSectSize), 0, pe, newSectOff + 8, 4);      // VirtualSize
            Array.Copy(BitConverter.GetBytes(importSectVA), 0, pe, newSectOff + 12, 4);        // VirtualAddress
            Array.Copy(BitConverter.GetBytes(importSectSize), 0, pe, newSectOff + 16, 4);      // SizeOfRawData
            Array.Copy(BitConverter.GetBytes(importSectFileOff), 0, pe, newSectOff + 20, 4);   // PointerToRawData
            Array.Copy(BitConverter.GetBytes(0xC0000040U), 0, pe, newSectOff + 36, 4);    // Characteristics: R|IDATA

            // Increment NumberOfSections
            BitConverter.GetBytes((ushort)(numSect + 1)).CopyTo(pe, (int)lfanew + 6);
        }

        // Combine: trimmed PE data + import section at file offset
        var result = new byte[(int)importSectFileOff + (int)importSectSize];
        int copyLen = Math.Min(pe.Length, (int)importSectFileOff);
        Array.Copy(pe, result, copyLen);
        Array.Copy(importData, 0, result, (int)importSectFileOff, Math.Min(strOffset, importData.Length));

        _api.Log.Info($"[Dump] {thunks.Count} import thunks, .import VA=0x{importSectVA:X} fileOff=0x{importSectFileOff:X}, size {result.Length} bytes");
        return result;
    }

    private byte[]? SimpleDump(uint pid)
    {
        var pe = ReadMemoryChunked(pid, _imageBase, (uint)(_imageBoundary - _imageBase));
        if (pe == null) return null;

        uint lfanew = BitConverter.ToUInt32(pe, 0x3C);
        uint oepRva = (uint)(_oep - _imageBase);
        Array.Copy(BitConverter.GetBytes(oepRva), 0, pe, (int)lfanew + 0x28, 4);

        // Disable ASLR (DllCharacteristics offset 0x46 from OptionalHeader for both x86/x64)
        int dllCharsOff = (int)lfanew + 0x18 + 0x46;
        if (dllCharsOff + 2 <= pe.Length)
        {
            ushort dc = BitConverter.ToUInt16(pe, dllCharsOff);
            dc &= unchecked((ushort)~0x40);
            Array.Copy(BitConverter.GetBytes(dc), 0, pe, dllCharsOff, 2);
        }

        return pe;
    }
}

// ════════════════════════════════════════════════════════════════════════════
//  P/Invoke for local forward collection
// ════════════════════════════════════════════════════════════════════════════

internal static class NativeMethods
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr LoadLibraryW(string lpFileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool FreeLibrary(IntPtr hModule);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    public static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    public static extern IntPtr GetModuleHandleA(string lpModuleName);
}
