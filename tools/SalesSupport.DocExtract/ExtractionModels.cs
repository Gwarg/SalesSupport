namespace SalesSupport.DocExtract;

// The extractor's strict output schema (D33). Flat lists instead of dictionaries —
// Claude's strict structured outputs require additionalProperties:false everywhere.

public sealed record ExtractedAttribute(string Key, string Value);

/// <summary>kind: option_of | module_of | accessory_of | software_for | successor_of | complement_of.</summary>
public sealed record ExtractedRelation(string Kind, string TargetModelCode, string? Note);

/// <summary>kind: instrument | option | module | accessory | software.</summary>
public sealed record ExtractedProduct(
    string ModelCode,
    string Name,
    string Kind,
    string CategoryPath,
    string Description,
    List<ExtractedAttribute> Attributes,
    List<string> Aliases,
    List<ExtractedRelation> Relations,
    string? Status);

public sealed record ExtractedCatalog(List<ExtractedProduct> Products, List<string> Notes);
