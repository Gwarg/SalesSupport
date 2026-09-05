using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace SalesSupport.Knowledge;

/// <summary>
/// Writes a knowledge pack per the DDL in docs/knowledge-pack.md and validates it before
/// returning — build fails, runtime never surprises. The pack is written to a temp file
/// and moved into place so a crashed build never leaves a half-written pack behind.
/// </summary>
public static class PackBuilder
{
    public const int SchemaVersion = 1;

    private const string Ddl = """
        CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        CREATE TABLE families (
          id TEXT PRIMARY KEY, parent_id TEXT REFERENCES families(id),
          name TEXT NOT NULL, path TEXT NOT NULL, summary TEXT NOT NULL,
          question_map TEXT, embedding BLOB NOT NULL);
        CREATE TABLE products (
          id TEXT PRIMARY KEY, sku TEXT NOT NULL, name TEXT NOT NULL,
          family_id TEXT NOT NULL REFERENCES families(id),
          status TEXT NOT NULL DEFAULT 'active',
          attributes TEXT NOT NULL DEFAULT '{}',
          price_amount REAL, price_currency TEXT, price_note TEXT, availability TEXT,
          card TEXT NOT NULL, embedding BLOB NOT NULL, source_ref TEXT);
        CREATE INDEX idx_products_family ON products(family_id);
        CREATE INDEX idx_products_sku ON products(sku);
        CREATE TABLE aliases (alias TEXT NOT NULL, kind TEXT NOT NULL, target_id TEXT NOT NULL,
          PRIMARY KEY (alias, target_id));
        CREATE TABLE relations (from_id TEXT NOT NULL, to_id TEXT NOT NULL, kind TEXT NOT NULL,
          note TEXT, PRIMARY KEY (from_id, to_id, kind));
        CREATE TABLE catalog_map (tier TEXT PRIMARY KEY, text TEXT NOT NULL, token_estimate INTEGER NOT NULL);
        CREATE TABLE stt_vocab (term TEXT PRIMARY KEY, weight REAL NOT NULL DEFAULT 1.0);
        CREATE VIRTUAL TABLE search_fts USING fts5(
          body, doc_id UNINDEXED, kind UNINDEXED,
          tokenize = 'unicode61 remove_diacritics 2');
        """;

    public static void Build(
        string path,
        PackMeta meta,
        IEmbedder embedder,
        IReadOnlyList<PackProduct> products,
        IReadOnlyList<PackFamily> families,
        IReadOnlyList<PackAlias> aliases,
        IReadOnlyList<PackRelation> relations,
        string catalogMapText,
        IReadOnlyList<string> sttVocab,
        string? catalogMapCompact = null)
    {
        Validate(embedder, products, families, aliases, relations, catalogMapText, sttVocab);

        var tempPath = path + ".building";
        File.Delete(tempPath);

        using (var connection = new SqliteConnection($"Data Source={tempPath}"))
        {
            connection.Open();
            Execute(connection, Ddl);

            using var transaction = connection.BeginTransaction();

            InsertMeta(connection, "schema_version", SchemaVersion.ToString());
            InsertMeta(connection, "company_id", meta.CompanyId);
            InsertMeta(connection, "pack_version", meta.PackVersion);
            InsertMeta(connection, "built_at", DateTime.UtcNow.ToString("O"));
            InsertMeta(connection, "feed_snapshot", meta.FeedSnapshot);
            InsertMeta(connection, "content_language", meta.ContentLanguage);
            InsertMeta(connection, "embedding_model", embedder.ModelId);
            InsertMeta(connection, "embedding_dims", embedder.Dims.ToString());
            InsertMeta(connection, "count_products", products.Count.ToString());
            InsertMeta(connection, "count_families", families.Count.ToString());

            foreach (var family in families)
            {
                Execute(connection,
                    "INSERT INTO families (id, parent_id, name, path, summary, question_map, embedding) VALUES ($1,$2,$3,$4,$5,$6,$7)",
                    family.Id, family.ParentId, family.Name, family.Path, family.Summary, family.QuestionMap, ToBlob(family.Embedding));
                Execute(connection,
                    "INSERT INTO search_fts (body, doc_id, kind) VALUES ($1,$2,$3)",
                    $"{family.Name}\n{family.Summary}", family.Id, "family");
            }

            foreach (var product in products)
            {
                Execute(connection,
                    "INSERT INTO products (id, sku, name, family_id, status, attributes, price_amount, price_currency, price_note, availability, card, embedding, source_ref) " +
                    "VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13)",
                    product.Id, product.Sku, product.Name, product.FamilyId, product.Status, product.AttributesJson,
                    product.PriceAmount, product.PriceCurrency, product.PriceNote, product.Availability,
                    product.Card, ToBlob(product.Embedding), product.SourceRef);
                var aliasText = string.Join(' ', aliases.Where(a => a.TargetId == product.Id).Select(a => a.Alias));
                Execute(connection,
                    "INSERT INTO search_fts (body, doc_id, kind) VALUES ($1,$2,$3)",
                    $"{product.Name} {product.Sku} {aliasText}\n{product.Card}\n{product.AttributesJson}", product.Id, "product");
            }

            foreach (var alias in aliases)
                Execute(connection, "INSERT OR IGNORE INTO aliases (alias, kind, target_id) VALUES ($1,$2,$3)", alias.Alias, alias.Kind, alias.TargetId);

            foreach (var relation in relations)
                Execute(connection, "INSERT INTO relations (from_id, to_id, kind, note) VALUES ($1,$2,$3,$4)", relation.FromId, relation.ToId, relation.Kind, relation.Note);

            Execute(connection, "INSERT INTO catalog_map (tier, text, token_estimate) VALUES ('full', $1, $2)",
                catalogMapText, catalogMapText.Length / 4);
            if (!string.IsNullOrWhiteSpace(catalogMapCompact))
                Execute(connection, "INSERT INTO catalog_map (tier, text, token_estimate) VALUES ('compact', $1, $2)",
                    catalogMapCompact, catalogMapCompact.Length / 4);

            foreach (var term in sttVocab.Distinct(StringComparer.OrdinalIgnoreCase))
                Execute(connection, "INSERT OR IGNORE INTO stt_vocab (term, weight) VALUES ($1, 1.0)", term);

            transaction.Commit();
        }

        SqliteConnection.ClearAllPools();
        File.Delete(path);
        File.Move(tempPath, path);
    }

