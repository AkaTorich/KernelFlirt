using System.Windows;
using KernelFlirt.SDK;

namespace AiAssistantPlugin;

public class AiAssistantPlugin : IKernelFlirtPlugin
{
    public string Name => "AI Assistant";
    public string Description => "AI-powered debugging assistant with multi-provider support";
    public string Version => "1.0";

    private IDebuggerApi? _api;
    private AiChatPanel? _panel;

    public void Initialize(IDebuggerApi api)
    {
        _api = api;

        _panel = new AiChatPanel(api);
        api.UI.AddToolPanel("AI Assistant", _panel);

        api.Log.Info("AI Assistant v1.0 loaded. See 'AI Assistant' tab.");
    }

    public void Shutdown()
    {
        _panel?.Shutdown();
        _api?.Log.Info("AI Assistant plugin unloaded");
    }
}
