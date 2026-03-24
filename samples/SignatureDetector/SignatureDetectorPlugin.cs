using KernelFlirt.SDK;

namespace SignatureDetector;

public class SignatureDetectorPlugin : IKernelFlirtPlugin
{
    public string Name        => "Signature Detector";
    public string Description => "PEiD-compatible packer/compiler signature detector (userdb.txt)";
    public string Version     => "1.0.0";

    private SignaturePanel? _panel;

    public void Initialize(IDebuggerApi api)
    {
        var dbPath = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(typeof(SignatureDetectorPlugin).Assembly.Location)!,
            "userdb.txt");

        var db = PeidDatabase.Load(dbPath);

        _panel = new SignaturePanel(api, db);
        api.UI.AddToolPanel("Signature Detector", _panel);

        api.Log.Info($"[SigDetector] Loaded {db.Count} PEiD signatures from userdb.txt");
    }

    public void Shutdown() { }
}
