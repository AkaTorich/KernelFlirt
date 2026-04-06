namespace KernelFlirt.SDK;

public interface IBreakpointApi
{
    uint? SetBreakpoint(uint pid, uint tid, ulong address, PluginBreakpointType type, uint length = 1);
    bool RemoveBreakpoint(uint handle);
    IReadOnlyList<PluginBreakpoint> GetAll();

    /// <summary>
    /// Toggle a breakpoint via the UI (updates breakpoint list, disassembly markers, and driver).
    /// If a breakpoint exists at this address, it is removed. Otherwise it is added.
    /// </summary>
    void ToggleBreakpoint(ulong address, PluginBreakpointType type = PluginBreakpointType.Software);
}
