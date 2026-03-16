using System.Runtime.InteropServices;
using static Magicmida.NativeApi;

namespace Magicmida;

public enum HWBPType : byte { Execute, Write, Reserved, Access }

public struct Breakpoint
{
    public nuint Address;
    public HWBPType BType;
    public bool Disabled;

    public void Change(nuint address, HWBPType type) { Address = address; BType = type; }
    public bool IsSet => !Disabled && Address > 0;
}

public enum SoftBPAction { KeepContinue, ClearContinue, KeepContinueNoStep }

public abstract class DebuggerCore
{
    private uint _attachPID;
    private string _dllExecutable = "";
    private Breakpoint _hw1, _hw2, _hw3, _hw4;
    private readonly Dictionary<uint, IntPtr> _threads = new(32);
    private readonly Dictionary<IntPtr, byte> _softBPs = new();
    private nuint _softBPReenable;

    protected LogProc Log;
    protected string FExecutable = "", FParameters = "";
    protected bool FIsDLL;
    protected PROCESS_INFORMATION FProcess;
    protected uint FCurrentThreadID;
    protected nuint FImageBase;
    protected bool FHideThreadEnd;

    private Thread? _thread;
    private readonly ManualResetEvent _done = new(false);

    protected DebuggerCore(string executable, string parameters, LogProc log)
    {
        FExecutable = executable;
        FParameters = parameters;
        Log = log;

        _thread = new Thread(Execute) { IsBackground = true };
        _thread.Start();
    }

    protected DebuggerCore(uint pid, LogProc log)
    {
        _attachPID = pid;
        Log = log;

        _thread = new Thread(Execute) { IsBackground = true };
        _thread.Start();
    }

    public bool FreeOnTerminate { get; set; }

    public void WaitFor() => _done.WaitOne();

    public IntPtr GetThread(uint threadId)
    {
        if (!_threads.TryGetValue(threadId, out var h))
            throw new Exception($"Thread {threadId} not found");
        return h;
    }

    public void Detach()
    {
        foreach (var h in _threads.Values)
            SuspendThread(h);

        if (DebugActiveProcessStop(FProcess.dwProcessId))
            Log(LogMsgType.Info, "Detached.");
        else
            Log(LogMsgType.Fatal, "Detaching failed.");
    }

    // ==================== Debug loop ====================

