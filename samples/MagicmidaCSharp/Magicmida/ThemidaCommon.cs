using System.Runtime.InteropServices;
using static Magicmida.NativeApi;

namespace Magicmida;

public abstract class TMCommon : DebuggerCore
{
    protected bool FCreateDataSections;
    protected nuint FBaseOfData;
    protected nuint FImageBoundary;
    protected IMAGE_SECTION_HEADER[] FPESections = Array.Empty<IMAGE_SECTION_HEADER>();
    protected byte FMajorLinkerVersion;

    protected unsafe byte* TMSect;
    protected MemoryRegion TMSectR;

    protected bool FThemidaV3, FIsVMOEP;
    protected List<nuint> FGuardAddrs = new();

    // Used by TraceIsAtAPI
    protected nuint FTracedAPI;
    protected nuint FSleepAPI, FlstrlenAPI;
    protected nuint FTraceStartSP;
    protected bool FTraceInVM;

    protected TMCommon(string executable, string parameters, LogProc log)
        : base(executable, parameters, log) { }

    protected unsafe void InitPEDetails(byte* ntPtr)
    {
#if CPUX86
        var nt = (IMAGE_NT_HEADERS32*)ntPtr;
        var sect = (IMAGE_SECTION_HEADER*)(ntPtr + sizeof(IMAGE_NT_HEADERS32));
#else
        var nt = (IMAGE_NT_HEADERS64*)ntPtr;
        var sect = (IMAGE_SECTION_HEADER*)(ntPtr + sizeof(IMAGE_NT_HEADERS64));
#endif
        FPESections = new IMAGE_SECTION_HEADER[nt->FileHeader.NumberOfSections];
        for (int i = 0; i < FPESections.Length; i++)
            FPESections[i] = sect[i];

        if (nt->OptionalHeader.AddressOfEntryPoint < FPESections[0].VirtualAddress + FPESections[0].VirtualSize)
            throw new Exception("The selected binary does not seem to be packed (entrypoint is in .text section).");

        FImageBoundary = (nuint)nt->OptionalHeader.SizeOfImage + FImageBase;
        Log(LogMsgType.Info, $"Image boundary: {FImageBoundary:X}");

        FMajorLinkerVersion = nt->OptionalHeader.MajorLinkerVersion;
        Log(LogMsgType.Info, $"Image linker: {FMajorLinkerVersion}.{nt->OptionalHeader.MinorLinkerVersion}");
    }

    protected unsafe void CheckVirtualizedOEP(nuint oep)
    {
        byte instr = 0;
        uint displ = 0;
        RPM(oep, &instr, 1);
        RPM(oep + 1, &displ, 4);
        if (instr != 0xE9 || oep + 5 + displ < TMSectR.Address)
            return;

        FIsVMOEP = true;
        Log(LogMsgType.Info, $"OEP is virtualized (!): jmp {oep + 5 + displ:X8}");
    }

