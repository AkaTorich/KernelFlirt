// IOCTL коды и структуры — копия kf_shared.h для использования из C#.
// Layout сопадает с native структурой (LayoutKind.Sequential, Pack=8).
//
// Изменения здесь должны идти в ногу с include/kf_shared.h.
using System.Runtime.InteropServices;

namespace KernelFlirt.Cli;

internal static class Ioctl
{
    private const uint DeviceType = 0x00008000;

    private static uint CTL_CODE(uint deviceType, uint function, uint method, uint access)
        => (deviceType << 16) | (access << 14) | (function << 2) | method;

    // CMD-channel IOCTLs
    public static readonly uint PING                = CTL_CODE(DeviceType, 0x8FF, 0, 0);
    public static readonly uint READ_MEMORY         = CTL_CODE(DeviceType, 0x800, 0, 0);
    public static readonly uint WRITE_MEMORY        = CTL_CODE(DeviceType, 0x801, 0, 0);
    public static readonly uint SET_BREAKPOINT      = CTL_CODE(DeviceType, 0x802, 0, 0);
    public static readonly uint REMOVE_BREAKPOINT   = CTL_CODE(DeviceType, 0x803, 0, 0);
    public static readonly uint SINGLE_STEP         = CTL_CODE(DeviceType, 0x804, 0, 0);
    public static readonly uint PROTECT_MEMORY      = CTL_CODE(DeviceType, 0x805, 0, 0);
    public static readonly uint ALLOC_MEMORY        = CTL_CODE(DeviceType, 0x806, 0, 0);
    public static readonly uint FREE_MEMORY         = CTL_CODE(DeviceType, 0x807, 0, 0);
    public static readonly uint READ_REGISTERS      = CTL_CODE(DeviceType, 0x810, 0, 0);
    public static readonly uint WRITE_REGISTERS     = CTL_CODE(DeviceType, 0x811, 0, 0);
    public static readonly uint WRITE_RIP           = CTL_CODE(DeviceType, 0x812, 0, 0);
    public static readonly uint ENUM_MODULES        = CTL_CODE(DeviceType, 0x820, 0, 0);
    public static readonly uint ENUM_KERNEL_MODULES = CTL_CODE(DeviceType, 0x821, 0, 0);
    public static readonly uint ENUM_THREADS        = CTL_CODE(DeviceType, 0x830, 0, 0);
    public static readonly uint SUSPEND_THREAD      = CTL_CODE(DeviceType, 0x831, 0, 0);
    public static readonly uint RESUME_THREAD       = CTL_CODE(DeviceType, 0x832, 0, 0);
    public static readonly uint ENUM_PROCESSES      = CTL_CODE(DeviceType, 0x835, 0, 0);
    public static readonly uint GET_PEB_ADDRESS     = CTL_CODE(DeviceType, 0x836, 0, 0);
    public static readonly uint CLEAR_DEBUG_PORT    = CTL_CODE(DeviceType, 0x837, 0, 0);
    public static readonly uint CLEAR_THREAD_HIDE   = CTL_CODE(DeviceType, 0x838, 0, 0);
    public static readonly uint INSTALL_NTQSI_HOOK  = CTL_CODE(DeviceType, 0x850, 0, 0);
    public static readonly uint REMOVE_NTQSI_HOOK   = CTL_CODE(DeviceType, 0x851, 0, 0);
    public static readonly uint SPOOF_SHARED_DATA   = CTL_CODE(DeviceType, 0x853, 0, 0);
    public static readonly uint INSTALL_HOOK        = CTL_CODE(DeviceType, 0x840, 0, 0);
    public static readonly uint REMOVE_HOOK         = CTL_CODE(DeviceType, 0x841, 0, 0);
    public static readonly uint WAIT_DEBUG_EVENT    = CTL_CODE(DeviceType, 0x842, 0, 0);
    public static readonly uint CONTINUE_DEBUG_EVENT = CTL_CODE(DeviceType, 0x843, 0, 0);
    public static readonly uint GET_HOOK_STATS      = CTL_CODE(DeviceType, 0x844, 0, 0);
    public static readonly uint SET_TARGET_PID      = CTL_CODE(DeviceType, 0x845, 0, 0);
    public static readonly uint RESET               = CTL_CODE(DeviceType, 0x8FE, 0, 0);

    // Relay-only pseudo-IOCTLs (обрабатывает KfRelay.exe, не сам драйвер)
    public static readonly uint CREATE_PROCESS      = CTL_CODE(DeviceType, 0x902, 0, 0);
}

// KF_CONTINUE_* values из kf_shared.h
internal static class ContinueMode
{
    public const uint Run        = 0;
    public const uint StepPast   = 1;
    public const uint StepInto   = 2;
    public const uint Handled    = 3;
    public const uint Trace      = 4;
}

// KF_DBG_* — типы debug-событий
internal static class DbgEventType
{
    public const uint Breakpoint      = 1;
    public const uint SingleStep      = 2;
    public const uint HwBreakpoint    = 3;
    public const uint HwWatchpoint    = 4;
    public const uint MemoryBp        = 5;
    public const uint AccessViolation = 6;
}

