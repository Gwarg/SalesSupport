using SalesSupport.Core.Contracts;
using SalesSupport.Knowledge;

namespace SalesSupport.Core.Tests;

public class PackRoundtripTests : IDisposable
{
    private readonly string _packPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.pack.sqlite");
    private readonly HashingEmbedder _embedder = new();

    private void BuildTestPack()
    {
        var family = new PackFamily
        {
            Id = "fam:skannrar", ParentId = null, Name = "Skannrar", Path = "Skannrar",
            Summary = "Handskannrar för lager, kyl och frys.",
            QuestionMap = "- Vilka temperaturzoner?",
            Embedding = _embedder.Embed("passage: Skannrar", false),
        };
        var x40 = new PackProduct
        {
            Id = "prod:du-x40", Sku = "DU-X40", Name = "X40 handskanner", FamilyId = "fam:skannrar",
            Status = "discontinued", Card = "# X40\nÄldre skanner, svag batteritid i frys.",
            Embedding = _embedder.Embed("passage: X40 äldre skanner svag batteritid frys", false),
        };
        var x60 = new PackProduct
        {
            Id = "prod:du-x60", Sku = "DU-X60", Name = "X60 handskanner", FamilyId = "fam:skannrar",
            Card = "# X60\nFrysklassad skanner för -30 grader, IP67.", PriceAmount = 6900, PriceCurrency = "SEK",
            Embedding = _embedder.Embed("passage: X60 frysklassad skanner -30 grader IP67", false),
        };

        PackBuilder.Build(
            _packPath,
            new PackMeta("test-co", "1", "sv", "abc123"),
            _embedder,
            [x40, x60],
            [family],
            [new PackAlias("DU-X40", "sku", "prod:du-x40"), new PackAlias("X-40", "spoken", "prod:du-x40"),
             new PackAlias("DU-X60", "sku", "prod:du-x60"),
             new PackAlias("skannern", "spoken", "prod:du-x40"), new PackAlias("skannern", "spoken", "prod:du-x60")],
            [new PackRelation("prod:du-x60", "prod:du-x40", "successor_of", null)],
            "Katalog: skannrar (X40 utgående, X60 frysklassad).",
            ["X40", "X60"],
            catalogMapCompact: "Katalog: X40, X60.");
    }

    [Fact]
    public async Task Search_finds_the_freezer_scanner_for_a_freezer_query()
    {
        BuildTestPack();
        var pack = SqlitePackKnowledge.Load(_packPath, _embedder);

        var hits = await pack.SearchAsync("frysklassad skanner för -30", 2);

        Assert.NotEmpty(hits);
        Assert.Equal("prod:du-x60", hits[0].DocId);
    }

    [Fact]
    public void Alias_resolution_normalizes_and_refuses_ambiguity()
    {
        BuildTestPack();
        var pack = SqlitePackKnowledge.Load(_packPath, _embedder);

        Assert.Equal("prod:du-x40", pack.ResolveAlias("x-40"));
        Assert.Equal("prod:du-x40", pack.ResolveAlias("X 40"));
        Assert.Null(pack.ResolveAlias("skannern"));
        Assert.Null(pack.ResolveAlias("okänd"));
    }

    [Fact]
    public void Load_refuses_a_pack_built_with_a_different_embedder()
    {
        BuildTestPack();
        var other = new FixedDimsEmbedder();

        var ex = Assert.Throws<InvalidOperationException>(() => SqlitePackKnowledge.Load(_packPath, other));
        Assert.Contains("embedder", ex.Message);
    }

    [Fact]
    public void Build_fails_on_broken_references()
    {
        var product = new PackProduct
        {
            Id = "prod:x", Sku = "X", Name = "X", FamilyId = "fam:missing",
            Card = "card", Embedding = _embedder.Embed("x", false),
        };

        var ex = Assert.Throws<InvalidOperationException>(() => PackBuilder.Build(
            _packPath, new PackMeta("t", "1", "sv", "s"), _embedder,
            [product], [], [], [], "map", ["x"]));
        Assert.Contains("unknown family", ex.Message);
    }

    [Fact]
    public void Catalog_map_roundtrips()
    {
        BuildTestPack();
        var pack = SqlitePackKnowledge.Load(_packPath, _embedder);

        Assert.Contains("X40 utgående", pack.GetCatalogMap());
        Assert.Equal("Katalog: X40, X60.", pack.GetCatalogMap(CatalogMapTier.Compact));
        Assert.Equal(pack.GetCatalogMap(), pack.GetCatalogMap(CatalogMapTier.Full));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_packPath); } catch { }
    }

    private sealed class FixedDimsEmbedder : IEmbedder
    {
        public string ModelId => "other-model";
        public int Dims => 8;
        public float[] Embed(string text, bool isQuery) => new float[8];
    }
}
