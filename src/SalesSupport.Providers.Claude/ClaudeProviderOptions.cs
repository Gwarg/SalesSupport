using SalesSupport.Core.Contracts;

namespace SalesSupport.Providers.Claude;

public sealed class ClaudeRoleConfig
{
    public required string Model { get; init; }
    public int MaxTokens { get; init; } = 4096;

    /// <summary>"low" | "medium" | "high" | "max". Null = model default. Not sent for models without effort support (Haiku 4.5).</summary>
    public string? Effort { get; init; }
}

/// <summary>
/// Role → model mapping per D12/D14: gate = Haiku 4.5 (fast, no thinking), advisor and
/// above = Opus 5 with adaptive thinking, effort as the latency dial (tuned in L0 against
/// the 1.5–3 s advisor budget).
/// </summary>
public sealed class ClaudeProviderOptions
{
    /// <summary>Invoked after every completed call with its token usage.</summary>
    public Action<LlmUsage>? UsageReported { get; init; }

    public ClaudeRoleConfig Gate { get; init; } = new() { Model = "claude-haiku-4-5", MaxTokens = 2048 };

    /// <summary>Advisor model/effort overridable via CLAUDE_ADVISOR_MODEL / CLAUDE_ADVISOR_EFFORT ("none" disables) for experiments.</summary>
    public ClaudeRoleConfig Advisor { get; init; } = BuildAdvisorConfig();

    private static ClaudeRoleConfig BuildAdvisorConfig()
    {
        // Measured 2026-08-26 (runs/): low matches medium's quality on the corpus at ~15%
        // lower latency and ~25% fewer output tokens; Sonnet 5 was no faster than Opus.
        var model = Environment.GetEnvironmentVariable("CLAUDE_ADVISOR_MODEL") is { Length: > 0 } m ? m : "claude-opus-5";
        var effort = Environment.GetEnvironmentVariable("CLAUDE_ADVISOR_EFFORT") is { Length: > 0 } e ? e : "low";
        // Haiku-class models reject output_config.effort — a model override to Haiku must
        // drop the effort dial instead of 400-ing every advisor call.
        if (effort == "none" || model.Contains("haiku", StringComparison.OrdinalIgnoreCase))
            return new ClaudeRoleConfig { Model = model, MaxTokens = 4096 };
        return new ClaudeRoleConfig { Model = model, MaxTokens = 4096, Effort = effort };
    }
    public ClaudeRoleConfig Summarizer { get; init; } = new() { Model = "claude-opus-5", MaxTokens = 4096, Effort = "high" };
    public ClaudeRoleConfig Drafter { get; init; } = new() { Model = "claude-opus-5", MaxTokens = 8192, Effort = "high" };

    public ClaudeRoleConfig Resolve(LlmRole role) => role switch
    {
        LlmRole.Gate => Gate,
        LlmRole.Advisor => Advisor,
        LlmRole.Summarizer => Summarizer,
        LlmRole.Drafter => Drafter,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };
}
