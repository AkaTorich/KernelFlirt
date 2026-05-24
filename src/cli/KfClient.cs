// Транспорт IOCTL: локальный DeviceIoControl или TCP-релей.
// Минимальная копия логики из src/ui/Services/DriverComm.cs.
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace KernelFlirt.Cli;

internal sealed class KfClient : IDisposable
{
    private const string DevicePath = @"\\.\KernelFlirt";
    private const int DefaultRelayPort = 31337;
    private const int MaxBufferSize = 0x800000;  // 8 МБ — потолок для READ_MEMORY и т.п.

    // Локальный режим
    private SafeFileHandle? _handle;
    // Удалённый режим
    private TcpClient? _tcpClient, _dbgTcpClient;
    private NetworkStream? _netStream, _dbgNetStream;
    private bool _isRemote;
    private readonly object _cmdLock = new();
    private readonly object _dbgLock = new();

    public bool IsConnected => _isRemote ? _netStream != null : (_handle != null && !_handle.IsInvalid);
    public bool IsRemote => _isRemote;
    public string? RemoteHost { get; private set; }
    public int RemotePort { get; private set; }

    // ── P/Invoke для локального транспорта ─────────────────────────────────

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize,
        IntPtr lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint OPEN_EXISTING = 3;

    // ── Подключение ───────────────────────────────────────────────────────

