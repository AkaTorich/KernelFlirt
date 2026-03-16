using System.Runtime.InteropServices;
using static Magicmida.NativeApi;

namespace Magicmida;

public class RemoteModule
{
    public IntPtr Base;
    public IntPtr EndOff;
    public string Name = "";
    public Dictionary<IntPtr, string>? ExportTbl;
}

public class ImportThunk
{
    public RemoteModule Module;
    public string Name;
    public List<int> IATOffsets = new(); // offsets into the IAT buffer

    public ImportThunk(RemoteModule module)
    {
        Module = module;
        Name = module.Name;
    }
}

public unsafe class Dumper
{
#if CPUX86
    public const int MAX_IAT_SIZE = 5120 * 4;
#else
    public const int MAX_IAT_SIZE = 5120 * 8;
#endif

    private PROCESS_INFORMATION _process;
    private nuint _oep, _iat, _imageBase;
    private Dictionary<IntPtr, IntPtr> _forwards = new(256);
    private Dictionary<IntPtr, IntPtr> _forwardsType2 = new(16);
    private Dictionary<IntPtr, IntPtr> _forwardsKernelbase = new(32);
    private Dictionary<IntPtr, IntPtr> _forwardsWsock = new(16);
    private List<RemoteModule>? _allModules;
    private byte[]? _iatImage;
    private int _iatImageSize;

    private string _usrPath = "";
    private IntPtr _hUsr;

