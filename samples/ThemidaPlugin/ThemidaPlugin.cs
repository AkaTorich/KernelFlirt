using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KernelFlirt.SDK;
using Microsoft.Win32;

namespace ThemidaPlugin;

public class ThemidaPlugin : IKernelFlirtPlugin
{
    public string Name => "Themida Unpacker";
    public string Description => "Automatic Themida/WinLicense unpacker with OEP finder, IAT fixer, and dumper";
    public string Version => "4.0";

    private IDebuggerApi? _api;
    private ThemidaPanel? _panel;

    public void Initialize(IDebuggerApi api)
    {
        _api = api;
        _panel = new ThemidaPanel(api);
        api.UI.AddToolPanel("Themida", _panel);
        api.UI.AddMenuItem("Themida: Detect protector", () => _panel.DetectProtector());
        api.UI.AddMenuItem("Themida: Start unpacking", () => _panel.StartUnpacking());
        api.Log.Info("Themida Unpacker v4.0 loaded (x86/x64). See 'Themida' tab.");
    }

    public void Shutdown()
    {
        _api?.Log.Info("Themida Unpacker unloaded");
    }
}

public class ThemidaPanel : ScrollViewer
{
    private readonly IDebuggerApi _api;

    // Detection results
    private bool _isThemida;
    private bool _isWinLicense;
    private bool _is64;         // PE32+ (x64) or PE32 (x86)
    private int _ptrSize;       // 8 for x64, 4 for x86
    private ulong _themidaSectionBase;
    private uint _themidaSectionSize;
    private ulong _bootSectionBase;
    private uint _bootSectionSize;
    private ulong _originalTextBase;
    private uint _originalTextSize;
    private ulong _baseOfData;  // start of .rdata/.data (end of code)
    private ulong _originalImageBase;
    private uint _originalImageSize;
    private ulong _originalEntryPointRva;
    private ulong _bootJmpRaxAddr; // address of "jmp rax/eax" in .boot section
    private ushort _majorLinkerVersion;

    // Unpacking state machine
    private enum UnpackPhase
    {
        Idle,
        DecompDetecting,            // Phase 1: confirm decompressor is writing to .text
        DecompRunning,              // Phase 2: guard on last page, waiting for decomp to finish
        WaitingForApiCall,          // Phase 3: HW BPs on API functions, check return addr in .text
        TextGuarded,                // PAGE_NOACCESS on .text — waiting for execute access = OEP
        TextStepRearm,              // Single-stepping past a read/write AV, will re-arm PAGE_NOACCESS
        OepStepThrough,             // Execute AV suppressed, waiting for SingleStep to break at OEP
        Done
    }

    private ulong _idataBase;      // .idata section base address
    private uint _idataSize;       // .idata section size

    private UnpackPhase _phase = UnpackPhase.Idle;
    private List<uint> _memBpHandles = new();
    private List<uint> _hwBpHandles = new();       // HW execution BPs on API functions
    private Dictionary<ulong, string> _apiBreakpoints = new(); // addr → API name
    private int _memBpHitCount;
    private int _apiHitCount;     // Phase 3 API hit counter
    private byte[]? _encryptedTextSnapshot; // used by legacy WaitingForApiCall phase
    private volatile bool _textDecrypted = false; // used by legacy WaitingForApiCall phase
    private ulong _discoveredOep;
    private ulong _unpackedPeBase;
    private int _stolenBytesSize;
    private byte[]? _restoredStolenBytes;
    private HashSet<ulong> _knownModuleBases = new();
    private List<SectionEntry> _sections = new();

    private record SectionEntry(string Name, uint Rva, uint VirtualSize, uint Characteristics);

    // PAGE_NOACCESS guard state
    private uint _guardOldProtection;  // original .text protection (to restore on OEP)
    private int _guardHitCount;        // total AV hits during guarding
    private ulong _firstTextExecAddr;  // first execute-access address in .text (Magicmida uses this)

    // IAT fix results
    private List<IatFixEntry> _iatFixes = new();
    private record IatFixEntry(ulong IatSlotAddress, ulong OriginalValue, ulong ResolvedApi, string DllName, string ApiName);

    // Settings
    public CheckBox ChkAutoUnpack { get; }
    public CheckBox ChkRestoreStolenBytes { get; }
    public CheckBox ChkFixIat { get; }
    public CheckBox ChkAutoFixDump { get; }

    private TextBlock _statusText;

    public ThemidaPanel(IDebuggerApi api)
    {
        _api = api;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;

        var root = new StackPanel { Margin = new Thickness(8) };
        var white = Brushes.White;

        root.Children.Add(new TextBlock
        {
            Text = "Themida / WinLicense Unpacker v4 (x86/x64)",
            FontSize = 16, FontWeight = FontWeights.Bold,
            Foreground = white, Margin = new Thickness(0, 0, 0, 10)
        });

        ChkAutoUnpack = MakeCheckBox("Auto-unpack on Run (F9)", true,
            "Automatically start unpacking when you press Run", white);
        ChkRestoreStolenBytes = MakeCheckBox("Restore stolen bytes", true,
            "Detect and restore stolen OEP bytes", white);
        ChkFixIat = MakeCheckBox("Auto-fix IAT", true,
            "Resolve Themida API wrappers to real addresses", white);
        ChkAutoFixDump = MakeCheckBox("Auto-fix dump on save", true,
            "Apply all fixes to the dumped PE", white);
        root.Children.Add(MakeGroup("Settings",
            [ChkAutoUnpack, ChkRestoreStolenBytes, ChkFixIat, ChkAutoFixDump], white));

        _statusText = new TextBlock
        {
            Text = "Status: Idle — use 'Detect Protector' first",
            Foreground = Brushes.LightGreen,
            FontFamily = new FontFamily("Consolas"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(4)
        };
        root.Children.Add(MakeGroup("Status", [_statusText], white));

        var btnPanel = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        AddButton(btnPanel, "Detect Protector", "Scan PE for Themida/WinLicense signatures", () => DetectProtector());
        AddButton(btnPanel, "Start Unpacking", "Arm breakpoints and start unpacking", () => StartUnpacking());
        AddButton(btnPanel, "Stop", "Cancel unpacking", () => StopUnpacking());
        AddButton(btnPanel, "Fix IAT", "Resolve Themida API wrappers", () => FixIat());
        AddButton(btnPanel, "Dump PE", "Dump with all fixes", () => DumpUnpackedPe());
        root.Children.Add(btnPanel);

        root.Children.Add(new TextBlock
        {
            Text = "Workflow: Detect → Anti-debug (other tab) → Start Unpacking → F9 → Fix IAT → Dump PE\n" +
                   "Uses stealth Memory BP (PAGE_GUARD) — no DR registers, no INT3 patching.",
            FontStyle = FontStyles.Italic, Foreground = white,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0)
        });

        Content = root;
        api.OnBeforeRun += OnBeforeRun;
        api.OnDebugEventFilter += OnDebugEventFilter;
    }

    private void SetStatus(string text)
    {
        try { Application.Current?.Dispatcher.BeginInvoke(() => _statusText.Text = text); }
        catch { }
    }

    // ════════════════════════════════════════════════════════════════════
    //  Detection — adapted for real Themida structure from IDA analysis
    // ════════════════════════════════════════════════════════════════════

