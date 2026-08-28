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

    /// <summary>
    /// Ollama's thinking switch. false disables thinking on thinking models (qwen3 —
    /// required to hold the gate's latency budget); null omits the field (required for
    /// models without a thinking mode, which reject the parameter).
    /// </summary>
    public bool? Think { get; init; }

    /// <summary>
    /// Hard output-token cap. Small models degenerate into repetition loops on long
    /// free-text generation (observed: a summarizer looping one sentence for minutes);
    /// Ollama's default is unbounded, so every role gets a ceiling.
    /// </summary>
    public int NumPredict { get; init; } = 1024;
}

/// <summary>
/// Role → local model mapping. Default: qwen3:8b with thinking disabled — the strongest
/// single model that fits an 8 GB consumer GPU. One model serves all roles on purpose:
/// two resident models thrash-swap on small VRAM. Use ForModel to benchmark alternatives.
/// </summary>
public sealed class OllamaProviderOptions
{
    public Uri BaseUrl { get; init; } =
        new(Environment.GetEnvironmentVariable("OLLAMA_HOST") is { Length: > 0 } host
            ? (host.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? host : $"http://{host}")
            : "http://localhost:11434");

    /// <summary>Keeps the model resident between ticks — cold loads would blow the latency budget.</summary>
    public string KeepAlive { get; init; } = "15m";

    // Context windows sized for an 8 GB GPU: the model (~5 GB) plus KV cache must stay
    // on-GPU or Ollama spills to CPU and every call crawls. 16k ctx on qwen3:8b costs
    // ~2-3 GB KV — too much. Gate is 6k since the D31 cache restructure grew its system
    // prompt (examples + catalog) past what 4k holds with generation headroom. Raise
    // further only for very large catalog maps, and check `ollama ps` says 100% GPU.
    public OllamaRoleConfig Gate { get; init; } = new() { Model = "qwen3:8b", NumCtx = 6144, Temperature = 0.1, Think = false };
    public OllamaRoleConfig Advisor { get; init; } = new() { Model = "qwen3:8b", NumCtx = 8192, Temperature = 0.3, Think = false };
    public OllamaRoleConfig Summarizer { get; init; } = new() { Model = "qwen3:8b", NumCtx = 8192, Temperature = 0.3, Think = false };
    public OllamaRoleConfig Drafter { get; init; } = new() { Model = "qwen3:8b", NumCtx = 8192, Temperature = 0.4, Think = false };

    /// <summary>Per-call performance lines ("gate qwen3:8b: prompt 2381 tok in 6.2s …") — wire to a logger.</summary>
    public Action<string>? Diagnostics { get; init; }

    /// <summary>One model for every role — think=false for thinking models (qwen3), null for the rest.</summary>
    public static OllamaProviderOptions ForModel(string model, bool? think, Action<string>? diagnostics = null) => new()
    {
        Gate = new OllamaRoleConfig { Model = model, NumCtx = 6144, Temperature = 0.1, Think = think },
        Advisor = new OllamaRoleConfig { Model = model, NumCtx = 8192, Temperature = 0.3, Think = think },
        Summarizer = new OllamaRoleConfig { Model = model, NumCtx = 8192, Temperature = 0.3, Think = think },
        Drafter = new OllamaRoleConfig { Model = model, NumCtx = 8192, Temperature = 0.4, Think = think },
        Diagnostics = diagnostics,
    };

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
            var hint = "";
            if (body.Contains("not found", StringComparison.OrdinalIgnoreCase))
                hint = $" (try: ollama pull {config.Model})";
            else if (body.Contains("does not support thinking", StringComparison.OrdinalIgnoreCase))
                hint = " (this model has no thinking mode — leave Think unset for it: Backend:OllamaNoThink=false or drop --ollama-nothink)";
            throw new InvalidOperationException($"Ollama {role} request failed ({(int)response.StatusCode}): {Truncate(body)}{hint}");
        }

        if (_options.Diagnostics is { } diagnostics)
            diagnostics($"{role} {config.Model}: {DescribeTimings(body)}");

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

        var request = new JsonObject
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
                ["num_predict"] = config.NumPredict,
                ["repeat_penalty"] = 1.1,
            },
        };
        if (config.Think is { } think) request["think"] = think;
        return request;
    }

    /// <summary>Ollama's per-call stats: prompt-eval vs generation split shows where time goes.</summary>
    internal static string DescribeTimings(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        double Seconds(string name) => root.TryGetProperty(name, out var v) ? v.GetInt64() / 1e9 : 0;
        long Count(string name) => root.TryGetProperty(name, out var v) ? v.GetInt64() : 0;
        return $"prompt {Count("prompt_eval_count")} tok in {Seconds("prompt_eval_duration"):F1}s, " +
               $"gen {Count("eval_count")} tok in {Seconds("eval_duration"):F1}s, " +
               $"load {Seconds("load_duration"):F1}s, total {Seconds("total_duration"):F1}s";
    }

    internal static string ParseResponseContent(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("message").GetProperty("content").GetString()
            ?? throw new InvalidOperationException("Ollama response had no message content.");
    }

    private static string Truncate(string text) => text.Length > 400 ? text[..400] + "…" : text;
}
