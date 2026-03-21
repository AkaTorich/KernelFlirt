using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiAssistantPlugin;

public class ProviderPreset
{
    public string Name { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public string DefaultModel { get; set; } = "";
    public bool RequiresKey { get; set; } = true;
    public bool IsAnthropic { get; set; }
    public int MaxTokensLimit { get; set; } = 65536;

    public static readonly ProviderPreset[] All =
    [
        new() { Name = "OpenAI (ChatGPT)", Endpoint = "https://api.openai.com/v1/chat/completions", DefaultModel = "gpt-4o", RequiresKey = true, MaxTokensLimit = 16384 },
        new() { Name = "DeepSeek", Endpoint = "https://api.deepseek.com/v1/chat/completions", DefaultModel = "deepseek-chat", RequiresKey = true, MaxTokensLimit = 8192 },
        new() { Name = "Qwen", Endpoint = "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions", DefaultModel = "qwen-turbo", RequiresKey = true, MaxTokensLimit = 8192 },
        new() { Name = "Ollama (local)", Endpoint = "http://localhost:11434/v1/chat/completions", DefaultModel = "llama3", RequiresKey = false, MaxTokensLimit = 65536 },
        new() { Name = "LM Studio (local)", Endpoint = "http://localhost:1234/v1/chat/completions", DefaultModel = "default", RequiresKey = false, MaxTokensLimit = 65536 },
        new() { Name = "Anthropic (Claude)", Endpoint = "https://api.anthropic.com/v1/messages", DefaultModel = "claude-sonnet-4-20250514", RequiresKey = true, IsAnthropic = true, MaxTokensLimit = 64000 },
        new() { Name = "Custom", Endpoint = "", DefaultModel = "", RequiresKey = false, MaxTokensLimit = 65536 },
    ];
}

public class AiSettings
{
    public string ProviderName { get; set; } = "Ollama (local)";
    public string Endpoint { get; set; } = "http://localhost:11434/v1/chat/completions";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "llama3";
    public bool IsAnthropic { get; set; }
    public int MaxTokens { get; set; } = 8192;
    public double Temperature { get; set; } = 0.3;
    public string SystemPrompt { get; set; } = DefaultSystemPrompt;

    // Context toggles
    public bool IncludeRegisters { get; set; } = true;
    public bool IncludeDisasm { get; set; } = true;
    public bool IncludeModules { get; set; }
    public bool IncludeStack { get; set; }
    public bool IncludeThreads { get; set; }
    public bool IncludeBreakpoints { get; set; }

    public const string DefaultSystemPrompt =
        """
        Expert reverse engineer in KernelFlirt debugger (Windows x64).
        USE tools to act — don't suggest commands. Always give a text answer after tool calls.
        Prefer decompile over disassemble. After continue/run call wait_for_break before reading state.
        Be concise. Respond in user's language.
        """;

    private static string GetSettingsPath()
    {
        var dir = Path.GetDirectoryName(typeof(AiSettings).Assembly.Location) ?? ".";
        return Path.Combine(dir, "ai_settings.json");
    }

    public static AiSettings Load()
    {
        try
        {
            var path = GetSettingsPath();
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<AiSettings>(json) ?? new AiSettings();
            }
        }
        catch { }
        return new AiSettings();
    }

    public void Save()
    {
        try
        {
            var path = GetSettingsPath();
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch { }
    }
}
