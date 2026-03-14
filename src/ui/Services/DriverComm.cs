using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using KernelFlirt.UI.Models;

namespace KernelFlirt.UI.Services;

/// <summary>
/// Communicates with the KernelFlirt kernel driver.
/// Supports two transports:
///   - Local: DeviceIoControl (driver on same machine)
///   - Remote: TCP via KfRelay agent (driver on VM, UI on host)
/// </summary>
public class DriverComm : IDisposable
{
    private const string DevicePath = @"\\.\KernelFlirt";
    private const uint DeviceType = 0x00008000;
    private const int DefaultRelayPort = 31337;

    private SafeFileHandle? _handle;
    private TcpClient? _tcpClient;
    private NetworkStream? _netStream;
    private readonly object _remoteLock = new();
    // Separate TCP connection for debug events (WAIT/CONTINUE) to avoid blocking cmd channel
    private TcpClient? _dbgTcpClient;
    private NetworkStream? _dbgNetStream;
    private readonly object _dbgRemoteLock = new();
    private bool _disposed;
    private bool _isRemote;

    #region IOCTL Codes

    private static uint CTL_CODE(uint deviceType, uint function, uint method, uint access)
        => (deviceType << 16) | (access << 14) | (function << 2) | method;

    private static readonly uint IOCTL_KF_PING            = CTL_CODE(DeviceType, 0x8FF, 0, 0);
    private static readonly uint IOCTL_KF_READ_MEMORY     = CTL_CODE(DeviceType, 0x800, 0, 0);
    private static readonly uint IOCTL_KF_WRITE_MEMORY    = CTL_CODE(DeviceType, 0x801, 0, 0);
    private static readonly uint IOCTL_KF_SET_BREAKPOINT  = CTL_CODE(DeviceType, 0x802, 0, 0);
    private static readonly uint IOCTL_KF_REMOVE_BREAKPOINT = CTL_CODE(DeviceType, 0x803, 0, 0);
    private static readonly uint IOCTL_KF_SINGLE_STEP     = CTL_CODE(DeviceType, 0x804, 0, 0);
    private static readonly uint IOCTL_KF_READ_REGISTERS  = CTL_CODE(DeviceType, 0x810, 0, 0);
    private static readonly uint IOCTL_KF_WRITE_REGISTERS = CTL_CODE(DeviceType, 0x811, 0, 0);
    private static readonly uint IOCTL_KF_ENUM_MODULES    = CTL_CODE(DeviceType, 0x820, 0, 0);
    private static readonly uint IOCTL_KF_ENUM_KERNEL_MODULES = CTL_CODE(DeviceType, 0x821, 0, 0);
    private static readonly uint IOCTL_KF_ENUM_THREADS    = CTL_CODE(DeviceType, 0x830, 0, 0);
    private static readonly uint IOCTL_KF_SUSPEND_THREAD  = CTL_CODE(DeviceType, 0x831, 0, 0);
    private static readonly uint IOCTL_KF_RESUME_THREAD   = CTL_CODE(DeviceType, 0x832, 0, 0);
    private static readonly uint IOCTL_KF_ENUM_PROCESSES  = CTL_CODE(DeviceType, 0x835, 0, 0);
    private static readonly uint IOCTL_KF_INSTALL_HOOK    = CTL_CODE(DeviceType, 0x840, 0, 0);
    private static readonly uint IOCTL_KF_REMOVE_HOOK     = CTL_CODE(DeviceType, 0x841, 0, 0);
    private static readonly uint IOCTL_KF_WAIT_DEBUG_EVENT = CTL_CODE(DeviceType, 0x842, 0, 0);
    private static readonly uint IOCTL_KF_CONTINUE_DEBUG_EVENT = CTL_CODE(DeviceType, 0x843, 0, 0);
    private static readonly uint IOCTL_KF_GET_HOOK_STATS = CTL_CODE(DeviceType, 0x844, 0, 0);
    private static readonly uint IOCTL_KF_RESET          = CTL_CODE(DeviceType, 0x8FE, 0, 0);

    // Relay pseudo-IOCTLs (handled by relay, not driver)
    private static readonly uint IOCTL_KF_LIST_DRIVES     = CTL_CODE(DeviceType, 0x900, 0, 0);
    private static readonly uint IOCTL_KF_LIST_DIRECTORY   = CTL_CODE(DeviceType, 0x901, 0, 0);
    private static readonly uint IOCTL_KF_CREATE_PROCESS   = CTL_CODE(DeviceType, 0x902, 0, 0);
    private static readonly uint IOCTL_KF_LOAD_DRIVER      = CTL_CODE(DeviceType, 0x903, 0, 0);
    private static readonly uint IOCTL_KF_UNLOAD_DRIVER    = CTL_CODE(DeviceType, 0x904, 0, 0);
    private static readonly uint IOCTL_KF_START_DRIVER     = CTL_CODE(DeviceType, 0x905, 0, 0);

