using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using KernelFlirt.SDK;

namespace McpServerPlugin;

/// <summary>
/// Embedded HTTP server that implements the MCP SSE transport.
///
/// Protocol:
///   GET  /sse              → opens SSE stream; sends "endpoint" event with /message?sessionId=xxx
///   POST /message?sessionId → receives JSON-RPC 2.0 request; sends response back via SSE stream
///
/// Add to .mcp.json:
///   "kf-debugger": { "url": "http://localhost:13371/sse" }
/// </summary>
public class McpHttpServer
{
    private readonly HttpListener _listener;
    private readonly McpDebuggerTools _tools;
    private readonly ConcurrentDictionary<string, SseSession> _sessions = new();
    private volatile bool _running;
    private Thread? _thread;

    /// <summary>Fired on the thread pool for each notable server event (connect, call, error).</summary>
    public event Action<string>? OnActivity;

    public McpHttpServer(IDebuggerApi api, int port = 13371)
    {
        _tools = new McpDebuggerTools(api);
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{port}/");
    }

    public void Start()
    {
        _running = true;
        _listener.Start();
        _thread = new Thread(AcceptLoop) { IsBackground = true, Name = "MCP-Listener" };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        try { _listener.Stop(); } catch { }
        foreach (var s in _sessions.Values)
            s.Dispose();
        _sessions.Clear();
    }

    // ── Accept loop ─────────────────────────────────────────────────────────

    private void AcceptLoop()
    {
        while (_running)
        {
            try
            {
                var ctx = _listener.GetContext();
                _ = Task.Run(() => RouteRequest(ctx));
            }
            catch when (!_running) { break; }
            catch { /* swallow transient errors */ }
        }
    }

    private async Task RouteRequest(HttpListenerContext ctx)
    {
        var req  = ctx.Request;
        var resp = ctx.Response;

        resp.Headers.Add("Access-Control-Allow-Origin",  "*");
        resp.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
        resp.Headers.Add("Access-Control-Allow-Headers", "Content-Type, Accept");

        if (req.HttpMethod == "OPTIONS")
        {
            resp.StatusCode = 204;
            resp.Close();
            return;
        }

        var path = req.Url?.AbsolutePath ?? "/";

        if (req.HttpMethod == "GET"  && path == "/sse")     await HandleSse(ctx);
        else if (req.HttpMethod == "POST" && path == "/message") await HandleMessage(ctx);
        else { resp.StatusCode = 404; resp.Close(); }
    }

    // ── SSE endpoint ─────────────────────────────────────────────────────────

    private async Task HandleSse(HttpListenerContext ctx)
    {
        var sessionId = Guid.NewGuid().ToString("N")[..12];
        using var session = new SseSession();
        _sessions[sessionId] = session;

        var resp = ctx.Response;
        resp.StatusCode  = 200;
        resp.ContentType = "text/event-stream; charset=utf-8";
        resp.Headers.Add("Cache-Control",    "no-cache, no-store");
        resp.Headers.Add("X-Accel-Buffering","no");
        resp.SendChunked = true;

        OnActivity?.Invoke($"Client connected (session {sessionId}, {_sessions.Count} active)");
        try
        {
            await using var writer = new StreamWriter(resp.OutputStream, new UTF8Encoding(false), leaveOpen: true);

            // Tell the client which URL to POST requests to
            await writer.WriteAsync($"event: endpoint\ndata: /message?sessionId={sessionId}\n\n");
            await writer.FlushAsync();

            // Stream responses until the client disconnects or the plugin shuts down
            await foreach (var msg in session.Reader.ReadAllAsync(session.Token))
            {
                await writer.WriteAsync($"event: message\ndata: {msg}\n\n");
                await writer.FlushAsync();
            }
        }
        catch { /* client disconnected */ }
        finally
        {
            _sessions.TryRemove(sessionId, out _);
            OnActivity?.Invoke($"Client disconnected (session {sessionId})");
            try { resp.Close(); } catch { }
        }
    }

    // ── Message endpoint ─────────────────────────────────────────────────────

