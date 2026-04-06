using System.IO;
using System.Runtime.InteropServices;
using KernelFlirt.SDK;
using KernelFlirt.UI.Models;
using KernelFlirt.UI.Services.Interop;

namespace KernelFlirt.UI.Services;

/// <summary>
/// Resolves addresses to symbol names using dbghelp.dll.
/// Reads PE debug directories from target VM to extract RSDS GUID/age/PDB name,
/// then uses SymFindFileInPathW to download PDBs from symbol server.
/// </summary>
public class SymbolService : IDisposable
{
    private readonly DriverComm _debugger;
    private readonly Dictionary<ulong, string> _symbolCache = new();
    private readonly HashSet<ulong> _loadedModules = new();
    private readonly Dictionary<ulong, string> _pdbPaths = new();

    /// <summary>
    /// User-defined function names (registered via RegisterFunction).
    /// Highest priority — checked before SymFromAddr and function table.
    /// </summary>
    private readonly Dictionary<ulong, (string Name, uint Size)> _userFunctions = new();

    /// <summary>
    /// Function lookup table built from SymEnumSymbols.
    /// Sorted by address for binary search.
    /// Used as fallback when SymFromAddr fails.
    /// </summary>
    private readonly List<(ulong Address, uint Size, string Name)> _functionTable = new();

    private readonly object _lock = new();
    private IntPtr _hProcess;
    private bool _initialized;
    private string _symbolPath = @"srv*C:\Symbols*https://msdl.microsoft.com/download/symbols";
    private DbgHelpNative.SymRegisterCallbackProc64? _callbackDelegate; // prevent GC

    // Session-space modules — their pages aren't mapped in System (PID 4) context.
    // Reading their PE headers from PID 4 causes PAGE_FAULT_IN_NONPAGED_AREA BSOD.
    private static readonly HashSet<string> SessionSpaceModules = new(StringComparer.OrdinalIgnoreCase)
    {
        "win32k.sys", "win32kbase.sys", "win32kfull.sys", "win32kns.sys",
        "cdd.dll", "TSDDD.dll", "rdpdd.dll",
        "dxgmms1.sys", "dxgmms2.sys",
    };

    // Modules known to have no public PDB on Microsoft Symbol Server.
    // Suppress "PDB not found" warnings for these.
    private static readonly HashSet<string> NoPdbModules = new(StringComparer.OrdinalIgnoreCase)
    {
        // VMware Tools drivers
        "vsock.sys", "vmci.sys", "vmrawdsk.sys", "vmmouse.sys",
        "vm3dmp_loader.sys", "vm3dmp.sys", "vmusbmouse.sys",
        "vmmemctl.sys", "vmhgfs.sys",
        // Windows modules without public symbols
        "clipsp.sys", "peauth.sys", "drmk.sys",
        // Our own driver
        "KernelFlirt.sys",
    };

    public event Action<string>? LogMessage;

    public string SymbolPath
    {
        get => _symbolPath;
        set
        {
            _symbolPath = value;
            if (_initialized)
                DbgHelpNative.SymSetSearchPathW(_hProcess, value);
        }
    }

    public bool IsInitialized => _initialized;

    public SymbolService(DriverComm debugger)
    {
        _debugger = debugger;
    }

    /// <summary>
    /// Initialize dbghelp symbol engine.
    /// Returns null on success or an error message on failure.
    /// </summary>
    public string? Initialize()
    {
        lock (_lock)
        {
            if (_initialized) return null;

            _hProcess = new IntPtr(0x1337);

            // No SYMOPT_DEBUG — we don't want verbose dbghelp output
            DbgHelpNative.SymSetOptions(
                DbgHelpNative.SYMOPT_UNDNAME |
                DbgHelpNative.SYMOPT_DEFERRED_LOADS |
                DbgHelpNative.SYMOPT_FAVOR_COMPRESSED);

            if (!DbgHelpNative.SymInitializeW(_hProcess, _symbolPath, false))
            {
                int err = Marshal.GetLastWin32Error();
                return $"SymInitialize failed (error {err})";
            }

            _callbackDelegate = OnDbgHelpCallback;
            DbgHelpNative.SymRegisterCallbackW64(_hProcess, _callbackDelegate, 0);

            _initialized = true;
            return null;
        }
    }

    /// <summary>
    /// Load a module with debug info read from target memory.
    /// pid=0 for kernel modules.
    /// Only logs errors — successful loads are silent.
    /// </summary>
    public bool LoadModule(uint pid, string moduleName, ulong baseAddress, uint size)
    {
        lock (_lock)
        {
            if (!_initialized) return false;
            if (_loadedModules.Contains(baseAddress)) return true;

            // Try to extract RSDS info from target PE to download PDB
            RsdsInfo? rsds = null;
            bool isSessionSpace = pid == 0 && IsSessionSpaceModule(moduleName);
            if (!isSessionSpace)
            {
                try
                {
                    rsds = ReadRsdsFromTarget(pid, baseAddress);
                }
                catch (Exception ex)
                {
                    LogMessage?.Invoke($"  {moduleName}: PE read error: {ex.Message}");
                }
            }

            string? pdbPath = null;
            if (rsds != null)
            {
                pdbPath = FindPdb(rsds);
                if (pdbPath == null && !IsNoPdbModule(moduleName))
                    LogMessage?.Invoke($"  {moduleName}: PDB not found (pdb='{rsds.PdbName}')");
            }

            // Load module — if we have a local PDB, pass its path as ImageName
            string imageName = pdbPath ?? moduleName;
            ulong result = DbgHelpNative.SymLoadModuleExW(
                _hProcess, IntPtr.Zero, imageName, null,
                baseAddress, size, IntPtr.Zero, 0);
            int err = Marshal.GetLastWin32Error();

            bool ok = result != 0 || err == 0;

            if (ok)
            {
                _loadedModules.Add(baseAddress);
                if (pdbPath != null)
                    _pdbPaths[baseAddress] = pdbPath;

                // Invalidate cached symbol lookups for addresses in this module's range
                // so they get re-resolved with the newly loaded PDB
                var staleKeys = _symbolCache.Keys
                    .Where(a => a >= baseAddress && a < baseAddress + size)
                    .ToList();
                foreach (var key in staleKeys)
                    _symbolCache.Remove(key);

                return true;
            }

            LogMessage?.Invoke($"  {moduleName}: SymLoadModuleExW FAILED err={err}");
            return false;
        }
    }

