using System.Runtime.InteropServices;
using static Magicmida.NativeApi;

namespace Magicmida;

#if CPUX86
public struct EFLRecord
{
    public nuint Address;
    public byte[]? Original;
}

public class TTMDebugger : TMCommon
{
    private int _wow64;

    // Themida state
    private int _baseAccessCount;
    private bool _compressed;
    private nuint _base1, _repEIP, _ntQIP;
    private IntPtr _closeHandleAPI, _allocMemAPI, _allocHeapAPI, _kiFastSystemCall, _ntSIT, _ntQIP64, _virtualProtectAPI;
    private IntPtr _corExeMain;
    private IntPtr _cmpImgBase, _magicJump, _magicJumpV1;
    private bool _baseAccessed, _newVer, _ancientVer;
    private int _allocMemCounter;
    private nuint _iJumper, _mj1, _mj2, _mj3, _mj4;
    private EFLRecord[] _efls = new EFLRecord[3];
    private bool _themidaV2BySections;

    private nuint _guardStart, _guardEnd;
    private uint _guardProtection;
    private bool _guardStepping;

    private uint _tlsAddressesOfCallbacks;
    private uint _tlsCounter, _tlsTotal;

    public TTMDebugger(string executable, string parameters, bool createData)
        : base(executable, parameters, Utils.Log!)
    {
        FCreateDataSections = createData;
        FGuardAddrs = new List<nuint>();
    }

    protected override void OnDebugStart(ref IntPtr hPE, IntPtr hThread)
    {
        if (Path.GetFileName(FExecutable).Length >= 50)
            Log(LogMsgType.Info, "WARNING: Long filenames crash some Themida versions (recommend <50)");

        _closeHandleAPI = GetProcAddress(GetModuleHandle("kernel32.dll"), "CloseHandle");
        SetBreakpoint((nuint)(nint)_closeHandleAPI, HWBPType.Execute, false);

        var mmPath = Path.GetDirectoryName((System.Reflection.Assembly.GetEntryAssembly()?.Location ?? ""))!;
        if (File.Exists(Path.Combine(mmPath, "InjectorCLIx86.exe")))
        {
            Log(LogMsgType.Good, "Applying ScyllaHide");
            ShellExecute(IntPtr.Zero, "open",
                Path.Combine(mmPath, "InjectorCLIx86.exe"),
                $"pid:{FProcess.dwProcessId} {Path.Combine(mmPath, "HookLibraryx86.dll")} nowait",
                null, (int)SW_HIDE);
        }
        else
        {
            _ntSIT = GetProcAddress(GetModuleHandle("ntdll.dll"), "ZwSetInformationThread");
            SetBreakpoint((nuint)(nint)_ntSIT, HWBPType.Execute, false);
            _kiFastSystemCall = GetProcAddress(GetModuleHandle("ntdll.dll"), "KiFastSystemCall");

            if (!(IsWow64Process(FProcess.hProcess, out _wow64) && _wow64 != 0))
            {
                SetSoftBP(_kiFastSystemCall);
                // Read NtQIP syscall number
                var ptr = GetProcAddress(GetModuleHandle("ntdll.dll"), "ZwQueryInformationProcess");
                uint syscallNum = 0;
                unsafe { RPM((nuint)(nint)ptr + 1, &syscallNum, 4); }
                _ntQIP = syscallNum;
            }
            else
            {
                _ntQIP64 = GetProcAddress(GetModuleHandle("ntdll.dll"), "ZwQueryInformationProcess");
                SetBreakpoint((nuint)(nint)_ntQIP64, HWBPType.Execute, false);
            }
        }

        _virtualProtectAPI = GetProcAddress(GetModuleHandle("kernel32.dll"), "VirtualProtect");
        FSleepAPI = (nuint)(nint)GetProcAddress(GetModuleHandle("kernel32.dll"), "Sleep");
        FlstrlenAPI = (nuint)(nint)GetProcAddress(GetModuleHandle("kernel32.dll"), "lstrlen");

        UpdateDR(hThread);
        TMInit(ref hPE);
    }

