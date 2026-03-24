using System.IO;

namespace SignatureDetector;

/// <summary>
/// A single PEiD signature entry parsed from userdb.txt.
/// </summary>
public sealed class PeidSignature
{
    public string Name { get; init; } = "";

    /// <summary>Compiled pattern: each element is a byte value, or -1 for wildcard (??).</summary>
    public short[] Pattern { get; init; } = [];

    /// <summary>If true, only match at the PE entry point. If false, scan all sections.</summary>
    public bool EpOnly { get; init; }
}

/// <summary>
/// Parser for PEiD userdb.txt format.
/// Format:
///   [Name]
///   signature = AA BB ?? CC ...
///   ep_only = true/false
/// </summary>
public static class PeidDatabase
{
    public static List<PeidSignature> Load(string path)
    {
        var sigs = new List<PeidSignature>(4500);
        if (!File.Exists(path)) return sigs;

        string? name = null;
        string? pattern = null;
        bool epOnly = true;

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();

            // Skip empty and comment lines
            if (line.Length == 0 || line[0] == ';')
                continue;

            // Section header [Name]
            if (line[0] == '[' && line[^1] == ']')
            {
                // Flush previous entry
                if (name != null && pattern != null)
                    AddSig(sigs, name, pattern, epOnly);

                name = line.Substring(1, line.Length - 2).Trim();
                pattern = null;
                epOnly = true;
                continue;
            }

            // Key = value
            var eq = line.IndexOf('=');
            if (eq < 0) continue;

            var key = line.Substring(0, eq).Trim().ToLowerInvariant();
            var val = line.Substring(eq + 1).Trim();

            switch (key)
            {
                case "signature":
                    pattern = val;
                    break;
                case "ep_only":
                    epOnly = val.Equals("true", StringComparison.OrdinalIgnoreCase);
                    break;
            }
        }

        // Flush last entry
        if (name != null && pattern != null)
            AddSig(sigs, name, pattern, epOnly);

        return sigs;
    }

    private static void AddSig(List<PeidSignature> sigs, string name, string patternStr, bool epOnly)
    {
        var compiled = CompilePattern(patternStr);
        if (compiled.Length >= 2) // need at least 2 bytes to be useful
        {
            sigs.Add(new PeidSignature
            {
                Name = name,
                Pattern = compiled,
                EpOnly = epOnly
            });
        }
    }

    private static short[] CompilePattern(string hex)
    {
        var tokens = hex.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var result = new short[tokens.Length];
        for (int i = 0; i < tokens.Length; i++)
        {
            var t = tokens[i];
            if (t == "??" || t == "?")
                result[i] = -1; // wildcard
            else if (byte.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out byte b))
                result[i] = b;
            else
                result[i] = -1; // treat unparseable as wildcard
        }
        return result;
    }
}
