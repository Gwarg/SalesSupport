namespace SalesSupport.Core.Model;

public sealed record NextStep(string Text, ActionOwner Owner);

/// <summary>Summarizer output (docs/prompts.md, "Summarizer").</summary>
public sealed class SummaryResult
{
    public string Summary { get; init; } = "";
    public List<NextStep> NextSteps { get; init; } = [];
}
