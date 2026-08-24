namespace SalesSupport.Core.Model;

/// <summary>
/// The gate's per-utterance output — a diff against the picture, never the full picture
/// (docs/customer-picture.md, "Gate diff schema"). Upserts with a null id are adds;
/// a known id updates that item. Ids are assigned by the merger, never by the model.
/// </summary>
public sealed class GateDiff
{
    public List<SignalEvent> Signals { get; init; } = [];
    public CompanyInfo? CompanyUpdate { get; init; }
    public List<FactUpsert> FactsUpsert { get; init; } = [];
    public List<string> FactsRemove { get; init; } = [];
    public List<ThreadUpsert> ThreadsUpsert { get; init; } = [];
    public List<ProductInterestUpsert> ProductInterestUpsert { get; init; } = [];
    public List<ActionItemUpsert> ActionItemsUpsert { get; init; } = [];
    public List<string> QuestionsAddressed { get; init; } = [];
    public string? SummaryAppend { get; init; }
    public AdviceDecision Advice { get; init; } = new(false, "", []);
    public string? LanguageFlag { get; init; }
}

public sealed record SignalEvent(SignalType Type, string Note);

/// <summary>Advice topics are hints (thread ids where known, plain topic text for new threads), not foreign keys.</summary>
public sealed record AdviceDecision(bool Needed, string Reason, List<string> Topics);

public sealed record FactUpsert(string? Id, FactCategory Category, string Text, Source Source, Confidence Confidence);

public sealed record ThreadUpsert(string? Id, string Topic, ThreadKind Kind, ThreadStatus Status, Salience Salience, string Note);

public sealed record ProductInterestUpsert(string? Id, string? ProductRef, string NameAsSaid, Stance Stance, string Reason, Source Source);

public sealed record ActionItemUpsert(string? Id, string Text, ActionOwner Owner, Source Source);
