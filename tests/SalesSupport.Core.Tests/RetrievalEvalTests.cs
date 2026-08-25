using SalesSupport.Core.Serialization;
using SalesSupport.Knowledge;
using SalesSupport.Pipeline;

namespace SalesSupport.Core.Tests;

/// <summary>
/// Retrieval quality eval: builds the demo pack with the real e5 embedder and asserts
/// top hits for queries shaped like what retrieval actually receives — thread topics
/// (D13) and ask-lane phrasings. Soft-skips without the downloaded model files.
/// </summary>
public class RetrievalEvalTests : IDisposable
{
    private readonly string _packPath = Path.Combine(Path.GetTempPath(), $"eval_{Guid.NewGuid():N}.pack.sqlite");

    private static string? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SalesSupport.slnx")))
            dir = dir.Parent;
        return dir?.FullName;
    }

    private SqlitePackKnowledge? BuildDemoPack()
    {
        if (RepoRoot() is not { } root) return null;
        var modelDir = EmbedderFactory.DefaultModelDir(root);
        if (!File.Exists(Path.Combine(modelDir, "model_quantized.onnx"))) return null;

        var canonical = Path.Combine(root, "samples", "catalog", "duab-demo.canonical.jsonl");
        var rows = File.ReadLines(canonical)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(JsonDefaults.Deserialize<RawProduct>)
            .ToList();

        var embedder = OnnxEmbedder.Load(modelDir);
        var assembly = PackAssembler.Assemble(rows, embedder);
        PackBuilder.Build(_packPath, new PackMeta("eval", "1", "sv", "eval"), embedder,
            assembly.Products, assembly.Families, assembly.Aliases, assembly.Relations,
            assembly.CatalogMap, assembly.SttVocab);
        return SqlitePackKnowledge.Load(_packPath, embedder);
    }

    [Theory]
    [InlineData("Skanner som klarar -30 grader i frysen", "prod:du-x60", 1)]
    [InlineData("förbättra batteritiden på befintliga X40", "prod:du-bat40a", 2)]
    [InlineData("skannrar tappar wifi-anslutning inne i fryszonen", "prod:du-ap-f", 1)]
    [InlineData("nya märkningskraven för djupfryst 2026", "prod:du-lp200", 1)]
    public async Task Realistic_queries_surface_the_expected_product(string query, string expectedProduct, int withinTopProducts)
    {
        if (BuildDemoPack() is not { } pack) return;

        var hits = await pack.SearchAsync(query, 5);
        var topProducts = hits.Where(h => h.Kind == "product").Take(withinTopProducts).Select(h => h.DocId).ToList();

        // withinTopProducts > 1 is deliberate where a sibling product legitimately outranks
        // (a query naming X40 ranks X40 first; picking its consumable is the advisor's job).
        Assert.Contains(expectedProduct, topProducts);
    }

    [Fact]
    public async Task English_query_reaches_swedish_content_cross_lingually()
    {
        if (BuildDemoPack() is not { } pack) return;

        var hits = await pack.SearchAsync("handheld scanner rated for freezer temperatures", 5);
        var topProduct = hits.FirstOrDefault(h => h.Kind == "product")?.DocId;

        Assert.Contains(topProduct, new[] { "prod:du-x60", "prod:du-x50" });
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_packPath); } catch { }
    }
}
