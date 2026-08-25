using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SalesSupport.Core.Contracts;
using SalesSupport.Core.Serialization;

namespace SalesSupport.Providers.Ollama;

public sealed class OllamaRoleConfig
{
    public required string Model { get; init; }
    public int NumCtx { get; init; } = 8192;
    public double Temperature { get; init; } = 0.2;
}

/// <summary>
/// Role → local model mapping. Defaults assume a mid-range GPU; override per installation.
/// qwen2.5 is a solid multilingual default without thinking-mode complications.
/// </summary>
public sealed class OllamaProviderOptions
{
    public Uri BaseUrl { get; init; } =
        new(Environment.GetEnvironmentVariable("OLLAMA_HOST") is { Length: > 0 } host
            ? (host.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? host : $"http://{host}")
            : "http://localhost:11434");

    /// <summary>Keeps the model resident between ticks — cold loads would blow the latency budget.</summary>
    public string KeepAlive { get; init; } = "15m";

    public OllamaRoleConfig Gate { get; init; } = new() { Model = "qwen2.5:7b", NumCtx = 8192, Temperature = 0.1 };
    public OllamaRoleConfig Advisor { get; init; } = new() { Model = "qwen2.5:7b", NumCtx = 16384, Temperature = 0.3 };
    public OllamaRoleConfig Summarizer { get; init; } = new() { Model = "qwen2.5:7b", NumCtx = 16384, Temperature = 0.3 };
    public OllamaRoleConfig Drafter { get; init; } = new() { Model = "qwen2.5:7b", NumCtx = 16384, Temperature = 0.4 };

    public OllamaRoleConfig Resolve(LlmRole role) => role switch
    {
        LlmRole.Gate => Gate,
        LlmRole.Advisor => Advisor,
        LlmRole.Summarizer => Summarizer,
        LlmRole.Drafter => Drafter,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };
}

/// <summary>
/// ILlmProvider over a local Ollama server (D14/D27: the fixed-cost, on-prem inference
/// path). Output shape is enforced by passing the JSON schema for T as Ollama's `format`
/// parameter (grammar-constrained decoding) — the same schemas the Claude provider uses,
/// derived from the same serializer options. No cloud, no keys, zero marginal cost.
/// </summary>
public sealed class OllamaLlmProvider(OllamaProviderOptions? options = null) : ILlmProvider
{
    private readonly OllamaProviderOptions _options = options ?? new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public async Task<T> CompleteJsonAsync<T>(LlmRole role, LlmConversation conversation, CancellationToken ct = default)
        where T : class
    {
        var config = _options.Resolve(role);
        var request = BuildRequest(config, _options.KeepAlive, conversation, JsonSchemaFactory.For<T>());

        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsync(
                new Uri(_options.BaseUrl, "/api/chat"),
                new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"),
                ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(
                $"Cannot reach Ollama at {_options.BaseUrl} — is it running? Install from ollama.com, then `ollama pull {config.Model}`.", ex);
        }

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var hint = body.Contains("not found", StringComparison.OrdinalIgnoreCase)
                ? $" (try: ollama pull {config.Model})"
                : "";
            throw new InvalidOperationException($"Ollama {role} request failed ({(int)response.StatusCode}): {Truncate(body)}{hint}");
        }

        var content = ParseResponseContent(body);
        try
        {
            return JsonDefaults.Deserialize<T>(content);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Ollama {role} ({config.Model}) returned JSON that failed to parse as {typeof(T).Name}: {ex.Message}\n{Truncate(content)}", ex);
        }
    }

    internal static JsonObject BuildRequest(
        OllamaRoleConfig config, string keepAlive, LlmConversation conversation, JsonNode schema)
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = conversation.System },
        };
        foreach (var message in conversation.Messages)
            messages.Add(new JsonObject { ["role"] = message.Role, ["content"] = message.Content });

        return new JsonObject
        {
            ["model"] = config.Model,
            ["messages"] = messages,
            ["stream"] = false,
            ["format"] = schema,
            ["keep_alive"] = keepAlive,
            ["options"] = new JsonObject
            {
                ["num_ctx"] = config.NumCtx,
                ["temperature"] = config.Temperature,
            },
        };
    }

    internal static string ParseResponseContent(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("message").GetProperty("content").GetString()
            ?? throw new InvalidOperationException("Ollama response had no message content.");
    }

    private static string Truncate(string text) => text.Length > 400 ? text[..400] + "…" : text;
}
