using System.IO;
using KernelFlirt.SDK;

namespace FlirtPlugin;

public class FlirtPlugin : IKernelFlirtPlugin
{
    public string Name        => "FLIRT Signatures";
    public string Description => "Recognize standard library functions by matching IDA .pat byte patterns at function entry points";
    public string Version     => "1.0.0";

    public void Initialize(IDebuggerApi api)
    {
        // Load .pat files from FLIRTpat/ subfolder next to the plugins directory
        var pluginDir = Path.GetDirectoryName(typeof(FlirtPlugin).Assembly.Location)!;
        var patDir = Path.Combine(pluginDir, "FLIRTpat");
        if (!Directory.Exists(patDir))
            Directory.CreateDirectory(patDir);
        var patFiles = Directory.GetFiles(patDir, "*.pat", SearchOption.AllDirectories);

        var allSigs = new List<PatSignature>();
        foreach (var patFile in patFiles)
        {
            var sigs = PatDatabase.LoadFile(patFile);
            allSigs.AddRange(sigs);
            if (sigs.Count > 0)
                api.Log.Info($"[FLIRT] Loaded {sigs.Count} signatures from {Path.GetFileName(patFile)}");
        }

        // Fall back to built-in patterns if no .pat files found
        if (allSigs.Count == 0)
        {
            allSigs = BuiltinPatterns.GetAll();
            api.Log.Info($"[FLIRT] No .pat files in {patDir}, using {allSigs.Count} built-in MSVC CRT signatures");
        }

        var index = new PatSignatureIndex(allSigs);
        var panel = new FlirtPanel(api, index);
        api.UI.AddToolPanel("FLIRT Signatures", panel);

        api.Log.Info($"[FLIRT] Ready — {index.Count} signatures indexed ({patFiles.Length} .pat files)");
    }

    public void Shutdown() { }
}
