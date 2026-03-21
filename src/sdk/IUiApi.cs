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

    /// <summary>
    /// Request decompilation of the function at the given address.
    /// This is async — call GetDecompiledCode() after a delay to get the result.
    /// </summary>
    void DecompileFunction(ulong address);

    /// <summary>
    /// Get the current decompiled code text (C pseudocode from RetDec).
    /// Returns empty string if no decompilation has been done.
    /// </summary>
    string GetDecompiledCode();

    /// <summary>
    /// Go back to previous disassembly location (undo NavigateDisassembly).
    /// </summary>
    void DisasmGoBack();
}
