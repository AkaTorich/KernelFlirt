using System.IO;
using System.Text;
using KernelFlirt.SDK;

namespace PeRebuilder;

/// <summary>
/// Dumps a PE from process memory and rebuilds it as a valid PE file.
/// Handles section alignment, import directory construction, and header fixes.
/// </summary>
public sealed class PeDumper
{
    private readonly IDebuggerApi _api;
    private readonly bool _is64;
    private readonly int _ptrSize;
    private readonly Action<string> _log;

    public PeDumper(IDebuggerApi api, Action<string> log)
    {
        _api     = api;
        _is64    = !api.Is32Bit;
        _ptrSize = _is64 ? 8 : 4;
        _log     = log;
    }

    /// <summary>
    /// Dump PE from memory and optionally rebuild imports.
    /// Returns the rebuilt PE as byte[], or null on failure.
    /// </summary>
    public byte[]? Dump(ulong imageBase, ulong oep,
                         List<(string dll, List<ReconstructedImport> funcs)>? imports = null)
    {
        // Read PE header
        byte[] hdr = ReadChunked(imageBase, 0x1000);
        if (hdr.Length < 0x40 || hdr[0] != 0x4D || hdr[1] != 0x5A)
        {
            _log("ERROR: Invalid MZ signature");
            return null;
        }

        int lfanew = BitConverter.ToInt32(hdr, 0x3C);
        if (lfanew < 0 || lfanew + 6 > hdr.Length)
        {
            _log("ERROR: Invalid e_lfanew");
            return null;
        }

        if (hdr[lfanew] != 'P' || hdr[lfanew + 1] != 'E')
        {
            _log("ERROR: Invalid PE signature");
            return null;
        }

        ushort numSections = BitConverter.ToUInt16(hdr, lfanew + 6);
        ushort optSize     = BitConverter.ToUInt16(hdr, lfanew + 20);
        ushort magic       = BitConverter.ToUInt16(hdr, lfanew + 24);
        bool pe64 = magic == 0x20B;

        uint sectAlign = BitConverter.ToUInt32(hdr, lfanew + 24 + 32);
        uint fileAlign = BitConverter.ToUInt32(hdr, lfanew + 24 + 36);
        if (sectAlign == 0) sectAlign = 0x1000;
        if (fileAlign == 0) fileAlign = 0x200;

        _log($"PE{(pe64 ? "64" : "32")} — {numSections} sections, SectAlign=0x{sectAlign:X}, FileAlign=0x{fileAlign:X}");

        // Parse sections
        int sectOff = lfanew + 4 + 20 + optSize;
        var sections = new List<SectionInfo>();
        for (int i = 0; i < numSections; i++)
        {
            int o = sectOff + i * 40;
            if (o + 40 > hdr.Length) break;
            string name = Encoding.ASCII.GetString(hdr, o, 8).TrimEnd('\0');
            uint vsize = BitConverter.ToUInt32(hdr, o + 8);
            uint vrva  = BitConverter.ToUInt32(hdr, o + 12);
            uint rsize = BitConverter.ToUInt32(hdr, o + 16);
            uint roff  = BitConverter.ToUInt32(hdr, o + 20);
            uint chars = BitConverter.ToUInt32(hdr, o + 36);
            sections.Add(new SectionInfo(name, vrva, vsize, rsize, roff, chars, o));
        }

        // Calculate total image size to read
        uint imageSize = 0;
        foreach (var s in sections)
        {
            uint end = s.VirtualRva + Math.Max(s.VirtualSize, s.RawSize);
            if (end > imageSize) imageSize = end;
        }
        imageSize = Align(imageSize, sectAlign);

        _log($"Reading {imageSize / 1024} KB from process memory...");
        byte[] pe = ReadChunked(imageBase, (int)imageSize);
        if (pe.Length < imageSize)
        {
            // Pad with zeros if short read
            byte[] padded = new byte[imageSize];
            Array.Copy(pe, padded, pe.Length);
            pe = padded;
        }

        // Fix sections: PointerToRawData = VirtualRva, SizeOfRawData = VirtualSize
        for (int i = 0; i < sections.Count; i++)
        {
            var s = sections[i];
            int o = s.HeaderOffset;
            // SizeOfRawData = VirtualSize (aligned)
            uint rawSize = Align(s.VirtualSize, fileAlign);
            BitConverter.TryWriteBytes(pe.AsSpan(o + 16), rawSize);
            // PointerToRawData = VirtualRva
            BitConverter.TryWriteBytes(pe.AsSpan(o + 20), s.VirtualRva);

            // Make .text writable for easier patching later
            if (s.Name.StartsWith(".text", StringComparison.OrdinalIgnoreCase))
            {
                uint ch = BitConverter.ToUInt32(pe, o + 36);
                ch |= 0x80000000; // IMAGE_SCN_MEM_WRITE
                BitConverter.TryWriteBytes(pe.AsSpan(o + 36), ch);
            }
        }

        // Fix OEP
        uint oepRva = (uint)(oep - imageBase);
        BitConverter.TryWriteBytes(pe.AsSpan(lfanew + 0x28), oepRva);
        _log($"OEP RVA = 0x{oepRva:X}");

        // Disable ASLR
        int dllCharsOff = lfanew + 24 + (pe64 ? 0x46 : 0x46);
        if (dllCharsOff + 2 <= pe.Length)
        {
            ushort dc = BitConverter.ToUInt16(pe, dllCharsOff);
            dc &= unchecked((ushort)~0x0040); // IMAGE_DLLCHARACTERISTICS_DYNAMIC_BASE
            BitConverter.TryWriteBytes(pe.AsSpan(dllCharsOff), dc);
        }

        // Trim trailing zeros in large sections
        TrimSections(pe, sections, sectAlign, fileAlign);

        // Build and append import section if imports provided
        if (imports != null && imports.Count > 0)
        {
            pe = AppendImportSection(pe, imageBase, lfanew, pe64, numSections, sectOff,
                                      sectAlign, fileAlign, imports);
        }
        else
        {
            // Clear import/IAT data directories if no imports
            int ddBase = lfanew + 24 + (pe64 ? 0x70 : 0x60);
            // Import dir (entry 1)
            BitConverter.TryWriteBytes(pe.AsSpan(ddBase + 8), 0u);
            BitConverter.TryWriteBytes(pe.AsSpan(ddBase + 12), 0u);
            // IAT dir (entry 12)
            BitConverter.TryWriteBytes(pe.AsSpan(ddBase + 96), 0u);
            BitConverter.TryWriteBytes(pe.AsSpan(ddBase + 100), 0u);
        }

        // Fix SizeOfImage
        uint finalImageSize = (uint)pe.Length;
        finalImageSize = Align(finalImageSize, sectAlign);
        BitConverter.TryWriteBytes(pe.AsSpan(lfanew + 24 + 56), finalImageSize);

        _log($"Dump complete: {pe.Length / 1024} KB");
        return pe;
    }

