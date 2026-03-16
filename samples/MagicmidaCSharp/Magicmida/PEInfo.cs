using System.Runtime.InteropServices;
using static Magicmida.NativeApi;

namespace Magicmida;

public class PESection
{
    public IMAGE_SECTION_HEADER Header;
    public byte[]? Data;

    public void Rename(string newName) => Header.SetName(newName);
}

public unsafe class PEHeader
{
    private readonly List<PESection> _sections = new();
    private uint _dumpSize;
    private uint _lfaNew;

    // Public NT headers (mutable)
    public IMAGE_FILE_HEADER FileHeader;
#if CPUX86
    public IMAGE_OPTIONAL_HEADER32 OptionalHeader;
#else
    public IMAGE_OPTIONAL_HEADER64 OptionalHeader;
#endif

    public PEHeader(byte* data)
    {
        var dos = (IMAGE_DOS_HEADER*)data;
        _lfaNew = (uint)dos->e_lfanew;
#if CPUX86
        var nt = (IMAGE_NT_HEADERS32*)(data + _lfaNew);
        FileHeader = nt->FileHeader;
        OptionalHeader = nt->OptionalHeader;
        _dumpSize = OptionalHeader.SizeOfImage;

        var sect = (IMAGE_SECTION_HEADER*)((byte*)nt + sizeof(IMAGE_NT_HEADERS32));
#else
        var nt = (IMAGE_NT_HEADERS64*)(data + _lfaNew);
        FileHeader = nt->FileHeader;
        OptionalHeader = nt->OptionalHeader;
        _dumpSize = OptionalHeader.SizeOfImage;

        var sect = (IMAGE_SECTION_HEADER*)((byte*)nt + sizeof(IMAGE_NT_HEADERS64));
#endif
        for (int i = 0; i < FileHeader.NumberOfSections; i++)
        {
            _sections.Add(new PESection { Header = sect[i] });
        }
    }

    public IList<PESection> Sections => _sections;
    public uint LFANew => _lfaNew;
    public uint DumpSize => _dumpSize;
    public uint SizeOfImage => OptionalHeader.SizeOfImage;

    public ref IMAGE_DATA_DIRECTORY GetDataDirectory(int index)
    {
#if CPUX86
        return ref NativeApi.GetDataDirectory(ref OptionalHeader, index);
#else
        return ref NativeApi.GetDataDirectory(ref OptionalHeader, index);
#endif
    }

    public PESection CreateSection(string name, uint size)
    {
        var prev = _sections[_sections.Count - 1].Header;
        var s = new PESection();
        s.Header.SetName(name);
        s.Header.VirtualSize = size;
        s.Header.VirtualAddress = prev.VirtualAddress + prev.VirtualSize;
        SectionAlign(ref s.Header.VirtualAddress);
        s.Header.PointerToRawData = OptionalHeader.SizeOfImage;
        s.Header.SizeOfRawData = size;
        s.Header.Characteristics = IMAGE_SCN_MEM_READ | IMAGE_SCN_CNT_INITIALIZED_DATA;
        _sections.Add(s);
        OptionalHeader.SizeOfImage += size;
        return s;
    }

    public void DeleteSection(int idx)
    {
        bool isLast = idx == _sections.Count - 1;
        uint sz;
        if (isLast)
            sz = OptionalHeader.SizeOfImage - _sections[idx].Header.SizeOfRawData;
        else
            sz = _sections[idx + 1].Header.PointerToRawData - _sections[idx].Header.PointerToRawData;

        for (int i = _sections.Count - 1; i > idx; i--)
            _sections[i].Header.PointerToRawData -= sz;

        _sections[idx - 1].Header.VirtualSize += _sections[idx].Header.VirtualSize;
        SectionAlign(ref _sections[idx - 1].Header.VirtualSize);

        _sections.RemoveAt(idx);
        FileHeader.NumberOfSections--;
    }

    public PESection? GetSectionByVA(uint va)
    {
        foreach (var s in _sections)
            if (s.Header.VirtualAddress + s.Header.VirtualSize > va)
                return s;
        return null;
    }

