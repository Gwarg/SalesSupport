namespace SalesSupport.Core.Contracts;

/// <summary>One retrieval hit: a product card or family summary from the knowledge pack (docs/knowledge-pack.md).</summary>
public sealed record RetrievedCard(string DocId, string Kind, string Title, string Body, double Score);

/// <summary>
/// Read side of the knowledge pack. L0 uses an in-memory fake; the SQLite pack reader
/// (FTS5 + vector + RRF) implements this in SalesSupport.Knowledge.
/// </summary>
/// <summary>Catalog map tiers stored in the pack (docs/knowledge-pack.md): full = families + headline products with one-line descriptions; compact = names and SKUs only.</summary>
public enum CatalogMapTier { Full, Compact }

public interface IKnowledgeSource
{
    Task<IReadOnlyList<RetrievedCard>> SearchAsync(
        string query, int k, IReadOnlyList<string>? boostFamilies = null, CancellationToken ct = default);

    /// <summary>Alias resolution (D28): exact → normalized → search fallback. Ambiguity returns null, never a guess.</summary>
    string? ResolveAlias(string text);

    /// <summary>Prompt-sized shape of the catalog (D4). Tier is per-provider config (D14): compact for small-context or per-token-priced providers.</summary>

    string GetCatalogMap(CatalogMapTier tier = CatalogMapTier.Full);
}
