// Минимальный wrapper вокруг dbghelp.dll: загрузка PDB по списку модулей,
// резолв address → "module!symbol+0xN" и обратный поиск по имени.
//
// Использует обычный SymInitialize + SymLoadModuleExW + SymFromAddr / SymFromName.
// PDB ищется по стандартному _NT_SYMBOL_PATH или явному пути из conf.
using System.Runtime.InteropServices;

namespace KernelFlirt.Cli;

internal sealed class SymbolService : IDisposable
{
    private readonly IntPtr _hProc;   // Псевдо-handle, dbghelp требует уникальный
    private bool _initialized;
    private KfClient? _client;        // Используется для чтения PE-header'а target'а

    // Список загруженных модулей: имя без расширения (lowercase) → base, size
    private readonly Dictionary<string, (ulong Base, ulong Size)> _modules =
        new(StringComparer.OrdinalIgnoreCase);

    // Кэш PDB-путей по base-адресу (для возможного reuse)
    private readonly Dictionary<ulong, string> _pdbPaths = new();

    public SymbolService()
    {
        _hProc = (IntPtr)Environment.ProcessId;
    }

    /// <summary>Привязывает клиент драйвера для чтения PE из target'а.</summary>
    public void AttachClient(KfClient client) => _client = client;

    public string SymbolPath { get; private set; } = "";

    /// <summary>Инициализирует dbghelp. Возвращает true при успехе.</summary>
    public bool Initialize(string? extraSearchPath = null)
    {
        if (_initialized) return true;

        // По умолчанию: %_NT_SYMBOL_PATH% + extraSearchPath + msdl, дедуплицируем.
        var env = Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH") ?? "";
        var parts = new List<string>();
        foreach (var p in (env + ";" + (extraSearchPath ?? "") + ";"
                          + @"srv*C:\Symbols*https://msdl.microsoft.com/download/symbols")
                          .Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = p.Trim();
            if (t.Length == 0) continue;
            if (!parts.Contains(t, StringComparer.OrdinalIgnoreCase))
                parts.Add(t);
        }
        SymbolPath = string.Join(';', parts);

        // SYMOPT_UNDNAME — раздекорирует C++ имена, SYMOPT_AUTO_PUBLICS — public+private.
        // НЕ ставим SYMOPT_DEFERRED_LOADS: хотим чтобы LoadModule сразу пробовал
        // прочитать PDB и мы могли это диагностировать прямо там, а не в первом
        // случайном SymFromAddr/Name.
        SymSetOptions(SYMOPT_UNDNAME | SYMOPT_AUTO_PUBLICS);
        _initialized = SymInitializeW(_hProc, SymbolPath, false);
        return _initialized;
    }

    /// <summary>Загружает PDB для одного модуля target'а.
    /// Реальная цепочка (повторяет UI SymbolService):
    ///   1. Через KfClient читаем PE-header target'а по baseAddr.
    ///   2. Проходим Debug Directory, находим IMAGE_DEBUG_TYPE_CODEVIEW (=2),
    ///      из него — RSDS подпись: GUID + Age + имя PDB.
    ///   3. SymFindFileInPathW с GUID/Age скачивает (или находит локально) PDB
    ///      через symbol-server.
    ///   4. SymLoadModuleExW(imageName = путь к PDB) — dbghelp подцепляет
    ///      символы с гарантированно совпадающим GUID, без проблем с локальной
    ///      установкой Windows (target и debugger могут иметь разные версии).</summary>
    /// <summary>Если != null — сюда летят диагностические сообщения о загрузке PDB.</summary>
    public Action<string>? LogMessage { get; set; }

    public bool LoadModule(uint pid, string name, ulong baseAddr, ulong size)
    {
        if (!_initialized) return false;
        var key = StripExt(name);
        if (_modules.ContainsKey(key)) return true;

        string? pdbPath = null;
        var rsds = ReadRsdsFromTarget(pid, baseAddr);
        if (rsds == null)
        {
            LogMessage?.Invoke($"  {name}: PE/RSDS не вычитан (target memory недоступен)");
        }
        else
        {
            pdbPath = FindPdb(rsds);
            if (pdbPath == null)
                LogMessage?.Invoke($"  {name}: PDB не найден (pdb='{rsds.PdbName}', "
                    + $"guid={rsds.Guid:N}{rsds.Age:X})");
        }

        // Без PDB-пути нет смысла говорить dbghelp о модуле — символов всё равно
        // не будет. Регистрируем только если действительно нашли PDB.
        if (pdbPath == null) return false;

        ulong loaded = SymLoadModuleExW(_hProc, IntPtr.Zero, pdbPath, null,
                                        baseAddr, (uint)size, IntPtr.Zero, 0);
        if (loaded == 0)
        {
            int err = Marshal.GetLastWin32Error();
            // ERROR_SUCCESS (0) после ненулевого baseAddr означает «уже загружен на этом адресе»;
            // если err != 0 — реальная ошибка.
            if (err != 0)
            {
                LogMessage?.Invoke($"  {name}: SymLoadModuleExW FAIL err={err}");
                return false;
            }
        }

        // Жёсткая проверка: PDB действительно прочитан, не SymType==Deferred/None.
        if (!VerifyPdbLoaded(baseAddr, name))
            return false;

        _modules[key] = (baseAddr, size);
        _pdbPaths[baseAddr] = pdbPath;
        return true;
    }

