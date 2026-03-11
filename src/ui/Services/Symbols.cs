using KernelFlirt.UI.Models;

namespace KernelFlirt.UI.Services;

/// <summary>
/// Resolves symbols from PE export tables.
/// </summary>
public class SymbolService
{
    private readonly Dictionary<ulong, string> _symbolCache = new();
    private readonly DriverComm _debugger;

    public SymbolService(DriverComm debugger)
    {
        _debugger = debugger;
    }

    public string? ResolveAddress(uint pid, ulong address, List<ModuleInfo> modules)
    {
        if (_symbolCache.TryGetValue(address, out var cached))
            return cached;

        var module = modules.FirstOrDefault(m =>
            address >= m.BaseAddress && address < m.BaseAddress + m.Size);

        if (module == null)
            return null;

        ulong offset = address - module.BaseAddress;
        string symbol = $"{module.Name}+0x{offset:X}";

        _symbolCache[address] = symbol;
        return symbol;
    }

    public void ClearCache() => _symbolCache.Clear();
}
