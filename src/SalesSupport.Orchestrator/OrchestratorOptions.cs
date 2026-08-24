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
    public string CompanyName { get; init; } = "";
}
