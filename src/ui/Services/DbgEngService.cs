// DbgEngService.cs — Kernel debugger via kd.exe child process.
// Launches kd.exe with redirected stdin/stdout, sends WinDbg text commands,
// parses output for break events, registers, modules, etc.
// Replaces broken COM interop approach (CCW crashes on .NET Core).

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using KernelFlirt.UI.Models;

namespace KernelFlirt.UI.Services;

public class DbgEngService : IDisposable
{
    private Process? _kdProcess;
    private StreamWriter? _stdin;
    private bool _disposed;
    private bool _connected;
    private string? _connectionString;

    // All output from kd.exe accumulates here
    private readonly StringBuilder _outputBuffer = new();
    private readonly object _outputLock = new();
    private readonly ManualResetEventSlim _promptReady = new(false);

    // Breakpoint tracking
    private readonly Dictionary<uint, BreakpointInfo> _breakpoints = new();
    private uint _nextBpHandle = 1;

    // Cached register index (not needed for kd.exe but kept for API compat)

    public bool IsConnected => _connected;
    public string? ConnectionString => _connectionString;

    // Fired when target breaks (BP hit, step complete, etc.)
    public event Action<DebugEvent?>? OnTargetBreak;

    // Track whether we're in "running" state
    private volatile bool _isRunning;

    // KD handshake complete — target is debuggable
    private volatile bool _kdHandshakeDone;

    // ========================================================================
    // Logging
    // ========================================================================

