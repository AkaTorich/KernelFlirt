using System.Globalization;
using System.IO;

namespace FlirtPlugin;

/// <summary>
/// A single FLIRT signature parsed from an IDA .pat file.
/// </summary>
public sealed class PatSignature
{
    /// <summary>Function name from the .pat entry.</summary>
    public string Name { get; init; } = "";

    /// <summary>Leading byte pattern. Each element is 0..255 for a concrete byte, or -1 for wildcard (..).</summary>
    public short[] LeadingPattern { get; init; } = [];

    /// <summary>Total function length from the .pat entry (0 if unknown).</summary>
    public int TotalLength { get; init; }
}

/// <summary>
/// Parser for IDA .pat (FLIRT pattern) text files.
///
/// Line format:
///   558BEC83EC..A1........33C5 08 E822 002F:0000 _function_name
///   ^pattern                   ^crc_len ^crc16 ^len:offset ^name
/// </summary>
public static class PatDatabase
{
    public static List<PatSignature> LoadFile(string path)
    {
        var sigs = new List<PatSignature>();
        if (!File.Exists(path)) return sigs;

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("---") || line[0] == ';' || line[0] == '#')
                continue;

            var sig = ParseLine(line);
            if (sig != null)
                sigs.Add(sig);
        }

        return sigs;
    }

    private static PatSignature? ParseLine(string line)
    {
        // Split into tokens by whitespace
        var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 5) return null;

        // Token 0: hex pattern (pairs of hex chars or ".." for wildcard)
        var pattern = CompilePattern(tokens[0]);
        if (pattern.Length < 4) return null; // too short to be useful

        // Token 1: CRC length (2 hex digits) — skip for v1
        // Token 2: CRC16 (4 hex digits) — skip for v1

        // Token 3: totalLen:offset (e.g. "002F:0000")
        int totalLength = 0;
        if (tokens[3].Contains(':'))
        {
            var parts = tokens[3].Split(':');
            if (parts.Length == 2)
                int.TryParse(parts[0], NumberStyles.HexNumber, null, out totalLength);
        }

        // Token 4+: function name — first token that doesn't start with ^ or :
        // (^ = referenced name, : = local label)
        string? name = null;
        for (int i = 4; i < tokens.Length; i++)
        {
            var t = tokens[i];
            if (t.StartsWith('^') || t.StartsWith(':'))
                continue;
            // Some .pat files have "(offset)" after names like ":0000 name"
            if (t.StartsWith('(') || t.Length == 0)
                continue;
            name = t;
            break;
        }

        if (string.IsNullOrEmpty(name)) return null;

        // Count non-wildcard bytes — require at least 6 concrete bytes
        int concreteCount = 0;
        foreach (var b in pattern)
            if (b >= 0) concreteCount++;
        if (concreteCount < 6) return null;

        return new PatSignature
        {
            Name = name,
            LeadingPattern = pattern,
            TotalLength = totalLength
        };
    }

    private static short[] CompilePattern(string hex)
    {
        // Pattern is pairs of hex chars: "558BEC..A1" → [0x55, 0x8B, 0xEC, -1, 0xA1]
        if (hex.Length % 2 != 0) return [];

        var result = new short[hex.Length / 2];
        for (int i = 0; i < hex.Length; i += 2)
        {
            if (hex[i] == '.' && hex[i + 1] == '.')
            {
                result[i / 2] = -1; // wildcard
            }
            else if (byte.TryParse(hex.AsSpan(i, 2), NumberStyles.HexNumber, null, out byte b))
            {
                result[i / 2] = b;
            }
            else
            {
                result[i / 2] = -1; // treat unparseable as wildcard
            }
        }

        return result;
    }
}

/// <summary>
/// Indexed collection of FLIRT signatures for fast matching.
/// Uses a 2-byte prefix hash to bucket signatures, reducing per-function scan cost.
/// </summary>
public sealed class PatSignatureIndex
{
    private readonly Dictionary<ushort, List<PatSignature>> _index = new();
    private readonly List<PatSignature> _wildcardStart = new();
    private int _totalCount;

    public int Count => _totalCount;

    public PatSignatureIndex(IEnumerable<PatSignature> signatures)
    {
        foreach (var sig in signatures)
            Add(sig);
    }

    private void Add(PatSignature sig)
    {
        _totalCount++;

        // Find first two concrete (non-wildcard) bytes for the prefix key
        int first = -1, second = -1;
        for (int i = 0; i < sig.LeadingPattern.Length; i++)
        {
            if (sig.LeadingPattern[i] >= 0)
            {
                if (first < 0) first = i;
                else if (second < 0) { second = i; break; }
            }
        }

        if (first < 0 || second < 0)
        {
            _wildcardStart.Add(sig);
            return;
        }

        ushort key = (ushort)(((ushort)sig.LeadingPattern[first] << 8) | (ushort)sig.LeadingPattern[second]);
        if (!_index.TryGetValue(key, out var bucket))
        {
            bucket = new List<PatSignature>();
            _index[key] = bucket;
        }
        bucket.Add(sig);
    }

    /// <summary>
    /// Match function bytes against the signature database.
    /// Returns the longest matching signature, or null if no match.
    /// </summary>
    public PatSignature? Match(byte[] functionBytes)
    {
        if (functionBytes == null || functionBytes.Length < 4) return null;

        PatSignature? best = null;

        // Try indexed signatures — find the prefix key from actual bytes
        // We need to check all possible prefix keys that could match
        // Since the index key is based on the sig's first two concrete positions,
        // and those positions vary per signature, we check all 2-byte combos from the function bytes
        // Optimization: just check the most common case (bytes[0],bytes[1]) and nearby positions
        var checkedBuckets = new HashSet<ushort>();

        for (int i = 0; i < Math.Min(functionBytes.Length, 8); i++)
        {
            for (int j = i + 1; j < Math.Min(functionBytes.Length, 12); j++)
            {
                ushort key = (ushort)((functionBytes[i] << 8) | functionBytes[j]);
                if (!checkedBuckets.Add(key)) continue;

                if (_index.TryGetValue(key, out var bucket))
                {
                    foreach (var sig in bucket)
                    {
                        if (MatchAt(functionBytes, sig.LeadingPattern))
                        {
                            if (best == null || sig.LeadingPattern.Length > best.LeadingPattern.Length)
                                best = sig;
                        }
                    }
                }
            }
        }

        // Try wildcard-start signatures
        foreach (var sig in _wildcardStart)
        {
            if (MatchAt(functionBytes, sig.LeadingPattern))
            {
                if (best == null || sig.LeadingPattern.Length > best.LeadingPattern.Length)
                    best = sig;
            }
        }

        return best;
    }

    private static bool MatchAt(byte[] data, short[] pattern)
    {
        if (pattern.Length > data.Length) return false;
        for (int i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] < 0) continue; // wildcard
            if (data[i] != (byte)pattern[i]) return false;
        }
        return true;
    }
}
