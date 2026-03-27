using System.IO;
using System.Text;
using KernelFlirt.SDK;

namespace PeRebuilder;

/// <summary>
/// Resolves addresses to API names by parsing export tables of loaded modules.
/// Handles JMP trampolines and forwarded exports (e.g. kernel32→ntdll).
/// </summary>
public sealed class ExportResolver
{
    private readonly IDebuggerApi _api;
    private readonly bool _is64;
    private readonly int _ptrSize;

    // module base → { rva → name }
    private readonly Dictionary<ulong, Dictionary<uint, string>> _exportCache = new();

    // address → (dllName, funcName)
    private readonly Dictionary<ulong, (string dll, string func)> _resolvedCache = new();

    // forward map: "ntdll.RtlAllocateHeap" → ("kernel32.dll", "HeapAlloc")
    private readonly Dictionary<string, (string dll, string name)> _forwardMap = new();

    private IReadOnlyList<PluginModuleInfo>? _modules;

    public ExportResolver(IDebuggerApi api)
    {
        _api   = api;
        _is64  = !api.Is32Bit;
        _ptrSize = _is64 ? 8 : 4;
    }

    /// <summary>Initialize module list and build forward map from host DLLs.</summary>
    public void Initialize()
    {
        _modules = _api.Symbols.GetModules();
        _exportCache.Clear();
        _resolvedCache.Clear();
        _forwardMap.Clear();
        CollectForwards();
    }

    public IReadOnlyList<PluginModuleInfo> Modules => _modules ?? (IReadOnlyList<PluginModuleInfo>)Array.Empty<PluginModuleInfo>();

    /// <summary>Find which module contains the given address.</summary>
    public PluginModuleInfo? FindModule(ulong address)
    {
        if (_modules == null) return null;
        foreach (var m in _modules)
            if (address >= m.BaseAddress && address < m.BaseAddress + m.Size)
                return m;
        return null;
    }

    /// <summary>Check if address is inside any loaded module (likely an API).</summary>
    public bool IsApiAddress(ulong address)
    {
        return FindModule(address) != null;
    }