    protected unsafe nuint DetermineIATAddress(nuint oep, Dumper dumper)
    {
        nuint textBase = FImageBase + FPESections[0].VirtualAddress;
        nuint codeSize = FBaseOfData - FPESections[0].VirtualAddress;

        int dataSectionIndex = 0;
        for (int i = 0; i < FPESections.Length; i++)
            if (FBaseOfData < FPESections[i].VirtualAddress + FPESections[i].VirtualSize)
            {
                dataSectionIndex = i;
                break;
            }

        nuint dataSize = FPESections[dataSectionIndex].VirtualSize - (FBaseOfData - FPESections[dataSectionIndex].VirtualAddress);
        Log(LogMsgType.Info, $"Text base: 0x{textBase:X8}, code size: 0x{codeSize:X}, data size: 0x{dataSize:X}");

        uint numInstr = 0;
        nuint iatRef = 0;
        var codeDump = new byte[codeSize];
        fixed (byte* pCode = codeDump)
        {
            if (!RPM(textBase, pCode, codeSize))
                throw new Exception("DetermineIATAddress: RPM failed");

            if (!FIsVMOEP)
                iatRef = FindCallOrJmpPtr(pCode, codeDump, textBase, codeSize, oep, ref numInstr, false);
            else
            {
                // Check for Delphi
#if CPUX86
                int checkOff = 6;
#else
                int checkOff = 10;
#endif
                uint marker = BitConverter.ToUInt32(codeDump, checkOff);
                uint marker2 = BitConverter.ToUInt32(codeDump, 6);
                if (marker == 0x6C6F6F42 || marker2 == 0x65747942) // "Bool" / "Byte"
                {
                    uint dOff = FindDelphiCall(pCode, (uint)codeSize);
                    if (dOff > 0)
                        iatRef = FindCallOrJmpPtr(pCode, codeDump, textBase, codeSize, textBase + dOff, ref numInstr, true);
                }
                else
                    iatRef = FindCallOrJmpPtr(pCode, codeDump, textBase, codeSize, textBase, ref numInstr, true);
            }
        }

        if (iatRef == 0)
        {
            Log(LogMsgType.Info, "No IAT reference found via reference search");
            if (FGuardAddrs.Count > 0)
            {
                var site = new byte[6];
                fixed (byte* pSite = site) RPM(FGuardAddrs[0], pSite, 6);

                nuint target;
                if (site[0] == 0xE8 || site[0] == 0xE9)
                    target = (nuint)(BitConverter.ToUInt32(site, 1) + FGuardAddrs[0] + 5);
                else if (site[1] == 0xE8 || site[1] == 0xE9)
                    target = (nuint)(BitConverter.ToUInt32(site, 2) + FGuardAddrs[0] + 6);
                else
                    throw new Exception("First guard addr is not call/jmp");

                Log(LogMsgType.Info, $"First guard addr {FGuardAddrs[0]:X8} yielded API {target:X8}");
                iatRef = ScanForPointer(target, textBase, codeSize, FBaseOfData, dataSize);
            }
            else
                throw new Exception("Found no way to obtain IAT reference");
        }
        Log(LogMsgType.Good, $"First IAT ref: {iatRef:X8}");

        // Find start of IAT
        nuint result = 0;
        nuint seeker = iatRef;
        int iatDataLen = Dumper.MAX_IAT_SIZE / IntPtr.Size;
        var iatData = new nuint[iatDataLen];
        fixed (nuint* pIAT = iatData)
            RPM(iatRef - (nuint)((iatDataLen - 1) * IntPtr.Size), pIAT, (nuint)(iatDataLen * IntPtr.Size));

        int consecutive0 = 0;
        int idx = iatDataLen - 1;
        while (idx >= 0)
        {
            if (iatData[idx] == 0)
            {
                consecutive0++;
                if (consecutive0 > 64) break;
            }
            else if (dumper.IsAPIAddress(iatData[idx]) || (FThemidaV3 && TMSectR.Contains(iatData[idx])))
            {
                result = seeker;
                consecutive0 = 0;
            }
            else
            {
                Log(LogMsgType.Info, $"Ending IAT start search at {seeker:X} because pointer is {iatData[idx]:X}");
                break;
            }
            idx--;
            seeker -= (nuint)IntPtr.Size;
        }
        if (idx == -1) throw new Exception("IAT too big");
        if (result == 0) throw new Exception("IAT assertion failed");

        return result;
    }

    private unsafe nuint FindCallOrJmpPtr(byte* code, byte[] codeDump, nuint textBase, nuint codeSize,
        nuint address, ref uint numInstr, bool ignoreMethodBoundary)
    {
        int offset = (int)(address - textBase);
        while (offset >= 0 && offset < (int)codeSize - 15 && (numInstr < 200 || (ignoreMethodBoundary && address < textBase + codeSize)))
        {
            fixed (byte* p = &codeDump[offset])
            {
                var dis = Disassembler.Disassemble(p, (uint)((int)codeSize - offset), address);

                if (dis.IsCallDwordPtr || dis.IsJmpDwordPtr)
                {
                    Log(LogMsgType.Info, $"Found {address:X8} : {dis.FullInstruction}");
                    nuint iatPointer = (nuint)dis.MemoryDisplacement;
                    nuint thePointer = 0;
                    if (!RPM(iatPointer, &thePointer, (nuint)IntPtr.Size) || thePointer > textBase + codeSize)
                        return iatPointer;
                }

                if (dis.IsCall && !ignoreMethodBoundary)
                {
                    if (dis.BranchTarget > textBase + codeSize)
                        return 0;
                    var r = FindCallOrJmpPtr(code, codeDump, textBase, codeSize, (nuint)dis.BranchTarget, ref numInstr, false);
                    if (r != 0) return r;
                }

                if (dis.IsRet && !ignoreMethodBoundary)
                    return 0;

                numInstr++;
                int len = dis.Length > 0 ? dis.Length : 1;
                offset += len;
                address += (nuint)len;
            }
        }
        return 0;
    }

