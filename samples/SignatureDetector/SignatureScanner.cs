using KernelFlirt.SDK;

namespace SignatureDetector;

/// <summary>
/// Result of a signature scan against a PE in memory.
/// </summary>
public sealed class ScanResult
{
    public string SignatureName { get; init; } = "";
    public ulong MatchAddress { get; init; }
    public string ModuleName { get; init; } = "";
    public bool AtEntryPoint { get; init; }
    public int PatternLength { get; init; }
}

/// <summary>
/// Scans process memory for PEiD signatures.
/// </summary>
public sealed class SignatureScanner
{
    private readonly IDebuggerApi _api;
    private readonly List<PeidSignature> _db;

    public SignatureScanner(IDebuggerApi api, List<PeidSignature> db)
    {
        _api = api;
        _db = db;
    }

    /// <summary>
    /// Scan the main module (or all modules) for matching signatures.
    /// </summary>
    public List<ScanResult> ScanMainModule()
    {
        var results = new List<ScanResult>();
        if (!_api.IsConnected || !_api.IsBreakState) return results;

        var modules = _api.Symbols.GetModules();
        if (modules == null || modules.Count == 0) return results;

        // Main module is first in list
        var main = modules[0];
        ScanModule(main, results);
        return results;
    }

    /// <summary>
    /// Scan all loaded modules.
    /// </summary>
    public List<ScanResult> ScanAllModules()
    {
        var results = new List<ScanResult>();
        if (!_api.IsConnected || !_api.IsBreakState) return results;

        var modules = _api.Symbols.GetModules();
        if (modules == null) return results;

        foreach (var m in modules)
            ScanModule(m, results);

        return results;
    }

    private void ScanModule(PluginModuleInfo module, List<ScanResult> results)
    {
        var baseAddr = module.BaseAddress;
        var pid = _api.TargetPid;

        // Read PE headers to find entry point and sections
        var headerBytes = _api.Memory.ReadMemory(pid, baseAddr, 0x1000);
        if (headerBytes == null || headerBytes.Length < 0x40) return;

        // Parse PE
        if (headerBytes[0] != 'M' || headerBytes[1] != 'Z') return;
        int peOff = BitConverter.ToInt32(headerBytes, 0x3C);
        if (peOff < 0 || peOff + 6 > headerBytes.Length) return;
        if (BitConverter.ToInt32(headerBytes, peOff) != 0x4550) return;

        bool is64 = BitConverter.ToUInt16(headerBytes, peOff + 4) == 0x8664;
        int optOff = peOff + 24;
        uint epRva;
        int sectCount = BitConverter.ToUInt16(headerBytes, peOff + 6);
        int sectOff;

        if (is64)
        {
            epRva = BitConverter.ToUInt32(headerBytes, optOff + 16);
            sectOff = optOff + 240; // sizeof IMAGE_OPTIONAL_HEADER64
        }
        else
        {
            epRva = BitConverter.ToUInt32(headerBytes, optOff + 16);
            sectOff = optOff + 224; // sizeof IMAGE_OPTIONAL_HEADER32
        }

        ulong epVa = baseAddr + epRva;

        // Read entry point region for ep_only signatures
        byte[]? epBytes = null;
        if (epRva != 0)
        {
            epBytes = _api.Memory.ReadMemory(pid, epVa, 512);
        }

        // Match ep_only signatures against entry point bytes
        if (epBytes != null && epBytes.Length > 0)
        {
            foreach (var sig in _db)
            {
                if (!sig.EpOnly) continue;
                if (MatchAt(epBytes, 0, sig.Pattern))
                {
                    results.Add(new ScanResult
                    {
                        SignatureName = sig.Name,
                        MatchAddress = epVa,
                        ModuleName = module.Name,
                        AtEntryPoint = true,
                        PatternLength = sig.Pattern.Length
                    });
                }
            }
        }

        // Collect non-ep_only signatures
        var scanSigs = _db.Where(s => !s.EpOnly).ToList();
        if (scanSigs.Count == 0) return;

        // Scan each executable section for non-ep signatures
        for (int i = 0; i < sectCount; i++)
        {
            int off = sectOff + i * 40;
            if (off + 40 > headerBytes.Length) break;

            uint characteristics = BitConverter.ToUInt32(headerBytes, off + 36);
            // Only scan executable sections
            if ((characteristics & 0x20000000) == 0) continue;

            uint sectRva = BitConverter.ToUInt32(headerBytes, off + 12);
            uint sectVSize = BitConverter.ToUInt32(headerBytes, off + 8);
            if (sectVSize > 0x100000) sectVSize = 0x100000; // cap at 1 MB

            var sectData = _api.Memory.ReadMemory(pid, baseAddr + sectRva, sectVSize);
            if (sectData == null || sectData.Length < 4) continue;

            foreach (var sig in scanSigs)
            {
                for (int j = 0; j <= sectData.Length - sig.Pattern.Length; j++)
                {
                    if (MatchAt(sectData, j, sig.Pattern))
                    {
                        results.Add(new ScanResult
                        {
                            SignatureName = sig.Name,
                            MatchAddress = baseAddr + sectRva + (ulong)j,
                            ModuleName = module.Name,
                            AtEntryPoint = (baseAddr + sectRva + (ulong)j) == epVa,
                            PatternLength = sig.Pattern.Length
                        });
                        break; // one match per sig per section is enough
                    }
                }
            }
        }
    }

    private static bool MatchAt(byte[] data, int offset, short[] pattern)
    {
        if (offset + pattern.Length > data.Length) return false;
        for (int i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] < 0) continue; // wildcard
            if (data[offset + i] != (byte)pattern[i]) return false;
        }
        return true;
    }
}