    private void Execute()
    {
        try
        {
            if (!PEExecute())
                throw new System.ComponentModel.Win32Exception();
        }
        catch (Exception ex)
        {
            Log(LogMsgType.Fatal, "Creating the process failed: " + ex.Message);
            _done.Set();
            return;
        }

        try
        {
            uint status = DBG_CONTINUE;
            while (true)
            {
                if (!WaitForDebugEvent(out var ev, INFINITE))
                {
                    Log(LogMsgType.Fatal, "OS Error: " + new System.ComponentModel.Win32Exception().Message);
                    break;
                }

                FCurrentThreadID = ev.dwThreadId;

                switch (ev.dwDebugEventCode)
                {
                    case EXCEPTION_DEBUG_EVENT:
                        status = DBG_EXCEPTION_NOT_HANDLED;
                        switch (ev.Exception.ExceptionRecord.ExceptionCode)
                        {
                            case EXCEPTION_ACCESS_VIOLATION:
                                status = OnAccessViolation(_threads[ev.dwThreadId], ev.Exception.ExceptionRecord);
                                break;
                            case EXCEPTION_BREAKPOINT:
                                if (_softBPs.ContainsKey(ev.Exception.ExceptionRecord.ExceptionAddress))
                                    status = HandleSoftwareBreakpoint(ref ev);
                                else
                                    OnUnsolicitedSoftwareBreakpoint(_threads[ev.dwThreadId], ev.Exception.ExceptionRecord.ExceptionAddress);
                                break;
                            case EXCEPTION_SINGLE_STEP:
                                status = HandleHardwareBreakpoint(ref ev);
                                break;
                            case EXCEPTION_DATATYPE_MISALIGNMENT:
                                break;
                            default:
                                if (ev.Exception.dwFirstChance == 0)
                                {
                                    Log(LogMsgType.Fatal, "dwFirstChance = 0");
                                    goto exitLoop;
                                }
                                Log(LogMsgType.Info, $"Code 0x{ev.Exception.ExceptionRecord.ExceptionCode:X8} at 0x{ev.Exception.ExceptionRecord.ExceptionAddress:X}");
                                status = DBG_EXCEPTION_NOT_HANDLED;
                                break;
                        }
                        break;

                    case CREATE_THREAD_DEBUG_EVENT:
                        status = OnCreateThreadDebugEvent(ref ev);
                        break;

                    case CREATE_PROCESS_DEBUG_EVENT:
                        status = OnCreateProcessDebugEvent(ref ev);
                        break;

                    case EXIT_THREAD_DEBUG_EVENT:
                        status = OnExitThreadDebugEvent(ref ev);
                        break;

                    case EXIT_PROCESS_DEBUG_EVENT:
                        status = OnExitProcessDebugEvent(ref ev);
                        ContinueDebugEvent(ev.dwProcessId, ev.dwThreadId, status);
                        goto exitLoop;

                    case LOAD_DLL_DEBUG_EVENT:
                        status = OnLoadDllDebugEvent(ref ev);
                        break;

                    case UNLOAD_DLL_DEBUG_EVENT:
                        status = DBG_CONTINUE;
                        break;

                    case OUTPUT_DEBUG_STRING_EVENT:
                        status = OnOutputDebugStringEvent(ref ev);
                        break;

                    case RIP_EVENT:
                        Log(LogMsgType.Fatal, "SYSTEM ERROR");
                        status = DBG_CONTINUE;
                        break;
                }

                ContinueDebugEvent(ev.dwProcessId, ev.dwThreadId, status);
            }
        }
        catch (Exception ex)
        {
            Log(LogMsgType.Fatal, "Critical error in debug loop: " + ex.Message);
        }

    exitLoop:
        if (FIsDLL)
        {
            Thread.Sleep(1000);
            DeleteFile(_dllExecutable);
        }
        _done.Set();
    }

    // ==================== Debug events ====================

    private uint OnCreateThreadDebugEvent(ref DEBUG_EVENT ev)
    {
        Log(LogMsgType.Info, $"[{ev.dwThreadId:D4}] Thread started ({ev.CreateThread.lpStartAddress:X}).");
        _threads[ev.dwThreadId] = ev.CreateThread.hThread;
        UpdateDR(ev.CreateThread.hThread);
        return DBG_CONTINUE;
    }

    private unsafe uint OnCreateProcessDebugEvent(ref DEBUG_EVENT ev)
    {
        int offsetImageBase = IntPtr.Size == 4 ? 8 : 16;
        int offsetShimData = IntPtr.Size == 4 ? 0x1E8 : 0x2D8;

        Log(LogMsgType.Info, $"Running on Windows build {Utils.GetWindowsBuildNumber()}");
        Log(LogMsgType.Info, $"Debug session launched (PID: {ev.dwProcessId}, TID: {ev.dwThreadId})");

        FProcess.hProcess = ev.CreateProcessInfo.hProcess;

        var pbi = new PROCESS_BASIC_INFORMATION();
        NtQueryInformationProcess(FProcess.hProcess, 0, (IntPtr)(&pbi), (uint)Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), IntPtr.Zero);
        Log(LogMsgType.Info, $"PEB: {pbi.PebBaseAddress:X}");

        // Patch PEB.BeingDebugged
        byte beingDebugged = 0;
        ReadProcessMemory(FProcess.hProcess, pbi.PebBaseAddress + 2, (IntPtr)(&beingDebugged), 1, out _);
        if (beingDebugged == 1)
        {
            Log(LogMsgType.Good, "Patching PEB.BeingDebugged");
            byte zero = 0;
            WriteProcessMemory(FProcess.hProcess, pbi.PebBaseAddress + 2, (IntPtr)(&zero), 1, out _);
        }

        // Read image base
        nuint imgBase = 0;
        if (ReadProcessMemory(FProcess.hProcess, pbi.PebBaseAddress + offsetImageBase, (IntPtr)(&imgBase), (nuint)IntPtr.Size, out _))
        {
            FImageBase = imgBase;
            Log(LogMsgType.Info, $"Process Image Base: {FImageBase:X}");
        }
        else
            throw new Exception("Reading process image base failed");

