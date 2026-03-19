using System.Runtime.InteropServices;
using static Magicmida.NativeApi;

namespace Magicmida;

#if CPUX64
public class TTMDebugger64 : TMCommon
{
    private IntPtr _closeHandleAPI, _virtualAllocAPI, _corExeMain;

    private nuint _guardStart, _guardEnd;
    private bool _guardStepping, _tmGuard, _traceMSVCOEP;

    private nuint _msvcInitCookie, _msvcOEP;

    private int _tlsCounter, _tlsTotal;

    public TTMDebugger64(string executable, string parameters, bool createData)
        : base(executable, parameters, Utils.Log!)
    {
        FCreateDataSections = createData;
        FThemidaV3 = true; // Themida V2 is not supported on x64 atm.
        FGuardAddrs = new List<nuint>();
    }

    private bool InImageBounds(nuint address) =>
        address >= FImageBase && address < FImageBoundary;

    private unsafe void SelectThemidaSection(nuint address)
    {
        for (int i = 0; i < FPESections.Length; i++)
        {
            nuint sectStart = FPESections[i].VirtualAddress + FImageBase;
            nuint sectEnd = sectStart + FPESections[i].VirtualSize;
            if (address >= sectStart && address < sectEnd)
            {
                TMSectR = new MemoryRegion(sectStart, FPESections[i].VirtualSize);
                TMSect = (byte*)Marshal.AllocHGlobal((int)TMSectR.Size);
                if (!RPM(TMSectR.Address, TMSect, TMSectR.Size))
                {
                    Marshal.FreeHGlobal((IntPtr)TMSect);
                    TMSect = null;
                }
                Log(LogMsgType.Info, $"TMSect: {TMSectR.Address:X} ({TMSectR.Size} bytes)");
                break;
            }
        }

        if (TMSect == null)
            throw new Exception($"Unable to find section for {address:X}");
    }

    protected override unsafe void OnDebugStart(ref IntPtr hPE, IntPtr hThread)
    {
        string mmPath = Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly()?.Location ?? "") ?? "";
        string injectorPath = Path.Combine(mmPath, "InjectorCLIx64.exe");
        if (File.Exists(injectorPath))
        {
            uint ts = Utils.GetPETimestamp(injectorPath);
            if (ts < 0x6484FEF9)
                throw new Exception("Your version of InjectorCLIx64 is unsuitable due to a bug. Please use the provided binary or build the latest git master.");
            Log(LogMsgType.Good, $"Applying ScyllaHide (built {DateTimeOffset.FromUnixTimeSeconds(ts).DateTime.ToShortDateString()})");
            ShellExecute(IntPtr.Zero, "open", injectorPath,
                $"pid:{FProcess.dwProcessId} {Path.Combine(mmPath, "HookLibraryx64.dll")} nowait",
                null, 0 /*SW_HIDE*/);
        }
        else
            throw new Exception("ScyllaHide is mandatory for Themida64 (InjectorCLIx64.exe not found)");

        _virtualAllocAPI = GetProcAddress(GetModuleHandle("kernel32.dll"), "VirtualAlloc");
        FSleepAPI = (nuint)(nint)GetProcAddress(GetModuleHandle("kernel32.dll"), "Sleep");
        FlstrlenAPI = (nuint)(nint)GetProcAddress(GetModuleHandle("kernel32.dll"), "lstrlen");

