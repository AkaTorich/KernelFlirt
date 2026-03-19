using System;

namespace KernelFlirt.UI.Models;

public class RemoteFileEntry
{
    public string Name { get; set; } = "";
    public bool IsDirectory { get; set; }
    public ulong FileSize { get; set; }
    public uint Attributes { get; set; }
    public DateTime LastWriteTime { get; set; }

    public string Extension => IsDirectory ? "" : System.IO.Path.GetExtension(Name).ToLowerInvariant();
    public string Display => IsDirectory ? $"[{Name}]" : Name;
    public string SizeText => IsDirectory ? "" : FormatSizeStatic(FileSize);
    public string DateText => LastWriteTime == DateTime.MinValue ? "" : LastWriteTime.ToString("yyyy-MM-dd HH:mm");

    public bool IsExe => !IsDirectory && Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    public bool IsSys => !IsDirectory && Name.EndsWith(".sys", StringComparison.OrdinalIgnoreCase);
    public bool IsDebuggable => IsExe || IsSys;

    public string Icon => IsDirectory ? "\U0001F4C1" : Extension switch
    {
        ".exe" => "\u2699",
        ".sys" => "\U0001F6E0",
        ".dll" => "\U0001F517",
        ".txt" or ".log" or ".md" or ".cfg" or ".ini" => "\U0001F4C4",
        ".zip" or ".7z" or ".rar" => "\U0001F4E6",
        _ => "\U0001F4C3"
    };

    public static string FormatSizeStatic(ulong bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024UL * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }
}

public class RemoteDriveInfo
{
    public char Letter { get; set; }
    public uint DriveType { get; set; }
    public string Label { get; set; } = "";

    public string Display => string.IsNullOrEmpty(Label)
        ? $"{Letter}:\\"
        : $"{Letter}:\\ ({Label})";

    public string Path => $"{Letter}:\\";
}
