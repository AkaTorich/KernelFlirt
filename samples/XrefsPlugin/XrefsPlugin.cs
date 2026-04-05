using System.Windows;
using KernelFlirt.SDK;

namespace XrefsPlugin;

public class Plugin : IKernelFlirtPlugin
{
    public string Name => "Xrefs";
    public string Description => "Cross-reference analysis: find who calls/references a given address";
    public string Version => "1.0";

    private IDebuggerApi _api = null!;
    private XrefsPanel _panel = null!;

    public void Initialize(IDebuggerApi api)
    {
        _api = api;
        _panel = new XrefsPanel(api);

        api.UI.AddToolPanel("Xrefs", _panel);
        api.UI.AddMenuItem("Find _Xrefs at RIP", () =>
            Application.Current.Dispatcher.BeginInvoke(_panel.AnalyzeAtRip));
    }

    public void Shutdown() { }
}