    private bool VerifyPdbLoaded(ulong baseAddr, string name)
    {
        // SymGetModuleInfoW заполнит SymType: PDB=4, DIA=11; всё что < PDB значит
        // символы не прочитаны (Coff/Export/None/Deferred — для нас бесполезно).
        var mi = new IMAGEHLP_MODULEW64 { SizeOfStruct = (uint)Marshal.SizeOf<IMAGEHLP_MODULEW64>() };
        if (!SymGetModuleInfoW64(_hProc, baseAddr, ref mi))
        {
            LogMessage?.Invoke($"  {name}: SymGetModuleInfoW64 FAIL err={Marshal.GetLastWin32Error()}");
            return false;
        }
        if (mi.SymType is not (SymType.Pdb or SymType.Dia))
        {
            LogMessage?.Invoke($"  {name}: PDB не прочитан (SymType={mi.SymType})");
            return false;
        }
        return true;
    }

    // ── RSDS extraction ───────────────────────────────────────────────────

    private sealed class RsdsInfo
    {
        public Guid Guid;
        public uint Age;
        public string PdbName = "";
    }

    /// <summary>Читает PE-header target'а через KfClient, извлекает RSDS-сигнатуру
    /// из IMAGE_DEBUG_TYPE_CODEVIEW. pid=0 — kernel-режим (System PID=4).</summary>
    private RsdsInfo? ReadRsdsFromTarget(uint pid, ulong baseAddr)
    {
        if (_client == null) return null;
        if (pid == 0) pid = 4;

        // DOS-header
        var dos = _client.ReadMemory(pid, baseAddr, 64);
        if (dos == null || dos.Length < 64) return null;
        if (dos[0] != 0x4D || dos[1] != 0x5A) return null;   // "MZ"

        uint elfanew = BitConverter.ToUInt32(dos, 0x3C);
        if (elfanew > 0x1000) return null;

        // PE+OptionalHeader+DataDirectories
        var pe = _client.ReadMemory(pid, baseAddr + elfanew, 0x200);
        if (pe == null || pe.Length < 0x18) return null;
        if (pe[0] != 0x50 || pe[1] != 0x45) return null;   // "PE"

        ushort optSize = BitConverter.ToUInt16(pe, 20);
        if (optSize < 0x70) return null;

        ushort magic = BitConverter.ToUInt16(pe, 24);
        int ddOffset;
        if      (magic == 0x20B) ddOffset = 24 + 112;   // PE32+
        else if (magic == 0x10B) ddOffset = 24 + 96;    // PE32
        else return null;

        // DataDirectory[6] — IMAGE_DIRECTORY_ENTRY_DEBUG
        int debugDdOffset = ddOffset + 6 * 8;
        if (debugDdOffset + 8 > pe.Length) return null;

        uint debugRva  = BitConverter.ToUInt32(pe, debugDdOffset);
        uint debugSize = BitConverter.ToUInt32(pe, debugDdOffset + 4);
        if (debugRva == 0 || debugSize == 0 || debugSize > 0x2000) return null;

        var debugEntries = _client.ReadMemory(pid, baseAddr + debugRva, debugSize);
        if (debugEntries == null || debugEntries.Length < 28) return null;

        int n = debugEntries.Length / 28;
        for (int i = 0; i < n; i++)
        {
            int off = i * 28;
            uint type = BitConverter.ToUInt32(debugEntries, off + 12);
            if (type != 2) continue;   // IMAGE_DEBUG_TYPE_CODEVIEW

            uint dataSize = BitConverter.ToUInt32(debugEntries, off + 16);
            uint dataRva  = BitConverter.ToUInt32(debugEntries, off + 20);
            if (dataSize == 0 || dataSize > 0x10000 || dataRva == 0) continue;

            var raw = _client.ReadMemory(pid, baseAddr + dataRva, dataSize);
            if (raw == null || raw.Length < 24) continue;
            if (BitConverter.ToUInt32(raw, 0) != 0x53445352) continue;   // 'RSDS'

            var guid = new Guid(new ReadOnlySpan<byte>(raw, 4, 16));
            uint age = BitConverter.ToUInt32(raw, 20);
            int nameStart = 24;
            int nameEnd = Array.IndexOf<byte>(raw, 0, nameStart);
            if (nameEnd < 0) nameEnd = raw.Length;
            string pdbName = System.Text.Encoding.ASCII.GetString(raw, nameStart, nameEnd - nameStart);
            pdbName = Path.GetFileName(pdbName);

            return new RsdsInfo { Guid = guid, Age = age, PdbName = pdbName };
        }
        return null;
    }