    /// <summary>
    /// Resolve an address to (DllName, FunctionName).
    /// Follows trampolines and applies forward resolution.
    /// Returns null if not resolvable.
    /// </summary>
    public (string dll, string func)? Resolve(ulong address)
    {
        if (_resolvedCache.TryGetValue(address, out var cached))
            return cached;

        var mod = FindModule(address);
        if (mod == null) return null;

        // Try direct lookup in export table
        var exports = GetExports(mod.BaseAddress);
        uint rva = (uint)(address - mod.BaseAddress);
        if (exports.TryGetValue(rva, out string? name))
        {
            var result = ApplyForwards(mod.Name, name);
            _resolvedCache[address] = result;
            return result;
        }

        // Try unwrapping trampolines (JMP chains)
        ulong unwrapped = TryUnwrapTrampoline(address);
        if (unwrapped != 0 && unwrapped != address)
        {
            var mod2 = FindModule(unwrapped);
            if (mod2 != null)
            {
                var exports2 = GetExports(mod2.BaseAddress);
                uint rva2 = (uint)(unwrapped - mod2.BaseAddress);
                if (exports2.TryGetValue(rva2, out string? name2))
                {
                    var result = ApplyForwards(mod2.Name, name2);
                    _resolvedCache[address] = result;
                    return result;
                }
            }
        }

        // Fallback: use SDK symbol resolver
        string? symName = _api.Symbols.ResolveAddress(address);
        if (!string.IsNullOrEmpty(symName) && symName.Contains('!'))
        {
            var parts = symName.Split('!', 2);
            string dllName = parts[0];
            if (!dllName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                dllName += ".dll";
            var result = (dllName, parts[1]);
            _resolvedCache[address] = result;
            return result;
        }

        return null;
    }

    /// <summary>Get or parse export table for a module.</summary>
    public Dictionary<uint, string> GetExports(ulong moduleBase)
    {
        if (_exportCache.TryGetValue(moduleBase, out var cached))
            return cached;

        var exports = new Dictionary<uint, string>();
        _exportCache[moduleBase] = exports;

        try
        {
            byte[]? hdr = _api.Memory.ReadMemory(_api.TargetPid, moduleBase, 0x1000u);
            if (hdr == null || hdr.Length < 0x40) return exports;
            if (hdr[0] != 0x4D || hdr[1] != 0x5A) return exports;

            int lfanew = BitConverter.ToInt32(hdr, 0x3C);
            if (lfanew < 0 || lfanew + 0x18 > hdr.Length) return exports;

            ushort magic = BitConverter.ToUInt16(hdr, lfanew + 0x18);
            bool pe64 = magic == 0x20B;
            int ddBase = lfanew + 0x18 + (pe64 ? 0x70 : 0x60);

            if (ddBase + 8 > hdr.Length) return exports;
            uint expRva  = BitConverter.ToUInt32(hdr, ddBase);
            uint expSize = BitConverter.ToUInt32(hdr, ddBase + 4);
            if (expRva == 0 || expSize == 0) return exports;

            byte[]? expBuf = _api.Memory.ReadMemory(_api.TargetPid, moduleBase + expRva, expSize);
            if (expBuf == null || expBuf.Length < 40) return exports;

            uint numNames    = BitConverter.ToUInt32(expBuf, 24);
            uint addrTableRva = BitConverter.ToUInt32(expBuf, 28);
            uint nameTableRva = BitConverter.ToUInt32(expBuf, 32);
            uint ordTableRva  = BitConverter.ToUInt32(expBuf, 36);
            uint ordBase      = BitConverter.ToUInt32(expBuf, 16);
            uint numFuncs     = BitConverter.ToUInt32(expBuf, 20);

            int at = (int)(addrTableRva - expRva);
            int nt = (int)(nameTableRva - expRva);
            int ot = (int)(ordTableRva  - expRva);

            // Named exports
            for (uint i = 0; i < numNames; i++)
            {
                if (nt + i * 4 + 4 > expBuf.Length) break;
                if (ot + i * 2 + 2 > expBuf.Length) break;

                uint nameRva = BitConverter.ToUInt32(expBuf, (int)(nt + i * 4));
                ushort ord   = BitConverter.ToUInt16(expBuf, (int)(ot + i * 2));

                if (at + ord * 4 + 4 > expBuf.Length) continue;
                uint funcRva = BitConverter.ToUInt32(expBuf, (int)(at + ord * 4));

                int nameOff = (int)(nameRva - expRva);
                if (nameOff < 0 || nameOff >= expBuf.Length) continue;
                int end = nameOff;
                while (end < expBuf.Length && expBuf[end] != 0) end++;
                string funcName = Encoding.ASCII.GetString(expBuf, nameOff, end - nameOff);

                if (!exports.ContainsKey(funcRva))
                    exports[funcRva] = funcName;
            }

            // Ordinal-only exports
            for (uint i = 0; i < numFuncs; i++)
            {
                if (at + i * 4 + 4 > expBuf.Length) break;
                uint funcRva = BitConverter.ToUInt32(expBuf, (int)(at + i * 4));
                if (funcRva == 0) continue;
                if (!exports.ContainsKey(funcRva))
                    exports[funcRva] = $"#{ordBase + i}";
            }
        }
        catch { }

        return exports;
    }

    /// <summary>Follow JMP chains to find the real target (max 5 hops).</summary>
    public ulong TryUnwrapTrampoline(ulong addr)
    {
        for (int hop = 0; hop < 5; hop++)
        {
            byte[]? code;
            try { code = _api.Memory.ReadMemory(_api.TargetPid, addr, 16u); }
            catch { return addr; }
            if (code == null || code.Length < 5) return addr;

            // E9 rel32 — JMP near
            if (code[0] == 0xE9)
            {
                int rel = BitConverter.ToInt32(code, 1);
                addr = (ulong)((long)addr + 5 + rel);
                continue;
            }

            // FF 25 disp32 — JMP [RIP+disp32] (x64) or JMP [addr] (x86)
            if (code[0] == 0xFF && code[1] == 0x25)
            {
                if (_is64)
                {
                    int disp = BitConverter.ToInt32(code, 2);
                    ulong ptrAddr = (ulong)((long)addr + 6 + disp);
                    byte[]? ptr = _api.Memory.ReadMemory(_api.TargetPid, ptrAddr, 8u);
                    if (ptr == null || ptr.Length < 8) return addr;
                    addr = BitConverter.ToUInt64(ptr, 0);
                }
                else
                {
                    uint target = BitConverter.ToUInt32(code, 2);
                    byte[]? ptr = _api.Memory.ReadMemory(_api.TargetPid, target, 4u);
                    if (ptr == null || ptr.Length < 4) return addr;
                    addr = BitConverter.ToUInt32(ptr, 0);
                }
                continue;
            }

            // 48 FF 25 disp32 — REX.W JMP [RIP+disp32]
            if (_is64 && code.Length >= 7 && code[0] == 0x48 && code[1] == 0xFF && code[2] == 0x25)
            {
                int disp = BitConverter.ToInt32(code, 3);
                ulong ptrAddr = (ulong)((long)addr + 7 + disp);
                byte[]? ptr = _api.Memory.ReadMemory(_api.TargetPid, ptrAddr, 8u);
                if (ptr == null || ptr.Length < 8) return addr;
                addr = BitConverter.ToUInt64(ptr, 0);
                continue;
            }

            // 48 B8 imm64; FF E0 — mov rax, imm64; jmp rax
            if (_is64 && code.Length >= 12 && code[0] == 0x48 && code[1] == 0xB8 &&
                code[10] == 0xFF && code[11] == 0xE0)
            {
                addr = BitConverter.ToUInt64(code, 2);
                continue;
            }

            // 48 B8 imm64; FF D0 — mov rax, imm64; call rax (tail-call)
            if (_is64 && code.Length >= 12 && code[0] == 0x48 && code[1] == 0xB8 &&
                code[10] == 0xFF && code[11] == 0xD0)
            {
                addr = BitConverter.ToUInt64(code, 2);
                continue;
            }

            break; // Not a trampoline
        }
        return addr;
    }

    /// <summary>Apply forward resolution: e.g. ntdll.RtlAllocateHeap → kernel32.HeapAlloc</summary>
    private (string dll, string func) ApplyForwards(string dll, string func)
    {
        // Check if there's a better (more canonical) name via forward map
        string key = $"{Path.GetFileNameWithoutExtension(dll).ToLowerInvariant()}.{func}";
        if (_forwardMap.TryGetValue(key, out var fwd))
            return fwd;
        return (dll, func);
    }

    /// <summary>
    /// Build forward map from host system DLLs.
    /// Parses export tables of key DLLs to find forwarded exports.
    /// </summary>
    private void CollectForwards()
    {
        if (_modules == null) return;

        // Key DLLs that commonly forward exports
        string[] forwardSources = {
            "kernel32.dll", "kernelbase.dll", "ntdll.dll",
            "user32.dll", "gdi32.dll", "advapi32.dll",
            "ole32.dll", "shell32.dll", "ws2_32.dll",
            "combase.dll", "sechost.dll", "ucrtbase.dll"
        };

        foreach (var dllName in forwardSources)
        {
            var mod = _modules.FirstOrDefault(m =>
                m.Name.Equals(dllName, StringComparison.OrdinalIgnoreCase));
            if (mod == null) continue;

            try
            {
                byte[]? hdr = _api.Memory.ReadMemory(_api.TargetPid, mod.BaseAddress, 0x1000u);
                if (hdr == null || hdr.Length < 0x40 || hdr[0] != 0x4D) continue;

                int lfanew = BitConverter.ToInt32(hdr, 0x3C);
                ushort magic = BitConverter.ToUInt16(hdr, lfanew + 0x18);
                bool pe64 = magic == 0x20B;
                int ddBase = lfanew + 0x18 + (pe64 ? 0x70 : 0x60);

                uint expRva  = BitConverter.ToUInt32(hdr, ddBase);
                uint expSize = BitConverter.ToUInt32(hdr, ddBase + 4);
                if (expRva == 0 || expSize == 0) continue;

                byte[]? expBuf = _api.Memory.ReadMemory(_api.TargetPid, mod.BaseAddress + expRva, expSize);
                if (expBuf == null || expBuf.Length < 40) continue;

                uint numNames     = BitConverter.ToUInt32(expBuf, 24);
                uint addrTableRva = BitConverter.ToUInt32(expBuf, 28);
                uint nameTableRva = BitConverter.ToUInt32(expBuf, 32);
                uint ordTableRva  = BitConverter.ToUInt32(expBuf, 36);

                int at = (int)(addrTableRva - expRva);
                int nt = (int)(nameTableRva - expRva);
                int ot = (int)(ordTableRva  - expRva);

                for (uint i = 0; i < numNames; i++)
                {
                    if (nt + i * 4 + 4 > expBuf.Length) break;
                    if (ot + i * 2 + 2 > expBuf.Length) break;

                    uint nameRva = BitConverter.ToUInt32(expBuf, (int)(nt + i * 4));
                    ushort ord   = BitConverter.ToUInt16(expBuf, (int)(ot + i * 2));
                    if (at + ord * 4 + 4 > expBuf.Length) continue;
                    uint funcRva = BitConverter.ToUInt32(expBuf, (int)(at + ord * 4));

                    // Forwarded if funcRva is within export directory
                    if (funcRva < expRva || funcRva >= expRva + expSize) continue;

                    int nameOff = (int)(nameRva - expRva);
                    if (nameOff < 0 || nameOff >= expBuf.Length) continue;
                    int end = nameOff;
                    while (end < expBuf.Length && expBuf[end] != 0) end++;
                    string exportName = Encoding.ASCII.GetString(expBuf, nameOff, end - nameOff);

                    int fwdOff = (int)(funcRva - expRva);
                    if (fwdOff < 0 || fwdOff >= expBuf.Length) continue;
                    int fwdEnd = fwdOff;
                    while (fwdEnd < expBuf.Length && expBuf[fwdEnd] != 0) fwdEnd++;
                    string forwardStr = Encoding.ASCII.GetString(expBuf, fwdOff, fwdEnd - fwdOff);

                    // forwardStr = "ntdll.RtlAllocateHeap" or "api-ms-win-core-heap-l1-1-0.HeapAlloc"
                    int dot = forwardStr.IndexOf('.');
                    if (dot <= 0) continue;

                    string fwdDll  = forwardStr[..dot].ToLowerInvariant();
                    string fwdFunc = forwardStr[(dot + 1)..];

                    // Map: targetDll.funcName → (sourceDll, exportName)
                    // e.g. "ntdll.RtlAllocateHeap" → ("kernel32.dll", "HeapAlloc")
                    string fwdKey = $"{fwdDll}.{fwdFunc}";
                    if (!_forwardMap.ContainsKey(fwdKey))
                        _forwardMap[fwdKey] = (dllName, exportName);
                }
            }
            catch { }
        }
    }
}