    private unsafe void TMInit(ref IntPtr hPE)
    {
        if (hPE == IntPtr.Zero || hPE == (IntPtr)(-1))
        {
            hPE = CreateFile(FExecutable, GENERIC_READ, FILE_SHARE_READ, IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
            if (hPE == (IntPtr)(-1))
                throw new Exception($"CreateFile code {Marshal.GetLastWin32Error()}");
        }
        SetFilePointer(hPE, 0, IntPtr.Zero, FILE_BEGIN);

        var buf = new byte[0x1000];
        fixed (byte* p = buf)
        {
            if (!ReadFile(hPE, (IntPtr)p, 0x1000, out _, IntPtr.Zero))
                throw new Exception($"ReadFile failed! Code: {Marshal.GetLastWin32Error()}");

            byte* pNT = p + ((IMAGE_DOS_HEADER*)p)->e_lfanew;
            var nt = (IMAGE_NT_HEADERS32*)pNT;
            var sect = (IMAGE_SECTION_HEADER*)(pNT + sizeof(IMAGE_NT_HEADERS32));

            InitPEDetails(pNT);

            if (FPESections.Length >= 2 &&
                FPESections[FPESections.Length - 1].VirtualSize == 0x1000 && FPESections[FPESections.Length - 1].SizeOfRawData == 0x1000 &&
                FPESections[FPESections.Length - 2].VirtualSize == 0x1000)
                _themidaV2BySections = true;

            FBaseOfData = nt->OptionalHeader.BaseOfData;
            _compressed = FPESections[0].VirtualSize != FPESections[0].SizeOfRawData;
            _base1 = FPESections[0].VirtualSize;

            // PE Header Antidump fix
            byte* test = (byte*)((nuint)((byte*)(&sect[2].Name[1]) - p) + FImageBase);
            uint oldProt;
            VirtualProtectEx(FProcess.hProcess, (IntPtr)test, 1, PAGE_READWRITE, out oldProt);
            uint val = (uint)'p';
            nuint w;
            if (!WriteProcessMemory(FProcess.hProcess, (IntPtr)test, (IntPtr)(&val), 1, out w))
                throw new Exception($"Fixing PE header antidump failed! Code: {Marshal.GetLastWin32Error()}");

            // TLS handling
            ref var tlsDir32 = ref NativeApi.GetDataDirectory(ref nt->OptionalHeader, IMAGE_DIRECTORY_ENTRY_TLS);
            if (tlsDir32.Size > 0)
            {
                var tls = new IMAGE_TLS_DIRECTORY32();
                RPM(FImageBase + tlsDir32.VirtualAddress, &tls, (uint)Math.Min(tlsDir32.Size, (uint)sizeof(IMAGE_TLS_DIRECTORY32)));
                var tlsDist = (int)(tls.AddressOfCallBacks - tls.AddressOfIndex);
                if (tlsDist > 0 && tlsDist <= 4 * 4)
                {
                    _tlsTotal = (uint)(tlsDist / 4);
                    _tlsAddressesOfCallbacks = tls.AddressOfCallBacks;
                    Log(LogMsgType.Info, $"Expecting up to {_tlsTotal} TLS entries");
                }
            }
        }

        _allocMemAPI = GetProcAddress(GetModuleHandle("ntdll.dll"), "ZwAllocateVirtualMemory");
        _allocHeapAPI = GetProcAddress(GetModuleHandle("ntdll.dll"), "RtlAllocateHeap");
    }

    protected override unsafe void OnHardwareBreakpoint(IntPtr hThread, nuint bpa, ref CONTEXT c)
    {
        var eip = (IntPtr)(nint)c.Eip;

        if (eip == _closeHandleAPI)
        {
            uint buf = 0;
            RPM(c.Esp, &buf, 4);
            if (buf < (uint)FImageBoundary)
            {
                ResetBreakpoint(eip);
                if (_compressed)
                    SetBreakpoint(FImageBase + 0x1000, HWBPType.Access);
                else
                    SetBreakpoint((nuint)(nint)_allocMemAPI);
            }
        }
        else if (eip == _allocMemAPI)
        {
            uint buf = 0;
            RPM(c.Ebp, &buf, 4);
            if (Math.Abs((int)(buf - c.Ebp)) < 0x40)
                RPM(buf + 4, &buf, 4);
            else
                RPM(c.Ebp + 4, &buf, 4);
            Log(LogMsgType.Info, $"AllocMem called from {buf:X8}");

            if (buf < (uint)FImageBoundary)
            {
                _allocMemCounter++;
                if (_allocMemCounter == (_compressed ? 4 : 5))
                {
                    ResetBreakpoint(_allocMemAPI);
                    if (!FThemidaV3)
                    {
                        Log(LogMsgType.Good, "IAT fixing started.");
                        TMIATFix(c.Eip);
                    }
                    else
                        InstallCodeSectionGuard(PAGE_NOACCESS);
                }
            }
        }
        else if (eip == _cmpImgBase)
        {
            ResetBreakpoint(_cmpImgBase);
            TMIATFix3(c.Eip);
        }
        else if (eip == _magicJump)
        {
            ResetBreakpoint(_magicJump);
            TMIATFix4();
        }
        else if (eip == (IntPtr)(nint)(long)_mj1)
        {
            ResetBreakpoint((IntPtr)(nint)(long)_mj1);
            TMIATFix5(c.Eax);
        }
        else if (eip == _magicJumpV1)
        {
            ResetBreakpoint(_magicJumpV1);
            TMIATFixThemidaV1((nuint)(nint)_magicJumpV1);
        }
        else if (eip == _allocHeapAPI)
        {
            Log(LogMsgType.Fatal, "Special IAT fix failed, perhaps not needed for this binary");
            ResetBreakpoint(_allocHeapAPI);
            SoftBPClear();
            InstallCodeSectionGuard(PAGE_NOACCESS);
        }
        else if (eip == _virtualProtectAPI)
        {
            InstallCodeSectionGuard(PAGE_NOACCESS);
        }
        else if (eip == _ntSIT)
        {
            uint ret = 0, infoClass = 0;
            RPM(c.Esp, &ret, 4);
            RPM(c.Esp + 8, &infoClass, 4);
            if (ret < (uint)FImageBoundary && infoClass == 17)
            {
                Log(LogMsgType.Good, "Ignoring NtSetInformationThread(ThreadHideFromDebugger)");
                c.Esp += 5 * 4;
                c.Eip = ret;
                c.Eax = (uint)STATUS_SUCCESS;
                c.ContextFlags = CONTEXT_CONTROL | CONTEXT_INTEGER;
                SetThreadContext(hThread, ref c);
            }
        }
        else if (_wow64 != 0 && eip == _ntQIP64)
        {
            uint ret = 0, infoClass = 0, bufAddr = 0;
            RPM(c.Esp, &ret, 4);
            RPM(c.Esp + 8, &infoClass, 4);
            if (infoClass == 7 || infoClass == 30)
            {
                Log(LogMsgType.Good, infoClass == 7 ? "Faking ProcessDebugPort" : "Faking ProcessDebugObjectHandle");
                RPM(c.Esp + 12, &bufAddr, 4);
                uint zero = 0;
                WriteProcessMemory(FProcess.hProcess, (IntPtr)(nint)(int)bufAddr, (IntPtr)(&zero), 4, out _);
                c.Esp += 6 * 4;
                c.Eip = ret;
                c.Eax = infoClass == 7 ? (uint)STATUS_SUCCESS : unchecked((uint)STATUS_PORT_NOT_SET);
                c.ContextFlags = CONTEXT_CONTROL | CONTEXT_INTEGER;
                SetThreadContext(hThread, ref c);
            }
        }
        else if (bpa == FImageBase + 0x1000)
        {
            _baseAccessCount++;
            Log(LogMsgType.Good, $"Accessed text base from {eip:X}");
            if (!_baseAccessed)
            {
                ResetBreakpoint((IntPtr)(nint)(long)(FImageBase + 0x1000));
                SetBreakpoint(FImageBase + 0x1000, HWBPType.Write);
                _baseAccessed = true;
            }
            else
            {
                if (TMFinderCheck(ref c))
                {
                    ResetBreakpoint((IntPtr)(nint)(long)(FImageBase + 0x1000));
                    SetBreakpoint((nuint)(nint)_allocMemAPI, HWBPType.Execute);
                    _repEIP = c.Eip;
                }
                else if (_baseAccessCount == 3 && !_themidaV2BySections)
                {
                    FThemidaV3 = true;
                    Log(LogMsgType.Info, "Assuming Themida v3");
                    SelectThemidaSection(c.Eip);
                    ResetBreakpoint((IntPtr)(nint)(long)(FImageBase + 0x1000));
                    SetBreakpoint((nuint)(nint)_allocMemAPI, HWBPType.Execute);
                }
            }
        }
        else
        {
            Log(LogMsgType.Info, $"Accessed {bpa:X} from {eip:X}");
        }
    }

    protected override uint OnSinglestep(nuint bpa)
    {
        if (_guardStepping)
        {
            uint oldProt;
            VirtualProtectEx(FProcess.hProcess, (IntPtr)(nint)_guardStart, _guardEnd - _guardStart, _guardProtection, out oldProt);
            _guardStepping = false;
            return DBG_CONTINUE;
        }
        return base.OnSinglestep(bpa);
    }

    protected override uint OnAccessViolation(IntPtr hThread, EXCEPTION_RECORD excRec)
    {
        if (IsGuardedAddress(excRec.GetExceptionInformation(1)))
            return ProcessGuardedAccess(hThread, excRec);
        return base.OnAccessViolation(hThread, excRec);
    }

    protected override void OnDLLLoad(string fileName, IntPtr baseAddress)
    {
        if (fileName.IndexOf(@"\mscoree.dll", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            Log(LogMsgType.Info, "This might be a .NET program - setting _CorExeMain BP");
            var hCorEE = LoadLibrary("mscoree.dll");
            if (hCorEE == (IntPtr)(nint)(long)(nuint)(nint)baseAddress)
            {
                _corExeMain = GetProcAddress(hCorEE, "_CorExeMain");
                SetSoftBP(_corExeMain);
            }
            else
                Log(LogMsgType.Fatal, "DLL was loaded at different base than in target!");
        }
        base.OnDLLLoad(fileName, baseAddress);
    }

    protected override SoftBPAction OnSoftwareBreakpoint(IntPtr hThread, IntPtr bpa)
    {
        if (bpa == _corExeMain)
        {
            new DumperDotnet(FProcess, FImageBase).DumpToFile(
                Path.Combine(Path.GetDirectoryName(FExecutable)!,
                    Path.GetFileNameWithoutExtension(FExecutable) + "U" + Path.GetExtension(FExecutable)));
            Log(LogMsgType.Good, ".NET process dumped.");
            FHideThreadEnd = true;
            TerminateProcess(FProcess.hProcess, 0);
            return SoftBPAction.ClearContinue;
        }

        // Regular software breakpoint handling for IAT patching
        var c = new CONTEXT { ContextFlags = CONTEXT_FULL };
        GetThreadContext(hThread, ref c);

        // Simplified: install code section guard and continue
        if (!_ancientVer)
            InstallCodeSectionGuard(PAGE_READONLY);
        else
            InstallCodeSectionGuard(PAGE_NOACCESS);

        SoftBPClear();
        return SoftBPAction.ClearContinue;
    }

    protected override unsafe bool TraceIsAtAPI(Tracer tracer, ref CONTEXT c)
    {
        if (tracer.Counter > 100 && tracer.Counter < 5000)
        {
            uint insnData = 0;
            RPM(c.Eip, &insnData, 4);
            if (insnData == 0x4CB10FF0) // lock cmpxchg
            {
                FTraceInVM = true;
                Log(LogMsgType.Info, "Trace ran into Themida VM, stopping");
                return true;
            }
        }

        if (c.Esp < (uint)FTraceStartSP && (c.Eip == (uint)FSleepAPI || c.Eip == (uint)FlstrlenAPI))
        {
            Log(LogMsgType.Info, $"Skipping anti-trace API at {c.Eip:X8}");
            uint retAddr = 0;
            RPM(c.Esp, &retAddr, 4);
            c.Esp += 8;
            c.Eip = retAddr;
        }

        bool result = !TMSectR.Contains(c.Eip);
        if (result && c.Esp < (uint)FTraceStartSP)
        {
            Log(LogMsgType.Info, $"Warning: Might have encountered new fake API at {c.Eip:X8}");
            result = false;
        }
        if (result)
            FTracedAPI = c.Eip;
        return result;
    }

    // ==================== Themida-specific methods ====================

    private unsafe bool TMFinderCheck(ref CONTEXT c)
    {
        ushort rep = 0;
        RPM(c.Eip, &rep, 2);
        if (rep == 0xA4F3) return true;

        nuint tmp = FImageBase + 0x1000 + _base1 - 4;
        return c.Eax == (uint)tmp || c.Ebx == (uint)tmp || c.Ecx == (uint)tmp ||
               c.Edx == (uint)tmp || c.Esi == (uint)tmp || c.Edi == (uint)tmp;
    }

    private unsafe void SelectThemidaSection(nuint eip)
    {
        for (int i = 0; i < FPESections.Length; i++)
        {
            if (eip >= FPESections[i].VirtualAddress + FImageBase &&
                eip < FPESections[i].VirtualAddress + FPESections[i].VirtualSize + FImageBase)
            {
                TMSectR = new MemoryRegion(FPESections[i].VirtualAddress + FImageBase, FPESections[i].VirtualSize);
                var mem = Marshal.AllocHGlobal((int)TMSectR.Size);
                TMSect = (byte*)mem;
                if (!RPM(TMSectR.Address, TMSect, TMSectR.Size))
                {
                    Marshal.FreeHGlobal(mem);
                    TMSect = null;
                }
                Log(LogMsgType.Info, $"TMSect: {TMSectR.Address:X} ({TMSectR.Size} bytes)");

                // Check for ancient Themida
                fixed (byte* pName = FPESections[i].Name)
                {
                    var sName = System.Text.Encoding.ASCII.GetString(pName, 8);
                    if (sName.StartsWith("Themida "))
                    {
                        _ancientVer = true;
                        Log(LogMsgType.Info, "Ancient Themida detected.");
                    }
                }
                break;
            }
        }
        if (TMSect == null) throw new Exception("FATAL NO DATA");
    }

    private unsafe uint FindDynamicTM(string pattern, nuint off = 0)
    {
        if (off != 0) off -= TMSectR.Address;
        uint result = Utils.FindDynamic(pattern, TMSect + off, TMSectR.Size - (uint)off);
        if (result > 0) result += (uint)(TMSectR.Address + off);
        return result;
    }

    private unsafe uint FindStaticTM(string pattern, nuint off = 0)
    {
        if (off != 0) off -= TMSectR.Address;
        uint result = Utils.FindStatic(pattern, TMSect + off, TMSectR.Size - (uint)off);
        if (result > 0) result += (uint)(TMSectR.Address + off);
        return result;
    }

    private void TMIATFix(nuint eip)
    {
        SelectThemidaSection(eip);
        TMIATFix2();
    }

    private unsafe void TMIATFix2()
    {
        uint compareJumpsNew = FindDynamicTM("74??8B8D????????8B093B8D????????7410");
        if (compareJumpsNew == 0)
        {
            uint cmpEax = FindStaticTM("3D000001000F83");
            if (cmpEax == 0) { Log(LogMsgType.Fatal, "\"cmp eax, 10000\" not found"); return; }
            Log(LogMsgType.Good, $"cmp eax, 10000 at {cmpEax:X8}");
            _magicJumpV1 = (IntPtr)(nint)(int)FindDynamicTM("3B8D????????0F84????0000", cmpEax);
            if (_magicJumpV1 == IntPtr.Zero) { Log(LogMsgType.Fatal, "First ImageBase compare jump not found"); return; }
            SetBreakpoint((nuint)(nint)_magicJumpV1, HWBPType.Execute);
        }
        else
        {
            Log(LogMsgType.Good, $"ImageBase compare jumps found at: {compareJumpsNew:X8}");
            _cmpImgBase = (IntPtr)(nint)(int)compareJumpsNew;
            SetBreakpoint(compareJumpsNew, HWBPType.Execute);
        }
    }

    private unsafe void TMIATFix3(nuint eip)
    {
        if (eip == 0) throw new Exception("Cannot call TMIATFix3 with EIP=0");
        uint x = (uint)(eip - TMSectR.Address);
        x = (uint)eip + Utils.FindDynamic("4B0F84????0000", TMSect + x, TMSectR.Size - x);
        if (x == eip)
            Log(LogMsgType.Fatal, "Magic jumps not found");
        else
        {
            Log(LogMsgType.Good, $"Magic jumps detected at: {x:X8}");
            _magicJump = (IntPtr)(nint)(int)x;
            SetBreakpoint(x, HWBPType.Execute);
        }
    }

    private unsafe void TMIATFix4()
    {
        uint res = FindStaticTM("83F8500F82");
        if (res == 0) throw new Exception("\"cmp eax, 50\" not found");
        Log(LogMsgType.Good, $"cmp eax, 50 detected at: {res:X8}");
        Log(LogMsgType.Good, "[LCF-AT] Fixing IAT with the Fast IAT Patch Method.");

        res = FindDynamicTM("3985????????0F84");
        if (res == 0) throw new Exception("Not found");
        _iJumper = res + 6;

        nuint off = res;
        res = FindStaticTM("2BD90F84", off);
        if (res == 0) res = FindStaticTM("29CB0F84", off);
        if (res == 0) throw new Exception("Both patterns not found");
        _mj2 = res;
        uint jumper = 6 + (uint)_mj2 + 2 + *(uint*)(TMSect + _mj2 + 4 - TMSectR.Address);

        off = res + 1;
        res = FindStaticTM("2BD90F84", off);
        if (res == 0) res = FindStaticTM("29CB0F84", off);
        if (res == 0) throw new Exception("Both patterns not found (2)");
        _mj3 = res;

        off = res + 1;
        res = FindStaticTM("2BD90F84", off);
        if (res == 0) res = FindStaticTM("29CB0F84", off);
        if (res == 0) throw new Exception("Both patterns not found (3)");
        _mj4 = res;

        off = _mj2;
        while (*(ushort*)(TMSect + off - TMSectR.Address) != 0x840F ||
               6 + off + *(uint*)(TMSect + off + 2 - TMSectR.Address) != jumper)
            off--;
        _mj1 = off;

        Log(LogMsgType.Info, $"MJ1 {_mj1:X8}");
        Log(LogMsgType.Info, $"MJ2 {_mj2:X8}");
        Log(LogMsgType.Info, $"MJ3 {_mj3:X8}");
        Log(LogMsgType.Info, $"MJ4 {_mj4:X8}");

        // Check version
        byte b1 = 0, b2 = 0, b3 = 0, b4 = 0;
        RPM(_mj1 - 1, &b1, 1);
        RPM(_mj2, &b2, 1);
        RPM(_mj3, &b3, 1);
        RPM(_mj4, &b4, 1);
        _newVer = (b1 == 0x4B && b2 == 0x2B && b3 == 0x2B && b4 == 0x2B) || b2 == 0x29;

        if (FindDynamicTM("68????????E9??????FF68????????E9??????FF68????????E9??????FF") != 0)
            _newVer = false;
        if (FindDynamicTM("68????????68????????E9??????FF68????????68????????E9??????FF") != 0)
            _newVer = true;

        Log(LogMsgType.Info, _newVer ? "Newer Themida version found." : "Older Themida version found.");

        // Set soft breakpoints
        res = FindStaticTM("3BC89CE9");
        if (res == 0)
        {
            nuint searchOff = TMSectR.Address;
            bool valid = GetIATBPAddressNew(ref searchOff);
            while (searchOff != 0)
            {
                if (valid)
                {
                    Log(LogMsgType.Info, $"SetSoft: {searchOff:X8}");
                    SetSoftBP((IntPtr)(nint)(long)searchOff);
                }
                searchOff += 2;
                valid = GetIATBPAddressNew(ref searchOff);
            }
        }
        else
        {
            do
            {
                res += 3;
                Log(LogMsgType.Info, $"SetSoft : {res:X8}");
                SetSoftBP((IntPtr)(nint)(int)res);
                res = FindStaticTM("3BC89CE9", res);
            } while (res != 0);
        }

        InstallCodeSectionGuard(PAGE_READONLY);
        SetBreakpoint(_mj1, HWBPType.Execute);
    }

    private unsafe void TMIATFix5(nuint eax)
    {
        Log(LogMsgType.Info, $"First API in eax: {eax:X8}");
        ulong buf = 0xE990;
        WriteProcessMemory(FProcess.hProcess, (IntPtr)(nint)_iJumper, (IntPtr)(&buf), 2, out _);
        buf = 0x909090909090;
        WriteProcessMemory(FProcess.hProcess, (IntPtr)(nint)(long)_mj1, (IntPtr)(&buf), 6, out _);
        WriteProcessMemory(FProcess.hProcess, (IntPtr)(nint)(long)(_mj2 + 2), (IntPtr)(&buf), 6, out _);
        WriteProcessMemory(FProcess.hProcess, (IntPtr)(nint)(long)(_mj3 + 2), (IntPtr)(&buf), 6, out _);
        WriteProcessMemory(FProcess.hProcess, (IntPtr)(nint)(long)(_mj4 + 2), (IntPtr)(&buf), 6, out _);
        FlushInstructionCache(FProcess.hProcess, (IntPtr)(nint)(long)_mj1, 6);
        Log(LogMsgType.Good, $"IAT Jumper was found & fixed at {_iJumper:X8}");
    }

    private unsafe void TMIATFixThemidaV1(nuint baseCompare1)
    {
        nuint bc2 = FindDynamicTM("3B8D????????0F84????0000", baseCompare1 + 12);
        if (bc2 == 0) throw new Exception("[Themida 1.x] BaseCompare2 not found");
        nuint bc3 = FindDynamicTM("3B8D????????0F84????0000", bc2 + 12);
        if (bc3 == 0) throw new Exception("[Themida 1.x] BaseCompare3 not found");

        _iJumper = FindDynamicTM("3985????????0F84");
        if (_iJumper == 0) throw new Exception("[Themida 1.x] IAT jumper not found");

        ulong nops = 0x909090909090;
        WriteProcessMemory(FProcess.hProcess, (IntPtr)(nint)(_iJumper + 6), (IntPtr)(&nops), 2, out _);
        nops = 0xE990;
        // Fix: write 2-byte jz->jmp at ijumper+6
        WriteProcessMemory(FProcess.hProcess, (IntPtr)(nint)(_iJumper + 6), (IntPtr)(&nops), 2, out _);

        nops = 0x909090909090;
        WriteProcessMemory(FProcess.hProcess, (IntPtr)(nint)(baseCompare1 + 6), (IntPtr)(&nops), 6, out _);
        WriteProcessMemory(FProcess.hProcess, (IntPtr)(nint)(bc2 + 6), (IntPtr)(&nops), 6, out _);
        WriteProcessMemory(FProcess.hProcess, (IntPtr)(nint)(bc3 + 6), (IntPtr)(&nops), 6, out _);
        Log(LogMsgType.Good, $"IAT Jumper was found & fixed at {_iJumper:X8}");

        _guardStart = FImageBase + FPESections[0].VirtualAddress;
        _guardEnd = FImageBase + 0x100000;
        _guardProtection = PAGE_NOACCESS;
        VirtualProtectEx(FProcess.hProcess, (IntPtr)(nint)_guardStart, _guardEnd - _guardStart, _guardProtection, out _);
    }

    private unsafe bool GetIATBPAddressNew(ref nuint res)
    {
        byte b;
        do
        {
            res = FindDynamicTM("39??9C", res);
            if (res == 0) return false;
            RPM(res - 1, &b, 1);
            res++;
        } while (b == 0x66);
        res--;

        if (res > _mj1) return false;
        res += 3;
        return true;
    }

    private void InstallCodeSectionGuard(uint protection)
    {
        _guardStart = FImageBase + FPESections[0].VirtualAddress;
        _guardEnd = FImageBase + FBaseOfData;
        _guardProtection = protection;
        VirtualProtectEx(FProcess.hProcess, (IntPtr)(nint)_guardStart, _guardEnd - _guardStart, _guardProtection, out _);
        if (!FThemidaV3 && !IsHWBreakpoint(_virtualProtectAPI))
            SetBreakpoint((nuint)(nint)_virtualProtectAPI);
    }

    private bool IsGuardedAddress(nuint address)
    {
        if (_guardStart == 0) return false;
        return address >= _guardStart && address < _guardEnd;
    }

    private unsafe uint ProcessGuardedAccess(IntPtr hThread, EXCEPTION_RECORD excRec)
    {
        uint oldProt;
        VirtualProtectEx(FProcess.hProcess, (IntPtr)(nint)_guardStart, _guardEnd - _guardStart, PAGE_EXECUTE_READWRITE, out oldProt);

        nuint excAddr = (nuint)(nint)excRec.ExceptionAddress;

        if (excAddr > _guardEnd)
        {
            FGuardAddrs.Add(excRec.GetExceptionInformation(1));
            _guardStepping = true;
            var c2 = new CONTEXT { ContextFlags = CONTEXT_CONTROL };
            GetThreadContext(hThread, ref c2);
            c2.EFlags |= 0x100;
            SetThreadContext(hThread, ref c2);
        }
        else if (_tlsTotal > 0 && _tlsCounter < _tlsTotal)
        {
            // TLS handling
            bool handled = false;
            var c2 = new CONTEXT { ContextFlags = CONTEXT_CONTROL };
            if (GetThreadContext(hThread, ref c2))
            {
                uint retAddr = 0;
                RPM(c2.Esp, &retAddr, 4);
                if (TMSectR.Contains(retAddr))
                {
                    _tlsCounter++;
                    Log(LogMsgType.Good, $"TLS {_tlsCounter}: {excAddr:X8}");
                    c2.Eip = retAddr;
                    c2.Esp += 4 + 3 * 4;
                    SetThreadContext(hThread, ref c2);
                    InstallCodeSectionGuard(_guardProtection);
                    handled = true;
                }
            }
            if (!handled)
            {
                nuint oep = excAddr;
                Log(LogMsgType.Good, $"OEP: {oep:X8}");
                CheckVirtualizedOEP(oep);
                FinishUnpacking(oep);
            }
        }
        else
        {
            nuint oep = excAddr;
            Log(LogMsgType.Good, $"OEP: {oep:X8}");
            CheckVirtualizedOEP(oep);
            FinishUnpacking(oep);
        }

        return DBG_CONTINUE;
    }

    private void FinishUnpacking(nuint oep)
    {
        var dumper = new Dumper(FProcess, FImageBase, oep);
        var iat = DetermineIATAddress(oep, dumper);
        Log(LogMsgType.Good, $"IAT: {iat:X8}");

        if (FThemidaV3)
            TraceImports(iat, dumper);

        if (FIsVMOEP && FThemidaV3)
            new AntiDumpFixer(FProcess.hProcess, FImageBase).RedirectOEP(oep, iat);

        var fn = Path.Combine(Path.GetDirectoryName(FExecutable)!,
            Path.GetFileNameWithoutExtension(FExecutable) + "U" + Path.GetExtension(FExecutable));
        dumper.IAT = iat;
        dumper.DumpToFile(fn, dumper.Process(), FIsDLL);

        FHideThreadEnd = true;
        TerminateProcess(FProcess.hProcess, 0);

        if (FCreateDataSections)
        {
            var patcher = new Patcher(fn);
            patcher.ProcessMkData();
        }

        Log(LogMsgType.Good, "Operation completed successfully.");
    }
}
#endif
