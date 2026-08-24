namespace SalesSupport.Core.Model;

/// <summary>
/// The live working state of a call — schema per docs/customer-picture.md.
/// Mutated only by <see cref="Merging.PictureMerger"/>; everything else reads.
/// </summary>
public sealed class CustomerPicture
{
    public int SchemaVersion { get; init; } = 1;
    public CompanyInfo? Company { get; set; }
    public List<Fact> Facts { get; } = [];
    public List<ConversationThread> Threads { get; } = [];
    public List<ProductInterest> ProductInterest { get; } = [];
    public List<ActionItem> ActionItems { get; } = [];
}

public sealed record CompanyInfo(
    string Name,
    string? Industry,
    string? SizeHint,
    string? LocationHint,
    Source Source);

public sealed record Fact(
    string Id,
    FactCategory Category,
    string Text,
    Source Source,
    Confidence Confidence,
    int Turn);

public sealed record ConversationThread(
    string Id,
    string Topic,
    ThreadKind Kind,
    ThreadStatus Status,
    Salience Salience,
    string Note,
    int Turn);

public sealed record ProductInterest(
    string Id,
    string? ProductRef,
    string NameAsSaid,
    Stance Stance,
    string Reason,
    Source Source,
    int Turn);

public sealed record ActionItem(
    string Id,
    string Text,
    ActionOwner Owner,
    Source Source,
    int Turn);
