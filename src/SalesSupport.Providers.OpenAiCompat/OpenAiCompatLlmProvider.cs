using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SalesSupport.Core.Contracts;
using SalesSupport.Core.Serialization;

namespace SalesSupport.Providers.OpenAiCompat;

public sealed class OpenAiCompatRoleConfig
{
    public required string Model { get; init; }
    public double Temperature { get; init; } = 0.2;

    /// <summary>Hard output cap — same repetition-loop insurance as the Ollama provider.</summary>
    public int MaxTokens { get; init; } = 2048;
}

/// <summary>
/// Configuration for any OpenAI-chat-completions-compatible endpoint: OpenRouter (and
/// through it GLM/DeepSeek/Qwen/…), Gemini's compat endpoint, or self-hosted vLLM.
/// The D31 bench gateway — every cheap-model candidate is a config, not a provider.
/// </summary>
public sealed class OpenAiCompatProviderOptions
{
    /// <summary>API root, e.g. https://openrouter.ai/api/v1 — /chat/completions is appended.</summary>
    public required Uri BaseUrl { get; init; }

    /// <summary>Bearer token; null for keyless local servers (vLLM).</summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// true: response_format json_schema with strict decoding (OpenRouter, vLLM, most
    /// modern endpoints). false: json_object mode with the schema stated in the system
    /// prompt — the fallback for endpoints that reject json_schema.
    /// </summary>
    public bool StrictSchema { get; init; } = true;

    /// <summary>Invoked after every completed call with its token usage (D31 cost ledger).</summary>
    public Action<LlmUsage>? UsageReported { get; init; }

    public OpenAiCompatRoleConfig Gate { get; init; } = new() { Model = "", Temperature = 0.1 };
    public OpenAiCompatRoleConfig Advisor { get; init; } = new() { Model = "", Temperature = 0.3 };
    public OpenAiCompatRoleConfig Summarizer { get; init; } = new() { Model = "", Temperature = 0.3 };
    public OpenAiCompatRoleConfig Drafter { get; init; } = new() { Model = "", Temperature = 0.4, MaxTokens = 4096 };

    /// <summary>One model for every role — the typical bench setup.</summary>
    public static OpenAiCompatProviderOptions ForModel(
        Uri baseUrl, string? apiKey, string model, Action<LlmUsage>? usageReported = null, bool strictSchema = true) => new()
    {
        BaseUrl = baseUrl,
        ApiKey = apiKey,
        StrictSchema = strictSchema,
        UsageReported = usageReported,
        Gate = new OpenAiCompatRoleConfig { Model = model, Temperature = 0.1 },
        Advisor = new OpenAiCompatRoleConfig { Model = model, Temperature = 0.3 },
        Summarizer = new OpenAiCompatRoleConfig { Model = model, Temperature = 0.3 },
        Drafter = new OpenAiCompatRoleConfig { Model = model, Temperature = 0.4, MaxTokens = 4096 },
    };

    public OpenAiCompatRoleConfig Resolve(LlmRole role) => role switch
    {
        LlmRole.Gate => Gate,
        LlmRole.Advisor => Advisor,
        LlmRole.Summarizer => Summarizer,
        LlmRole.Drafter => Drafter,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };
}

