using System.Collections.Concurrent;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using SalesSupport.Core.Contracts;
using SalesSupport.Core.Serialization;
using SysType = System.Type;

namespace SalesSupport.Providers.Claude;

/// <summary>The model hit MaxTokens mid-output — callers that can split their input (DocExtract) catch this specifically.</summary>
public sealed class LlmOutputTruncatedException(string message) : InvalidOperationException(message);

/// <summary>
/// ILlmProvider over the Claude API (D14). Output shape is enforced with structured
/// outputs — the JSON schema is derived from T via JsonSchemaFactory, so the model
/// cannot return anything the parser rejects. System blocks carry a cache breakpoint
/// (the advisor's per-company block is the big win). Auth: ANTHROPIC_API_KEY.
/// </summary>
public sealed class ClaudeLlmProvider(ClaudeProviderOptions? options = null) : ILlmProvider
{
    private readonly AnthropicClient _client = new();
    private readonly ClaudeProviderOptions _options = options ?? new();
    private static readonly ConcurrentDictionary<SysType, Dictionary<string, JsonElement>> SchemaCache = new();

    public async Task<T> CompleteJsonAsync<T>(LlmRole role, LlmConversation conversation, CancellationToken ct = default)
        where T : class
    {
        var config = _options.Resolve(role);

        var parameters = new MessageCreateParams
        {
            Model = config.Model,
            MaxTokens = config.MaxTokens,
            System = new List<TextBlockParam>
            {
                new() { Text = conversation.System, CacheControl = new CacheControlEphemeral() },
            },
            Messages = conversation.Messages
                .Select(m => new MessageParam
                {
                    Role = m.Role == "assistant" ? Role.Assistant : Role.User,
                    Content = m.Content,
                })
                .ToList(),
            OutputConfig = BuildOutputConfig<T>(config),
        };

        var response = await _client.Messages.Create(parameters, cancellationToken: ct);

        if (_options.UsageReported is { } report)
        {
            // Cache writes are processed input too (billed 1.25x) — without them the
            // first tick of every call under-reports by the whole prefix.
            report(new LlmUsage(role, config.Model,
                response.Usage.InputTokens + (response.Usage.CacheCreationInputTokens ?? 0),
                response.Usage.CacheReadInputTokens ?? 0,
                response.Usage.OutputTokens));
        }

        if (response.StopReason == "refusal")
            throw new InvalidOperationException($"Claude declined the {role} request (stop_reason=refusal).");
        if (response.StopReason == "max_tokens")
            throw new LlmOutputTruncatedException($"{role} response was truncated at {config.MaxTokens} tokens — raise MaxTokens for this role or split the input.");

        var text = response.Content.Select(b => b.Value).OfType<TextBlock>().FirstOrDefault()?.Text
            ?? throw new InvalidOperationException($"{role} response contained no text block.");

        try
        {
            return JsonDefaults.Deserialize<T>(text);
        }
        catch (JsonException ex)
        {
            var snippet = text.Length > 400 ? text[..400] + "…" : text;
            throw new InvalidOperationException($"{role} returned JSON that failed to parse as {typeof(T).Name}: {ex.Message}\n{snippet}", ex);
        }
    }

    private static OutputConfig BuildOutputConfig<T>(ClaudeRoleConfig config) => config.Effort switch
    {
        null => new OutputConfig { Format = new JsonOutputFormat { Schema = SchemaFor(typeof(T)) } },
        _ => new OutputConfig
        {
            Format = new JsonOutputFormat { Schema = SchemaFor(typeof(T)) },
            Effort = config.Effort switch
            {
                "low" => Effort.Low,
                "medium" => Effort.Medium,
                "high" => Effort.High,
                "max" => Effort.Max,
                _ => throw new ArgumentException($"Unknown effort '{config.Effort}'"),
            },
        },
    };

    private static Dictionary<string, JsonElement> SchemaFor(SysType type) =>
        SchemaCache.GetOrAdd(type, t =>
        {
            var element = JsonSerializer.SerializeToElement(JsonSchemaFactory.For(t));
            return element.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        });
}
