using SalesSupport.Core.Contracts;
namespace SalesSupport.Orchestrator;

public sealed class OrchestratorOptions
{
    /// <summary>Utterances of context the gate sees (docs/prompts.md).</summary>
    public int GateWindow { get; init; } = 10;

    /// <summary>Cards retrieved per advice topic (D13).</summary>
    public int RetrievalK { get; init; } = 4;

    /// <summary>Damping floor (D11): minimum turns between proactive advisor runs. Rep queries bypass this.</summary>
    public int MinTurnsBetweenAdvice { get; init; } = 2;

    public int MaxQuestions { get; init; } = 3;
    public int MaxProducts { get; init; } = 3;

    public string CallLanguage { get; init; } = "sv";

    /// <summary>Installation locale — the language of post-call summaries and UI chrome (D7).</summary>
    public string UiLanguage { get; init; } = "sv";

    public string CompanyName { get; init; } = "";

    /// <summary>The D27 cost dial: how eagerly the gate requests advice (docs/prompts.md).</summary>
    public GateStrictness GateStrictness { get; init; } = GateStrictness.Balanced;

    /// <summary>Catalog map tier handed to gate/advisor prompts (D14: per-provider context budget).</summary>
    public CatalogMapTier CatalogMapTier { get; init; } = CatalogMapTier.Full;

    /// <summary>Installation-specific house rules for the advisor's company block (≤ ~300 tokens, docs/prompts.md).</summary>
    public string SalesGuidance { get; init; } = "";
}

public enum GateStrictness { Strict, Balanced, Eager }