    private static readonly string _logFile = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "dbgeng.log");

    private static void Trace(string msg)
    {
        try { File.AppendAllText(_logFile, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n"); } catch { }
    }

    // ========================================================================
    // Connection — launch kd.exe
    // ========================================================================

    public bool Connect(string connectionString)
    {
        if (_connected) return true;

        try
        {
            Trace($"Connect start: {connectionString}");

            // Find kd.exe — should be in the same directory as the app (copied by build)
            string kdPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "kd.exe");
            if (!File.Exists(kdPath))
            {
                // Fallback to Windows SDK location
                kdPath = @"C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\kd.exe";
            }

            if (!File.Exists(kdPath))
            {
                Trace($"kd.exe not found");
                return false;
            }

            Trace($"Using kd.exe: {kdPath}");

            // Launch kd.exe with kernel connection string
            // Example: kd.exe -k com:pipe,port=\\.\pipe\com_1,resets=0,reconnect
            var psi = new ProcessStartInfo
            {
                FileName = kdPath,
                Arguments = $"-k {connectionString}",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                // Set symbol path via environment
                Environment =
                {
                    ["_NT_SYMBOL_PATH"] = @"srv*C:\Symbols*https://msdl.microsoft.com/download/symbols"
                }
            };

            _kdProcess = new Process { StartInfo = psi };
            _kdProcess.Start();

            _stdin = _kdProcess.StandardInput;
            _stdin.AutoFlush = true;

            // Start reading stdout/stderr in background threads
            StartOutputReader(_kdProcess.StandardOutput, "stdout");
            StartOutputReader(_kdProcess.StandardError, "stderr");

            _connected = true;
            _connectionString = connectionString;

            Trace("kd.exe launched, waiting for KD handshake...");
            return true;
        }
        catch (Exception ex)
        {
            Trace($"Connect FAILED: {ex}");
            Cleanup();
            return false;
        }
    }

    private void StartOutputReader(StreamReader reader, string label)
    {
        var thread = new Thread(() =>
        {
            try
            {
                var buf = new char[4096];
                while (!_disposed)
                {
                    int count = reader.Read(buf, 0, buf.Length);
                    if (count <= 0) break;

                    string text = new string(buf, 0, count);
                    Trace($"[{label}] {text.TrimEnd('\r', '\n')}");

                    lock (_outputLock)
                    {
                        _outputBuffer.Append(text);
                    }

                    // Detect KD handshake completion — auto break-in
                    if (!_kdHandshakeDone && text.Contains("Kernel Debugger connection established"))
                    {
                        _kdHandshakeDone = true;
                        Trace("KD handshake done — sending break-in (Ctrl+C)...");
                        // Small delay to let KD finish initialization
                        Thread.Sleep(500);
                        try
                        {
                            _stdin?.Write('\x03');
                            _stdin?.Flush();
                        }
                        catch (Exception ex) { Trace($"Auto break-in failed: {ex.Message}"); }
                    }

                    // Check if we got a prompt (kd>, lkd>, or similar)
                    if (ContainsPrompt(text))
                    {
                        Trace($"[{label}] Prompt detected");

                        // Target just broke — fire event
                        if (_isRunning || !_promptReady.IsSet)
                        {
                            _isRunning = false;
                            Trace("Target stopped");

                            var evt = ParseBreakEvent();
                            OnTargetBreak?.Invoke(evt);
                        }

                        _promptReady.Set();
                    }
                }
            }
            catch (Exception ex)
            {
                Trace($"[{label}] reader exception: {ex.Message}");
            }
        })
        { IsBackground = true, Name = $"KD-{label}" };
        thread.Start();
    }

    private static bool ContainsPrompt(string text)
    {
        // kd> or N:NNN> or lkd> at end of text (possibly with whitespace)
        string trimmed = text.TrimEnd();
        return trimmed.EndsWith("kd>", StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith("lkd>", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(trimmed, @"\d+:\d+>$");
    }

    /// <summary>
    /// Send a command to kd.exe and wait for the prompt to return.
    /// Returns the output text between sending the command and getting the prompt.
    /// </summary>
    private string? SendCommand(string command, int timeoutMs = 30000)
    {
        if (_stdin == null || _kdProcess == null || _kdProcess.HasExited) return null;

        // Clear prompt signal and output buffer
        _promptReady.Reset();
        lock (_outputLock) { _outputBuffer.Clear(); }

        Trace($">>> {command}");
        _stdin.WriteLine(command);

        // Wait for prompt
        if (!_promptReady.Wait(timeoutMs))
        {
            Trace($"Command '{command}' timed out after {timeoutMs}ms");
            return null;
        }

        string output;
        lock (_outputLock)
        {
            output = _outputBuffer.ToString();
            _outputBuffer.Clear();
        }

        return output;
    }

    /// <summary>
    /// Send an execution command (g/t/p) — these don't return until target breaks.
    /// </summary>
    private void SendExecCommand(string command)
    {
        if (_stdin == null || _kdProcess == null || _kdProcess.HasExited) return;

        // Clear prompt signal and buffer
        _promptReady.Reset();
        lock (_outputLock) { _outputBuffer.Clear(); }

        _isRunning = true;
        Trace($">>> {command} (exec)");
        _stdin.WriteLine(command);

        // Don't wait — the output reader thread will detect the prompt
        // and fire OnTargetBreak when the target stops.
    }

    public (uint version, bool success) Ping()
    {
        if (!_connected) return (0, false);
        return (0xDB6E0001, true);
    }

    // ========================================================================
    // Execution Control
    // ========================================================================

    public bool Break()
    {
        if (_kdProcess == null || _kdProcess.HasExited) return false;
        // Ctrl+C / break-in — send Ctrl+Break to kd.exe
        // On Windows, GenerateConsoleCtrlEvent won't work for redirected processes.
        // Instead, send the break character (Ctrl+C = 0x03) via stdin.
        Trace("Break: sending Ctrl+C to kd stdin");
        try
        {
            _stdin?.Write('\x03');
            _stdin?.Flush();
            return true;
        }
        catch (Exception ex)
        {
            Trace($"Break failed: {ex.Message}");
            return false;
        }
    }

    public bool Run()
    {
        Trace("Run: sending 'g'");
        SendExecCommand("g");
        return true;
    }

    public bool StepInto()
    {
        Trace("StepInto: sending 't'");
        SendExecCommand("t");
        return true;
    }

    public bool StepOver()
    {
        Trace("StepOver: sending 'p'");
        SendExecCommand("p");
        return true;
    }

    public bool ContinueDebugEvent(uint mode = 0)
    {
        string cmd = mode switch
        {
            2 => "t",
            _ => "g"
        };
        SendExecCommand(cmd);
        return true;
    }

    // ========================================================================
    // Event Listener (no-op — output reader handles events)
    // ========================================================================

    public void StartListening() { /* Output reader thread handles break detection */ }
    public void StopListening() { }
    public void StartEventListener() { }
    public void StopEventListener() { }

    // ========================================================================
    // Memory
    // ========================================================================

    public byte[]? ReadMemory(uint pid, ulong address, uint size)
    {
        if (!_connected) return null;

        // Use "db" command to read memory bytes
        // For larger reads, use "db <addr> L<size>"
        string? output = SendCommand($"db {address:x} L{size:x}");
        if (output == null) return null;

        return ParseDbOutput(output, size);
    }

    public bool WriteMemory(uint pid, ulong address, byte[] data)
    {
        if (!_connected) return false;

        // Use "eb" (edit bytes) command
        var sb = new StringBuilder();
        sb.Append($"eb {address:x}");
        foreach (byte b in data)
            sb.Append($" {b:x2}");

        string? output = SendCommand(sb.ToString());
        return output != null;
    }

    private static byte[]? ParseDbOutput(string output, uint expectedSize)
    {
        // "db" output format:
        // fffff802`12345678  cc 90 90 48 89 5c 24 08-48 89 6c 24 10 48 89 74  ...H.\$.H.l$.H.t
        var result = new List<byte>();
        foreach (string line in output.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            // Find the byte content between address and ASCII dump
            // Address is followed by two spaces, then hex bytes
            var match = Regex.Match(trimmed, @"[0-9a-f`]+\s{2}(.+?)  .{0,16}$", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                // Try without ASCII part
                match = Regex.Match(trimmed, @"[0-9a-f`]+\s{2}([0-9a-f \-]+)", RegexOptions.IgnoreCase);
            }
            if (!match.Success) continue;

            string hexPart = match.Groups[1].Value.Replace("-", " ");
            foreach (string byteStr in hexPart.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (byte.TryParse(byteStr, NumberStyles.HexNumber, null, out byte b))
                    result.Add(b);
            }

            if (result.Count >= expectedSize) break;
        }

        return result.Count > 0 ? result.ToArray() : null;
    }

    // ========================================================================
    // Registers
    // ========================================================================

    public List<Register> ReadRegisters(uint pid, uint tid)
    {
        var regs = new List<Register>();
        if (!_connected) return regs;

        // Use "r" command to dump registers
        string? output = SendCommand("r");
        if (output == null) return regs;

        return ParseRegisters(output);
    }

    private static List<Register> ParseRegisters(string output)
    {
        var regs = new List<Register>();

        // KD register output format:
        // rax=0000000000000000 rbx=fffff80212345678 rcx=0000000000000001
        // dr0=0000000000000000 dr1=0000000000000000 ...
        // efl=00000246
        var regPattern = new Regex(@"(\w+)=([0-9a-f]+)", RegexOptions.IgnoreCase);
        var knownRegs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "rax", "rbx", "rcx", "rdx", "rsi", "rdi", "rbp", "rsp",
            "r8", "r9", "r10", "r11", "r12", "r13", "r14", "r15",
            "rip", "efl",
            "cs", "ds", "es", "fs", "gs", "ss",
            "dr0", "dr1", "dr2", "dr3", "dr6", "dr7"
        };

        foreach (Match match in regPattern.Matches(output))
        {
            string name = match.Groups[1].Value.ToLowerInvariant();
            if (!knownRegs.Contains(name)) continue;

            if (ulong.TryParse(match.Groups[2].Value, NumberStyles.HexNumber, null, out ulong val))
            {
                string displayName = name.ToUpperInvariant();
                if (displayName == "EFL") displayName = "RFLAGS";
                regs.Add(new Register { Name = displayName, Value = val });
            }
        }

        return regs;
    }

    public bool WriteRip(uint pid, uint tid, ulong newRip)
    {
        if (!_connected) return false;
        string? output = SendCommand($"r rip={newRip:x}");
        return output != null;
    }

    // ========================================================================
    // Breakpoints
    // ========================================================================

    private class BreakpointInfo
    {
        public uint KdId; // kd-assigned breakpoint ID
        public ulong Address;
        public BreakpointType Type;
    }

    public uint? SetBreakpoint(uint pid, uint tid, ulong address, BreakpointType type, uint length = 1)
    {
        if (!_connected) return null;

        string cmd = type switch
        {
            BreakpointType.Software => $"bp {address:x}",
            BreakpointType.Hardware => $"ba e 1 {address:x}",
            BreakpointType.HwWrite => $"ba w {length} {address:x}",
            BreakpointType.HwReadWrite => $"ba r {length} {address:x}",
            _ => $"bp {address:x}"
        };

        string? output = SendCommand(cmd);
        if (output == null) return null;

        // Parse breakpoint ID from output (e.g., "Breakpoint 0 hit" or just success)
        // List breakpoints to find the one we just set
        string? blOutput = SendCommand("bl");
        uint kdId = 0;
        if (blOutput != null)
        {
            // bl output format: "  0 e Disable Clear  fffff802`12345678     0001 (0001) nt!func"
            var blPattern = new Regex(@"^\s*(\d+)\s+e\s+.*?([0-9a-f`]+)", RegexOptions.IgnoreCase | RegexOptions.Multiline);
            foreach (Match m in blPattern.Matches(blOutput))
            {
                string addrStr = m.Groups[2].Value.Replace("`", "");
                if (ulong.TryParse(addrStr, NumberStyles.HexNumber, null, out ulong bpAddr) && bpAddr == address)
                {
                    kdId = uint.Parse(m.Groups[1].Value);
                    break;
                }
            }
        }

        uint handle = _nextBpHandle++;
        _breakpoints[handle] = new BreakpointInfo { KdId = kdId, Address = address, Type = type };
        return handle;
    }

    public bool RemoveBreakpoint(uint handle)
    {
        if (!_connected) return false;
        if (!_breakpoints.TryGetValue(handle, out var bp)) return false;

        string? output = SendCommand($"bc {bp.KdId}");
        _breakpoints.Remove(handle);
        return output != null;
    }

    public bool SingleStep(uint pid, uint tid)
    {
        return StepInto();
    }

    // ========================================================================
    // Process / Thread / Module Enumeration
    // ========================================================================

    public List<ProcessInfo> EnumProcesses()
    {
        var result = new List<ProcessInfo>();
        if (!_connected) return result;

        // Use "!process 0 0" to list all processes
        string? output = SendCommand("!process 0 0", 60000);
        if (output == null) return result;

        // Parse output:
        // PROCESS fffffa8012345678
        //     SessionId: 1  Cid: 0abc    Peb: ...
        //     Image: notepad.exe
        var procPattern = new Regex(
            @"PROCESS\s+[0-9a-f]+\s*\n\s*SessionId:\s*(\d+)\s+Cid:\s*([0-9a-f]+).*?\n\s*.*?Image:\s*(\S+)",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);

        foreach (Match m in procPattern.Matches(output))
        {
            if (uint.TryParse(m.Groups[2].Value, NumberStyles.HexNumber, null, out uint pid))
            {
                uint.TryParse(m.Groups[1].Value, out uint sessionId);
                result.Add(new ProcessInfo
                {
                    ProcessId = pid,
                    Name = m.Groups[3].Value,
                    SessionId = sessionId
                });
            }
        }

        return result;
    }

    public List<ThreadInfo> EnumThreads(uint pid)
    {
        var result = new List<ThreadInfo>();
        if (!_connected) return result;

        // Use "!process <pid_hex> 2" to list threads for a process
        string? output = SendCommand($"!process 0n{pid} 2", 30000);
        if (output == null) return result;

        // Parse: THREAD fffffa80... Cid 0abc.0def Teb: ...
        var threadPattern = new Regex(@"THREAD\s+[0-9a-f]+\s+Cid\s+[0-9a-f]+\.([0-9a-f]+)",
            RegexOptions.IgnoreCase);

        foreach (Match m in threadPattern.Matches(output))
        {
            if (uint.TryParse(m.Groups[1].Value, NumberStyles.HexNumber, null, out uint tid))
            {
                result.Add(new ThreadInfo
                {
                    ThreadId = tid,
                    StartAddress = 0,
                    State = 5,
                    Priority = 0
                });
            }
        }

        return result;
    }

    public List<ModuleInfo> EnumModules(uint pid)
    {
        var result = new List<ModuleInfo>();
        if (!_connected) return result;

        // Use "lm" to list modules
        string? output = SendCommand("lm", 30000);
        if (output == null) return result;

        return ParseLmOutput(output).Select(m => new ModuleInfo
        {
            BaseAddress = m.BaseAddress,
            Size = m.Size,
            Name = m.Name
        }).ToList();
    }

    public List<KernelModuleInfo> EnumKernelModules()
    {
        var result = new List<KernelModuleInfo>();
        if (!_connected) return result;

        string? output = SendCommand("lm", 30000);
        if (output == null) return result;

        ushort order = 0;
        foreach (var m in ParseLmOutput(output))
        {
            result.Add(new KernelModuleInfo
            {
                BaseAddress = m.BaseAddress,
                Size = m.Size,
                Name = m.Name,
                LoadOrder = order++
            });
        }

        return result;
    }

    private static List<(ulong BaseAddress, uint Size, string Name)> ParseLmOutput(string output)
    {
        // lm output format:
        // start             end                 module name
        // fffff802`12340000 fffff802`12a00000   nt         (pdb symbols) ...
        var result = new List<(ulong, uint, string)>();
        var lmPattern = new Regex(
            @"([0-9a-f`]+)\s+([0-9a-f`]+)\s+(\S+)",
            RegexOptions.IgnoreCase);

        foreach (string line in output.Split('\n'))
        {
            var match = lmPattern.Match(line);
            if (!match.Success) continue;

            string startStr = match.Groups[1].Value.Replace("`", "");
            string endStr = match.Groups[2].Value.Replace("`", "");
            string name = match.Groups[3].Value;

            // Skip header line
            if (name.Equals("module", StringComparison.OrdinalIgnoreCase)) continue;

            if (ulong.TryParse(startStr, NumberStyles.HexNumber, null, out ulong start) &&
                ulong.TryParse(endStr, NumberStyles.HexNumber, null, out ulong end))
            {
                result.Add((start, (uint)(end - start), name));
            }
        }

        return result;
    }

    // ========================================================================
    // Thread Control
    // ========================================================================

    public bool SuspendThread(uint tid)
    {
        string? output = SendCommand($"~[{tid}]f");
        return output != null;
    }

    public bool ResumeThread(uint tid)
    {
        string? output = SendCommand($"~[{tid}]u");
        return output != null;
    }

    // ========================================================================
    // Stubs for compatibility
    // ========================================================================

    public bool InstallDebugHook(uint targetPid = 0) => true;
    public bool RemoveDebugHook() => true;

    // ========================================================================
    // Symbol Resolution
    // ========================================================================

    public string? ResolveSymbol(ulong address)
    {
        if (!_connected) return null;

        string? output = SendCommand($"ln {address:x}");
        if (output == null) return null;

        // ln output format:
        // (fffff802`12345678)   nt!KeBugCheck   |  (fffff802`12345700)   nt!KeBugCheck2
        var lnPattern = new Regex(@"\(\s*[0-9a-f`]+\s*\)\s+(\S+!?\S+)", RegexOptions.IgnoreCase);
        var match = lnPattern.Match(output);
        if (!match.Success) return null;

        string symbol = match.Groups[1].Value;

        // Calculate displacement if needed
        string addrStr = Regex.Match(output, @"\(\s*([0-9a-f`]+)\s*\)")?.Groups[1]?.Value?.Replace("`", "") ?? "";
        if (ulong.TryParse(addrStr, NumberStyles.HexNumber, null, out ulong symAddr) && symAddr != address)
        {
            ulong disp = address - symAddr;
            return $"{symbol}+0x{disp:X}";
        }

        return symbol;
    }

    public ulong? ResolveAddress(string symbol)
    {
        if (!_connected) return null;

        // Use "x" command to find symbol address
        string? output = SendCommand($"x {symbol}");
        if (output == null) return null;

        // x output: fffff802`12345678 nt!KeBugCheck (<no parameter info>)
        var xPattern = new Regex(@"([0-9a-f`]+)\s+\S+!\S+", RegexOptions.IgnoreCase);
        var match = xPattern.Match(output);
        if (!match.Success) return null;

        string addrStr = match.Groups[1].Value.Replace("`", "");
        return ulong.TryParse(addrStr, NumberStyles.HexNumber, null, out ulong addr) ? addr : null;
    }

    // ========================================================================
    // Execute raw debugger command
    // ========================================================================

    public int ExecuteCommand(string command)
    {
        string? output = SendCommand(command);
        return output != null ? 0 : -1; // S_OK or E_FAIL
    }

    // ========================================================================
    // Break Event Parsing
    // ========================================================================

    private DebugEvent? ParseBreakEvent()
    {
        string output;
        lock (_outputLock)
        {
            output = _outputBuffer.ToString();
        }

        var evt = new DebugEvent { Type = DebugEventType.Breakpoint };

        // Try to parse RIP from the break output
        // Common format: "nt!DbgBreakPointWithStatus:"
        // followed by "fffff802`12345678 cc              int     3"
        // Or register dump with rip=...

        // Look for instruction pointer in the output
        var ripMatch = Regex.Match(output, @"rip=([0-9a-f]+)", RegexOptions.IgnoreCase);
        if (ripMatch.Success && ulong.TryParse(ripMatch.Groups[1].Value, NumberStyles.HexNumber, null, out ulong rip))
        {
            evt.Address = rip;
        }
        else
        {
            // Try address from disassembly line: "fffff802`12345678 cc   int 3"
            var asmMatch = Regex.Match(output, @"([0-9a-f`]{17})\s+[0-9a-f]{2}", RegexOptions.IgnoreCase);
            if (asmMatch.Success)
            {
                string addrStr = asmMatch.Groups[1].Value.Replace("`", "");
                if (ulong.TryParse(addrStr, NumberStyles.HexNumber, null, out ulong addr))
                    evt.Address = addr;
            }
        }

        // Try to parse process/thread IDs from prompt: "0:000>"
        var promptMatch = Regex.Match(output, @"(\d+):(\d+)>");
        if (promptMatch.Success)
        {
            uint.TryParse(promptMatch.Groups[1].Value, out uint proc);
            uint.TryParse(promptMatch.Groups[2].Value, out uint thread);
            evt.ProcessId = proc;
            evt.ThreadId = thread;
        }

        evt.IsKernelMode = true;

        Trace($"ParseBreakEvent: RIP={evt.Address:X16} PID={evt.ProcessId} TID={evt.ThreadId}");
        return evt;
    }

    // ========================================================================
    // Cleanup
    // ========================================================================

    private void Cleanup()
    {
        _connected = false;

        if (_kdProcess != null && !_kdProcess.HasExited)
        {
            try
            {
                _stdin?.WriteLine("q");
                _stdin?.Flush();
                if (!_kdProcess.WaitForExit(3000))
                    _kdProcess.Kill();
            }
            catch { }
        }

        _stdin?.Dispose();
        _stdin = null;
        _kdProcess?.Dispose();
        _kdProcess = null;

        _breakpoints.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cleanup();
        GC.SuppressFinalize(this);
    }
}
