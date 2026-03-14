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
            // IMAGE_SCN_TYPE_NO_PAD 0x00000008
            if ((Characteristics & 0x00000008) != 0) parts.Add("NO_PAD");
            // IMAGE_SCN_CNT_CODE 0x00000020
            if ((Characteristics & 0x00000020) != 0) parts.Add("CODE");
            // IMAGE_SCN_CNT_INITIALIZED_DATA 0x00000040
            if ((Characteristics & 0x00000040) != 0) parts.Add("IDATA");
            // IMAGE_SCN_CNT_UNINITIALIZED_DATA 0x00000080
            if ((Characteristics & 0x00000080) != 0) parts.Add("UDATA");
            // IMAGE_SCN_LNK_OTHER 0x00000100
            if ((Characteristics & 0x00000100) != 0) parts.Add("LNK_OTHER");
            // IMAGE_SCN_LNK_INFO 0x00000200
            if ((Characteristics & 0x00000200) != 0) parts.Add("INFO");
            // IMAGE_SCN_LNK_REMOVE 0x00000800
            if ((Characteristics & 0x00000800) != 0) parts.Add("REMOVE");
            // IMAGE_SCN_LNK_COMDAT 0x00001000
            if ((Characteristics & 0x00001000) != 0) parts.Add("COMDAT");
            // IMAGE_SCN_NO_DEFER_SPEC_EXC 0x00004000
            if ((Characteristics & 0x00004000) != 0) parts.Add("NO_DEFER_SPEC_EXC");
            // IMAGE_SCN_GPREL 0x00008000
            if ((Characteristics & 0x00008000) != 0) parts.Add("GPREL");
            // IMAGE_SCN_MEM_PURGEABLE 0x00020000
            if ((Characteristics & 0x00020000) != 0) parts.Add("PURGEABLE");
            // IMAGE_SCN_MEM_LOCKED 0x00040000
            if ((Characteristics & 0x00040000) != 0) parts.Add("LOCKED");
            // IMAGE_SCN_MEM_PRELOAD 0x00080000
            if ((Characteristics & 0x00080000) != 0) parts.Add("PRELOAD");
            // IMAGE_SCN_ALIGN_*BYTES 0x00100000–0x00E00000 (bits 20-23)
            uint align = (Characteristics >> 20) & 0xF;
            if (align != 0)
                parts.Add($"ALIGN_{1u << ((int)align - 1)}");
            // IMAGE_SCN_LNK_NRELOC_OVFL 0x01000000
            if ((Characteristics & 0x01000000) != 0) parts.Add("NRELOC_OVFL");
            // IMAGE_SCN_MEM_DISCARDABLE 0x02000000
            if ((Characteristics & 0x02000000) != 0) parts.Add("DISC");
            // IMAGE_SCN_MEM_NOT_CACHED 0x04000000
            if ((Characteristics & 0x04000000) != 0) parts.Add("!CACHE");
            // IMAGE_SCN_MEM_NOT_PAGED 0x08000000
            if ((Characteristics & 0x08000000) != 0) parts.Add("!PAGE");
            // IMAGE_SCN_MEM_SHARED 0x10000000
            if ((Characteristics & 0x10000000) != 0) parts.Add("SHARED");
            // IMAGE_SCN_MEM_EXECUTE 0x20000000
            if ((Characteristics & 0x20000000) != 0) parts.Add("X");
            // IMAGE_SCN_MEM_READ 0x40000000
            if ((Characteristics & 0x40000000) != 0) parts.Add("R");
            // IMAGE_SCN_MEM_WRITE 0x80000000
            if ((Characteristics & 0x80000000) != 0) parts.Add("W");
            return string.Join(" | ", parts);
        }
    }
}