    private byte[] AppendImportSection(byte[] pe, ulong imageBase, int lfanew, bool pe64,
                                        int numSections, int sectOff, uint sectAlign, uint fileAlign,
                                        List<(string dll, List<ReconstructedImport> funcs)> imports)
    {
        // Calculate new section VA
        uint lastSectEnd = 0;
        for (int i = 0; i < numSections; i++)
        {
            int o = sectOff + i * 40;
            uint vrva  = BitConverter.ToUInt32(pe, o + 12);
            uint vsize = BitConverter.ToUInt32(pe, o + 8);
            uint end = Align(vrva + vsize, sectAlign);
            if (end > lastSectEnd) lastSectEnd = end;
        }

        uint importSectVa  = lastSectEnd;
        uint importSectOff = Align((uint)pe.Length, fileAlign);

        // Build import data
        byte[] importData = BuildImportDirectory(imageBase, importSectVa, pe64, imports);
        uint importDataAligned = Align((uint)importData.Length, fileAlign);

        // Expand PE
        byte[] newPe = new byte[importSectOff + importDataAligned];
        Array.Copy(pe, newPe, pe.Length);
        Array.Copy(importData, 0, newPe, importSectOff, importData.Length);
        pe = newPe;

        // Add section header
        int newSectOff = sectOff + numSections * 40;
        if (newSectOff + 40 > pe.Length)
        {
            _log("WARNING: No room for new section header");
            return pe;
        }

        byte[] sname = Encoding.ASCII.GetBytes(".import\0");
        Array.Copy(sname, 0, pe, newSectOff, 8);
        BitConverter.TryWriteBytes(pe.AsSpan(newSectOff + 8), importDataAligned);  // VirtualSize
        BitConverter.TryWriteBytes(pe.AsSpan(newSectOff + 12), importSectVa);       // VirtualAddress
        BitConverter.TryWriteBytes(pe.AsSpan(newSectOff + 16), importDataAligned);  // SizeOfRawData
        BitConverter.TryWriteBytes(pe.AsSpan(newSectOff + 20), importSectOff);      // PointerToRawData
        BitConverter.TryWriteBytes(pe.AsSpan(newSectOff + 36), 0xC0000040u);        // R | INITIALIZED_DATA

        // Update NumberOfSections
        BitConverter.TryWriteBytes(pe.AsSpan(lfanew + 6), (ushort)(numSections + 1));

        // Update data directories
        int ddBase = lfanew + 24 + (pe64 ? 0x70 : 0x60);

        // Import directory (entry 1) — descriptors are at start of import section
        BitConverter.TryWriteBytes(pe.AsSpan(ddBase + 8), importSectVa);
        uint importDirSize = (uint)((imports.Count + 1) * 20); // +1 for null terminator
        BitConverter.TryWriteBytes(pe.AsSpan(ddBase + 12), importDirSize);

        // IAT directory (entry 12) — keep original IAT location
        // (we wrote real addresses there, pointed to by FirstThunk)

        _log($"Import section: VA=0x{importSectVa:X}, FileOff=0x{importSectOff:X}, " +
             $"Size=0x{importDataAligned:X}, {imports.Count} DLLs");

        return pe;
    }

