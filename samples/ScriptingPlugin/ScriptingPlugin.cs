using KernelFlirt.SDK;

namespace ScriptingPlugin;

public class ScriptingPlugin : IKernelFlirtPlugin
{
    public string Name        => "Scripting";
    public string Description => "C# scripting console with full access to the debugger API (Roslyn REPL)";
    public string Version     => "1.0.0";

    public void Initialize(IDebuggerApi api)
    {
        var panel = new ScriptPanel(api);
        api.UI.AddToolPanel("Scripting", panel);
        api.Log.Info("[Scripting] C# REPL ready. Use F5 or Ctrl+Enter to run scripts.");
    }

    public void Shutdown() { }
}
