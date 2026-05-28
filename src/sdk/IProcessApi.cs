namespace KernelFlirt.SDK;

public interface IProcessApi
{
    IReadOnlyList<PluginProcessInfo> EnumProcesses();
    IReadOnlyList<PluginThreadInfo> EnumThreads(uint pid);
    bool SuspendThread(uint tid);
    bool ResumeThread(uint tid);

    /// <summary>
    /// Переключает активный поток отладчика на указанный TID. Эквивалент пункта
    /// контекстного меню "Switch to Thread" во вкладке Threads: обновляет регистры,
    /// дизассемблер, стек и call stack для нового потока. Не меняет состояние
    /// suspend-count в драйвере — это только переключение фокуса отладчика.
    /// </summary>
    void SwitchToThread(uint tid);
    (ulong PebAddress, ulong Peb32Address) GetPebAddress(uint pid);
    bool ClearDebugPort(uint pid);
    bool ClearThreadHide(uint pid);
    bool InstallNtQsiHook();
    bool RemoveNtQsiHook();
    string ProbeNtQsiHook();
    bool SetSpoofSharedUserData(bool enable);
}
