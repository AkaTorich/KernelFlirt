using System.Runtime.InteropServices;
using static Magicmida.NativeApi;

namespace Magicmida;

#if CPUX86
public unsafe class Patcher
{
    private string _fileName;
    private MemoryStream _stream;
    private PEHeader _pe;

    public Patcher(string fileName)
    {
        _fileName = fileName;
        _stream = new MemoryStream(File.ReadAllBytes(fileName));
        _stream.Position = 0;

        var buf = _stream.GetBuffer();
        fixed (byte* p = buf)
            _pe = new PEHeader(p);
    }

    public void ProcessShrink()
    {
        ShrinkPE();
        ShrinkExportSect();

        _pe.SaveToStream(_stream);
        File.WriteAllBytes(_fileName, _stream.ToArray());
    }

    public void ProcessMkData()
    {
        string lower = _fileName.ToLowerInvariant();
        int posMS = lower.LastIndexOf("maplestory", StringComparison.Ordinal);
        if (posMS > 0 && lower.IndexOf(".exe", posMS, StringComparison.Ordinal) < posMS + 20)
            MapleCreateDataSections();
        else if (_pe.OptionalHeader.MajorLinkerVersion == 14)
            MSVCCreateDataSections();
        else
        {
            Utils.Log?.Invoke(LogMsgType.Info, "Data section creation not available for this compiler.");
            return;
        }

        _pe.SaveToStream(_stream);
        File.WriteAllBytes(_fileName, _stream.ToArray());
    }

    private void ShrinkPE()
    {
        const int IMAGE_NUMBEROF_DIRECTORY_ENTRIES = 16;
        var data = _stream.ToArray();
        var del = new List<int>();

        bool IsReferenced(IMAGE_SECTION_HEADER sh)
        {
            for (int d = 0; d < IMAGE_NUMBEROF_DIRECTORY_ENTRIES; d++)
            {
                ref var dir = ref _pe.GetDataDirectory(d);
                if (dir.VirtualAddress >= sh.VirtualAddress &&
                    dir.VirtualAddress + dir.Size <= sh.VirtualAddress + sh.VirtualSize)
                    return true;
            }
            return false;
        }

        // Build new stream keeping only referenced/important sections
        var ns = new MemoryStream();
        int firstSectRaw = (int)_pe.Sections[0].Header.PointerToRawData;
        ns.Write(data, 0, firstSectRaw);

        int pos = firstSectRaw;
        for (int i = 0; i < _pe.Sections.Count; i++)
        {
            string name = _pe.Sections[i].Header.GetName();
            if (!IsReferenced(_pe.Sections[i].Header) && name != ".data" && name != ".rdata" && i > 0)
            {
                del.Add(i);
                if (i != _pe.Sections.Count - 1)
                    pos = (int)_pe.Sections[i + 1].Header.PointerToRawData;
            }
            else
            {
                int start = (int)_pe.Sections[i].Header.PointerToRawData;
                int end;
                if (i != _pe.Sections.Count - 1)
                    end = (int)_pe.Sections[i + 1].Header.PointerToRawData;
                else
                    end = data.Length;

                // Advance pos if needed
                if (start > pos)
                    pos = start;

                ns.Write(data, start, end - start);
                pos = end;
            }
        }

        _stream = ns;

        del.Reverse();
        foreach (int i in del)
            _pe.DeleteSection(i);
    }

    private void ShrinkExportSect()
    {
        ref var dir = ref _pe.GetDataDirectory(IMAGE_DIRECTORY_ENTRY_EXPORT);
        if (dir.VirtualAddress == 0 || dir.Size == 0) return;

        var eh = _pe.GetSectionByVA(dir.VirtualAddress);
        if (eh == null) return;

        var data = _stream.ToArray();
        uint pBase = eh.Header.PointerToRawData;
        uint pExp = pBase + (dir.VirtualAddress - eh.Header.VirtualAddress);

        // Move export data to start of section
        Array.Copy(data, pExp, data, pBase, dir.Size);
        Array.Clear(data, (int)(pBase + dir.Size), (int)(0x1000 - dir.Size));

        uint diff = pExp - pBase;
        dir.VirtualAddress = eh.Header.VirtualAddress;

        // Fix export directory pointers
        fixed (byte* p = &data[pBase])
        {
            var e = (IMAGE_EXPORT_DIRECTORY*)p;
            e->Name -= diff;
            e->AddressOfFunctions -= diff;
            e->AddressOfNames -= diff;
            e->AddressOfNameOrdinals -= diff;

            uint* names = (uint*)(p + (e->AddressOfNames - eh.Header.VirtualAddress));
            for (int i = 0; i < e->NumberOfNames; i++)
                names[i] -= diff;
        }

        diff = eh.Header.SizeOfRawData - 0x1000;
        eh.Header.SizeOfRawData = 0x1000;
        for (int i = 0; i < _pe.Sections.Count; i++)
        {
            if (_pe.Sections[i].Header.VirtualAddress > eh.Header.VirtualAddress)
                _pe.Sections[i].Header.PointerToRawData -= diff;
        }

        eh.Header.SetName(".export");
        eh.Header.Characteristics &= ~(IMAGE_SCN_MEM_WRITE | IMAGE_SCN_MEM_EXECUTE);

        // Rebuild stream without the excess
        var ns = new MemoryStream();
        ns.Write(data, 0, (int)(pBase + 0x1000));
        int skipTo = (int)(pBase + 0x1000 + diff);
        if (skipTo < data.Length)
            ns.Write(data, skipTo, data.Length - skipTo);

        _stream = ns;
    }

