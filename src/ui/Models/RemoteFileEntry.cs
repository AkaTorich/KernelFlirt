namespace KernelFlirt.UI.Models;

public class RemoteFileEntry
{
    public string Name { get; set; } = "";
    public bool IsDirectory { get; set; }
    public ulong FileSize { get; set; }

    public string Display => IsDirectory ? $"[{Name}]" : Name;
    public string SizeText => IsDirectory ? "" : FormatSize(FileSize);

    private static string FormatSize(ulong bytes)
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
