using System.IO;
using System.Reflection;
using System.Text;
using KernelFlirt.SDK;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace ScriptingPlugin;

/// <summary>
/// Globals object injected into every script execution.
/// All public fields/properties are accessible as top-level variables in scripts.
/// </summary>
public class ScriptGlobals
{
    /// <summary>Main debugger API — memory, breakpoints, symbols, process, UI, log.</summary>
    public IDebuggerApi api { get; set; } = null!;

    /// <summary>Shortcut: read memory from the target process.</summary>
    public byte[]? ReadMem(ulong address, uint size)
        => api.Memory.ReadMemory(api.TargetPid, address, size);

    /// <summary>Shortcut: write memory to the target process.</summary>
    public bool WriteMem(ulong address, byte[] data)
        => api.Memory.WriteMemory(api.TargetPid, address, data);

    /// <summary>Shortcut: read a null-terminated ASCII string from memory.</summary>
    public string ReadString(ulong address, int maxLen = 256)
    {
        var data = api.Memory.ReadMemory(api.TargetPid, address, (uint)maxLen);
        if (data == null) return "<read failed>";
        int end = Array.IndexOf(data, (byte)0);
        if (end < 0) end = data.Length;
        return Encoding.ASCII.GetString(data, 0, end);
    }

    /// <summary>Shortcut: read a null-terminated Unicode string from memory.</summary>
    public string ReadWString(ulong address, int maxLen = 256)
    {
        var data = api.Memory.ReadMemory(api.TargetPid, address, (uint)(maxLen * 2));
        if (data == null) return "<read failed>";
        int end = 0;
        for (int i = 0; i < data.Length - 1; i += 2)
        {
            if (data[i] == 0 && data[i + 1] == 0) break;
            end = i + 2;
        }
        return Encoding.Unicode.GetString(data, 0, end);
    }

    /// <summary>Shortcut: read a pointer (8 bytes on x64, 4 on x86).</summary>
    public ulong ReadPtr(ulong address)
    {
        int size = api.Is32Bit ? 4 : 8;
        var data = api.Memory.ReadMemory(api.TargetPid, address, (uint)size);
        if (data == null) return 0;
        return size == 8 ? BitConverter.ToUInt64(data) : BitConverter.ToUInt32(data);
    }

    /// <summary>Shortcut: read a uint32 from memory.</summary>
    public uint ReadU32(ulong address)
    {
        var data = api.Memory.ReadMemory(api.TargetPid, address, 4);
        return data != null ? BitConverter.ToUInt32(data) : 0;
    }

    /// <summary>Shortcut: read a uint64 from memory.</summary>
    public ulong ReadU64(ulong address)
    {
        var data = api.Memory.ReadMemory(api.TargetPid, address, 8);
        return data != null ? BitConverter.ToUInt64(data) : 0;
    }

    /// <summary>Shortcut: get register value by name.</summary>
    public ulong Reg(string name)
    {
        var regs = api.Memory.ReadRegisters(api.TargetPid, api.SelectedThreadId);
        var r = regs.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return r?.Value ?? 0;
    }

    /// <summary>Shortcut: get RIP.</summary>
    public ulong RIP => Reg("RIP");

    /// <summary>Shortcut: get RSP.</summary>
    public ulong RSP => Reg("RSP");

    /// <summary>Shortcut: resolve symbol name at address.</summary>
    public string? Sym(ulong address) => api.Symbols.ResolveAddress(address);

    /// <summary>Shortcut: resolve address by symbol name.</summary>
    public ulong Addr(string name) => api.Symbols.ResolveNameToAddress(name);

    /// <summary>Print to the output panel.</summary>
    public Action<string> print { get; set; } = Console.WriteLine;
}

/// <summary>
/// Roslyn-based C# script execution engine.
/// Maintains script state between executions for REPL-like behavior.
/// </summary>
public sealed class ScriptEngine
{
    private readonly ScriptGlobals _globals;
    private readonly ScriptOptions _options;
    private ScriptState<object>? _state;

    public ScriptEngine(IDebuggerApi api, Action<string> printCallback)
    {
        _globals = new ScriptGlobals
        {
            api = api,
            print = printCallback
        };

        _options = ScriptOptions.Default
            .AddReferences(
                typeof(object).Assembly,                    // System.Runtime
                typeof(Console).Assembly,                   // System.Console
                typeof(Enumerable).Assembly,                // System.Linq
                typeof(List<>).Assembly,                    // System.Collections
                typeof(BitConverter).Assembly,              // System.Runtime.Extensions
                typeof(Encoding).Assembly,                  // System.Text.Encoding
                typeof(File).Assembly,                      // System.IO
                typeof(IDebuggerApi).Assembly               // KernelFlirt.SDK
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

    /// <summary>
    /// Execute a C# script. Maintains state between calls (variables persist).
    /// Returns the result as a string, or error message.
    /// </summary>
    public async Task<string> ExecuteAsync(string code, CancellationToken ct = default)
    {
        try
        {
            // Redirect Console.WriteLine to our output
            var outputCapture = new StringWriter();
            var originalOut = Console.Out;
            Console.SetOut(outputCapture);

            try
            {
                if (_state == null)
                {
                    _state = await CSharpScript.RunAsync(code, _options, _globals,
                        typeof(ScriptGlobals), ct);
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

            // Captured Console.Write output
            var consoleOutput = outputCapture.ToString();
            if (!string.IsNullOrEmpty(consoleOutput))
                sb.Append(consoleOutput);

            // Script return value
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

    /// <summary>Reset script state (clear all variables).</summary>
    public void Reset()
    {
        _state = null;
    }

    private static string FormatValue(object? value)
    {
        if (value == null) return "null";
        if (value is byte[] bytes)
            return BitConverter.ToString(bytes).Replace("-", " ");
        if (value is string s) return $"\"{s}\"";
        return value.ToString() ?? "null";
    }
}