    private unsafe nuint ScanForPointer(nuint toFind, nuint textBase, nuint codeSize, nuint dataOffset, nuint dataSize, bool scanCode = false)
    {
        nuint startOffset = scanCode ? textBase : textBase + codeSize;
        nuint scanSize = scanCode ? codeSize : dataSize;

        var dataSect = new byte[scanSize];
        fixed (byte* p = dataSect)
        {
            if (!RPM(startOffset, p, scanSize))
                throw new Exception("DetermineIATAddress.ScanData: RPM failed");

            for (nuint i = 0; i + (nuint)IntPtr.Size <= scanSize; i += (nuint)IntPtr.Size)
            {
                nuint val = IntPtr.Size == 4 ? *(uint*)(p + i) : (nuint)(*(ulong*)(p + i));
                if (val == toFind)
                    return i + startOffset;
            }
        }

        if (scanCode)
            throw new Exception("Unable to find API in section");
        return ScanForPointer(toFind, textBase, codeSize, 0, 0, true);
    }

    private static unsafe uint FindDelphiCall(byte* codeDump, uint codeSize)
    {
        uint i = 0, counter = 0;
        while (i < codeSize - 6)
        {
            if (*(ushort*)(codeDump + i) == 0x25FF)
            {
                counter++;
                if (counter == 3) return i;
            }
            i++;
        }
        return 0;
    }

    protected unsafe void TraceImports(nuint iat, Dumper dumper)
    {
        int ptrSize = IntPtr.Size;
        int count = Dumper.MAX_IAT_SIZE / ptrSize;
        var iatData = new nuint[count];
        fixed (nuint* pIAT = iatData)
            RPM(iat, pIAT, (nuint)(count * ptrSize));

        bool didSetExitProcess = false;
        uint trashCounter = 0;

        for (int i = 0; i < count; i++)
        {
            if (TMSectR.Contains(iatData[i]))
            {
                Log(LogMsgType.Info, $"Trace: {iatData[i]:X8} [{iat + (nuint)(i * ptrSize):X8}]");
                trashCounter = 0;

                var ctx = new CONTEXT { ContextFlags = CONTEXT_CONTROL };
                GetThreadContext(GetThread(FCurrentThreadID), ref ctx);
                FTraceStartSP = ctx.SP;

                FTracedAPI = 0;
                FTraceInVM = false;
                var tracer = new Tracer(FProcess.dwProcessId, FCurrentThreadID, GetThread(FCurrentThreadID), TraceIsAtAPI, Log);
                tracer.Trace(iatData[i], 500000);

                if (FTraceInVM)
                {
                    if (!didSetExitProcess)
                    {
                        didSetExitProcess = true;
                        iatData[i] = (nuint)(nint)GetProcAddress(GetModuleHandle("kernel32.dll"), "ExitProcess");
                        Log(LogMsgType.Info, "Setting API to ExitProcess");
                    }
                    else
                        Log(LogMsgType.Fatal, $"Unable to determine IAT address for {iat + (nuint)(i * ptrSize):X8}");
                }
                else if (FTracedAPI != 0)
                {
                    Log(LogMsgType.Info, $"-> {FTracedAPI:X8}");
                    if (FTracedAPI < 0x10000 || (FTracedAPI >= FImageBase && FTracedAPI < FImageBoundary))
                    {
                        Log(LogMsgType.Info, "Discarding result & aborting IAT tracing");
                        break;
                    }
                    iatData[i] = FTracedAPI;
                }
                else
                    Log(LogMsgType.Fatal, "Tracing failed!");
            }
            else if (iatData[i] == 0 || !dumper.IsAPIAddress(iatData[i]))
            {
                trashCounter++;
                if (trashCounter > 64) break;
            }
            else
                trashCounter = 0;
        }

        uint oldProtect;
        VirtualProtectEx(FProcess.hProcess, (IntPtr)(nint)iat, (nuint)(count * ptrSize), PAGE_READWRITE, out oldProtect);
        fixed (nuint* pIAT = iatData)
            if (!WriteProcessMemory(FProcess.hProcess, (IntPtr)(nint)iat, (IntPtr)pIAT, (nuint)(count * ptrSize), out _))
                throw new System.ComponentModel.Win32Exception();
    }

    protected abstract bool TraceIsAtAPI(Tracer tracer, ref CONTEXT c);

}
