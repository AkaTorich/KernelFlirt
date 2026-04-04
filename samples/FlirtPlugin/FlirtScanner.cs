using KernelFlirt.SDK;

namespace FlirtPlugin;

/// <summary>
/// Result of matching a FLIRT signature against a function in memory.
/// </summary>
public sealed class FlirtMatch
{
    public string FunctionName { get; init; } = "";
    public ulong Address { get; init; }
    public string ModuleName { get; init; } = "";
    public int PatternLength { get; init; }
    public bool AlreadyHasSymbol { get; init; }
}

/// <summary>
/// Scans process modules for FLIRT signature matches.
/// Discovers function entry points via .pdata (x64 RUNTIME_FUNCTION) or prologue scanning (x86),
/// reads their first bytes, and matches against the signature index.
/// </summary>
public sealed class FlirtScanner
{
    private readonly IDebuggerApi _api;
    private readonly PatSignatureIndex _index;

    public FlirtScanner(IDebuggerApi api, PatSignatureIndex index)
    {
        _api = api;
        _index = index;
    }

    /// <summary>Scan the main (first) module.</summary>
    public List<FlirtMatch> ScanMainModule(Action<int, int>? progress = null)
    {
        if (!_api.IsConnected || !_api.IsBreakState) return [];

        var modules = _api.Symbols.GetModules();
        if (modules == null || modules.Count == 0) return [];

        return ScanModule(modules[0], progress);
    }

    /// <summary>Scan all loaded user-mode modules.</summary>
    public List<FlirtMatch> ScanAllModules(Action<int, int>? progress = null)
    {
        if (!_api.IsConnected || !_api.IsBreakState) return [];

        var modules = _api.Symbols.GetModules();
        if (modules == null) return [];

        var results = new List<FlirtMatch>();
        int totalDone = 0;
        int totalFunctions = 0;

        // First pass: estimate total function count for progress
        foreach (var mod in modules)
            totalFunctions += EstimateFunctionCount(mod);

        foreach (var mod in modules)
        {
            var captured = totalDone;
            var matches = ScanModule(mod, (current, _) =>
                progress?.Invoke(captured + current, totalFunctions));
            results.AddRange(matches);
            totalDone += matches.Count > 0 ? matches.Count : 0;
        }

        progress?.Invoke(totalFunctions, totalFunctions);
        return results;
    }

    private List<FlirtMatch> ScanModule(PluginModuleInfo module, Action<int, int>? progress)
    {
        var results = new List<FlirtMatch>();
        var pid = _api.TargetPid;
        var baseAddr = module.BaseAddress;

        // Read PE headers (4KB covers all headers + section table)
        var headers = _api.Memory.ReadMemory(pid, baseAddr, 0x1000);
        if (headers == null || headers.Length < 0x40) return results;

        // Validate MZ + PE
        if (headers[0] != 'M' || headers[1] != 'Z') return results;
        uint peOffset = BitConverter.ToUInt32(headers, 0x3C);
        if (peOffset + 0x18 > headers.Length) return results;
        if (headers[peOffset] != 'P' || headers[peOffset + 1] != 'E') return results;

        ushort magic = BitConverter.ToUInt16(headers, (int)peOffset + 0x18);
        bool is64 = magic == 0x20B;

        // Get section info for bulk reads
        int sectionCount = BitConverter.ToUInt16(headers, (int)peOffset + 6);
        int sectionTableOffset = is64
            ? (int)peOffset + 24 + 240  // PE + COFF header(24) + optional header64(240)
            : (int)peOffset + 24 + 224; // PE + COFF header(24) + optional header32(224)

        // Collect executable sections (bulk read to avoid per-function kernel round-trips)
        var execSections = new List<(uint rva, uint vsize, byte[]? data)>();
        for (int i = 0; i < sectionCount; i++)
        {
            int off = sectionTableOffset + i * 40;
            if (off + 40 > headers.Length) break;

            uint characteristics = BitConverter.ToUInt32(headers, off + 36);
            if ((characteristics & 0x20000000) == 0) continue; // IMAGE_SCN_MEM_EXECUTE

            uint sectRva = BitConverter.ToUInt32(headers, off + 12);
            uint sectVSize = BitConverter.ToUInt32(headers, off + 8);

            // Cap reads at 4MB per section
            uint readSize = Math.Min(sectVSize, 0x400000);
            var data = _api.Memory.ReadMemory(pid, baseAddr + sectRva, readSize);
            execSections.Add((sectRva, sectVSize, data));
        }

        // Get function entry points
        List<uint> functionRvas;

        if (is64)
            functionRvas = ParsePdataFunctions(pid, baseAddr, headers, (int)peOffset);
        else
            functionRvas = ScanPrologues32(execSections);

        // Match each function against the signature index
        int total = functionRvas.Count;
        int done = 0;

        foreach (uint funcRva in functionRvas)
        {
            if (done % 200 == 0)
                progress?.Invoke(done, total);
            done++;

            // Find which section contains this function and extract bytes
            byte[]? funcBytes = ExtractFunctionBytes(funcRva, execSections);
            if (funcBytes == null) continue;

            var match = _index.Match(funcBytes);
            if (match == null) continue;

            ulong funcVa = baseAddr + funcRva;
            var existingSymbol = _api.Symbols.ResolveAddress(funcVa);

            results.Add(new FlirtMatch
            {
                FunctionName = match.Name,
                Address = funcVa,
                ModuleName = module.Name,
                PatternLength = match.LeadingPattern.Length,
                AlreadyHasSymbol = !string.IsNullOrEmpty(existingSymbol)
            });
        }

        progress?.Invoke(total, total);
        return results;
    }