[StructLayout(LayoutKind.Sequential)]
internal struct KF_PING_OUT
{
    public uint Version;
    public uint Magic;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct KF_READ_MEMORY_IN
{
    public uint   ProcessId;
    public ulong  Address;
    public uint   Size;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct KF_WRITE_MEMORY_IN
{
    public uint   ProcessId;
    public ulong  Address;
    public uint   Size;
    // далее идут данные input->Size байт
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct KF_SET_BP_IN
{
    public uint   ProcessId;
    public uint   ThreadId;
    public ulong  Address;
    public uint   Type;       // KF_BP_SOFTWARE = 0, HARDWARE=1, HW_WRITE=2, HW_RW=3, MEMORY=4
    public uint   Reserved;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct KF_REMOVE_BP_IN
{
    public uint Handle;
}

[StructLayout(LayoutKind.Sequential)]
internal struct KF_THREAD_TARGET
{
    public uint ProcessId;
    public uint ThreadId;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct KF_REGISTERS
{
    public ulong Rax, Rbx, Rcx, Rdx;
    public ulong Rsi, Rdi, Rbp, Rsp;
    public ulong R8,  R9,  R10, R11;
    public ulong R12, R13, R14, R15;
    public ulong Rip;
    public ulong Rflags;
    public ushort Cs, Ds, Es, Fs, Gs, Ss;
    public ulong Dr0, Dr1, Dr2, Dr3, Dr6, Dr7;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct KF_WRITE_REGISTERS_IN
{
    public KF_THREAD_TARGET Target;
    public KF_REGISTERS     Registers;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct KF_WRITE_RIP_IN
{
    public uint   ThreadId;
    public uint   Flags;     // bit 0: also write RSP
    public ulong  NewRip;
    public ulong  NewRsp;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct KF_CONTINUE_IN
{
    public uint   Mode;
    public uint   Flags;
    public ulong  NewRip;
    public ulong  NewRsp;
    public ulong  TraceRangeBase;
    public ulong  TraceRangeEnd;
    public uint   TraceMaxSteps;
    public uint   TraceReserved;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct KF_DEBUG_EVENT
{
    public uint           Type;
    public uint           ProcessId;
    public uint           ThreadId;
    public ulong          Address;
    public uint           PreviousMode;
    public uint           ExceptionCode;
    public ulong          FaultAddress;
    public uint           AccessType;
    public uint           Reserved0;
    public KF_REGISTERS   Registers;
}

// Native layout:
//   ULONG64 BaseAddress;    offset 0  (8)
//   ULONG   Size;           offset 8  (4)
//   WCHAR   Name[256];      offset 12 (512) — native использует Pack(natural),
//                                              16-byte struct alignment не требует
// Total = 12 + 512 = 524 байта.
[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 524)]
internal unsafe struct KF_MODULE_ENTRY
{
    public ulong BaseAddress;
    public uint  Size;
    // Name[256] начинается с offset 12.

    public const int NameOffset   = 12;
    public const int NameMaxChars = 256;
}

// Native layout (kf_shared.h):
//   ULONG   ProcessId;      offset 0  (4)
//   ULONG   SessionId;      offset 4  (4)
//   ULONG64 PeakVirtualSize; offset 8  (8) — естественное выравнивание ULONG64
//   WCHAR   Name[260];      offset 16 (520)
// Total = 536 bytes.
//
// Имя читаем НЕ через `fixed char[]` (там были проблемы с layout у MSVC clang
// сборки), а вручную через смещение: всё равно offset известен на уровне ABI.
[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 536)]
internal unsafe struct KF_PROCESS_ENTRY
{
    public uint   ProcessId;
    public uint   SessionId;
    public ulong  PeakVirtualSize;
    // Name[260] — 520 байт сразу после, читаем отдельно в StructUtil.

    public const int NameOffset    = 16;
    public const int NameMaxChars  = 260;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct KF_THREAD_ENTRY
{
    public uint   ThreadId;
    public ulong  StartAddress;
    public uint   State;
    public uint   Priority;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct KF_THREAD_OP_IN
{
    public uint ThreadId;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct KF_GET_PEB_IN
{
    public uint ProcessId;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct KF_GET_PEB_OUT
{
    public ulong PebAddress;    // 64-bit PEB
    public ulong Peb32Address;  // WoW64 PEB (0 если native x64)
}

// IOCTL_KF_CREATE_PROCESS output (CMD via relay). Релей патчит entry на EB FE
// и возвращает оригинальные 1-2 байта, которые мы восстановим после остановки
// потока на entry-point.
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal unsafe struct KF_CREATE_PROCESS_OUT
{
    public uint    ProcessId;
    public uint    ThreadId;
    public ulong   ImageBase;
    public ulong   EntryPointAddress;
    public byte    EntryOrigByte0;
    public byte    EntryOrigByte1;
    public byte    EntryPatchBytes;
    public byte    EntryIs32Bit;
    public fixed byte Reserved[4];
}

internal static class StructUtil
{
    public static byte[] ToBytes<T>(T s) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        var bytes = new byte[size];
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(s!, ptr, false);
            Marshal.Copy(ptr, bytes, 0, size);
        }
        finally { Marshal.FreeHGlobal(ptr); }
        return bytes;
    }

    public static T FromBytes<T>(byte[] data, int offset = 0) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        if (data.Length - offset < size)
            throw new InvalidDataException($"buffer too small: need {size}, have {data.Length - offset}");
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(data, offset, ptr, size);
            return Marshal.PtrToStructure<T>(ptr);
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }
}
