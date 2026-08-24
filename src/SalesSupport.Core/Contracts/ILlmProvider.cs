namespace SalesSupport.Core.Contracts;

/// <summary>
/// Model roles (D12, D30). Providers map roles to concrete models
/// (Claude: gate = Haiku-class, advisor/drafter = Sonnet/Opus-class).
/// </summary>
public enum LlmRole { Gate, Advisor, Summarizer, Drafter }

public sealed record LlmMessage(string Role, string Content)
{
    public static LlmMessage User(string content) => new("user", content);
    public static LlmMessage Assistant(string content) => new("assistant", content);
}

public sealed record LlmConversation(string System, IReadOnlyList<LlmMessage> Messages);

/// <summary>
/// The hard boundary of D14: chat completion + strict JSON output, nothing provider-specific.
/// Implementations enforce the JSON schema for T (structured outputs / guided decoding)
/// and may cache, think, or batch internally — none of that leaks through this interface.
/// </summary>
public interface ILlmProvider
{
    Task<T> CompleteJsonAsync<T>(LlmRole role, LlmConversation conversation, CancellationToken ct = default)
        where T : class;
}