    /// <summary>
    /// Extract up to 64 bytes from the section buffer at the given RVA.
    /// </summary>
    private static byte[]? ExtractFunctionBytes(uint funcRva,
        List<(uint rva, uint vsize, byte[]? data)> sections)
    {
        foreach (var (sectRva, sectVSize, sectData) in sections)
        {
            if (sectData == null) continue;
            if (funcRva < sectRva || funcRva >= sectRva + sectVSize) continue;

            int offset = (int)(funcRva - sectRva);
            int available = sectData.Length - offset;
            if (available < 4) return null;

            int len = Math.Min(available, 64);
            var bytes = new byte[len];
            Array.Copy(sectData, offset, bytes, 0, len);
            return bytes;
        }
        return null;
    }

    /// <summary>
    /// Parse .pdata (IMAGE_RUNTIME_FUNCTION_ENTRY) to get x64 function RVAs.
    /// Each entry is 12 bytes: BeginAddress(4) EndAddress(4) UnwindData(4).
    /// </summary>
    private List<uint> ParsePdataFunctions(uint pid, ulong baseAddr, byte[] headers, int peOffset)
    {
        var rvas = new List<uint>();

        // Exception directory is data dir entry #3
        // x64 optional header: data dirs start at PE+0x88, entry #3 at PE+0xA0
        int exceptDirOffset = peOffset + 0x88 + 3 * 8;
        if (exceptDirOffset + 8 > headers.Length) return rvas;

        uint exceptRva = BitConverter.ToUInt32(headers, exceptDirOffset);
        uint exceptSize = BitConverter.ToUInt32(headers, exceptDirOffset + 4);
        if (exceptRva == 0 || exceptSize == 0) return rvas;

        // Read .pdata section from process memory (may be outside the 4KB header read)
        var pdataBytes = _api.Memory.ReadMemory(pid, baseAddr + exceptRva,
            Math.Min(exceptSize, 0x100000)); // cap at 1MB
        if (pdataBytes == null) return rvas;

        int entryCount = pdataBytes.Length / 12;
        rvas.Capacity = entryCount;

        for (int i = 0; i < entryCount; i++)
        {
            int off = i * 12;
            if (off + 4 > pdataBytes.Length) break;

            uint beginRva = BitConverter.ToUInt32(pdataBytes, off);
            if (beginRva != 0)
                rvas.Add(beginRva);
        }

        return rvas;
    }

    /// <summary>
    /// Scan executable sections for common x86 function prologues.
    /// </summary>
    private static List<uint> ScanPrologues32(List<(uint rva, uint vsize, byte[]? data)> sections)
    {
        var rvas = new List<uint>();

        foreach (var (sectRva, _, sectData) in sections)
        {
            if (sectData == null || sectData.Length < 3) continue;

            for (int i = 0; i <= sectData.Length - 5; i++)
            {
                // push ebp; mov ebp, esp (55 8B EC)
                if (sectData[i] == 0x55 && sectData[i + 1] == 0x8B && sectData[i + 2] == 0xEC)
                {
                    rvas.Add(sectRva + (uint)i);
                    i += 2;
                    continue;
                }

                // mov edi, edi; push ebp; mov ebp, esp (8B FF 55 8B EC) — hotpatch
                if (i <= sectData.Length - 5 &&
                    sectData[i] == 0x8B && sectData[i + 1] == 0xFF &&
                    sectData[i + 2] == 0x55 && sectData[i + 3] == 0x8B && sectData[i + 4] == 0xEC)
                {
                    rvas.Add(sectRva + (uint)i);
                    i += 4;
                }
            }
        }

        return rvas;
    }

    /// <summary>
    /// Estimate function count for progress bar (without full scan).
    /// </summary>
    private int EstimateFunctionCount(PluginModuleInfo module)
    {
        var headers = _api.Memory.ReadMemory(_api.TargetPid, module.BaseAddress, 0x1000);
        if (headers == null || headers.Length < 0x40) return 0;
        if (headers[0] != 'M' || headers[1] != 'Z') return 0;

        uint peOffset = BitConverter.ToUInt32(headers, 0x3C);
        if (peOffset + 0x18 > headers.Length) return 0;

        ushort magic = BitConverter.ToUInt16(headers, (int)peOffset + 0x18);
        if (magic == 0x20B) // PE32+
        {
            int exceptDirOffset = (int)peOffset + 0x88 + 3 * 8;
            if (exceptDirOffset + 8 > headers.Length) return 0;
            uint exceptSize = BitConverter.ToUInt32(headers, exceptDirOffset + 4);
            return (int)(exceptSize / 12);
        }

        return 500; // rough estimate for x86
    }
}
