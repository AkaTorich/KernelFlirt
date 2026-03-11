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

            DbgHelpNative.SymSetOptions(
                DbgHelpNative.SYMOPT_UNDNAME |
                DbgHelpNative.SYMOPT_DEFERRED_LOADS |
                DbgHelpNative.SYMOPT_FAVOR_COMPRESSED |
                DbgHelpNative.SYMOPT_DEBUG);

            if (!DbgHelpNative.SymInitializeW(_hProcess, _symbolPath, false))
            {
                int err = Marshal.GetLastWin32Error();
                return $"SymInitialize failed (error {err})";
            }

            // Register callback to capture dbghelp debug output
            _callbackDelegate = OnDbgHelpCallback;
            DbgHelpNative.SymRegisterCallbackW64(_hProcess, _callbackDelegate, 0);

            _initialized = true;
            return null;
        }
    }

    /// <summary>
    /// Load a module with debug info read from target memory.
    /// pid=0 for kernel modules.
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
            if (isSessionSpace)
            {
                LogMessage?.Invoke($"  {moduleName}: session-space module, skipping PE read");
            }
            else
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
                LogMessage?.Invoke($"  {moduleName}: RSDS {rsds.Guid} age={rsds.Age} pdb='{rsds.PdbName}'");
                pdbPath = FindPdb(rsds);
                if (pdbPath != null)
                    LogMessage?.Invoke($"  {moduleName}: PDB found: {pdbPath}");
                else
                    LogMessage?.Invoke($"  {moduleName}: PDB not found on symbol server");
            }

            // Load module — if we have a local PDB, pass its path as ImageName
            // so dbghelp associates the PDB with this module
            string imageName = pdbPath ?? moduleName;
            ulong result = DbgHelpNative.SymLoadModuleExW(
                _hProcess, IntPtr.Zero, imageName, null,
                baseAddress, size, IntPtr.Zero, 0);
            int err = Marshal.GetLastWin32Error();

            bool ok = result != 0 || err == 0;
            LogMessage?.Invoke($"  SymLoadModuleExW('{imageName}') -> 0x{result:X}, err={err}");

            if (ok)
            {
                _loadedModules.Add(baseAddress);
                return true;
            }

            LogMessage?.Invoke($"  {moduleName}: FAILED err={err}");
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
    /// Returns local path to PDB or null if not found.
    /// </summary>
    private string? FindPdb(RsdsInfo rsds)
    {
        // Allocate buffer for GUID
        IntPtr guidPtr = Marshal.AllocHGlobal(16);
        // Allocate buffer for result path (MAX_PATH * 2 for Unicode)
        IntPtr pathBuf = Marshal.AllocHGlobal(260 * 2);
        try
        {
            // Write GUID bytes
            byte[] guidBytes = rsds.Guid.ToByteArray();
            Marshal.Copy(guidBytes, 0, guidPtr, 16);

            // Zero the path buffer
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
            else
            {
                int err = Marshal.GetLastWin32Error();
                LogMessage?.Invoke($"    SymFindFileInPath failed err={err}");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(guidPtr);
            Marshal.FreeHGlobal(pathBuf);
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

    private bool OnDbgHelpCallback(IntPtr hProcess, uint actionCode, ulong callbackData, ulong userContext)
    {
        if (actionCode == DbgHelpNative.CBA_DEBUG_INFO && callbackData != 0)
        {
            try
            {
                string? msg = Marshal.PtrToStringUni((IntPtr)callbackData);
                if (!string.IsNullOrWhiteSpace(msg))
                    LogMessage?.Invoke($"  [dbghelp] {msg.TrimEnd('\r', '\n')}");
            }
            catch { }
        }
        return false;
    }

    private static bool IsSessionSpaceModule(string moduleName)
    {
        var name = Path.GetFileName(moduleName);
        return SessionSpaceModules.Contains(name);
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