    private void MapleCreateDataSections()
    {
        var data = _stream.ToArray();
        fixed (byte* pMem = data)
        {
            uint dataStart;
            uint found = Utils.FindStatic("10000000200000004000000060000000",
                pMem + 0x2000000, (uint)(data.Length - 0x2000000));

            if (found == 0)
            {
                if (_pe.OptionalHeader.MajorLinkerVersion == 6)
                {
                    dataStart = FindDataStartMSVC6(data);
                    if (dataStart == 0)
                        throw new Exception("Data section not found");
                }
                else
                {
                    uint searchOff = 0xB00000 - 0x400000;
                    found = Utils.FindStatic("2E3F41565F636F6D5F6572726F724040",
                        pMem + searchOff, (uint)(data.Length - searchOff));

                    uint foundTW = Utils.FindStatic(
                        "2E3F41563F245A4C69737440554D4150494E464F4043416374696F6E4672616D6540404040",
                        pMem + searchOff, (uint)(data.Length - searchOff));

                    if (foundTW != 0 && foundTW < found)
                        found = foundTW;

                    if (found == 0)
                        throw new Exception("Data section not found");

                    dataStart = found + searchOff - 8;
                    if ((dataStart & 0xFFF) == 0xB4 || (dataStart & 0xFFF) == 0xF8 || (dataStart & 0xFFF) == 0xFC)
                        dataStart &= 0xFFFFF000;
                    Utils.Log?.Invoke(LogMsgType.Info, "Old executable");
                }
            }
            else
                dataStart = found + 0x2000000;

            if ((dataStart & 0xFFF) != 0)
                throw new Exception($"Data section bytes found, but not aligned: {dataStart:X}");

            Utils.Log?.Invoke(LogMsgType.Good, $".data section at {dataStart:X8} (VA {dataStart + 0x400000:X8})");

            // Insert 2 section slots
            _pe.AddSectionSlot();
            _pe.AddSectionSlot();
            // Shift sections [3..end] = [1..end-2]
            for (int i = _pe.Sections.Count - 1; i >= 3; i--)
            {
                _pe.Sections[i].Header = _pe.Sections[i - 2].Header;
                _pe.Sections[i].Data = _pe.Sections[i - 2].Data;
            }

            // Find zero region
            uint zEnd = 0, zSize = 0;
            bool locked = false;
            uint a = _pe.Sections[3].Header.PointerToRawData - 1;
            while (a >= dataStart)
            {
                if (pMem[a] == 0)
                {
                    if (zSize == 0) zEnd = a + 1;
                    zSize++;
                    if (zSize > 0x2000) locked = true;
                }
                else
                {
                    if (locked) break;
                    zSize = 0;
                }
                if (a == 0) break;
                a--;
            }
            a++;

            if (zSize == 0)
                throw new Exception("Data section doesn't contain zeroes");

            if ((zEnd & 0xFFF) == 1) { zEnd--; zSize--; }
            if ((zEnd & 0xFFF) != 0)
                throw new Exception($"Real .data section end not found (got {zEnd:X} with a size of {zSize:X})");

            uint zStart = (a + 0x1000) & 0xFFFFF000;
            zSize -= zStart - a;

            uint gfidsSize = _pe.Sections[3].Header.PointerToRawData - zEnd;

            // .data at [2]
            uint dataSize = _pe.Sections[3].Header.PointerToRawData - dataStart - gfidsSize;
            _pe.Sections[2] = new PESection();
            _pe.Sections[2].Header.SetName(".data");
            _pe.Sections[2].Header.VirtualSize = dataSize;
            _pe.Sections[2].Header.VirtualAddress = dataStart;
            _pe.Sections[2].Header.PointerToRawData = dataStart;
            _pe.Sections[2].Header.SizeOfRawData = dataSize - zSize;
            _pe.Sections[2].Header.Characteristics = IMAGE_SCN_MEM_READ | IMAGE_SCN_MEM_WRITE | IMAGE_SCN_CNT_INITIALIZED_DATA;

            // .rdata at [1]
            uint rdataStart = _pe.OptionalHeader.BaseOfData;
            uint rdataSize = dataStart - rdataStart;
            _pe.Sections[1] = new PESection();
            _pe.Sections[1].Header.SetName(".rdata");
            _pe.Sections[1].Header.VirtualSize = rdataSize;
            _pe.Sections[1].Header.VirtualAddress = rdataStart;
            _pe.Sections[1].Header.PointerToRawData = rdataStart;
            _pe.Sections[1].Header.SizeOfRawData = rdataSize;
            _pe.Sections[1].Header.Characteristics = IMAGE_SCN_MEM_READ | IMAGE_SCN_CNT_INITIALIZED_DATA;

            // .gfids/.vmp
            if (gfidsSize != 0)
            {
                _pe.AddSectionSlot();
                for (int i = _pe.Sections.Count - 1; i >= 4; i--)
                {
                    _pe.Sections[i].Header = _pe.Sections[i - 1].Header;
                    _pe.Sections[i].Data = _pe.Sections[i - 1].Data;
                }

                string gName = _pe.OptionalHeader.MajorLinkerVersion >= 14 ? ".gfids" : ".vmp";
                _pe.Sections[3] = new PESection();
                _pe.Sections[3].Header.SetName(gName);
                _pe.Sections[3].Header.VirtualSize = gfidsSize;
                _pe.Sections[3].Header.VirtualAddress = zEnd;
                _pe.Sections[3].Header.PointerToRawData = zEnd;
                _pe.Sections[3].Header.SizeOfRawData = gfidsSize;
                _pe.Sections[3].Header.Characteristics = IMAGE_SCN_MEM_READ | IMAGE_SCN_MEM_WRITE | IMAGE_SCN_CNT_INITIALIZED_DATA;

                _pe.FileHeader.NumberOfSections += 3;
            }
            else
            {
                if (_pe.OptionalHeader.MajorLinkerVersion >= 14)
                    Utils.Log?.Invoke(LogMsgType.Fatal, ".gfids not found");
                _pe.FileHeader.NumberOfSections += 2;
            }

            _pe.Sections[0].Header.VirtualSize -= rdataSize + dataSize + gfidsSize;
            _pe.Sections[0].Header.SizeOfRawData -= rdataSize + dataSize + gfidsSize;

            _pe.Sections[0].Header.SetName(".text");
            _pe.Sections[0].Header.Characteristics &= ~IMAGE_SCN_MEM_WRITE;
        }
    }