        // Clear shimdata
        nuint shimData = 0;
        if (ReadProcessMemory(FProcess.hProcess, pbi.PebBaseAddress + offsetShimData, (IntPtr)(&shimData), (nuint)IntPtr.Size, out _) && shimData != 0)
        {
            nuint z = 0;
            if (WriteProcessMemory(FProcess.hProcess, pbi.PebBaseAddress + offsetShimData, (IntPtr)(&z), (nuint)IntPtr.Size, out _))
                Log(LogMsgType.Info, "Cleared PEB.pShimData to prevent apphelp hooks");
        }

        _threads[ev.dwThreadId] = ev.CreateProcessInfo.hThread;

        OnDebugStart(ref ev.CreateProcessInfo.hFile, ev.CreateProcessInfo.hThread);

        CloseHandle(ev.CreateProcessInfo.hFile);

        return DBG_CONTINUE;
    }

    private uint OnExitThreadDebugEvent(ref DEBUG_EVENT ev)
    {
        if (!FHideThreadEnd)
            Log(LogMsgType.Info, $"[{ev.dwThreadId:D4}] Thread ended (code {ev.ExitThread.dwExitCode}).");
        _threads.Remove(ev.dwThreadId);
        return DBG_CONTINUE;
    }

    private uint OnExitProcessDebugEvent(ref DEBUG_EVENT ev)
    {
        Log(LogMsgType.Info, $"Process ended (code {ev.ExitProcess.dwExitCode}).");
        return DBG_CONTINUE;
    }

    private unsafe uint OnLoadDllDebugEvent(ref DEBUG_EVENT ev)
    {
        string dll = "?";
        IntPtr lpImageName = IntPtr.Zero;
        var buf = stackalloc char[261];
        if (ReadProcessMemory(FProcess.hProcess, ev.LoadDll.lpImageName, (IntPtr)(&lpImageName), (nuint)IntPtr.Size, out _) &&
            lpImageName != IntPtr.Zero &&
            ReadProcessMemory(FProcess.hProcess, lpImageName, (IntPtr)buf, 260 * 2, out _))
        {
            dll = new string(buf);
        }
        Log(LogMsgType.Info, $"[{(nuint)(nint)ev.LoadDll.lpBaseOfDll:X8}] Loaded {dll}");
        OnDLLLoad(dll, ev.LoadDll.lpBaseOfDll);
        CloseHandle(ev.LoadDll.hFile);
        return DBG_CONTINUE;
    }

    private unsafe uint OnOutputDebugStringEvent(ref DEBUG_EVENT ev)
    {
        if (ev.DebugString.nDebugStringLength > 0 && ev.DebugString.nDebugStringLength < 256)
        {
            var buf = stackalloc byte[256];
            if (RPM((nuint)(nint)ev.DebugString.lpDebugStringData, buf, ev.DebugString.nDebugStringLength))
            {
                buf[ev.DebugString.nDebugStringLength] = 0;
                Log(LogMsgType.Info, "[Debug Str] " + Marshal.PtrToStringAnsi((IntPtr)buf));
            }
        }
        return DBG_CONTINUE;
    }

    // ==================== Breakpoints ====================

    private unsafe uint HandleHardwareBreakpoint(ref DEBUG_EVENT ev)
    {
        var eip = ev.Exception.ExceptionRecord.ExceptionAddress;
        var hThread = _threads[ev.dwThreadId];

        var c = new CONTEXT { ContextFlags = CONTEXT_CONTROL | CONTEXT_INTEGER | CONTEXT_DEBUG_REGISTERS };
        if (!GetThreadContext(hThread, ref c))
            Log(LogMsgType.Fatal, "GetThreadContext failed");

        var dr6 = (uint)c.Dr6;

        if (((dr6 >> 14) & 1) == 0 && (_hw1.IsSet || _hw2.IsSet || _hw3.IsSet || _hw4.IsSet))
        {
            Breakpoint bp;
            switch (dr6 & 0xF)
            {
                case 1: bp = _hw1; break;
                case 2: bp = _hw2; break;
                case 4: bp = _hw3; break;
                case 8: bp = _hw4; break;
                default:
                    Log(LogMsgType.Fatal, $"Unknown hwbp at {eip:X} (Dr6: {dr6:X8})");
                    return DBG_EXCEPTION_NOT_HANDLED;
            }

            OnHardwareBreakpoint(hThread, bp.Address, ref c);

            if (bp.BType == HWBPType.Execute && DisableBreakpoint((IntPtr)(nint)(long)bp.Address))
            {
                UpdateDR(hThread);
                c.ContextFlags = CONTEXT_CONTROL;
                c.EFlags |= 0x100;
                if (!SetThreadContext(hThread, ref c))
                    Log(LogMsgType.Fatal, "SetThreadContext failed");
            }

            return DBG_CONTINUE;
        }
        else if (_softBPReenable != 0)
        {
            byte cc = 0xCC;
            WriteProcessMemory(FProcess.hProcess, (IntPtr)(nint)_softBPReenable, (IntPtr)(&cc), 1, out _);
            _softBPReenable = 0;
            return DBG_CONTINUE;
        }
        else
        {
            return OnSinglestep((nuint)(nint)eip);
        }
    }

    private unsafe uint HandleSoftwareBreakpoint(ref DEBUG_EVENT ev)
    {
        var eip = ev.Exception.ExceptionRecord.ExceptionAddress;
        var hThread = _threads[ev.dwThreadId];

        var c = new CONTEXT { ContextFlags = CONTEXT_CONTROL };
        GetThreadContext(hThread, ref c);
        c.IP--;
        SetThreadContext(hThread, ref c);

        byte origByte = _softBPs[eip];
        if (!WriteByte(eip, origByte))
            Log(LogMsgType.Fatal, "Restoring original byte failed");

        var action = OnSoftwareBreakpoint(hThread, eip);

        if (action == SoftBPAction.ClearContinue)
        {
            _softBPs.Remove(eip);
        }
        else if (action == SoftBPAction.KeepContinue)
        {
            _softBPReenable = (nuint)(nint)c.IP;
            c.EFlags |= 0x100;
            SetThreadContext(hThread, ref c);
        }
        else if (action == SoftBPAction.KeepContinueNoStep)
        {
            if (!WriteByte(eip, 0xCC))
                Log(LogMsgType.Fatal, "KeepContinueNoStep failed");
        }

        return DBG_CONTINUE;
    }

    // ==================== Protected API ====================

    protected unsafe bool RPM(nuint address, void* buf, nuint size)
    {
        return ReadProcessMemory(FProcess.hProcess, (IntPtr)(nint)address, (IntPtr)buf, size, out _);
    }

    protected unsafe bool RPM(nuint address, byte[] buf, int size)
    {
        fixed (byte* p = buf)
            return ReadProcessMemory(FProcess.hProcess, (IntPtr)(nint)address, (IntPtr)p, (nuint)size, out _);
    }

    protected void SetBreakpoint(nuint address, HWBPType type = HWBPType.Execute, bool refreshContexts = true)
    {
        if (_hw1.Address == 0) _hw1.Change(address, type);
        else if (_hw2.Address == 0) _hw2.Change(address, type);
        else if (_hw3.Address == 0) _hw3.Change(address, type);
        else if (_hw4.Address == 0) _hw4.Change(address, type);
        else throw new Exception("All breakpoints in use");

        if (refreshContexts)
            foreach (var t in _threads.Values)
                UpdateDR(t);
    }

    protected bool DisableBreakpoint(IntPtr address)
    {
        var addr = (nuint)(nint)address;
        if (_hw1.Address == addr) _hw1.Disabled = true;
        else if (_hw2.Address == addr) _hw2.Disabled = true;
        else if (_hw3.Address == addr) _hw3.Disabled = true;
        else if (_hw4.Address == addr) _hw4.Disabled = true;
        else return false;
        return true;
    }

    protected void EnableBreakpoints()
    {
        if (_hw1.Disabled || _hw2.Disabled || _hw3.Disabled || _hw4.Disabled)
        {
            _hw1.Disabled = false; _hw2.Disabled = false;
            _hw3.Disabled = false; _hw4.Disabled = false;
            foreach (var t in _threads.Values) UpdateDR(t);
        }
    }

    protected void ResetBreakpoint(IntPtr address)
    {
        var addr = (nuint)(nint)address;
        if (_hw1.Address == addr) _hw1.Address = 0;
        else if (_hw2.Address == addr) _hw2.Address = 0;
        else if (_hw3.Address == addr) _hw3.Address = 0;
        else if (_hw4.Address == addr) _hw4.Address = 0;
        foreach (var t in _threads.Values) UpdateDR(t);
    }

    protected bool IsHWBreakpoint(IntPtr address)
    {
        var a = (nuint)(nint)address;
        return _hw1.Address == a || _hw2.Address == a || _hw3.Address == a || _hw4.Address == a;
    }

    protected void UpdateDR(IntPtr hThread)
    {
        var c = new CONTEXT { ContextFlags = CONTEXT_DEBUG_REGISTERS };
        if (GetThreadContext(hThread, ref c))
        {
            ApplyDebugRegisters(ref c);
            SetThreadContext(hThread, ref c);
        }
        else
            Log(LogMsgType.Fatal, "GetThreadContext failed");
    }

    private void ApplyDebugRegisters(ref CONTEXT c)
    {
        uint mask = 0;
#if CPUX86
        c.Dr0 = (uint)_hw1.Address;
        if (_hw1.IsSet) mask = 1;
        c.Dr1 = (uint)_hw2.Address;
        if (_hw2.IsSet) mask |= 1 << 2;
        c.Dr2 = (uint)_hw3.Address;
        if (_hw3.IsSet) mask |= 1 << 4;
        c.Dr3 = (uint)_hw4.Address;
        if (_hw4.IsSet) mask |= 1 << 6;
#else
        c.Dr0 = (ulong)_hw1.Address;
        if (_hw1.IsSet) mask = 1;
        c.Dr1 = (ulong)_hw2.Address;
        if (_hw2.IsSet) mask |= 1 << 2;
        c.Dr2 = (ulong)_hw3.Address;
        if (_hw3.IsSet) mask |= 1 << 4;
        c.Dr3 = (ulong)_hw4.Address;
        if (_hw4.IsSet) mask |= 1 << 6;
#endif

        c.Dr6 = c.Dr6 & 0xFFFFBFFF;
        c.Dr7 = mask
            | ((uint)_hw1.BType << 16)
            | ((uint)_hw2.BType << 20)
            | ((uint)_hw3.BType << 24)
            | ((uint)_hw4.BType << 28);
    }

    protected unsafe void SetSoftBP(IntPtr address)
    {
        byte b = 0;
        if (!ReadProcessMemory(FProcess.hProcess, address, (IntPtr)(&b), 1, out _))
            throw new Exception($"Read for soft bp at {address:X} failed");

        if (_softBPs.ContainsKey(address))
        {
            if (b != 0xCC)
                Log(LogMsgType.Fatal, $"Soft bp inconsistency at {address:X}!");
            return;
        }

        _softBPs[address] = b;
        if (!WriteByte(address, 0xCC))
            throw new Exception($"Write for soft bp at {address:X} failed");
        FlushInstructionCache(FProcess.hProcess, address, 1);
    }

    protected void SoftBPClear()
    {
        foreach (var bp in _softBPs)
            WriteByte(bp.Key, bp.Value);
        _softBPs.Clear();
    }

    private unsafe bool WriteByte(IntPtr address, byte value)
    {
        uint oldProt;
        VirtualProtectEx(FProcess.hProcess, address, 1, PAGE_EXECUTE_READWRITE, out oldProt);
        bool ok = WriteProcessMemory(FProcess.hProcess, address, (IntPtr)(&value), 1, out _);
        VirtualProtectEx(FProcess.hProcess, address, 1, oldProt, out _);
        FlushInstructionCache(FProcess.hProcess, address, 1);
        return ok;
    }

    // ==================== PE launch ====================

    private unsafe bool PEExecute()
    {
        if (_attachPID != 0)
            return DebugActiveProcess(_attachPID);

        PEInspect();

        string currentDir;
        var exePath = Path.GetDirectoryName(System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName)!;
        var cwd = Directory.GetCurrentDirectory();
        if (exePath.TrimEnd('\\') != cwd.TrimEnd('\\'))
            currentDir = cwd;
        else
        {
            currentDir = Path.GetDirectoryName(FExecutable)!;
            if (currentDir.EndsWith("\\")) currentDir = currentDir.TrimEnd('\\');
        }

        var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), dwFlags = (int)STARTF_USESHOWWINDOW, wShowWindow = (short)SW_SHOW };
        string cmdLine;
        if (FIsDLL)
        {
            MakeDLLExecutable();
            Log(LogMsgType.Info, "Debugging modified DLL: " + _dllExecutable);
            cmdLine = $"\"{_dllExecutable}\"";
        }
        else
            cmdLine = $"\"{FExecutable}\" {FParameters}".TrimEnd();

        uint flags = CREATE_DEFAULT_ERROR_MODE | CREATE_NEW_CONSOLE | NORMAL_PRIORITY_CLASS | DEBUG_PROCESS | DEBUG_ONLY_THIS_PROCESS;
        bool ok = CreateProcess(null, cmdLine, IntPtr.Zero, IntPtr.Zero, false, flags, IntPtr.Zero, currentDir, ref si, out var pi);
        FProcess = pi;
        return ok;
    }

    private unsafe void PEInspect()
    {
        var header = new byte[0x1000];
        using var fs = new FileStream(FExecutable, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs.Read(header, 0, header.Length);

        fixed (byte* p = header)
        {
            var dos = (IMAGE_DOS_HEADER*)p;
            if ((uint)dos->e_lfanew > 0xF00)
                throw new Exception("Selected file is not a PE or is malformed");

#if CPUX86
            var nt = (IMAGE_NT_HEADERS32*)(p + dos->e_lfanew);
            if (nt->Signature != IMAGE_NT_SIGNATURE) throw new Exception("PE file signature mismatch");
            if (nt->FileHeader.Machine != IMAGE_FILE_MACHINE_I386)
                throw new Exception("File is for the wrong architecture, please use the 64-bit version of Magicmida.");
            FIsDLL = (nt->FileHeader.Characteristics & IMAGE_FILE_DLL) != 0;
#else
            var nt = (IMAGE_NT_HEADERS64*)(p + dos->e_lfanew);
            if (nt->Signature != IMAGE_NT_SIGNATURE) throw new Exception("PE file signature mismatch");
            if (nt->FileHeader.Machine != IMAGE_FILE_MACHINE_AMD64)
                throw new Exception("File is for the wrong architecture, please use the 32-bit version of Magicmida.");
            FIsDLL = (nt->FileHeader.Characteristics & IMAGE_FILE_DLL) != 0;
#endif
        }
    }

    private unsafe void MakeDLLExecutable()
    {
#if CPUX86
        byte[] stub = { 0x8B, 0x40, 0x08, 0x6A, 0x00, 0x6A, 0x01, 0x50, 0xE8, 0x00, 0x00, 0x00, 0x00 };
#else
        byte[] stub = { 0x48, 0x83, 0xEC, 0x28, 0x65, 0x48, 0x8B, 0x04, 0x25, 0x60, 0x00, 0x00, 0x00,
                        0x48, 0x8B, 0x48, 0x10, 0xBA, 0x01, 0x00, 0x00, 0x00, 0x45, 0x31, 0xC0,
                        0xE8, 0x00, 0x00, 0x00, 0x00 };
#endif
        _dllExecutable = FExecutable + "MM.exe";
        if (!CopyFile(FExecutable, _dllExecutable, false))
            throw new Exception("Copying DLL failed");

        using var fs = new FileStream(_dllExecutable, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        var header = new byte[0x1000];
        fs.Read(header, 0, header.Length);

        fixed (byte* p = header)
        {
            var dos = (IMAGE_DOS_HEADER*)p;
#if CPUX86
            var nt = (IMAGE_NT_HEADERS32*)(p + dos->e_lfanew);
#else
            var nt = (IMAGE_NT_HEADERS64*)(p + dos->e_lfanew);
#endif
            // Remove DLL flag
            nt->FileHeader.Characteristics = (ushort)(nt->FileHeader.Characteristics & ~IMAGE_FILE_DLL);
            fs.Seek(dos->e_lfanew + 22, SeekOrigin.Begin);
            fs.Write(BitConverter.GetBytes(nt->FileHeader.Characteristics), 0, 2);

            long posOptHdr = fs.Position;

            // Disable Code Integrity Image
            if ((nt->OptionalHeader.DllCharacteristics & 0x80) != 0)
            {
                nt->OptionalHeader.DllCharacteristics = (ushort)(nt->OptionalHeader.DllCharacteristics & ~0x80);
                // DllCharacteristics offset in optional header
#if CPUX86
                int dllCharOffset = 70;
#else
                int dllCharOffset = 70;
#endif
                fs.Seek(posOptHdr + dllCharOffset, SeekOrigin.Begin);
                fs.Write(BitConverter.GetBytes(nt->OptionalHeader.DllCharacteristics), 0, 2);
            }

            var pe = new PEHeader(p);
            var epSection = pe.GetSectionByVA(nt->OptionalHeader.AddressOfEntryPoint);
            if (epSection == null) { Log(LogMsgType.Fatal, "EP section not found"); return; }

            long stubOffset = epSection.Header.PointerToRawData + epSection.Header.SizeOfRawData - stub.Length;
            fs.Seek(stubOffset, SeekOrigin.Begin);
            var curData = new byte[stub.Length];
            fs.Read(curData, 0, curData.Length);

            for (int i = 0; i < curData.Length; i++)
                if (curData[i] != 0) { Log(LogMsgType.Fatal, "Not enough room in EP section"); return; }

            Array.Copy(stub, curData, stub.Length);
            uint newEP = epSection.Header.VirtualAddress + epSection.Header.SizeOfRawData - (uint)stub.Length;
            int relOffset = (int)(nt->OptionalHeader.AddressOfEntryPoint - (newEP + (uint)stub.Length));
            BitConverter.GetBytes(relOffset).CopyTo(curData, curData.Length - 4);

            fs.Seek(stubOffset, SeekOrigin.Begin);
            fs.Write(curData, 0, curData.Length);

            nt->OptionalHeader.AddressOfEntryPoint = newEP;
#if CPUX86
            int epOffset = 40; // offset of AddressOfEntryPoint in IMAGE_OPTIONAL_HEADER32
#else
            int epOffset = 16; // offset of AddressOfEntryPoint in IMAGE_OPTIONAL_HEADER64
#endif
            fs.Seek(posOptHdr + epOffset, SeekOrigin.Begin);
            fs.Write(BitConverter.GetBytes(nt->OptionalHeader.AddressOfEntryPoint), 0, 4);
        }
    }

    // ==================== Virtual methods ====================

    protected abstract void OnDebugStart(ref IntPtr hPE, IntPtr hThread);
    protected abstract void OnHardwareBreakpoint(IntPtr hThread, nuint bpa, ref CONTEXT c);
    protected abstract SoftBPAction OnSoftwareBreakpoint(IntPtr hThread, IntPtr bpa);

    protected virtual uint OnAccessViolation(IntPtr hThread, EXCEPTION_RECORD excRec)
    {
        Log(LogMsgType.Info, $"[{FCurrentThreadID}] Access violation at 0x{excRec.ExceptionAddress:X}: {Utils.AccessViolationFlagToStr(excRec.GetExceptionInformation(0))} of 0x{excRec.GetExceptionInformation(1):X}");
        return DBG_EXCEPTION_NOT_HANDLED;
    }

    protected virtual void OnDLLLoad(string fileName, IntPtr baseAddress)
    {
        if (fileName.IndexOf("aclayers.dll", StringComparison.OrdinalIgnoreCase) >= 0)
            throw new Exception("[FATAL] Compatibility mode screws up the unpacking process.");
    }

    protected virtual void OnUnsolicitedSoftwareBreakpoint(IntPtr hThread, IntPtr bpa)
    {
        Log(LogMsgType.Info, "Unsolicited int3");
    }

    protected virtual uint OnSinglestep(nuint bpa)
    {
        EnableBreakpoints();
        return DBG_CONTINUE;
    }
}
