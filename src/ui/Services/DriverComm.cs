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
    private static readonly uint IOCTL_KF_PROTECT_MEMORY  = CTL_CODE(DeviceType, 0x805, 0, 0);
    private static readonly uint IOCTL_KF_READ_REGISTERS  = CTL_CODE(DeviceType, 0x810, 0, 0);
    private static readonly uint IOCTL_KF_WRITE_REGISTERS = CTL_CODE(DeviceType, 0x811, 0, 0);
    private static readonly uint IOCTL_KF_WRITE_RIP       = CTL_CODE(DeviceType, 0x812, 0, 0);
    private static readonly uint IOCTL_KF_ENUM_MODULES    = CTL_CODE(DeviceType, 0x820, 0, 0);
    private static readonly uint IOCTL_KF_ENUM_KERNEL_MODULES = CTL_CODE(DeviceType, 0x821, 0, 0);
    private static readonly uint IOCTL_KF_ENUM_THREADS    = CTL_CODE(DeviceType, 0x830, 0, 0);
    private static readonly uint IOCTL_KF_SUSPEND_THREAD  = CTL_CODE(DeviceType, 0x831, 0, 0);
    private static readonly uint IOCTL_KF_RESUME_THREAD   = CTL_CODE(DeviceType, 0x832, 0, 0);
    private static readonly uint IOCTL_KF_ENUM_PROCESSES  = CTL_CODE(DeviceType, 0x835, 0, 0);
    private static readonly uint IOCTL_KF_GET_PEB_ADDRESS  = CTL_CODE(DeviceType, 0x836, 0, 0);
    private static readonly uint IOCTL_KF_CLEAR_DEBUG_PORT = CTL_CODE(DeviceType, 0x837, 0, 0);
    private static readonly uint IOCTL_KF_CLEAR_THREAD_HIDE = CTL_CODE(DeviceType, 0x838, 0, 0);
    private static readonly uint IOCTL_KF_INSTALL_NTQSI_HOOK = CTL_CODE(DeviceType, 0x850, 0, 0);
    private static readonly uint IOCTL_KF_REMOVE_NTQSI_HOOK = CTL_CODE(DeviceType, 0x851, 0, 0);
    private static readonly uint IOCTL_KF_PROBE_NTQSI = CTL_CODE(DeviceType, 0x852, 0, 0);
    private static readonly uint IOCTL_KF_SPOOF_SHARED_DATA = CTL_CODE(DeviceType, 0x853, 0, 0);
    private static readonly uint IOCTL_KF_ALLOC_MEMORY = CTL_CODE(DeviceType, 0x806, 0, 0);
    private static readonly uint IOCTL_KF_FREE_MEMORY = CTL_CODE(DeviceType, 0x807, 0, 0);
    private static readonly uint IOCTL_KF_INSTALL_HOOK    = CTL_CODE(DeviceType, 0x840, 0, 0);
    private static readonly uint IOCTL_KF_REMOVE_HOOK     = CTL_CODE(DeviceType, 0x841, 0, 0);
    private static readonly uint IOCTL_KF_WAIT_DEBUG_EVENT = CTL_CODE(DeviceType, 0x842, 0, 0);
    private static readonly uint IOCTL_KF_CONTINUE_DEBUG_EVENT = CTL_CODE(DeviceType, 0x843, 0, 0);
    private static readonly uint IOCTL_KF_GET_HOOK_STATS = CTL_CODE(DeviceType, 0x844, 0, 0);
    private static readonly uint IOCTL_KF_SET_TARGET_PID = CTL_CODE(DeviceType, 0x845, 0, 0);
    private static readonly uint IOCTL_KF_RESET          = CTL_CODE(DeviceType, 0x8FE, 0, 0);

    // Relay pseudo-IOCTLs (handled by relay, not driver)
    private static readonly uint IOCTL_KF_LIST_DRIVES     = CTL_CODE(DeviceType, 0x900, 0, 0);
    private static readonly uint IOCTL_KF_LIST_DIRECTORY   = CTL_CODE(DeviceType, 0x901, 0, 0);
    private static readonly uint IOCTL_KF_CREATE_PROCESS   = CTL_CODE(DeviceType, 0x902, 0, 0);
    private static readonly uint IOCTL_KF_LOAD_DRIVER      = CTL_CODE(DeviceType, 0x903, 0, 0);
    private static readonly uint IOCTL_KF_UNLOAD_DRIVER    = CTL_CODE(DeviceType, 0x904, 0, 0);
    private static readonly uint IOCTL_KF_START_DRIVER     = CTL_CODE(DeviceType, 0x905, 0, 0);
    private static readonly uint IOCTL_KF_READ_FILE       = CTL_CODE(DeviceType, 0x906, 0, 0);
    private static readonly uint IOCTL_KF_WRITE_FILE      = CTL_CODE(DeviceType, 0x907, 0, 0);
    private static readonly uint IOCTL_KF_DELETE_PATH     = CTL_CODE(DeviceType, 0x908, 0, 0);
    private static readonly uint IOCTL_KF_CREATE_DIR      = CTL_CODE(DeviceType, 0x909, 0, 0);
    private static readonly uint IOCTL_KF_RENAME_PATH     = CTL_CODE(DeviceType, 0x90A, 0, 0);
    private static readonly uint IOCTL_KF_STOP_SERVICE    = CTL_CODE(DeviceType, 0x90B, 0, 0);
    private static readonly uint IOCTL_KF_START_SERVICE   = CTL_CODE(DeviceType, 0x90C, 0, 0);
    private static readonly uint IOCTL_KF_QUERY_SERVICE_PID = CTL_CODE(DeviceType, 0x90D, 0, 0);

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
    private struct KF_WRITE_RIP_IN
    {
        public uint ThreadId;
        public uint Flags;      // bit 0: also write RSP
        public ulong NewRip;
        public ulong NewRsp;    // only written if Flags & 1
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
    private struct KF_GET_PEB_IN
    {
        public uint ProcessId;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct KF_GET_PEB_OUT
    {
        public ulong PebAddress;
        public ulong Peb32Address;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct KF_CLEAR_DEBUG_PORT_IN
    {
        public uint ProcessId;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct KF_CLEAR_THREAD_HIDE_IN
    {
        public uint ProcessId;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct KF_WRITE_REGISTERS_IN
    {
        public KF_THREAD_TARGET Target;
        public KF_REGISTERS Registers;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct KF_SERVICE_PID_OUT
    {
        public uint ProcessId;
        public uint ServiceState;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct KF_START_SERVICE_OUT
    {
        public uint ProcessId;
        public uint ServiceState;
        public uint EntryPointRva;
        public byte OriginalByte0;
        public byte OriginalByte1;
        public byte Reserved0;
        public byte Reserved1;
    }

    private const int KF_MAX_SERVICE_PATH = 520;

    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
    private unsafe struct KF_SERVICE_INFO_OUT
    {
        public uint ProcessId;
        public uint ServiceState;
        public fixed char BinaryPath[520];
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
        public uint Attributes;
        public ulong FileSize;
        public ulong LastWriteTime;
        public fixed char Name[KF_MAX_FILENAME];
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private unsafe struct KF_CREATE_PROCESS_OUT
    {
        public uint ProcessId;
        public uint ThreadId;
        public ulong ImageBase;
        public ulong EntryPointAddress;
        public fixed byte EntryOriginalBytes[2];
        public byte  EntryPatchBytes;   // 0 = not patched, 1 = CC (64-bit), 2 = EB FE (32-bit)
        public byte  EntryIs32Bit;
        public uint  Reserved;
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
        public uint ExceptionCode;
        public ulong FaultAddress;
        public uint AccessType;     // For AV: 0=read, 1=write, 8=execute
        public uint Reserved0;      // Alignment padding
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
        public uint TraceStepCount;
        public uint TraceActive;
        public uint ThreadBlocked;
        public uint ContinueMode;
        public uint DiagIrql;
        public uint DiagWaitResult;
        public uint DiagWaitCount;
        public uint DiagReportCount;
        public uint TraceAvCount;
        public uint TraceInt3Count;
        public uint TraceUnkCount;
        public uint TraceLastExcCode;
        public ulong TraceLastExcAddr;
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

    /// <summary>
    /// Set a short read timeout on the DBG channel so a blocked WaitDebugEvent
    /// will throw and release _dbgRemoteLock.  Call ResetDbgTimeout() after.
    /// </summary>
    public void InterruptDbgChannel()
    {
        try { if (_dbgNetStream != null) _dbgNetStream.ReadTimeout = 500; } catch { }
    }

    /// <summary>Restore infinite read timeout on DBG channel.</summary>
    public void ResetDbgTimeout()
    {
        try { if (_dbgNetStream != null) _dbgNetStream.ReadTimeout = Timeout.Infinite; } catch { }
    }

    /// <summary>
    /// Drain any stale responses from the DBG TCP receive buffer.
    /// Call after StopDebugListener + ResetDriver to flush orphaned WAIT responses.
    /// </summary>
    public void FlushDbgChannel()
    {
        if (_dbgNetStream == null) return;
        lock (_dbgRemoteLock)
        {
            try
            {
                _dbgNetStream.ReadTimeout = 200;
                var trash = new byte[4096];
                while (_dbgNetStream.DataAvailable)
                    _ = _dbgNetStream.Read(trash, 0, trash.Length);
                // Also try one timed read in case DataAvailable is stale
                try { _ = _dbgNetStream.Read(trash, 0, trash.Length); } catch { }
            }
            catch { /* timeout or error — expected */ }
            finally
            {
                _dbgNetStream.ReadTimeout = Timeout.Infinite;
            }
        }
    }

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
            _dbgNetStream.ReadTimeout = Timeout.Infinite; // WaitDebugEvent blocks until event

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

    public List<Register> ReadRegisters(uint pid, uint tid, bool is32Bit = false)
    {
        var input = new KF_THREAD_TARGET { ProcessId = pid, ThreadId = tid };
        // Retry policy: для SUSPENDED-потоков KTHREAD->TrapFrame регулярно вытесняется
        // на диск (kernel-stack page-out), и MmIsAddressValid в драйвере возвращает FALSE.
        // Первый вызов "трогает" PTE, и через ~50 мс kernel MM поднимает страницу обратно.
        // 5 попыток × 50 мс покрывают практически все случаи (зеркалит KfClient в CLI).
        byte[]? data = null;
        bool ok = false;
        int outSize = Marshal.SizeOf<KF_REGISTERS>();
        var inBytes = StructToBytes(input);
        for (int attempt = 0; attempt < 5; attempt++)
        {
            (ok, data) = SendIoctl(IOCTL_KF_READ_REGISTERS, inBytes, outSize);
            if (ok && data != null) break;
            System.Threading.Thread.Sleep(50);
        }
        if (!ok || data == null) return [];

        var r = BytesToStruct<KF_REGISTERS>(data);

        if (is32Bit)
        {
            // 32-bit: truncate to lower 32 bits, use x86 names, no R8-R15
            return
            [
                new() { Name = "EAX", Value = (uint)r.Rax, Is32Bit = true },
                new() { Name = "EBX", Value = (uint)r.Rbx, Is32Bit = true },
                new() { Name = "ECX", Value = (uint)r.Rcx, Is32Bit = true },
                new() { Name = "EDX", Value = (uint)r.Rdx, Is32Bit = true },
                new() { Name = "ESI", Value = (uint)r.Rsi, Is32Bit = true },
                new() { Name = "EDI", Value = (uint)r.Rdi, Is32Bit = true },
                new() { Name = "EBP", Value = (uint)r.Rbp, Is32Bit = true },
                new() { Name = "ESP", Value = (uint)r.Rsp, Is32Bit = true },
                new() { Name = "EIP", Value = (uint)r.Rip, Is32Bit = true },
                new() { Name = "EFLAGS", Value = (uint)r.Rflags, Is32Bit = true },
                ..Register.ExpandFlags(r.Rflags),
                new() { Name = "DR0", Value = (uint)r.Dr0, Is32Bit = true },
                new() { Name = "DR1", Value = (uint)r.Dr1, Is32Bit = true },
                new() { Name = "DR2", Value = (uint)r.Dr2, Is32Bit = true },
                new() { Name = "DR3", Value = (uint)r.Dr3, Is32Bit = true },
                new() { Name = "DR6", Value = (uint)r.Dr6, Is32Bit = true },
                new() { Name = "DR7", Value = (uint)r.Dr7, Is32Bit = true },
            ];
        }

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
        var inBytes = StructToBytes(input);
        // Та же page-out защита, что и в ReadRegisters: 5 попыток с 50 мс паузой.
        // Без этого Step Into на только что замороженном потоке часто фейлится с первого раза.
        for (int attempt = 0; attempt < 5; attempt++)
        {
            var (ok, _) = SendIoctl(IOCTL_KF_SINGLE_STEP, inBytes, 0);
            if (ok) return true;
            System.Threading.Thread.Sleep(50);
        }
        return false;
    }

    /// <summary>
    /// Read-modify-write: reads all registers, sets the named register to newValue, writes back.
    /// </summary>
    public bool WriteRegisterByName(uint pid, uint tid, string regName, ulong newValue)
    {
        // Read current state
        var readInput = new KF_THREAD_TARGET { ProcessId = pid, ThreadId = tid };
        var (rok, rdata) = SendIoctl(IOCTL_KF_READ_REGISTERS, StructToBytes(readInput), Marshal.SizeOf<KF_REGISTERS>());
        if (!rok || rdata == null) return false;

        var regs = BytesToStruct<KF_REGISTERS>(rdata);

        // Modify the requested register
        switch (regName)
        {
            case "RAX": case "EAX": regs.Rax = newValue; break;
            case "RBX": case "EBX": regs.Rbx = newValue; break;
            case "RCX": case "ECX": regs.Rcx = newValue; break;
            case "RDX": case "EDX": regs.Rdx = newValue; break;
            case "RSI": case "ESI": regs.Rsi = newValue; break;
            case "RDI": case "EDI": regs.Rdi = newValue; break;
            case "RBP": case "EBP": regs.Rbp = newValue; break;
            case "RSP": case "ESP": regs.Rsp = newValue; break;
            case "R8":  regs.R8  = newValue; break;
            case "R9":  regs.R9  = newValue; break;
            case "R10": regs.R10 = newValue; break;
            case "R11": regs.R11 = newValue; break;
            case "R12": regs.R12 = newValue; break;
            case "R13": regs.R13 = newValue; break;
            case "R14": regs.R14 = newValue; break;
            case "R15": regs.R15 = newValue; break;
            case "RIP": case "EIP": regs.Rip = newValue; break;
            case "RFLAGS": case "EFLAGS": regs.Rflags = newValue; break;
            case "DR0": regs.Dr0 = newValue; break;
            case "DR1": regs.Dr1 = newValue; break;
            case "DR2": regs.Dr2 = newValue; break;
            case "DR3": regs.Dr3 = newValue; break;
            case "DR6": regs.Dr6 = newValue; break;
            case "DR7": regs.Dr7 = newValue; break;
            default: return false;
        }

        // Write back
        var writeInput = new KF_WRITE_REGISTERS_IN
        {
            Target = new KF_THREAD_TARGET { ProcessId = pid, ThreadId = tid },
            Registers = regs
        };
        var (wok, _) = SendIoctl(IOCTL_KF_WRITE_REGISTERS, StructToBytes(writeInput), 0);
        return wok;
    }

    public bool WriteRip(uint pid, uint tid, ulong newRip)
    {
        // Use dedicated WRITE_RIP IOCTL that modifies ONLY RIP in trap frame.
        // The old approach (read all regs, modify RIP, write all back) zeroed
        // R12-R15 and could corrupt segment/debug registers, causing BSOD.
        var input = new KF_WRITE_RIP_IN { ThreadId = tid, Flags = 0, NewRip = newRip, NewRsp = 0 };
        var (ok, _) = SendIoctl(IOCTL_KF_WRITE_RIP, StructToBytes(input), 0);
        return ok;
    }

    public bool WriteRipAndRsp(uint tid, ulong newRip, ulong newRsp)
    {
        var input = new KF_WRITE_RIP_IN { ThreadId = tid, Flags = 1, NewRip = newRip, NewRsp = newRsp };
        var (ok, _) = SendIoctl(IOCTL_KF_WRITE_RIP, StructToBytes(input), 0);
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

    public bool SetTargetPid(uint pid)
    {
        var pidBytes = BitConverter.GetBytes(pid);
        var (ok, _) = SendIoctl(IOCTL_KF_SET_TARGET_PID, pidBytes, 0);
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
            ExceptionCode = ev.ExceptionCode,
            FaultAddress = ev.FaultAddress,
            AccessType = ev.AccessType,
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
    public const uint CONTINUE_HANDLED    = 3;
    public const uint CONTINUE_TRACE      = 4;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct KF_CONTINUE_IN
    {
        public uint Mode;
        public uint Flags;      // bit 0: set RIP, bit 1: set RSP
        public ulong NewRip;
        public ulong NewRsp;
        public ulong TraceRangeBase;  // For CONTINUE_TRACE
        public ulong TraceRangeEnd;   // For CONTINUE_TRACE
        public uint TraceMaxSteps;    // For CONTINUE_TRACE (0 = 500K default)
        public uint TraceReserved;
    }

    public bool ContinueDebugEvent(uint mode = CONTINUE_RUN, ulong newRip = 0, ulong newRsp = 0,
        ulong traceRangeBase = 0, ulong traceRangeEnd = 0, uint traceMaxSteps = 0)
    {
        uint flags = 0;
        if (newRip != 0) flags |= 1; // KF_CONT_SET_RIP
        if (newRsp != 0) flags |= 2; // KF_CONT_SET_RSP

        var input = new KF_CONTINUE_IN {
            Mode = mode, Flags = flags, NewRip = newRip, NewRsp = newRsp,
            TraceRangeBase = traceRangeBase, TraceRangeEnd = traceRangeEnd,
            TraceMaxSteps = traceMaxSteps
        };
        // Must use CMD channel — DBG channel's _dbgRemoteLock is held by WaitDebugEvent
        var (ok, _) = SendIoctl(IOCTL_KF_CONTINUE_DEBUG_EVENT, StructToBytes(input), 0);
        return ok;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct KF_PROTECT_MEMORY_IN
    {
        public uint ProcessId;
        public ulong Address;
        public uint Size;
        public uint NewProtection;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct KF_PROTECT_MEMORY_OUT
    {
        public uint OldProtection;
    }

    public (bool ok, uint oldProtection) ProtectMemory(uint pid, ulong address, uint size, uint newProtection)
    {
        var input = StructToBytes(new KF_PROTECT_MEMORY_IN
        {
            ProcessId = pid,
            Address = address,
            Size = size,
            NewProtection = newProtection
        });
        var (ok, data) = SendIoctl(IOCTL_KF_PROTECT_MEMORY, input, Marshal.SizeOf<KF_PROTECT_MEMORY_OUT>());
        if (!ok || data == null) return (false, 0);
        var output = BytesToStruct<KF_PROTECT_MEMORY_OUT>(data);
        return (true, output.OldProtection);
    }

    public (uint hookCalls, uint bpHits, uint bpNotFound, uint steps, byte kdEnabled, byte kdNotPresent,
            uint targetCalls, ulong lastTargetAddr, uint lastTargetCode, uint lastNonTargetPid,
            ulong kiDebugAddr, ulong kiDebugOrig, ulong kiDebugNow,
            ulong hookedFunc, ulong kdTrap,
            uint traceSteps, uint traceActive, uint threadBlocked, uint continueMode,
            uint diagIrql, uint diagWaitResult, uint diagWaitCount, uint diagReportCount,
            uint traceAvCount, uint traceInt3Count, uint traceUnkCount,
            uint traceLastExcCode, ulong traceLastExcAddr)? GetHookStats()
    {
        var (ok, data) = SendIoctl(IOCTL_KF_GET_HOOK_STATS, null, Marshal.SizeOf<KF_HOOK_STATS_OUT>());
        if (!ok || data == null) return null;
        var s = BytesToStruct<KF_HOOK_STATS_OUT>(data);
        return (s.HookCallCount, s.BpHitCount, s.BpNotFoundCount, s.StepCount, s.KdDebuggerEnabled, s.KdDebuggerNotPresent,
                s.TargetCallCount, s.LastTargetAddr, s.LastTargetCode, s.LastNonTargetPid,
                s.KiDebugRoutineAddr, s.KiDebugRoutineOrig, s.KiDebugRoutineNow,
                s.HookedFuncAddr, s.KdTrapAddr,
                s.TraceStepCount, s.TraceActive, s.ThreadBlocked, s.ContinueMode,
                s.DiagIrql, s.DiagWaitResult, s.DiagWaitCount, s.DiagReportCount,
                s.TraceAvCount, s.TraceInt3Count, s.TraceUnkCount,
                s.TraceLastExcCode, s.TraceLastExcAddr);
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

    public (ulong PebAddress, ulong Peb32Address) GetPebAddress(uint pid)
    {
        var input = new KF_GET_PEB_IN { ProcessId = pid };
        var (ok, data) = SendIoctl(IOCTL_KF_GET_PEB_ADDRESS, StructToBytes(input), Marshal.SizeOf<KF_GET_PEB_OUT>());
        if (!ok || data == null) return (0, 0);
        var result = BytesToStruct<KF_GET_PEB_OUT>(data, 0);
        return (result.PebAddress, result.Peb32Address);
    }

    public bool ClearDebugPort(uint pid)
    {
        var input = new KF_CLEAR_DEBUG_PORT_IN { ProcessId = pid };
        var (ok, _) = SendIoctl(IOCTL_KF_CLEAR_DEBUG_PORT, StructToBytes(input), 0);
        return ok;
    }

    public bool ClearThreadHide(uint pid)
    {
        var input = new KF_CLEAR_THREAD_HIDE_IN { ProcessId = pid };
        var (ok, _) = SendIoctl(IOCTL_KF_CLEAR_THREAD_HIDE, StructToBytes(input), 0);
        return ok;
    }

    public bool InstallNtQsiHook()
    {
        var (ok, _) = SendIoctl(IOCTL_KF_INSTALL_NTQSI_HOOK, [], 0);
        return ok;
    }

    public bool RemoveNtQsiHook()
    {
        var (ok, _) = SendIoctl(IOCTL_KF_REMOVE_NTQSI_HOOK, [], 0);
        return ok;
    }

    public bool SetSpoofSharedUserData(bool enable)
    {
        var (ok, _) = SendIoctl(IOCTL_KF_SPOOF_SHARED_DATA, [enable ? (byte)1 : (byte)0], 0);
        return ok;
    }

    public ulong AllocateMemory(uint pid, ulong size, uint protection = 0x40 /* PAGE_EXECUTE_READWRITE */)
    {
        byte[] input = new byte[16];
        BitConverter.GetBytes(pid).CopyTo(input, 0);
        BitConverter.GetBytes(size).CopyTo(input, 4);
        BitConverter.GetBytes(protection).CopyTo(input, 12);
        var (ok, data) = SendIoctl(IOCTL_KF_ALLOC_MEMORY, input, 8);
        if (!ok || data == null || data.Length < 8) return 0;
        return BitConverter.ToUInt64(data, 0);
    }

    public bool FreeMemory(uint pid, ulong address)
    {
        byte[] input = new byte[12];
        BitConverter.GetBytes(pid).CopyTo(input, 0);
        BitConverter.GetBytes(address).CopyTo(input, 4);
        var (ok, _) = SendIoctl(IOCTL_KF_FREE_MEMORY, input, 0);
        return ok;
    }

    public (bool ok, ulong address, byte[] bytes, uint status, uint decodedLen, uint numInsns, bool hasRipRelative) ProbeNtQsi()
    {
        int outSize = 8 + 32 + 4 + 4 + 4 + 1 + 3; // KF_PROBE_NTQSI_OUT = 56 bytes
        var (ok, data) = SendIoctl(IOCTL_KF_PROBE_NTQSI, [], outSize);
        if (!ok || data == null || data.Length < outSize)
            return (false, 0, [], 0, 0, 0, false);

        ulong address = BitConverter.ToUInt64(data, 0);
        byte[] bytes = new byte[32];
        Array.Copy(data, 8, bytes, 0, 32);
        uint st = BitConverter.ToUInt32(data, 40);
        uint decodedLen = BitConverter.ToUInt32(data, 44);
        uint numInsns = BitConverter.ToUInt32(data, 48);
        bool hasRipRel = data[52] != 0;

        return (true, address, bytes, st, decodedLen, numInsns, hasRipRel);
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
                    FileSize = entry.FileSize,
                    Attributes = entry.Attributes,
                    LastWriteTime = entry.LastWriteTime > 0
                        ? DateTime.FromFileTimeUtc((long)entry.LastWriteTime).ToLocalTime()
                        : DateTime.MinValue
                });
            }
        }
        return entries;
    }

    public (uint pid, uint tid, ulong imageBase)? CreateRemoteProcess(string exePath)
    {
        var full = CreateRemoteProcessEx(exePath);
        if (full == null) return null;
        return (full.Value.pid, full.Value.tid, full.Value.imageBase);
    }

    /// <summary>
    /// Full result of CreateRemoteProcess: also returns the relay-patched entry
    /// point address + the original bytes (1 for 64-bit INT3 patch, 2 for 32-bit
    /// EB FE spin-loop patch) so the UI can restore them after the target
    /// reaches entry.
    /// </summary>
    public unsafe (uint pid, uint tid, ulong imageBase, ulong entryAddr,
                   byte entryOrig0, byte entryOrig1, byte patchLen, bool is32Bit)?
        CreateRemoteProcessEx(string exePath)
    {
        byte[] input = System.Text.Encoding.Unicode.GetBytes(exePath + "\0");
        var (ok, data) = SendIoctl(IOCTL_KF_CREATE_PROCESS, input, Marshal.SizeOf<KF_CREATE_PROCESS_OUT>());
        if (!ok || data == null) return null;

        var result = BytesToStruct<KF_CREATE_PROCESS_OUT>(data);
        byte o0 = result.EntryOriginalBytes[0];
        byte o1 = result.EntryOriginalBytes[1];
        return (result.ProcessId, result.ThreadId, result.ImageBase,
                result.EntryPointAddress, o0, o1, result.EntryPatchBytes,
                result.EntryIs32Bit != 0);
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

    // ── File browser operations ──

    private const int FILE_CHUNK_SIZE = 2 * 1024 * 1024; // 2MB chunks

    /// <summary>Read a chunk of a remote file. Returns null on failure, empty array at EOF.</summary>
    public byte[]? ReadRemoteFileChunk(string path, ulong offset, uint length)
    {
        byte[] pathBytes = System.Text.Encoding.Unicode.GetBytes(path + "\0");
        byte[] input = new byte[pathBytes.Length + 12];
        Array.Copy(pathBytes, 0, input, 0, pathBytes.Length);
        BitConverter.GetBytes(offset).CopyTo(input, pathBytes.Length);
        BitConverter.GetBytes(length).CopyTo(input, pathBytes.Length + 8);

        var (ok, data) = SendIoctl(IOCTL_KF_READ_FILE, input, (int)length);
        if (!ok) return null;
        return data ?? [];
    }

    /// <summary>Write a chunk to a remote file. Returns bytes written, 0 on failure.</summary>
    public uint WriteRemoteFileChunk(string path, byte[] data, bool append)
    {
        byte[] pathBytes = System.Text.Encoding.Unicode.GetBytes(path + "\0");
        uint flags = append ? 1u : 0u;
        uint dataLen = (uint)data.Length;

        byte[] input = new byte[pathBytes.Length + 8 + data.Length];
        Array.Copy(pathBytes, 0, input, 0, pathBytes.Length);
        BitConverter.GetBytes(flags).CopyTo(input, pathBytes.Length);
        BitConverter.GetBytes(dataLen).CopyTo(input, pathBytes.Length + 4);
        Array.Copy(data, 0, input, pathBytes.Length + 8, data.Length);

        var (ok, result) = SendIoctl(IOCTL_KF_WRITE_FILE, input, 4);
        if (!ok || result == null || result.Length < 4) return 0;
        return BitConverter.ToUInt32(result, 0);
    }

    public bool DeleteRemotePath(string path)
    {
        byte[] input = System.Text.Encoding.Unicode.GetBytes(path + "\0");
        var (ok, _) = SendIoctl(IOCTL_KF_DELETE_PATH, input, 0);
        return ok;
    }

    public bool CreateRemoteDirectory(string path)
    {
        byte[] input = System.Text.Encoding.Unicode.GetBytes(path + "\0");
        var (ok, _) = SendIoctl(IOCTL_KF_CREATE_DIR, input, 0);
        return ok;
    }

    public bool RenameRemotePath(string oldPath, string newPath)
    {
        byte[] oldBytes = System.Text.Encoding.Unicode.GetBytes(oldPath + "\0");
        byte[] newBytes = System.Text.Encoding.Unicode.GetBytes(newPath + "\0");
        byte[] input = new byte[oldBytes.Length + newBytes.Length];
        Array.Copy(oldBytes, 0, input, 0, oldBytes.Length);
        Array.Copy(newBytes, 0, input, oldBytes.Length, newBytes.Length);

        var (ok, _) = SendIoctl(IOCTL_KF_RENAME_PATH, input, 0);
        return ok;
    }

    /// <summary>Download entire remote file to a local path. Reports progress via callback.</summary>
    public bool DownloadRemoteFile(string remotePath, string localPath, Action<long, long>? progress, CancellationToken ct)
    {
        using var fs = new System.IO.FileStream(localPath, System.IO.FileMode.Create, System.IO.FileAccess.Write);
        ulong offset = 0;
        while (!ct.IsCancellationRequested)
        {
            var chunk = ReadRemoteFileChunk(remotePath, offset, (uint)FILE_CHUNK_SIZE);
            if (chunk == null) return false; // error
            if (chunk.Length == 0) break; // EOF
            fs.Write(chunk, 0, chunk.Length);
            offset += (ulong)chunk.Length;
            progress?.Invoke((long)offset, -1);
            if (chunk.Length < FILE_CHUNK_SIZE) break; // last chunk
        }
        return !ct.IsCancellationRequested;
    }

    /// <summary>Upload a local file to the remote VM. Reports progress via callback.</summary>
    public bool UploadLocalFile(string localPath, string remotePath, Action<long, long>? progress, CancellationToken ct)
    {
        var fi = new System.IO.FileInfo(localPath);
        long totalSize = fi.Length;
        using var fs = new System.IO.FileStream(localPath, System.IO.FileMode.Open, System.IO.FileAccess.Read);
        byte[] buf = new byte[FILE_CHUNK_SIZE];
        long sent = 0;
        bool first = true;
        while (!ct.IsCancellationRequested)
        {
            int read = fs.Read(buf, 0, buf.Length);
            if (read == 0) break;
            byte[] chunk = read == buf.Length ? buf : buf[..read];
            uint written = WriteRemoteFileChunk(remotePath, chunk, !first);
            if (written == 0) return false;
            sent += written;
            first = false;
            progress?.Invoke(sent, totalSize);
        }
        return !ct.IsCancellationRequested;
    }

    // ── Service control (relay) ──

    public bool StopService(string serviceName)
    {
        byte[] input = System.Text.Encoding.ASCII.GetBytes(serviceName + "\0");
        var (ok, _) = SendIoctl(IOCTL_KF_STOP_SERVICE, input, 0);
        return ok;
    }

    public (bool ok, uint pid, uint entryRva, byte[] originalBytes) StartService(string serviceName)
    {
        byte[] input = System.Text.Encoding.ASCII.GetBytes(serviceName + "\0");
        var (ok, data) = SendIoctl(IOCTL_KF_START_SERVICE, input, Marshal.SizeOf<KF_START_SERVICE_OUT>());
        if (!ok || data == null) return (ok, 0, 0, Array.Empty<byte>());
        try
        {
            var result = BytesToStruct<KF_START_SERVICE_OUT>(data);
            return (true, result.ProcessId, result.EntryPointRva,
                    new byte[] { result.OriginalByte0, result.OriginalByte1 });
        }
        catch { return (ok, 0, 0, Array.Empty<byte>()); }
    }

    public (uint pid, uint state, string binaryPath) QueryServiceInfo(string serviceName)
    {
        byte[] input = System.Text.Encoding.ASCII.GetBytes(serviceName + "\0");
        var (ok, data) = SendIoctl(IOCTL_KF_QUERY_SERVICE_PID, input, Marshal.SizeOf<KF_SERVICE_INFO_OUT>());
        if (!ok || data == null) return (0, 0, "");
        var result = BytesToStruct<KF_SERVICE_INFO_OUT>(data);
        string path;
        unsafe { path = new string(result.BinaryPath).TrimEnd('\0'); }
        return (result.ProcessId, result.ServiceState, path);
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
