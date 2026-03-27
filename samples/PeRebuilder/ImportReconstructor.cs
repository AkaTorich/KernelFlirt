using System.Text;
using KernelFlirt.SDK;

namespace PeRebuilder;

/// <summary>Result of IAT scanning and import reconstruction.</summary>
public sealed class ReconstructedImport
{
    public string DllName  { get; set; } = "";
    public string FuncName { get; set; } = "";
    public ushort Ordinal  { get; set; }
    public bool   ByOrdinal { get; set; }
    public ulong  IatAddress { get; set; }
    public ulong  ResolvedAddress { get; set; }
    public bool   Valid { get; set; }
}

/// <summary>
/// Scans IAT in process memory and resolves all entries to DLL+function names.
/// </summary>
public sealed class ImportReconstructor
{
    private readonly IDebuggerApi _api;
    private readonly ExportResolver _resolver;
    private readonly bool _is64;
    private readonly int _ptrSize;

    public ulong IatBase { get; set; }
    public int   IatSize { get; set; }  // in bytes
    public List<ReconstructedImport> Imports { get; } = new();

    public ImportReconstructor(IDebuggerApi api, ExportResolver resolver)
    {
        _api      = api;
        _resolver = resolver;
        _is64     = !api.Is32Bit;
        _ptrSize  = _is64 ? 8 : 4;
    }

