using KernelFlirt.SDK;

namespace SamplePlugin;

public class SamplePlugin : IKernelFlirtPlugin
{
    public string Name => "Sample Plugin";
    public string Description => "Demonstrates the KernelFlirt plugin API";
    public string Version => "1.0";

    private IDebuggerApi? _api;

    public void Initialize(IDebuggerApi api)
    {
        _api = api;
        api.Log.Info("Sample plugin loaded!");

        api.UI.AddMenuItem("Dump Registers", OnDumpRegisters);
        api.UI.AddMenuItem("List Modules", OnListModules);

        api.OnBreakStateEntered += () =>
        {
            api.Log.Info($"Break at PID={api.TargetPid} TID={api.SelectedThreadId}");
        };

        api.OnConnected += () => api.Log.Info("Connected to target");
        api.OnDisconnected += () => api.Log.Info("Disconnected from target");
    }

    private void OnDumpRegisters()
    {
        if (_api is null || !_api.IsBreakState)
        {
            _api?.Log.Warning("Not in break state");
            return;
        }

        var regs = _api.Memory.ReadRegisters(_api.TargetPid, _api.SelectedThreadId);
        foreach (var reg in regs.Where(r => !r.IsFlag))
        {
            _api.Log.Info($"  {reg.Name} = 0x{reg.Value:X16}");
        }
    }

    private void OnListModules()
    {
        if (_api is null || !_api.IsConnected)
        {
            _api?.Log.Warning("Not connected");
            return;
        }

        var modules = _api.Symbols.GetModules();
        _api.Log.Info($"--- {modules.Count} modules ---");
        foreach (var m in modules)
        {
            _api.Log.Info($"  {m.BaseAddress:X16}  {m.Size:X8}  {m.Name}");
        }
    }

    public void Shutdown()
    {
        _api?.Log.Info("Sample plugin shutting down");
    }
}
