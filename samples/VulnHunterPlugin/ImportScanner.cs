using System.Text;
using KernelFlirt.SDK;

namespace VulnHunterPlugin;

/// <summary>
/// Scans PE import tables of loaded modules to find calls to dangerous sink functions.
/// Reads PE headers directly from process memory.
/// </summary>
public class ImportScanner
{
    private readonly IDebuggerApi _api;

    // All dangerous function names for fast lookup (case-insensitive)
    private readonly Dictionary<string, SinkDef> _sinkLookup;

    public ImportScanner(IDebuggerApi api)
    {
        _api = api;

        _sinkLookup = new(StringComparer.OrdinalIgnoreCase);
        foreach (var sink in SinkDatabase.Sinks)
        {
            // Key by function name only — we'll match module separately
            if (!_sinkLookup.ContainsKey(sink.Function))
                _sinkLookup[sink.Function] = sink;
        }
    }

    /// <summary>
    /// Scan a single module's import table for dangerous function calls.
    /// </summary>
    public List<ScanResult> ScanModule(uint pid, PluginModuleInfo module)
    {
        var results = new List<ScanResult>();

        try
        {
            // Read DOS header
            var dosHeader = _api.Memory.ReadMemory(pid, module.BaseAddress, 64);
            if (dosHeader == null || dosHeader.Length < 64) return results;
            if (dosHeader[0] != 0x4D || dosHeader[1] != 0x5A) return results; // MZ check

            uint peOffset = BitConverter.ToUInt32(dosHeader, 0x3C);
            if (peOffset > 0x1000) return results;

            // Read PE header (enough for optional header + data directories)
            var peHeader = _api.Memory.ReadMemory(pid, module.BaseAddress + peOffset, 0x200);
            if (peHeader == null || peHeader.Length < 0x88) return results;
            if (peHeader[0] != 0x50 || peHeader[1] != 0x45) return results; // PE\0\0 check

            ushort magic = BitConverter.ToUInt16(peHeader, 0x18);
            bool isPe32Plus = magic == 0x20B; // PE32+
            bool isPe32 = magic == 0x10B;
            if (!isPe32Plus && !isPe32) return results;

            // Import directory RVA and size
            int importDirOffset = isPe32Plus ? 0x18 + 0x78 : 0x18 + 0x68; // DataDirectory[1]
            if (peHeader.Length < importDirOffset + 8) return results;

            uint importRva = BitConverter.ToUInt32(peHeader, importDirOffset);
            uint importSize = BitConverter.ToUInt32(peHeader, importDirOffset + 4);
            if (importRva == 0 || importSize == 0) return results;

            // Read import directory table
            ulong importAddr = module.BaseAddress + importRva;
            uint readSize = Math.Min(importSize, 0x4000); // Cap read size
            var importData = _api.Memory.ReadMemory(pid, importAddr, readSize);
            if (importData == null) return results;

            // Walk IMAGE_IMPORT_DESCRIPTORs (20 bytes each)
            int descriptorSize = 20;
            int count = (int)(readSize / descriptorSize);

            for (int i = 0; i < count; i++)
            {
                int off = i * descriptorSize;
                if (off + descriptorSize > importData.Length) break;

                uint originalFirstThunk = BitConverter.ToUInt32(importData, off + 0);  // INT
                uint firstThunk = BitConverter.ToUInt32(importData, off + 16);          // IAT
                uint nameRva = BitConverter.ToUInt32(importData, off + 12);

                // End of import descriptors
                if (nameRva == 0 && firstThunk == 0) break;
                if (nameRva == 0) continue;

                // Read DLL name
                string dllName = ReadAsciiString(pid, module.BaseAddress + nameRva, 128);
                if (string.IsNullOrEmpty(dllName)) continue;

                // Walk thunks (INT preferred, fallback to IAT)
                uint thunkRva = originalFirstThunk != 0 ? originalFirstThunk : firstThunk;
                int thunkSize = isPe32Plus ? 8 : 4;
                int maxThunks = 4096;

                for (int t = 0; t < maxThunks; t++)
                {
                    ulong thunkAddr = module.BaseAddress + thunkRva + (ulong)(t * thunkSize);
                    var thunkData = _api.Memory.ReadMemory(pid, thunkAddr, (uint)thunkSize);
                    if (thunkData == null) break;

                    ulong thunkValue = isPe32Plus
                        ? BitConverter.ToUInt64(thunkData, 0)
                        : BitConverter.ToUInt32(thunkData, 0);

                    if (thunkValue == 0) break; // End of thunk array

                    // Check ordinal import (MSB set)
                    bool isOrdinal = isPe32Plus
                        ? (thunkValue & 0x8000000000000000) != 0
                        : (thunkValue & 0x80000000) != 0;
                    if (isOrdinal) continue;

                    // Read IMAGE_IMPORT_BY_NAME: skip 2-byte hint, read name
                    uint hintNameRva = (uint)(thunkValue & 0x7FFFFFFF);
                    string funcName = ReadAsciiString(pid, module.BaseAddress + hintNameRva + 2, 128);
                    if (string.IsNullOrEmpty(funcName)) continue;

                    // Check against our sink database
                    if (_sinkLookup.TryGetValue(funcName, out var sink))
                    {
                        ulong iatEntry = module.BaseAddress + firstThunk + (ulong)(t * thunkSize);
                        results.Add(new ScanResult
                        {
                            Address = iatEntry,
                            CallerModule = module.Name,
                            Function = funcName,
                            TargetModule = dllName,
                            Danger = sink.Danger,
                            Description = sink.Description
                        });
                    }
                }
            }
        }
        catch
        {
            // Corrupted PE or paged-out memory — skip silently
        }

        return results;
    }

    /// <summary>
    /// Scan all loaded non-system modules.
    /// </summary>
    public List<ScanResult> ScanAllModules(uint pid)
    {
        var results = new List<ScanResult>();
        var modules = _api.Symbols.GetModules();

        foreach (var mod in modules)
        {
            // Skip CRT/system DLLs — we want to find callers, not the sinks themselves
            string name = mod.Name.ToLowerInvariant();
            if (IsSystemModule(name)) continue;

            results.AddRange(ScanModule(pid, mod));
        }

        return results;
    }

    /// <summary>
    /// Scan only the main executable module.
    /// </summary>
    public List<ScanResult> ScanMainModule(uint pid)
    {
        var modules = _api.Symbols.GetModules();
        if (modules.Count == 0) return [];

        // First module is typically the main EXE
        return ScanModule(pid, modules[0]);
    }

    private static bool IsSystemModule(string name)
    {
        return name.StartsWith("ntdll") ||
               name.StartsWith("kernel32") || name.StartsWith("kernelbase") ||
               name.StartsWith("msvcrt") || name.StartsWith("ucrtbase") ||
               name.StartsWith("vcruntime") || name.StartsWith("api-ms-") ||
               name.StartsWith("user32") || name.StartsWith("ws2_32") ||
               name.StartsWith("advapi32") || name.StartsWith("combase") ||
               name.StartsWith("rpcrt4") || name.StartsWith("sechost") ||
               name.StartsWith("bcrypt") || name.StartsWith("gdi32");
    }

    private string ReadAsciiString(uint pid, ulong address, int maxLen)
    {
        var data = _api.Memory.ReadMemory(pid, address, (uint)maxLen);
        if (data == null) return "";

        int len = Array.IndexOf(data, (byte)0);
        if (len < 0) len = data.Length;
        return Encoding.ASCII.GetString(data, 0, len);
    }
}
