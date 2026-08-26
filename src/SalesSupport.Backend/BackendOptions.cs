using SalesSupport.Orchestrator;

namespace SalesSupport.Backend;

/// <summary>Bound from the "Backend" section of appsettings.json. One installation = one config (D2).</summary>
public sealed class BackendOptions
{
    public string CompanyName { get; init; } = "Duab (demo)";
    public string DefaultLanguage { get; init; } = "sv";
    public string UiLanguage { get; init; } = "sv";

    /// <summary>"ollama" or "claude" (D14). The desktop client never sees model keys (D20).</summary>
    public string LlmProvider { get; init; } = "ollama";

    /// <summary>One local model for all roles (small-VRAM constraint). Benchmark alternatives with the harness.</summary>
    public string OllamaModel { get; init; } = "qwen3:8b";

    /// <summary>true sends think=false (needed for qwen3-class models); set false for models without a thinking mode.</summary>
    public bool OllamaNoThink { get; init; } = true;

    /// <summary>Explicit pack path; when empty the newest *.pack.sqlite in PacksDirectory is loaded.</summary>
    public string? PackPath { get; init; }
    public string PacksDirectory { get; init; } = "packs";
    public string ModelDirectory { get; init; } = "models/multilingual-e5-small";

    public GateStrictness GateStrictness { get; init; } = GateStrictness.Balanced;
    public string SalesGuidance { get; init; } = "";

    /// <summary>Interaction retention (D17). Purged on startup and on every save.</summary>
    public int RetentionDays { get; init; } = 90;
    public string DatabasePath { get; init; } = "data/salessupport.db";
}