        TMInit(ref hPE);
    }

    protected override uint OnAccessViolation(IntPtr hThread, EXCEPTION_RECORD excRecord)
    {
        if (IsGuardedAddress(excRecord.ExceptionInformation1))
            return ProcessGuardedAccess(hThread, excRecord);
        return base.OnAccessViolation(hThread, excRecord);
    }

    protected override void OnDLLLoad(string fileName, IntPtr baseAddress)
    {
        if (fileName.IndexOf("\\mscoree.dll", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            Log(LogMsgType.Info, "This might be a .NET program - setting _CorExeMain BP");
            var hCorEE = LoadLibrary("mscoree.dll");
            if (hCorEE == baseAddress)
            {
                _corExeMain = GetProcAddress(hCorEE, "_CorExeMain");
                SetSoftBP(_corExeMain);
            }
            else
                Log(LogMsgType.Fatal, "DLL was loaded at different base than in target!");
        }

        base.OnDLLLoad(fileName, baseAddress);
    }

    protected override unsafe void OnHardwareBreakpoint(IntPtr hThread, nuint bpa, ref CONTEXT c)
    {
        nuint eip = c.IP;

        if (eip == (nuint)(nint)_closeHandleAPI)
        {
            nuint buf = 0;
            RPM(c.SP, &buf, (nuint)IntPtr.Size);
            Log(LogMsgType.Info, $"CloseHandle called from {buf:X}");

            if (InImageBounds(buf))
            {
                ResetBreakpoint((IntPtr)(nint)eip);
                SetBreakpoint(FImageBase + 0x1000, HWBPType.Write);
            }
        }
        else if (eip == (nuint)(nint)_virtualAllocAPI)
        {
            nuint buf = 0;
            RPM(c.SP, &buf, (nuint)IntPtr.Size);
            Log(LogMsgType.Info, $"AllocMem called from {buf:X}");

            if (InImageBounds(buf))
            {
                ResetBreakpoint(_virtualAllocAPI);
                InstallCodeSectionGuard();
            }
        }
        else if (bpa == FImageBase + 0x1000)
        {
            Log(LogMsgType.Good, $"Wrote to .text base from {eip:X}");

            if (TMSectR.Address == 0)
                SelectThemidaSection(eip);

            ResetBreakpoint((IntPtr)(nint)(FImageBase + 0x1000));
            SetBreakpoint((nuint)(nint)_virtualAllocAPI, HWBPType.Execute);
        }
        else
        {
            Log(LogMsgType.Info, $"Accessed {bpa:X} from {eip:X}");
        }
    }

    protected override unsafe uint OnSinglestep(nuint bpa)
    {
        if (_guardStepping)
        {
            uint oldProt;
            if (!VirtualProtectEx(FProcess.hProcess, (IntPtr)(nint)_guardStart, (nuint)(_guardEnd - _guardStart), PAGE_NOACCESS, out oldProt))
                throw new System.ComponentModel.Win32Exception();
            _guardStepping = false;
            return DBG_CONTINUE;
        }

        return base.OnSinglestep(bpa);
    }

    protected override SoftBPAction OnSoftwareBreakpoint(IntPtr hThread, IntPtr bpa)
    {
        if (bpa == _corExeMain)
        {
            var dotnetDumper = new DumperDotnet(FProcess, FImageBase);
            string fn = Path.Combine(Path.GetDirectoryName(FExecutable)!,
                Path.GetFileNameWithoutExtension(FExecutable) + "U" + Path.GetExtension(FExecutable));
            dotnetDumper.DumpToFile(fn);
            Log(LogMsgType.Good, ".NET process dumped.");

            FHideThreadEnd = true;
            TerminateProcess(FProcess.hProcess, 0);
            return SoftBPAction.ClearContinue;
        }

        throw new Exception($"Unexpected SoftBP at {bpa:X}");
    }

    private unsafe void DumpContext(uint threadId)
    {
        var hThread = GetThread(threadId);
        var c = new CONTEXT { ContextFlags = CONTEXT_CONTROL | CONTEXT_INTEGER };
        if (!GetThreadContext(hThread, ref c))
        {
            Log(LogMsgType.Fatal, "DumpContext: GetThreadContext failed");
            return;
        }

        Log(LogMsgType.Info, $"rax: {c.Rax:X} rbx: {c.Rbx:X} rcx: {c.Rcx:X} rdx: {c.Rdx:X} rsi: {c.Rsi:X} rdi: {c.Rdi:X}");
        Log(LogMsgType.Info, $"r8: {c.R8:X} r9: {c.R9:X} r10: {c.R10:X} r11: {c.R11:X} r12: {c.R12:X} r13: {c.R13:X} r14: {c.R14:X} r15: {c.R15:X}");
        Log(LogMsgType.Info, $"rip: {c.Rip:X} rbp: {c.Rbp:X} rsp: {c.Rsp:X} eflags: {c.EFlags:X}");
    }

    private unsafe void TMInit(ref IntPtr hPE)
    {
        if (hPE == IntPtr.Zero || hPE == new IntPtr(-1))
        {
            hPE = CreateFile(FExecutable, GENERIC_READ, FILE_SHARE_READ, IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
            if (hPE == new IntPtr(-1))
                throw new System.ComponentModel.Win32Exception();
        }

        SetFilePointer(hPE, 0, IntPtr.Zero, 0 /*FILE_BEGIN*/);

        var buf = new byte[0x1000];
        fixed (byte* pBufRead = buf)
        if (!ReadFile(hPE, (IntPtr)pBufRead, (uint)buf.Length, out uint bytesRead, IntPtr.Zero))
            throw new System.ComponentModel.Win32Exception();

        fixed (byte* pBuf = buf)
        {
            var dos = (IMAGE_DOS_HEADER*)pBuf;
            byte* ntPtr = pBuf + dos->e_lfanew;
            var nt = (IMAGE_NT_HEADERS64*)ntPtr;
            var sect = (IMAGE_SECTION_HEADER*)(ntPtr + sizeof(IMAGE_NT_HEADERS64));

            InitPEDetails(ntPtr);

            FBaseOfData = sect[0].VirtualAddress + nt->OptionalHeader.SizeOfCode;

            // PE Header Antidump
            if (sect[2].Name[1] == (byte)'i')
            {
                nuint testOff = (nuint)((byte*)&sect[2].Name[1] - pBuf) + FImageBase;
                uint oldProt;
                VirtualProtectEx(FProcess.hProcess, (IntPtr)(nint)testOff, 1, PAGE_READWRITE, out oldProt);
                byte newVal = (byte)'p';
                WriteProcessMemory(FProcess.hProcess, (IntPtr)(nint)testOff, (IntPtr)(&newVal), 1, out _);
            }

            // Check if text section is already decrypted
            byte* namePtr = sect[0].Name;
            string sectName = Marshal.PtrToStringAnsi((IntPtr)namePtr, 8)?.TrimEnd('\0') ?? "";
            if (sectName == ".text")
            {
                Log(LogMsgType.Good, "Text section not encrypted/compressed, installing page guard");
                InstallCodeSectionGuard();
            }
            else
            {
                _closeHandleAPI = GetProcAddress(GetModuleHandle("kernel32.dll"), "CloseHandle");
                SetBreakpoint((nuint)(nint)_closeHandleAPI, HWBPType.Execute);
            }

            // TLS handling
            ref var tlsDir = ref NativeApi.GetDataDirectory(ref nt->OptionalHeader, IMAGE_DIRECTORY_ENTRY_TLS);
            if (tlsDir.Size > 0)
            {
                var tlsData = new byte[Math.Min(tlsDir.Size, (uint)sizeof(IMAGE_TLS_DIRECTORY64))];
                fixed (byte* pTls = tlsData)
                {
                    if (RPM(FImageBase + tlsDir.VirtualAddress, pTls, (nuint)tlsData.Length))
                    {
                        var tls = (IMAGE_TLS_DIRECTORY64*)pTls;
                        long tlsDist = (long)(FImageBase + tlsDir.VirtualAddress) - (long)tls->AddressOfCallBacks;
                        if (tlsDist > 0 && tlsDist <= sizeof(ulong) * (4 + 1))
                        {
                            _tlsTotal = (int)(tlsDist / sizeof(ulong)) - 1;
                            Log(LogMsgType.Info, $"[MSVC] Expecting {_tlsTotal} TLS entries");
                        }
                    }
                }
            }
        }
    }

    private unsafe uint FindDynamicTM(string pattern, nuint offset = 0)
    {
        if (offset != 0)
            offset -= TMSectR.Address;

        uint result = Utils.FindDynamic(pattern, TMSect + offset, TMSectR.Size - (uint)offset);
        if (result > 0)
            result += (uint)(TMSectR.Address + offset);
        return result;
    }

    private unsafe uint FindStaticTM(string pattern, nuint offset = 0)
    {
        if (offset != 0)
            offset -= TMSectR.Address;

        uint result = Utils.FindStatic(pattern, TMSect + offset, TMSectR.Size - (uint)offset);
        if (result > 0)
            result += (uint)(TMSectR.Address + offset);
        return result;
    }

    private void InstallCodeSectionGuard()
    {
        _guardStart = FImageBase + FPESections[0].VirtualAddress;
        _guardEnd = FImageBase + FBaseOfData;
        VirtualProtectEx(FProcess.hProcess, (IntPtr)(nint)_guardStart, (nuint)(_guardEnd - _guardStart), PAGE_NOACCESS, out _);
    }

    private bool IsGuardedAddress(nuint address)
    {
        if (_guardStart == 0) return false;
        return address >= _guardStart && address < _guardEnd;
    }

    private unsafe uint ProcessGuardedAccess(IntPtr hThread, in EXCEPTION_RECORD excRecord)
    {
        nuint accessType = excRecord.ExceptionInformation0;
        nuint accessAddr = excRecord.ExceptionInformation1;
        nuint excAddr = (nuint)(nint)excRecord.ExceptionAddress;

        Log(LogMsgType.Info, $"[Guard] {Utils.AccessViolationFlagToStr(accessType)} {accessAddr:X}");

        uint oldProt;
        VirtualProtectEx(FProcess.hProcess, (IntPtr)(nint)_guardStart, (nuint)(_guardEnd - _guardStart), PAGE_EXECUTE_READWRITE, out oldProt);

        if (_tmGuard)
        {
            _tmGuard = false;
            InstallCodeSectionGuard();
        }
        else if (!InImageBounds(excAddr))
        {
            // Random library code reading our text base...
            _guardStepping = true;
        }
        else if (excAddr > _guardEnd)
        {
            // Themida access
            if (TMSectR.Address == 0)
                SelectThemidaSection(excAddr);

            FGuardAddrs.Add(accessAddr);
            _guardStepping = true;
        }
        else if (accessType == 8 && _tlsTotal > 0 && _tlsCounter < _tlsTotal)
        {
            _tlsCounter++;
            Log(LogMsgType.Good, $"TLS {_tlsCounter}: {excAddr:X8}");
            _guardStart = TMSectR.Address;
            _guardEnd = FImageBoundary;
            _tmGuard = true;
            VirtualProtectEx(FProcess.hProcess, (IntPtr)(nint)_guardStart, (nuint)(_guardEnd - _guardStart), PAGE_READWRITE, out _);
        }
        else if (_traceMSVCOEP)
        {
            WriteMSVCOEP(excAddr);
            FinishUnpacking(_msvcOEP);
        }
        else
        {
            nuint oep = excAddr;

            CheckVirtualizedOEP(oep);

            var c = new CONTEXT { ContextFlags = CONTEXT_CONTROL };
            if (GetThreadContext(hThread, ref c))
            {
                nuint retAddr = 0;
                RPM(c.SP, &retAddr, (nuint)IntPtr.Size);
                if (TMSectR.Contains(retAddr))
                {
                    Log(LogMsgType.Info, $"Return address points into Themida section: {retAddr:X9}");
                    oep = TryFindCorrectOEP(oep);

                    if (_traceMSVCOEP)
                    {
                        _msvcOEP = oep;

                        // Skip and wait for next .text hit.
                        c.IP = retAddr;
                        c.SP += (nuint)IntPtr.Size;
                        if (!SetThreadContext(hThread, ref c))
                            throw new System.ComponentModel.Win32Exception();

                        InstallCodeSectionGuard();
                        return DBG_CONTINUE;
                    }
                }
                else
                    Log(LogMsgType.Good, $"OEP: {(ulong)oep:X8}");
            }
            else
                Log(LogMsgType.Fatal, "GetThreadContext failed for further OEP check");

            FinishUnpacking(oep);
        }

        if (_guardStepping)
        {
            var c = new CONTEXT { ContextFlags = CONTEXT_CONTROL };
            if (!GetThreadContext(hThread, ref c))
                throw new System.ComponentModel.Win32Exception();
            c.EFlags |= 0x100; // Trap flag
            SetThreadContext(hThread, ref c);
        }

        return DBG_CONTINUE;
    }

    private unsafe nuint TryFindCorrectOEP(nuint hitAddress)
    {
        if (FMajorLinkerVersion != 9 && FMajorLinkerVersion != 10 && FMajorLinkerVersion != 11 &&
            FMajorLinkerVersion != 12 && FMajorLinkerVersion != 14)
        {
            Log(LogMsgType.Fatal, "Don't know what to do about OEP for this compiler. Your target likely won't run.");
            return hitAddress;
        }

        // MSVC: Assume HitAddress is at __security_init_cookie.
        int textLen = (int)(FBaseOfData - FPESections[0].VirtualAddress);
        var textBuf = new byte[textLen];
        fixed (byte* pText = textBuf)
        {
            RPM(FImageBase + FPESections[0].VirtualAddress, pText, (nuint)textLen);

            uint scanFor = (uint)(hitAddress - FImageBase - FPESections[0].VirtualAddress);
            for (uint i = 0; i < textLen - 10; i++)
            {
                if (pText[i] == 0xE8 && pText[i + 5] == 0xE9 &&
                    BitConverter.ToUInt32(textBuf, (int)(i + 1)) + i + 5 == scanFor)
                {
                    nuint oep = FImageBase + FPESections[0].VirtualAddress + i;
                    Log(LogMsgType.Good, $"Found suitable real OEP {oep:X9}");
                    return oep;
                }
            }

            // Got two suspicious reads as last accesses
            if (FGuardAddrs.Count >= 2 && FGuardAddrs[FGuardAddrs.Count - 1] == FGuardAddrs[FGuardAddrs.Count - 2] + 1)
            {
                _msvcInitCookie = hitAddress;
                _traceMSVCOEP = true;
                return FGuardAddrs[FGuardAddrs.Count - 2];
            }

            Log(LogMsgType.Fatal, "Real OEP not found. Your target likely won't run.");
        }

        return hitAddress;
    }

    private unsafe void WriteMSVCOEP(nuint crtStartup)
    {
        nuint x;
        VirtualProtectEx(FProcess.hProcess, (IntPtr)(nint)_msvcOEP, 18, PAGE_EXECUTE_READWRITE, out _);

        // Build: sub rsp, 28h / call initcookie / add rsp, 28h / jmp crtstartup
        var code = new byte[18];
        // sub rsp, 28h = 48 83 EC 28
        code[0] = 0x48; code[1] = 0x83; code[2] = 0xEC; code[3] = 0x28;
        // call rel32
        code[4] = 0xE8;
        int callRel = (int)(_msvcInitCookie - (_msvcOEP + 4) - 5);
        Array.Copy(BitConverter.GetBytes(callRel), 0, code, 5, 4);
        // add rsp, 28h = 48 83 C4 28
        code[9] = 0x48; code[10] = 0x83; code[11] = 0xC4; code[12] = 0x28;
        // jmp rel32
        code[13] = 0xE9;
        int jmpRel = (int)(crtStartup - (_msvcOEP + 13) - 5);
        Array.Copy(BitConverter.GetBytes(jmpRel), 0, code, 14, 4);

        fixed (byte* pCode = code)
            WriteProcessMemory(FProcess.hProcess, (IntPtr)(nint)_msvcOEP, (IntPtr)pCode, (nuint)code.Length, out _);

        Log(LogMsgType.Good, $"Virtualized MSVC9+ OEP restored: {_msvcOEP:X}");
    }

    private void FinishUnpacking(nuint oep)
    {
        var dumper = new Dumper(FProcess, FImageBase, oep);

        nuint iat = DetermineIATAddress(oep, dumper);
        Log(LogMsgType.Good, $"IAT: {iat:X8}");

        TraceImports(iat, dumper);

        string fn = Path.Combine(Path.GetDirectoryName(FExecutable)!,
            Path.GetFileNameWithoutExtension(FExecutable) + "U" + Path.GetExtension(FExecutable));
        dumper.IAT = iat;
        dumper.DumpToFile(fn, dumper.Process(), FIsDLL);

        FHideThreadEnd = true;
        TerminateProcess(FProcess.hProcess, 0);

        Log(LogMsgType.Good, "Operation completed successfully.");
    }

    protected override unsafe bool TraceIsAtAPI(Tracer tracer, ref CONTEXT c)
    {
        if (tracer.Counter > 100 && tracer.Counter < 5000)
        {
            uint insnData = 0;
            RPM(c.IP, &insnData, 4);
            if (insnData == 0x0CB10FF0) // lock cmpxchg [rbx+rbp], ecx
            {
                FTraceInVM = true;
                Log(LogMsgType.Info, "Trace ran into Themida VM, stopping");
                return true;
            }
        }

        // cat & mouse game with fake calls
        if (c.SP < FTraceStartSP && (c.IP == FSleepAPI || c.IP == FlstrlenAPI))
        {
            Log(LogMsgType.Info, $"Skipping anti-trace API at {c.IP:X}");
            nuint retAddr = 0;
            RPM(c.SP, &retAddr, (nuint)IntPtr.Size);
            c.SP += (nuint)IntPtr.Size;
            c.IP = retAddr;
        }

        bool result = !TMSectR.Contains(c.IP);
        if (result && c.SP < FTraceStartSP)
        {
            Log(LogMsgType.Info, $"Warning: Might have encountered new fake API at {c.IP:X8}");
            result = false;
        }

        if (result)
            FTracedAPI = c.IP;

        return result;
    }
}
#endif
