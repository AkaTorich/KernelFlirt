namespace KernelFlirt.SDK;

public interface IProcessApi
{
    IReadOnlyList<PluginProcessInfo> EnumProcesses();
    IReadOnlyList<PluginThreadInfo> EnumThreads(uint pid);
    bool SuspendThread(uint tid);
    bool ResumeThread(uint tid);
    (ulong PebAddress, ulong Peb32Address) GetPebAddress(uint pid);
    bool ClearDebugPort(uint pid);
    bool ClearThreadHide(uint pid);
}
