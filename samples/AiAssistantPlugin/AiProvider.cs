using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AiAssistantPlugin;

public class ChatMessage
{
    public string Role { get; set; } = "user";   // "system", "user", "assistant", "tool"
    public string Content { get; set; } = "";
    public List<ToolCall>? ToolCalls { get; set; }
    public string? ToolCallId { get; set; }   // for role="tool" responses
}

public class ToolCall
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Arguments { get; set; } = "";
}

public class AiProvider : IDisposable
{
    private readonly HttpClient _http;
    private CancellationTokenSource? _cts;

    public AiProvider()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    }

    public void Cancel()
    {
        _cts?.Cancel();
    }

    /// <summary>
    /// Send chat with streaming. Returns list of tool calls if AI wants to use tools, otherwise null.
    /// </summary>
    public async Task<List<ToolCall>?> StreamChatAsync(
        AiSettings settings,
        List<ChatMessage> messages,
        object[]? tools,
        Action<string> onToken,
        Action<string?> onError)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            List<ToolCall>? toolCalls;
            if (settings.IsAnthropic)
                toolCalls = await StreamAnthropicAsync(settings, messages, tools, onToken, ct);
            else
                toolCalls = await StreamOpenAiAsync(settings, messages, tools, onToken, ct);

            onError(null);
            return toolCalls;
        }
        catch (OperationCanceledException)
        {
            onError(null);
            return null;
        }
        catch (Exception ex)
        {
            onError(ex.Message);
            return null;
        }
    }

    private async Task<List<ToolCall>?> StreamOpenAiAsync(
        AiSettings settings, List<ChatMessage> messages, object[]? tools,
        Action<string> onToken, CancellationToken ct)
    {
        // Build messages array with tool call/result support
        var msgArray = new List<object>();
        foreach (var m in messages)
        {
            if (m.Role == "tool")
            {
                msgArray.Add(new { role = "tool", content = m.Content, tool_call_id = m.ToolCallId });
            }
            else if (m.ToolCalls != null && m.ToolCalls.Count > 0)
            {
                // Assistant message with tool calls
                msgArray.Add(new
                {
                    role = "assistant",
                    content = (string?)null,
                    tool_calls = m.ToolCalls.Select(tc => new
                    {
                        id = tc.Id,
                        type = "function",
                        function = new { name = tc.Name, arguments = tc.Arguments }
                    }).ToArray()
                });
            }
            else
            {
                msgArray.Add(new { role = m.Role, content = m.Content });
            }
        }

        var bodyDict = new Dictionary<string, object>
        {
            ["model"] = settings.Model,
            ["messages"] = msgArray,
            ["max_tokens"] = settings.MaxTokens,
            ["temperature"] = settings.Temperature,
            ["stream"] = true
        };
        if (tools != null && tools.Length > 0)
            bodyDict["tools"] = tools;

        using var req = new HttpRequestMessage(HttpMethod.Post, settings.Endpoint);
        req.Content = new StringContent(JsonSerializer.Serialize(bodyDict), Encoding.UTF8, "application/json");

        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new Exception($"API {(int)resp.StatusCode}: {err}");
        }

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        // Accumulate tool calls from streaming deltas
        var accToolCalls = new Dictionary<int, ToolCall>(); // index -> ToolCall
        bool hasToolCalls = false;

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (line == null) break;

            if (!line.StartsWith("data: ")) continue;
            var data = line[6..];
            if (data == "[DONE]") break;

            try
            {
                using var doc = JsonDocument.Parse(data);
                var choices = doc.RootElement.GetProperty("choices");
                if (choices.GetArrayLength() == 0) continue;

                var choice = choices[0];
                var delta = choice.GetProperty("delta");

                // Check for content
                if (delta.TryGetProperty("content", out var content))
                {
                    var text = content.GetString();
                    if (!string.IsNullOrEmpty(text))
                        onToken(text);
                }

                // Check for tool_calls
                if (delta.TryGetProperty("tool_calls", out var toolCallsArr))
                {
                    hasToolCalls = true;
                    foreach (var tc in toolCallsArr.EnumerateArray())
                    {
                        var idx = tc.GetProperty("index").GetInt32();
                        if (!accToolCalls.ContainsKey(idx))
                            accToolCalls[idx] = new ToolCall();

                        if (tc.TryGetProperty("id", out var idProp))
                        {
                            var id = idProp.GetString();
                            if (!string.IsNullOrEmpty(id))
                                accToolCalls[idx].Id = id;
                        }

                        if (tc.TryGetProperty("function", out var fn))
                        {
                            if (fn.TryGetProperty("name", out var nameProp))
                            {
                                var name = nameProp.GetString();
                                if (!string.IsNullOrEmpty(name))
                                    accToolCalls[idx].Name = name;
                            }
                            if (fn.TryGetProperty("arguments", out var argsProp))
                            {
                                var a = argsProp.GetString();
                                if (a != null)
                                    accToolCalls[idx].Arguments += a;
                            }
                        }
                    }
                }
            }
            catch { }
        }

        if (hasToolCalls && accToolCalls.Count > 0)
            return accToolCalls.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();

        return null;
    }

    private async Task<List<ToolCall>?> StreamAnthropicAsync(
        AiSettings settings, List<ChatMessage> messages, object[]? tools,
        Action<string> onToken, CancellationToken ct)
    {
        string? systemPrompt = null;
        var apiMessages = new List<object>();

        foreach (var msg in messages)
        {
            if (msg.Role == "system")
            {
                systemPrompt = msg.Content;
                continue;
            }

            if (msg.Role == "tool")
            {
                apiMessages.Add(new
                {
                    role = "user",
                    content = new[] { new { type = "tool_result", tool_use_id = msg.ToolCallId, content = msg.Content } }
                });
            }
            else if (msg.ToolCalls != null && msg.ToolCalls.Count > 0)
            {
                var contentBlocks = new List<object>();
                foreach (var tc in msg.ToolCalls)
                {
                    contentBlocks.Add(new
                    {
                        type = "tool_use",
                        id = tc.Id,
                        name = tc.Name,
                        input = JsonSerializer.Deserialize<object>(tc.Arguments)
                    });
                }
                apiMessages.Add(new { role = "assistant", content = contentBlocks });
            }
            else
            {
                apiMessages.Add(new { role = msg.Role, content = msg.Content });
            }
        }

        var bodyDict = new Dictionary<string, object>
        {
            ["model"] = settings.Model,
            ["messages"] = apiMessages,
            ["max_tokens"] = settings.MaxTokens,
            ["stream"] = true
        };
        if (systemPrompt != null)
            bodyDict["system"] = systemPrompt;

        if (tools != null && tools.Length > 0)
        {
            // Convert OpenAI tool format to Anthropic format
            var anthropicTools = tools.Select(t =>
            {
                var json = JsonSerializer.Serialize(t);
                using var doc = JsonDocument.Parse(json);
                var fn = doc.RootElement.GetProperty("function");
                return new Dictionary<string, object>
                {
                    ["name"] = fn.GetProperty("name").GetString()!,
                    ["description"] = fn.GetProperty("description").GetString()!,
                    ["input_schema"] = JsonSerializer.Deserialize<object>(fn.GetProperty("parameters").GetRawText())!
                };
            }).ToArray();
            bodyDict["tools"] = anthropicTools;
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, settings.Endpoint);
        req.Content = new StringContent(JsonSerializer.Serialize(bodyDict), Encoding.UTF8, "application/json");
        req.Headers.Add("x-api-key", settings.ApiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new Exception($"Anthropic {(int)resp.StatusCode}: {err}");
        }

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        var toolCalls = new List<ToolCall>();
        string? currentToolId = null;
        string? currentToolName = null;
        var currentToolArgs = new StringBuilder();
        bool inToolUse = false;

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (line == null) break;

            if (!line.StartsWith("data: ")) continue;
            var data = line[6..];

            try
            {
                using var doc = JsonDocument.Parse(data);
                var type = doc.RootElement.GetProperty("type").GetString();

                if (type == "content_block_start")
                {
                    var cb = doc.RootElement.GetProperty("content_block");
                    var cbType = cb.GetProperty("type").GetString();
                    if (cbType == "tool_use")
                    {
                        inToolUse = true;
                        currentToolId = cb.GetProperty("id").GetString();
                        currentToolName = cb.GetProperty("name").GetString();
                        currentToolArgs.Clear();
                    }
                }
                else if (type == "content_block_delta")
                {
                    var delta = doc.RootElement.GetProperty("delta");
                    var deltaType = delta.GetProperty("type").GetString();

                    if (deltaType == "text_delta")
                    {
                        if (delta.TryGetProperty("text", out var text))
                        {
                            var t = text.GetString();
                            if (!string.IsNullOrEmpty(t))
                                onToken(t);
                        }
                    }
                    else if (deltaType == "input_json_delta" && inToolUse)
                    {
                        if (delta.TryGetProperty("partial_json", out var pj))
                            currentToolArgs.Append(pj.GetString());
                    }
                }
                else if (type == "content_block_stop" && inToolUse)
                {
                    toolCalls.Add(new ToolCall
                    {
                        Id = currentToolId ?? "",
                        Name = currentToolName ?? "",
                        Arguments = currentToolArgs.ToString()
                    });
                    inToolUse = false;
                }
                else if (type == "message_stop")
                {
                    break;
                }
            }
            catch { }
        }

        return toolCalls.Count > 0 ? toolCalls : null;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _http.Dispose();
    }
}