    public void AddSectionSlot() => _sections.Add(new PESection());

    public nuint ConvertOffsetToRVAVector(nuint offset)
    {
        foreach (var s in _sections)
            if (s.Header.PointerToRawData <= offset && s.Header.PointerToRawData + s.Header.SizeOfRawData > offset)
                return (offset - s.Header.PointerToRawData) + s.Header.VirtualAddress;
        return 0;
    }

    public uint TrimHugeSections(byte[] buf, ref uint iatRawAddr)
    {
        uint totalDelta = 0;
        for (int i = 0; i < FileHeader.NumberOfSections; i++)
        {
            ref var hdr = ref _sections[i].Header;
            uint sectionStart = hdr.PointerToRawData;
            int zeroStart = -1;
            for (int j = (int)(hdr.SizeOfRawData / 4) - 1; j >= 0; j--)
            {
                if (BitConverter.ToUInt32(buf, (int)(sectionStart + (uint)j * 4)) == 0)
                    zeroStart = j * 4;
                else
                    break;
            }

            if (zeroStart != -1 && hdr.SizeOfRawData - (uint)zeroStart > 1024 * 1024)
            {
                uint oldSize = hdr.SizeOfRawData;
                SectionAlign(ref oldSize);

                uint newSize = (uint)zeroStart;
                FileAlign(ref newSize);
                Utils.Log?.Invoke(LogMsgType.Info,
                    $"Reducing size of section [{hdr.GetName()}]: {oldSize:X} -> {newSize:X}");

                uint delta = oldSize - newSize;
                totalDelta += delta;
                hdr.SizeOfRawData = newSize;

                if (i < _sections.Count - 1)
                {
                    Array.Copy(buf, _sections[i + 1].Header.PointerToRawData,
                        buf, sectionStart + newSize,
                        _dumpSize - sectionStart - oldSize);

                    for (int j = i + 1; j < _sections.Count; j++)
                        _sections[j].Header.PointerToRawData -= delta;
                }

                if (iatRawAddr >= sectionStart + oldSize)
                    iatRawAddr -= delta;
            }
        }
        return totalDelta;
    }

    public void Sanitize()
    {
        foreach (var s in _sections)
        {
            s.Header.PointerToRawData = s.Header.VirtualAddress;
            s.Header.SizeOfRawData = s.Header.VirtualSize;
        }
        OptionalHeader.SizeOfHeaders = _sections[0].Header.PointerToRawData;
        _sections[0].Header.Characteristics |= IMAGE_SCN_MEM_WRITE;
    }

    public void SaveToStream(Stream stream)
    {
        stream.Seek(_lfaNew, SeekOrigin.Begin);
        // Write signature + file header
        var bw = new BinaryWriter(stream);
        bw.Write(IMAGE_NT_SIGNATURE);

        // FileHeader
        var fhBytes = StructToBytes(FileHeader);
        bw.Write(fhBytes);

        // OptionalHeader
        var ohBytes = StructToBytes(OptionalHeader);
        bw.Write(ohBytes);

        // Section headers
        foreach (var s in _sections)
        {
            var shBytes = StructToBytes(s.Header);
            bw.Write(shBytes);
        }

        // Zero out leftovers
        bw.Write(new byte[0x200]);
    }

    public void FileAlign(ref uint v)
    {
        uint delta = v % OptionalHeader.FileAlignment;
        if (delta > 0) v += OptionalHeader.FileAlignment - delta;
    }

    public void SectionAlign(ref uint v)
    {
        uint delta = v % OptionalHeader.SectionAlignment;
        if (delta > 0) v += OptionalHeader.SectionAlignment - delta;
    }

    private static byte[] StructToBytes<T>(T s) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        var buf = new byte[size];
        var handle = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try { Marshal.StructureToPtr(s, handle.AddrOfPinnedObject(), false); }
        finally { handle.Free(); }
        return buf;
    }
}