    public Dumper(PROCESS_INFORMATION process, nuint imageBase, nuint oep)
    {
        _process = process;
        _imageBase = imageBase;
        _oep = oep;

        if (Environment.OSVersion.Version.Major > 5)
        {
            _usrPath = Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly()?.Location ?? "")!, "mmusr32.dll");
            CopyFile(@"C:\Windows\system32\user32.dll", _usrPath, false);
            var h = LoadLibraryEx(_usrPath, IntPtr.Zero, 0x20);
            _hUsr = h - 2;
        }

        CollectNTFwd();
    }

    ~Dumper()
    {
        if (_hUsr != IntPtr.Zero)
        {
            FreeLibrary(_hUsr + 2);
            DeleteFile(_usrPath);
        }
    }

    public nuint IAT { get => _iat; set => _iat = value; }

    private bool RPM(nuint address, void* buf, nuint size)
    {
        return ReadProcessMemory(_process.hProcess, (IntPtr)(nint)address, (IntPtr)buf, size, out _);
    }

    private bool RPM(nuint address, byte[] buf, int size)
    {
        fixed (byte* p = buf) return RPM(address, p, (nuint)size);
    }

    // ==================== Forward collection ====================

    private void CollectNTFwd()
    {
        CollectForwards(_forwards, GetModuleHandle("kernel32.dll"), IntPtr.Zero);
        if (_hUsr != IntPtr.Zero)
            CollectForwards(_forwardsType2, GetModuleHandle("user32.dll"), _hUsr);
        CollectForwards(_forwards, GetModuleHandle("ole32.dll"), IntPtr.Zero);
        CollectForwards(_forwards, GetModuleHandle("advapi32.dll"), IntPtr.Zero);

        LoadAndCollect(_forwards, "netapi32.dll", "srvcli.dll", "samcli.dll");

        if (Environment.OSVersion.Version.Major >= 6)
            LoadAndCollect(_forwards, "crypt32.dll", "dpapi.dll");

        LoadAndCollect(_forwards, "dbghelp.dll", "dbgcore.dll");
        LoadAndCollect(_forwards, "setupapi.dll", "cfgmgr32.dll");
        LoadAndCollect(_forwardsWsock, "wsock32.dll", "ws2_32.dll");

        var kb = GetModuleHandle("kernelbase.dll");
        if (kb != IntPtr.Zero)
            CollectForwards(_forwardsKernelbase, kb, IntPtr.Zero);
    }

    private void LoadAndCollect(Dictionary<IntPtr, IntPtr> fwds, string mainDll, params string[] deps)
    {
        var handles = new List<IntPtr>();
        foreach (var dep in deps)
        {
            var h = LoadLibrary(dep);
            if (h != IntPtr.Zero) handles.Add(h);
        }
        var hMain = LoadLibrary(mainDll);
        if (hMain != IntPtr.Zero)
        {
            CollectForwards(fwds, hMain, IntPtr.Zero);
            FreeLibrary(hMain);
        }
        foreach (var h in handles) FreeLibrary(h);
    }

    private void CollectForwards(Dictionary<IntPtr, IntPtr> fwds, IntPtr hModReal, IntPtr hModScan)
    {
        if (hModReal == IntPtr.Zero) return;
        if (hModScan == IntPtr.Zero) hModScan = hModReal;

        byte* modScan = (byte*)hModScan;
        var dos = (IMAGE_DOS_HEADER*)modScan;
#if CPUX86
        var nt = (IMAGE_NT_HEADERS32*)(modScan + dos->e_lfanew);
        var expDir = (IMAGE_EXPORT_DIRECTORY*)(modScan + NativeApi.GetDataDirectory(ref nt->OptionalHeader, IMAGE_DIRECTORY_ENTRY_EXPORT).VirtualAddress);
#else
        var nt = (IMAGE_NT_HEADERS64*)(modScan + dos->e_lfanew);
        var expDir = (IMAGE_EXPORT_DIRECTORY*)(modScan + NativeApi.GetDataDirectory(ref nt->OptionalHeader, IMAGE_DIRECTORY_ENTRY_EXPORT).VirtualAddress);
#endif

        uint* addrFuncs = (uint*)(modScan + expDir->AddressOfFunctions);
        for (uint i = 0; i < expDir->NumberOfFunctions; i++)
        {
            byte* fwd = modScan + addrFuncs[i];
            var fwdStr = Marshal.PtrToStringAnsi((IntPtr)fwd) ?? "";
            int dotPos = fwdStr.IndexOf('.');
            if (fwdStr.Length >= 10 && fwdStr.Length <= 90 &&
                ((dotPos > 0 && dotPos < 15) || fwdStr.Contains("api-ms-win")) &&
                !fwdStr.Contains(".#"))
            {
                var moduleName = fwdStr.Substring(0, dotPos);
                var hMod = GetModuleHandleA(moduleName);
                if (hMod != IntPtr.Zero)
                {
                    var procName = fwdStr.Substring(dotPos + 1);
                    var procAddr = GetLocalProcAddr(hMod, procName);
                    if (procAddr != IntPtr.Zero)
                        fwds[(IntPtr)procAddr] = (IntPtr)((byte*)hModReal + addrFuncs[i]);
                }
            }
        }
    }

    private IntPtr GetLocalProcAddr(IntPtr hModule, string procName)
    {
        byte* mod = (byte*)hModule;
        var dos = (IMAGE_DOS_HEADER*)mod;
#if CPUX86
        var nt = (IMAGE_NT_HEADERS32*)(mod + dos->e_lfanew);
        var expDir = (IMAGE_EXPORT_DIRECTORY*)(mod + NativeApi.GetDataDirectory(ref nt->OptionalHeader, IMAGE_DIRECTORY_ENTRY_EXPORT).VirtualAddress);
#else
        var nt = (IMAGE_NT_HEADERS64*)(mod + dos->e_lfanew);
        var expDir = (IMAGE_EXPORT_DIRECTORY*)(mod + NativeApi.GetDataDirectory(ref nt->OptionalHeader, IMAGE_DIRECTORY_ENTRY_EXPORT).VirtualAddress);
#endif
        byte* off = (byte*)expDir - NativeApi.GetDataDirectory(ref nt->OptionalHeader, IMAGE_DIRECTORY_ENTRY_EXPORT).VirtualAddress;
        uint* a = (uint*)(off + expDir->AddressOfFunctions);
        uint* n = (uint*)(off + expDir->AddressOfNames);
        ushort* o = (ushort*)(off + expDir->AddressOfNameOrdinals);

        for (int i = 0; i < (int)expDir->NumberOfNames; i++)
        {
            var name = Marshal.PtrToStringAnsi((IntPtr)(off + n[i]));
            if (name == procName)
                return (IntPtr)(hModule + (nint)a[o[i]]);
        }
        return IntPtr.Zero;
    }

    // ==================== Module snapshot ====================

    private void TakeModuleSnapshot()
    {
        _allModules = new List<RemoteModule>();
        var hSnap = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE, _process.dwProcessId);
        var me = new MODULEENTRY32 { dwSize = (uint)Marshal.SizeOf<MODULEENTRY32>() };
        if (!Module32First(hSnap, ref me))
            throw new Exception("Module32First");
        do
        {
            if ((nuint)(nint)me.hModule != _imageBase)
            {
                _allModules.Add(new RemoteModule
                {
                    Base = me.modBaseAddr,
                    EndOff = me.modBaseAddr + (nint)me.modBaseSize,
                    Name = me.szModule.ToLower()
                });
            }
        } while (Module32Next(hSnap, ref me));
        CloseHandle(hSnap);
    }

    private bool TargetHasModule(string name)
    {
        if (_allModules == null) return false;
        return _allModules.Any(m => m.Name == name);
    }

    public bool IsAPIAddress(nuint address)
    {
        if (_allModules == null) TakeModuleSnapshot();
        foreach (var rm in _allModules!)
        {
            if (address >= (nuint)(nint)rm.Base && address < (nuint)(nint)rm.EndOff)
            {
                if (rm.ExportTbl == null) GatherModuleExportsFromRemoteProcess(rm);
                return rm.ExportTbl!.ContainsKey((IntPtr)(nint)address);
            }
        }
        return false;
    }

    private void GatherModuleExportsFromRemoteProcess(RemoteModule m)
    {
        m.ExportTbl = new Dictionary<IntPtr, string>();
        var head = new byte[0x1000];
        fixed (byte* pH = head)
        {
            RPM((nuint)(nint)m.Base, pH, 0x1000);
            var dos = (IMAGE_DOS_HEADER*)pH;
#if CPUX86
            var nt = (IMAGE_NT_HEADERS32*)(pH + dos->e_lfanew);
            var dd = NativeApi.GetDataDirectory(ref nt->OptionalHeader, IMAGE_DIRECTORY_ENTRY_EXPORT);
#else
            var nt = (IMAGE_NT_HEADERS64*)(pH + dos->e_lfanew);
            var dd = NativeApi.GetDataDirectory(ref nt->OptionalHeader, IMAGE_DIRECTORY_ENTRY_EXPORT);
#endif
            var expBuf = new byte[dd.Size];
            fixed (byte* pExp = expBuf)
            {
                RPM((nuint)(nint)m.Base + dd.VirtualAddress, pExp, dd.Size);
                byte* off = pExp - dd.VirtualAddress;
                var exp = (IMAGE_EXPORT_DIRECTORY*)pExp;

                uint* a = (uint*)(off + exp->AddressOfFunctions);
                uint* n = (uint*)(off + exp->AddressOfNames);
                ushort* o = (ushort*)(off + exp->AddressOfNameOrdinals);

                var named = new bool[exp->NumberOfFunctions];
                for (int i = 0; i < (int)exp->NumberOfNames; i++)
                {
                    uint funcIdx = o[i];
                    named[funcIdx] = true;
                    var name = Marshal.PtrToStringAnsi((IntPtr)(off + n[i])) ?? "";
                    m.ExportTbl[(IntPtr)((byte*)m.Base + a[funcIdx])] = name;
                }
                for (int i = 0; i < (int)exp->NumberOfFunctions; i++)
                {
                    if (!named[i])
                    {
                        uint ordinal = exp->Base + (uint)i;
                        m.ExportTbl[(IntPtr)((byte*)m.Base + a[i])] = "#" + ordinal;
                    }
                }
            }
        }
    }

    // ==================== IAT Processing ====================

    public uint DetermineIATSize(byte* iat)
    {
        uint lastValid = 0, i = 0;
        while (i < MAX_IAT_SIZE && (lastValid == 0 || i < lastValid + 0x100))
        {
            nuint val = IntPtr.Size == 4
                ? *(uint*)(iat + i)
                : (nuint)(*(ulong*)(iat + i));
            if (IsAPIAddress(val))
                lastValid = i;
            i += (uint)IntPtr.Size;
        }
        return lastValid + (uint)IntPtr.Size;
    }

    public PEHeader Process()
    {
        if (_iat == 0) throw new Exception("Must set IAT before calling Process()");

        // Read header from memory
        var sectionBuf = new byte[0x1000];
        fixed (byte* pSec = sectionBuf)
        {
            RPM(_imageBase, pSec, 0x1000);
            var pe = new PEHeader(pSec);
            pe.Sanitize();

            var iatBuf = new byte[MAX_IAT_SIZE];
            fixed (byte* pIAT = iatBuf) RPM(_iat, pIAT, (nuint)MAX_IAT_SIZE);

            uint iatSize;
            fixed (byte* p = iatBuf) { iatSize = DetermineIATSize(p); }
            Utils.Log?.Invoke(LogMsgType.Info, $"Determined IAT size: {iatSize:X}");

            ref var iatDir = ref pe.GetDataDirectory(IMAGE_DIRECTORY_ENTRY_IAT);
            iatDir.VirtualAddress = (uint)(_iat - _imageBase);
            iatDir.Size = iatSize + (uint)IntPtr.Size;

            if (_allModules == null) TakeModuleSnapshot();

            var thunks = new List<ImportThunk>();
            bool needNewThunk = false;
            int ptrSize = IntPtr.Size;

            for (uint i = 0; i < iatSize; i += (uint)ptrSize)
            {
                nuint val = ptrSize == 4
                    ? BitConverter.ToUInt32(iatBuf, (int)i)
                    : (nuint)BitConverter.ToUInt64(iatBuf, (int)i);

                IntPtr rangeChecker;
                IntPtr aPtr = (IntPtr)(nint)val;

                // Forward resolution
                if (_forwardsType2.TryGetValue(aPtr, out var fwd2))
                {
#if CPUX64
                    // Write resolved value back
                    BitConverter.GetBytes((ulong)(nuint)(nint)fwd2).CopyTo(iatBuf, (int)i);
#endif
                    rangeChecker = fwd2;
                }
                else
                {
                    if (_forwards.TryGetValue(aPtr, out var fwd))
                        WritePtrToIAT(iatBuf, (int)i, fwd);
                    else if (_forwardsKernelbase.TryGetValue(aPtr, out fwd))
                        WritePtrToIAT(iatBuf, (int)i, fwd);
                    else if (_forwardsWsock.TryGetValue(aPtr, out fwd) && TargetHasModule("wsock32.dll"))
                        WritePtrToIAT(iatBuf, (int)i, fwd);
                    rangeChecker = ReadPtrFromIAT(iatBuf, (int)i);
                }

                bool found = false;
                foreach (var rm in _allModules!)
                {
                    if ((nint)rangeChecker > (nint)rm.Base && (nint)rangeChecker < (nint)rm.EndOff)
                    {
                        if (rm.ExportTbl == null) GatherModuleExportsFromRemoteProcess(rm);
                        var curAddr = ReadPtrFromIAT(iatBuf, (int)i);
                        if (rm.ExportTbl!.ContainsKey(curAddr))
                        {
                            if (thunks.Count == 0 || thunks[thunks.Count - 1].Name != rm.Name || needNewThunk)
                            {
                                thunks.Add(new ImportThunk(rm));
                                needNewThunk = false;
                            }
                            found = true;
                            thunks[thunks.Count - 1].IATOffsets.Add((int)i);
                        }
                        else
                        {
                            Utils.Log?.Invoke(LogMsgType.Fatal, $"IAT {_iat + i:X} -> API {(nuint)(nint)curAddr:X} not in export table of {rm.Name}");
                        }
                        break;
                    }
                }
                if (!found) needNewThunk = true;
            }

            // Build import section
            var importSect = pe.CreateSection(".import", 0x1000);
            var section = new byte[importSect.Header.SizeOfRawData];
            int descOffset = 0;
            int strOffset = (thunks.Count + 1) * Marshal.SizeOf<IMAGE_IMPORT_DESCRIPTOR>();

            for (int ti = 0; ti < thunks.Count; ti++)
            {
                var thunk = thunks[ti];
                var rm = thunk.Module;

                // Write descriptor
                var desc = new IMAGE_IMPORT_DESCRIPTOR();
                desc.FirstThunk = (uint)((_iat - _imageBase) + (uint)thunk.IATOffsets[0]);
                desc.Name = (uint)pe.ConvertOffsetToRVAVector(importSect.Header.PointerToRawData + (uint)strOffset);
                WriteStruct(section, descOffset, desc);
                descOffset += Marshal.SizeOf<IMAGE_IMPORT_DESCRIPTOR>();

                // Write module name
                var nameBytes = System.Text.Encoding.ASCII.GetBytes(thunk.Name);
                Array.Copy(nameBytes, 0, section, strOffset, nameBytes.Length);
                strOffset += nameBytes.Length + 1;

                Utils.Log?.Invoke(LogMsgType.Info, $"Thunk {thunk.Name} - first import: {rm.ExportTbl![ReadPtrFromIAT(iatBuf, thunk.IATOffsets[0])]}");

                foreach (int off in thunk.IATOffsets)
                {
                    var addr = ReadPtrFromIAT(iatBuf, off);
                    var funcName = rm.ExportTbl[addr];

                    if (funcName.StartsWith("#"))
                    {
                        var ordIdx = uint.Parse(funcName.Substring(1));
                        WritePtrToIAT(iatBuf, off, (IntPtr)(nint)(long)(IMAGE_ORDINAL_FLAG | ordIdx));
                        continue;
                    }

                    strOffset += 2; // Hint
                    WritePtrToIAT(iatBuf, off, (IntPtr)(nint)(long)pe.ConvertOffsetToRVAVector(importSect.Header.PointerToRawData + (uint)(strOffset - 2)));

                    var fnBytes = System.Text.Encoding.ASCII.GetBytes(funcName);
                    Array.Copy(fnBytes, 0, section, strOffset, fnBytes.Length);
                    strOffset += fnBytes.Length + 1;

                    if (strOffset > section.Length - 0x100)
                    {
                        importSect.Header.SizeOfRawData += 0x1000;
                        importSect.Header.VirtualSize += 0x1000;
                        pe.OptionalHeader.SizeOfImage += 0x1000;
                        Array.Resize(ref section, (int)importSect.Header.SizeOfRawData);
                    }
                }
            }

            importSect.Data = section;

            ref var importDir = ref pe.GetDataDirectory(IMAGE_DIRECTORY_ENTRY_IMPORT);
            importDir.VirtualAddress = importSect.Header.VirtualAddress;
            importDir.Size = (uint)(thunks.Count * Marshal.SizeOf<IMAGE_IMPORT_DESCRIPTOR>());

            _iatImage = iatBuf;
            _iatImageSize = (int)iatSize;

            return pe;
        }
    }

    public void DumpToFile(string fileName, PEHeader pe, bool isDLL = false)
    {
        using var fs = new FileStream(fileName, FileMode.Create);
        uint size = pe.DumpSize;
        var buf = new byte[size];
        fixed (byte* p = buf)
            RPM(_imageBase, p, size);

        uint iatRawOffset = (uint)(_iat - _imageBase);
        uint delta = pe.TrimHugeSections(buf, ref iatRawOffset);
        size -= delta;
        fs.Write(buf, 0, (int)size);

        for (int i = pe.FileHeader.NumberOfSections; i < pe.Sections.Count; i++)
        {
            if (pe.Sections[i].Data != null)
                fs.Write(pe.Sections[i].Data, 0, (int)pe.Sections[i].Header.SizeOfRawData);
        }

        pe.FileHeader.NumberOfSections = (ushort)pe.Sections.Count;
        pe.OptionalHeader.AddressOfEntryPoint = (uint)(_oep - _imageBase);

        if (isDLL)
            pe.FileHeader.Characteristics |= (ushort)IMAGE_FILE_DLL;

        if ((pe.OptionalHeader.DllCharacteristics & 0x40) != 0)
        {
            Utils.Log?.Invoke(LogMsgType.Info, "Executable is ASLR-aware - disabling the flag in the dump");
            pe.OptionalHeader.DllCharacteristics = (ushort)(pe.OptionalHeader.DllCharacteristics & ~0x40);
        }

        pe.SaveToStream(fs);

        fs.Seek(iatRawOffset, SeekOrigin.Begin);
        fs.Write(_iatImage!, 0, _iatImageSize);
    }

    // ==================== Helpers ====================

    private static IntPtr ReadPtrFromIAT(byte[] iat, int offset)
    {
        return IntPtr.Size == 4
            ? (IntPtr)(nint)(int)BitConverter.ToUInt32(iat, offset)
            : (IntPtr)(nint)(long)BitConverter.ToUInt64(iat, offset);
    }

    private static void WritePtrToIAT(byte[] iat, int offset, IntPtr value)
    {
        if (IntPtr.Size == 4)
            BitConverter.GetBytes((uint)(nuint)(nint)value).CopyTo(iat, offset);
        else
            BitConverter.GetBytes((ulong)(nuint)(nint)value).CopyTo(iat, offset);
    }

    private static void WriteStruct<T>(byte[] buf, int offset, T s) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        var handle = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try { Marshal.StructureToPtr(s, handle.AddrOfPinnedObject() + offset, false); }
        finally { handle.Free(); }
    }
}