    /// <summary>
    /// Get the local PDB file path for a loaded module, or null if not available.
    /// </summary>
    public string? GetPdbPath(ulong baseAddress)
    {
        lock (_lock)
        {
            return _pdbPaths.TryGetValue(baseAddress, out var path) ? path : null;
        }
    }

    /// <summary>
    /// RSDS CodeView info extracted from PE debug directory.
    /// </summary>
    private class RsdsInfo
    {
        public Guid Guid;
        public uint Age;
        public string PdbName = "";
    }

    /// <summary>
    /// Read RSDS CodeView info from target process PE debug directory.
    /// </summary>
    private RsdsInfo? ReadRsdsFromTarget(uint pid, ulong baseAddress)
    {
        if (pid == 0) pid = 4;

        var dosHeader = _debugger.ReadMemory(pid, baseAddress, 64);
        if (dosHeader == null || dosHeader.Length < 64) return null;
        if (dosHeader[0] != 0x4D || dosHeader[1] != 0x5A) return null;

        uint e_lfanew = BitConverter.ToUInt32(dosHeader, 0x3C);
        if (e_lfanew > 0x1000) return null;

        var peHeader = _debugger.ReadMemory(pid, baseAddress + e_lfanew, 0x200);
        if (peHeader == null || peHeader.Length < 0x18) return null;
        if (peHeader[0] != 0x50 || peHeader[1] != 0x45) return null;

        ushort optHeaderSize = BitConverter.ToUInt16(peHeader, 20);
        if (optHeaderSize < 0x70) return null;

        ushort magic = BitConverter.ToUInt16(peHeader, 24);
        int ddOffset;
        if (magic == 0x20B) ddOffset = 24 + 112;
        else if (magic == 0x10B) ddOffset = 24 + 96;
        else return null;

        int debugDdOffset = ddOffset + 6 * 8;
        if (debugDdOffset + 8 > peHeader.Length) return null;

        uint debugDirRva = BitConverter.ToUInt32(peHeader, debugDdOffset);
        uint debugDirSize = BitConverter.ToUInt32(peHeader, debugDdOffset + 4);
        if (debugDirRva == 0 || debugDirSize == 0) return null;
        if (debugDirSize > 0x2000) return null;

        var debugDirEntries = _debugger.ReadMemory(pid, baseAddress + debugDirRva, debugDirSize);
        if (debugDirEntries == null || debugDirEntries.Length < 28) return null;

        int numEntries = debugDirEntries.Length / 28;

        for (int i = 0; i < numEntries; i++)
        {
            int off = i * 28;
            uint type = BitConverter.ToUInt32(debugDirEntries, off + 12);
            if (type != 2) continue; // IMAGE_DEBUG_TYPE_CODEVIEW

            uint dataSize = BitConverter.ToUInt32(debugDirEntries, off + 16);
            uint dataRva = BitConverter.ToUInt32(debugDirEntries, off + 20);
            if (dataSize == 0 || dataSize > 0x10000 || dataRva == 0) continue;

            var rawData = _debugger.ReadMemory(pid, baseAddress + dataRva, dataSize);
            if (rawData == null || rawData.Length < 24) continue;

            uint cvSig = BitConverter.ToUInt32(rawData, 0);
            if (cvSig != 0x53445352) continue; // 'RSDS'

            var guid = new Guid(new ReadOnlySpan<byte>(rawData, 4, 16));
            uint age = BitConverter.ToUInt32(rawData, 20);
            int nameStart = 24;
            int nameEnd = Array.IndexOf<byte>(rawData, 0, nameStart);
            if (nameEnd < 0) nameEnd = rawData.Length;
            string pdbName = System.Text.Encoding.ASCII.GetString(rawData, nameStart, nameEnd - nameStart);

            // Use just the filename, not full path
            pdbName = Path.GetFileName(pdbName);

            return new RsdsInfo { Guid = guid, Age = age, PdbName = pdbName };
        }

        return null;
    }

