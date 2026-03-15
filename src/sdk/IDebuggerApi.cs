namespace KernelFlirt.SDK;

/// <summary>
/// Main API surface provided to plugins.
/// </summary>
public interface IDebuggerApi
{
    IMemoryApi Memory { get; }
    IBreakpointApi Breakpoints { get; }
    ISymbolApi Symbols { get; }
    IProcessApi Process { get; }
    ILogApi Log { get; }
    IUiApi UI { get; }

    bool IsConnected { get; }
    bool IsBreakState { get; }
    uint TargetPid { get; }
    uint SelectedThreadId { get; }
    bool Is32Bit { get; }

    event Action<PluginDebugEvent>? OnDebugEvent;
    event Action? OnConnected;
    event Action? OnDisconnected;
    event Action? OnBreakStateEntered;
    event Action? OnBreakStateExited;

    /// <summary>
    /// Resume process execution programmatically (equivalent to Run/F9).
    /// Can be called from a debug event filter to auto-continue.
    /// </summary>
    void Continue();

    /// <summary>
    /// Single-step one instruction on the current thread.
    /// </summary>
    void SingleStep();

    /// <summary>
    /// Fires before the process resumes (Run/F9/Continue).
    /// Plugins can set breakpoints here.
    /// </summary>
    event Action? OnBeforeRun;

    /// <summary>
    /// Register a debug event filter. Called BEFORE the UI processes the event.
    /// Return true to suppress the UI break (plugin handles the event).
    /// The plugin should call Continue() or SingleStep() to resume.
    /// Return false to let the UI handle the event normally.
    /// </summary>
    event Func<PluginDebugEvent, bool>? OnDebugEventFilter;
}
