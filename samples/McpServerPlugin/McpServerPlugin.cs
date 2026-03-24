using KernelFlirt.SDK;

namespace McpServerPlugin;

/// <summary>
/// KernelFlirt plugin that exposes the debugger as an MCP (Model Context Protocol) server.
/// Shows a settings panel in the "MCP Server" tab.
/// Connect any MCP client (Claude Code, etc.) to http://localhost:{port}/sse
/// </summary>
public class McpServerPlugin : IKernelFlirtPlugin
{
    public string Name        => "MCP Server";
    public string Description => "Exposes the debugger as an MCP server (SSE transport)";
    public string Version     => "1.0.0";

    private McpSettingsPanel? _panel;

    public void Initialize(IDebuggerApi api)
    {
        _panel = new McpSettingsPanel(api);
        api.UI.AddToolPanel("MCP Server", _panel);

        // Auto-start with the persisted port
        _panel.AutoStart();

        api.Log.Info("[MCP] Plugin loaded — see 'MCP Server' tab for status and settings.");
    }

    public void Shutdown()
    {
        _panel?.Shutdown();
        _panel = null;
    }
}