    private byte[] BuildImportDirectory(ulong imageBase, uint sectionRva, bool pe64,
                                         List<(string dll, List<ReconstructedImport> funcs)> imports)
    {
        // Layout inside .import section:
        // [IMAGE_IMPORT_DESCRIPTOR array (n+1 * 20 bytes)]
        // [DLL name strings]
        // [Hint/Name entries] (pointed to by OriginalFirstThunk / ILT)

        int descSize = (imports.Count + 1) * 20; // +1 null terminator

        // Estimate sizes
        var dllNames = new List<(int offset, string name)>();
        var hintNames = new List<(int offset, ReconstructedImport imp)>();

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        // Reserve space for descriptors
        bw.Write(new byte[descSize]);

        // Write DLL name strings
        foreach (var (dll, _) in imports)
        {
            int nameOff = (int)ms.Position;
            dllNames.Add((nameOff, dll));
            bw.Write(Encoding.ASCII.GetBytes(dll));
            bw.Write((byte)0);
            // Align to 2
            if (ms.Position % 2 != 0) bw.Write((byte)0);
        }

        // Write ILT (OriginalFirstThunk) arrays + Hint/Name entries
        var iltOffsets = new List<int>();
        foreach (var (dll, funcs) in imports)
        {
            int iltOff = (int)ms.Position;
            iltOffsets.Add(iltOff);

            // Reserve space for ILT array (n+1 pointers)
            int iltSize = (funcs.Count + 1) * (pe64 ? 8 : 4);
            long iltPos = ms.Position;
            bw.Write(new byte[iltSize]);

            // Write Hint/Name entries and fill ILT
            long savedPos = ms.Position;
            for (int i = 0; i < funcs.Count; i++)
            {
                var f = funcs[i];
                long iltEntryPos = iltPos + i * (pe64 ? 8 : 4);

                if (f.ByOrdinal)
                {
                    // Ordinal import
                    ms.Position = iltEntryPos;
                    if (pe64)
                        bw.Write((ulong)f.Ordinal | 0x8000000000000000);
                    else
                        bw.Write((uint)f.Ordinal | 0x80000000);
                }
                else
                {
                    // Name import — write hint/name at end
                    ms.Position = savedPos;
                    int hnOff = (int)ms.Position;
                    bw.Write((ushort)0); // hint (0 is fine, loader searches by name)
                    bw.Write(Encoding.ASCII.GetBytes(f.FuncName));
                    bw.Write((byte)0);
                    if (ms.Position % 2 != 0) bw.Write((byte)0);
                    savedPos = ms.Position;

                    // Write ILT entry pointing to hint/name
                    uint hnRva = sectionRva + (uint)hnOff;
                    ms.Position = iltEntryPos;
                    if (pe64)
                        bw.Write((ulong)hnRva);
                    else
                        bw.Write(hnRva);
                }
            }
            ms.Position = savedPos;
        }

        // Now fill in IMAGE_IMPORT_DESCRIPTOR array
        for (int i = 0; i < imports.Count; i++)
        {
            var (dll, funcs) = imports[i];
            int descOff = i * 20;

            // OriginalFirstThunk (ILT)
            uint iltRva = sectionRva + (uint)iltOffsets[i];
            ms.Position = descOff;
            bw.Write(iltRva);

            // TimeDateStamp
            bw.Write(0u);

            // ForwarderChain
            bw.Write(0u);

            // Name RVA
            uint nameRva = sectionRva + (uint)dllNames[i].offset;
            bw.Write(nameRva);

            // FirstThunk (IAT) — points to actual IAT in original .rdata/.idata
            uint firstThunkRva = (uint)(funcs[0].IatAddress - imageBase);
            bw.Write(firstThunkRva);
        }

        return ms.ToArray();
    }

