namespace KernelFlirt.UI.Models;

public class ThreadInfo
{
    public uint ThreadId { get; set; }
    public ulong StartAddress { get; set; }
    public uint State { get; set; }
    public uint Priority { get; set; }

    public string StartAddressHex => $"{StartAddress:X16}";
    public string StateText => State switch
    {
        0 => "Initialized",
        1 => "Ready",
        2 => "Running",
        3 => "Standby",
        4 => "Terminated",
        5 => "Waiting",
        6 => "Transition",
        7 => "DeferredReady",
        _ => $"Unknown({State})"
    };
}
