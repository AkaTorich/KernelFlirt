using System.IO;
using System.Runtime.InteropServices;
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
                return true;
            }

            LogMessage?.Invoke($"  {moduleName}: SymLoadModuleExW FAILED err={err}");
            return false;
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

        // Try dbghelp first
        if (_initialized)
            result = ResolveViaDbgHelp(address);

        // Fallback: module+offset
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

        if (result != null)
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
            return null;
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
        }
        return results;
    }

    public void ClearCache() => _symbolCache.Clear();

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
