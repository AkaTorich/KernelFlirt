using KernelFlirt.SDK;

namespace GraphViewPlugin;

public class GraphViewPlugin : IKernelFlirtPlugin
{
    public string Name        => "Graph View";
    public string Description => "Control flow graph (CFG) visualization of functions with MSAGL layout";
    public string Version     => "1.0.0";

    public void Initialize(IDebuggerApi api)
    {
        var panel = new GraphPanel(api);
        api.UI.AddToolPanel("Graph View", panel);
        api.Log.Info("[GraphView] CFG viewer ready. Use 'Graph at RIP' to visualize current function.");
    }

    public void Shutdown() { }
}
