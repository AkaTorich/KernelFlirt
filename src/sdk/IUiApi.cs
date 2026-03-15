namespace KernelFlirt.SDK;

public interface IUiApi
{
    void NavigateDisassembly(ulong address);
    void AddMenuItem(string header, Action callback);
    void AddToolPanel(string title, object wpfContent);

    /// <summary>
    /// Add a dynamically unpacked PE as a virtual module and refresh all views
    /// (sections, imports, strings, functions).
    /// </summary>
    void AddUnpackedModule(ulong peBase, string name);

    /// <summary>
    /// Force refresh the module list and sections tab.
    /// </summary>
    void RefreshModulesAndSections();

    /// <summary>
    /// Provide section entries for a module directly (bypasses PE header parsing).
    /// Use when the PE header is zeroed by a packer (anti-dump).
    /// </summary>
    void AddModuleSections(string moduleName, IReadOnlyList<PluginSectionInfo> sections);
}
