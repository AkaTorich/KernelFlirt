using KernelFlirt.SDK;

namespace VulnHunterPlugin;

public class VulnHunterPlugin : IKernelFlirtPlugin
{
    public string Name => "VulnHunter";
    public string Description => "Find buffer overflow vulnerabilities via static import scan and dynamic sink monitoring";
    public string Version => "1.0";

    private IDebuggerApi? _api;
    private VulnHunterPanel? _panel;

    public void Initialize(IDebuggerApi api)
    {
        _api = api;
        _panel = new VulnHunterPanel(api);

        api.UI.AddToolPanel("VulnHunter", _panel);
        api.UI.AddMenuItem("VulnHunter: Scan All Modules", () => _panel.StartMonitoring());
        api.UI.AddMenuItem("VulnHunter: Start Monitor", () => _panel.StartMonitoring());
        api.UI.AddMenuItem("VulnHunter: Stop Monitor", () => _panel.StopMonitoring());

        api.OnDebugEventFilter += OnDebugEventFilter;

        api.Log.Info("[VulnHunter] v1.0 loaded. See 'VulnHunter' tab.");
    }

    private bool OnDebugEventFilter(PluginDebugEvent evt)
    {
        if (_panel == null || !_panel.IsMonitoring) return false;
        return _panel.HandleDebugEvent(evt);
    }

    public void Shutdown()
    {
        _panel?.StopMonitoring();
        _api?.Log.Info("[VulnHunter] Plugin unloaded");
    }
}
