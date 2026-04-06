using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using KernelFlirt.SDK;

namespace SessionPlugin;

public class Plugin : IKernelFlirtPlugin
{
    public string Name => "Session Manager";
    public string Description => "Save/load session state (breakpoints, comments, function names, graph colors) to a .kfsession file";
    public string Version => "1.0";

    private IDebuggerApi _api = null!;

    public void Initialize(IDebuggerApi api)
    {
        _api = api;
        api.UI.AddMenuItem("Save _Session...", OnSave);
        api.UI.AddMenuItem("Load S_ession...", OnLoad);
    }

    public void Shutdown() { }

    private void OnSave()
    {
        if (!_api.IsConnected || !_api.IsBreakState)
        {
            MessageBox.Show("Connect and break first.", "Session", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "KF Session (*.kfsession)|*.kfsession",
            Title = "Save Session",
            DefaultExt = ".kfsession",
            FileName = GetTargetName() + ".kfsession"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var data = new SessionData();

            // Breakpoints
            var bps = _api.Breakpoints.GetAll();
            foreach (var bp in bps)
                data.Breakpoints.Add(new BpEntry { Address = bp.Address, Type = (int)bp.Type });

            // Annotations/comments
            var annotations = _api.UI.GetAllAnnotations();
            foreach (var (addr, text) in annotations)
                data.Annotations.Add(new AnnotationEntry { Address = addr, Text = text });

            // User-defined functions
            var funcs = _api.Symbols.GetRegisteredFunctions();
            foreach (var f in funcs)
                data.Functions.Add(new FunctionEntry { Address = f.Address, Name = f.Name, Size = f.Size });

            // Graph block colors
            if (_api.UI.GetPluginData("GraphBlockColors") is Dictionary<ulong, Color> colors)
            {
                foreach (var (addr, color) in colors)
                    data.BlockColors.Add(new BlockColorEntry
                    {
                        Address = addr,
                        Color = $"#{color.R:X2}{color.G:X2}{color.B:X2}"
                    });
            }

            // Module bases (for rebasing)
            var modules = _api.Symbols.GetModules();
            foreach (var mod in modules)
                data.Modules.Add(new ModuleEntry { Name = mod.Name, BaseAddress = mod.BaseAddress, Size = mod.Size });

            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(dlg.FileName, json);

            _api.Log.Info($"[Session] Saved: {bps.Count} bp, {annotations.Count} annotations, {funcs.Count} functions, {data.BlockColors.Count} colors → {Path.GetFileName(dlg.FileName)}");
        }
        catch (Exception ex)
        {
            _api.Log.Error($"[Session] Save failed: {ex.Message}");
        }
    }

    private void OnLoad()
    {
        if (!_api.IsConnected || !_api.IsBreakState)
        {
            MessageBox.Show("Connect and break first, then load session.", "Session", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "KF Session (*.kfsession)|*.kfsession",
            Title = "Load Session"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var json = File.ReadAllText(dlg.FileName);
            var data = JsonSerializer.Deserialize<SessionData>(json);
            if (data == null) { _api.Log.Warning("[Session] Empty file."); return; }

            // Build rebase map
            var currentModules = _api.Symbols.GetModules();
            var rebaseMap = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var saved in data.Modules)
            {
                var cur = currentModules.FirstOrDefault(m =>
                    m.Name.Equals(saved.Name, StringComparison.OrdinalIgnoreCase));
                if (cur != null)
                    rebaseMap[saved.Name] = (long)cur.BaseAddress - (long)saved.BaseAddress;
            }

            int bpCount = 0, annotCount = 0, funcCount = 0, colorCount = 0, skipCount = 0;

            // Restore breakpoints (via UI toggle — updates list, disasm markers, and driver)
            var existingBps = _api.Breakpoints.GetAll();
            foreach (var bp in data.Breakpoints)
            {
                ulong addr = Rebase(bp.Address, data.Modules, rebaseMap);
                if (existingBps.Any(b => b.Address == addr)) { skipCount++; continue; }
                _api.Breakpoints.ToggleBreakpoint(addr, (PluginBreakpointType)bp.Type);
                bpCount++;
            }

            // Restore annotations
            foreach (var ann in data.Annotations)
            {
                ulong addr = Rebase(ann.Address, data.Modules, rebaseMap);
                _api.UI.SetAddressAnnotation(addr, ann.Text);
                annotCount++;
            }

            // Restore functions
            foreach (var f in data.Functions)
            {
                ulong addr = Rebase(f.Address, data.Modules, rebaseMap);
                _api.Symbols.RegisterFunction(addr, f.Name, f.Size);
                funcCount++;
            }

            // Restore graph block colors
            if (data.BlockColors.Count > 0 &&
                _api.UI.GetPluginData("GraphBlockColors") is Dictionary<ulong, Color> colors)
            {
                foreach (var bc in data.BlockColors)
                {
                    ulong addr = Rebase(bc.Address, data.Modules, rebaseMap);
                    if (TryParseColor(bc.Color, out var color))
                    {
                        colors[addr] = color;
                        colorCount++;
                    }
                }
            }

            _api.UI.RefreshDisassembly();
            _api.Log.Info($"[Session] Loaded: {bpCount} bp, {annotCount} annotations, {funcCount} functions, {colorCount} colors ({skipCount} skipped) from {Path.GetFileName(dlg.FileName)}");
        }
        catch (Exception ex)
        {
            _api.Log.Error($"[Session] Load failed: {ex.Message}");
        }
    }

    private string GetTargetName()
    {
        var modules = _api.Symbols.GetModules();
        if (modules.Count > 0)
            return Path.GetFileNameWithoutExtension(modules[0].Name);
        return "session";
    }

    private static ulong Rebase(ulong addr, List<ModuleEntry> savedModules, Dictionary<string, long> rebaseMap)
    {
        foreach (var mod in savedModules)
        {
            if (addr >= mod.BaseAddress && addr < mod.BaseAddress + mod.Size)
            {
                if (rebaseMap.TryGetValue(mod.Name, out long delta))
                    return (ulong)((long)addr + delta);
                break;
            }
        }
        return addr;
    }

    private static bool TryParseColor(string hex, out Color color)
    {
        color = default;
        if (string.IsNullOrEmpty(hex)) return false;
        hex = hex.TrimStart('#');
        if (hex.Length != 6) return false;
        if (!byte.TryParse(hex[0..2], System.Globalization.NumberStyles.HexNumber, null, out byte r)) return false;
        if (!byte.TryParse(hex[2..4], System.Globalization.NumberStyles.HexNumber, null, out byte g)) return false;
        if (!byte.TryParse(hex[4..6], System.Globalization.NumberStyles.HexNumber, null, out byte b)) return false;
        color = Color.FromRgb(r, g, b);
        return true;
    }
}

public class SessionData
{
    public List<BpEntry> Breakpoints { get; set; } = new();
    public List<AnnotationEntry> Annotations { get; set; } = new();
    public List<FunctionEntry> Functions { get; set; } = new();
    public List<BlockColorEntry> BlockColors { get; set; } = new();
    public List<ModuleEntry> Modules { get; set; } = new();
}

public class BpEntry
{
    public ulong Address { get; set; }
    public int Type { get; set; }
}

public class AnnotationEntry
{
    public ulong Address { get; set; }
    public string Text { get; set; } = "";
}

public class FunctionEntry
{
    public ulong Address { get; set; }
    public string Name { get; set; } = "";
    public uint Size { get; set; }
}

public class BlockColorEntry
{
    public ulong Address { get; set; }
    public string Color { get; set; } = "";
}

public class ModuleEntry
{
    public string Name { get; set; } = "";
    public ulong BaseAddress { get; set; }
    public ulong Size { get; set; }
}
