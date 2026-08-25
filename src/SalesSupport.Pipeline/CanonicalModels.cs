namespace SalesSupport.Pipeline;

/// <summary>
/// The canonical import model (D29) — the narrow waist every per-company adapter maps into.
/// One JSONL line per product. The pipeline consumes only this; source formats never leak past
/// the adapter.
/// </summary>
public sealed record RawProduct(
    string ExternalId,
    string Sku,
    string Name,
    string CategoryPathRaw,
    string DescriptionRaw,
    Dictionary<string, string>? AttributesRaw,
    double? Price,
    string? Currency,
    string? PriceNote,
    string? AvailabilityRaw,
    string? Status,
    List<string>? AliasesRaw,
    List<RawRelation>? RelationsRaw,
    List<string>? DocRefs);

public sealed record RawRelation(string Kind, string TargetSku, string? Note);
