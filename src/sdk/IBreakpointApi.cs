namespace KernelFlirt.SDK;

public interface IBreakpointApi
{
    uint? SetBreakpoint(uint pid, uint tid, ulong address, PluginBreakpointType type, uint length = 1);
    bool RemoveBreakpoint(uint handle);
    IReadOnlyList<PluginBreakpoint> GetAll();
}