    /// <summary>
    /// Find/download PDB using SymFindFileInPathW with RSDS GUID and age.
    /// Falls back to searching for PDB by filename in symbol path directories.
    /// Returns local path to PDB or null if not found.
    /// </summary>
    private string? FindPdb(RsdsInfo rsds)
    {
        // Try SymFindFileInPathW first (handles symbol server downloads + GUID matching)
        IntPtr guidPtr = Marshal.AllocHGlobal(16);
        IntPtr pathBuf = Marshal.AllocHGlobal(260 * 2);
        try
        {
            byte[] guidBytes = rsds.Guid.ToByteArray();
            Marshal.Copy(guidBytes, 0, guidPtr, 16);

            unsafe { new Span<byte>((void*)pathBuf, 260 * 2).Clear(); }

            bool found = DbgHelpNative.SymFindFileInPathW(
                _hProcess,
                null,              // use default search path
                rsds.PdbName,
                guidPtr,
                rsds.Age,
                0,                 // unused
                DbgHelpNative.SSRVOPT_GUIDPTR,
                pathBuf,
                IntPtr.Zero,       // no callback
                IntPtr.Zero);

            if (found)
            {
                string? path = Marshal.PtrToStringUni(pathBuf);
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    return path;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(guidPtr);
            Marshal.FreeHGlobal(pathBuf);
        }

        // Fallback: search for PDB by filename in local directories from symbol path.
        // This finds PDBs for user-built apps that aren't on a symbol server.
        // Searches each non-srv* component and also looks next to the PDB name's original path.
        return FindPdbInLocalPaths(rsds.PdbName);
    }

    /// <summary>
    /// Search local directories in the symbol path for a PDB file by name.
    /// Handles paths like: C:\MyPDBs;srv*C:\Symbols*url;D:\Build\Output
    /// Only searches plain directory components (not srv* entries).
    /// </summary>
    private string? FindPdbInLocalPaths(string pdbName)
    {
        if (string.IsNullOrEmpty(pdbName)) return null;

        var fileName = Path.GetFileName(pdbName);

        foreach (var component in _symbolPath.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = component.Trim();

            if (trimmed.StartsWith("srv*", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("symsrv*", StringComparison.OrdinalIgnoreCase))
            {
                // For srv* entries, check the local cache directory (second part)
                // srv*C:\Symbols*https://... -> check C:\Symbols
                var parts = trimmed.Split('*');
                if (parts.Length >= 2 && Directory.Exists(parts[1]))
                {
                    var candidate = Path.Combine(parts[1], fileName);
                    if (File.Exists(candidate)) return candidate;
                }
                continue;
            }

            // Plain directory path — search for PDB here and in subdirectories (1 level)
            if (!Directory.Exists(trimmed)) continue;

            var direct = Path.Combine(trimmed, fileName);
            if (File.Exists(direct)) return direct;

            // Check immediate subdirectories (common for build output: bin/Debug, bin/Release)
            try
            {
                foreach (var subDir in Directory.EnumerateDirectories(trimmed))
                {
                    var sub = Path.Combine(subDir, fileName);
                    if (File.Exists(sub)) return sub;
                }
            }
            catch { /* access denied etc. — skip */ }
        }

        return null;
    }

    /// <summary>
    /// Resolve an address to a symbol name.
    /// </summary>
    public string? ResolveAddress(uint pid, ulong address, List<ModuleInfo> modules)
    {
        if (_symbolCache.TryGetValue(address, out var cached))
            return cached;

        string? result = null;
        bool isRealSymbol = false;

        // Try dbghelp (SymFromAddr + function table fallback)
        if (_initialized)
        {
            result = ResolveViaDbgHelp(address);
            if (result != null)
                isRealSymbol = true;
        }

        // Fallback: module+offset (NOT cached — may be resolved later when PDB loads)
        if (result == null)
        {
            var module = modules.FirstOrDefault(m =>
                address >= m.BaseAddress && address < m.BaseAddress + m.Size);
            if (module != null)
            {
                ulong offset = address - module.BaseAddress;
                result = $"{module.Name}+0x{offset:X}";
            }
        }

        // Only cache real symbol resolutions, not module+offset fallbacks
        if (result != null && isRealSymbol)
            _symbolCache[address] = result;

        return result;
    }

    /// <summary>
    /// Resolve address using dbghelp SymFromAddr.
    /// </summary>
    public string? ResolveViaDbgHelp(ulong address)
    {
        lock (_lock)
        {
            if (!_initialized) return null;

            // Check user-defined functions first (highest priority)
            foreach (var (funcAddr, (funcName, funcSize)) in _userFunctions)
            {
                uint effSize = funcSize > 0 ? funcSize : 0x1000;
                if (address >= funcAddr && address < funcAddr + effSize)
                {
                    ulong disp = address - funcAddr;
                    return disp > 0 ? $"{funcName}+0x{disp:X}" : funcName;
                }
            }

            // Try SymFromAddr (works for system DLLs with proper PDBs)
            var symbolInfo = DbgHelpNative.AllocSymbolInfo();
            try
            {
                bool ok = DbgHelpNative.SymFromAddrW(_hProcess, address, out ulong displacement, symbolInfo);
                if (ok)
                {
                    var (name, symAddr, symSize) = DbgHelpNative.ReadSymbolInfo(symbolInfo);
                    if (!string.IsNullOrEmpty(name))
                    {
                        return displacement > 0
                            ? $"{name}+0x{displacement:X}"
                            : name;
                    }
                }
            }
            finally
            {
                DbgHelpNative.FreeSymbolInfo(symbolInfo);
            }

            // Fallback: use function table built from SymEnumSymbols
            // (works when SymFromAddr fails but SymEnumSymbols found the functions)
            return ResolveViaFunctionTable(address);
        }
    }

    /// <summary>
    /// Resolve address to exact symbol name (displacement must be 0).
    /// Returns null if address is not at the start of a known symbol.
    /// </summary>
    public string? ResolveExact(ulong address)
    {
        lock (_lock)
        {
            if (!_initialized) return null;

            var symbolInfo = DbgHelpNative.AllocSymbolInfo();
            try
            {
                bool ok = DbgHelpNative.SymFromAddrW(_hProcess, address, out ulong displacement, symbolInfo);
                if (ok && displacement == 0)
                {
                    var (name, _, _) = DbgHelpNative.ReadSymbolInfo(symbolInfo);
                    if (!string.IsNullOrEmpty(name))
                        return name;
                }
            }
            finally
            {
                DbgHelpNative.FreeSymbolInfo(symbolInfo);
            }
            return null;
        }
    }

    /// <summary>
    /// Get the containing function's start address and size for the given address.
    /// Returns (funcStart, funcSize) or (0, 0) if not found.
    /// </summary>
    public (ulong Address, uint Size) GetFunctionBounds(ulong address)
    {
        lock (_lock)
        {
            if (!_initialized) return (0, 0);

            var symbolInfo = DbgHelpNative.AllocSymbolInfo();
            try
            {
                bool ok = DbgHelpNative.SymFromAddrW(_hProcess, address, out _, symbolInfo);
                if (ok)
                {
                    var (_, symAddr, symSize) = DbgHelpNative.ReadSymbolInfo(symbolInfo);
                    if (symAddr != 0 && symSize > 0)
                        return (symAddr, symSize);
                }
            }
            finally
            {
                DbgHelpNative.FreeSymbolInfo(symbolInfo);
            }
            return (0, 0);
        }
    }

    /// <summary>
    /// Resolve a symbol name to an address using SymFromNameW.
    /// Supports: "WinMain", "module!func", "ntdll!NtClose", etc.
    /// </summary>
    public ulong ResolveNameToAddress(string name)
    {
        lock (_lock)
        {
            if (!_initialized) return 0;
            var symbolInfo = DbgHelpNative.AllocSymbolInfo();
            try
            {
                if (DbgHelpNative.SymFromNameW(_hProcess, name, symbolInfo))
                {
                    var (symName, address, _) = DbgHelpNative.ReadSymbolInfo(symbolInfo);
                    return address;
                }
            }
            finally
            {
                DbgHelpNative.FreeSymbolInfo(symbolInfo);
            }
            return 0;
        }
    }

    private bool OnDbgHelpCallback(IntPtr hProcess, uint actionCode, ulong callbackData, ulong userContext)
    {
        // Callback kept for future use but silent by default
        return false;
    }

    private static bool IsSessionSpaceModule(string moduleName)
    {
        var name = Path.GetFileName(moduleName);
        return SessionSpaceModules.Contains(name);
    }

    private static bool IsNoPdbModule(string moduleName)
    {
        var name = Path.GetFileName(moduleName);
        return NoPdbModules.Contains(name);
    }

    /// <summary>
    /// Enumerate all function symbols from loaded modules.
    /// Returns (name, address, size) tuples.
    /// </summary>
    public List<(string Name, ulong Address, uint Size)> EnumFunctions(ulong moduleBase = 0)
    {
        var results = new List<(string, ulong, uint)>();
        lock (_lock)
        {
            if (!_initialized) return results;

            var bases = moduleBase != 0
                ? new List<ulong> { moduleBase }
                : _loadedModules.ToList();

            foreach (var baseDll in bases)
            {
                DbgHelpNative.SymEnumSymbolsCallbackW callback = (pSymInfo, symbolSize, ctx) =>
                {
                    var (name, addr, sz) = DbgHelpNative.ReadSymbolInfo(pSymInfo);
                    uint tag = (uint)Marshal.ReadInt32(pSymInfo, 72); // Tag at offset 72
                    if (tag == DbgHelpNative.SymTagFunction && addr != 0 && !string.IsNullOrEmpty(name))
                        results.Add((name, addr, sz));
                    return true; // continue enumeration
                };

                DbgHelpNative.SymEnumSymbolsW(_hProcess, baseDll, "*", callback, IntPtr.Zero);
            }

            // Update function lookup table for ResolveAddress fallback
            if (results.Count > 0)
            {
                // Merge new results into the function table (avoid duplicates)
                var existing = new HashSet<ulong>(_functionTable.Select(f => f.Address));
                foreach (var (name, addr, sz) in results)
                {
                    if (!existing.Contains(addr))
                        _functionTable.Add((addr, sz, name));
                }
                _functionTable.Sort((a, b) => a.Address.CompareTo(b.Address));

            }
        }
        return results;
    }

    /// <summary>
    /// Look up a function name from the function table (built by EnumFunctions).
    /// Uses binary search. Returns "funcname" or "funcname+0xOFFSET" or null.
    /// </summary>
    private string? ResolveViaFunctionTable(ulong address)
    {
        if (_functionTable.Count == 0) return null;

        // Binary search: find the last function with Address <= address
        int lo = 0, hi = _functionTable.Count - 1;
        int best = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (_functionTable[mid].Address <= address)
            {
                best = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        if (best < 0) return null;

        var (funcAddr, funcSize, funcName) = _functionTable[best];

        // Check if address is within the function
        // If size is 0, allow up to 0x1000 (heuristic for functions without size info)
        uint effectiveSize = funcSize > 0 ? funcSize : 0x1000;
        if (address >= funcAddr + effectiveSize) return null;

        ulong displacement = address - funcAddr;
        return displacement > 0
            ? $"{funcName}+0x{displacement:X}"
            : funcName;
    }

    /// <summary>
    /// Result of PDB type resolution for a function's parameters and locals.
    /// </summary>
    public class FunctionTypeInfo
    {
        /// <summary>Ordered list of parameters (in declaration order) with PDB name and type.</summary>
        public List<(string Name, string Type)> Params { get; } = new();
        /// <summary>Local variables: PDB name → resolved type.</summary>
        public Dictionary<string, string> Locals { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Get parameter and local variable types for a function from PDB.
    /// Uses SymSetContext + SymEnumSymbolsW to enumerate function scope,
    /// then SymGetTypeInfo to resolve type names (HWND, LPARAM, etc.).
    /// </summary>
    public FunctionTypeInfo GetFunctionTypeInfo(ulong funcAddress)
    {
        var result = new FunctionTypeInfo();
        lock (_lock)
        {
            if (!_initialized)
            {
                LogMessage?.Invoke("GetFunctionTypeInfo: not initialized");
                return result;
            }

            // 1. Get the function symbol to find its module base
            var symbolInfo = DbgHelpNative.AllocSymbolInfo();
            ulong modBase;
            string funcName;
            uint funcTypeIndex;
            try
            {
                if (!DbgHelpNative.SymFromAddrW(_hProcess, funcAddress, out _, symbolInfo))
                {
                    LogMessage?.Invoke($"GetFunctionTypeInfo: SymFromAddr failed for 0x{funcAddress:X} err={Marshal.GetLastWin32Error()}");
                    return result;
                }
                modBase = (ulong)Marshal.ReadInt64(symbolInfo, 32);
                funcTypeIndex = (uint)Marshal.ReadInt32(symbolInfo, 4); // TypeIndex
                var (name, _, _) = DbgHelpNative.ReadSymbolInfo(symbolInfo);
                funcName = name;
            }
            finally
            {
                DbgHelpNative.FreeSymbolInfo(symbolInfo);
            }

            LogMessage?.Invoke($"GetFunctionTypeInfo: func={funcName} modBase=0x{modBase:X}");
            if (modBase == 0) return result;

            // 2. Set context to the function scope
            IntPtr stackFrame = Marshal.AllocHGlobal(128);
            try
            {
                unsafe { new Span<byte>((void*)stackFrame, 128).Clear(); }
                Marshal.WriteInt64(stackFrame, 0, (long)funcAddress);

                bool ctxOk = DbgHelpNative.SymSetContext(_hProcess, stackFrame, IntPtr.Zero);
                int ctxErr = Marshal.GetLastWin32Error();
                LogMessage?.Invoke($"GetFunctionTypeInfo: SymSetContext={ctxOk} err={ctxErr}");

                if (!ctxOk && ctxErr != 0)
                    return result;
            }
            finally
            {
                Marshal.FreeHGlobal(stackFrame);
            }

            // 3. Enumerate locals/params in function scope
            // IMPORTANT: collect (name, typeIndex) pairs first, then resolve types AFTER
            // SymEnumSymbolsW returns — dbghelp is not re-entrant, SymGetTypeInfo inside
            // a SymEnumSymbols callback causes a crash.
            var collected = new List<(string Name, uint TypeIndex, bool IsParam)>();
            DbgHelpNative.SymEnumSymbolsCallbackW callback = (pSymInfo, symSize, ctx) =>
            {
                uint tag = (uint)Marshal.ReadInt32(pSymInfo, 72);
                if (tag != DbgHelpNative.SymTagData) return true;

                uint flags = (uint)Marshal.ReadInt32(pSymInfo, 40);
                uint typeIndex = (uint)Marshal.ReadInt32(pSymInfo, 4);

                // Read name — use null-terminated read to avoid including trailing \0
                string name = Marshal.PtrToStringUni(pSymInfo + DbgHelpNative.SYMBOL_INFO_NAME_OFFSET) ?? "";

                if (!string.IsNullOrEmpty(name))
                {
                    bool isParam = (flags & DbgHelpNative.SYMFLAG_PARAMETER) != 0;
                    collected.Add((name, typeIndex, isParam));
                }
                return true;
            };

            DbgHelpNative.SymEnumSymbolsW(_hProcess, 0, "*", callback, IntPtr.Zero);
            LogMessage?.Invoke($"GetFunctionTypeInfo: enumerated {collected.Count} symbols");

            // 4. Resolve types AFTER enumeration is complete
            foreach (var (name, typeIndex, isParam) in collected)
            {
                string? typeName = ResolveTypeNameFromIndex(modBase, typeIndex, 0);
                LogMessage?.Invoke($"  {name} (typeIdx={typeIndex}, param={isParam}) -> {typeName ?? "(null)"}");
                if (typeName == null) continue;

                if (isParam)
                    result.Params.Add((name, typeName));
                else if (!result.Locals.ContainsKey(name))
                    result.Locals[name] = typeName;
            }

            // 5. Fallback for public PDBs: if SymEnumSymbolsW returned no params,
            // try to get parameter types from the function's FunctionType via TI_FINDCHILDREN.
            // Public PDBs have function type info but no local/param symbol info.
            if (result.Params.Count == 0)
            {
                var funcTypeParams = GetFunctionTypeParams(modBase, funcTypeIndex);
                if (funcTypeParams.Count > 0)
                {
                    LogMessage?.Invoke($"GetFunctionTypeInfo: fallback via FunctionType got {funcTypeParams.Count} param types");
                    foreach (var (name, type) in funcTypeParams)
                    {
                        result.Params.Add((name, type));
                        LogMessage?.Invoke($"  param: {type} {name}");
                    }
                }
            }

            LogMessage?.Invoke($"GetFunctionTypeInfo: {result.Params.Count} params, {result.Locals.Count} locals");
        }
        return result;
    }

    // Windows handle types: internal struct pointer → proper typedef name
    // e.g., HWND__* → HWND, HDC__* → HDC, etc.
    private static readonly Dictionary<string, string> HandleTypeMap = new(StringComparer.Ordinal)
    {
        // Window/UI handles
        { "HWND__*", "HWND" },
        { "HDC__*", "HDC" },
        { "HMENU__*", "HMENU" },
        { "HICON__*", "HICON" },
        { "HCURSOR__*", "HCURSOR" },
        { "HBRUSH__*", "HBRUSH" },
        { "HFONT__*", "HFONT" },
        { "HPEN__*", "HPEN" },
        { "HBITMAP__*", "HBITMAP" },
        { "HRGN__*", "HRGN" },
        { "HPALETTE__*", "HPALETTE" },
        { "HACCEL__*", "HACCEL" },
        { "HDWP__*", "HDWP" },
        { "HMONITOR__*", "HMONITOR" },

        // GDI handles
        { "HGDIOBJ__*", "HGDIOBJ" },
        { "HMETAFILE__*", "HMETAFILE" },
        { "HENHMETAFILE__*", "HENHMETAFILE" },
        { "HCOLORSPACE__*", "HCOLORSPACE" },
        { "HGLRC__*", "HGLRC" },

        // System handles
        { "HINSTANCE__*", "HINSTANCE" },
        { "HMODULE__*", "HMODULE" },
        { "HKEY__*", "HKEY" },
        { "HDESK__*", "HDESK" },
        { "HWINSTA__*", "HWINSTA" },
        { "HTASK__*", "HTASK" },
        { "HFILE__*", "HFILE" },
        { "HRSRC__*", "HRSRC" },
        { "HGLOBAL__*", "HGLOBAL" },
        { "HLOCAL__*", "HLOCAL" },
        { "HDPA__*", "HDPA" },
        { "HDSA__*", "HDSA" },

        // Service/event/device handles
        { "SC_HANDLE__*", "SC_HANDLE" },
        { "SERVICE_STATUS_HANDLE__*", "SERVICE_STATUS_HANDLE" },
        { "HDEVINFO__*", "HDEVINFO" },
        { "HDEVNOTIFY__*", "HDEVNOTIFY" },
        { "HPOWERNOTIFY__*", "HPOWERNOTIFY" },

        // Multimedia handles
        { "HWAVEOUT__*", "HWAVEOUT" },
        { "HWAVEIN__*", "HWAVEIN" },
        { "HMIDIOUT__*", "HMIDIOUT" },
        { "HMIDIIN__*", "HMIDIIN" },
        { "HMIXER__*", "HMIXER" },

        // Imaging/print
        { "HIMAGELIST__*", "HIMAGELIST" },
        { "HDROP__*", "HDROP" },
        { "HPROPSHEETPAGE__*", "HPROPSHEETPAGE" },

        // Common controls
        { "HTREEITEM__*", "HTREEITEM" },

        // Crypto
        { "HCERTSTORE__*", "HCERTSTORE" },
        { "HCRYPTPROV__*", "HCRYPTPROV" },
    };

    /// <summary>
    /// Get parameter types from a function's FunctionType (works with public PDBs).
    /// The function symbol's TypeIndex → FunctionType → children = FunctionArgType → actual type.
    /// Returns param types without names (public PDBs don't have param names).
    /// </summary>
    private List<(string Name, string Type)> GetFunctionTypeParams(ulong modBase, uint funcSymTypeIndex)
    {
        var result = new List<(string, string)>();
        LogMessage?.Invoke($"  FuncType fallback: typeIndex={funcSymTypeIndex}");

        if (funcSymTypeIndex == 0)
        {
            LogMessage?.Invoke("  FuncType fallback: typeIndex=0, no type info in PDB");
            return result;
        }

        uint funcTypeId = funcSymTypeIndex;

        IntPtr buf = Marshal.AllocHGlobal(8);
        try
        {
            // Check tag of funcSymTypeIndex
            if (!DbgHelpNative.SymGetTypeInfo(_hProcess, modBase, funcSymTypeIndex,
                DbgHelpNative.TI_GET_SYMTAG, buf))
            {
                LogMessage?.Invoke($"  FuncType fallback: TI_GET_SYMTAG failed err={Marshal.GetLastWin32Error()}");
                return result;
            }
            uint tag = (uint)Marshal.ReadInt32(buf);
            LogMessage?.Invoke($"  FuncType fallback: symTag={tag} (need 13=FunctionType)");

            if (tag != DbgHelpNative.SymTagFunctionType)
            {
                // Chase TI_GET_TYPE to get the FunctionType
                if (!DbgHelpNative.SymGetTypeInfo(_hProcess, modBase, funcSymTypeIndex,
                    DbgHelpNative.TI_GET_TYPE, buf))
                {
                    LogMessage?.Invoke($"  FuncType fallback: TI_GET_TYPE failed err={Marshal.GetLastWin32Error()}");
                    return result;
                }
                funcTypeId = (uint)Marshal.ReadInt32(buf);
                LogMessage?.Invoke($"  FuncType fallback: chased to funcTypeId={funcTypeId}");

                // Verify tag of chased type
                if (DbgHelpNative.SymGetTypeInfo(_hProcess, modBase, funcTypeId,
                    DbgHelpNative.TI_GET_SYMTAG, buf))
                {
                    uint chasedTag = (uint)Marshal.ReadInt32(buf);
                    LogMessage?.Invoke($"  FuncType fallback: chased tag={chasedTag}");
                }
            }

            // Get children count (= number of parameters)
            if (!DbgHelpNative.SymGetTypeInfo(_hProcess, modBase, funcTypeId,
                DbgHelpNative.TI_GET_CHILDRENCOUNT, buf))
            {
                LogMessage?.Invoke($"  FuncType fallback: TI_GET_CHILDRENCOUNT failed err={Marshal.GetLastWin32Error()}");
                return result;
            }
            int childCount = Marshal.ReadInt32(buf);
            LogMessage?.Invoke($"  FuncType fallback: childCount={childCount}");
            if (childCount <= 0) return result;

            LogMessage?.Invoke($"  FunctionType: typeId={funcTypeId} childCount={childCount}");

            // Allocate TI_FINDCHILDREN_PARAMS: { ULONG Count, ULONG Start, ULONG ChildId[Count] }
            int paramsSize = 8 + 4 * childCount; // 2 ULONGs header + Count ULONGs
            IntPtr findBuf = Marshal.AllocHGlobal(paramsSize);
            try
            {
                Marshal.WriteInt32(findBuf, 0, childCount); // Count
                Marshal.WriteInt32(findBuf, 4, 0);          // Start

                if (!DbgHelpNative.SymGetTypeInfo(_hProcess, modBase, funcTypeId,
                    DbgHelpNative.TI_FINDCHILDREN, findBuf))
                {
                    LogMessage?.Invoke($"  FunctionType: TI_FINDCHILDREN failed err={Marshal.GetLastWin32Error()}");
                    return result;
                }

                // Each child is a FunctionArgType — get its underlying type
                for (int i = 0; i < childCount; i++)
                {
                    uint childTypeId = (uint)Marshal.ReadInt32(findBuf, 8 + 4 * i);

                    // FunctionArgType → TI_GET_TYPE gives the actual parameter type
                    uint paramTypeId = childTypeId;
                    if (DbgHelpNative.SymGetTypeInfo(_hProcess, modBase, childTypeId,
                        DbgHelpNative.TI_GET_SYMTAG, buf))
                    {
                        uint childTag = (uint)Marshal.ReadInt32(buf);
                        if (childTag == DbgHelpNative.SymTagFunctionArgType)
                        {
                            if (DbgHelpNative.SymGetTypeInfo(_hProcess, modBase, childTypeId,
                                DbgHelpNative.TI_GET_TYPE, buf))
                                paramTypeId = (uint)Marshal.ReadInt32(buf);
                        }
                    }

                    string? typeName = ResolveTypeNameFromIndex(modBase, paramTypeId, 0);
                    // Generate placeholder name since public PDB has no param names
                    string paramName = $"a{i + 1}";
                    result.Add((paramName, typeName ?? "int64_t"));
                }
            }
            finally { Marshal.FreeHGlobal(findBuf); }
        }
        finally { Marshal.FreeHGlobal(buf); }

        return result;
    }

    /// <summary>
    /// Recursively resolve a PDB type index to a human-readable type name.
    /// Handles typedefs (HWND), pointers (PVOID), UDTs (structs), base types (int), etc.
    /// </summary>
    private string? ResolveTypeNameFromIndex(ulong modBase, uint typeIndex, int depth)
    {
        if (depth > 10 || typeIndex == 0) return null;

        // Get the tag (what kind of type is this?)
        IntPtr tagBuf = Marshal.AllocHGlobal(4);
        uint symTag;
        try
        {
            if (!DbgHelpNative.SymGetTypeInfo(_hProcess, modBase, typeIndex,
                DbgHelpNative.TI_GET_SYMTAG, tagBuf))
            {
                int err = Marshal.GetLastWin32Error();
                if (depth == 0)
                    LogMessage?.Invoke($"  ResolveType: TI_GET_SYMTAG FAILED typeIdx={typeIndex} err={err}");
                return null;
            }
            symTag = (uint)Marshal.ReadInt32(tagBuf);
            if (depth == 0)
                LogMessage?.Invoke($"  ResolveType: typeIdx={typeIndex} tag={symTag}");
        }
        finally { Marshal.FreeHGlobal(tagBuf); }

        switch (symTag)
        {
            case DbgHelpNative.SymTagTypedef:
            {
                // Typedef — get the typedef name (e.g., "HWND", "LPARAM", "BOOL")
                string? name = GetTypeSymName(modBase, typeIndex);
                if (name != null) return name;
                // Fallback: chase underlying type
                uint underType = GetUnderlyingTypeIndex(modBase, typeIndex);
                return ResolveTypeNameFromIndex(modBase, underType, depth + 1);
            }

            case DbgHelpNative.SymTagPointerType:
            {
                // Pointer — resolve pointee and append "*"
                uint pointeeType = GetUnderlyingTypeIndex(modBase, typeIndex);
                string? pointee = ResolveTypeNameFromIndex(modBase, pointeeType, depth + 1);
                if (pointee == null) return null;
                string ptrType = pointee + "*";
                // Map Windows handle pseudo-struct pointers to proper typedefs
                return HandleTypeMap.TryGetValue(ptrType, out string? handleName) ? handleName : ptrType;
            }

            case DbgHelpNative.SymTagBaseType:
            {
                // Basic type — map to C type name
                return ResolveBaseType(modBase, typeIndex);
            }

            case DbgHelpNative.SymTagUDT:
            case DbgHelpNative.SymTagEnum:
            {
                // Struct/union/enum — get name
                return GetTypeSymName(modBase, typeIndex);
            }

            case DbgHelpNative.SymTagArrayType:
            {
                uint elemType = GetUnderlyingTypeIndex(modBase, typeIndex);
                string? elem = ResolveTypeNameFromIndex(modBase, elemType, depth + 1);
                return elem != null ? elem + "[]" : null;
            }

            case DbgHelpNative.SymTagFunctionType:
                return null; // function pointers are too complex to represent simply

            default:
                return null;
        }
    }

    private string? GetTypeSymName(ulong modBase, uint typeIndex)
    {
        IntPtr nameBuf = Marshal.AllocHGlobal(8); // pointer-sized
        try
        {
            if (!DbgHelpNative.SymGetTypeInfo(_hProcess, modBase, typeIndex,
                DbgHelpNative.TI_GET_SYMNAME, nameBuf))
                return null;

            IntPtr namePtr = Marshal.ReadIntPtr(nameBuf);
            if (namePtr == IntPtr.Zero) return null;

            string? name = Marshal.PtrToStringUni(namePtr);
            DbgHelpNative.LocalFree(namePtr); // TI_GET_SYMNAME allocates with LocalAlloc
            return string.IsNullOrEmpty(name) ? null : name;
        }
        finally { Marshal.FreeHGlobal(nameBuf); }
    }

    private uint GetUnderlyingTypeIndex(ulong modBase, uint typeIndex)
    {
        IntPtr buf = Marshal.AllocHGlobal(4);
        try
        {
            if (!DbgHelpNative.SymGetTypeInfo(_hProcess, modBase, typeIndex,
                DbgHelpNative.TI_GET_TYPE, buf))
                return 0;
            return (uint)Marshal.ReadInt32(buf);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private string? ResolveBaseType(ulong modBase, uint typeIndex)
    {
        IntPtr btBuf = Marshal.AllocHGlobal(4);
        IntPtr lenBuf = Marshal.AllocHGlobal(8);
        try
        {
            if (!DbgHelpNative.SymGetTypeInfo(_hProcess, modBase, typeIndex,
                DbgHelpNative.TI_GET_BASETYPE, btBuf))
                return null;
            uint bt = (uint)Marshal.ReadInt32(btBuf);

            DbgHelpNative.SymGetTypeInfo(_hProcess, modBase, typeIndex,
                DbgHelpNative.TI_GET_LENGTH, lenBuf);
            ulong len = (ulong)Marshal.ReadInt64(lenBuf);

            return bt switch
            {
                DbgHelpNative.btVoid => "void",
                DbgHelpNative.btChar => "char",
                DbgHelpNative.btWChar => "wchar_t",
                DbgHelpNative.btBool => "BOOL",
                DbgHelpNative.btInt => len switch
                {
                    1 => "int8_t",
                    2 => "short",
                    4 => "int",
                    8 => "int64_t",
                    _ => $"int{len * 8}_t"
                },
                DbgHelpNative.btUInt => len switch
                {
                    1 => "uint8_t",
                    2 => "uint16_t",
                    4 => "uint32_t",
                    8 => "uint64_t",
                    _ => $"uint{len * 8}_t"
                },
                DbgHelpNative.btFloat => len == 4 ? "float" : "double",
                DbgHelpNative.btLong => len == 4 ? "LONG" : "int64_t",
                DbgHelpNative.btULong => len == 4 ? "ULONG" : "uint64_t",
                DbgHelpNative.btHresult => "HRESULT",
                _ => null
            };
        }
        finally
        {
            Marshal.FreeHGlobal(btBuf);
            Marshal.FreeHGlobal(lenBuf);
        }
    }

    public void ClearCache() => _symbolCache.Clear();

    /// <summary>
    /// Register a user-defined function name. Takes priority over PDB/SymFromAddr.
    /// </summary>
    public void RegisterFunction(ulong address, string? name, uint size = 0)
    {
        lock (_lock)
        {
            if (string.IsNullOrEmpty(name))
            {
                _userFunctions.Remove(address);
            }
            else
            {
                _userFunctions[address] = (name, size);
            }
            // Invalidate cache for this address range
            _symbolCache.Remove(address);
            if (size > 0)
            {
                var staleKeys = _symbolCache.Keys
                    .Where(a => a > address && a < address + size)
                    .ToList();
                foreach (var key in staleKeys)
                    _symbolCache.Remove(key);
            }
        }
    }

    public List<PluginFunctionEntry> GetRegisteredFunctions()
    {
        lock (_lock)
        {
            return _userFunctions.Select(kv => new PluginFunctionEntry
            {
                Address = kv.Key,
                Name = kv.Value.Name,
                Size = kv.Value.Size
            }).ToList();
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _symbolCache.Clear();
            if (_initialized)
            {
                foreach (var baseAddr in _loadedModules)
                    DbgHelpNative.SymUnloadModule64(_hProcess, baseAddr);
                _loadedModules.Clear();
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_initialized)
            {
                _loadedModules.Clear();
                DbgHelpNative.SymCleanup(_hProcess);
                _initialized = false;
            }
            _symbolCache.Clear();
        }
    }
}