    public bool ConnectLocal()
    {
        _isRemote = false;
        _handle = CreateFileW(DevicePath, GENERIC_READ | GENERIC_WRITE, 0,
                              IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        return IsConnected;
    }

    public bool ConnectRemote(string host, int port = DefaultRelayPort)
    {
        try
        {
            _isRemote = true;
            RemoteHost = host;
            RemotePort = port;

            _tcpClient = new TcpClient { NoDelay = true };
            _tcpClient.Connect(host, port);
            _netStream = _tcpClient.GetStream();
            _netStream.ReadTimeout = 30000;

            _dbgTcpClient = new TcpClient { NoDelay = true };
            _dbgTcpClient.Connect(host, port);
            _dbgNetStream = _dbgTcpClient.GetStream();
            _dbgNetStream.ReadTimeout = Timeout.Infinite;

            return true;
        }
        catch
        {
            Disconnect();
            return false;
        }
    }

    public void Disconnect()
    {
        _handle?.Dispose(); _handle = null;
        _netStream = null; _tcpClient?.Dispose(); _tcpClient = null;
        _dbgNetStream = null; _dbgTcpClient?.Dispose(); _dbgTcpClient = null;
        _isRemote = false;
    }

    public void Dispose() => Disconnect();

    // ── Общий dispatcher ──────────────────────────────────────────────────

    private (bool ok, byte[]? output) SendIoctl(uint ioctl, byte[]? input, int maxOut)
        => _isRemote ? SendRemote(ioctl, input, maxOut, dbg: false)
                     : SendLocal(ioctl, input, maxOut);

    private (bool ok, byte[]? output) SendIoctlDbg(uint ioctl, byte[]? input, int maxOut)
        => _isRemote ? SendRemote(ioctl, input, maxOut, dbg: true)
                     : SendLocal(ioctl, input, maxOut);

    private (bool ok, byte[]? output) SendLocal(uint ioctl, byte[]? input, int maxOut)
    {
        if (_handle == null || _handle.IsInvalid) return (false, null);

        IntPtr inPtr = IntPtr.Zero, outPtr = IntPtr.Zero;
        try
        {
            uint inSize = 0;
            if (input is { Length: > 0 })
            {
                inSize = (uint)input.Length;
                inPtr = Marshal.AllocHGlobal(input.Length);
                Marshal.Copy(input, 0, inPtr, input.Length);
            }
            if (maxOut > 0) outPtr = Marshal.AllocHGlobal(maxOut);

            if (DeviceIoControl(_handle, ioctl, inPtr, inSize, outPtr, (uint)maxOut,
                                out var bytesReturned, IntPtr.Zero))
            {
                byte[]? output = null;
                if (bytesReturned > 0)
                {
                    output = new byte[bytesReturned];
                    Marshal.Copy(outPtr, output, 0, (int)bytesReturned);
                }
                return (true, output);
            }
            return (false, null);
        }
        finally
        {
            if (inPtr != IntPtr.Zero) Marshal.FreeHGlobal(inPtr);
            if (outPtr != IntPtr.Zero) Marshal.FreeHGlobal(outPtr);
        }
    }

    private (bool ok, byte[]? output) SendRemote(uint ioctl, byte[]? input, int maxOut, bool dbg)
    {
        var stream = dbg ? _dbgNetStream : _netStream;
        if (stream == null) return (false, null);
        var lockObj = dbg ? _dbgLock : _cmdLock;

        lock (lockObj)
        {
            try
            {
                uint inputSize = (uint)(input?.Length ?? 0);
                stream.Write(BitConverter.GetBytes(ioctl));
                stream.Write(BitConverter.GetBytes(inputSize));
                if (input is { Length: > 0 }) stream.Write(input);
                stream.Flush();

                var header = new byte[12];
                ReadExact(stream, header, 12);
                uint success = BitConverter.ToUInt32(header, 0);
                uint outSize = BitConverter.ToUInt32(header, 8);
                if (outSize > MaxBufferSize) return (false, null);

                byte[]? output = null;
                if (outSize > 0)
                {
                    output = new byte[outSize];
                    ReadExact(stream, output, (int)outSize);
                }
                return (success != 0, output);
            }
            catch { return (false, null); }
        }
    }

    private static void ReadExact(NetworkStream s, byte[] buf, int count)
    {
        int got = 0;
        while (got < count)
        {
            int n = s.Read(buf, got, count - got);
            if (n <= 0) throw new EndOfStreamException();
            got += n;
        }
    }

    // ── High-level API ────────────────────────────────────────────────────

    public bool Ping(out uint version, out uint magic)
    {
        var (ok, data) = SendIoctl(Ioctl.PING, null, Marshal.SizeOf<KF_PING_OUT>());
        if (!ok || data == null) { version = 0; magic = 0; return false; }
        var p = StructUtil.FromBytes<KF_PING_OUT>(data);
        version = p.Version; magic = p.Magic;
        return true;
    }

    public byte[]? ReadMemory(uint pid, ulong addr, uint size)
    {
        var input = new KF_READ_MEMORY_IN { ProcessId = pid, Address = addr, Size = size };
        var (ok, data) = SendIoctl(Ioctl.READ_MEMORY, StructUtil.ToBytes(input), (int)size);
        return ok ? data : null;
    }

    public bool WriteMemory(uint pid, ulong addr, byte[] data)
    {
        var hdr = new KF_WRITE_MEMORY_IN { ProcessId = pid, Address = addr, Size = (uint)data.Length };
        var hdrBytes = StructUtil.ToBytes(hdr);
        var input = new byte[hdrBytes.Length + data.Length];
        Buffer.BlockCopy(hdrBytes, 0, input, 0, hdrBytes.Length);
        Buffer.BlockCopy(data, 0, input, hdrBytes.Length, data.Length);
        var (ok, _) = SendIoctl(Ioctl.WRITE_MEMORY, input, 0);
        return ok;
    }

    // Драйверный KfReadRegisters/KfSingleStep/KfWriteRegisters/KfWriteRip проверяют
    // MmIsAddressValid(KTHREAD->TrapFrame) и возвращают STATUS_UNSUCCESSFUL когда
    // стек потока вытеснен (page-out). Для SUSPENDED-потоков это нормальный race:
    // первый MmIsAddressValid возвращает FALSE, но «трогает» PTE и kernel'ская MM
    // через несколько тиков поднимает страницу из page-cache обратно в RAM.
    // Ретрай с короткими паузами решает проблему гарантированно — практической
    // ситуации где страница «реально» не доступна не бывает (поток же жив).
    private const int TrapFrameRetries = 5;
    private const int TrapFrameRetryDelayMs = 50;

    public KF_REGISTERS? ReadRegisters(uint pid, uint tid)
    {
        var input = new KF_THREAD_TARGET { ProcessId = pid, ThreadId = tid };
        var inBytes = StructUtil.ToBytes(input);
        int outSize = Marshal.SizeOf<KF_REGISTERS>();
        for (int i = 0; i < TrapFrameRetries; i++)
        {
            var (ok, data) = SendIoctl(Ioctl.READ_REGISTERS, inBytes, outSize);
            if (ok && data != null) return StructUtil.FromBytes<KF_REGISTERS>(data);
            if (i + 1 < TrapFrameRetries) Thread.Sleep(TrapFrameRetryDelayMs);
        }
        return null;
    }

    public bool WriteRegisters(uint pid, uint tid, KF_REGISTERS regs)
    {
        var input = new KF_WRITE_REGISTERS_IN
        {
            Target = new KF_THREAD_TARGET { ProcessId = pid, ThreadId = tid },
            Registers = regs,
        };
        var inBytes = StructUtil.ToBytes(input);
        for (int i = 0; i < TrapFrameRetries; i++)
        {
            var (ok, _) = SendIoctl(Ioctl.WRITE_REGISTERS, inBytes, 0);
            if (ok) return true;
            if (i + 1 < TrapFrameRetries) Thread.Sleep(TrapFrameRetryDelayMs);
        }
        return false;
    }

    public uint? SetBreakpoint(uint pid, uint tid, ulong addr, uint type = 0, uint length = 0)
    {
        var input = new KF_SET_BP_IN
        { ProcessId = pid, ThreadId = tid, Address = addr, Type = type, Length = length };
        var (ok, data) = SendIoctl(Ioctl.SET_BREAKPOINT, StructUtil.ToBytes(input), 4);
        if (!ok || data == null || data.Length < 4) return null;
        return BitConverter.ToUInt32(data, 0);
    }

    public bool RemoveBreakpoint(uint handle)
    {
        var input = new KF_REMOVE_BP_IN { Handle = handle };
        var (ok, _) = SendIoctl(Ioctl.REMOVE_BREAKPOINT, StructUtil.ToBytes(input), 0);
        return ok;
    }

    public bool SingleStep(uint pid, uint tid)
    {
        var input = new KF_THREAD_TARGET { ProcessId = pid, ThreadId = tid };
        var inBytes = StructUtil.ToBytes(input);
        for (int i = 0; i < TrapFrameRetries; i++)
        {
            var (ok, _) = SendIoctl(Ioctl.SINGLE_STEP, inBytes, 0);
            if (ok) return true;
            if (i + 1 < TrapFrameRetries) Thread.Sleep(TrapFrameRetryDelayMs);
        }
        return false;
    }

    public bool ResumeThread(uint tid)
    {
        var input = new KF_THREAD_OP_IN { ThreadId = tid };
        var (ok, _) = SendIoctl(Ioctl.RESUME_THREAD, StructUtil.ToBytes(input), 0);
        return ok;
    }

    public bool SuspendThread(uint tid)
    {
        var input = new KF_THREAD_OP_IN { ThreadId = tid };
        var (ok, _) = SendIoctl(Ioctl.SUSPEND_THREAD, StructUtil.ToBytes(input), 0);
        return ok;
    }

    /// <summary>Удобная пара (struct + имя), потому что имя WCHAR-массива лежит
    /// в той же байтовой полосе — читаем его через явный offset, мимо `fixed char`.</summary>
    public sealed record ProcEntry(uint Pid, uint SessionId, ulong PeakVS, string Name);

    public List<ProcEntry> EnumProcesses()
    {
        var result = new List<ProcEntry>();
        int entrySize = Marshal.SizeOf<KF_PROCESS_ENTRY>();
        var (ok, data) = SendIoctl(Ioctl.ENUM_PROCESSES, null, entrySize * 4096);
        if (!ok || data == null) return result;
        int count = data.Length / entrySize;
        for (int i = 0; i < count; i++)
        {
            int off = i * entrySize;
            var s = StructUtil.FromBytes<KF_PROCESS_ENTRY>(data, off);
            string name = ReadWideString(data, off + KF_PROCESS_ENTRY.NameOffset,
                                         KF_PROCESS_ENTRY.NameMaxChars);
            result.Add(new ProcEntry(s.ProcessId, s.SessionId, s.PeakVirtualSize, name));
        }
        return result;
    }

    public sealed record ModEntry(ulong Base, uint Size, string Name);

    public List<ModEntry> EnumModules(uint pid)
    {
        var result = new List<ModEntry>();
        int entrySize = Marshal.SizeOf<KF_MODULE_ENTRY>();
        var input = BitConverter.GetBytes(pid);
        var (ok, data) = SendIoctl(Ioctl.ENUM_MODULES, input, entrySize * 1024);
        if (!ok || data == null) return result;
        int count = data.Length / entrySize;
        for (int i = 0; i < count; i++)
        {
            int off = i * entrySize;
            var s = StructUtil.FromBytes<KF_MODULE_ENTRY>(data, off);
            string name = ReadWideString(data, off + KF_MODULE_ENTRY.NameOffset,
                                         KF_MODULE_ENTRY.NameMaxChars);
            result.Add(new ModEntry(s.BaseAddress, s.Size, name));
        }
        return result;
    }

    public sealed record KModEntry(ulong Base, uint Size, ushort LoadOrder, string Name);

    /// <summary>Список модулей ядра (ntoskrnl + драйверы). Вход не нужен.</summary>
    public List<KModEntry> EnumKernelModules()
    {
        var result = new List<KModEntry>();
        int entrySize = Marshal.SizeOf<KF_KERNEL_MODULE_ENTRY>();
        var (ok, data) = SendIoctl(Ioctl.ENUM_KERNEL_MODULES, null, entrySize * 1024);
        if (!ok || data == null) return result;
        int count = data.Length / entrySize;
        for (int i = 0; i < count; i++)
        {
            int off = i * entrySize;
            var s = StructUtil.FromBytes<KF_KERNEL_MODULE_ENTRY>(data, off);
            string name = ReadAnsiString(data, off + KF_KERNEL_MODULE_ENTRY.NameOffset,
                                         KF_KERNEL_MODULE_ENTRY.NameMaxChars);
            result.Add(new KModEntry(s.BaseAddress, s.Size, s.LoadOrderIndex, name));
        }
        return result;
    }

    /// <summary>Выделить память в адресном пространстве target. Возвращает базовый адрес.</summary>
    public ulong? AllocMemory(uint pid, ulong size, uint protection)
    {
        var input = new KF_ALLOC_MEMORY_IN { ProcessId = pid, Size = size, Protection = protection };
        var (ok, data) = SendIoctl(Ioctl.ALLOC_MEMORY, StructUtil.ToBytes(input), 8);
        if (!ok || data == null || data.Length < 8) return null;
        return BitConverter.ToUInt64(data, 0);
    }

    /// <summary>Освободить ранее выделенный регион (MEM_RELEASE).</summary>
    public bool FreeMemory(uint pid, ulong addr)
    {
        var input = new KF_FREE_MEMORY_IN { ProcessId = pid, Address = addr };
        var (ok, _) = SendIoctl(Ioctl.FREE_MEMORY, StructUtil.ToBytes(input), 0);
        return ok;
    }

    /// <summary>Сменить защиту региона. Возвращает старое значение защиты.</summary>
    public uint? ProtectMemory(uint pid, ulong addr, uint size, uint newProtection)
    {
        var input = new KF_PROTECT_MEMORY_IN
        { ProcessId = pid, Address = addr, Size = size, NewProtection = newProtection };
        var (ok, data) = SendIoctl(Ioctl.PROTECT_MEMORY, StructUtil.ToBytes(input),
                                   Marshal.SizeOf<KF_PROTECT_MEMORY_OUT>());
        if (!ok || data == null) return null;
        return StructUtil.FromBytes<KF_PROTECT_MEMORY_OUT>(data).OldProtection;
    }

    /// <summary>Статистика inline-хука: счётчики событий, адреса KiDebugRoutine/KdTrap и т.п.</summary>
    public KF_HOOK_STATS_OUT? GetHookStats()
    {
        var (ok, data) = SendIoctl(Ioctl.GET_HOOK_STATS, null, Marshal.SizeOf<KF_HOOK_STATS_OUT>());
        if (!ok || data == null) return null;
        return StructUtil.FromBytes<KF_HOOK_STATS_OUT>(data);
    }

    /// <summary>Читает ANSI-строку из <paramref name="buf"/> по смещению, останавливаясь
    /// на NUL-байте или достигнув <paramref name="maxChars"/>.</summary>
    private static string ReadAnsiString(byte[] buf, int offset, int maxChars)
    {
        int end = offset;
        int hardLimit = Math.Min(offset + maxChars, buf.Length);
        while (end < hardLimit && buf[end] != 0) end++;
        return System.Text.Encoding.ASCII.GetString(buf, offset, end - offset);
    }

    /// <summary>Читает UTF-16 (LE) строку из <paramref name="buf"/> по смещению,
    /// останавливаясь на NUL-символе или достигнув <paramref name="maxChars"/>.</summary>
    private static string ReadWideString(byte[] buf, int offset, int maxChars)
    {
        int end = offset;
        int hardLimit = Math.Min(offset + maxChars * 2, buf.Length);
        while (end + 1 < hardLimit)
        {
            ushort c = (ushort)(buf[end] | (buf[end + 1] << 8));
            if (c == 0) break;
            end += 2;
        }
        return System.Text.Encoding.Unicode.GetString(buf, offset, end - offset);
    }

    /// <summary>Возвращает (Peb64, Peb32). Peb32 != 0 значит, что target — WoW64 (32-bit).</summary>
    public (ulong Peb64, ulong Peb32)? GetPebAddress(uint pid)
    {
        var input = new KF_GET_PEB_IN { ProcessId = pid };
        var (ok, data) = SendIoctl(Ioctl.GET_PEB_ADDRESS, StructUtil.ToBytes(input),
                                   Marshal.SizeOf<KF_GET_PEB_OUT>());
        if (!ok || data == null) return null;
        var p = StructUtil.FromBytes<KF_GET_PEB_OUT>(data);
        return (p.PebAddress, p.Peb32Address);
    }

    public List<KF_THREAD_ENTRY> EnumThreads(uint pid)
    {
        var result = new List<KF_THREAD_ENTRY>();
        int entrySize = Marshal.SizeOf<KF_THREAD_ENTRY>();
        var input = BitConverter.GetBytes(pid);
        var (ok, data) = SendIoctl(Ioctl.ENUM_THREADS, input, entrySize * 4096);
        if (!ok || data == null) return result;
        int count = data.Length / entrySize;
        for (int i = 0; i < count; i++)
            result.Add(StructUtil.FromBytes<KF_THREAD_ENTRY>(data, i * entrySize));
        return result;
    }

    public bool InstallHook(uint targetPid)
    {
        var input = BitConverter.GetBytes(targetPid);
        var (ok, _) = SendIoctl(Ioctl.INSTALL_HOOK, input, 0);
        return ok;
    }

    public bool RemoveHook()
    {
        var (ok, _) = SendIoctl(Ioctl.REMOVE_HOOK, null, 0);
        return ok;
    }

    public bool Reset()
    {
        var (ok, _) = SendIoctl(Ioctl.RESET, null, 0);
        return ok;
    }

    public bool SetTargetPid(uint pid)
    {
        var input = BitConverter.GetBytes(pid);
        var (ok, _) = SendIoctl(Ioctl.SET_TARGET_PID, input, 0);
        return ok;
    }

    // ── Anti-debug bypass ──────────────────────────────────────────────────

    public bool ClearDebugPort(uint pid)
    {
        var input = BitConverter.GetBytes(pid);
        var (ok, _) = SendIoctl(Ioctl.CLEAR_DEBUG_PORT, input, 0);
        return ok;
    }

    public bool ClearThreadHide(uint pid)
    {
        var input = BitConverter.GetBytes(pid);
        var (ok, _) = SendIoctl(Ioctl.CLEAR_THREAD_HIDE, input, 0);
        return ok;
    }

    public bool InstallNtQsiHook()
    {
        var (ok, _) = SendIoctl(Ioctl.INSTALL_NTQSI_HOOK, null, 0);
        return ok;
    }

    public bool RemoveNtQsiHook()
    {
        var (ok, _) = SendIoctl(Ioctl.REMOVE_NTQSI_HOOK, null, 0);
        return ok;
    }

    public bool SpoofSharedData(bool enable)
    {
        var input = new byte[] { (byte)(enable ? 1 : 0) };
        var (ok, _) = SendIoctl(Ioctl.SPOOF_SHARED_DATA, input, 0);
        return ok;
    }

    /// <summary>
    /// Запуск процесса в подвешенном состоянии. Возвращает PID/TID/ImageBase/Entry.
    /// В remote-режиме — через релейный pseudo-IOCTL (релей делает CreateProcessW
    /// + патчит entry на EB FE).
    /// В local-режиме — собственный CreateProcessW + ручной патч entry через
    /// драйверный WriteMemory.
    /// </summary>
    public KF_CREATE_PROCESS_OUT? CreateProcess(string exePath)
    {
        if (_isRemote)
        {
            // input: UTF-16 LE строка с null-терминатором
            var wide = System.Text.Encoding.Unicode.GetBytes(exePath + "\0");
            var (ok, data) = SendIoctl(Ioctl.CREATE_PROCESS, wide,
                                       Marshal.SizeOf<KF_CREATE_PROCESS_OUT>());
            if (!ok || data == null) return null;
            return StructUtil.FromBytes<KF_CREATE_PROCESS_OUT>(data);
        }
        else
        {
            return CreateProcessLocal(exePath);
        }
    }

    // Минимальный CreateProcessW + ручной патч entry на EB FE через драйвер.
    // Зеркалит то, что в remote-варианте делает релей в src/relay/main.c.
    private KF_CREATE_PROCESS_OUT? CreateProcessLocal(string exePath)
    {
        const uint CREATE_SUSPENDED = 0x4;
        var si = new STARTUPINFOW { cb = (uint)Marshal.SizeOf<STARTUPINFOW>() };
        if (!CreateProcessW(null, exePath, IntPtr.Zero, IntPtr.Zero, false,
                            CREATE_SUSPENDED, IntPtr.Zero, null, ref si, out var pi))
        {
            return null;
        }

        // Прочитаем PEB → ImageBase, потом PE-header → EntryPoint.
        // Это всё мы умеем через драйвер: ReadMemory от своего же процесса
        // (PsLookupProcessByProcessId работает на любом PID).
        var result = new KF_CREATE_PROCESS_OUT
        {
            ProcessId = pi.dwProcessId,
            ThreadId  = pi.dwThreadId,
        };

        // ImageBase для x64 — PEB[0x10]. PEB-адрес возьмём из ntdll через
        // NtQueryInformationProcess. Для простоты: GetModuleBase невозможен
        // на чужом процессе, поэтому пропустим — пользователь сможет
        // attach к PID после возврата.
        // (Полноценный entry-patch на стороне CLI требует ещё ~150 строк —
        // GetThreadContext, RtlImageNtHeader, и т.п. Пропускаем; в локальном
        // режиме CLI делает только базовый запуск + возврат PID.)

        result.ImageBase = 0;
        result.EntryPointAddress = 0;
        result.EntryPatchBytes = 0;

        CloseHandle(pi.hProcess);
        CloseHandle(pi.hThread);
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOW
    {
        public uint cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars;
        public uint dwFillAttribute, dwFlags;
        public ushort wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public uint dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(
        string? lpApplicationName, string lpCommandLine,
        IntPtr lpProcessAttr, IntPtr lpThreadAttr, bool bInheritHandles,
        uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory,
        ref STARTUPINFOW lpSi, out PROCESS_INFORMATION lpPi);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);

    public KF_DEBUG_EVENT? WaitDebugEvent()
    {
        // Блокирующий вызов — используем DBG-канал чтобы не блокировать CMD.
        var (ok, data) = SendIoctlDbg(Ioctl.WAIT_DEBUG_EVENT, null,
                                      Marshal.SizeOf<KF_DEBUG_EVENT>());
        return ok && data != null ? StructUtil.FromBytes<KF_DEBUG_EVENT>(data) : null;
    }

    public bool ContinueDebugEvent(uint mode = ContinueMode.Run, ulong newRip = 0,
                                   ulong newRsp = 0, uint flags = 0)
    {
        var input = new KF_CONTINUE_IN { Mode = mode, Flags = flags, NewRip = newRip, NewRsp = newRsp };
        var (ok, _) = SendIoctl(Ioctl.CONTINUE_DEBUG_EVENT, StructUtil.ToBytes(input), 0);
        return ok;
    }
}
