using System.Numerics.Tensors;
using SalesSupport.Knowledge;

namespace SalesSupport.Core.Tests;

/// <summary>
/// These tests need the downloaded model files (run `SalesSupport.Pipeline fetch-model`)
/// and soft-skip when they are absent, so CI without the 120 MB download stays green.
/// </summary>
public class OnnxEmbedderTests
{
    private static string? FindModelDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SalesSupport.slnx")))
            dir = dir.Parent;
        if (dir is null) return null;
        var modelDir = Path.Combine(dir.FullName, "models", "multilingual-e5-small");
        return File.Exists(Path.Combine(modelDir, "model_quantized.onnx"))
               && File.Exists(Path.Combine(modelDir, "sentencepiece.bpe.model"))
            ? modelDir
            : null;
    }

    [Fact]
    public void Tokenization_respects_the_fairseq_id_contract()
    {
        if (FindModelDir() is not { } modelDir) return;
        using var embedder = OnnxEmbedder.Load(modelDir);

        var ids = embedder.TokenizeXlmR("query: frysklassad handskanner för lager");

        Assert.Equal(0, ids[0]);
        Assert.Equal(2, ids[^1]);
        Assert.DoesNotContain(1, ids);
        Assert.True(ids.Length > 5, "expected several sentence pieces");
        Assert.All(ids[1..^1], id => Assert.True(id >= 3, $"regular piece id {id} below fairseq offset"));
    }

    [Fact]
    public void Embeddings_are_normalized_and_semantically_sane_across_languages()
    {
        if (FindModelDir() is not { } modelDir) return;
        using var embedder = OnnxEmbedder.Load(modelDir);

        var query = embedder.Embed("en skanner som klarar fryslager", isQuery: true);
        var freezer = embedder.Embed("Frysklassad handdatorskanner för lager, kyl och frys.", isQuery: false);
        var freezerEn = embedder.Embed("Freezer-rated handheld scanner for cold storage warehouses.", isQuery: false);
        var unrelated = embedder.Embed("Etikettrulle för djupfrysmärkning som fäster ner till -35 grader.", isQuery: false);

        Assert.Equal(1.0, TensorPrimitives.Norm(query), 2);

        var simFreezer = TensorPrimitives.Dot<float>(query, freezer);
        var simFreezerEn = TensorPrimitives.Dot<float>(query, freezerEn);
        var simUnrelated = TensorPrimitives.Dot<float>(query, unrelated);

        Assert.True(simFreezer > simUnrelated, $"sv match {simFreezer:F3} should beat unrelated {simUnrelated:F3}");
        Assert.True(simFreezerEn > simUnrelated, $"cross-lingual match {simFreezerEn:F3} should beat unrelated {simUnrelated:F3}");
    }
}
