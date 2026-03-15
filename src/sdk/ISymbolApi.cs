namespace KernelFlirt.SDK;

public interface ISymbolApi
{
    string? ResolveAddress(ulong address);
    ulong ResolveNameToAddress(string name);
    IReadOnlyList<PluginModuleInfo> GetModules();
    IReadOnlyList<PluginKernelModuleInfo> GetKernelModules();
}
