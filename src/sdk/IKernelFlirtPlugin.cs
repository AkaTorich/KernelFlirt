namespace KernelFlirt.SDK;

/// <summary>
/// Entry point for a KernelFlirt plugin.
/// Implement this interface and place the DLL in the plugins/ folder.
/// </summary>
public interface IKernelFlirtPlugin
{
    string Name { get; }
    string Description { get; }
    string Version { get; }

    void Initialize(IDebuggerApi api);
    void Shutdown();
}
