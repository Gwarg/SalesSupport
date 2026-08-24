namespace SalesSupport.Core.Model;

/// <summary>
/// The advisor's output: the desired panel state with id reuse for stability
/// (docs/prompts.md, "Advisor"). Items with a known id are kept unchanged; null id = new.
/// An unchanged panel is a valid output. Answer is non-null only in on-demand mode.
/// </summary>
public sealed class AdvisorResult
{
    public List<PanelQuestion> Questions { get; init; } = [];
    public List<PanelProduct> Products { get; init; } = [];
    public List<ThreadUpdate> ThreadUpdates { get; init; } = [];
    public string? Answer { get; init; }
}

public sealed record PanelQuestion(string? Id, string Text, string? Thread);

public sealed record PanelProduct(string? Id, string ProductRef, string DisplayName, string Why, string? Thread, string? PriceNote);

/// <summary>Advisor may re-prioritize or park threads — never create them (that is the gate's job).</summary>
public sealed record ThreadUpdate(string Id, ThreadStatus? Status, Salience? Salience);
