using System.ComponentModel;
using System.Text;
using KernelFlirt.SDK;

namespace NetworkMonitorPlugin;

/// <summary>
/// Hooked network API functions.
/// </summary>
public enum NetFunc
{
    // Winsock data
    send, recv, sendto, recvfrom,
    WSASend, WSARecv, WSASendTo, WSARecvFrom,
    WSASendMsg, WSARecvMsg, TransmitFile,
    // Winsock control
    connect, accept, bind, listen,
    closesocket, shutdown, select,
    ioctlsocket, setsockopt, getsockopt,
    // Winsock DNS/info
    getpeername, getsockname,
    gethostbyname, gethostbyaddr,
    getaddrinfo, GetAddrInfoW, freeaddrinfo,
    gethostname,
    // WinINet
    InternetOpenA, InternetOpenW,
    InternetConnectA, InternetConnectW,
    InternetOpenUrlA, InternetOpenUrlW,
    HttpOpenRequestA, HttpOpenRequestW,
    HttpSendRequestA, HttpSendRequestW,
    HttpQueryInfoA, HttpQueryInfoW,
    InternetReadFile, InternetWriteFile,
    InternetCloseHandle,
    // WinHTTP
    WinHttpOpen, WinHttpConnect,
    WinHttpOpenRequest, WinHttpSendRequest,
    WinHttpReceiveResponse,
    WinHttpReadData, WinHttpWriteData,
    WinHttpQueryDataAvailable, WinHttpQueryHeaders,
    WinHttpCloseHandle,
    // URLMon
    URLDownloadToFileA, URLDownloadToFileW,
    URLDownloadToCacheFileA, URLDownloadToCacheFileW,
}

