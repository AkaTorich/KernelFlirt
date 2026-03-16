using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KernelFlirt.SDK;
using Iced.Intel;

namespace ThemidaPlugin;

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

    // Sections
    private ulong _textBase, _textEnd;     // .text (first executable section)
    private ulong _baseOfData;             // start of data (end of code)
    private ulong _tmBase, _tmEnd;         // .themida / .winlice combined range
    private ulong _bootBase, _bootEnd;     // .boot section
    private List<SectionInfo> _sections = new();
    private record SectionInfo(string Name, uint Rva, uint VirtualSize, uint Chars);

    // ── State machine ──
    private enum Phase { Idle, TextGuarded, TextStepRearm, OepStepThrough, IatTracing, Done }
    private Phase _phase = Phase.Idle;

    // OEP finding
    private int _guardHitCount;
    private uint _guardOldProt;
    private ulong _firstTextExecAddr;
    private ulong _oepAddr;

    // IAT tracing
    private ulong _iatBase;
    private int _iatCount;              // number of pointer-sized slots
    private ulong[] _iatData = [];      // local copy of IAT
    private int _iatIdx;                // current slot being traced
    private int _iatResolvedCount;
    private int _iatFailedCount;
    private ulong _savedRip, _savedRsp;
    private List<uint> _suspendedTids = new();

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
    //  Detection
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

        int sectOff = (int)lfanew + 4 + 20 + optSize;
        _sections.Clear();
        _textBase = 0; _tmBase = 0; _bootBase = 0; _baseOfData = 0;
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
            _sections.Add(new SectionInfo(name, rva, vsz, ch));

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
                _bootBase = sBase; _bootEnd = sEnd;
                // Include .boot in TM range for trace
                if (_tmBase == 0) { _tmBase = sBase; _tmEnd = sEnd; }
                else { _tmBase = Math.Min(_tmBase, sBase); _tmEnd = Math.Max(_tmEnd, sEnd); }
            }
            else if (_textBase == 0 && (ch & 0x20000000) != 0) // first CODE section
            {
                _textBase = sBase;
                _textEnd = sEnd;
                // BaseOfData = textRVA + code size (heuristic: next section)
                _baseOfData = sEnd;
            }

            string perm = ((ch & 0x20000000) != 0 ? "X" : "") +
                          ((ch & 0x40000000) != 0 ? "R" : "") +
                          ((ch & 0x80000000) != 0 ? "W" : "");
            sb.AppendLine($"  [{i}] {name,-10} 0x{rva:X8} sz=0x{vsz:X8} {perm}");
        }

        // Refine _baseOfData: first non-executable section after .text
        for (int i = 0; i < _sections.Count; i++)
        {
            ulong sBase = _imageBase + _sections[i].Rva;
            if (sBase > _textBase && (_sections[i].Chars & 0x20000000) == 0)
            {
                _baseOfData = sBase;
                break;
            }
        }

        // Check EP is outside .text (packed)
        ulong epAddr = _imageBase + _entryPointRva;
        bool epInText = epAddr >= _textBase && epAddr < _textEnd;

        _api.Log.Info(sb.ToString());

        if (!foundTm)
        {
            _api.Log.Warning("No .themida/.boot sections found — not Themida protected.");
            _detected = false;
            SetStatus("Not Themida");
            return;
        }

        if (epInText)
            _api.Log.Warning("EP is inside .text — binary may not be packed (or already unpacked).");

        _detected = true;
        _api.Log.Warning($"Themida detected! .text=0x{_textBase:X}-0x{_textEnd:X}, TM range=0x{_tmBase:X}-0x{_tmEnd:X}");
        SetStatus($"Detected. .text=0x{_textBase:X}, TM=0x{_tmBase:X}");
    }

    // ════════════════════════════════════════════════════════════════
    //  Unpacking start/stop
    // ════════════════════════════════════════════════════════════════

    public void StartUnpacking()
    {
        if (!_detected) { DetectProtector(); if (!_detected) return; }
        if (_phase != Phase.Idle && _phase != Phase.Done) { _api.Log.Warning("Already unpacking."); return; }

        uint pid = _api.TargetPid;

        // Guard .text with PAGE_NOACCESS
        var (ok, oldProt) = _api.Memory.ProtectMemory(pid, _textBase, (uint)(_textEnd - _textBase), 0x01);
        if (!ok) { _api.Log.Error("Failed to set PAGE_NOACCESS on .text"); return; }
        _guardOldProt = oldProt;
        _guardHitCount = 0;
        _firstTextExecAddr = 0;

        _phase = Phase.TextGuarded;
        _api.Log.Warning("[Unpack] PAGE_NOACCESS on .text — press F9 to run.");
        SetStatus("Guarding .text — press F9");
    }

    public void StopUnpacking()
    {
        if (_phase == Phase.Idle) return;

        uint pid = _api.TargetPid;

        // Restore .text
        if (_guardOldProt != 0)
            _api.Memory.ProtectMemory(pid, _textBase, (uint)(_textEnd - _textBase), _guardOldProt);

        // Resume suspended threads
        foreach (var tid in _suspendedTids)
            _api.Process.ResumeThread(tid);
        _suspendedTids.Clear();

        _phase = Phase.Idle;
        _api.Log.Info("[Unpack] Stopped.");
        SetStatus("Idle");
    }

    private void OnBeforeRun()
    {
        // Auto-start if not yet started
        if (_phase == Phase.Idle && _detected)
            StartUnpacking();
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
            if (fault < _textBase || fault >= _textEnd) return false; // not our AV

            _guardHitCount++;
            uint access = evt.AccessType; // 0=read, 1=write, 8=execute

            if (access == 8) // EXECUTE → OEP!
            {
                _firstTextExecAddr = fault;
                _api.Log.Warning($"[OEP] Execute in .text at 0x{fault:X} (after {_guardHitCount} hits)");

                // Restore protection, suppress AV + TF → next event = SingleStep at OEP
                _api.Memory.ProtectMemory(pid, _textBase, (uint)(_textEnd - _textBase), _guardOldProt);
                _phase = Phase.OepStepThrough;
                evt.ContinueMode = 3; // HANDLED
                return true;
            }

            // Read/write — Themida decrypting .text
            if (_guardHitCount <= 5 || _guardHitCount % 1000 == 0)
            {
                string at = access == 0 ? "read" : "write";
                _api.Log.Info($"[Guard] .text {at} 0x{fault:X} from 0x{rip:X} (#{_guardHitCount})");
            }

            if (_guardHitCount > 500000)
            {
                _api.Log.Error("[Guard] 500K hits — aborting.");
                _api.Memory.ProtectMemory(pid, _textBase, (uint)(_textEnd - _textBase), _guardOldProt);
                _phase = Phase.Idle;
                return false;
            }

            // Restore protection temporarily, suppress AV + TF → re-arm on SingleStep
            _api.Memory.ProtectMemory(pid, _textBase, (uint)(_textEnd - _textBase), _guardOldProt);
            _phase = Phase.TextStepRearm;
            evt.ContinueMode = 3; // HANDLED
            return true;
        }

        // ── TextStepRearm: re-arm PAGE_NOACCESS after stepping past read/write ──
        if (_phase == Phase.TextStepRearm && evt.Type == PluginDebugEventType.SingleStep)
        {
            _api.Memory.ProtectMemory(pid, _textBase, (uint)(_textEnd - _textBase), 0x01);
            _phase = Phase.TextGuarded;

            // Check if RIP landed in .text (unlikely but handle it)
            if (rip >= _textBase && rip < _textEnd)
            {
                _firstTextExecAddr = rip;
                _api.Memory.ProtectMemory(pid, _textBase, (uint)(_textEnd - _textBase), _guardOldProt);
                HandleOepFound(pid, rip, evt);
                return false; // break at OEP
            }

            return true; // silent continue
        }

        // ── OepStepThrough: AV suppressed, now at first .text instruction ──
        if (_phase == Phase.OepStepThrough && evt.Type == PluginDebugEventType.SingleStep)
        {
            HandleOepFound(pid, rip, evt);

            // If IAT tracing started, keep running
            if (_phase == Phase.IatTracing)
            {
                return true;
            }
            return false; // break at OEP
        }

        // ── IatTracing: driver-side trace reports ──
        if (_phase == Phase.IatTracing)
        {
            if (evt.Type == PluginDebugEventType.SingleStep)
            {
                HandleTraceResult(pid, rip, evt);
                return true; // keep going
            }
            // Unexpected event during tracing — log and continue
            if (evt.Type == PluginDebugEventType.AccessViolation ||
                evt.Type == PluginDebugEventType.Breakpoint)
            {
                _api.Log.Info($"[Trace] Unexpected {evt.Type} at 0x{rip:X}, continuing");
                evt.ContinueMode = 0;
                return true;
            }
        }

        return false;
    }

    // ════════════════════════════════════════════════════════════════
    //  OEP found
    // ════════════════════════════════════════════════════════════════

    private void HandleOepFound(uint pid, ulong rip, PluginDebugEvent evt)
    {
        // Check for virtualized OEP (Magicmida: first byte is jmp into .themida)
        var oepCode = _api.Memory.ReadMemory(pid, rip, 16);
        if (oepCode != null && oepCode.Length >= 5 && oepCode[0] == 0xE9)
        {
            int disp = BitConverter.ToInt32(oepCode, 1);
            ulong target = rip + 5 + (ulong)(long)disp;
            if (IsInTmRange(target))
                _api.Log.Warning($"[OEP] Virtualized OEP: jmp 0x{target:X} (into .themida)");
        }

        // Check return address for MSVC virtualized OEP
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
                    _api.Log.Info($"[OEP] Return address 0x{retAddr:X} is in .themida → checking MSVC OEP");
                    ulong realOep = TryFindMsvcOep(pid, _firstTextExecAddr);
                    if (realOep != 0)
                    {
                        _api.Log.Warning($"[OEP] Real MSVC OEP: 0x{realOep:X}");
                        rip = realOep;
                        _api.Memory.WriteRip(pid, evt.ThreadId, rip);
                    }
                }
            }
        }

        _oepAddr = rip;
        _api.Log.Warning($"[OEP] ★ OEP = 0x{_oepAddr:X}");
        _api.UI.NavigateDisassembly(_oepAddr);
        SetStatus($"OEP = 0x{_oepAddr:X}");

        // Auto-fix IAT?
        bool autoIat = false;
        try { Application.Current?.Dispatcher.Invoke(() => autoIat = _chkAutoIat.IsChecked == true); }
        catch { autoIat = true; }

        if (autoIat)
            StartIatTrace(pid, evt);
        else
            _phase = Phase.Done;
    }

    private ulong TryFindMsvcOep(uint pid, ulong hitAddr)
    {
        if (_majorLinkerVersion is not (9 or 10 or 11 or 12 or 14)) return 0;

        uint len = (uint)(_baseOfData > _textBase ? _baseOfData - _textBase : _textEnd - _textBase);
        if (len > 0x200000) len = 0x200000;

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
        return 0;
    }

    // ════════════════════════════════════════════════════════════════
    //  IAT scanning — find IAT start via code references
    // ════════════════════════════════════════════════════════════════

    private ulong FindIatAddress(uint pid)
    {
        // Read .text code
        uint codeSize = (uint)(_baseOfData > _textBase ? _baseOfData - _textBase : _textEnd - _textBase);
        if (codeSize > 0x400000) codeSize = 0x400000;

        var code = _api.Memory.ReadMemory(pid, _textBase, codeSize);
        if (code == null) { _api.Log.Error("Cannot read .text for IAT scan"); return 0; }

        // Scan from OEP for call/jmp [rip+disp] (x64) or call/jmp [addr] (x86)
        ulong iatRef = FindCallOrJmpPtr(code, _textBase, codeSize, _oepAddr, pid);

        if (iatRef == 0)
        {
            // Fallback: scan all data sections for pointers into .themida
            _api.Log.Info("[IAT] No code reference found, scanning data sections for TM pointers");
            iatRef = ScanDataForTmPointers(pid);
        }

        if (iatRef == 0) return 0;
        _api.Log.Info($"[IAT] First ref: 0x{iatRef:X}");

        // Walk backwards to find IAT start
        return FindIatStart(pid, iatRef);
    }

    private ulong FindCallOrJmpPtr(byte[] code, ulong textBase, uint codeSize, ulong startAddr, uint pid)
    {
        // Use Iced disassembler to find call/jmp [mem] instructions
        int offset = (int)(startAddr - textBase);
        if (offset < 0 || offset >= code.Length) offset = 0;

        var reader = new ByteArrayCodeReader(code, offset, code.Length - offset);
        var decoder = Iced.Intel.Decoder.Create(_is64 ? 64 : 32, reader, startAddr);
        int instrCount = 0;

        while (decoder.IP < textBase + codeSize && instrCount < 500)
        {
            var instr = decoder.Decode();
            if (instr.IsInvalid) break;
            instrCount++;

            // Looking for: FF 15 xx xx xx xx (call [rip+disp]) or FF 25 xx xx xx xx (jmp [rip+disp])
            if ((instr.Mnemonic == Mnemonic.Call || instr.Mnemonic == Mnemonic.Jmp) &&
                instr.Op0Kind == OpKind.Memory)
            {
                ulong memAddr = instr.IPRelativeMemoryAddress;
                if (memAddr == 0 && instr.MemoryDisplacement64 != 0)
                    memAddr = instr.MemoryDisplacement64;

                if (memAddr == 0) continue;

                // Read what the pointer points to
                var ptrData = _api.Memory.ReadMemory(pid, memAddr, (uint)_ptrSize);
                if (ptrData == null) continue;
                ulong target = _is64 ? BitConverter.ToUInt64(ptrData) : BitConverter.ToUInt32(ptrData);

                // If it points outside .text (to a DLL or .themida) — this is an IAT ref
                if (target > textBase + codeSize || target < textBase)
                    return memAddr;
            }

            // Stop at ret (function boundary)
            if (instr.Mnemonic == Mnemonic.Ret)
                break;
        }
        return 0;
    }

    private ulong ScanDataForTmPointers(uint pid)
    {
        foreach (var sect in _sections)
        {
            bool isExec = (sect.Chars & 0x20000000) != 0;
            if (isExec) continue;
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

    private ulong FindIatStart(uint pid, ulong iatRef)
    {
        // Read backwards from iatRef looking for valid IAT entries
        const int maxSlots = 5120;
        int readBack = maxSlots * _ptrSize;
        ulong readStart = iatRef > (ulong)readBack ? iatRef - (ulong)readBack : _imageBase;

        var data = _api.Memory.ReadMemory(pid, readStart, (uint)(iatRef - readStart + (ulong)(_ptrSize * 64)));
        if (data == null) return iatRef;

        // Walk backward from iatRef
        ulong result = iatRef;
        int refOff = (int)(iatRef - readStart);
        int consec0 = 0;

        for (int i = refOff - _ptrSize; i >= 0; i -= _ptrSize)
        {
            ulong val = _is64 ? BitConverter.ToUInt64(data, i) : BitConverter.ToUInt32(data, i);

            if (val == 0)
            {
                consec0++;
                if (consec0 > 64) break;
            }
            else if (IsInTmRange(val) || IsApiAddress(pid, val))
            {
                result = readStart + (ulong)i;
                consec0 = 0;
            }
            else
            {
                break;
            }
        }

        return result;
    }

    private bool IsApiAddress(uint pid, ulong addr)
    {
        // Quick check: is it in a loaded DLL (not in main image)?
        if (addr >= _imageBase && addr < _imageBoundary) return false;
        if (addr < 0x10000) return false;

        // Check if it's in any loaded module
        var modules = _api.Symbols.GetModules();
        foreach (var m in modules)
        {
            if (m.BaseAddress == _imageBase) continue;
            if (addr >= m.BaseAddress && addr < m.BaseAddress + m.Size)
                return true;
        }
        return false;
    }

    // ════════════════════════════════════════════════════════════════
    //  IAT tracing via ContinueMode=4 (driver-side trace)
    // ════════════════════════════════════════════════════════════════

    private void StartIatTrace(uint pid, PluginDebugEvent evt)
    {
        // Find IAT
        ulong iatAddr = FindIatAddress(pid);
        if (iatAddr == 0)
        {
            _api.Log.Error("[IAT] Cannot find IAT address. Use manual Fix IAT.");
            _phase = Phase.Done;
            return;
        }

        _iatBase = iatAddr;
        _api.Log.Warning($"[IAT] IAT at 0x{_iatBase:X}");

        // Read IAT
        const int maxSlots = 5120;
        int readSize = maxSlots * _ptrSize;
        var raw = _api.Memory.ReadMemory(pid, _iatBase, (uint)readSize);
        if (raw == null) { _api.Log.Error("Cannot read IAT"); _phase = Phase.Done; return; }

        // Parse IAT into array, find end
        _iatData = new ulong[maxSlots];
        _iatCount = 0;
        int consec0 = 0;

        for (int i = 0; i < maxSlots; i++)
        {
            int off = i * _ptrSize;
            if (off + _ptrSize > raw.Length) break;
            ulong val = _is64 ? BitConverter.ToUInt64(raw, off) : BitConverter.ToUInt32(raw, off);
            _iatData[i] = val;

            if (val == 0)
            {
                consec0++;
                if (consec0 > 64) break;
                _iatCount = i + 1;
            }
            else
            {
                consec0 = 0;
                _iatCount = i + 1;
            }
        }

        // Count wrapped entries
        int wrappedCount = 0;
        for (int i = 0; i < _iatCount; i++)
            if (IsInTmRange(_iatData[i])) wrappedCount++;

        _api.Log.Warning($"[IAT] {_iatCount} slots, {wrappedCount} wrapped in .themida");

        if (wrappedCount == 0)
        {
            _api.Log.Info("[IAT] No wrapped imports — IAT is clean.");
            _phase = Phase.Done;
            FinishUnpacking(pid);
            return;
        }

        // Save state for IAT tracing
        var regs = _api.Memory.ReadRegisters(pid, evt.ThreadId);
        _savedRip = _oepAddr;
        _savedRsp = GetReg(regs, _is64 ? "RSP" : "ESP");

        // Suspend other threads
        _suspendedTids.Clear();
        var threads = _api.Process.EnumThreads(pid);
        foreach (var t in threads)
        {
            if (t.ThreadId != _api.SelectedThreadId)
            {
                _api.Process.SuspendThread(t.ThreadId);
                _suspendedTids.Add(t.ThreadId);
            }
        }
        _api.Log.Info($"[IAT] Suspended {_suspendedTids.Count} threads");

        // Start tracing
        _iatIdx = -1;
        _iatResolvedCount = 0;
        _iatFailedCount = 0;
        _phase = Phase.IatTracing;

        // Advance to first wrapped entry and launch trace
        if (!AdvanceToNextWrapper(evt))
        {
            FinishIatTrace(pid);
            return;
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

        if (_iatIdx >= _iatCount) return false; // all done

        ulong wrapperAddr = _iatData[_iatIdx];

        // Set RIP to wrapper, use ContinueMode=4 (TRACE) with TM range
        evt.NewRip = wrapperAddr;
        evt.NewRsp = _savedRsp;
        evt.ContinueMode = 4; // KF_CONTINUE_TRACE
        evt.TraceRangeBase = _tmBase;
        evt.TraceRangeEnd = _tmEnd;
        evt.TraceMaxSteps = 500000;

        if (_iatResolvedCount < 5 || _iatResolvedCount % 50 == 0)
            _api.Log.Info($"[IAT] Tracing slot {_iatIdx} wrapper 0x{wrapperAddr:X}...");

        return true;
    }

    private void HandleTraceResult(uint pid, ulong rip, PluginDebugEvent evt)
    {
        ulong slotAddr = _iatBase + (ulong)(_iatIdx * _ptrSize);

        // RIP exited TM range → should be at real API
        bool isApi = !IsInTmRange(rip) &&
                     !(rip >= _imageBase && rip < _imageBoundary) &&
                     rip >= 0x10000;

        if (isApi)
        {
            // Write resolved address to IAT slot
            byte[] fix = _is64 ? BitConverter.GetBytes(rip) : BitConverter.GetBytes((uint)rip);
            _api.Memory.WriteMemory(pid, slotAddr, fix);
            _iatData[_iatIdx] = rip;
            _iatResolvedCount++;

            if (_iatResolvedCount <= 10 || _iatResolvedCount % 20 == 0)
            {
                string name = _api.Symbols.ResolveAddress(rip) ?? $"0x{rip:X}";
                _api.Log.Info($"[IAT] #{_iatResolvedCount} [0x{slotAddr:X}] → {name}");
            }
        }
        else
        {
            _iatFailedCount++;
            _api.Log.Info($"[IAT] Slot {_iatIdx} → 0x{rip:X} (not API, skip)");
        }

        // Next wrapper
        if (!AdvanceToNextWrapper(evt))
        {
            FinishIatTrace(pid);
            // Break at OEP
            evt.ContinueMode = 0;
            evt.NewRip = _savedRip;
            evt.NewRsp = _savedRsp;
        }
    }

    private void FinishIatTrace(uint pid)
    {
        // Restore context
        _api.Memory.WriteRipAndRsp(_api.SelectedThreadId, _savedRip, _savedRsp);

        // Resume threads
        foreach (var tid in _suspendedTids)
            _api.Process.ResumeThread(tid);
        _suspendedTids.Clear();

        _api.Log.Warning($"[IAT] Done: {_iatResolvedCount} resolved, {_iatFailedCount} failed.");
        SetStatus($"IAT fixed: {_iatResolvedCount} resolved. OEP=0x{_oepAddr:X}");

        FinishUnpacking(pid);
    }

    private void FinishUnpacking(uint pid)
    {
        _phase = Phase.Done;

        // Refresh UI
        _api.UI.NavigateDisassembly(_oepAddr);

        bool autoDump = false;
        try { Application.Current?.Dispatcher.Invoke(() => autoDump = _chkAutoDump.IsChecked == true); }
        catch { }

        if (autoDump)
            DumpPe();
        else
            _api.Log.Warning($"[Done] OEP=0x{_oepAddr:X}. Use 'Dump PE' to save.");
    }

    // ════════════════════════════════════════════════════════════════
    //  Manual IAT fix (from button)
    // ════════════════════════════════════════════════════════════════

    private void ManualFixIat()
    {
        if (!_api.IsConnected || !_api.IsBreakState)
        { _api.Log.Warning("Must be in Break state."); return; }

        if (_oepAddr == 0)
        { _api.Log.Warning("OEP not found yet. Run unpacking first."); return; }

        _api.Log.Info("[IAT] Manual IAT fix — will scan and resolve on next F9.");

        // We need to be in a debug event to use ContinueMode=4.
        // Queue the IAT fix for next OnDebugEventFilter call.
        _phase = Phase.OepStepThrough;
        _api.SingleStep(); // trigger a SingleStep event → OepStepThrough handler starts IAT
    }

    // ════════════════════════════════════════════════════════════════
    //  PE dump
    // ════════════════════════════════════════════════════════════════

    public void DumpPe()
    {
        if (!_api.IsConnected || !_api.IsBreakState)
        { _api.Log.Warning("Must be in Break state."); return; }

        uint pid = _api.TargetPid;
        if (_imageBase == 0) { _api.Log.Warning("Detect protector first."); return; }

        // Read entire image
        var pe = _api.Memory.ReadMemory(pid, _imageBase, _imageSize);
        if (pe == null) { _api.Log.Error("Cannot read PE image."); return; }

        // Fix EP to OEP
        uint lfanew = BitConverter.ToUInt32(pe, 0x3C);
        uint oepRva = (uint)(_oepAddr > 0 ? _oepAddr - _imageBase : _entryPointRva);
        byte[] oepBytes = BitConverter.GetBytes(oepRva);
        Array.Copy(oepBytes, 0, pe, (int)lfanew + 0x28, 4);

        // Disable ASLR
        int dllCharsOff = (int)lfanew + 0x18 + (_is64 ? 0x46 : 0x2E); // DllCharacteristics
        if (dllCharsOff + 2 <= pe.Length)
        {
            ushort dllChars = BitConverter.ToUInt16(pe, dllCharsOff);
            if ((dllChars & 0x40) != 0) // IMAGE_DLLCHARACTERISTICS_DYNAMIC_BASE
            {
                dllChars &= unchecked((ushort)~0x40);
                Array.Copy(BitConverter.GetBytes(dllChars), 0, pe, dllCharsOff, 2);
                _api.Log.Info("[Dump] ASLR disabled.");
            }
        }

        // Fix IAT directory if we have IAT data
        if (_iatBase != 0 && _iatCount > 0)
        {
            uint iatRva = (uint)(_iatBase - _imageBase);
            uint iatSize = (uint)(_iatCount * _ptrSize);

            // Write resolved IAT into dump
            for (int i = 0; i < _iatCount; i++)
            {
                int off = (int)(iatRva + (uint)(i * _ptrSize));
                if (off + _ptrSize > pe.Length) break;
                if (_is64)
                    Array.Copy(BitConverter.GetBytes(_iatData[i]), 0, pe, off, 8);
                else
                    Array.Copy(BitConverter.GetBytes((uint)_iatData[i]), 0, pe, off, 4);
            }

            // Update IAT data directory
            int ddBase = (int)lfanew + 0x18 + (_is64 ? 0x78 : 0x60); // DataDirectory[0]
            int iatDirOff = ddBase + 12 * 8; // IAT = directory index 12
            if (iatDirOff + 8 <= pe.Length)
            {
                Array.Copy(BitConverter.GetBytes(iatRva), 0, pe, iatDirOff, 4);
                Array.Copy(BitConverter.GetBytes(iatSize), 0, pe, iatDirOff + 4, 4);
            }
        }

        // Fix section permissions — make all sections RWX (simplifies running)
        int sectStart = (int)lfanew + 4 + 20 + BitConverter.ToUInt16(pe, (int)lfanew + 0x14);
        ushort numSect = BitConverter.ToUInt16(pe, (int)lfanew + 6);
        for (int i = 0; i < numSect; i++)
        {
            int off = sectStart + i * 40 + 36; // Characteristics
            if (off + 4 > pe.Length) break;
            uint chars = BitConverter.ToUInt32(pe, off);
            chars |= 0xE0000000; // RWX
            Array.Copy(BitConverter.GetBytes(chars), 0, pe, off, 4);
        }

        // Save dialog
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
                    File.WriteAllBytes(dlg.FileName, pe);
                    _api.Log.Warning($"[Dump] Saved to {dlg.FileName} ({pe.Length} bytes, OEP RVA=0x{oepRva:X})");
                    SetStatus($"Dumped: {dlg.FileName}");
                }
                catch (Exception ex)
                {
                    _api.Log.Error($"[Dump] Save failed: {ex.Message}");
                }
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

    // ── UI helpers ──

    private static CheckBox MakeCb(string text, bool isChecked, Brush fg) => new()
    {
        Content = text, IsChecked = isChecked, Foreground = fg,
        Margin = new Thickness(0, 2, 0, 2)
    };

    private static GroupBox Grp(string header, UIElement[] items, Brush fg)
    {
        var sp = new StackPanel();
        foreach (var item in items) sp.Children.Add(item);
        return new GroupBox
        {
            Header = new TextBlock { Text = header, Foreground = fg, FontWeight = FontWeights.SemiBold },
            Content = sp, Margin = new Thickness(0, 5, 0, 5),
            BorderBrush = Brushes.Gray, Foreground = fg
        };
    }

    private static void Btn(WrapPanel panel, string text, Action click)
    {
        var b = new Button
        {
            Content = text, Padding = new Thickness(12, 4, 12, 4),
            Margin = new Thickness(0, 0, 6, 0)
        };
        b.Click += (_, _) => click();
        panel.Children.Add(b);
    }
}
