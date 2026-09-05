using System.Text;
using SalesSupport.Core.Serialization;
using SalesSupport.Knowledge;

namespace SalesSupport.Pipeline;

/// <summary>
/// The offline "prelearning" stages (D4, D29): validate at the waist, derive the family
/// taxonomy from category paths, write product cards and family summaries, and assemble
/// everything the PackBuilder needs. Runs in template mode (deterministic, no LLM) —
/// the LLM enrichment pass slots into WriteCard/WriteFamilySummary/WriteQuestionMap when
/// an ILlmProvider and budget exist; the pipeline structure is identical either way.
/// The deterministic trio rule (D29): ids, skus, prices, currency, and availability are
/// copied straight from the canonical feed — never through any model.
/// </summary>
public static class PackAssembler
{
    public sealed record Assembly(
        List<PackProduct> Products,
        List<PackFamily> Families,
        List<PackAlias> Aliases,
        List<PackRelation> Relations,
        string CatalogMap,
        List<string> SttVocab);

    public static List<string> Validate(IReadOnlyList<RawProduct> rows)
    {
        var errors = new List<string>();
        var externalIds = new HashSet<string>();
        var skus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var line = $"line {i + 1} ({row.Sku})";
            if (string.IsNullOrWhiteSpace(row.ExternalId)) errors.Add($"{line}: missing external_id");
            if (string.IsNullOrWhiteSpace(row.Sku)) errors.Add($"{line}: missing sku");
            if (string.IsNullOrWhiteSpace(row.Name)) errors.Add($"{line}: missing name");
            if (string.IsNullOrWhiteSpace(row.CategoryPathRaw)) errors.Add($"{line}: missing category_path_raw");
            if (string.IsNullOrWhiteSpace(row.DescriptionRaw)) errors.Add($"{line}: missing description_raw");
            if (!externalIds.Add(row.ExternalId)) errors.Add($"{line}: duplicate external_id");
            if (!skus.Add(row.Sku)) errors.Add($"{line}: duplicate sku");
            if (row.Price is < 0) errors.Add($"{line}: negative price");
        }

