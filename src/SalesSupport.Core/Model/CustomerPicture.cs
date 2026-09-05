namespace SalesSupport.Core.Model;

/// <summary>
/// The live working state of a call — schema per docs/customer-picture.md.
/// Mutated only by <see cref="Merging.PictureMerger"/>; everything else reads.
/// </summary>
public sealed class CustomerPicture
{
    public int SchemaVersion { get; init; } = 1;
    public CompanyInfo? Company { get; set; }
    // init setters: System.Text.Json skips get-only collections on deserialization, which
    // left the panel's Kundbild with the company name and nothing else (found in the
    // first recorded demo). The merger still mutates these lists in place.
    public List<Fact> Facts { get; init; } = [];
    public List<ConversationThread> Threads { get; init; } = [];
    public List<ProductInterest> ProductInterest { get; init; } = [];
    public List<ActionItem> ActionItems { get; init; } = [];
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