    private uint FindDataStartMSVC6(byte[] data)
    {
        fixed (byte* pMem = data)
        {
            uint cinitCode = Utils.FindDynamic("68????????68??????00E8????????83C410C3",
                pMem + 0x100000, (uint)(data.Length - 0x100000));
            if (cinitCode == 0) return 0;

            cinitCode += 0x100000;
            uint result = BitConverter.ToUInt32(data, (int)(cinitCode + 6));
            if ((result & 0xFFF) != 0)
                return 0;
            result -= _pe.OptionalHeader.ImageBase;
            return result;
        }
    }

    private bool FindDynTLSMSVC14(out uint dynTLSInit)
    {
        dynTLSInit = 0;
        var data = _stream.ToArray();
        fixed (byte* pMem = data)
        {
            uint dynTLSCode = Utils.FindDynamic("8BF033FF393E74??56E8",
                pMem + 0x1000, (uint)(data.Length - 0x1000));
            if (dynTLSCode == 0)
            {
                Utils.Log?.Invoke(LogMsgType.Info, "DynTLS code sequence not found.");
                return false;
            }

            byte* codePtr = pMem + 0x1000 + dynTLSCode;
            if (*(codePtr - 5) != 0xE8)
            {
                Utils.Log?.Invoke(LogMsgType.Info, "DynTLS code sequence mismatch.");
                return false;
            }

            byte* getPtrFunc = codePtr + *(int*)(codePtr - 4);
            if (*getPtrFunc == 0xE9) // another indirection via jmp
                getPtrFunc = getPtrFunc + *(int*)(getPtrFunc + 1) + 5;
            if (*getPtrFunc != 0xB8)
            {
                Utils.Log?.Invoke(LogMsgType.Info, "DynTLS call analysis failed.");
                return false;
            }

            dynTLSInit = *(uint*)(getPtrFunc + 1) - _pe.OptionalHeader.ImageBase;
            uint dynTLSInitVal = *(uint*)(pMem + dynTLSInit);

            Utils.Log?.Invoke(LogMsgType.Info, $"[MSVC] dyn_tls_init at {dynTLSInitVal:X8}");

            if (dynTLSInitVal == 0)
                dynTLSInit = 0;

            return true;
        }
    }

