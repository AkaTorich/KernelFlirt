namespace KernelFlirt.SDK;

public interface ISymbolApi
{
    string? ResolveAddress(ulong address);
    ulong ResolveNameToAddress(string name);
    IReadOnlyList<PluginModuleInfo> GetModules();
    IReadOnlyList<PluginKernelModuleInfo> GetKernelModules();

    /// <summary>
    /// Register a user-defined function name at the given address.
    /// ResolveAddress will return this name for the address and addresses within the function range.
    /// Set name to null to unregister.
    /// </summary>
    void RegisterFunction(ulong address, string? name, uint size = 0);
}
