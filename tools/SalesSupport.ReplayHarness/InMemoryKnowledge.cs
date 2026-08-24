using SalesSupport.Core.Contracts;

namespace SalesSupport.ReplayHarness;

/// <summary>
/// L0 stand-in for the SQLite pack reader: a handful of Nordfrys-scenario cards with
/// naive word-overlap scoring. Real hybrid retrieval (FTS5 + vectors + RRF) replaces
/// this behind the same interface (docs/knowledge-pack.md).
/// </summary>
public sealed class InMemoryKnowledge : IKnowledgeSource
{
    private sealed record Entry(string DocId, string Kind, string Title, string Body);

    private static readonly Entry[] Entries =
    [
        new("fam:handscanners", "family", "Handskannrar",
            "Handhållna streckkodsskannrar för lager, kyl och frys. Familjer: X40 (utgående), X60 (frysklassad)."),
        new("prod:x40", "product", "X40 handskanner",
            "Äldre generation handskanner. Känd svaghet: batteritid i kallmiljö. Ersätts av X60. Laddas i LP-dock."),
        new("prod:x60", "product", "X60 handskanner",
            "Frysklassad handskanner, drifttemp -30 till +50 grader, IP67, batteri 14 timmar i kallmiljö. Laddas i samma dockor som X40. Ersätter X40."),
        new("prod:serviceavtal-frys", "product", "Serviceavtal frys",
            "Serviceavtal för skannrar i kyl- och frysmiljö. Täcker batteribyten och slitage i kallmiljö."),
        new("prod:lp-dock", "product", "LP-dock laddstation",
            "Laddstation kompatibel med X40 och X60. Behövs endast vid utökning av antal laddplatser."),
    ];

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["x40"] = "prod:x40",
        ["x-40"] = "prod:x40",
        ["x60"] = "prod:x60",
        ["x-60"] = "prod:x60",
        ["lp-dock"] = "prod:lp-dock",
    };

    public Task<IReadOnlyList<RetrievedCard>> SearchAsync(
        string query, int k, IReadOnlyList<string>? boostFamilies = null, CancellationToken ct = default)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(w => w.Length > 3)
            .Select(w => w.ToLowerInvariant())
            .ToArray();

        var scored = Entries
            .Select(e =>
            {
                var haystack = $"{e.Title} {e.Body}".ToLowerInvariant();
                var score = words.Count(haystack.Contains);
                return (Entry: e, Score: (double)score);
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(k)
            .Select(x => new RetrievedCard(x.Entry.DocId, x.Entry.Kind, x.Entry.Title, x.Entry.Body, x.Score))
            .ToList();

        if (scored.Count == 0)
            scored.Add(new RetrievedCard(Entries[0].DocId, Entries[0].Kind, Entries[0].Title, Entries[0].Body, 0));

        return Task.FromResult<IReadOnlyList<RetrievedCard>>(scored);
    }

    public string? ResolveAlias(string text) => Aliases.GetValueOrDefault(text.Trim());

    public string GetCatalogMap() =>
        "Nordfrys-demokatalog: handskannrar (X40 utgående, X60 frysklassad), laddstationer (LP-dock), serviceavtal (frys).";
}
