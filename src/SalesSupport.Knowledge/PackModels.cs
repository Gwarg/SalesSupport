namespace SalesSupport.Knowledge;

public sealed record PackMeta(
    string CompanyId,
    string PackVersion,
    string ContentLanguage,
    string FeedSnapshot);

public sealed class PackProduct
{
    public required string Id { get; init; }
    public required string Sku { get; init; }
    public required string Name { get; init; }
    public required string FamilyId { get; init; }
    public string Status { get; init; } = "active";
    public string AttributesJson { get; init; } = "{}";
    public double? PriceAmount { get; init; }
    public string? PriceCurrency { get; init; }
    public string? PriceNote { get; init; }
    public string? Availability { get; init; }
    public required string Card { get; init; }
    public string? SourceRef { get; init; }
    public required float[] Embedding { get; init; }
}

public sealed class PackFamily
{
    public required string Id { get; init; }
    public string? ParentId { get; init; }
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required string Summary { get; init; }
    public string? QuestionMap { get; init; }
    public required float[] Embedding { get; init; }
}

public sealed record PackAlias(string Alias, string Kind, string TargetId);

public sealed record PackRelation(string FromId, string ToId, string Kind, string? Note);
