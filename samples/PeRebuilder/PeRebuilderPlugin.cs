using KernelFlirt.SDK;

namespace PeRebuilder;

public class PeRebuilderPlugin : IKernelFlirtPlugin
{
    public string Name        => "PE Rebuilder";
    public string Description => "PE dumper & import reconstructor (like Scylla)";
    public string Version     => "1.0";

    private IDebuggerApi? _api;
    private RebuilderPanel? _panel;

    public void Initialize(IDebuggerApi api)
    {
        _api = api;
        _panel = new RebuilderPanel(api);
        api.UI.AddToolPanel("PE Rebuilder", _panel);
        api.Log.Info("[PeRebuilder] Plugin loaded — see 'PE Rebuilder' tab.");
    }

    public void Shutdown()
    {
        _api?.Log.Info("[PeRebuilder] Plugin unloaded.");
    }
}
