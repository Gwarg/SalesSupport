using SalesSupport.Orchestrator;

namespace SalesSupport.Backend;

/// <summary>Bound from the "Backend" section of appsettings.json. One installation = one config (D2).</summary>
public sealed class BackendOptions
{
    public string CompanyName { get; init; } = "Duab (demo)";
    public string DefaultLanguage { get; init; } = "sv";
    public string UiLanguage { get; init; } = "sv";

    /// <summary>"ollama", "claude" or "openai-compat" (D14/D31). The desktop client never sees model keys (D20).</summary>
    public string LlmProvider { get; init; } = "ollama";

    /// <summary>One local model for all roles (small-VRAM constraint). Benchmark alternatives with the harness.</summary>
    public string OllamaModel { get; init; } = "qwen3:8b";

    /// <summary>API root of an OpenAI-compatible endpoint (D31), e.g. https://openrouter.ai/api/v1.</summary>
    public string? OpenAiCompatBaseUrl { get; init; }

    /// <summary>Model id at that endpoint, used for all roles.</summary>
    public string? OpenAiCompatModel { get; init; }

    /// <summary>Name of the env var holding the key — the key itself never sits in appsettings (D9 spirit).</summary>
    public string OpenAiCompatApiKeyEnv { get; init; } = "OPENAI_COMPAT_API_KEY";

    /// <summary>false switches to json_object + schema-in-prompt for endpoints that reject json_schema.</summary>
    public bool OpenAiCompatStrictSchema { get; init; } = true;

    /// <summary>Reasoning control for thinking-class models: "none", "low", "medium", "high" or null (omit).</summary>
    public string? OpenAiCompatReasoning { get; init; }

    /// <summary>true sends think=false (needed for qwen3-class models); set false for models without a thinking mode.</summary>
    public bool OllamaNoThink { get; init; } = true;

    /// <summary>Explicit pack path; when empty the newest *.pack.sqlite in PacksDirectory is loaded.</summary>
    public string? PackPath { get; init; }
    public string PacksDirectory { get; init; } = "packs";
    public string ModelDirectory { get; init; } = "models/multilingual-e5-small";

    public GateStrictness GateStrictness { get; init; } = GateStrictness.Balanced;

    /// <summary>"full" | "compact" | "auto" (default): compact for Ollama (small context) and per-token-priced providers, full for Claude where the prompt is cached (D14).</summary>
    public string CatalogMap { get; init; } = "auto";

    public Core.Contracts.CatalogMapTier ResolveCatalogMapTier() => CatalogMap.ToLowerInvariant() switch
    {
        "full" => Core.Contracts.CatalogMapTier.Full,
        "compact" => Core.Contracts.CatalogMapTier.Compact,
        _ => LlmProvider == "claude" ? Core.Contracts.CatalogMapTier.Full : Core.Contracts.CatalogMapTier.Compact,
    };
    public string SalesGuidance { get; init; } = "";

    /// <summary>Phone → customer index (JSONL rows: phone, company, crm_id, notes) for incoming-call screen-pop (D32). Missing file = no resolution.</summary>
    public string CustomerIndexPath { get; init; } = "data/customers.jsonl";

    /// <summary>Applied to trunk-zero national numbers when normalizing callers.</summary>
    public string DefaultCountryCode { get; init; } = "+46";

    /// <summary>When set, telephony webhooks must carry token=&lt;secret&gt;; unset leaves the endpoint open (development only).</summary>
    public string? TelephonyWebhookSecret { get; init; }

    /// <summary>Interaction retention (D17). Purged on startup and on every save.</summary>
    public int RetentionDays { get; init; } = 90;
    public string DatabasePath { get; init; } = "data/salessupport.db";
}