    public void DetectProtector()
    {
        if (!_api.IsConnected || !_api.IsBreakState)
        { _api.Log.Warning("Must be connected and in Break state."); return; }

        uint pid = _api.TargetPid;
        if (pid == 0) { _api.Log.Warning("No target process."); return; }

        var modules = _api.Symbols.GetModules();
        if (modules.Count == 0) { _api.Log.Warning("No modules loaded."); return; }

        var mainModule = modules[0];
        _originalImageBase = mainModule.BaseAddress;
        _originalImageSize = mainModule.Size;

        _api.Log.Info($"Scanning {mainModule.Name} at 0x{_originalImageBase:X} (size 0x{_originalImageSize:X})...");

        var dosHdr = _api.Memory.ReadMemory(pid, _originalImageBase, 0x1000);
        if (dosHdr == null || dosHdr.Length < 0x40 || dosHdr[0] != 'M' || dosHdr[1] != 'Z')
        { _api.Log.Error("Invalid PE header."); return; }

        uint lfanew = BitConverter.ToUInt32(dosHdr, 0x3C);
        if (lfanew + 0x18 > (uint)dosHdr.Length || dosHdr[lfanew] != 'P' || dosHdr[lfanew + 1] != 'E')
        { _api.Log.Error("Invalid PE."); return; }

        ushort numSections = BitConverter.ToUInt16(dosHdr, (int)lfanew + 6);
        ushort optSize = BitConverter.ToUInt16(dosHdr, (int)lfanew + 0x14);
        ushort magic = BitConverter.ToUInt16(dosHdr, (int)lfanew + 0x18);
        _is64 = magic == 0x20B; // PE32+ = x64, PE32 (0x10B) = x86
        _ptrSize = _is64 ? 8 : 4;
        _majorLinkerVersion = dosHdr[(int)lfanew + 0x18 + 2]; // MajorLinkerVersion
        _originalEntryPointRva = BitConverter.ToUInt32(dosHdr, (int)lfanew + 0x18 + 0x10);

        // BaseOfData (PE32 only, at offset 24 in optional header)
        uint baseOfDataRva = 0;
        if (!_is64 && optSize >= 28)
            baseOfDataRva = BitConverter.ToUInt32(dosHdr, (int)lfanew + 0x18 + 24);

        int sectStart = (int)lfanew + 4 + 20 + optSize;

        _isThemida = false;
        _isWinLicense = false;
        _themidaSectionBase = 0;
        _bootSectionBase = 0;
        _originalTextBase = 0;
        _baseOfData = 0;
        _bootJmpRaxAddr = 0;
                _sections.Clear();

        _api.Log.Info($"PE type: {(_is64 ? "PE32+ (x64)" : "PE32 (x86)")}, Linker {_majorLinkerVersion}.x");

        var sb = new StringBuilder();
        sb.AppendLine($"Sections ({numSections}):");

        for (int i = 0; i < numSections; i++)
        {
            int off = sectStart + i * 40;
            if (off + 40 > dosHdr.Length) break;

            string name = Encoding.ASCII.GetString(dosHdr, off, 8).TrimEnd('\0');
            uint rva = BitConverter.ToUInt32(dosHdr, off + 12);
            uint vsz = BitConverter.ToUInt32(dosHdr, off + 8);
            uint chars = BitConverter.ToUInt32(dosHdr, off + 36);

            _sections.Add(new SectionEntry(name, rva, vsz, chars));

            string permStr = ((chars & 0x20000000) != 0 ? "X" : "") +
                            ((chars & 0x40000000) != 0 ? "R" : "") +
                            ((chars & 0x80000000) != 0 ? "W" : "");
            sb.AppendLine($"  [{i}] {name,-10} RVA=0x{rva:X8} Size=0x{vsz:X8} {permStr}");

            string nameLower = name.ToLowerInvariant();

            // Detect Themida/WinLicense sections
            if (nameLower is ".themida" or "themida")
            {
                _isThemida = true;
                _themidaSectionBase = _originalImageBase + rva;
                _themidaSectionSize = vsz;
            }
            else if (nameLower is ".winlice" or "winlice")
            {
                _isWinLicense = true;
                _isThemida = true;
                _themidaSectionBase = _originalImageBase + rva;
                _themidaSectionSize = vsz;
            }
            else if (nameLower is ".boot" or "boot")
            {
                _bootSectionBase = _originalImageBase + rva;
                _bootSectionSize = vsz;
                _isThemida = true; // .boot is a Themida indicator
            }
            else if (nameLower == ".idata")
            {
                _idataBase = _originalImageBase + rva;
                _idataSize = vsz;
            }

            // Detect .text section:
            // Themida renames sections to "________", so detect by:
            // 1. First CODE section with Execute permission
            // 2. That is NOT .themida, .boot, or .idata
            if (_originalTextBase == 0 &&
                (chars & 0x20000000) != 0 && // IMAGE_SCN_MEM_EXECUTE
                nameLower is not (".themida" or "themida" or ".winlice" or "winlice" or ".boot" or "boot"))
            {
                _originalTextBase = _originalImageBase + rva;
                _originalTextSize = vsz;
            }
        }

        _api.Log.Info(sb.ToString());

        // Calculate BaseOfData = first non-executable section after .text
        // This marks the end of code, used for guard range (like Magicmida)
        if (_originalTextBase != 0)
        {
            if (baseOfDataRva != 0)
                _baseOfData = _originalImageBase + baseOfDataRva;
            else
            {
                // For PE32+: find first non-code section after .text
                foreach (var s in _sections)
                {
                    ulong sAddr = _originalImageBase + s.Rva;
                    if (sAddr > _originalTextBase && (s.Characteristics & 0x20000000) == 0)
                    {
                        _baseOfData = sAddr;
                        break;
                    }
                }
                if (_baseOfData == 0)
                    _baseOfData = _originalTextBase + _originalTextSize;
            }
        }

        // Heuristic: EP in large RWX last section
        if (!_isThemida && _sections.Count >= 2)
        {
            var lastSect = _sections[^1];
            bool epInLast = _originalEntryPointRva >= lastSect.Rva &&
                            _originalEntryPointRva < lastSect.Rva + lastSect.VirtualSize;
            bool lastIsRwx = (lastSect.Characteristics & 0xE0000000) == 0xE0000000;
            if (epInLast && lastIsRwx && lastSect.VirtualSize > 0x100000)
            {
                _isThemida = true;
                _themidaSectionBase = _originalImageBase + lastSect.Rva;
                _themidaSectionSize = lastSect.VirtualSize;
            }
        }

        // Watermark scan
        if (!_isThemida)
        {
            var overlay = _api.Memory.ReadMemory(pid, _originalImageBase + 0x200, 0x200);
            if (overlay != null)
            {
                string s = Encoding.ASCII.GetString(overlay);
                if (s.Contains("Themida") || s.Contains("WinLicense") || s.Contains("Oreans"))
                    _isThemida = true;
            }
        }

        // Find "jmp rax" (FF E0) in .boot section for Method 2
        if (_bootSectionBase != 0)
        {
            FindBootJmpRax(pid);
        }

        if (_isThemida)
        {
            string variant = _isWinLicense ? "WinLicense" : "Themida";
            string arch = _is64 ? "x64" : "x86";
            _api.Log.Warning($"★ {variant} protection detected! ({arch})");
            _api.Log.Info($"  .text:    0x{_originalTextBase:X} (0x{_originalTextSize:X})");
            if (_themidaSectionBase != 0)
                _api.Log.Info($"  .themida: 0x{_themidaSectionBase:X} (0x{_themidaSectionSize:X})");
            if (_bootSectionBase != 0)
                _api.Log.Info($"  .boot:    0x{_bootSectionBase:X} (0x{_bootSectionSize:X})");
            if (_bootJmpRaxAddr != 0)
                _api.Log.Info($"  .boot jmp rax: 0x{_bootJmpRaxAddr:X} (Method 2 target)");
            _api.Log.Info($"  EP RVA:   0x{_originalEntryPointRva:X} (in {(_originalEntryPointRva >= (_bootSectionBase - _originalImageBase) ? ".boot" : ".text")})");

            var status = $"Detected: {variant}\n" +
                        $".text: 0x{_originalTextBase:X} ({_originalTextSize / 1024}KB)\n";
            if (_bootJmpRaxAddr != 0)
                status += $".boot jmp rax: 0x{_bootJmpRaxAddr:X}\n";
            status += $"EP RVA: 0x{_originalEntryPointRva:X}";
            SetStatus(status);
        }
        else
        {
            _api.Log.Info("No Themida/WinLicense detected.");
            SetStatus("Status: No Themida detected");
        }
    }

    /// <summary>
    /// Scan .boot section for "jmp rax/eax" (FF E0) pattern.
    /// In Themida, this is the final instruction that transfers control
    /// from the import resolver/decompressor to the VM or OEP.
    /// x64: pop rbp; pop rdi; pop rsi; pop rdx; pop rcx; pop rbx; jmp rax
    /// x86: pop ebp; pop edi; pop esi; pop edx; pop ecx; pop ebx; jmp eax
    /// (Same bytes — 5D 5F 5E 5A 59 5B FF E0)
    /// </summary>
    private void FindBootJmpRax(uint pid)
    {
        ulong epAddr = _originalImageBase + _originalEntryPointRva;
        ulong searchStart = epAddr > 0x1000 ? epAddr - 0x1000 : _bootSectionBase;
        uint searchSize = 0x2000;
        if (searchStart + searchSize > _bootSectionBase + _bootSectionSize)
            searchSize = (uint)(_bootSectionBase + _bootSectionSize - searchStart);

        var code = _api.Memory.ReadMemory(pid, searchStart, searchSize);
        if (code == null) return;

        // pop rbp/ebp(5D) pop rdi/edi(5F) pop rsi/esi(5E) pop rdx/edx(5A) pop rcx/ecx(59) pop rbx/ebx(5B) jmp rax/eax(FF E0)
        byte[] pattern = [0x5D, 0x5F, 0x5E, 0x5A, 0x59, 0x5B, 0xFF, 0xE0];

        for (int i = 0; i <= code.Length - pattern.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < pattern.Length; j++)
                if (code[i + j] != pattern[j]) { match = false; break; }
            if (match)
            {
                _bootJmpRaxAddr = searchStart + (ulong)i + 6;
                string reg = _is64 ? "rax" : "eax";
                _api.Log.Info($"[Detect] Found .boot resolver epilogue, jmp {reg} at 0x{_bootJmpRaxAddr:X}");
                return;
            }
        }

