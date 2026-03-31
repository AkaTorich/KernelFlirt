using System.Text;
using KernelFlirt.SDK;

namespace VulnHunterPlugin;

/// <summary>
/// Sets breakpoints on dangerous sink functions and analyzes arguments at runtime.
/// Detects potential buffer overflows by comparing copy size vs estimated buffer size.
/// </summary>
public class RuntimeMonitor
{
    private readonly IDebuggerApi _api;

    // address → sink definition
    private readonly Dictionary<ulong, SinkDef> _hooks = new();
    // breakpoint handles for cleanup
    private readonly List<uint> _bpHandles = [];
    // resolved addresses (avoid double-hooking)
    private readonly HashSet<ulong> _hookedAddresses = [];
    // cached modules (snapshot at Start, avoids calling GetModules per hit)
    private IReadOnlyList<PluginModuleInfo> _cachedModules = [];

    private int _hitIndex;

    public bool IsMonitoring { get; private set; }

    /// <summary>Fired when a sink function is called. UI subscribes to this.</summary>
    public event Action<RuntimeHit>? OnHit;

    public RuntimeMonitor(IDebuggerApi api)
    {
        _api = api;
    }

    public int Start()
    {
        if (IsMonitoring) return 0;
        if (!_api.IsConnected || _api.TargetPid == 0 || !_api.IsBreakState) return 0;

        uint pid = _api.TargetPid;
        int installed = 0;

        foreach (var sink in SinkDatabase.Sinks)
        {
            ulong addr = ResolveSink(sink);
            if (addr == 0) continue;
            if (_hookedAddresses.Contains(addr)) continue;

            var h = _api.Breakpoints.SetBreakpoint(pid, 0, addr, PluginBreakpointType.Software);
            if (!h.HasValue) continue;

            _hooks[addr] = sink;
            _hookedAddresses.Add(addr);
            _bpHandles.Add(h.Value);
            installed++;
        }

        _cachedModules = _api.Symbols.GetModules();
        IsMonitoring = true;
        return installed;
    }

    public void Stop()
    {
        if (!IsMonitoring) return;

        foreach (var h in _bpHandles)
            _api.Breakpoints.RemoveBreakpoint(h);

        _bpHandles.Clear();
        _hooks.Clear();
        _hookedAddresses.Clear();
        _cachedModules = [];
        IsMonitoring = false;
    }

    /// <summary>
    /// Handle a debug event. Returns true if this was one of our hooks (consumed).
    /// </summary>
    public bool HandleDebugEvent(PluginDebugEvent evt)
    {
        if (!_hooks.TryGetValue(evt.Address, out var sink))
            return false;

        var hit = AnalyzeHit(evt, sink);
        OnHit?.Invoke(hit);

        _api.Continue();
        return true;
    }

    public void ResetIndex() => _hitIndex = 0;

    private RuntimeHit AnalyzeHit(PluginDebugEvent evt, SinkDef sink)
    {
        uint pid = evt.ProcessId;
        uint tid = evt.ThreadId;

        var regs = _api.Memory.ReadRegisters(pid, tid);
        ulong rcx = GetReg(regs, "RCX");
        ulong rdx = GetReg(regs, "RDX");
        ulong r8  = GetReg(regs, "R8");
        ulong r9  = GetReg(regs, "R9");
        ulong rsp = GetReg(regs, "RSP");

        ulong[] args = [rcx, rdx, r8, r9];

        ulong destAddr = sink.DestParam >= 0 && sink.DestParam < 4 ? args[sink.DestParam] : 0;
        ulong srcAddr  = sink.SrcParam >= 0  && sink.SrcParam < 4  ? args[sink.SrcParam]  : 0;

        // Determine copy size
        ulong copySize = 0;
        if (sink.SizeParam >= 0 && sink.SizeParam < 4)
        {
            // Explicit size parameter (memcpy, strncpy, etc.)
            copySize = args[sink.SizeParam];
        }
        else if (srcAddr != 0 && sink.SizeParam == -1)
        {
            // Unbounded — measure source string length
            copySize = MeasureStringLength(pid, srcAddr);
        }

        // Estimate destination buffer size from stack context
        ulong bufferEstimate = EstimateBufferSize(pid, destAddr, rsp);

        // Suspicious if copy could overflow
        bool suspicious = false;
        if (copySize > 0 && bufferEstimate > 0 && copySize > bufferEstimate)
            suspicious = true;
        else if (sink.SizeParam == -1 && copySize > 256)
            suspicious = true; // Unbounded copy with large source
        else if (sink.Danger == DangerLevel.Critical && copySize > 64)
            suspicious = true; // Critical sink with non-trivial input

        // Build call chain from return address
        string callChain = BuildCallChain(pid, rsp);

        return new RuntimeHit
        {
            Index = ++_hitIndex,
            Time = DateTime.Now.ToString("HH:mm:ss.fff"),
            ThreadId = tid,
            Function = sink.Function,
            Danger = sink.Danger,
            DestAddress = destAddr,
            SrcAddress = srcAddr,
            CopySize = copySize,
            BufferEstimate = bufferEstimate,
            IsSuspicious = suspicious,
            CallChain = callChain
        };
    }