    private void TrimSections(byte[] pe, List<SectionInfo> sections, uint sectAlign, uint fileAlign)
    {
        foreach (var s in sections)
        {
            if (s.VirtualSize < 0x100000) continue; // Skip small sections

            uint end = s.VirtualRva + s.VirtualSize;
            if (end > pe.Length) end = (uint)pe.Length;
            uint start = s.VirtualRva;

            // Scan backward for trailing zeros (DWORD granularity)
            uint trimEnd = end;
            while (trimEnd > start + 0x1000)
            {
                uint dword = BitConverter.ToUInt32(pe, (int)(trimEnd - 4));
                if (dword != 0) break;
                trimEnd -= 4;
            }
            trimEnd = Align(trimEnd - start, sectAlign) + start;

            if (trimEnd < end)
            {
                uint saved = end - trimEnd;
                _log($"  Trimmed {s.Name}: {saved / 1024} KB trailing zeros");
            }
        }
    }

    private byte[] ReadChunked(ulong address, int totalSize)
    {
        const int chunkSize = 0x100000; // 1MB
        byte[] result = new byte[totalSize];
        int offset = 0;

        while (offset < totalSize)
        {
            int toRead = Math.Min(chunkSize, totalSize - offset);
            try
            {
                byte[]? chunk = _api.Memory.ReadMemory(_api.TargetPid, address + (ulong)offset, (uint)toRead);
                if (chunk == null || chunk.Length == 0) break;
                Array.Copy(chunk, 0, result, offset, chunk.Length);
                offset += chunk.Length;
            }
            catch { break; }
        }

        return result;
    }

    private static uint Align(uint value, uint alignment) =>
        alignment == 0 ? value : (value + alignment - 1) & ~(alignment - 1);

    private record SectionInfo(string Name, uint VirtualRva, uint VirtualSize,
                                uint RawSize, uint RawOffset, uint Characteristics, int HeaderOffset);
}
