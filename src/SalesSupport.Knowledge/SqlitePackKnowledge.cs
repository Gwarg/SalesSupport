using System.Runtime.InteropServices;
using System.Numerics.Tensors;
using Microsoft.Data.Sqlite;
using SalesSupport.Core.Contracts;

namespace SalesSupport.Knowledge;

/// <summary>
/// IKnowledgeSource over a knowledge pack file (docs/knowledge-pack.md). The hot set —
/// vectors, cards, aliases, catalog map — loads into RAM at open; FTS5 queries go to the
/// read-only SQLite file per call (pooled connections). Hybrid retrieval = FTS5 BM25 +
/// brute-force cosine over the vector matrix, fused with reciprocal rank fusion (k=60).
/// </summary>
public sealed class SqlitePackKnowledge : IKnowledgeSource
{
    private const int RrfK = 60;
    private const double FamilyBoost = 0.01;

    private sealed record Doc(string Id, string Kind, string Title, string Body, string? FamilyId);

    private readonly string _connectionString;
    private readonly IEmbedder _embedder;
    private readonly List<Doc> _docs;
    private readonly Dictionary<string, int> _docIndex;
    private readonly float[] _vectors;
    private readonly Dictionary<string, List<string>> _aliases;
    private readonly string _catalogMap;

    public string CompanyId { get; }
    public string PackVersion { get; }

    private SqlitePackKnowledge(
        string connectionString, IEmbedder embedder, List<Doc> docs, float[] vectors,
        Dictionary<string, List<string>> aliases, string catalogMap, string companyId, string packVersion)
    {
        _connectionString = connectionString;
        _embedder = embedder;
        _docs = docs;
        _docIndex = docs.Select((d, i) => (d.Id, i)).ToDictionary(x => x.Id, x => x.i);
        _vectors = vectors;
        _aliases = aliases;
        _catalogMap = catalogMap;
        CompanyId = companyId;
        PackVersion = packVersion;
    }

