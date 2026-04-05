using System.IO;
using System.Text;
using KernelFlirt.SDK;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace ScriptingPlugin;

/// <summary>
/// Roslyn-based C# script execution engine.
/// Uses PluginScriptHost from SDK as globals type (shared assembly — no ALC conflicts).
/// Helper shortcuts are injected as a script preamble on first run.
/// </summary>
public sealed class ScriptEngine
{
    private readonly PluginScriptHost _host;
    private readonly ScriptOptions _options;
    private ScriptState<object>? _state;

    // Preamble that defines helper functions as local lambdas/variables.
    // Runs once on first execution, variables persist via REPL state.
    private const string Preamble = @"
// ── Helper shortcuts ──────────────────────────────────────────────
Func<ulong, uint, byte[]> ReadMem = (addr, size) => api.Memory.ReadMemory(api.TargetPid, addr, size);
Func<ulong, byte[], bool> WriteMem = (addr, data) => api.Memory.WriteMemory(api.TargetPid, addr, data);

Func<ulong, int, string> ReadString = (addr, maxLen) => {
    var _d = api.Memory.ReadMemory(api.TargetPid, addr, (uint)maxLen);
    if (_d == null) return ""<read failed>"";
    int _e = Array.IndexOf(_d, (byte)0);
    if (_e < 0) _e = _d.Length;
    return System.Text.Encoding.ASCII.GetString(_d, 0, _e);
};

Func<ulong, int, string> ReadWString = (addr, maxLen) => {
    var _d = api.Memory.ReadMemory(api.TargetPid, addr, (uint)(maxLen * 2));
    if (_d == null) return ""<read failed>"";
    int _e = 0;
    for (int _i = 0; _i < _d.Length - 1; _i += 2) {
        if (_d[_i] == 0 && _d[_i+1] == 0) break;
        _e = _i + 2;
    }
    return System.Text.Encoding.Unicode.GetString(_d, 0, _e);
};

Func<ulong, ulong> ReadPtr = (addr) => {
    int _sz = api.Is32Bit ? 4 : 8;
    var _d = api.Memory.ReadMemory(api.TargetPid, addr, (uint)_sz);
    if (_d == null) return 0UL;
    return _sz == 8 ? BitConverter.ToUInt64(_d) : BitConverter.ToUInt32(_d);
};

Func<ulong, uint> ReadU32 = (addr) => {
    var _d = api.Memory.ReadMemory(api.TargetPid, addr, 4);
    return _d != null ? BitConverter.ToUInt32(_d) : 0U;
};

Func<ulong, ulong> ReadU64 = (addr) => {
    var _d = api.Memory.ReadMemory(api.TargetPid, addr, 8);
    return _d != null ? BitConverter.ToUInt64(_d) : 0UL;
};

Func<string, ulong> Reg = (name) => {
    var _regs = api.Memory.ReadRegisters(api.TargetPid, api.SelectedThreadId);
    var _r = _regs.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    return _r?.Value ?? 0UL;
};

Func<ulong, string> Sym = (addr) => api.Symbols.ResolveAddress(addr);
Func<string, ulong> Addr = (name) => api.Symbols.ResolveNameToAddress(name);
";

    public ScriptEngine(IDebuggerApi api, Action<string> printCallback)
    {
        _host = new PluginScriptHost
        {
            api = api,
            print = printCallback
        };

        _options = ScriptOptions.Default
            .AddReferences(
                typeof(object).Assembly,
                typeof(Console).Assembly,
                typeof(Enumerable).Assembly,
                typeof(List<>).Assembly,
                typeof(BitConverter).Assembly,
                typeof(Encoding).Assembly,
                typeof(File).Assembly,
                typeof(IDebuggerApi).Assembly   // SDK — includes PluginScriptHost
            )
            .AddImports(
                "System",
                "System.Collections.Generic",
                "System.IO",
                "System.Linq",
                "System.Text",
                "System.Threading.Tasks",
                "KernelFlirt.SDK"
            );
    }

    public async Task<string> ExecuteAsync(string code, CancellationToken ct = default)
    {
        try
        {
            var outputCapture = new StringWriter();
            var originalOut = Console.Out;
            Console.SetOut(outputCapture);

            try
            {
                if (_state == null)
                {
                    // First run: inject preamble with helper shortcuts
                    var fullCode = Preamble + "\n" + code;
                    _state = await CSharpScript.RunAsync(fullCode, _options, _host,
                        typeof(PluginScriptHost), ct);
                }
                else
                {
                    _state = await _state.ContinueWithAsync(code, _options, ct);
                }
            }
            finally
            {
                Console.SetOut(originalOut);
            }

            var sb = new StringBuilder();

            var consoleOutput = outputCapture.ToString();
            if (!string.IsNullOrEmpty(consoleOutput))
                sb.Append(consoleOutput);

            if (_state?.ReturnValue != null)
            {
                if (sb.Length > 0 && !sb.ToString().EndsWith('\n'))
                    sb.AppendLine();
                sb.Append(FormatValue(_state.ReturnValue));
            }

            return sb.ToString();
        }
        catch (CompilationErrorException ex)
        {
            return $"Compilation error:\n{string.Join('\n', ex.Diagnostics)}";
        }
        catch (Exception ex)
        {
            return $"Runtime error: {ex.GetType().Name}: {ex.Message}";
        }
    }

    public void Reset() => _state = null;

    private static string FormatValue(object? value)
    {
        if (value == null) return "null";
        if (value is byte[] bytes)
            return BitConverter.ToString(bytes).Replace("-", " ");
        if (value is string s) return $"\"{s}\"";
        return value.ToString() ?? "null";
    }
}