public unsafe class DumperDotnet
{
    private PROCESS_INFORMATION _process;
    private nuint _imageBase;

    public DumperDotnet(PROCESS_INFORMATION process, nuint imageBase)
    {
        _process = process;
        _imageBase = imageBase;
    }

    public void DumpToFile(string fileName)
    {
        var header = new byte[0x1000];
        fixed (byte* pH = header)
        {
            if (!ReadProcessMemory(_process.hProcess, (IntPtr)(nint)_imageBase, (IntPtr)pH, 0x1000, out _))
                throw new Exception("DumpToFile header RPM failed");

            var pe = new PEHeader(pH);
            var lastSect = pe.Sections[pe.FileHeader.NumberOfSections - 1];
            uint size = lastSect.Header.VirtualAddress + lastSect.Header.VirtualSize;

            using var fs = new FileStream(fileName, FileMode.Create);
            var buf = new byte[size];

            var ptr = (byte*)_imageBase;
            uint done = 0;
            while (done < size)
            {
                if (VirtualQueryEx(_process.hProcess, (IntPtr)(ptr), out var mbi, (nuint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) == 0)
                    throw new Exception($"VirtualQueryEx failed at {(nuint)(nint)(IntPtr)ptr:X}");
                if (mbi.RegionSize == 0)
                    throw new Exception("VirtualQueryEx returned zero region");

                uint chunkSize = (uint)Math.Min(size - done, (uint)mbi.RegionSize);
                if (mbi.State == MEM_COMMIT)
                {
                    fixed (byte* pBuf = &buf[done])
                        if (!ReadProcessMemory(_process.hProcess, (IntPtr)ptr, (IntPtr)pBuf, chunkSize, out _))
                            throw new Exception("DumpToFile RPM failed");
                }
                else if (mbi.State == MEM_RESERVE)
                {
                    Array.Clear(buf, (int)done, (int)chunkSize);
                }

                done += (uint)mbi.RegionSize;
                ptr += mbi.RegionSize;
            }

            fs.Write(buf, 0, (int)size);

            uint physicalSize = size;
            pe.FileAlign(ref physicalSize);
            if (size < physicalSize)
            {
                var padBytes = new byte[physicalSize - size];
                fs.Write(padBytes, 0, padBytes.Length);
            }

            uint imageSize = physicalSize;
            pe.SectionAlign(ref imageSize);
            pe.OptionalHeader.SizeOfImage = imageSize;
            pe.Sections[0].Rename(".text");

            Utils.Log?.Invoke(LogMsgType.Info, $"Output has {pe.FileHeader.NumberOfSections} sections, determined size to be 0x{size:X}");

            pe.SaveToStream(fs);
        }
    }
}
