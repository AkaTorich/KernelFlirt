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

    /// <summary>
    /// Set a text annotation for an address. Shown as "; comment" in the disassembly view.
    /// If annotation is null or empty, removes the annotation.
    /// </summary>
    void SetAddressAnnotation(ulong address, string? annotation);

    /// <summary>
    /// Get the annotation for an address, or null if none.
    /// </summary>
    string? GetAddressAnnotation(ulong address);

    /// <summary>
    /// Get all address annotations as a dictionary.
    /// </summary>
    IReadOnlyDictionary<ulong, string> GetAllAnnotations();

    /// <summary>
    /// Refresh the disassembly view to reflect updated annotations.
    /// </summary>
    void RefreshDisassembly();

    /// <summary>
    /// Store arbitrary plugin data (persisted in memory, accessible by any plugin).
    /// Use for cross-plugin communication (e.g. graph block colors).
    /// </summary>
    void SetPluginData(string key, object? value);

    /// <summary>
    /// Retrieve plugin data previously stored via SetPluginData.
    /// </summary>
    object? GetPluginData(string key);

    /// <summary>Fires when user adds a note via disasm context menu.</summary>
    event Action<ulong, string>? OnNoteAdded;

    /// <summary>Fires when user edits a note via disasm context menu.</summary>
    event Action<ulong, string>? OnNoteEdited;

    /// <summary>Fires when user removes a note via disasm context menu.</summary>
    event Action<ulong>? OnNoteRemoved;
}