/// <summary>
/// ILlmProvider over the OpenAI chat-completions dialect (D31). Output shape is enforced
/// via response_format json_schema (strict) where supported, falling back to json_object
/// with the schema in the prompt. Same schemas as every other provider, derived from the
/// same serializer options.
/// </summary>
public sealed class OpenAiCompatLlmProvider(OpenAiCompatProviderOptions options) : ILlmProvider
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public async Task<T> CompleteJsonAsync<T>(LlmRole role, LlmConversation conversation, CancellationToken ct = default)
        where T : class
    {
        var config = options.Resolve(role);
        if (config.Model.Length == 0)
            throw new InvalidOperationException($"OpenAI-compat provider has no model configured for role {role}.");

        var request = BuildRequest(config, conversation, JsonSchemaFactory.For<T>(), typeof(T).Name, options.StrictSchema);
        using var content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json");
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(options.BaseUrl.AbsoluteUri.TrimEnd('/') + "/chat/completions"))
        {
            Content = content,
        };
        if (options.ApiKey is { Length: > 0 } key)
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(message, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Cannot reach OpenAI-compatible endpoint at {options.BaseUrl} — check the URL and network.", ex);
        }

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var hint = (int)response.StatusCode switch
            {
                401 or 403 => " (check the API key)",
                404 => $" (check the model name '{config.Model}' and that BaseUrl points at the API root, e.g. …/v1)",
                _ when body.Contains("response_format", StringComparison.OrdinalIgnoreCase) =>
                    " (this endpoint may not support json_schema — retry with StrictSchema=false / --compat-loose)",
                _ => "",
            };
            throw new InvalidOperationException($"OpenAI-compat {role} request failed ({(int)response.StatusCode}): {Truncate(body)}{hint}");
        }

        var (text, usage) = ParseResponse(body);
        if (usage is not null && options.UsageReported is { } report)
            report(new LlmUsage(role, config.Model, usage.Value.Input, usage.Value.Cached, usage.Value.Output));

        try
        {
            return JsonDefaults.Deserialize<T>(text);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"OpenAI-compat {role} ({config.Model}) returned JSON that failed to parse as {typeof(T).Name}: {ex.Message}\n{Truncate(text)}", ex);
        }
    }

    internal static JsonObject BuildRequest(
        OpenAiCompatRoleConfig config, LlmConversation conversation, JsonNode schema, string schemaName, bool strictSchema)
    {
        var system = conversation.System;
        if (!strictSchema)
            system += "\n\nRespond with ONLY a single JSON object that exactly follows this JSON Schema (no other text):\n" +
                      schema.ToJsonString();

        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = system },
        };
        foreach (var message in conversation.Messages)
            messages.Add(new JsonObject { ["role"] = message.Role, ["content"] = message.Content });

        return new JsonObject
        {
            ["model"] = config.Model,
            ["messages"] = messages,
            ["stream"] = false,
            ["temperature"] = config.Temperature,
            ["max_tokens"] = config.MaxTokens,
            ["response_format"] = strictSchema
                ? new JsonObject
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new JsonObject
                    {
                        ["name"] = schemaName,
                        ["strict"] = true,
                        ["schema"] = schema,
                    },
                }
                : new JsonObject { ["type"] = "json_object" },
        };
    }

    internal static (string Text, (long Input, long Cached, long Output)? Usage) ParseResponse(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var text = root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()
            ?? throw new InvalidOperationException("OpenAI-compat response had no message content.");
        text = StripFences(text);

        (long, long, long)? usage = null;
        if (root.TryGetProperty("usage", out var u))
        {
            long prompt = u.TryGetProperty("prompt_tokens", out var p) ? p.GetInt64() : 0;
            long output = u.TryGetProperty("completion_tokens", out var c) ? c.GetInt64() : 0;
            long cached = u.TryGetProperty("prompt_tokens_details", out var d) &&
                          d.ValueKind == JsonValueKind.Object &&
                          d.TryGetProperty("cached_tokens", out var ct) ? ct.GetInt64() : 0;
            // OpenAI's prompt_tokens includes cached tokens; our ledger keeps them apart.
            usage = (prompt - cached, cached, output);
        }
        return (text, usage);
    }

    /// <summary>json_object mode on weaker endpoints sometimes wraps the payload in ```json fences.</summary>
    internal static string StripFences(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal)) return trimmed;
        var firstNewline = trimmed.IndexOf('\n');
        var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        return firstNewline >= 0 && lastFence > firstNewline
            ? trimmed[(firstNewline + 1)..lastFence].Trim()
            : trimmed;
    }

    private static string Truncate(string text) => text.Length > 400 ? text[..400] + "…" : text;
}