/// <summary>
/// A single captured network event.
/// </summary>
public sealed class NetEvent : INotifyPropertyChanged
{
    public int Index { get; init; }
    public string Time { get; init; } = "";
    public uint ThreadId { get; init; }
    public string Function { get; init; } = "";
    public string Direction { get; init; } = ""; // "SEND", "RECV", "CTRL", "HTTP"
    public int Socket { get; init; }
    public int DataSize { get; set; }
    public string Preview { get; set; } = "";
    public string Details { get; set; } = "";
    private string _returnValue = "";
    public string ReturnValue
    {
        get => _returnValue;
        set { _returnValue = value; PropertyChanged?.Invoke(this, new(nameof(ReturnValue))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Definition of a hooked network function.
/// </summary>
public sealed class NetApiDef
{
    public string Module { get; init; } = "";
    public string Function { get; init; } = "";
    public NetFunc Id { get; init; }
    public string Direction { get; init; } = "";
    public bool CaptureData { get; init; }
}

/// <summary>
/// Core network monitoring engine.
/// Sets breakpoints on network APIs, captures arguments on hit, auto-continues.
/// </summary>
public sealed class NetworkMonitorEngine
{
    private readonly IDebuggerApi _api;
    private readonly Dictionary<ulong, NetApiDef> _hooks = new();
    private readonly List<uint> _bpHandles = new();
    private readonly Dictionary<ulong, (NetApiDef Def, NetEvent Entry, uint BpHandle)> _returnHooks = new();
    private int _eventIndex;

    public event Action<NetEvent>? OnNetEvent;
    public bool IsMonitoring { get; private set; }

    // API database
    public static readonly NetApiDef[] ApiDefs =
    [
        // ── Winsock: data transfer ──────────────────────────────────────────
        new() { Module = "ws2_32", Function = "send", Id = NetFunc.send, Direction = "SEND", CaptureData = true },
        new() { Module = "ws2_32", Function = "recv", Id = NetFunc.recv, Direction = "RECV", CaptureData = true },
        new() { Module = "ws2_32", Function = "sendto", Id = NetFunc.sendto, Direction = "SEND", CaptureData = true },
        new() { Module = "ws2_32", Function = "recvfrom", Id = NetFunc.recvfrom, Direction = "RECV", CaptureData = true },
        new() { Module = "ws2_32", Function = "WSASend", Id = NetFunc.WSASend, Direction = "SEND", CaptureData = true },
        new() { Module = "ws2_32", Function = "WSARecv", Id = NetFunc.WSARecv, Direction = "RECV", CaptureData = true },
        new() { Module = "ws2_32", Function = "WSASendTo", Id = NetFunc.WSASendTo, Direction = "SEND", CaptureData = true },
        new() { Module = "ws2_32", Function = "WSARecvFrom", Id = NetFunc.WSARecvFrom, Direction = "RECV", CaptureData = true },
        new() { Module = "ws2_32", Function = "WSASendMsg", Id = NetFunc.WSASendMsg, Direction = "SEND", CaptureData = true },
        new() { Module = "ws2_32", Function = "WSARecvMsg", Id = NetFunc.WSARecvMsg, Direction = "RECV", CaptureData = true },
        new() { Module = "mswsock", Function = "TransmitFile", Id = NetFunc.TransmitFile, Direction = "SEND" },

        // ── Winsock: connection control ──────────────────────────────────────
        new() { Module = "ws2_32", Function = "connect", Id = NetFunc.connect, Direction = "CTRL" },
        new() { Module = "ws2_32", Function = "accept", Id = NetFunc.accept, Direction = "CTRL" },
        new() { Module = "ws2_32", Function = "bind", Id = NetFunc.bind, Direction = "CTRL" },
        new() { Module = "ws2_32", Function = "listen", Id = NetFunc.listen, Direction = "CTRL" },
        new() { Module = "ws2_32", Function = "closesocket", Id = NetFunc.closesocket, Direction = "CTRL" },
        new() { Module = "ws2_32", Function = "shutdown", Id = NetFunc.shutdown, Direction = "CTRL" },
        new() { Module = "ws2_32", Function = "select", Id = NetFunc.select, Direction = "CTRL" },
        new() { Module = "ws2_32", Function = "ioctlsocket", Id = NetFunc.ioctlsocket, Direction = "CTRL" },
        new() { Module = "ws2_32", Function = "setsockopt", Id = NetFunc.setsockopt, Direction = "CTRL" },
        new() { Module = "ws2_32", Function = "getsockopt", Id = NetFunc.getsockopt, Direction = "CTRL" },

        // ── Winsock: DNS & address info ─────────────────────────────────────
        new() { Module = "ws2_32", Function = "getpeername", Id = NetFunc.getpeername, Direction = "CTRL" },
        new() { Module = "ws2_32", Function = "getsockname", Id = NetFunc.getsockname, Direction = "CTRL" },
        new() { Module = "ws2_32", Function = "gethostbyname", Id = NetFunc.gethostbyname, Direction = "CTRL" },
        new() { Module = "ws2_32", Function = "gethostbyaddr", Id = NetFunc.gethostbyaddr, Direction = "CTRL" },
        new() { Module = "ws2_32", Function = "getaddrinfo", Id = NetFunc.getaddrinfo, Direction = "CTRL" },
        new() { Module = "ws2_32", Function = "GetAddrInfoW", Id = NetFunc.GetAddrInfoW, Direction = "CTRL" },
        new() { Module = "ws2_32", Function = "freeaddrinfo", Id = NetFunc.freeaddrinfo, Direction = "CTRL" },
        new() { Module = "ws2_32", Function = "gethostname", Id = NetFunc.gethostname, Direction = "CTRL" },

        // ── WinINet ─────────────────────────────────────────────────────────
        new() { Module = "wininet", Function = "InternetOpenA", Id = NetFunc.InternetOpenA, Direction = "HTTP" },
        new() { Module = "wininet", Function = "InternetOpenW", Id = NetFunc.InternetOpenW, Direction = "HTTP" },
        new() { Module = "wininet", Function = "InternetConnectA", Id = NetFunc.InternetConnectA, Direction = "HTTP" },
        new() { Module = "wininet", Function = "InternetConnectW", Id = NetFunc.InternetConnectW, Direction = "HTTP" },
        new() { Module = "wininet", Function = "InternetOpenUrlA", Id = NetFunc.InternetOpenUrlA, Direction = "HTTP" },
        new() { Module = "wininet", Function = "InternetOpenUrlW", Id = NetFunc.InternetOpenUrlW, Direction = "HTTP" },
        new() { Module = "wininet", Function = "HttpOpenRequestA", Id = NetFunc.HttpOpenRequestA, Direction = "HTTP" },
        new() { Module = "wininet", Function = "HttpOpenRequestW", Id = NetFunc.HttpOpenRequestW, Direction = "HTTP" },
        new() { Module = "wininet", Function = "HttpSendRequestA", Id = NetFunc.HttpSendRequestA, Direction = "HTTP" },
        new() { Module = "wininet", Function = "HttpSendRequestW", Id = NetFunc.HttpSendRequestW, Direction = "HTTP" },
        new() { Module = "wininet", Function = "HttpQueryInfoA", Id = NetFunc.HttpQueryInfoA, Direction = "HTTP" },
        new() { Module = "wininet", Function = "HttpQueryInfoW", Id = NetFunc.HttpQueryInfoW, Direction = "HTTP" },
        new() { Module = "wininet", Function = "InternetReadFile", Id = NetFunc.InternetReadFile, Direction = "RECV", CaptureData = true },
        new() { Module = "wininet", Function = "InternetWriteFile", Id = NetFunc.InternetWriteFile, Direction = "SEND", CaptureData = true },
        new() { Module = "wininet", Function = "InternetCloseHandle", Id = NetFunc.InternetCloseHandle, Direction = "CTRL" },

        // ── WinHTTP ─────────────────────────────────────────────────────────
        new() { Module = "winhttp", Function = "WinHttpOpen", Id = NetFunc.WinHttpOpen, Direction = "HTTP" },
        new() { Module = "winhttp", Function = "WinHttpConnect", Id = NetFunc.WinHttpConnect, Direction = "HTTP" },
        new() { Module = "winhttp", Function = "WinHttpOpenRequest", Id = NetFunc.WinHttpOpenRequest, Direction = "HTTP" },
        new() { Module = "winhttp", Function = "WinHttpSendRequest", Id = NetFunc.WinHttpSendRequest, Direction = "SEND" },
        new() { Module = "winhttp", Function = "WinHttpReceiveResponse", Id = NetFunc.WinHttpReceiveResponse, Direction = "RECV" },
        new() { Module = "winhttp", Function = "WinHttpReadData", Id = NetFunc.WinHttpReadData, Direction = "RECV", CaptureData = true },
        new() { Module = "winhttp", Function = "WinHttpWriteData", Id = NetFunc.WinHttpWriteData, Direction = "SEND", CaptureData = true },
        new() { Module = "winhttp", Function = "WinHttpQueryDataAvailable", Id = NetFunc.WinHttpQueryDataAvailable, Direction = "RECV" },
        new() { Module = "winhttp", Function = "WinHttpQueryHeaders", Id = NetFunc.WinHttpQueryHeaders, Direction = "HTTP" },
        new() { Module = "winhttp", Function = "WinHttpCloseHandle", Id = NetFunc.WinHttpCloseHandle, Direction = "CTRL" },

        // ── URLMon ──────────────────────────────────────────────────────────
        new() { Module = "urlmon", Function = "URLDownloadToFileA", Id = NetFunc.URLDownloadToFileA, Direction = "RECV" },
        new() { Module = "urlmon", Function = "URLDownloadToFileW", Id = NetFunc.URLDownloadToFileW, Direction = "RECV" },
        new() { Module = "urlmon", Function = "URLDownloadToCacheFileA", Id = NetFunc.URLDownloadToCacheFileA, Direction = "RECV" },
        new() { Module = "urlmon", Function = "URLDownloadToCacheFileW", Id = NetFunc.URLDownloadToCacheFileW, Direction = "RECV" },
    ];

    public NetworkMonitorEngine(IDebuggerApi api)
    {
        _api = api;
    }

    public int Start()
    {
        if (IsMonitoring) return 0;

        uint pid = _api.TargetPid;
        int installed = 0;

        foreach (var def in ApiDefs)
        {
            ulong addr = _api.Symbols.ResolveNameToAddress($"{def.Module}!{def.Function}");
            if (addr == 0)
                addr = _api.Symbols.ResolveNameToAddress($"{def.Module}.dll!{def.Function}");
            if (addr == 0) continue;
            if (_hooks.ContainsKey(addr)) continue;

            var h = _api.Breakpoints.SetBreakpoint(pid, 0, addr, PluginBreakpointType.Software);
            if (!h.HasValue) continue;

            _hooks[addr] = def;
            _bpHandles.Add(h.Value);
            installed++;
        }

        IsMonitoring = true;
        return installed;
    }

    public void Stop()
    {
        foreach (var h in _bpHandles)
            _api.Breakpoints.RemoveBreakpoint(h);
        foreach (var (_, (_, _, h)) in _returnHooks)
            _api.Breakpoints.RemoveBreakpoint(h);

        _hooks.Clear();
        _bpHandles.Clear();
        _returnHooks.Clear();
        IsMonitoring = false;
    }

    /// <summary>
    /// Handle a debug event. Returns true if consumed (auto-continues).
    /// </summary>
    public bool HandleEvent(PluginDebugEvent evt)
    {
        if (evt.Type != PluginDebugEventType.Breakpoint) return false;

        // Check entry hooks
        if (_hooks.TryGetValue(evt.Address, out var def))
        {
            HandleEntry(evt, def);
            return true;
        }

        // Check return hooks
        if (_returnHooks.TryGetValue(evt.Address, out var retInfo))
        {
            HandleReturn(evt, retInfo.Def, retInfo.Entry, retInfo.BpHandle);
            return true;
        }

        return false;
    }

    private void HandleEntry(PluginDebugEvent evt, NetApiDef def)
    {
        uint pid = evt.ProcessId;
        uint tid = evt.ThreadId;
        bool is32 = _api.Is32Bit;

        var regs = _api.Memory.ReadRegisters(pid, tid);
        ulong arg1 = GetReg(regs, is32 ? "ECX" : "RCX");
        ulong arg2 = GetReg(regs, is32 ? "EDX" : "RDX");
        ulong arg3 = GetReg(regs, is32 ? "R8" : "R8");
        ulong arg4 = GetReg(regs, is32 ? "R9" : "R9");
        ulong rsp = GetReg(regs, is32 ? "ESP" : "RSP");

        // For x86 __stdcall: args on stack
        if (is32 && rsp != 0)
        {
            arg1 = ReadU32(pid, rsp + 4);
            arg2 = ReadU32(pid, rsp + 8);
            arg3 = ReadU32(pid, rsp + 12);
            arg4 = ReadU32(pid, rsp + 16);
        }

        string details = "";
        int dataSize = 0;
        string preview = "";
        int socket = (int)arg1;

        switch (def.Id)
        {
            case NetFunc.send:
            case NetFunc.sendto:
                // send(SOCKET s, const char* buf, int len, int flags)
                dataSize = (int)arg3;
                preview = ReadDataPreview(pid, arg2, dataSize);
                details = $"socket={socket} buf=0x{arg2:X} len={dataSize}";
                break;

            case NetFunc.recv:
            case NetFunc.recvfrom:
                // recv(SOCKET s, char* buf, int len, int flags)
                dataSize = (int)arg3;
                details = $"socket={socket} buf=0x{arg2:X} maxlen={dataSize}";
                break;

            case NetFunc.WSASend:
                // WSASend(SOCKET, LPWSABUF, DWORD dwBufferCount, ...)
                // WSABUF: { ULONG len; CHAR* buf; }
                dataSize = (int)ReadU32(pid, arg2); // first WSABUF.len
                var wsaBuf = ReadPtr(pid, arg2 + (ulong)(is32 ? 4 : 8)); // first WSABUF.buf
                preview = ReadDataPreview(pid, wsaBuf, dataSize);
                details = $"socket={socket} bufs={arg3} len={dataSize}";
                break;

            case NetFunc.WSARecv:
            case NetFunc.WSARecvMsg:
                dataSize = (int)ReadU32(pid, arg2);
                details = $"socket={socket} bufs={arg3}";
                break;

            case NetFunc.WSASendMsg:
                details = $"socket={socket} msg=0x{arg2:X}";
                break;

            case NetFunc.TransmitFile:
                details = $"socket={socket} file=0x{arg2:X} bytes={arg3}";
                dataSize = (int)arg3;
                break;

            // ── Connection control ──────────────────────────────────────
            case NetFunc.connect:
                details = $"socket={socket} addr={FormatSockAddr(pid, arg2)}";
                break;

            case NetFunc.accept:
                details = $"socket={socket}";
                break;

            case NetFunc.bind:
                details = $"socket={socket} addr={FormatSockAddr(pid, arg2)}";
                break;

            case NetFunc.listen:
                details = $"socket={socket} backlog={arg2}";
                break;

            case NetFunc.closesocket:
            case NetFunc.InternetCloseHandle:
            case NetFunc.WinHttpCloseHandle:
                details = $"handle={socket}";
                break;

            case NetFunc.shutdown:
                var how = arg2 == 0 ? "SD_RECEIVE" : arg2 == 1 ? "SD_SEND" : "SD_BOTH";
                details = $"socket={socket} how={how}";
                break;

            case NetFunc.select:
                details = $"nfds={arg1} readfds=0x{arg2:X} writefds=0x{arg3:X} exceptfds=0x{arg4:X}";
                socket = 0;
                break;

            case NetFunc.ioctlsocket:
                details = $"socket={socket} cmd=0x{arg2:X}";
                break;

            case NetFunc.setsockopt:
            case NetFunc.getsockopt:
                details = $"socket={socket} level={arg2} optname={arg3}";
                break;

            // ── DNS / address info ──────────────────────────────────────
            case NetFunc.getpeername:
            case NetFunc.getsockname:
                details = $"socket={socket}";
                break;

            case NetFunc.gethostbyname:
                details = $"name=\"{ReadStringAt(pid, arg1)}\"";
                socket = 0;
                break;

            case NetFunc.gethostbyaddr:
                details = $"addr=0x{arg1:X} len={arg2} type={arg3}";
                socket = 0;
                break;

            case NetFunc.getaddrinfo:
            case NetFunc.GetAddrInfoW:
                var nodeName = ReadStringAt(pid, arg1);
                var serviceName = ReadStringAt(pid, arg2);
                details = $"node=\"{nodeName}\" service=\"{serviceName}\"";
                socket = 0;
                break;

            case NetFunc.freeaddrinfo:
                details = $"addrinfo=0x{arg1:X}";
                socket = 0;
                break;

            case NetFunc.gethostname:
                details = $"buf=0x{arg1:X} len={arg2}";
                socket = 0;
                break;

            // ── WinINet ─────────────────────────────────────────────────
            case NetFunc.InternetOpenA:
            case NetFunc.InternetOpenW:
                var agent = ReadStringAt(pid, arg1);
                details = $"agent=\"{agent}\" access={arg2}";
                socket = 0;
                break;

            case NetFunc.InternetConnectA:
            case NetFunc.InternetConnectW:
            case NetFunc.WinHttpConnect:
                var server = ReadStringAt(pid, arg2);
                details = $"handle=0x{arg1:X} server=\"{server}\" port={arg3}";
                socket = 0;
                break;

            case NetFunc.InternetOpenUrlA:
            case NetFunc.InternetOpenUrlW:
                var url = ReadStringAt(pid, arg2);
                details = $"handle=0x{arg1:X} url=\"{url}\"";
                socket = 0;
                break;

            case NetFunc.HttpOpenRequestA:
            case NetFunc.HttpOpenRequestW:
            case NetFunc.WinHttpOpenRequest:
                var verb = ReadStringAt(pid, arg2);
                var path = ReadStringAt(pid, arg3);
                details = $"handle=0x{arg1:X} verb=\"{verb}\" path=\"{path}\"";
                socket = 0;
                break;

            case NetFunc.HttpSendRequestA:
            case NetFunc.HttpSendRequestW:
            case NetFunc.WinHttpSendRequest:
                details = $"handle=0x{arg1:X}";
                socket = 0;
                break;

            case NetFunc.HttpQueryInfoA:
            case NetFunc.HttpQueryInfoW:
            case NetFunc.WinHttpQueryHeaders:
                details = $"handle=0x{arg1:X} infoLevel=0x{arg2:X}";
                socket = 0;
                break;

            case NetFunc.WinHttpReceiveResponse:
                details = $"handle=0x{arg1:X}";
                socket = 0;
                break;

            case NetFunc.WinHttpReadData:
            case NetFunc.InternetReadFile:
                details = $"handle=0x{arg1:X} buf=0x{arg2:X} size={arg3}";
                socket = 0;
                break;

            case NetFunc.InternetWriteFile:
            case NetFunc.WinHttpWriteData:
                dataSize = (int)arg3;
                preview = ReadDataPreview(pid, arg2, dataSize);
                details = $"handle=0x{arg1:X} buf=0x{arg2:X} len={dataSize}";
                socket = 0;
                break;

            case NetFunc.WinHttpQueryDataAvailable:
                details = $"handle=0x{arg1:X}";
                socket = 0;
                break;

            case NetFunc.WinHttpOpen:
                var userAgent = ReadStringAt(pid, arg1);
                details = $"agent=\"{userAgent}\" access={arg2}";
                socket = 0;
                break;

            // ── URLMon ──────────────────────────────────────────────────
            case NetFunc.URLDownloadToFileA:
            case NetFunc.URLDownloadToFileW:
            case NetFunc.URLDownloadToCacheFileA:
            case NetFunc.URLDownloadToCacheFileW:
                var dlUrl = ReadStringAt(pid, arg2);
                var dlPath = ReadStringAt(pid, arg3);
                details = $"url=\"{dlUrl}\" path=\"{dlPath}\"";
                socket = 0;
                break;

            default:
                details = $"arg1=0x{arg1:X} arg2=0x{arg2:X} arg3=0x{arg3:X}";
                break;
        }

        var entry = new NetEvent
        {
            Index = ++_eventIndex,
            Time = DateTime.Now.ToString("HH:mm:ss.fff"),
            ThreadId = tid,
            Function = $"{def.Module}!{def.Function}",
            Direction = def.Direction,
            Socket = socket,
            DataSize = dataSize,
            Preview = preview,
            Details = details
        };

        OnNetEvent?.Invoke(entry);

        // Set return breakpoint to capture result
        var retAddr = ReadPtr(pid, rsp);
        if (retAddr != 0 && !_returnHooks.ContainsKey(retAddr))
        {
            var rh = _api.Breakpoints.SetBreakpoint(pid, 0, retAddr, PluginBreakpointType.Software);
            if (rh.HasValue)
                _returnHooks[retAddr] = (def, entry, rh.Value);
        }

        _api.Continue();
    }

    private void HandleReturn(PluginDebugEvent evt, NetApiDef def, NetEvent entry, uint bpHandle)
    {
        var regs = _api.Memory.ReadRegisters(evt.ProcessId, evt.ThreadId);
        ulong rax = GetReg(regs, _api.Is32Bit ? "EAX" : "RAX");

        string retStr = def.Id switch
        {
            NetFunc.recv or NetFunc.recvfrom => $"{(int)rax} bytes",
            NetFunc.send or NetFunc.sendto => $"{(int)rax} bytes",
            NetFunc.accept => $"socket={rax}",
            _ => $"0x{rax:X}"
        };

        // For recv, capture the received data now
        if (def.CaptureData && def.Direction == "RECV" && (int)rax > 0)
        {
            // We need the buffer address — it was arg2, stored in details
            // Re-read isn't possible without saving state, so just update size
            entry.DataSize = (int)rax;
        }

        entry.ReturnValue = retStr;

        _api.Breakpoints.RemoveBreakpoint(bpHandle);
        _returnHooks.Remove(evt.Address);
        _api.Continue();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private string ReadDataPreview(uint pid, ulong bufAddr, int len)
    {
        if (bufAddr == 0 || len <= 0) return "";
        int readLen = Math.Min(len, 64);
        var data = _api.Memory.ReadMemory(pid, bufAddr, (uint)readLen);
        if (data == null) return "";

        // Try ASCII if printable
        bool printable = true;
        foreach (var b in data)
            if (b < 0x20 && b != 0x0A && b != 0x0D && b != 0x09) { printable = false; break; }

        if (printable)
            return Encoding.ASCII.GetString(data).Replace("\r", "").Replace("\n", "\\n");

        return BitConverter.ToString(data[..Math.Min(data.Length, 32)]).Replace("-", " ");
    }

    private string FormatSockAddr(uint pid, ulong addr)
    {
        if (addr == 0) return "<null>";
        var data = _api.Memory.ReadMemory(pid, addr, 16);
        if (data == null || data.Length < 4) return "?";

        ushort family = BitConverter.ToUInt16(data, 0);
        if (family == 2 && data.Length >= 8) // AF_INET
        {
            ushort port = (ushort)((data[2] << 8) | data[3]); // network byte order
            string ip = $"{data[4]}.{data[5]}.{data[6]}.{data[7]}";
            return $"{ip}:{port}";
        }
        return $"family={family}";
    }

    private string ReadStringAt(uint pid, ulong addr)
    {
        if (addr == 0) return "<null>";
        var data = _api.Memory.ReadMemory(pid, addr, 256);
        if (data == null) return "?";
        int end = Array.IndexOf(data, (byte)0);
        if (end < 0) end = data.Length;
        return Encoding.ASCII.GetString(data, 0, Math.Min(end, 128));
    }

    private ulong ReadPtr(uint pid, ulong addr)
    {
        int sz = _api.Is32Bit ? 4 : 8;
        var data = _api.Memory.ReadMemory(pid, addr, (uint)sz);
        if (data == null) return 0;
        return sz == 8 ? BitConverter.ToUInt64(data) : BitConverter.ToUInt32(data);
    }

    private uint ReadU32(uint pid, ulong addr)
    {
        var data = _api.Memory.ReadMemory(pid, addr, 4);
        return data != null ? BitConverter.ToUInt32(data) : 0;
    }

    private static ulong GetReg(IReadOnlyList<PluginRegister>? regs, string name)
    {
        if (regs == null) return 0;
        foreach (var r in regs)
            if (r.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return r.Value;
        return 0;
    }
}
