using KernelFlirt.SDK;

namespace NetworkMonitorPlugin;

public class NetworkMonitorPlugin : IKernelFlirtPlugin
{
    public string Name        => "Network Monitor";
    public string Description => "Monitor network API calls (send/recv/WSASend/WSARecv, WinHTTP, WinINet) in real-time";
    public string Version     => "1.0.0";

    private NetworkPanel? _panel;

    public void Initialize(IDebuggerApi api)
    {
        _panel = new NetworkPanel(api);
        api.UI.AddToolPanel("Network Monitor", _panel);
        api.OnDebugEventFilter += OnFilter;
        api.OnDisconnected += OnDisconnected;
        api.Log.Info("[NetMon] Network Monitor ready. See 'Network Monitor' tab.");
    }

    private bool OnFilter(PluginDebugEvent evt)
    {
        if (_panel == null || !_panel.Engine.IsMonitoring) return false;
        return _panel.Engine.HandleEvent(evt);
    }

    private void OnDisconnected()
    {
        _panel?.Engine.Stop();
    }

    public void Shutdown()
    {
        _panel?.Engine.Stop();
    }
}