    public static SqlitePackKnowledge Load(string path, IEmbedder embedder)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"Knowledge pack not found: {path}");
        var connectionString = $"Data Source={path};Mode=ReadOnly";

        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        var meta = ReadAll(connection, "SELECT key, value FROM meta", r => (r.GetString(0), r.GetString(1)))
            .ToDictionary(x => x.Item1, x => x.Item2);

        if (meta["schema_version"] != PackBuilder.SchemaVersion.ToString())
            throw new InvalidOperationException($"Pack schema_version {meta["schema_version"]} not supported (expected {PackBuilder.SchemaVersion}).");
        if (meta["embedding_model"] != embedder.ModelId || meta["embedding_dims"] != embedder.Dims.ToString())
            throw new InvalidOperationException(
                $"Pack was built with embedder {meta["embedding_model"]}/{meta["embedding_dims"]} but the local embedder is {embedder.ModelId}/{embedder.Dims} — rebuild the pack or switch embedder.");

        var docs = new List<Doc>();
        var vectorRows = new List<byte[]>();

        foreach (var (id, name, summary, blob) in ReadAll(connection,
            "SELECT id, name, summary, embedding FROM families",
            r => (r.GetString(0), r.GetString(1), r.GetString(2), (byte[])r.GetValue(3))))
        {
            docs.Add(new Doc(id, "family", name, summary, null));
            vectorRows.Add(blob);
        }

        foreach (var (id, name, familyId, card, blob) in ReadAll(connection,
            "SELECT id, name, family_id, card, embedding FROM products",
            r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), (byte[])r.GetValue(4))))
        {
            docs.Add(new Doc(id, "product", name, card, familyId));
            vectorRows.Add(blob);
        }

        var vectors = new float[docs.Count * embedder.Dims];
        for (var i = 0; i < vectorRows.Count; i++)
        {
            if (vectorRows[i].Length != embedder.Dims * sizeof(float))
                throw new InvalidOperationException($"Pack vector for {docs[i].Id} has wrong length.");
            MemoryMarshal.Cast<byte, float>(vectorRows[i]).CopyTo(vectors.AsSpan(i * embedder.Dims, embedder.Dims));
        }

        var aliases = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (alias, target) in ReadAll(connection, "SELECT alias, target_id FROM aliases", r => (r.GetString(0), r.GetString(1))))
        {
            var key = NormalizeAlias(alias);
            if (!aliases.TryGetValue(key, out var targets)) aliases[key] = targets = [];
            if (!targets.Contains(target)) targets.Add(target);
        }

        var catalogMap = ReadAll(connection, "SELECT text FROM catalog_map WHERE tier = 'full'", r => r.GetString(0)).FirstOrDefault()
            ?? throw new InvalidOperationException("Pack has no catalog_map('full').");

        return new SqlitePackKnowledge(connectionString, embedder, docs, vectors, aliases, catalogMap,
            meta["company_id"], meta["pack_version"]);
    }

    public Task<IReadOnlyList<RetrievedCard>> SearchAsync(
        string query, int k, IReadOnlyList<string>? boostFamilies = null, CancellationToken ct = default)
    {
        var ftsRanks = FtsRanks(query);
        var vectorRanks = VectorRanks(query);

        var scores = new Dictionary<int, double>();
        foreach (var (docIndex, rank) in ftsRanks)
            scores[docIndex] = scores.GetValueOrDefault(docIndex) + 1.0 / (RrfK + rank);
        foreach (var (docIndex, rank) in vectorRanks)
            scores[docIndex] = scores.GetValueOrDefault(docIndex) + 1.0 / (RrfK + rank);

        if (boostFamilies is { Count: > 0 })
        {
            var boostSet = boostFamilies.ToHashSet();
            foreach (var docIndex in scores.Keys.ToList())
            {
                var doc = _docs[docIndex];
                if (boostSet.Contains(doc.Id) || (doc.FamilyId is { } family && boostSet.Contains(family)))
                    scores[docIndex] += FamilyBoost;
            }
        }

        IReadOnlyList<RetrievedCard> result = scores
            .OrderByDescending(kv => kv.Value)
            .Take(k)
            .Select(kv => new RetrievedCard(_docs[kv.Key].Id, _docs[kv.Key].Kind, _docs[kv.Key].Title, _docs[kv.Key].Body, kv.Value))
            .ToList();
        return Task.FromResult(result);
    }

    public string? ResolveAlias(string text)
    {
        var targets = _aliases.GetValueOrDefault(NormalizeAlias(text));
        return targets is { Count: 1 } ? targets[0] : null;
    }

    public string GetCatalogMap() => _catalogMap;

    private List<(int DocIndex, int Rank)> FtsRanks(string query)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length > 1)
            .Select(t => "\"" + t.Replace("\"", "") + "\"")
            .ToArray();
        if (terms.Length == 0) return [];

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT doc_id FROM search_fts WHERE search_fts MATCH $q ORDER BY rank LIMIT 20";
        command.Parameters.AddWithValue("$q", string.Join(" OR ", terms));

        var results = new List<(int, int)>();
        using var reader = command.ExecuteReader();
        var rank = 0;
        while (reader.Read())
        {
            if (_docIndex.TryGetValue(reader.GetString(0), out var docIndex))
                results.Add((docIndex, rank++));
        }
        return results;
    }

    private List<(int DocIndex, int Rank)> VectorRanks(string query)
    {
        var queryVector = _embedder.Embed(query, isQuery: true);
        var similarities = new (int DocIndex, float Score)[_docs.Count];
        for (var i = 0; i < _docs.Count; i++)
        {
            var docVector = _vectors.AsSpan(i * _embedder.Dims, _embedder.Dims);
            similarities[i] = (i, TensorPrimitives.Dot<float>(queryVector, docVector));
        }
        return similarities
            .OrderByDescending(x => x.Score)
            .Take(20)
            .Where(x => x.Score > 0)
            .Select((x, rank) => (x.DocIndex, rank))
            .ToList();
    }

    private static string NormalizeAlias(string text) =>
        text.Trim().ToLowerInvariant().Replace("-", "").Replace(" ", "");

    private static IEnumerable<T> ReadAll<T>(SqliteConnection connection, string sql, Func<SqliteDataReader, T> map)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read()) yield return map(reader);
    }
}