    private async Task HandleMessage(HttpListenerContext ctx)
    {
        var sessionId = ctx.Request.QueryString["sessionId"] ?? "";

        string body;
        using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding))
            body = await reader.ReadToEndAsync();

        // Acknowledge immediately — response comes via SSE
        ctx.Response.StatusCode = 202;
        ctx.Response.Close();

        if (!_sessions.TryGetValue(sessionId, out var session))
            return;

        _ = Task.Run(async () =>
        {
            var response = await ProcessRpc(body);
            if (response != null)
                session.Writer.TryWrite(response);
        });
    }

    // ── JSON-RPC 2.0 dispatcher ──────────────────────────────────────────────

    private async Task<string?> ProcessRpc(string body)
    {
        JsonNode? req;
        try { req = JsonNode.Parse(body); }
        catch { return null; }

        if (req is null) return null;

        var id     = req["id"];
        var method = req["method"]?.GetValue<string>() ?? "";
        var params_ = req["params"];

        // Notifications have no "id" → no response
        if (id is null) return null;

        try
        {
            object result = method switch
            {
                "initialize"             => RpcInitialize(),
                "tools/list"             => RpcToolsList(),
                "tools/call"             => await RpcToolsCall(params_),
                "ping"                   => new { },
                _ => throw new InvalidOperationException($"Method not found: {method}")
            };

            if (method == "tools/call")
            {
                var toolName = params_?["name"]?.GetValue<string>() ?? "?";
                OnActivity?.Invoke($"tools/call → {toolName}");
            }
            else if (method == "initialize")
            {
                var clientName = params_?["clientInfo"]?["name"]?.GetValue<string>() ?? "unknown";
                OnActivity?.Invoke($"initialize ← {clientName}");
            }
            else if (method == "tools/list")
            {
                OnActivity?.Invoke("tools/list requested");
            }

            return Respond(id, result: result);
        }
        catch (Exception ex)
        {
            OnActivity?.Invoke($"ERROR {method}: {ex.Message}");
            return Respond(id, error: new { code = -32601, message = ex.Message });
        }
    }

    // ── RPC handlers ─────────────────────────────────────────────────────────

    private static object RpcInitialize() => new
    {
        protocolVersion = "2024-11-05",
        capabilities    = new { tools = new { } },
        serverInfo      = new { name = "kf-debugger", version = "1.0.0" },
        instructions    = "KernelFlirt kernel debugger. " +
            "When analyzing a program: ALWAYS use 'decompile' first on key functions (entry, main) " +
            "to get C pseudocode — this is far more informative than raw disassembly. " +
            "Use 'disassemble' only for small snippets or when decompile fails. " +
            "Start with get_debugger_state + list_modules, then decompile the entry point. " +
            "Use read_string/read_unicode_string to resolve string references from decompiled code."
    };

    private object RpcToolsList() => new
    {
        tools = _tools.GetToolDefinitions()
    };

    private async Task<object> RpcToolsCall(JsonNode? p)
    {
        var name      = p?["name"]?.GetValue<string>()    ?? "";
        var arguments = p?["arguments"]?.ToJsonString()   ?? "{}";

        var text = await Task.Run(() => _tools.Execute(name, arguments));

        return new
        {
            content  = new[] { new { type = "text", text } },
            isError  = false
        };
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = false };

    private static string Respond(JsonNode id, object? result = null, object? error = null)
    {
        var obj = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"]      = id.DeepClone()
        };

        if (error is not null)
            obj["error"]  = JsonSerializer.SerializeToNode(error,  _jsonOpts);
        else
            obj["result"] = JsonSerializer.SerializeToNode(result, _jsonOpts);

        return obj.ToJsonString();
    }
}

// ── SSE session ───────────────────────────────────────────────────────────────

internal sealed class SseSession : IDisposable
{
    private readonly Channel<string>            _ch  = Channel.CreateUnbounded<string>();
    private readonly CancellationTokenSource    _cts = new();

    public ChannelReader<string> Reader => _ch.Reader;
    public ChannelWriter<string> Writer => _ch.Writer;
    public CancellationToken     Token  => _cts.Token;

    public void Dispose()
    {
        _cts.Cancel();
        _ch.Writer.TryComplete();
        _cts.Dispose();
    }
}
