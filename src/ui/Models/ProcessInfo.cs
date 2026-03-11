namespace KernelFlirt.UI.Models;

public class ProcessInfo
{
    public uint ProcessId { get; set; }
    public uint SessionId { get; set; }
    public string Name { get; set; } = "";

    public override string ToString() => $"{Name} ({ProcessId})";
}