        var skuSet = rows.Select(r => r.Sku).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            foreach (var relation in row.RelationsRaw ?? [])
                if (!skuSet.Contains(relation.TargetSku))
                    errors.Add($"{row.Sku}: relation target sku '{relation.TargetSku}' not in feed");
        }

        return errors;
    }

    public static Assembly Assemble(IReadOnlyList<RawProduct> rows, IEmbedder embedder)
    {
        var families = BuildFamilies(rows, embedder);
        var familyByPath = families.ToDictionary(f => f.Path, f => f.Id);
        var idBySku = rows.ToDictionary(r => r.Sku, r => ProductId(r.Sku), StringComparer.OrdinalIgnoreCase);

        var products = new List<PackProduct>();
        var aliases = new List<PackAlias>();
        var relations = new List<PackRelation>();
        var vocab = new List<string>();

        foreach (var row in rows)
        {
            var id = idBySku[row.Sku];
            var card = WriteCard(row, idBySku);
            products.Add(new PackProduct
            {
                Id = id,
                Sku = row.Sku,
                Name = row.Name,
                FamilyId = familyByPath[row.CategoryPathRaw],
                Status = row.Status ?? "active",
                AttributesJson = JsonDefaults.Serialize(row.AttributesRaw ?? []),
                PriceAmount = row.Price,
                PriceCurrency = row.Currency,
                PriceNote = row.PriceNote,
                Availability = row.AvailabilityRaw,
                Card = card,
                SourceRef = row.ExternalId,
                Embedding = embedder.Embed($"passage: {row.Name}\n{card}", isQuery: false),
            });

            aliases.Add(new PackAlias(row.Sku, "sku", id));
            aliases.Add(new PackAlias(row.Name, "name", id));
            foreach (var alias in row.AliasesRaw ?? [])
                aliases.Add(new PackAlias(alias, "spoken", id));

            foreach (var relation in row.RelationsRaw ?? [])
                relations.Add(new PackRelation(id, idBySku[relation.TargetSku], relation.Kind, relation.Note));

            vocab.Add(row.Name);
            vocab.Add(row.Sku);
            vocab.AddRange(row.AliasesRaw ?? []);
        }

        var catalogMap = BuildCatalogMap(rows, families);
        return new Assembly(products, families, aliases, relations, catalogMap, vocab);
    }

    private static List<PackFamily> BuildFamilies(IReadOnlyList<RawProduct> rows, IEmbedder embedder)
    {
        var families = new Dictionary<string, PackFamily>();

        foreach (var path in rows.Select(r => r.CategoryPathRaw).Distinct())
        {
            var segments = path.Split('>', StringSplitOptions.TrimEntries);
            string? parentId = null;
            for (var depth = 0; depth < segments.Length; depth++)
            {
                var subPath = string.Join(" > ", segments[..(depth + 1)]);
                var id = FamilyId(segments[..(depth + 1)]);
                if (!families.ContainsKey(id))
                {
                    var members = rows.Where(r => r.CategoryPathRaw == subPath || r.CategoryPathRaw.StartsWith(subPath + " >", StringComparison.Ordinal)).ToList();
                    families[id] = new PackFamily
                    {
                        Id = id,
                        ParentId = parentId,
                        Name = segments[depth],
                        Path = subPath,
                        Summary = WriteFamilySummary(segments[depth], members),
                        QuestionMap = members.Any(m => m.CategoryPathRaw == subPath) ? WriteQuestionMap(segments[depth], members) : null,
                        Embedding = embedder.Embed($"passage: {segments[depth]}\n{WriteFamilySummary(segments[depth], members)}", isQuery: false),
                    };
                }
                parentId = id;
            }
        }

        return [.. families.Values];
    }

    private static string WriteCard(RawProduct row, Dictionary<string, string> idBySku)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {row.Name}");
        sb.AppendLine(row.DescriptionRaw.Trim());

        if (row.AttributesRaw is { Count: > 0 })
            sb.AppendLine("**Nyckelspecar:** " + string.Join(" · ", row.AttributesRaw.Select(kv => $"{kv.Key.Replace('_', ' ')}: {kv.Value}")));

        var relationLines = (row.RelationsRaw ?? [])
            .Select(rel => rel.Kind switch
            {
                "successor_of" => $"Ersätter {rel.TargetSku}",
                "accessory_of" => $"Tillbehör till {rel.TargetSku}",
                "complement_of" => $"Kompletterar {rel.TargetSku}",
                "consumable_for" => $"Förbrukning till {rel.TargetSku}",
                "variant_of" => $"Variant av {rel.TargetSku}",
                _ => $"{rel.Kind}: {rel.TargetSku}",
            })
            .ToList();
        if (relationLines.Count > 0)
            sb.AppendLine("**Relationer:** " + string.Join(" · ", relationLines));

        if (row.Status == "discontinued")
            sb.AppendLine("**Status:** utgående modell");

        return sb.ToString().TrimEnd();
    }

    private static string WriteFamilySummary(string name, IReadOnlyList<RawProduct> members)
    {
        var names = members.Select(m => m.Name).Take(6).ToList();
        return $"{name}: {members.Count} produkter — {string.Join(", ", names)}.";
    }

    private static string WriteQuestionMap(string familyName, IReadOnlyList<RawProduct> members)
    {
        var attributeKeys = members
            .SelectMany(m => m.AttributesRaw?.Keys ?? Enumerable.Empty<string>())
            .GroupBy(k => k)
            .OrderByDescending(g => g.Count())
            .Take(3)
            .Select(g => g.Key.Replace('_', ' '));

        var sb = new StringBuilder();
        sb.AppendLine($"Att fråga kunden om {familyName.ToLowerInvariant()}:");
        foreach (var key in attributeKeys)
            sb.AppendLine($"- Vilka krav ställer ni på {key}?");
        sb.AppendLine("- Vilken volym eller omfattning gäller det?");
        sb.AppendLine("- Vad använder ni idag, och vad fungerar sämst?");
        return sb.ToString().TrimEnd();
    }

    private static string BuildCatalogMap(IReadOnlyList<RawProduct> rows, IReadOnlyList<PackFamily> families)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Produktkatalog — familjer och produkter:");
        foreach (var family in families.Where(f => f.ParentId is not null).OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            var members = rows.Where(r => r.CategoryPathRaw == family.Path).ToList();
            if (members.Count == 0) continue;
            sb.AppendLine($"\n## {family.Path}");
            // The map is "families and instruments" (D4), not every SKU: options, modules and
            // accessories (kind attribute from document-source adapters, D33) collapse into
            // one line per family so hundreds of probes and option codes do not swamp the
            // gate/advisor prompts. Packs without a kind attribute list everything as before.
            var headline = members.Where(IsHeadline).ToList();
            var rest = members.Where(m => !IsHeadline(m)).ToList();
            foreach (var member in headline)
                sb.AppendLine($"- {member.Name} ({member.Sku}){(member.Status == "discontinued" ? " — utgående" : "")}: {FirstSentence(member.DescriptionRaw)}");
            if (rest.Count > 0)
                sb.AppendLine($"- {rest.Count} tillbehör/optioner/moduler, t.ex. {string.Join(", ", rest.Take(6).Select(m => m.Sku))}{(rest.Count > 6 ? ", …" : "")}");
        }
        return sb.ToString().TrimEnd();
    }

    private static bool IsHeadline(RawProduct row) =>
        row.AttributesRaw is null
        || !row.AttributesRaw.TryGetValue("kind", out var kind)
        || kind is "instrument" or "software";

    private static string FirstSentence(string text)
    {
        var index = text.IndexOf(". ", StringComparison.Ordinal);
        return index > 0 ? text[..(index + 1)] : text;
    }

    private static string ProductId(string sku) => "prod:" + Slug(sku);

    private static string FamilyId(string[] segments) => "fam:" + string.Join("-", segments.Select(Slug));

    private static string Slug(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text.ToLowerInvariant())
        {
            sb.Append(c switch
            {
                'å' or 'ä' => 'a',
                'ö' => 'o',
                'é' => 'e',
                ' ' or '_' => '-',
                _ when char.IsAsciiLetterOrDigit(c) || c == '-' => c,
                _ => '\0',
            });
        }
        return sb.ToString().Replace("\0", "").Trim('-');
    }
}