        // Fallback: find any FF E0
        for (int i = 0; i <= code.Length - 2; i++)
        {
            if (code[i] == 0xFF && code[i + 1] == 0xE0)
            {
                _bootJmpRaxAddr = searchStart + (ulong)i;
                _api.Log.Info($"[Detect] Found jmp {(_is64 ? "rax" : "eax")} at 0x{_bootJmpRaxAddr:X} (generic)");
                return;
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  Unpacking — two methods
    // ════════════════════════════════════════════════════════════════════

    public void StartUnpacking()
    {
        if (!_api.IsConnected || !_api.IsBreakState)
        { _api.Log.Warning("Must be connected and in Break state."); return; }

        if (_originalTextBase == 0)
        {
            DetectProtector();
            if (_originalTextBase == 0) return;
        }

        _knownModuleBases.Clear();
        foreach (var m in _api.Symbols.GetModules())
            _knownModuleBases.Add(m.BaseAddress);

        // Strategy: PAGE_NOACCESS on .text section (Magicmida approach).
        // Any access to .text causes ACCESS_VIOLATION.
        // ExceptionInformation[0] tells us: 0=read, 1=write, 8=execute.
        // Read/write = Themida unpacking → single-step past, re-arm guard.
        // Execute = code running in .text = OEP found!
        uint pid = _api.TargetPid;
        _guardHitCount = 0;
        _firstTextExecAddr = 0;

        // Set PAGE_NOACCESS on .text section
        var (ok, oldProt) = _api.Memory.ProtectMemory(pid, _originalTextBase, _originalTextSize, 0x01 /* PAGE_NOACCESS */);
        if (!ok)
        {
            _api.Log.Error("[Unpack] Failed to set PAGE_NOACCESS on .text!");
            SetStatus("Failed to set PAGE_NOACCESS");
            return;
        }

        _guardOldProtection = oldProt;
        _phase = UnpackPhase.TextGuarded;
        _api.Log.Info($"[Unpack] PAGE_NOACCESS set on .text (0x{_originalTextBase:X}, 0x{_originalTextSize:X} bytes)");
        _api.Log.Info($"[Unpack] Old protection: 0x{oldProt:X}. Press Run (F9).");
        _api.Log.Info("[Unpack] Will detect OEP via execute-access AV (AccessType=8).");
        SetStatus("PAGE_NOACCESS on .text\nPress Run (F9)");
    }

    public void StopUnpacking()
    {
        // Restore .text protection if we were guarding
        if ((_phase == UnpackPhase.TextGuarded || _phase == UnpackPhase.TextStepRearm) &&
            _guardOldProtection != 0 && _originalTextBase != 0)
        {
            _api.Memory.ProtectMemory(_api.TargetPid, _originalTextBase, _originalTextSize, _guardOldProtection);
            _api.Log.Info("[Unpack] Restored .text protection.");
        }

        CleanupAllBps();
        _phase = UnpackPhase.Idle;

        _api.Log.Info("Unpacker stopped.");
        SetStatus("Status: Stopped");
    }

    // ── Poll-based OEP detection ──
    // After Run, poll .text bytes every 500ms. When they change from encrypted
    // to valid code, pause the process and scan for CRT startup OEP pattern.

    private void PollForDecryptedText()
    {
        // Background thread: poll .text bytes to detect when decryption finishes.
        // Only sets _textDecrypted flag — the event filter uses this to decide when to break.
        uint pid = _api.TargetPid;

        for (int attempt = 0; attempt < 120; attempt++) // max 60 seconds
        {
            if (_phase == UnpackPhase.Idle || _phase == UnpackPhase.Done)
                return;

            var current = _api.Memory.ReadMemory(pid, _originalTextBase, 16);
            if (current != null && _encryptedTextSnapshot != null &&
                !current.SequenceEqual(_encryptedTextSnapshot))
            {
                _textDecrypted = true;
                _api.Log.Info("[Unpack] .text decryption detected! API BPs will now catch program start.");
                SetStatus("Decrypted!\nWaiting for API hit...");
                return;
            }

            Thread.Sleep(500);
        }

        _api.Log.Warning("[Unpack] Timeout: .text not decrypted after 60s.");
        SetStatus("Timeout: .text unchanged");
    }

    private void SetSwBpsOnApis(uint pid)
    {
        CleanupAllBps();
        _apiBreakpoints.Clear();

        // Only CRT-specific APIs that Themida VM does NOT call during unpacking.
        // Themida calls GetModuleHandleA/GetProcAddress/LoadLibraryA heavily during IAT resolution.
        // But GetCommandLineW, GetStartupInfoW are only called by the CRT after unpacking.
        string[] targetApis = [
            "GetCommandLineA", "GetCommandLineW",
            "GetStartupInfoW",
        ];

        // Find kernel32.dll base
        var modules = _api.Symbols.GetModules();
        var kernel32 = modules.FirstOrDefault(m =>
            m.Name.Contains("kernel32", StringComparison.OrdinalIgnoreCase));
        if (kernel32 == null) { _api.Log.Error("[Unpack] kernel32.dll not found!"); return; }

        var foundApis = ResolveExports(pid, kernel32.BaseAddress, targetApis);

        // Also try ntdll for very early APIs
        var ntdll = modules.FirstOrDefault(m =>
            m.Name.Contains("ntdll", StringComparison.OrdinalIgnoreCase));
        if (ntdll != null)
        {
            var ntdllApis = ResolveExports(pid, ntdll.BaseAddress,
                ["RtlInitUnicodeString", "NtQueryInformationProcess"]);
            foundApis.AddRange(ntdllApis);
        }

        // Set SW BPs (INT3) on each API — these are in system DLLs, outside Themida CRC
        foreach (var (name, addr) in foundApis)
        {
            var h = _api.Breakpoints.SetBreakpoint(pid, 0, addr, PluginBreakpointType.Software);
            if (h.HasValue)
            {
                _memBpHandles.Add(h.Value);
                _apiBreakpoints[addr] = name;
            }
        }

        _api.Log.Info($"[Unpack] Set {_apiBreakpoints.Count} SW BPs on APIs: {string.Join(", ", _apiBreakpoints.Values)}");
    }

    private List<(string name, ulong addr)> ResolveExports(uint pid, ulong dllBase, string[] targetNames)
    {
        var result = new List<(string name, ulong addr)>();
        var dosHdr = _api.Memory.ReadMemory(pid, dllBase, 0x40);
        if (dosHdr == null) return result;
        uint lfanew = BitConverter.ToUInt32(dosHdr, 0x3C);

        var peHdr = _api.Memory.ReadMemory(pid, dllBase + lfanew, 0x120);
        if (peHdr == null) return result;

        uint exportRva = BitConverter.ToUInt32(peHdr, 24 + 112);
        if (exportRva == 0) return result;

        var exportDir = _api.Memory.ReadMemory(pid, dllBase + exportRva, 40);
        if (exportDir == null) return result;

        uint numNames = BitConverter.ToUInt32(exportDir, 24);
        uint namesRva = BitConverter.ToUInt32(exportDir, 32);
        uint ordinalsRva = BitConverter.ToUInt32(exportDir, 36);
        uint functionsRva = BitConverter.ToUInt32(exportDir, 28);

        var namePointers = _api.Memory.ReadMemory(pid, dllBase + namesRva, numNames * 4);
        var ordinals = _api.Memory.ReadMemory(pid, dllBase + ordinalsRva, numNames * 2);
        var functions = _api.Memory.ReadMemory(pid, dllBase + functionsRva, numNames * 4);
        if (namePointers == null || ordinals == null || functions == null) return result;

        // Bulk read all name strings
        uint minNameRva = uint.MaxValue, maxNameRva = 0;
        for (uint i = 0; i < numNames; i++)
        {
            uint rva = BitConverter.ToUInt32(namePointers, (int)(i * 4));
            if (rva < minNameRva) minNameRva = rva;
            if (rva > maxNameRva) maxNameRva = rva;
        }
        uint nameBlockSize = maxNameRva - minNameRva + 64;
        if (nameBlockSize > 0x100000) nameBlockSize = 0x100000;
        var nameBlock = _api.Memory.ReadMemory(pid, dllBase + minNameRva, nameBlockSize);
        if (nameBlock == null) return result;

        var targetSet = new HashSet<string>(targetNames);
        for (uint i = 0; i < numNames; i++)
        {
            uint nameRva = BitConverter.ToUInt32(namePointers, (int)(i * 4));
            uint offset = nameRva - minNameRva;
            if (offset >= nameBlock.Length) continue;

            int end = (int)offset;
            while (end < nameBlock.Length && nameBlock[end] != 0) end++;
            string funcName = System.Text.Encoding.ASCII.GetString(nameBlock, (int)offset, end - (int)offset);

            if (targetSet.Contains(funcName))
            {
                ushort ordinal = BitConverter.ToUInt16(ordinals, (int)(i * 2));
                uint funcRva = BitConverter.ToUInt32(functions, ordinal * 4);
                result.Add((funcName, dllBase + funcRva));
                targetSet.Remove(funcName);
                if (targetSet.Count == 0) break;
            }
        }

        return result;
    }

    // ── Legacy phases (kept for compatibility) ──
    //   3. WaitingForApiCall: HW BPs on APIs → check return addr on stack

    private void BeginMemoryBpMethod()
    {
        uint pid = _api.TargetPid;

        // Set PAGE_GUARD on the first page of .text section
        var h = _api.Breakpoints.SetBreakpoint(pid, 0, _originalTextBase, PluginBreakpointType.Memory);
        if (!h.HasValue)
        { _api.Log.Error("Failed to set Memory BP on .text section."); return; }

        _memBpHandles.Clear();
        _memBpHandles.Add(h.Value);
        _memBpHitCount = 0;
        _phase = UnpackPhase.DecompDetecting;

        _api.Log.Info($"[Stealth] Phase 1: PAGE_GUARD on .text page 0 at 0x{_originalTextBase:X}");
        _api.Log.Info($"[Stealth] Watching .text: 0x{_originalTextBase:X}-0x{_originalTextBase + _originalTextSize:X}");
        _api.Log.Info("[Stealth] No DR registers, no INT3 — invisible to anti-debug");
        SetStatus("Stealth: PAGE_GUARD on .text\nPhase 1: detecting decompressor...");
    }

    private void SetGuardOnLastPage(uint pid)
    {
        // Calculate address of last page of .text
        ulong lastPageAddr = (_originalTextBase + _originalTextSize - 1) & ~0xFFFUL;
        if (lastPageAddr < _originalTextBase) lastPageAddr = _originalTextBase;

        var h = _api.Breakpoints.SetBreakpoint(pid, 0, lastPageAddr, PluginBreakpointType.Memory);
        if (h.HasValue)
        {
            _memBpHandles.Add(h.Value);
            _api.Log.Info($"[Stealth] Phase 2: guard on last .text page at 0x{lastPageAddr:X}");
            _api.Log.Info("[Stealth] Decompressor runs freely now — waiting for it to reach the end...");
        }
        else
        {
            _api.Log.Warning("[Stealth] Failed to set guard on last page — falling back to re-arm");
        }
    }

    private void SetHwBpsOnApis(uint pid)
    {
        CleanupAllBps();
        _hwBpHandles.Clear();
        _apiBreakpoints.Clear();
        _apiHitCount = 0;

        // Resolve common API addresses using the symbol engine.
        string[] apiNames = [
            "kernel32!GetModuleHandleA",
            "kernel32!GetModuleHandleW",
            "kernel32!GetProcAddress",
            "kernel32!LoadLibraryA",
        ];

        // Resolve addresses first
        var apiAddrs = new List<(string name, ulong addr)>();
        foreach (var name in apiNames)
        {
            ulong addr = _api.Symbols.ResolveNameToAddress(name);
            if (addr != 0)
                apiAddrs.Add((name, addr));
        }

        if (apiAddrs.Count == 0)
        {
            _api.Log.Warning("[Stealth] Symbol resolution failed, trying manual export scan...");
            TrySetFallbackApiBp(pid);
            return;
        }

        // HW BPs are per-thread (DR0-DR3 in trap frame).
        // Set on ALL threads so we catch API calls from any thread.
        var threads = _api.Process.EnumThreads(pid);
        int totalSet = 0;

        foreach (var thread in threads)
        {
            int perThread = 0;
            foreach (var (name, addr) in apiAddrs)
            {
                if (perThread >= 4) break; // Only 4 DR slots per thread
                var h = _api.Breakpoints.SetBreakpoint(pid, thread.ThreadId, addr, PluginBreakpointType.Hardware, 1);
                if (h.HasValue)
                {
                    _hwBpHandles.Add(h.Value);
                    _apiBreakpoints[addr] = name;
                    totalSet++;
                    perThread++;
                }
            }
            if (perThread > 0)
                _api.Log.Info($"[Stealth] Phase 3: {perThread} HW BPs set on TID {thread.ThreadId}");
        }

        if (totalSet == 0)
        {
            _api.Log.Warning("[Stealth] Failed to set HW BPs via symbols, trying manual resolution...");
            TrySetFallbackApiBp(pid);
        }
        else
        {
            _api.Log.Info($"[Stealth] Phase 3: {totalSet} HW BPs across {threads.Count} threads");
            _api.Log.Info("[Stealth] When unpacked code calls any API → return addr in .text → OEP found");
        }
    }

    private void TrySetFallbackApiBp(uint pid)
    {
        // Find kernel32.dll base from loaded modules
        var modules = _api.Symbols.GetModules();
        var kernel32 = modules.FirstOrDefault(m =>
            m.Name.Contains("kernel32", StringComparison.OrdinalIgnoreCase));
        if (kernel32 == null)
        {
            _api.Log.Error("[Stealth] Cannot find kernel32.dll in loaded modules!");
            return;
        }

        // Read PE exports to find GetModuleHandleA
        ulong k32base = kernel32.BaseAddress;
        var dosHdr = _api.Memory.ReadMemory(pid, k32base, 0x40);
        if (dosHdr == null) return;
        uint lfanew = BitConverter.ToUInt32(dosHdr, 0x3C);

        var peHdr = _api.Memory.ReadMemory(pid, k32base + lfanew, 0x120);
        if (peHdr == null) return;

        // Export directory RVA is at offset 0x88 in PE64 optional header (offset 24+112=136)
        uint exportRva = BitConverter.ToUInt32(peHdr, 24 + 112);
        if (exportRva == 0) return;

        var exportDir = _api.Memory.ReadMemory(pid, k32base + exportRva, 40);
        if (exportDir == null) return;

        uint numNames = BitConverter.ToUInt32(exportDir, 24);
        uint namesRva = BitConverter.ToUInt32(exportDir, 32);
        uint ordinalsRva = BitConverter.ToUInt32(exportDir, 36);
        uint functionsRva = BitConverter.ToUInt32(exportDir, 28);

        var namePointers = _api.Memory.ReadMemory(pid, k32base + namesRva, numNames * 4);
        var ordinals = _api.Memory.ReadMemory(pid, k32base + ordinalsRva, numNames * 2);
        var functions = _api.Memory.ReadMemory(pid, k32base + functionsRva, numNames * 4);
        if (namePointers == null || ordinals == null || functions == null) return;

        // Read ALL name strings in one bulk read (find min/max RVA, read entire range)
        uint minNameRva = uint.MaxValue, maxNameRva = 0;
        for (uint i = 0; i < numNames; i++)
        {
            uint rva = BitConverter.ToUInt32(namePointers, (int)(i * 4));
            if (rva < minNameRva) minNameRva = rva;
            if (rva > maxNameRva) maxNameRva = rva;
        }
        // Read from minRVA to maxRVA + 64 bytes for last name
        uint nameBlockSize = maxNameRva - minNameRva + 64;
        if (nameBlockSize > 0x100000) nameBlockSize = 0x100000; // cap at 1MB
        var nameBlock = _api.Memory.ReadMemory(pid, k32base + minNameRva, nameBlockSize);
        if (nameBlock == null) return;

        var foundApis = new List<(string name, ulong addr)>();
        for (uint i = 0; i < numNames && foundApis.Count < 4; i++)
        {
            uint nameRva = BitConverter.ToUInt32(namePointers, (int)(i * 4));
            uint offset = nameRva - minNameRva;
            if (offset >= nameBlock.Length) continue;

            // Extract null-terminated string from bulk buffer
            int end = (int)offset;
            while (end < nameBlock.Length && nameBlock[end] != 0) end++;
            string funcName = System.Text.Encoding.ASCII.GetString(nameBlock, (int)offset, end - (int)offset);

            if (funcName is "GetModuleHandleA" or "GetProcAddress" or "LoadLibraryA")
            {
                ushort ordinal = BitConverter.ToUInt16(ordinals, (int)(i * 2));
                uint funcRva = BitConverter.ToUInt32(functions, ordinal * 4);
                foundApis.Add((funcName, k32base + funcRva));
            }
        }

        // Set HW BPs on all threads
        var threads = _api.Process.EnumThreads(pid);
        foreach (var thread in threads)
        {
            int perThread = 0;
            foreach (var (name, addr) in foundApis)
            {
                if (perThread >= 4) break;
                var h = _api.Breakpoints.SetBreakpoint(pid, thread.ThreadId, addr, PluginBreakpointType.Hardware, 1);
                if (h.HasValue)
                {
                    _hwBpHandles.Add(h.Value);
                    _apiBreakpoints[addr] = name;
                    perThread++;
                }
            }
        }

        if (_hwBpHandles.Count > 0)
            _api.Log.Info($"[Stealth] Phase 3: {_hwBpHandles.Count} HW BP(s) set via manual export scan");
        else
            _api.Log.Error("[Stealth] Phase 3: FAILED to set any API breakpoints!");
    }

    // ── Common: when we find the OEP ──

    private void OnOepFound(uint pid, ulong oepAddr)
    {
        CleanupAllBps();

        _discoveredOep = oepAddr;
        _unpackedPeBase = _originalImageBase;

        _api.Log.Warning($"[Themida] ★ Execution entered .text at 0x{oepAddr:X}!");

        // Read code at OEP
        var code = _api.Memory.ReadMemory(pid, oepAddr, 64);

        // Check for virtualized OEP: JMP rel32 into .themida section (Magicmida approach)
        if (code != null && code.Length >= 5 && code[0] == 0xE9)
        {
            int disp = BitConverter.ToInt32(code, 1);
            ulong jmpTarget = oepAddr + 5 + (ulong)(long)disp;
            if (_themidaSectionBase != 0 && jmpTarget >= _themidaSectionBase &&
                jmpTarget < _themidaSectionBase + _themidaSectionSize)
            {
                _api.Log.Warning($"[Themida] OEP is VIRTUALIZED: jmp 0x{jmpTarget:X} (into .themida VM)");

                // Try to find the real OEP using MSVC pattern (Magicmida: E8 call + E9 jmp)
                ulong realOep = TryFindMsvcOep(pid, oepAddr);
                if (realOep != 0 && realOep != oepAddr)
                {
                    _api.Log.Warning($"[Themida] Found real MSVC OEP at 0x{realOep:X}");
                    _discoveredOep = realOep;
                    oepAddr = realOep;
                    code = _api.Memory.ReadMemory(pid, oepAddr, 64);
                }
            }
        }

        // Check return address — if it points into .themida, OEP may be stolen
        var regs = _api.Memory.ReadRegisters(pid, _api.SelectedThreadId);
        ulong rsp = GetReg(regs, _is64 ? "RSP" : "ESP");
        if (rsp != 0)
        {
            var retData = _api.Memory.ReadMemory(pid, rsp, (uint)_ptrSize);
            if (retData != null)
            {
                ulong retAddr = _is64 ? BitConverter.ToUInt64(retData) : BitConverter.ToUInt32(retData);
                if (_themidaSectionBase != 0 && retAddr >= _themidaSectionBase &&
                    retAddr < _themidaSectionBase + _themidaSectionSize)
                {
                    _api.Log.Warning($"[Themida] Return address 0x{retAddr:X} is in .themida — stolen bytes likely");
                }
            }
        }

        // Stolen bytes
        if (ChkRestoreStolenBytes.IsChecked == true && code != null)
        {
            _stolenBytesSize = DetectAndRestoreStolenBytes(pid, oepAddr, code);
        }

        // Auto IAT fix
        if (ChkFixIat.IsChecked == true)
        {
            _api.Log.Info("[Themida] Auto-fixing IAT...");
            FixIatInternal(pid);
        }

        _phase = UnpackPhase.Done;

        string status = $"★ UNPACKED!\nOEP: 0x{_discoveredOep:X}\nPE Base: 0x{_unpackedPeBase:X}";
        if (_stolenBytesSize > 0) status += $"\nStolen bytes: {_stolenBytesSize} restored";
        else if (_stolenBytesSize < 0) status += "\nStolen bytes: entry virtualized";
        if (_iatFixes.Count > 0) status += $"\nIAT: {_iatFixes.Count} imports fixed";
        SetStatus(status);

        _api.Log.Warning($"[Themida] ★ OEP = 0x{_discoveredOep:X}");
        if (_iatFixes.Count > 0)
            _api.Log.Warning($"[Themida] ★ IAT: {_iatFixes.Count} imports resolved");
        _api.Log.Info("[Themida] Use 'Dump PE' to save the fully fixed binary.");

        _api.UI.AddUnpackedModule(_unpackedPeBase, "unpacked.exe");
        _api.UI.RefreshModulesAndSections();
    }

    // ════════════════════════════════════════════════════════════════════
    //  Debug event filter — background thread
    // ════════════════════════════════════════════════════════════════════

    private void OnBeforeRun()
    {
        if (ChkAutoUnpack.IsChecked != true) return;
        if (!_api.IsBreakState || _api.TargetPid == 0) return;
        if (_phase != UnpackPhase.Idle) return;
        if (_originalTextBase != 0)
        {
            // Auto-start: set HW BPs on APIs immediately (process is stopped)
            StartUnpacking();
        }
    }

    private bool OnDebugEventFilter(PluginDebugEvent evt)
    {
        if (_phase == UnpackPhase.Idle || _phase == UnpackPhase.Done)
            return false;

        uint pid = evt.ProcessId;
        ulong rip = evt.Address;

        // ══════ PAGE_NOACCESS guard: handle AV on .text ══════
        if (_phase == UnpackPhase.TextGuarded &&
            evt.Type == PluginDebugEventType.AccessViolation)
        {
            ulong faultAddr = evt.FaultAddress;
            bool faultInText = faultAddr >= _originalTextBase &&
                               faultAddr < _originalTextBase + _originalTextSize;

            if (!faultInText)
                return false; // AV not on .text — let normal handler deal with it

            _guardHitCount++;
            uint accessType = evt.AccessType; // 0=read, 1=write, 8=execute

            if (accessType == 8) // EXECUTE — code is running in .text = OEP!
            {
                _api.Log.Warning($"[Unpack] ★ Execute access in .text at 0x{faultAddr:X}! ({_guardHitCount} guard hits)");
                _firstTextExecAddr = faultAddr;

                // Restore original .text protection so instruction can execute
                _api.Memory.ProtectMemory(pid, _originalTextBase, _originalTextSize, _guardOldProtection);

                // Suppress AV + set TF → on next SingleStep we'll break at OEP
                _phase = UnpackPhase.OepStepThrough;
                evt.ContinueMode = 3; // KF_CONTINUE_HANDLED
                return true; // Don't break yet — wait for SingleStep
            }

            // Read (0) or Write (1) — Themida VM is reading/writing .text (decryption)
            if (_guardHitCount <= 5 || _guardHitCount % 500 == 0)
            {
                string accessStr = accessType == 0 ? "read" : accessType == 1 ? "write" : $"type{accessType}";
                _api.Log.Info($"[Unpack] .text {accessStr} at 0x{faultAddr:X} from RIP=0x{rip:X} (#{_guardHitCount})");
            }

            if (_guardHitCount > 500000)
            {
                _api.Log.Warning("[Unpack] 500K guard hits — aborting.");
                _api.Memory.ProtectMemory(pid, _originalTextBase, _originalTextSize, _guardOldProtection);
                _phase = UnpackPhase.Idle;
                return false;
            }

            // Temporarily restore .text protection so instruction can execute
            _api.Memory.ProtectMemory(pid, _originalTextBase, _originalTextSize, _guardOldProtection);

            // Continue with HANDLED mode: suppresses AV + sets TF (single-step)
            // On next SingleStep event we'll re-arm PAGE_NOACCESS
            _phase = UnpackPhase.TextStepRearm;
            evt.ContinueMode = 3; // KF_CONTINUE_HANDLED
            return true; // Plugin handled — don't break in UI
        }

        // ══════ OEP step-through: AV was suppressed, now at first instruction ══════
        if (_phase == UnpackPhase.OepStepThrough &&
            evt.Type == PluginDebugEventType.SingleStep)
        {
            _api.Log.Warning($"[Unpack] ★ OEP reached at 0x{rip:X} (after execute AV at 0x{_firstTextExecAddr:X})");

            // Check if this is virtualized OEP
            var regs = _api.Memory.ReadRegisters(pid, evt.ThreadId);
            ulong rsp = GetReg(regs, _is64 ? "RSP" : "ESP");
            if (rsp != 0)
            {
                var retData = _api.Memory.ReadMemory(pid, rsp, (uint)_ptrSize);
                if (retData != null)
                {
                    ulong retAddr = _is64 ? BitConverter.ToUInt64(retData) : BitConverter.ToUInt32(retData);
                    if (_themidaSectionBase != 0 && retAddr >= _themidaSectionBase &&
                        retAddr < _themidaSectionBase + _themidaSectionSize)
                    {
                        _api.Log.Warning($"[Unpack] Return addr 0x{retAddr:X} is in .themida → OEP is virtualized");
                        ulong realOep = TryFindMsvcOep(pid, _firstTextExecAddr);
                        if (realOep != 0)
                        {
                            _api.Log.Warning($"[Unpack] Found real MSVC OEP at 0x{realOep:X}");
                            rip = realOep;
                        }
                    }
                }
            }

            OnOepFound(pid, rip);
            return false; // Break in UI at OEP
        }

        // ══════ Single-step re-arm: re-set PAGE_NOACCESS after stepping past ══════
        if (_phase == UnpackPhase.TextStepRearm &&
            evt.Type == PluginDebugEventType.SingleStep)
        {
            // Re-arm PAGE_NOACCESS on .text
            _api.Memory.ProtectMemory(pid, _originalTextBase, _originalTextSize, 0x01 /* PAGE_NOACCESS */);
            _phase = UnpackPhase.TextGuarded;

            // Check if RIP is now in .text (shouldn't happen normally, but just in case)
            if (rip >= _originalTextBase && rip < _originalTextBase + _originalTextSize)
            {
                _api.Log.Warning($"[Unpack] ★ RIP entered .text at 0x{rip:X} after step!");
                _api.Memory.ProtectMemory(pid, _originalTextBase, _originalTextSize, _guardOldProtection);
                OnOepFound(pid, rip);
                return false;
            }

            return true; // continue silently
        }

        // ══════ Legacy phases (DecompDetecting, WaitingForApiCall) ══════

        // Check if RIP is in .text — execution entered unpacked code = OEP!
        bool ripInText = rip >= _originalTextBase && rip < _originalTextBase + _originalTextSize;

        if (ripInText && _phase != UnpackPhase.TextGuarded && _phase != UnpackPhase.TextStepRearm)
        {
            _api.Log.Warning($"[Stealth] ★ Execution entered .text at 0x{rip:X}!");
            OnOepFound(pid, rip);
            return false;
        }

        if (_phase == UnpackPhase.DecompDetecting)
        {
            _memBpHitCount++;
            CleanupAllBps();
            var h = _api.Breakpoints.SetBreakpoint(pid, 0, _originalTextBase, PluginBreakpointType.Memory);
            if (h.HasValue) _memBpHandles.Add(h.Value);

            if (_memBpHitCount > 100000)
            {
                _api.Log.Warning("[Stealth] 100K guard hits without .text execution — aborting.");
                CleanupAllBps();
                _phase = UnpackPhase.Idle;
                return false;
            }
            return true;
        }

        if (_phase == UnpackPhase.WaitingForApiCall)
        {
            _apiHitCount++;
            bool isOurApiBp = _apiBreakpoints.ContainsKey(rip);
            if (!isOurApiBp) return false;

            string apiName = _apiBreakpoints[rip];
            if (!_textDecrypted)
            {
                if (_apiHitCount <= 3)
                    _api.Log.Info($"[Unpack] API hit during unpacking: {apiName} (continuing)");
                return true;
            }

            _api.Log.Warning($"[Unpack] ★ {apiName} called after .text decryption!");
            CleanupAllBps();
            OnOepFound(pid, rip);
            return false;
        }

        return false;
    }

    private static ulong GetReg(IReadOnlyList<PluginRegister> regs, string name)
    {
        return regs.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value ?? 0;
    }

    /// <summary>
    /// Magicmida approach: for MSVC binaries, scan .text for pattern:
    ///   E8 ?? ?? ?? ??    call __security_init_cookie
    ///   E9 ?? ?? ?? ??    jmp  __scrt_common_main_seh
    /// where the CALL target matches the first .text access address.
    /// </summary>
    private ulong TryFindMsvcOep(uint pid, ulong hitAddress)
    {
        if (_majorLinkerVersion is not (9 or 10 or 11 or 12 or 14)) return 0;

        uint textLen = _baseOfData != 0
            ? (uint)(_baseOfData - _originalTextBase)
            : _originalTextSize;
        if (textLen > 0x200000) textLen = 0x200000; // limit scan

        var textBuf = _api.Memory.ReadMemory(pid, _originalTextBase, textLen);
        if (textBuf == null) return 0;

        uint scanFor = (uint)(hitAddress - _originalTextBase);

        for (int i = 0; i + 10 <= textBuf.Length; i++)
        {
            if (textBuf[i] == 0xE8 && textBuf[i + 5] == 0xE9)
            {
                uint callDisp = BitConverter.ToUInt32(textBuf, i + 1);
                if (callDisp + (uint)i + 5 == scanFor)
                {
                    ulong oep = _originalTextBase + (ulong)i;
                    _api.Log.Info($"[MSVC] Found call+jmp at 0x{oep:X} (call → 0x{hitAddress:X})");
                    return oep;
                }
            }
        }

        return 0;
    }

    // ════════════════════════════════════════════════════════════════════
    //  Stolen bytes detection + restoration
    // ════════════════════════════════════════════════════════════════════

    private int DetectAndRestoreStolenBytes(uint pid, ulong oepAddr, byte[] codeAtOep)
    {
        if (codeAtOep.Length < 4) return 0;

        if (LooksLikeValidPrologue(codeAtOep))
        {
            _api.Log.Info("[Stolen] OEP looks like valid entry point — no stolen bytes.");
            return 0;
        }

        _api.Log.Warning("[Stolen] First bytes at OEP don't look like a prologue!");

        // Check: did we land at EP+N? (Themida executed first N bytes in VM)
        ulong realEpAddr = _originalImageBase + _originalEntryPointRva;

        // This check only makes sense if EP is in .text
        if (realEpAddr >= _originalTextBase && realEpAddr < _originalTextBase + _originalTextSize)
        {
            if (realEpAddr != oepAddr)
            {
                long offset = (long)(oepAddr - realEpAddr);
                if (offset > 0 && offset < 64)
                {
                    _api.Log.Info($"[Stolen] Landed at EP+0x{offset:X}, stolen = {offset} bytes");

                    byte[]? match = FindBestPrologue((int)offset);
                    if (match != null)
                    {
                        _restoredStolenBytes = match;
                        _discoveredOep = realEpAddr;

                        if (_api.Memory.WriteMemory(pid, realEpAddr, match))
                        {
                            _api.Log.Warning($"[Stolen] ★ Restored {match.Length} bytes at 0x{realEpAddr:X}: " +
                                            BitConverter.ToString(match).Replace("-", " "));
                            _api.Memory.WriteRip(pid, _api.SelectedThreadId, realEpAddr);
                            return match.Length;
                        }
                    }
                    else
                    {
                        _api.Log.Warning($"[Stolen] Cannot auto-match prologue for {offset} stolen bytes");
                    }
                    return (int)offset;
                }
            }
        }

        // Check if prologue starts at offset N
        int skipOff = FindPrologueOffset(codeAtOep);
        if (skipOff > 0)
        {
            _api.Log.Info($"[Stolen] Prologue at OEP+0x{skipOff:X} — {skipOff} bytes stolen");
            byte[]? match = FindBestPrologue(skipOff);
            if (match != null)
            {
                _restoredStolenBytes = match;
                if (_api.Memory.WriteMemory(pid, oepAddr, match))
                {
                    _api.Log.Warning($"[Stolen] ★ Restored {match.Length} bytes: " +
                                    BitConverter.ToString(match).Replace("-", " "));
                    return match.Length;
                }
            }
            return skipOff;
        }

        // JMP to VM — fully virtualized
        if (codeAtOep[0] == 0xE9 || codeAtOep[0] == 0xFF)
        {
            _api.Log.Warning("[Stolen] OEP starts with JMP — entry is fully virtualized");
            _restoredStolenBytes = _is64
                ? [0x48, 0x83, 0xEC, 0x28]   // x64: sub rsp, 0x28
                : [0x55, 0x8B, 0xEC];        // x86: push ebp; mov ebp, esp
            return -1;
        }

        return 0;
    }

    private static bool LooksLikeValidPrologue(byte[] c)
    {
        if (c.Length < 2) return false;
        // x64 prologues
        if (c.Length >= 3)
        {
            if (c[0] == 0x48 && c[1] == 0x83 && c[2] == 0xEC) return true; // sub rsp, imm8
            if (c[0] == 0x48 && c[1] == 0x81 && c[2] == 0xEC) return true; // sub rsp, imm32
            if (c[0] == 0x48 && c[1] == 0x89 && c[2] is 0x5C or 0x7C or 0x4C) return true; // mov [rsp+X], reg
            if (c[0] == 0x48 && c[1] == 0x8D && c[2] == 0x0D) return true; // lea rcx, [rip+X]
        }
        // x86 prologues
        if (c.Length >= 3)
        {
            if (c[0] == 0x55 && c[1] == 0x8B && c[2] == 0xEC) return true; // push ebp; mov ebp, esp
            if (c[0] == 0x55 && c[1] == 0x89 && c[2] == 0xE5) return true; // push ebp; mov ebp, esp (GCC)
            if (c[0] == 0x83 && c[1] == 0xEC) return true; // sub esp, imm8
            if (c[0] == 0x81 && c[1] == 0xEC) return true; // sub esp, imm32
        }
        // Common to both
        if (c[0] is 0x53 or 0x55 or 0x56 or 0x57) return true; // push rbx/ebx/rbp/ebp/rsi/esi/rdi/edi
        if (c[0] == 0xE8) return true; // call rel32
        if (c[0] == 0x6A) return true; // push imm8 (x86 common)
        if (c[0] == 0x68) return true; // push imm32 (x86 common)
        return false;
    }

    private static int FindPrologueOffset(byte[] code)
    {
        for (int i = 1; i < Math.Min(64, code.Length - 3); i++)
        {
            var slice = new byte[Math.Min(code.Length - i, 4)];
            Array.Copy(code, i, slice, 0, slice.Length);
            if (LooksLikeValidPrologue(slice)) return i;
        }
        return 0;
    }

    private byte[]? FindBestPrologue(int stolenSize)
    {
        byte[][] prologues64 =
        [
            [0x48, 0x83, 0xEC, 0x28],                         // sub rsp, 0x28
            [0x48, 0x83, 0xEC, 0x38],                         // sub rsp, 0x38
            [0x48, 0x83, 0xEC, 0x48],                         // sub rsp, 0x48
            [0x48, 0x83, 0xEC, 0x20],                         // sub rsp, 0x20
            [0x55, 0x48, 0x89, 0xE5],                         // push rbp; mov rbp, rsp
            [0x53, 0x48, 0x83, 0xEC, 0x20],                   // push rbx; sub rsp, 0x20
            [0x48, 0x89, 0x4C, 0x24, 0x08, 0x48, 0x83, 0xEC, 0x28], // mov [rsp+8],rcx; sub rsp,0x28
        ];

        byte[][] prologues32 =
        [
            [0x55, 0x8B, 0xEC],                               // push ebp; mov ebp, esp
            [0x83, 0xEC, 0x10],                               // sub esp, 0x10
            [0x83, 0xEC, 0x08],                               // sub esp, 0x08
            [0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x10],             // push ebp; mov ebp, esp; sub esp, 0x10
            [0x6A, 0xFF, 0x68],                               // push -1; push addr (SEH frame)
            [0x55, 0x8B, 0xEC, 0x6A, 0xFF],                   // push ebp; mov ebp, esp; push -1
        ];

        var prologues = _is64 ? prologues64 : prologues32;

        foreach (var p in prologues)
            if (p.Length == stolenSize) return p;
        foreach (var p in prologues)
            if (p.Length <= stolenSize) return p;
        return null;
    }

    // ════════════════════════════════════════════════════════════════════
    //  IAT auto-fix
    // ════════════════════════════════════════════════════════════════════

    public void FixIat()
    {
        if (!_api.IsConnected || !_api.IsBreakState)
        { _api.Log.Warning("Must be connected and in Break state."); return; }
        FixIatInternal(_api.TargetPid);
    }

    private void FixIatInternal(uint pid)
    {
        if (pid == 0) return;
        ulong peBase = _unpackedPeBase != 0 ? _unpackedPeBase : _originalImageBase;
        if (peBase == 0) return;

        _iatFixes.Clear();

        var dosHdr = _api.Memory.ReadMemory(pid, peBase, 0x400);
        if (dosHdr == null || dosHdr.Length < 0x40 || dosHdr[0] != 'M' || dosHdr[1] != 'Z')
        {
            // PE header may be encrypted still — try brute force on known section
            ScanAndFixIatBruteForce(pid, peBase, true);
            return;
        }

        uint lfanew = BitConverter.ToUInt32(dosHdr, 0x3C);
        if (lfanew + 0x88 > (uint)dosHdr.Length) return;

        ushort magic = BitConverter.ToUInt16(dosHdr, (int)lfanew + 24);
        bool is64 = magic == 0x20B;
        int ptrSize = is64 ? 8 : 4;

        int ddOffset = (int)lfanew + 24 + (is64 ? 0x70 : 0x60);
        if (ddOffset + 16 > dosHdr.Length) return;

        uint importRva = BitConverter.ToUInt32(dosHdr, ddOffset + 8);

        var modules = _api.Symbols.GetModules();
        var moduleRanges = modules.Select(m => (m.BaseAddress, End: m.BaseAddress + m.Size, m.Name)).ToList();

        if (importRva == 0)
        {
            _api.Log.Warning("[IAT] Import directory zeroed — brute-force scanning...");
            ScanAndFixIatBruteForce(pid, peBase, is64);
            return;
        }

        var importData = _api.Memory.ReadMemory(pid, peBase + importRva, 0x2000);
        if (importData == null) return;

        int totalFixed = 0, totalRedirected = 0;

        for (int i = 0; i + 20 <= importData.Length; i += 20)
        {
            uint nameRva = BitConverter.ToUInt32(importData, i + 12);
            uint firstThunk = BitConverter.ToUInt32(importData, i + 16);
            uint origFirstThunk = BitConverter.ToUInt32(importData, i);

            if (nameRva == 0 && firstThunk == 0) break;
            if (firstThunk == 0) continue;

            string dllName = ReadAsciiString(pid, peBase + nameRva);

            byte[]? intData = origFirstThunk != 0
                ? _api.Memory.ReadMemory(pid, peBase + origFirstThunk, 0x800) : null;

            var iatData = _api.Memory.ReadMemory(pid, peBase + firstThunk, 0x800);
            if (iatData == null) continue;

            for (int j = 0; j + ptrSize <= iatData.Length; j += ptrSize)
            {
                ulong iatEntry = is64 ? BitConverter.ToUInt64(iatData, j) : BitConverter.ToUInt32(iatData, j);
                if (iatEntry == 0) break;

                string funcName = "???";
                if (intData != null && j + ptrSize <= intData.Length)
                {
                    ulong intEntry = is64 ? BitConverter.ToUInt64(intData, j) : BitConverter.ToUInt32(intData, j);
                    if (intEntry != 0 && (intEntry & (is64 ? 0x8000000000000000UL : 0x80000000UL)) == 0)
                        funcName = ReadAsciiString(pid, peBase + intEntry + 2);
                    else if (intEntry != 0)
                        funcName = $"Ordinal#{intEntry & 0xFFFF}";
                }

                bool isLegit = moduleRanges.Any(m => iatEntry >= m.BaseAddress && iatEntry < m.End);
                // Themida wraps APIs via thunks in .themida section — these look "in-module" but are protection code
                if (isLegit && IsThemidaAddress(iatEntry)) isLegit = false;
                if (!isLegit)
                {
                    totalRedirected++;
                    ulong resolved = TraceThemidaWrapper(pid, iatEntry, moduleRanges);
                    if (resolved != 0)
                    {
                        string apiName = _api.Symbols.ResolveAddress(resolved) ?? $"0x{resolved:X}";
                        ulong slot = peBase + firstThunk + (ulong)j;
                        byte[] fix = is64 ? BitConverter.GetBytes(resolved) : BitConverter.GetBytes((uint)resolved);
                        if (_api.Memory.WriteMemory(pid, slot, fix))
                        {
                            _iatFixes.Add(new IatFixEntry(slot, iatEntry, resolved, dllName, apiName));
                            totalFixed++;
                        }
                    }
                }
            }
        }

        // Also brute-force scan .rdata for additional redirected pointers
        // (Themida API-wrapping puts extra thunks outside import directory)
        int bfFixed = ScanAndFixIatBruteForce(pid, peBase, is64, moduleRanges);
        totalFixed += bfFixed;

        if (totalRedirected > 0 || bfFixed > 0)
        {
            _api.Log.Warning($"[IAT] {totalRedirected} redirected in import dir, {totalFixed} total fixed (incl. brute-force)");
            foreach (var fix in _iatFixes.Take(15))
                _api.Log.Info($"  {fix.DllName}!{fix.ApiName} → 0x{fix.ResolvedApi:X}");
            if (_iatFixes.Count > 15)
                _api.Log.Info($"  ... and {_iatFixes.Count - 15} more");
        }
        else
        {
            _api.Log.Info("[IAT] All imports clean.");
        }
    }

    private ulong TraceThemidaWrapper(uint pid, ulong addr,
        List<(ulong BaseAddress, ulong End, string Name)> modules)
    {
        ulong current = addr;
        var visited = new HashSet<ulong>();

        for (int depth = 0; depth < 32; depth++)
        {
            if (!visited.Add(current)) return 0;

            var code = _api.Memory.ReadMemory(pid, current, 32);
            if (code == null || code.Length < 2) return 0;

            // JMP rel32
            if (code[0] == 0xE9 && code.Length >= 5)
            {
                int rel = BitConverter.ToInt32(code, 1);
                ulong target = current + 5 + (ulong)(long)rel;
                if (IsInModule(target, modules)) return target;
                current = target; continue;
            }

            // JMP [rip+disp32] (x64) or JMP [disp32] (x86)
            if (code[0] == 0xFF && code[1] == 0x25 && code.Length >= 6)
            {
                ulong ptrAddr;
                if (_is64)
                {
                    int disp = BitConverter.ToInt32(code, 2);
                    ptrAddr = current + 6 + (ulong)(long)disp;
                }
                else
                {
                    ptrAddr = BitConverter.ToUInt32(code, 2); // absolute address on x86
                }
                var p = _api.Memory.ReadMemory(pid, ptrAddr, (uint)_ptrSize);
                if (p != null && p.Length == _ptrSize)
                {
                    ulong target = _is64 ? BitConverter.ToUInt64(p) : BitConverter.ToUInt32(p);
                    if (IsInModule(target, modules)) return target;
                    current = target; continue;
                }
                return 0;
            }

            // JMP reg (can't follow)
            if (code[0] == 0xFF && (code[1] & 0xF8) == 0xE0) return 0;

            // CALL rel32
            if (code[0] == 0xE8 && code.Length >= 5)
            {
                int rel = BitConverter.ToInt32(code, 1);
                ulong target = current + 5 + (ulong)(long)rel;
                if (IsInModule(target, modules)) return target;
                current = target; continue;
            }

            // x64: MOV RAX, imm64; JMP RAX (48 B8 ... FF E0)
            if (_is64 && code[0] == 0x48 && code[1] == 0xB8 && code.Length >= 12 &&
                code[10] == 0xFF && code[11] == 0xE0)
            {
                ulong target = BitConverter.ToUInt64(code, 2);
                if (IsInModule(target, modules)) return target;
                current = target; continue;
            }

            // x64: MOV RBX, imm64; JMP RBX (48 BB ... FF E3)
            if (_is64 && code[0] == 0x48 && code[1] == 0xBB && code.Length >= 12 &&
                code[10] == 0xFF && code[11] == 0xE3)
            {
                ulong target = BitConverter.ToUInt64(code, 2);
                if (IsInModule(target, modules)) return target;
                current = target; continue;
            }

            // x86: MOV EAX, imm32; JMP EAX (B8 ... FF E0)
            if (!_is64 && code[0] == 0xB8 && code.Length >= 7 &&
                code[5] == 0xFF && code[6] == 0xE0)
            {
                ulong target = BitConverter.ToUInt32(code, 1);
                if (IsInModule(target, modules)) return target;
                current = target; continue;
            }

            // PUSH imm32; RET (common x86 obfuscation, works on x64 too)
            if (code[0] == 0x68 && code.Length >= 6 && code[5] == 0xC3)
            {
                ulong target = BitConverter.ToUInt32(code, 1);
                if (IsInModule(target, modules)) return target;
                current = target; continue;
            }

            // x86: CALL [disp32] (FF 15 XX XX XX XX)
            if (!_is64 && code[0] == 0xFF && code[1] == 0x15 && code.Length >= 6)
            {
                ulong ptrAddr = BitConverter.ToUInt32(code, 2);
                var p = _api.Memory.ReadMemory(pid, ptrAddr, 4);
                if (p is { Length: 4 })
                {
                    ulong target = BitConverter.ToUInt32(p);
                    if (IsInModule(target, modules)) return target;
                }
                return 0;
            }

            // NOP / INT3 / push-pop pairs (obfuscation skip)
            if (code[0] is 0x90 or 0xCC) { current++; continue; }
            if (code[0] == 0x50 && code.Length >= 2 && code[1] == 0x58) { current += 2; continue; }
            if (code[0] == 0x51 && code.Length >= 2 && code[1] == 0x59) { current += 2; continue; }
            if (code[0] == 0x52 && code.Length >= 2 && code[1] == 0x5A) { current += 2; continue; }
            if (code[0] == 0x53 && code.Length >= 2 && code[1] == 0x5B) { current += 2; continue; }

            return 0;
        }
        return 0;
    }

    private int ScanAndFixIatBruteForce(uint pid, ulong peBase, bool is64,
        List<(ulong BaseAddress, ulong End, string Name)>? moduleRanges = null)
    {
        if (moduleRanges == null)
        {
            var modules = _api.Symbols.GetModules();
            moduleRanges = modules.Select(m => (m.BaseAddress, End: m.BaseAddress + m.Size, m.Name)).ToList();
        }

        int ptrSize = is64 ? 8 : 4;

        // Scan .rdata and .idata sections for redirected pointers
        int fixedCount = 0;
        foreach (var sect in _sections)
        {
            string nl = sect.Name.ToLowerInvariant();
            // Scan data sections that might contain IAT
            bool isDataSect = nl is ".rdata" or ".idata" or "________";
            // For unnamed sections, check if it's a readable non-executable section
            if (!isDataSect && sect.Name.TrimEnd('_') == "")
            {
                bool isReadable = (sect.Characteristics & 0x40000000) != 0;
                bool isExecutable = (sect.Characteristics & 0x20000000) != 0;
                if (isReadable && !isExecutable) isDataSect = true;
            }
            if (!isDataSect) continue;

            ulong scanBase = peBase + sect.Rva;
            uint scanSize = Math.Min(sect.VirtualSize, 0x20000);

            var data = _api.Memory.ReadMemory(pid, scanBase, scanSize);
            if (data == null) continue;

            for (int i = 0; i + ptrSize <= data.Length; i += ptrSize)
            {
                ulong ptr = is64 ? BitConverter.ToUInt64(data, i) : BitConverter.ToUInt32(data, i);
                if (ptr == 0 || ptr < 0x10000 || ptr > 0x7FFFFFFFFFFF) continue;
                if (IsInModule(ptr, moduleRanges) && !IsThemidaAddress(ptr)) continue;
                if (ptr >= peBase && ptr < peBase + _originalImageSize && !IsThemidaAddress(ptr)) continue;

                // Check if we already fixed this slot
                ulong slotAddr = scanBase + (ulong)i;
                if (_iatFixes.Any(f => f.IatSlotAddress == slotAddr)) continue;

                ulong resolved = TraceThemidaWrapper(pid, ptr, moduleRanges);
                if (resolved != 0)
                {
                    byte[] fix = is64 ? BitConverter.GetBytes(resolved) : BitConverter.GetBytes((uint)resolved);
                    if (_api.Memory.WriteMemory(pid, slotAddr, fix))
                    {
                        string apiName = _api.Symbols.ResolveAddress(resolved) ?? $"0x{resolved:X}";
                        _iatFixes.Add(new IatFixEntry(slotAddr, ptr, resolved, "?", apiName));
                        fixedCount++;
                    }
                }
            }
        }

        if (fixedCount > 0)
            _api.Log.Info($"[IAT] Brute-force: {fixedCount} additional imports fixed");

        return fixedCount;
    }

    private static bool IsInModule(ulong addr, List<(ulong BaseAddress, ulong End, string Name)> modules)
        => modules.Any(m => addr >= m.BaseAddress && addr < m.End);

    /// <summary>
    /// Check if address is in Themida/WinLicense or .boot section (not real code).
    /// These addresses look like they're "in the exe" but actually point to
    /// protection code that will be stripped from the dump.
    /// </summary>
    private bool IsThemidaAddress(ulong addr)
    {
        if (_themidaSectionBase != 0 && addr >= _themidaSectionBase &&
            addr < _themidaSectionBase + _themidaSectionSize)
            return true;
        if (_bootSectionBase != 0 && addr >= _bootSectionBase &&
            addr < _bootSectionBase + _bootSectionSize)
            return true;
        return false;
    }

    private string ReadAsciiString(uint pid, ulong addr)
    {
        if (addr == 0) return "???";
        var bytes = _api.Memory.ReadMemory(pid, addr, 128);
        if (bytes == null) return "???";
        int nul = Array.IndexOf(bytes, (byte)0);
        if (nul < 0) nul = bytes.Length;
        return Encoding.ASCII.GetString(bytes, 0, nul);
    }

    // ════════════════════════════════════════════════════════════════════
    //  PE Dump
    // ════════════════════════════════════════════════════════════════════

    public void DumpUnpackedPe()
    {
        if (!_api.IsConnected || !_api.IsBreakState)
        { _api.Log.Warning("Must be connected and in Break state."); return; }

        uint pid = _api.TargetPid;
        if (pid == 0) return;

        ulong peBase = _unpackedPeBase != 0 ? _unpackedPeBase : _originalImageBase;
        if (peBase == 0) { _api.Log.Warning("No PE base."); return; }

        var dosHdr = _api.Memory.ReadMemory(pid, peBase, 0x1000);
        if (dosHdr == null || dosHdr.Length < 0x40)
        { _api.Log.Error("Cannot read PE header."); return; }

        if (dosHdr[0] != 'M' || dosHdr[1] != 'Z')
        {
            if (_sections.Count == 0) { _api.Log.Error("No section info."); return; }
            DumpWithReconstructedHeader(pid, peBase);
            return;
        }

        uint lfanew = BitConverter.ToUInt32(dosHdr, 0x3C);
        if (lfanew + 0x18 > 0x1000) { _api.Log.Error("Invalid e_lfanew."); return; }

        ushort numSections = BitConverter.ToUInt16(dosHdr, (int)lfanew + 6);
        ushort optSize = BitConverter.ToUInt16(dosHdr, (int)lfanew + 0x14);
        int sectStart = (int)lfanew + 4 + 20 + optSize;

        // Calculate size excluding Themida/boot sections
        uint totalSize = 0x1000;
        var skipIndices = new HashSet<int>();
        for (int i = 0; i < numSections; i++)
        {
            int off = sectStart + i * 40;
            if (off + 40 > dosHdr.Length) break;
            string sname = Encoding.ASCII.GetString(dosHdr, off, 8).TrimEnd('\0').ToLowerInvariant();
            uint secRva = BitConverter.ToUInt32(dosHdr, off + 12);
            uint secVsz = BitConverter.ToUInt32(dosHdr, off + 8);

            if (sname is ".themida" or "themida" or ".winlice" or "winlice" or ".boot" or "boot")
            {
                skipIndices.Add(i);
                continue;
            }

            uint secEnd = secRva + ((secVsz + 0xFFFu) & ~0xFFFu);
            if (secEnd > totalSize) totalSize = secEnd;
        }

        _api.Log.Info($"Dumping {totalSize / 1024}KB (excluding Themida/boot sections)...");
        var image = ReadImageChunked(pid, peBase, totalSize);

        const uint fileAlign = 0x1000;
        int optOff = (int)lfanew + 24;

        Array.Copy(BitConverter.GetBytes(fileAlign), 0, image, optOff + 36, 4); // FileAlignment
        Array.Copy(BitConverter.GetBytes(fileAlign), 0, image, optOff + 60, 4); // SizeOfHeaders

        // Rebuild section table without Themida/boot
        int actual = 0;
        for (int i = 0; i < numSections; i++)
        {
            if (skipIndices.Contains(i)) continue;
            int src = sectStart + i * 40;
            int dst = sectStart + actual * 40;
            if (src + 40 > image.Length || dst + 40 > image.Length) break;
            if (src != dst) Array.Copy(image, src, image, dst, 40);

            uint secRva = BitConverter.ToUInt32(image, dst + 12);
            uint secVsz = BitConverter.ToUInt32(image, dst + 8);
            uint rawSize = (secVsz + fileAlign - 1) & ~(fileAlign - 1);
            Array.Copy(BitConverter.GetBytes(secRva), 0, image, dst + 20, 4);
            Array.Copy(BitConverter.GetBytes(rawSize), 0, image, dst + 16, 4);
            actual++;
        }

        // Update section count
        Array.Copy(BitConverter.GetBytes((ushort)actual), 0, image, (int)lfanew + 6, 2);

        // Fix header fields
        Array.Copy(BitConverter.GetBytes(totalSize), 0, image, optOff + 56, 4); // SizeOfImage
        if (_is64)
            Array.Copy(BitConverter.GetBytes(peBase), 0, image, optOff + 24, 8);    // ImageBase (PE32+)
        else
            Array.Copy(BitConverter.GetBytes((uint)peBase), 0, image, optOff + 28, 4); // ImageBase (PE32)
        Array.Copy(BitConverter.GetBytes(0u), 0, image, optOff + 64, 4);        // Checksum = 0

        // Zero Exception directory (DD[3]) — points to removed .themida section, causes crash
        int ddBase = optOff + (_is64 ? 112 : 96); // start of DataDirectory array
        int exceptDdOff = ddBase + 3 * 8;          // each DD entry is 8 bytes (RVA + Size)
        if (exceptDdOff + 8 <= image.Length)
        {
            Array.Copy(BitConverter.GetBytes(0u), 0, image, exceptDdOff, 4);     // RVA = 0
            Array.Copy(BitConverter.GetBytes(0u), 0, image, exceptDdOff + 4, 4); // Size = 0
        }

        // Zero Debug directory (DD[6]) — may point to removed section
        int debugDdOff = ddBase + 6 * 8;
        if (debugDdOff + 8 <= image.Length)
        {
            Array.Copy(BitConverter.GetBytes(0u), 0, image, debugDdOff, 4);
            Array.Copy(BitConverter.GetBytes(0u), 0, image, debugDdOff + 4, 4);
        }

        // Zero TLS directory (DD[9]) — Themida hooks TLS callbacks
        int tlsDdOff = ddBase + 9 * 8;
        if (tlsDdOff + 8 <= image.Length)
        {
            Array.Copy(BitConverter.GetBytes(0u), 0, image, tlsDdOff, 4);
            Array.Copy(BitConverter.GetBytes(0u), 0, image, tlsDdOff + 4, 4);
        }

        // Zero Load Config directory (DD[10]) — Guard CF/XF pointers point to removed sections
        int loadCfgDdOff = ddBase + 10 * 8;
        if (loadCfgDdOff + 8 <= image.Length)
        {
            Array.Copy(BitConverter.GetBytes(0u), 0, image, loadCfgDdOff, 4);
            Array.Copy(BitConverter.GetBytes(0u), 0, image, loadCfgDdOff + 4, 4);
        }

        // Zero Bound Import directory (DD[11])
        int boundDdOff = ddBase + 11 * 8;
        if (boundDdOff + 8 <= image.Length)
        {
            Array.Copy(BitConverter.GetBytes(0u), 0, image, boundDdOff, 4);
            Array.Copy(BitConverter.GetBytes(0u), 0, image, boundDdOff + 4, 4);
        }

        if (_discoveredOep != 0)
        {
            uint epRva = (uint)(_discoveredOep - peBase);
            Array.Copy(BitConverter.GetBytes(epRva), 0, image, optOff + 16, 4);
        }

        // Clear ASLR/DLL/GuardCF flags
        ushort dllChars = BitConverter.ToUInt16(image, optOff + 70);
        dllChars &= unchecked((ushort)~0x4060);
        Array.Copy(BitConverter.GetBytes(dllChars), 0, image, optOff + 70, 2);

        ushort fileChars = BitConverter.ToUInt16(image, (int)lfanew + 22);
        fileChars &= unchecked((ushort)~0x2000);
        Array.Copy(BitConverter.GetBytes(fileChars), 0, image, (int)lfanew + 22, 2);

        // Apply stolen bytes
        if (ChkAutoFixDump.IsChecked == true && _restoredStolenBytes != null && _discoveredOep != 0)
        {
            uint oepOff = (uint)(_discoveredOep - peBase);
            if (oepOff + _restoredStolenBytes.Length <= image.Length)
                Array.Copy(_restoredStolenBytes, 0, image, oepOff, _restoredStolenBytes.Length);
        }

        // Apply IAT fixes
        if (ChkAutoFixDump.IsChecked == true)
            ApplyIatFixesToImage(image, peBase);

        // Trim to totalSize
        if (totalSize < (uint)image.Length)
        {
            var trimmed = new byte[totalSize];
            Array.Copy(image, trimmed, totalSize);
            image = trimmed;
        }

        SaveDumpFile(image);
    }

    private void DumpWithReconstructedHeader(uint pid, ulong peBase)
    {
        _api.Log.Info("Reconstructing PE header...");

        var clean = _sections
            .Where(s => s.Name.ToLowerInvariant() is not (".themida" or "themida" or ".winlice" or "winlice" or ".boot" or "boot"))
            .ToList();

        uint sizeOfImage = 0x1000;
        foreach (var s in clean)
        {
            uint end = s.Rva + ((s.VirtualSize + 0xFFFu) & ~0xFFFu);
            if (end > sizeOfImage) sizeOfImage = end;
        }

        var image = ReadImageChunked(pid, peBase, sizeOfImage);

        int numSect = clean.Count;
        // PE32: Machine=0x14C, Magic=0x10B, OptSize=0xE0
        // PE32+: Machine=0x8664, Magic=0x20B, OptSize=0xF0
        ushort machine = _is64 ? (ushort)0x8664 : (ushort)0x014C;
        ushort peMagic = _is64 ? (ushort)0x020B : (ushort)0x010B;
        uint optSize = _is64 ? 0xF0u : 0xE0u;

        uint lfanew = 0x80, coffOff = lfanew + 4, optOff = coffOff + 20, sectOff = optOff + optSize;

        for (int i = 0; i < Math.Min(0x1000, image.Length); i++) image[i] = 0;

        image[0] = (byte)'M'; image[1] = (byte)'Z';
        Array.Copy(BitConverter.GetBytes(lfanew), 0, image, 0x3C, 4);
        image[lfanew] = (byte)'P'; image[lfanew + 1] = (byte)'E';

        Array.Copy(BitConverter.GetBytes(machine), 0, image, coffOff, 2);
        Array.Copy(BitConverter.GetBytes((ushort)numSect), 0, image, coffOff + 2, 2);
        Array.Copy(BitConverter.GetBytes((ushort)0x0022), 0, image, coffOff + 18, 2);
        Array.Copy(BitConverter.GetBytes((ushort)optSize), 0, image, coffOff + 16, 2);
        Array.Copy(BitConverter.GetBytes(peMagic), 0, image, optOff, 2);
        if (_discoveredOep != 0)
            Array.Copy(BitConverter.GetBytes((uint)(_discoveredOep - peBase)), 0, image, optOff + 16, 4);

        if (_is64)
        {
            Array.Copy(BitConverter.GetBytes(peBase), 0, image, optOff + 24, 8); // ImageBase (8 bytes)
            Array.Copy(BitConverter.GetBytes(0x1000u), 0, image, optOff + 32, 4); // SectionAlignment
            Array.Copy(BitConverter.GetBytes(0x1000u), 0, image, optOff + 36, 4); // FileAlignment
            Array.Copy(BitConverter.GetBytes((ushort)6), 0, image, optOff + 40, 2); // MajorOSVersion
            Array.Copy(BitConverter.GetBytes((ushort)6), 0, image, optOff + 48, 2); // MajorSubsystemVersion
            Array.Copy(BitConverter.GetBytes(sizeOfImage), 0, image, optOff + 56, 4); // SizeOfImage
            Array.Copy(BitConverter.GetBytes(0x1000u), 0, image, optOff + 60, 4); // SizeOfHeaders
            Array.Copy(BitConverter.GetBytes((ushort)3), 0, image, optOff + 68, 2); // Subsystem=CONSOLE
            Array.Copy(BitConverter.GetBytes((ushort)0x8100), 0, image, optOff + 70, 2); // DllCharacteristics
            Array.Copy(BitConverter.GetBytes(0x100000UL), 0, image, optOff + 72, 8);
            Array.Copy(BitConverter.GetBytes(0x1000UL), 0, image, optOff + 80, 8);
            Array.Copy(BitConverter.GetBytes(0x100000UL), 0, image, optOff + 88, 8);
            Array.Copy(BitConverter.GetBytes(0x1000UL), 0, image, optOff + 96, 8);
            Array.Copy(BitConverter.GetBytes(16u), 0, image, optOff + 108, 4); // NumberOfRvaAndSizes
        }
        else
        {
            Array.Copy(BitConverter.GetBytes((uint)peBase), 0, image, optOff + 28, 4); // ImageBase (4 bytes)
            Array.Copy(BitConverter.GetBytes(0x1000u), 0, image, optOff + 32, 4); // SectionAlignment
            Array.Copy(BitConverter.GetBytes(0x1000u), 0, image, optOff + 36, 4); // FileAlignment
            Array.Copy(BitConverter.GetBytes((ushort)6), 0, image, optOff + 40, 2); // MajorOSVersion
            Array.Copy(BitConverter.GetBytes((ushort)6), 0, image, optOff + 48, 2); // MajorSubsystemVersion
            Array.Copy(BitConverter.GetBytes(sizeOfImage), 0, image, optOff + 56, 4); // SizeOfImage
            Array.Copy(BitConverter.GetBytes(0x1000u), 0, image, optOff + 60, 4); // SizeOfHeaders
            Array.Copy(BitConverter.GetBytes((ushort)3), 0, image, optOff + 68, 2); // Subsystem=CONSOLE
            Array.Copy(BitConverter.GetBytes((ushort)0x8100), 0, image, optOff + 70, 2); // DllCharacteristics
            Array.Copy(BitConverter.GetBytes(0x100000u), 0, image, optOff + 72, 4);
            Array.Copy(BitConverter.GetBytes(0x1000u), 0, image, optOff + 76, 4);
            Array.Copy(BitConverter.GetBytes(0x100000u), 0, image, optOff + 80, 4);
            Array.Copy(BitConverter.GetBytes(0x1000u), 0, image, optOff + 84, 4);
            Array.Copy(BitConverter.GetBytes(16u), 0, image, optOff + 116, 4); // NumberOfRvaAndSizes
        }

        for (int i = 0; i < numSect; i++)
        {
            var s = clean[i];
            int off = (int)sectOff + i * 40;
            byte[] nameBytes = Encoding.ASCII.GetBytes(s.Name.PadRight(8, '\0')[..8]);
            Array.Copy(nameBytes, 0, image, off, 8);
            Array.Copy(BitConverter.GetBytes(s.VirtualSize), 0, image, off + 8, 4);
            Array.Copy(BitConverter.GetBytes(s.Rva), 0, image, off + 12, 4);
            uint rawSize = (s.VirtualSize + 0xFFFu) & ~0xFFFu;
            Array.Copy(BitConverter.GetBytes(rawSize), 0, image, off + 16, 4);
            Array.Copy(BitConverter.GetBytes(s.Rva), 0, image, off + 20, 4);
            Array.Copy(BitConverter.GetBytes(s.Characteristics), 0, image, off + 36, 4);
        }

        if (ChkAutoFixDump.IsChecked == true && _restoredStolenBytes != null && _discoveredOep != 0)
        {
            uint oepOff = (uint)(_discoveredOep - peBase);
            if (oepOff + _restoredStolenBytes.Length <= image.Length)
                Array.Copy(_restoredStolenBytes, 0, image, oepOff, _restoredStolenBytes.Length);
        }

        if (ChkAutoFixDump.IsChecked == true)
            ApplyIatFixesToImage(image, peBase);

        SaveDumpFile(image);
    }

    private void ApplyIatFixesToImage(byte[] image, ulong peBase)
    {
        int applied = 0;
        foreach (var fix in _iatFixes)
        {
            if (fix.IatSlotAddress >= peBase && fix.IatSlotAddress < peBase + (ulong)image.Length)
            {
                uint off = (uint)(fix.IatSlotAddress - peBase);
                if (off + _ptrSize <= (uint)image.Length)
                {
                    byte[] fixBytes = _is64
                        ? BitConverter.GetBytes(fix.ResolvedApi)
                        : BitConverter.GetBytes((uint)fix.ResolvedApi);
                    Array.Copy(fixBytes, 0, image, off, _ptrSize);
                    applied++;
                }
            }
        }
        if (applied > 0)
            _api.Log.Info($"[Dump] Applied {applied} IAT fixes");
    }

    private byte[] ReadImageChunked(uint pid, ulong baseAddr, uint totalSize)
    {
        var image = new byte[totalSize];
        const uint chunk = 0x10000;
        for (uint off = 0; off < totalSize; off += chunk)
        {
            uint sz = Math.Min(chunk, totalSize - off);
            var data = _api.Memory.ReadMemory(pid, baseAddr + off, sz);
            if (data != null)
                Array.Copy(data, 0, image, off, Math.Min(data.Length, (int)sz));
        }
        return image;
    }

    private void SaveDumpFile(byte[] image)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "Executable|*.exe|All files|*.*",
            FileName = "unpacked_themida.exe",
            Title = "Save unpacked PE"
        };

        if (dlg.ShowDialog() == true)
        {
            File.WriteAllBytes(dlg.FileName, image);

            var sb = new StringBuilder();
            sb.AppendLine($"★ Dumped {image.Length / 1024}KB to {dlg.FileName}");
            if (_restoredStolenBytes != null)
                sb.AppendLine($"  Stolen bytes: {_restoredStolenBytes.Length} bytes restored");
            if (_iatFixes.Count > 0)
                sb.AppendLine($"  IAT: {_iatFixes.Count} imports fixed");
            if (_discoveredOep != 0)
            {
                ulong @base = _unpackedPeBase != 0 ? _unpackedPeBase : _originalImageBase;
                sb.AppendLine($"  OEP RVA: 0x{_discoveredOep - @base:X}");
            }
            sb.AppendLine("  Themida + .boot sections removed");
            _api.Log.Warning(sb.ToString());
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  Cleanup
    // ════════════════════════════════════════════════════════════════════

    private void CleanupAllBps()
    {
        foreach (var h in _memBpHandles)
            _api.Breakpoints.RemoveBreakpoint(h);
        _memBpHandles.Clear();

        foreach (var h in _hwBpHandles)
            _api.Breakpoints.RemoveBreakpoint(h);
        _hwBpHandles.Clear();
        _apiBreakpoints.Clear();
    }

    // ════════════════════════════════════════════════════════════════════
    //  UI helpers
    // ════════════════════════════════════════════════════════════════════

    private static GroupBox MakeGroup(string header, UIElement[] items, Brush fg)
    {
        var sp = new StackPanel { Margin = new Thickness(4) };
        foreach (var item in items) sp.Children.Add(item);
        return new GroupBox
        {
            Header = new TextBlock { Text = header, FontWeight = FontWeights.SemiBold, Foreground = fg },
            Content = sp, Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(6),
            BorderBrush = new SolidColorBrush(Color.FromRgb(80, 80, 80))
        };
    }

    private static CheckBox MakeCheckBox(string text, bool isChecked, string tooltip, Brush fg) => new()
    {
        Content = new TextBlock { Text = text, Foreground = fg },
        IsChecked = isChecked, ToolTip = tooltip, Margin = new Thickness(0, 2, 0, 2)
    };

    private static Button MakeButton(string text, string tooltip) => new()
    {
        Content = text, Padding = new Thickness(16, 6, 16, 6),
        Margin = new Thickness(0, 0, 8, 8), ToolTip = tooltip
    };

    private void AddButton(WrapPanel panel, string text, string tooltip, Action onClick)
    {
        var btn = MakeButton(text, tooltip);
        btn.Click += (_, _) => onClick();
        panel.Children.Add(btn);
    }
}