    private static void Validate(
        IEmbedder embedder,
        IReadOnlyList<PackProduct> products,
        IReadOnlyList<PackFamily> families,
        IReadOnlyList<PackAlias> aliases,
        IReadOnlyList<PackRelation> relations,
        string catalogMapText,
        IReadOnlyList<string> sttVocab)
    {
        var errors = new List<string>();
        var familyIds = families.Select(f => f.Id).ToHashSet();
        var allIds = familyIds.Concat(products.Select(p => p.Id)).ToHashSet();

        if (products.Count == 0) errors.Add("no products");
        if (string.IsNullOrWhiteSpace(catalogMapText)) errors.Add("catalog map is empty");
        if (sttVocab.Count == 0) errors.Add("stt_vocab is empty");

        foreach (var family in families)
        {
            if (family.ParentId is { } parent && !familyIds.Contains(parent)) errors.Add($"family {family.Id}: unknown parent {parent}");
            if (family.Embedding.Length != embedder.Dims) errors.Add($"family {family.Id}: embedding dims {family.Embedding.Length} != {embedder.Dims}");
        }

        var seenIds = new HashSet<string>();
        foreach (var product in products)
        {
            if (!seenIds.Add(product.Id)) errors.Add($"duplicate product id {product.Id}");
            if (!familyIds.Contains(product.FamilyId)) errors.Add($"product {product.Id}: unknown family {product.FamilyId}");
            if (string.IsNullOrWhiteSpace(product.Card)) errors.Add($"product {product.Id}: empty card");
            if (product.Embedding.Length != embedder.Dims) errors.Add($"product {product.Id}: embedding dims {product.Embedding.Length} != {embedder.Dims}");
        }

        foreach (var alias in aliases)
            if (!allIds.Contains(alias.TargetId)) errors.Add($"alias '{alias.Alias}': unknown target {alias.TargetId}");

        foreach (var relation in relations)
        {
            if (!allIds.Contains(relation.FromId)) errors.Add($"relation: unknown from {relation.FromId}");
            if (!allIds.Contains(relation.ToId)) errors.Add($"relation: unknown to {relation.ToId}");
        }

        if (errors.Count > 0)
            throw new InvalidOperationException("Pack validation failed:\n  " + string.Join("\n  ", errors));
    }

    private static void InsertMeta(SqliteConnection connection, string key, string value) =>
        Execute(connection, "INSERT INTO meta (key, value) VALUES ($1,$2)", key, value);

    private static void Execute(SqliteConnection connection, string sql, params object?[] args)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        for (var i = 0; i < args.Length; i++)
            command.Parameters.AddWithValue($"${i + 1}", args[i] ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static byte[] ToBlob(float[] vector) => MemoryMarshal.AsBytes<float>(vector).ToArray();
}
