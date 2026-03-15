using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using KernelFlirt.SDK;

namespace KernelFlirt.UI.Services;

public class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginPath) : base(isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Share SDK assembly from default context
        if (assemblyName.Name == "KernelFlirt.SDK")
            return null;

        string? assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        return assemblyPath != null ? LoadFromAssemblyPath(assemblyPath) : null;
    }
}

public class LoadedPlugin
{
    public required IKernelFlirtPlugin Plugin { get; init; }
    public required PluginLoadContext Context { get; init; }
    public required string DllPath { get; init; }
}

public class PluginManager
{
    private readonly List<LoadedPlugin> _plugins = [];
    private readonly Action<string> _log;

    public IReadOnlyList<LoadedPlugin> Plugins => _plugins;

    public PluginManager(Action<string> log)
    {
        _log = log;
    }

    public void LoadPlugins(string pluginsDir, IDebuggerApi api)
    {
        if (!Directory.Exists(pluginsDir))
        {
            Directory.CreateDirectory(pluginsDir);
            _log($"[Plugins] Created plugins directory: {pluginsDir}");
            return;
        }

        var dllFiles = Directory.GetFiles(pluginsDir, "*.dll", SearchOption.AllDirectories);
        foreach (string dllPath in dllFiles)
        {
            // Skip KernelFlirt.SDK.dll if it ended up in plugins folder
            if (Path.GetFileName(dllPath).Equals("KernelFlirt.SDK.dll", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var context = new PluginLoadContext(dllPath);
                var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(dllPath));
                var pluginTypes = assembly.GetTypes()
                    .Where(t => typeof(IKernelFlirtPlugin).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);

                foreach (var type in pluginTypes)
                {
                    try
                    {
                        var plugin = (IKernelFlirtPlugin)Activator.CreateInstance(type)!;
                        plugin.Initialize(api);
                        _plugins.Add(new LoadedPlugin
                        {
                            Plugin = plugin,
                            Context = context,
                            DllPath = dllPath
                        });
                        _log($"[Plugins] Loaded: {plugin.Name} v{plugin.Version} ({Path.GetFileName(dllPath)})");
                    }
                    catch (Exception ex)
                    {
                        _log($"[Plugins] Failed to initialize {type.Name}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                _log($"[Plugins] Failed to load {Path.GetFileName(dllPath)}: {ex.Message}");
            }
        }

        _log($"[Plugins] {_plugins.Count} plugin(s) loaded");
    }

    public void UnloadAll()
    {
        foreach (var loaded in _plugins)
        {
            try
            {
                loaded.Plugin.Shutdown();
            }
            catch (Exception ex)
            {
                _log($"[Plugins] Error shutting down {loaded.Plugin.Name}: {ex.Message}");
            }
        }
        _plugins.Clear();
    }

    // Event forwarding - call these from MainViewModel
    public event Action<PluginDebugEvent>? OnDebugEvent;
    public event Action? OnConnected;
    public event Action? OnDisconnected;
    public event Action? OnBreakStateEntered;
    public event Action? OnBreakStateExited;
    public event Action? OnBeforeRun;
    public event Func<PluginDebugEvent, bool>? OnDebugEventFilter;

    // Action callbacks - set by MainViewModel for plugin→VM calls
    public Action? ContinueAction { get; set; }
    public Action? SingleStepAction { get; set; }

    public void NotifyDebugEvent(PluginDebugEvent evt) => SafeInvoke(() => OnDebugEvent?.Invoke(evt));
    public void NotifyConnected() => SafeInvoke(() => OnConnected?.Invoke());
    public void NotifyDisconnected() => SafeInvoke(() => OnDisconnected?.Invoke());
    public void NotifyBreakStateEntered() => SafeInvoke(() => OnBreakStateEntered?.Invoke());
    public void NotifyBreakStateExited() => SafeInvoke(() => OnBreakStateExited?.Invoke());
    public void NotifyBeforeRun() => SafeInvoke(() => OnBeforeRun?.Invoke());

    /// <summary>
    /// Run debug event filters. Returns true if any plugin handled the event
    /// (UI should NOT break).
    /// </summary>
    public bool RunDebugEventFilters(PluginDebugEvent evt)
    {
        var filter = OnDebugEventFilter;
        if (filter == null) return false;

        foreach (var handler in filter.GetInvocationList().Cast<Func<PluginDebugEvent, bool>>())
        {
            try
            {
                if (handler(evt))
                    return true;
            }
            catch (Exception ex)
            {
                _log($"[Plugins] Event filter error: {ex.Message}");
            }
        }
        return false;
    }

    private void SafeInvoke(Action action)
    {
        try { action(); }
        catch (Exception ex) { _log($"[Plugins] Event handler error: {ex.Message}"); }
    }
}