    /// <summary>
    /// Auto-detect IAT location by disassembling from OEP and looking for
    /// indirect calls/jumps (call [mem], jmp [mem]).
    /// </summary>
    public bool AutoDetectIat(ulong oep)
    {
        try
        {
            byte[]? code = _api.Memory.ReadMemory(_api.TargetPid, oep, 4096u);
            if (code == null || code.Length < 16) return false;

            var decoder = Iced.Intel.Decoder.Create(_is64 ? 64 : 32, code);
            decoder.IP = oep;
            ulong endIp = oep + (ulong)code.Length;

            ulong firstIatRef = 0;
            int maxInstr = 500;

            while (decoder.IP < endIp && maxInstr-- > 0)
            {
                var instr = decoder.Decode();
                if (instr.IsInvalid) break;

                // Look for CALL [mem] or JMP [mem]
                if (instr.Mnemonic == Iced.Intel.Mnemonic.Call ||
                    instr.Mnemonic == Iced.Intel.Mnemonic.Jmp)
                {
                    if (instr.Op0Kind == Iced.Intel.OpKind.Memory)
                    {
                        ulong memAddr = instr.MemoryDisplacement64;
                        if (_is64 && instr.IsIPRelativeMemoryOperand)
                            memAddr = instr.IPRelativeMemoryAddress;

                        // Read the pointer — should point to a loaded module
                        byte[]? ptrBuf = _api.Memory.ReadMemory(_api.TargetPid, memAddr, (uint)_ptrSize);
                        if (ptrBuf != null && ptrBuf.Length == _ptrSize)
                        {
                            ulong target = _is64
                                ? BitConverter.ToUInt64(ptrBuf, 0)
                                : BitConverter.ToUInt32(ptrBuf, 0);

                            if (_resolver.IsApiAddress(target))
                            {
                                firstIatRef = memAddr;
                                break;
                            }
                        }
                    }
                }

                // Follow CALL rel32 to look deeper (e.g. into __security_init_cookie)
                if (instr.Mnemonic == Iced.Intel.Mnemonic.Call &&
                    instr.Op0Kind == Iced.Intel.OpKind.NearBranch64)
                {
                    ulong callTarget = instr.NearBranch64;
                    byte[]? subCode = _api.Memory.ReadMemory(_api.TargetPid, callTarget, 2048u);
                    if (subCode != null)
                    {
                        var subDecoder = Iced.Intel.Decoder.Create(_is64 ? 64 : 32, subCode);
                        subDecoder.IP = callTarget;
                        ulong subEndIp = callTarget + (ulong)subCode.Length;
                        int subMax = 200;
                        while (subDecoder.IP < subEndIp && subMax-- > 0)
                        {
                            var sub = subDecoder.Decode();
                            if (sub.IsInvalid) break;
                            if ((sub.Mnemonic == Iced.Intel.Mnemonic.Call ||
                                 sub.Mnemonic == Iced.Intel.Mnemonic.Jmp) &&
                                sub.Op0Kind == Iced.Intel.OpKind.Memory)
                            {
                                ulong ma = sub.MemoryDisplacement64;
                                if (_is64 && sub.IsIPRelativeMemoryOperand)
                                    ma = sub.IPRelativeMemoryAddress;

                                byte[]? pb = _api.Memory.ReadMemory(_api.TargetPid, ma, (uint)_ptrSize);
                                if (pb != null && pb.Length == _ptrSize)
                                {
                                    ulong t = _is64 ? BitConverter.ToUInt64(pb, 0) : BitConverter.ToUInt32(pb, 0);
                                    if (_resolver.IsApiAddress(t))
                                    {
                                        firstIatRef = ma;
                                        break;
                                    }
                                }
                            }
                        }
                        if (firstIatRef != 0) break;
                    }
                }
            }

            if (firstIatRef == 0) return false;

            // Walk backward to find IAT start
            ulong iatStart = firstIatRef;
            int nullRun = 0;
            while (nullRun < 16)
            {
                iatStart -= (ulong)_ptrSize;
                byte[]? val = _api.Memory.ReadMemory(_api.TargetPid, iatStart, (uint)_ptrSize);
                if (val == null || val.Length < _ptrSize) break;
                ulong v = _is64 ? BitConverter.ToUInt64(val, 0) : BitConverter.ToUInt32(val, 0);
                if (v == 0)
                    nullRun++;
                else if (_resolver.IsApiAddress(v))
                    nullRun = 0;
                else
                    break;
            }
            iatStart += (ulong)(_ptrSize * (nullRun + 1));

            // Walk forward to find IAT end
            ulong iatEnd = firstIatRef;
            nullRun = 0;
            while (nullRun < 64)
            {
                byte[]? val = _api.Memory.ReadMemory(_api.TargetPid, iatEnd, (uint)_ptrSize);
                if (val == null || val.Length < _ptrSize) break;
                ulong v = _is64 ? BitConverter.ToUInt64(val, 0) : BitConverter.ToUInt32(val, 0);
                if (v == 0)
                    nullRun++;
                else if (_resolver.IsApiAddress(v))
                    nullRun = 0;
                else
                    nullRun++;
                iatEnd += (ulong)_ptrSize;
            }

            IatBase = iatStart;
            IatSize = (int)(iatEnd - iatStart);
            return IatSize > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Read IAT and resolve all entries.</summary>
    public int ScanAndResolve()
    {
        Imports.Clear();
        if (IatBase == 0 || IatSize == 0) return 0;

        int count = IatSize / _ptrSize;
        byte[] iatData = ReadMemoryChunked(IatBase, IatSize);
        if (iatData.Length < IatSize) return 0;

        int resolved = 0;
        for (int i = 0; i < count; i++)
        {
            ulong addr = _is64
                ? BitConverter.ToUInt64(iatData, i * _ptrSize)
                : BitConverter.ToUInt32(iatData, i * _ptrSize);

            ulong iatAddr = IatBase + (ulong)(i * _ptrSize);

            if (addr == 0)
            {
                Imports.Add(new ReconstructedImport
                {
                    IatAddress = iatAddr,
                    ResolvedAddress = 0,
                    Valid = false,
                    FuncName = "(null separator)"
                });
                continue;
            }

            var result = _resolver.Resolve(addr);
            if (result != null)
            {
                var (dll, func) = result.Value;
                bool byOrd = func.StartsWith('#');
                Imports.Add(new ReconstructedImport
                {
                    DllName = dll,
                    FuncName = func,
                    ByOrdinal = byOrd,
                    Ordinal = byOrd ? ushort.Parse(func[1..]) : (ushort)0,
                    IatAddress = iatAddr,
                    ResolvedAddress = addr,
                    Valid = true
                });
                resolved++;
            }
            else
            {
                Imports.Add(new ReconstructedImport
                {
                    IatAddress = iatAddr,
                    ResolvedAddress = addr,
                    Valid = false,
                    FuncName = $"??? ({addr:X}"
                });
            }
        }

        return resolved;
    }

    /// <summary>Group imports by DLL (ordered by IAT position).</summary>
    public List<(string dll, List<ReconstructedImport> funcs)> GroupByDll()
    {
        var result = new List<(string dll, List<ReconstructedImport> funcs)>();
        string? currentDll = null;
        List<ReconstructedImport>? currentList = null;

        foreach (var imp in Imports)
        {
            if (!imp.Valid && imp.ResolvedAddress == 0)
            {
                // Null separator — next group
                currentDll = null;
                continue;
            }

            if (!imp.Valid) continue;

            if (currentDll == null || !imp.DllName.Equals(currentDll, StringComparison.OrdinalIgnoreCase))
            {
                currentDll = imp.DllName;
                currentList = new List<ReconstructedImport>();
                result.Add((currentDll, currentList));
            }

            currentList!.Add(imp);
        }

        return result;
    }

    private byte[] ReadMemoryChunked(ulong address, int totalSize)
    {
        const int chunkSize = 0x100000; // 1MB
        byte[] result = new byte[totalSize];
        int offset = 0;

        while (offset < totalSize)
        {
            int toRead = Math.Min(chunkSize, totalSize - offset);
            try
            {
                byte[]? chunk = _api.Memory.ReadMemory(_api.TargetPid, address + (ulong)offset, (uint)toRead);
                if (chunk == null || chunk.Length == 0) break;
                Array.Copy(chunk, 0, result, offset, chunk.Length);
                offset += chunk.Length;
            }
            catch { break; }
        }

        return result;
    }
}
