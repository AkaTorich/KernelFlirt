using System.ComponentModel;

namespace VulnHunterPlugin;

// ═══════════════════════════════════════════════════════════════════════
//  Enums
// ═══════════════════════════════════════════════════════════════════════

public enum DangerLevel
{
    Low,
    Medium,
    High,
    Critical
}

// ═══════════════════════════════════════════════════════════════════════
//  Dangerous function definition (sink)
// ═══════════════════════════════════════════════════════════════════════

public class SinkDef
{
    /// <summary>Module name without extension (e.g. "msvcrt", "ucrtbase")</summary>
    public required string Module { get; init; }
    public required string Function { get; init; }
    public DangerLevel Danger { get; init; }

    /// <summary>x64 arg index of dest buffer (0=RCX,1=RDX,2=R8,3=R9). -1 = N/A</summary>
    public int DestParam { get; init; } = -1;
    /// <summary>x64 arg index of source pointer. -1 = N/A</summary>
    public int SrcParam { get; init; } = -1;
    /// <summary>x64 arg index of explicit size. -1 = unbounded (most dangerous)</summary>
    public int SizeParam { get; init; } = -1;

    public string Description { get; init; } = "";
    public string FullName => $"{Module}!{Function}";
}

// ═══════════════════════════════════════════════════════════════════════
//  Static scan result (import table)
// ═══════════════════════════════════════════════════════════════════════

public class ScanResult
{
    public ulong Address { get; set; }
    public string CallerModule { get; set; } = "";
    public string Function { get; set; } = "";
    public string TargetModule { get; set; } = "";
    public DangerLevel Danger { get; set; }
    public string Description { get; set; } = "";

    public string AddressHex => $"{Address:X16}";
    public string DangerText => Danger.ToString();
}

// ═══════════════════════════════════════════════════════════════════════
//  Runtime monitor hit
// ═══════════════════════════════════════════════════════════════════════

public class RuntimeHit : INotifyPropertyChanged
{
    public int Index { get; set; }
    public string Time { get; set; } = "";
    public uint ThreadId { get; set; }
    public string Function { get; set; } = "";
    public DangerLevel Danger { get; set; }
    public ulong DestAddress { get; set; }
    public ulong SrcAddress { get; set; }
    public ulong CopySize { get; set; }
    public ulong BufferEstimate { get; set; }
    public bool IsSuspicious { get; set; }
    public string CallChain { get; set; } = "";

    public string DestHex => $"{DestAddress:X16}";
    public string SrcHex => SrcAddress == 0 ? "" : $"{SrcAddress:X16}";
    public string SizeText => CopySize == 0 ? "" : $"{CopySize}";
    public string BufferText => BufferEstimate == 0 ? "?" : $"{BufferEstimate}";
    public string DangerText => Danger.ToString();
    public string SuspiciousText => IsSuspicious ? "⚠ YES" : "";

    public event PropertyChangedEventHandler? PropertyChanged;
}