    /// <summary>
    /// Read memory at srcAddr until null byte, return length (cap at 4096).
    /// </summary>
    private ulong MeasureStringLength(uint pid, ulong srcAddr)
    {
        var data = _api.Memory.ReadMemory(pid, srcAddr, 1024);
        if (data == null) return 0;

        int idx = Array.IndexOf(data, (byte)0);
        return (ulong)(idx >= 0 ? idx : data.Length);
    }

    /// <summary>
    /// Heuristic: if dest is on the stack, estimate available space.
    /// If dest is a heap pointer, return 0 (unknown).
    /// </summary>
    private ulong EstimateBufferSize(uint pid, ulong destAddr, ulong rsp)
    {
        if (destAddr == 0 || rsp == 0) return 0;

        // Stack grows down on x64. If dest is above RSP and within a reasonable
        // stack frame range, it's a stack buffer.
        if (destAddr >= rsp && destAddr < rsp + 0x10000)
        {
            // Conservative estimate: space from dest to the assumed end of stack frame.
            // Read return address at [RSP] to find caller frame boundary.
            // The stack frame likely ends around RSP + 0x1000 (default stack page).
            ulong frameEnd = rsp + 0x1000;
            return frameEnd - destAddr;
        }

        return 0; // Heap or global — can't estimate without heap metadata
    }

    /// <summary>
    /// Build a simple call chain by reading return addresses from the stack.
    /// </summary>
    private string BuildCallChain(uint pid, ulong rsp)
    {
        if (rsp == 0) return "";

        var sb = new StringBuilder();
        var modules = _cachedModules;

        // Read up to 6 return address candidates from the stack
        for (int i = 0; i < 6; i++)
        {
            ulong stackAddr = rsp + (ulong)(i * 8);
            var data = _api.Memory.ReadMemory(pid, stackAddr, 8);
            if (data == null) break;

            ulong candidate = BitConverter.ToUInt64(data, 0);
            if (candidate == 0) continue;

            // Check if candidate falls within any loaded module's code
            foreach (var mod in modules)
            {
                if (candidate >= mod.BaseAddress && candidate < mod.BaseAddress + mod.Size)
                {
                    string? sym = _api.Symbols.ResolveAddress(candidate);
                    string label = sym ?? $"{mod.Name}+{candidate - mod.BaseAddress:X}";

                    if (sb.Length > 0) sb.Append(" <- ");
                    sb.Append(label);
                    break;
                }
            }
        }

        return sb.ToString();
    }

    private ulong ResolveSink(SinkDef sink)
    {
        // Try exact module!function
        ulong addr = _api.Symbols.ResolveNameToAddress($"{sink.Module}!{sink.Function}");
        if (addr != 0) return addr;

        // Try with .dll suffix
        addr = _api.Symbols.ResolveNameToAddress($"{sink.Module}.dll!{sink.Function}");
        if (addr != 0) return addr;

        return 0;
    }

    private static ulong GetReg(IReadOnlyList<PluginRegister> regs, string name)
    {
        foreach (var r in regs)
            if (r.Name == name) return r.Value;
        return 0;
    }
}
