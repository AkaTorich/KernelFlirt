namespace KernelFlirt.UI.Models;

/// <summary>
/// Represents an IMAGE_SECTION_HEADER from a PE file.
/// </summary>
public class SectionEntry
{
    public int Index { get; set; }
    public string ModuleName { get; set; } = "";
    public string Name { get; set; } = "";
    public ulong VirtualAddress { get; set; }
    public uint VirtualSize { get; set; }
    public uint RawDataOffset { get; set; }
    public uint RawDataSize { get; set; }
    public uint Characteristics { get; set; }
    public bool HasBreakpoint { get; set; }

    public string VaHex => $"{VirtualAddress:X16}";
    public string VirtualSizeHex => $"0x{VirtualSize:X}";
    public string RawOffsetHex => $"0x{RawDataOffset:X}";
    public string RawSizeHex => $"0x{RawDataSize:X}";
    public string CharacteristicsHex => $"{Characteristics:X8}";

    public string Flags
    {
        get
        {
            var parts = new List<string>();
            if ((Characteristics & 0x00000020) != 0) parts.Add("CODE");
            if ((Characteristics & 0x00000040) != 0) parts.Add("IDATA");
            if ((Characteristics & 0x00000080) != 0) parts.Add("UDATA");
            if ((Characteristics & 0x20000000) != 0) parts.Add("X");
            if ((Characteristics & 0x40000000) != 0) parts.Add("R");
            if ((Characteristics & 0x80000000) != 0) parts.Add("W");
            if ((Characteristics & 0x02000000) != 0) parts.Add("DISC");
            if ((Characteristics & 0x04000000) != 0) parts.Add("!CACHE");
            if ((Characteristics & 0x08000000) != 0) parts.Add("!PAGE");
            if ((Characteristics & 0x10000000) != 0) parts.Add("SHARED");
            return string.Join(" | ", parts);
        }
    }
}
