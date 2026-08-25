using Microsoft.Data.Sqlite;

namespace SalesSupport.Knowledge;

/// <summary>
/// Constructs the embedder a pack was built with, by peeking its meta — the reader-side
/// half of the pack's embedder contract. Unknown model ids fail loudly.
/// </summary>
public static class EmbedderFactory
{
    public static string DefaultModelDir(string repoRoot) => Path.Combine(repoRoot, "models", "multilingual-e5-small");

    public static IEmbedder ForPack(string packPath, string? modelDir = null)
    {
        using var connection = new SqliteConnection($"Data Source={packPath};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = 'embedding_model'";
        var modelId = command.ExecuteScalar() as string
            ?? throw new InvalidOperationException($"Pack {packPath} has no embedding_model meta.");

        return Create(modelId, modelDir);
    }

    public static IEmbedder Create(string modelId, string? modelDir = null) => modelId switch
    {
        "hashing-trigram-v1" => new HashingEmbedder(),
        "multilingual-e5-small-q8" => OnnxEmbedder.Load(RequireDir(modelDir), quantized: true),
        "multilingual-e5-small-fp32" => OnnxEmbedder.Load(RequireDir(modelDir), quantized: false),
        _ => throw new InvalidOperationException($"Unknown embedder '{modelId}'."),
    };

    private static string RequireDir(string? modelDir) =>
        modelDir ?? throw new InvalidOperationException("This pack needs the ONNX embedder — pass the model directory.");
}
