namespace KernelFlirt.SDK;

public interface IMemoryApi
{
    byte[]? ReadMemory(uint pid, ulong address, uint size);
    bool WriteMemory(uint pid, ulong address, byte[] data);
    IReadOnlyList<PluginRegister> ReadRegisters(uint pid, uint tid);
    bool WriteRip(uint pid, uint tid, ulong newRip);
    (bool ok, uint oldProtection) ProtectMemory(uint pid, ulong address, uint size, uint newProtection);
}
