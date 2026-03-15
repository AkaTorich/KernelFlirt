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
}