    #endregion

    #region Native Structures (matching kf_shared.h)

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct KF_READ_MEMORY_IN
    {
        public uint ProcessId;
        public ulong Address;
        public uint Size;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct KF_THREAD_TARGET
    {
        public uint ProcessId;
        public uint ThreadId;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct KF_REGISTERS
    {
        public ulong Rax, Rbx, Rcx, Rdx;
        public ulong Rsi, Rdi, Rbp, Rsp;
        public ulong R8, R9, R10, R11;
        public ulong R12, R13, R14, R15;
        public ulong Rip;
        public ulong Rflags;
        public ushort Cs, Ds, Es, Fs, Gs, Ss;
        public ulong Dr0, Dr1, Dr2, Dr3, Dr6, Dr7;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct KF_SET_BP_IN
    {
        public uint ProcessId;
        public uint ThreadId;
        public ulong Address;
        public uint Type;
        public uint Length;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct KF_SET_BP_OUT
    {
        public uint Handle;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct KF_REMOVE_BP_IN
    {
        public uint Handle;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct KF_ENUM_MODULES_IN
    {
        public uint ProcessId;
    }

    private const int KF_MAX_MODULE_NAME = 256;

    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
    private unsafe struct KF_MODULE_ENTRY
    {
        public ulong BaseAddress;
        public uint Size;
        public fixed char Name[KF_MAX_MODULE_NAME];
    }

    private const int KF_MAX_KMOD_NAME = 256;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private unsafe struct KF_KERNEL_MODULE_ENTRY
    {
        public ulong BaseAddress;
        public uint Size;
        public ushort LoadOrderIndex;
        public fixed byte Name[KF_MAX_KMOD_NAME]; // ANSI
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct KF_ENUM_THREADS_IN
    {
        public uint ProcessId;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct KF_THREAD_ENTRY
    {
        public uint ThreadId;
        public ulong StartAddress;
        public uint State;
        public uint Priority;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct KF_THREAD_OP_IN
    {
        public uint ThreadId;
    }

    private const int KF_MAX_PROCESS_NAME = 260;

    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
    private unsafe struct KF_PROCESS_ENTRY
    {
        public uint ProcessId;
        public uint SessionId;
        public ulong PeakVirtualSize;
        public fixed char Name[KF_MAX_PROCESS_NAME];
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct KF_WRITE_REGISTERS_IN
    {
        public KF_THREAD_TARGET Target;
        public KF_REGISTERS Registers;
    }

    // Relay pseudo-IOCTL structures
    private const int KF_MAX_DRIVE_LABEL = 64;

    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
    private unsafe struct KF_DRIVE_ENTRY
    {
        public byte Letter;
        public fixed byte Padding[3];
        public uint DriveType;
        public fixed char Label[KF_MAX_DRIVE_LABEL];
    }

    private const int KF_MAX_FILENAME = 260;

    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
    private unsafe struct KF_DIR_ENTRY
    {
        public uint IsDirectory;
        public ulong FileSize;
        public fixed char Name[KF_MAX_FILENAME];
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct KF_CREATE_PROCESS_OUT
    {
        public uint ProcessId;
        public uint ThreadId;
        public ulong ImageBase;
    }

    private const int KF_MAX_SERVICE_NAME = 64;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private unsafe struct KF_LOAD_DRIVER_OUT
    {
        public fixed byte ServiceName[KF_MAX_SERVICE_NAME];
        public uint EntryPointRva;
        public byte OriginalByte;
        public byte Reserved0;
        public byte Reserved1;
        public byte Reserved2;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct KF_DEBUG_EVENT
    {
        public uint Type;
        public uint ProcessId;
        public uint ThreadId;
        public ulong Address;
        public uint PreviousMode;
        public KF_REGISTERS Registers;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct KF_PING_OUT
    {
        public uint Version;
        public uint Magic;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct KF_HOOK_STATS_OUT
    {
        public uint HookCallCount;
        public uint BpHitCount;
        public uint BpNotFoundCount;
        public uint StepCount;
        public byte KdDebuggerEnabled;
        public byte KdDebuggerNotPresent;
        public byte Reserved0;
        public byte Reserved1;
        public uint TargetCallCount;
        public ulong LastTargetAddr;
        public uint LastTargetCode;
        public uint LastNonTargetPid;
        public ulong KiDebugRoutineAddr;
        public ulong KiDebugRoutineOrig;
        public ulong KiDebugRoutineNow;
        public ulong HookedFuncAddr;
        public ulong KdTrapAddr;
    }

    #endregion

    #region P/Invoke (local transport)

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize,
        IntPtr lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint OPEN_EXISTING = 3;

    #endregion

    public bool IsConnected => _isRemote
        ? (_tcpClient?.Connected ?? false)
        : (_handle is { IsInvalid: false, IsClosed: false });

    public bool IsRemote => _isRemote;
    public string? RemoteHost { get; private set; }
    public int RemotePort { get; private set; }

    /// <summary>Connect to local driver via DeviceIoControl.</summary>
    public bool Connect()
    {
        _isRemote = false;
        _handle = CreateFileW(DevicePath, GENERIC_READ | GENERIC_WRITE, 0,
                              IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        return IsConnected;
    }

    /// <summary>Close all connections without marking as disposed (allows reconnect).</summary>
    public void Disconnect()
    {
        _handle?.Dispose();
        _handle = null;
        _netStream = null;
        _tcpClient?.Dispose();
        _tcpClient = null;
        _dbgNetStream = null;
        _dbgTcpClient?.Dispose();
        _dbgTcpClient = null;
        _isRemote = false;
    }

    /// <summary>Connect to remote relay agent via TCP (dual-channel: cmd + dbg).</summary>
    public bool ConnectRemote(string host, int port = DefaultRelayPort)
    {
        try
        {
            _isRemote = true;
            RemoteHost = host;
            RemotePort = port;

            // Connection 1: CMD channel (normal IOCTLs)
            _tcpClient = new TcpClient();
            _tcpClient.NoDelay = true;
            _tcpClient.Connect(host, port);
            _netStream = _tcpClient.GetStream();
            _netStream.ReadTimeout = 30000;  // 30s timeout for CMD channel

            // Connection 2: DBG channel (WAIT_DEBUG_EVENT / CONTINUE_DEBUG_EVENT)
            _dbgTcpClient = new TcpClient();
            _dbgTcpClient.NoDelay = true;
            _dbgTcpClient.Connect(host, port);
            _dbgNetStream = _dbgTcpClient.GetStream();
            // No read timeout on DBG channel — WaitDebugEvent is expected to block

            return true;
        }
        catch
        {
            _tcpClient?.Dispose();
            _tcpClient = null;
            _netStream = null;
            _dbgTcpClient?.Dispose();
            _dbgTcpClient = null;
            _dbgNetStream = null;
            return false;
        }
    }

    #region Unified IOCTL dispatcher

    /// <summary>
    /// Send an IOCTL and receive a response, using either local or remote transport.
    /// Returns (success, outputBytes).
    /// </summary>
    private (bool success, byte[]? output) SendIoctl(uint ioctlCode, byte[]? inputData, int maxOutputSize)
    {
        if (_isRemote)
            return SendIoctlRemote(ioctlCode, inputData, maxOutputSize);
        else
            return SendIoctlLocal(ioctlCode, inputData, maxOutputSize);
    }

    private (bool success, byte[]? output) SendIoctlLocal(uint ioctlCode, byte[]? inputData, int maxOutputSize)
    {
        if (!IsConnected) return (false, null);

        var inPtr = IntPtr.Zero;
        var outPtr = IntPtr.Zero;
        try
        {
            uint inSize = 0;
            if (inputData != null && inputData.Length > 0)
            {
                inSize = (uint)inputData.Length;
                inPtr = Marshal.AllocHGlobal(inputData.Length);
                Marshal.Copy(inputData, 0, inPtr, inputData.Length);
            }

            if (maxOutputSize > 0)
                outPtr = Marshal.AllocHGlobal(maxOutputSize);

            if (DeviceIoControl(_handle!, ioctlCode, inPtr, inSize,
                    outPtr, (uint)maxOutputSize, out var bytesReturned, IntPtr.Zero))
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

    private (bool success, byte[]? output) SendIoctlRemote(uint ioctlCode, byte[]? inputData, int maxOutputSize)
    {
        if (_netStream == null) return (false, null);

        lock (_remoteLock)
        {
            try
            {
                var stream = _netStream;
                uint inputSize = (uint)(inputData?.Length ?? 0);

                // Send: [ioctl_code:4][input_size:4][input_data]
                stream.Write(BitConverter.GetBytes(ioctlCode));
                stream.Write(BitConverter.GetBytes(inputSize));
                if (inputData != null && inputData.Length > 0)
                    stream.Write(inputData);
                stream.Flush();

                // Recv: [success:4][win32_error:4][output_size:4][output_data]
                var header = new byte[12];
                ReadExact(stream, header, 12);

                uint success = BitConverter.ToUInt32(header, 0);
                // uint win32Error = BitConverter.ToUInt32(header, 4);
                uint outputSize = BitConverter.ToUInt32(header, 8);

                byte[]? output = null;
                if (outputSize > 0)
                {
                    output = new byte[outputSize];
                    ReadExact(stream, output, (int)outputSize);
                }

                return (success != 0, output);
            }
            catch
            {
                return (false, null);
            }
        }
    }

    /// <summary>
    /// Send an IOCTL on the dedicated debug channel (for WAIT/CONTINUE debug events).
    /// This uses a separate TCP connection so it doesn't block normal IOCTLs.
    /// </summary>
    private (bool success, byte[]? output) SendIoctlDbg(uint ioctlCode, byte[]? inputData, int maxOutputSize)
    {
        if (_isRemote)
        {
            if (_dbgNetStream == null) return (false, null);
            lock (_dbgRemoteLock)
            {
                try
                {
                    var stream = _dbgNetStream;
                    uint inputSize = (uint)(inputData?.Length ?? 0);
                    stream.Write(BitConverter.GetBytes(ioctlCode));
                    stream.Write(BitConverter.GetBytes(inputSize));
                    if (inputData != null && inputData.Length > 0)
                        stream.Write(inputData);
                    stream.Flush();

                    var header = new byte[12];
                    ReadExact(stream, header, 12);
                    uint success = BitConverter.ToUInt32(header, 0);
                    uint win32err = BitConverter.ToUInt32(header, 4);
                    uint outputSize = BitConverter.ToUInt32(header, 8);
                    byte[]? output = null;
                    if (outputSize > 0)
                    {
                        output = new byte[outputSize];
                        ReadExact(stream, output, (int)outputSize);
                    }
                    if (success == 0)
                        System.Diagnostics.Debug.WriteLine($"[DriverComm] DBG IOCTL 0x{ioctlCode:X8} failed: win32err={win32err} outSize={outputSize}");
                    return (success != 0, output);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[DriverComm] DBG IOCTL exception: {ex.Message}");
                    return (false, null);
                }
            }
        }
        else
        {
            // Local mode: just use the normal IOCTL path
            return SendIoctlLocal(ioctlCode, inputData, maxOutputSize);
        }
    }

    private static void ReadExact(NetworkStream stream, byte[] buffer, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            int n = stream.Read(buffer, offset, count - offset);
            if (n == 0) throw new IOException("Connection closed");
            offset += n;
        }
    }

    #endregion

    #region Struct serialization helpers

    private static byte[] StructToBytes<T>(T value) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        byte[] bytes = new byte[size];
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(value, ptr, false);
            Marshal.Copy(ptr, bytes, 0, size);
        }
        finally { Marshal.FreeHGlobal(ptr); }
        return bytes;
    }

    private static T BytesToStruct<T>(byte[] data, int offset = 0) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(data, offset, ptr, size);
            return Marshal.PtrToStructure<T>(ptr);
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    #endregion

    #region Public API

    public (uint version, bool success) Ping()
    {
        var (ok, data) = SendIoctl(IOCTL_KF_PING, null, Marshal.SizeOf<KF_PING_OUT>());
        if (ok && data != null)
        {
            var output = BytesToStruct<KF_PING_OUT>(data);
            return (output.Version, output.Magic == 0x4B464C54);
        }
        return (0, false);
    }

    public byte[]? ReadMemory(uint pid, ulong address, uint size)
    {
        var input = new KF_READ_MEMORY_IN { ProcessId = pid, Address = address, Size = size };
        var (ok, data) = SendIoctl(IOCTL_KF_READ_MEMORY, StructToBytes(input), (int)size);
        return ok ? data : null;
    }

    public bool WriteMemory(uint pid, ulong address, byte[] data)
    {
        int headerSize = Marshal.SizeOf<KF_READ_MEMORY_IN>();
        byte[] input = new byte[headerSize + data.Length];

        // Write header manually: PID(4) + Address(8) + Size(4)
        BitConverter.GetBytes(pid).CopyTo(input, 0);
        BitConverter.GetBytes(address).CopyTo(input, 4);
        BitConverter.GetBytes(data.Length).CopyTo(input, 12);
        data.CopyTo(input, headerSize);

        var (ok, _) = SendIoctl(IOCTL_KF_WRITE_MEMORY, input, 0);
        return ok;
    }

    public List<Register> ReadRegisters(uint pid, uint tid)
    {
        var input = new KF_THREAD_TARGET { ProcessId = pid, ThreadId = tid };
        var (ok, data) = SendIoctl(IOCTL_KF_READ_REGISTERS, StructToBytes(input), Marshal.SizeOf<KF_REGISTERS>());
        if (!ok || data == null) return [];

        var r = BytesToStruct<KF_REGISTERS>(data);
        return
        [
            new() { Name = "RAX", Value = r.Rax },
            new() { Name = "RBX", Value = r.Rbx },
            new() { Name = "RCX", Value = r.Rcx },
            new() { Name = "RDX", Value = r.Rdx },
            new() { Name = "RSI", Value = r.Rsi },
            new() { Name = "RDI", Value = r.Rdi },
            new() { Name = "RBP", Value = r.Rbp },
            new() { Name = "RSP", Value = r.Rsp },
            new() { Name = "R8",  Value = r.R8 },
            new() { Name = "R9",  Value = r.R9 },
            new() { Name = "R10", Value = r.R10 },
            new() { Name = "R11", Value = r.R11 },
            new() { Name = "R12", Value = r.R12 },
            new() { Name = "R13", Value = r.R13 },
            new() { Name = "R14", Value = r.R14 },
            new() { Name = "R15", Value = r.R15 },
            new() { Name = "RIP", Value = r.Rip },
            new() { Name = "RFLAGS", Value = r.Rflags },
            ..Register.ExpandFlags(r.Rflags),
            new() { Name = "DR0", Value = r.Dr0 },
            new() { Name = "DR1", Value = r.Dr1 },
            new() { Name = "DR2", Value = r.Dr2 },
            new() { Name = "DR3", Value = r.Dr3 },
            new() { Name = "DR6", Value = r.Dr6 },
            new() { Name = "DR7", Value = r.Dr7 },
        ];
    }

    public List<ModuleInfo> EnumModules(uint pid)
    {
        var input = new KF_ENUM_MODULES_IN { ProcessId = pid };
        const int maxModules = 512;
        int entrySize = Marshal.SizeOf<KF_MODULE_ENTRY>();
        int outSize = entrySize * maxModules;

        var (ok, data) = SendIoctl(IOCTL_KF_ENUM_MODULES, StructToBytes(input), outSize);
        var modules = new List<ModuleInfo>();
        if (!ok || data == null) return modules;

        int count = data.Length / entrySize;
        for (int i = 0; i < count; i++)
        {
            var entry = BytesToStruct<KF_MODULE_ENTRY>(data, i * entrySize);
            unsafe
            {
                modules.Add(new ModuleInfo
                {
                    BaseAddress = entry.BaseAddress,
                    Size = entry.Size,
                    Name = new string(entry.Name).TrimEnd('\0')
                });
            }
        }
        return modules;
    }

    public List<KernelModuleInfo> EnumKernelModules()
    {
        const int maxModules = 512;
        int entrySize = Marshal.SizeOf<KF_KERNEL_MODULE_ENTRY>();
        int outSize = entrySize * maxModules;

        var (ok, data) = SendIoctl(IOCTL_KF_ENUM_KERNEL_MODULES, null, outSize);
        var kmods = new List<KernelModuleInfo>();
        if (!ok || data == null) return kmods;

        int count = data.Length / entrySize;
        for (int i = 0; i < count; i++)
        {
            var entry = BytesToStruct<KF_KERNEL_MODULE_ENTRY>(data, i * entrySize);
            unsafe
            {
                byte[] nameBytes = new byte[KF_MAX_KMOD_NAME];
                for (int j = 0; j < KF_MAX_KMOD_NAME; j++)
                    nameBytes[j] = entry.Name[j];
                string name = System.Text.Encoding.ASCII.GetString(nameBytes).TrimEnd('\0');

                kmods.Add(new KernelModuleInfo
                {
                    BaseAddress = entry.BaseAddress,
                    Size = entry.Size,
                    LoadOrder = entry.LoadOrderIndex,
                    Name = name
                });
            }
        }
        return kmods;
    }

    public List<ThreadInfo> EnumThreads(uint pid)
    {
        var input = new KF_ENUM_THREADS_IN { ProcessId = pid };
        const int maxThreads = 1024;
        int entrySize = Marshal.SizeOf<KF_THREAD_ENTRY>();
        int outSize = entrySize * maxThreads;

        var (ok, data) = SendIoctl(IOCTL_KF_ENUM_THREADS, StructToBytes(input), outSize);
        var threads = new List<ThreadInfo>();
        if (!ok || data == null) return threads;

        int count = data.Length / entrySize;
        for (int i = 0; i < count; i++)
        {
            var entry = BytesToStruct<KF_THREAD_ENTRY>(data, i * entrySize);
            threads.Add(new ThreadInfo
            {
                ThreadId = entry.ThreadId,
                StartAddress = entry.StartAddress,
                State = entry.State,
                Priority = entry.Priority
            });
        }
        return threads;
    }

    public uint? SetBreakpoint(uint pid, uint tid, ulong address, BreakpointType type, uint length = 1)
    {
        var input = new KF_SET_BP_IN
        {
            ProcessId = pid, ThreadId = tid, Address = address,
            Type = (uint)type, Length = length
        };
        var (ok, data) = SendIoctl(IOCTL_KF_SET_BREAKPOINT, StructToBytes(input), Marshal.SizeOf<KF_SET_BP_OUT>());
        if (ok && data != null)
            return BytesToStruct<KF_SET_BP_OUT>(data).Handle;
        return null;
    }

    public bool RemoveBreakpoint(uint handle)
    {
        var input = new KF_REMOVE_BP_IN { Handle = handle };
        var (ok, _) = SendIoctl(IOCTL_KF_REMOVE_BREAKPOINT, StructToBytes(input), 0);
        return ok;
    }

    public bool SingleStep(uint pid, uint tid)
    {
        var input = new KF_THREAD_TARGET { ProcessId = pid, ThreadId = tid };
        var (ok, _) = SendIoctl(IOCTL_KF_SINGLE_STEP, StructToBytes(input), 0);
        return ok;
    }

    public bool WriteRip(uint pid, uint tid, ulong newRip)
    {
        // Read current registers, modify RIP, write back
        var regs = ReadRegisters(pid, tid);
        if (regs.Count == 0) return false;

        var r = new KF_REGISTERS();
        foreach (var reg in regs)
        {
            switch (reg.Name)
            {
                case "RAX": r.Rax = reg.Value; break;
                case "RBX": r.Rbx = reg.Value; break;
                case "RCX": r.Rcx = reg.Value; break;
                case "RDX": r.Rdx = reg.Value; break;
                case "RSI": r.Rsi = reg.Value; break;
                case "RDI": r.Rdi = reg.Value; break;
                case "RBP": r.Rbp = reg.Value; break;
                case "RSP": r.Rsp = reg.Value; break;
                case "R8":  r.R8  = reg.Value; break;
                case "R9":  r.R9  = reg.Value; break;
                case "R10": r.R10 = reg.Value; break;
                case "R11": r.R11 = reg.Value; break;
                case "R12": r.R12 = reg.Value; break;
                case "R13": r.R13 = reg.Value; break;
                case "R14": r.R14 = reg.Value; break;
                case "R15": r.R15 = reg.Value; break;
                case "RIP": r.Rip = reg.Value; break;
                case "RFLAGS": r.Rflags = reg.Value; break;
                case "DR0": r.Dr0 = reg.Value; break;
                case "DR1": r.Dr1 = reg.Value; break;
                case "DR2": r.Dr2 = reg.Value; break;
                case "DR3": r.Dr3 = reg.Value; break;
                case "DR6": r.Dr6 = reg.Value; break;
                case "DR7": r.Dr7 = reg.Value; break;
            }
        }

        // Override RIP
        r.Rip = newRip;

        var input = new KF_WRITE_REGISTERS_IN
        {
            Target = new KF_THREAD_TARGET { ProcessId = pid, ThreadId = tid },
            Registers = r
        };
        var (ok, _) = SendIoctl(IOCTL_KF_WRITE_REGISTERS, StructToBytes(input), 0);
        return ok;
    }

    public bool SuspendThread(uint tid)
    {
        var input = new KF_THREAD_OP_IN { ThreadId = tid };
        var (ok, _) = SendIoctl(IOCTL_KF_SUSPEND_THREAD, StructToBytes(input), 0);
        return ok;
    }

    public bool ResumeThread(uint tid)
    {
        var input = new KF_THREAD_OP_IN { ThreadId = tid };
        var (ok, _) = SendIoctl(IOCTL_KF_RESUME_THREAD, StructToBytes(input), 0);
        return ok;
    }

    public bool InstallDebugHook(uint targetPid = 0)
    {
        var pidBytes = BitConverter.GetBytes(targetPid);
        var (ok, _) = SendIoctl(IOCTL_KF_INSTALL_HOOK, pidBytes, 0);
        return ok;
    }

    public bool RemoveDebugHook()
    {
        var (ok, _) = SendIoctl(IOCTL_KF_REMOVE_HOOK, null, 0);
        return ok;
    }

    /// <summary>
    /// Send RESET to driver: removes all BPs, hook, and cancels pending WAIT IRP.
    /// </summary>
    public bool ResetDriver()
    {
        var (ok, _) = SendIoctl(IOCTL_KF_RESET, null, 0);
        return ok;
    }

    public DebugEvent? WaitDebugEvent()
    {
        // Use dedicated debug channel to avoid blocking normal IOCTLs
        System.Diagnostics.Debug.WriteLine("[DriverComm] WaitDebugEvent: sending IOCTL...");
        var (ok, data) = SendIoctlDbg(IOCTL_KF_WAIT_DEBUG_EVENT, null, Marshal.SizeOf<KF_DEBUG_EVENT>());
        System.Diagnostics.Debug.WriteLine($"[DriverComm] WaitDebugEvent: ok={ok} data={(data != null ? $"{data.Length}b" : "null")}");
        if (!ok || data == null) return null;

        var ev = BytesToStruct<KF_DEBUG_EVENT>(data);
        return new DebugEvent
        {
            Type = (DebugEventType)ev.Type,
            ProcessId = ev.ProcessId,
            ThreadId = ev.ThreadId,
            Address = ev.Address,
            IsKernelMode = ev.PreviousMode == 0,
            Registers = new DebugEventRegisters
            {
                Rax = ev.Registers.Rax, Rbx = ev.Registers.Rbx,
                Rcx = ev.Registers.Rcx, Rdx = ev.Registers.Rdx,
                Rsi = ev.Registers.Rsi, Rdi = ev.Registers.Rdi,
                Rbp = ev.Registers.Rbp, Rsp = ev.Registers.Rsp,
                R8  = ev.Registers.R8,  R9  = ev.Registers.R9,
                R10 = ev.Registers.R10, R11 = ev.Registers.R11,
                R12 = ev.Registers.R12, R13 = ev.Registers.R13,
                R14 = ev.Registers.R14, R15 = ev.Registers.R15,
                Rip = ev.Registers.Rip, Rflags = ev.Registers.Rflags
            }
        };
    }

    // Continue modes (must match KF_CONTINUE_* in kf_shared.h)
    public const uint CONTINUE_RUN        = 0;
    public const uint CONTINUE_STEP_PAST  = 1;
    public const uint CONTINUE_STEP_INTO  = 2;

    public bool ContinueDebugEvent(uint mode = CONTINUE_RUN)
    {
        var input = BitConverter.GetBytes(mode);
        // Must use CMD channel — DBG channel's _dbgRemoteLock is held by WaitDebugEvent
        var (ok, _) = SendIoctl(IOCTL_KF_CONTINUE_DEBUG_EVENT, input, 0);
        return ok;
    }

    public (uint hookCalls, uint bpHits, uint bpNotFound, uint steps, byte kdEnabled, byte kdNotPresent,
            uint targetCalls, ulong lastTargetAddr, uint lastTargetCode, uint lastNonTargetPid,
            ulong kiDebugAddr, ulong kiDebugOrig, ulong kiDebugNow,
            ulong hookedFunc, ulong kdTrap)? GetHookStats()
    {
        var (ok, data) = SendIoctl(IOCTL_KF_GET_HOOK_STATS, null, Marshal.SizeOf<KF_HOOK_STATS_OUT>());
        if (!ok || data == null) return null;
        var s = BytesToStruct<KF_HOOK_STATS_OUT>(data);
        return (s.HookCallCount, s.BpHitCount, s.BpNotFoundCount, s.StepCount, s.KdDebuggerEnabled, s.KdDebuggerNotPresent,
                s.TargetCallCount, s.LastTargetAddr, s.LastTargetCode, s.LastNonTargetPid,
                s.KiDebugRoutineAddr, s.KiDebugRoutineOrig, s.KiDebugRoutineNow,
                s.HookedFuncAddr, s.KdTrapAddr);
    }

    public List<ProcessInfo> EnumProcesses()
    {
        const int maxProcesses = 1024;
        int entrySize = Marshal.SizeOf<KF_PROCESS_ENTRY>();
        int outSize = entrySize * maxProcesses;

        var (ok, data) = SendIoctl(IOCTL_KF_ENUM_PROCESSES, null, outSize);
        var processes = new List<ProcessInfo>();
        if (!ok || data == null) return processes;

        int count = data.Length / entrySize;
        for (int i = 0; i < count; i++)
        {
            var entry = BytesToStruct<KF_PROCESS_ENTRY>(data, i * entrySize);
            unsafe
            {
                processes.Add(new ProcessInfo
                {
                    ProcessId = entry.ProcessId,
                    SessionId = entry.SessionId,
                    Name = new string(entry.Name).TrimEnd('\0')
                });
            }
        }
        return processes;
    }

    // ── Remote file browser (relay pseudo-IOCTLs) ──

    public List<RemoteDriveInfo> ListRemoteDrives()
    {
        int entrySize = Marshal.SizeOf<KF_DRIVE_ENTRY>();
        int outSize = entrySize * 26; // max 26 drives
        var (ok, data) = SendIoctl(IOCTL_KF_LIST_DRIVES, null, outSize);
        var drives = new List<RemoteDriveInfo>();
        if (!ok || data == null) return drives;

        int count = data.Length / entrySize;
        for (int i = 0; i < count; i++)
        {
            var entry = BytesToStruct<KF_DRIVE_ENTRY>(data, i * entrySize);
            unsafe
            {
                drives.Add(new RemoteDriveInfo
                {
                    Letter = (char)entry.Letter,
                    DriveType = entry.DriveType,
                    Label = new string(entry.Label).TrimEnd('\0')
                });
            }
        }
        return drives;
    }

    public List<RemoteFileEntry> ListRemoteDirectory(string path)
    {
        // Input: null-terminated wide string
        byte[] input = System.Text.Encoding.Unicode.GetBytes(path + "\0");
        int entrySize = Marshal.SizeOf<KF_DIR_ENTRY>();
        int outSize = entrySize * 4096; // up to 4096 entries
        var (ok, data) = SendIoctl(IOCTL_KF_LIST_DIRECTORY, input, outSize);
        var entries = new List<RemoteFileEntry>();
        if (!ok || data == null) return entries;

        int count = data.Length / entrySize;
        for (int i = 0; i < count; i++)
        {
            var entry = BytesToStruct<KF_DIR_ENTRY>(data, i * entrySize);
            unsafe
            {
                entries.Add(new RemoteFileEntry
                {
                    Name = new string(entry.Name).TrimEnd('\0'),
                    IsDirectory = entry.IsDirectory != 0,
                    FileSize = entry.FileSize
                });
            }
        }
        return entries;
    }

    public (uint pid, uint tid, ulong imageBase)? CreateRemoteProcess(string exePath)
    {
        byte[] input = System.Text.Encoding.Unicode.GetBytes(exePath + "\0");
        var (ok, data) = SendIoctl(IOCTL_KF_CREATE_PROCESS, input, Marshal.SizeOf<KF_CREATE_PROCESS_OUT>());
        if (!ok || data == null) return null;

        var result = BytesToStruct<KF_CREATE_PROCESS_OUT>(data);
        return (result.ProcessId, result.ThreadId, result.ImageBase);
    }

    public (string serviceName, uint entryRva, byte originalByte)? LoadRemoteDriver(string sysPath)
    {
        byte[] input = System.Text.Encoding.Unicode.GetBytes(sysPath + "\0");
        var (ok, data) = SendIoctl(IOCTL_KF_LOAD_DRIVER, input, Marshal.SizeOf<KF_LOAD_DRIVER_OUT>());
        if (!ok || data == null) return null;

        // Parse service name from raw bytes (first 64 bytes, ANSI null-terminated)
        int nameLen = 0;
        while (nameLen < KF_MAX_SERVICE_NAME && nameLen < data.Length && data[nameLen] != 0)
            nameLen++;
        string name = System.Text.Encoding.ASCII.GetString(data, 0, nameLen);

        var result = BytesToStruct<KF_LOAD_DRIVER_OUT>(data);
        return (name, result.EntryPointRva, result.OriginalByte);
    }

    public bool StartRemoteDriver(string serviceName)
    {
        byte[] input = System.Text.Encoding.ASCII.GetBytes(serviceName + "\0");
        var (ok, _) = SendIoctl(IOCTL_KF_START_DRIVER, input, 0);
        return ok;
    }

    public bool UnloadRemoteDriver(string serviceName)
    {
        byte[] input = System.Text.Encoding.ASCII.GetBytes(serviceName + "\0");
        var (ok, _) = SendIoctl(IOCTL_KF_UNLOAD_DRIVER, input, 0);
        return ok;
    }

    #endregion

    public void Dispose()
    {
        if (!_disposed)
        {
            _handle?.Dispose();
            _netStream?.Dispose();
            _tcpClient?.Dispose();
            _dbgNetStream?.Dispose();
            _dbgTcpClient?.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