    private void MSVCCreateDataSections()
    {
        uint baseOfData = _pe.OptionalHeader.BaseOfData;
        if (baseOfData > _pe.Sections[0].Header.VirtualAddress &&
            baseOfData < _pe.Sections[0].Header.VirtualAddress + _pe.Sections[0].Header.VirtualSize &&
            (baseOfData & 0xFFF) == 0)
        {
            if (!FindDynTLSMSVC14(out uint dynTLS))
                return;

            uint dataStart;
            if (dynTLS != 0)
                dataStart = (dynTLS + 0x1000) & ~0xFFFu;
            else
            {
                dataStart = baseOfData + 0x1000;
                Utils.Log?.Invoke(LogMsgType.Info, "Setting .rdata size to just 1000 (no reference point for actual size)");
            }

            _pe.AddSectionSlot();
            _pe.AddSectionSlot();
            for (int i = _pe.Sections.Count - 1; i >= 3; i--)
            {
                _pe.Sections[i].Header = _pe.Sections[i - 2].Header;
                _pe.Sections[i].Data = _pe.Sections[i - 2].Data;
            }

            _pe.FileHeader.NumberOfSections += 2;

            // .data at [2]
            uint dataSize = _pe.Sections[3].Header.PointerToRawData - dataStart;
            _pe.Sections[2] = new PESection();
            _pe.Sections[2].Header.SetName(".data");
            _pe.Sections[2].Header.VirtualSize = dataSize;
            _pe.Sections[2].Header.VirtualAddress = dataStart;
            _pe.Sections[2].Header.PointerToRawData = dataStart;
            _pe.Sections[2].Header.SizeOfRawData = dataSize;
            _pe.Sections[2].Header.Characteristics = IMAGE_SCN_MEM_READ | IMAGE_SCN_MEM_WRITE | IMAGE_SCN_CNT_INITIALIZED_DATA;

            // .rdata at [1]
            uint rdataStart = baseOfData;
            uint rdataSize = dataStart - rdataStart;
            _pe.Sections[1] = new PESection();
            _pe.Sections[1].Header.SetName(".rdata");
            _pe.Sections[1].Header.VirtualSize = rdataSize;
            _pe.Sections[1].Header.VirtualAddress = rdataStart;
            _pe.Sections[1].Header.PointerToRawData = rdataStart;
            _pe.Sections[1].Header.SizeOfRawData = rdataSize;
            _pe.Sections[1].Header.Characteristics = IMAGE_SCN_MEM_READ | IMAGE_SCN_CNT_INITIALIZED_DATA;

            _pe.Sections[0].Header.VirtualSize -= rdataSize + dataSize;
            _pe.Sections[0].Header.SizeOfRawData -= rdataSize + dataSize;

            Utils.Log?.Invoke(LogMsgType.Info,
                $".text : {_pe.Sections[0].Header.VirtualAddress:X8} ~ {_pe.Sections[0].Header.VirtualAddress + _pe.Sections[0].Header.VirtualSize:X8}");
            Utils.Log?.Invoke(LogMsgType.Info,
                $".rdata: {_pe.Sections[1].Header.VirtualAddress:X8} ~ {_pe.Sections[1].Header.VirtualAddress + _pe.Sections[1].Header.VirtualSize:X8}");
            Utils.Log?.Invoke(LogMsgType.Info,
                $".data : {_pe.Sections[2].Header.VirtualAddress:X8} ~ {_pe.Sections[2].Header.VirtualAddress + _pe.Sections[2].Header.VirtualSize:X8}");
        }
        else
            Utils.Log?.Invoke(LogMsgType.Info, "Assuming sections are not merged.");

        // Rename first section and remove WRITE
        _pe.Sections[0].Header.SetName(".text");
        _pe.Sections[0].Header.Characteristics &= ~IMAGE_SCN_MEM_WRITE;
    }

    public void DumpProcessCode(IntPtr hProcess)
    {
        nuint startAddr = _pe.OptionalHeader.ImageBase + _pe.Sections[0].Header.VirtualAddress;
        nuint endAddr = _pe.OptionalHeader.ImageBase + _pe.OptionalHeader.BaseOfData;
        nuint size = endAddr - startAddr;

        var buf = new byte[size];
        fixed (byte* pBuf = buf)
        {
            if (!ReadProcessMemory(hProcess, (IntPtr)(nint)startAddr, (IntPtr)pBuf, size, out nuint numRead) || numRead != size)
                throw new System.ComponentModel.Win32Exception();
        }

        _stream.Seek(_pe.Sections[0].Header.PointerToRawData, SeekOrigin.Begin);
        _stream.Write(buf, 0, buf.Length);

        string outFile = Path.ChangeExtension(_fileName, ".novm.exe");
        File.WriteAllBytes(outFile, _stream.ToArray());

        Utils.Log?.Invoke(LogMsgType.Good, $"Dumped {size:X} bytes.");
    }
}
#endif