    // ── PDB lookup ────────────────────────────────────────────────────────

    private string? FindPdb(RsdsInfo rsds)
    {
        // SymFindFileInPathW: dbghelp найдёт PDB по GUID+Age либо локально, либо
        // подтянет с symbol-сервера (msdl.microsoft.com через srv*-компонент пути).
        IntPtr guidPtr = Marshal.AllocHGlobal(16);
        IntPtr pathBuf = Marshal.AllocHGlobal(260 * 2);
        try
        {
            Marshal.Copy(rsds.Guid.ToByteArray(), 0, guidPtr, 16);
            unsafe { new Span<byte>((void*)pathBuf, 260 * 2).Clear(); }

            bool found = SymFindFileInPathW(
                _hProc,
                null,                  // search path: использовать установленный SymInitializeW
                rsds.PdbName,
                guidPtr,
                rsds.Age,
                0,                     // two — для PE-images, для PDB не используется
                SSRVOPT_GUIDPTR,
                pathBuf,
                IntPtr.Zero,           // FindFileInPathProc — не нужен
                IntPtr.Zero);

            if (found)
            {
                string? path = Marshal.PtrToStringUni(pathBuf);
                if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(guidPtr);
            Marshal.FreeHGlobal(pathBuf);
        }

        // Fallback: ищем PDB рядом с локальными директориями из symbol-path.
        return FindPdbInLocalPaths(rsds.PdbName);
    }

    private string? FindPdbInLocalPaths(string pdbName)
    {
        if (string.IsNullOrEmpty(pdbName)) return null;
        var fileName = Path.GetFileName(pdbName);

        foreach (var component in SymbolPath.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var c = component.Trim();
            if (c.StartsWith("srv*", StringComparison.OrdinalIgnoreCase) ||
                c.StartsWith("symsrv*", StringComparison.OrdinalIgnoreCase))
            {
                // У srv* кэш — вторая часть после '*'
                var parts = c.Split('*');
                if (parts.Length >= 2 && Directory.Exists(parts[1]))
                {
                    var cand = Path.Combine(parts[1], fileName);
                    if (File.Exists(cand)) return cand;
                }
                continue;
            }
            if (!Directory.Exists(c)) continue;
            var direct = Path.Combine(c, fileName);
            if (File.Exists(direct)) return direct;
            // Рядом + один уровень подпапок (типично C:\MyPDBs\<exe>\foo.pdb)
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(c))
                {
                    var p = Path.Combine(sub, fileName);
                    if (File.Exists(p)) return p;
                }
            }
            catch { }
        }
        return null;
    }

    /// <summary>Резолв адреса → "module!symbol+0xN", или null если символа нет
    /// (PDB не загружен или адрес не в известном модуле). НИКАКИХ module+offset
    /// fallback'ов — это создавало шум для адресов где реальный символ unknown.</summary>
    public string? Resolve(ulong addr)
    {
        if (!_initialized) return null;
        var buf = new byte[Marshal.SizeOf<SYMBOL_INFOW>() + (MaxNameLen - 1) * 2];
        unsafe
        {
            fixed (byte* p = buf)
            {
                var sym = (SYMBOL_INFOW*)p;
                sym->SizeOfStruct = (uint)Marshal.SizeOf<SYMBOL_INFOW>();
                sym->MaxNameLen = MaxNameLen;
                ulong disp = 0;
                if (!SymFromAddrW(_hProc, addr, ref disp, (IntPtr)p)) return null;

                string name = Marshal.PtrToStringUni(
                    (IntPtr)(p + Marshal.OffsetOf<SYMBOL_INFOW>(nameof(SYMBOL_INFOW.Name)).ToInt32())) ?? "";
                if (name.Length == 0) return null;
                string mod = FindModuleName(sym->ModBase) ?? "?";
                return disp == 0 ? $"{mod}!{name}" : $"{mod}!{name}+0x{disp:X}";
            }
        }
    }

    /// <summary>Резолв "module!Name" → адрес. Возвращает 0 если не найден.</summary>
    public ulong Lookup(string moduleAndName)
    {
        if (!_initialized) return 0;
        var buf = new byte[Marshal.SizeOf<SYMBOL_INFOW>() + (MaxNameLen - 1) * 2];
        unsafe
        {
            fixed (byte* p = buf)
            {
                var sym = (SYMBOL_INFOW*)p;
                sym->SizeOfStruct = (uint)Marshal.SizeOf<SYMBOL_INFOW>();
                sym->MaxNameLen = MaxNameLen;
                return SymFromNameW(_hProc, moduleAndName, (IntPtr)p) ? sym->Address : 0;
            }
        }
    }

    private string? FindModuleName(ulong baseAddr)
    {
        foreach (var kv in _modules)
            if (kv.Value.Base == baseAddr) return kv.Key;
        return null;
    }

    private static string StripExt(string name)
    {
        int dot = name.LastIndexOf('.');
        return (dot > 0 ? name[..dot] : name).ToLowerInvariant();
    }

    public void Dispose()
    {
        if (_initialized) { SymCleanup(_hProc); _initialized = false; }
    }

    // ── P/Invoke к dbghelp.dll ──────────────────────────────────────────

    private const int MaxNameLen = 1024;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct SYMBOL_INFOW
    {
        public uint   SizeOfStruct;
        public uint   TypeIndex;
        public ulong  Reserved0;
        public ulong  Reserved1;
        public uint   Index;
        public uint   Size;
        public ulong  ModBase;
        public uint   Flags;
        public ulong  Value;
        public ulong  Address;
        public uint   Register;
        public uint   Scope;
        public uint   Tag;
        public uint   NameLen;
        public uint   MaxNameLen;
        public fixed char Name[1];
    }

    private const uint SYMOPT_UNDNAME         = 0x00000002;
    private const uint SYMOPT_DEFERRED_LOADS  = 0x00000004;
    private const uint SYMOPT_AUTO_PUBLICS    = 0x00010000;

    internal enum SymType : uint
    {
        None = 0, Coff = 1, Cv = 2, Pdb = 3, Export = 4, Deferred = 5,
        Sym = 6, Dia = 7, Virtual = 8,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal unsafe struct IMAGEHLP_MODULEW64
    {
        public uint SizeOfStruct;
        public ulong BaseOfImage;
        public uint ImageSize;
        public uint TimeDateStamp;
        public uint CheckSum;
        public uint NumSyms;
        public SymType SymType;
        public fixed char ModuleName[32];
        public fixed char ImageName[256];
        public fixed char LoadedImageName[256];
        public fixed char LoadedPdbName[256];
        public uint CVSig;
        public fixed char CVData[260 * 3];
        public uint PdbSig;
        public Guid PdbSig70;
        public uint PdbAge;
        public int  PdbUnmatched;
        public int  DbgUnmatched;
        public int  LineNumbers;
        public int  GlobalSymbols;
        public int  TypeInfo;
        public int  SourceIndexed;
        public int  Publics;
        public uint MachineType;
        public uint Reserved;
    }

    [DllImport("dbghelp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SymGetModuleInfoW64(IntPtr hProcess, ulong dwAddr, ref IMAGEHLP_MODULEW64 ModuleInfo);

    [DllImport("dbghelp.dll")] private static extern uint SymSetOptions(uint options);
    [DllImport("dbghelp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SymInitializeW(IntPtr hProcess, string? userSearchPath, bool fInvadeProcess);
    [DllImport("dbghelp.dll")] private static extern bool SymCleanup(IntPtr hProcess);
    [DllImport("dbghelp.dll", CharSet = CharSet.Unicode)]
    private static extern ulong SymLoadModuleExW(
        IntPtr hProcess, IntPtr hFile, string ImageName, string? ModuleName,
        ulong BaseOfDll, uint SizeOfDll, IntPtr Data, uint Flags);
    [DllImport("dbghelp.dll", CharSet = CharSet.Unicode)]
    private static extern bool SymFromAddrW(IntPtr hProcess, ulong Address, ref ulong Displacement, IntPtr Symbol);
    [DllImport("dbghelp.dll", CharSet = CharSet.Unicode)]
    private static extern bool SymFromNameW(IntPtr hProcess, string Name, IntPtr Symbol);

    private const uint SSRVOPT_GUIDPTR = 0x00000008;

    [DllImport("dbghelp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SymFindFileInPathW(
        IntPtr hProcess,
        string? SearchPath,
        string FileName,
        IntPtr id,            // указатель на GUID при SSRVOPT_GUIDPTR
        uint   two,
        uint   three,
        uint   flags,
        IntPtr FilePath,
        IntPtr callerback,
        IntPtr context);
}
