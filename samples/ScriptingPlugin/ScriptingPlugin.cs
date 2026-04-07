using KernelFlirt.SDK;

namespace ScriptingPlugin;

public class ScriptingPlugin : IKernelFlirtPlugin
{
    public string Name        => "Scripting";
    public string Description => "C# scripting console with full access to the debugger API (Roslyn REPL)";
    public string Version     => "1.0.0";

    private ScriptEngine? _engine;

    public void Initialize(IDebuggerApi api)
    {
        _engine = new ScriptEngine(api, msg => api.Log.Info($"[Script] {msg}"));
        var panel = new ScriptPanel(api);
        api.UI.AddToolPanel("Scripting", panel);

        // Expose script execution function for MCP/AI plugins
        Func<string, Task<string>> executeScript = async (code) =>
        {
            try { return await _engine.ExecuteAsync(code); }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        };
        api.UI.SetPluginData("ScriptExecute", executeScript);

        api.Log.Info("[Scripting] C# REPL ready. Use F5 or Ctrl+Enter to run scripts.");
    }

    public void Shutdown() { }
}
