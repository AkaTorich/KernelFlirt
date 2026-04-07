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
        resp.Headers.Add("Access-Control-Allow-Methods", "GET, POST, DELETE, OPTIONS");
        resp.Headers.Add("Access-Control-Allow-Headers", "Content-Type, Accept, Mcp-Session-Id, MCP-Protocol-Version");
        resp.Headers.Add("Access-Control-Expose-Headers", "Mcp-Session-Id");

        if (req.HttpMethod == "OPTIONS")
        {
            resp.StatusCode = 204;
            resp.Close();
            return;
        }

        var path = req.Url?.AbsolutePath ?? "/";
        OnActivity?.Invoke($"[{req.HttpMethod}] {path}");

        // Streamable HTTP (MCP 2025-06-18) — single /mcp endpoint
        if (path == "/mcp")
        {
            if (req.HttpMethod == "POST") await HandleMcpPost(ctx);
            else if (req.HttpMethod == "DELETE") { resp.StatusCode = 204; resp.Close(); }
            else { resp.StatusCode = 405; resp.Close(); }
        }
        // Legacy SSE transport (deprecated but kept for backwards compat)
        else if (req.HttpMethod == "GET"  && path == "/sse")     await HandleSse(ctx);
        else if (req.HttpMethod == "POST" && path == "/message") await HandleMessage(ctx);
        // OAuth endpoints (dummy auto-approve for localhost)
        else if (path == "/.well-known/oauth-authorization-server" || path == "/.well-known/oauth-authorization-server/mcp")
            await HandleOAuthMetadata(ctx);
        else if (path == "/.well-known/oauth-protected-resource" || path == "/.well-known/oauth-protected-resource/mcp")
            await HandleProtectedResourceMetadata(ctx);
        else if (path == "/register" && req.HttpMethod == "POST")
            await HandleOAuthRegister(ctx);
        else if (path == "/authorize")
            await HandleOAuthAuthorize(ctx);
        else if (path == "/token" && req.HttpMethod == "POST")
            await HandleOAuthToken(ctx);
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

    // ── Dummy OAuth (auto-approve for localhost) ────────────────────────────

    private readonly ConcurrentDictionary<string, string> _oauthClients = new(); // client_id → redirect_uri
    private readonly ConcurrentDictionary<string, string> _oauthCodes = new();   // code → client_id

    private async Task HandleOAuthMetadata(HttpListenerContext ctx)
    {
        var baseUrl = "http://localhost:13371";
        var meta = JsonSerializer.Serialize(new
        {
            issuer = baseUrl,
            authorization_endpoint = $"{baseUrl}/authorize",
            token_endpoint = $"{baseUrl}/token",
            registration_endpoint = $"{baseUrl}/register",
            response_types_supported = new[] { "code" },
            grant_types_supported = new[] { "authorization_code", "refresh_token" },
            token_endpoint_auth_methods_supported = new[] { "none" },
            code_challenge_methods_supported = new[] { "S256" },
            scopes_supported = new[] { "mcp" }
        });
        await WriteJson(ctx, meta);
        OnActivity?.Invoke("[OAuth] metadata served");
    }

    private async Task HandleProtectedResourceMetadata(HttpListenerContext ctx)
    {
        var baseUrl = "http://localhost:13371";
        var meta = JsonSerializer.Serialize(new
        {
            resource = $"{baseUrl}/mcp",
            authorization_servers = new[] { baseUrl },
            bearer_methods_supported = new[] { "header" }
        });
        await WriteJson(ctx, meta);
    }

    private async Task HandleOAuthRegister(HttpListenerContext ctx)
    {
        string body;
        using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding))
            body = await reader.ReadToEndAsync();

        var node = JsonNode.Parse(body);
        var redirectUris = node?["redirect_uris"]?.AsArray();
        var redirectUri = redirectUris?[0]?.GetValue<string>() ?? "http://localhost";

        var clientId = Guid.NewGuid().ToString("N")[..16];
        _oauthClients[clientId] = redirectUri;

        var result = JsonSerializer.Serialize(new
        {
            client_id = clientId,
            client_id_issued_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            token_endpoint_auth_method = "none",
            grant_types = new[] { "authorization_code", "refresh_token" },
            response_types = new[] { "code" },
            redirect_uris = new[] { redirectUri }
        });
        ctx.Response.StatusCode = 201;
        await WriteJson(ctx, result);
        OnActivity?.Invoke($"[OAuth] client registered: {clientId}");
    }

    private async Task HandleOAuthAuthorize(HttpListenerContext ctx)
    {
        var qs = ctx.Request.QueryString;
        var clientId = qs["client_id"] ?? "";
        var redirectUri = qs["redirect_uri"] ?? "";
        var state = qs["state"] ?? "";
        var codeChallenge = qs["code_challenge"] ?? "";

        // Auto-approve: generate code and redirect immediately
        var code = Guid.NewGuid().ToString("N");
        _oauthCodes[code] = clientId;

        var separator = redirectUri.Contains('?') ? "&" : "?";
        var location = $"{redirectUri}{separator}code={code}&state={Uri.EscapeDataString(state)}";

        ctx.Response.StatusCode = 302;
        ctx.Response.Headers.Add("Location", location);
        ctx.Response.Close();
        OnActivity?.Invoke($"[OAuth] auto-approved, redirecting");
    }

    private async Task HandleOAuthToken(HttpListenerContext ctx)
    {
        string body;
        using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding))
            body = await reader.ReadToEndAsync();

        // Issue a dummy token
        var token = Convert.ToBase64String(Guid.NewGuid().ToByteArray()).TrimEnd('=');
        var result = JsonSerializer.Serialize(new
        {
            access_token = token,
            token_type = "Bearer",
            expires_in = 86400,
            refresh_token = Convert.ToBase64String(Guid.NewGuid().ToByteArray()).TrimEnd('=')
        });
        await WriteJson(ctx, result);
        OnActivity?.Invoke("[OAuth] token issued");
    }

    private async Task WriteJson(HttpListenerContext ctx, string json)
    {
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/json";
        var data = Encoding.UTF8.GetBytes(json);
        ctx.Response.ContentLength64 = data.Length;
        await ctx.Response.OutputStream.WriteAsync(data);
        ctx.Response.Close();
    }

    // ── Streamable HTTP (MCP 2025-06-18) ────────────────────────────────────

    private string? _mcpSessionId;

    private async Task HandleMcpPost(HttpListenerContext ctx)
    {
        string body;
        using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding))
            body = await reader.ReadToEndAsync();

        // Parse to check if it's a request (has id) or notification (no id)
        JsonNode? node;
        try { node = JsonNode.Parse(body); }
        catch { ctx.Response.StatusCode = 400; ctx.Response.Close(); return; }

        var method = node?["method"]?.GetValue<string>() ?? "";
        var hasId = node?["id"] != null;

        // Handle initialize — create session
        if (method == "initialize")
        {
            _mcpSessionId = Guid.NewGuid().ToString();
            var response = await ProcessRpc(body);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            ctx.Response.Headers.Add("Mcp-Session-Id", _mcpSessionId);
            var data = Encoding.UTF8.GetBytes(response ?? "{}");
            ctx.Response.ContentLength64 = data.Length;
            await ctx.Response.OutputStream.WriteAsync(data);
            ctx.Response.Close();
            OnActivity?.Invoke($"[HTTP] initialize — session {_mcpSessionId[..8]}");
            return;
        }

        // Notifications (no id) — respond 202
        if (!hasId)
        {
            ctx.Response.StatusCode = 202;
            ctx.Response.Close();
            if (method == "initialized")
                OnActivity?.Invoke("[HTTP] initialized");
            return;
        }

        // Regular request — process and return JSON response
        var result = await ProcessRpc(body);
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/json";
        if (_mcpSessionId != null)
            ctx.Response.Headers.Add("Mcp-Session-Id", _mcpSessionId);
        var bytes = Encoding.UTF8.GetBytes(result ?? "{}");
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();

        if (method == "tools/call")
        {
            var toolName = node?["params"]?["name"]?.GetValue<string>() ?? "?";
            OnActivity?.Invoke($"[HTTP] tools/call → {toolName}");
        }
        else if (method == "tools/list")
            OnActivity?.Invoke("[HTTP] tools/list");
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
