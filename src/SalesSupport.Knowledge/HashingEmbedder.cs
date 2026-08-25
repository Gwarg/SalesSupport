using System.Numerics.Tensors;

namespace SalesSupport.Knowledge;

/// <summary>
/// Deterministic character-trigram feature-hashing embedder: no model download, no
/// dependencies, works offline. Captures lexical similarity (shared word fragments),
/// not semantics — good enough to exercise the full vector path (BLOB storage, cosine,
/// RRF fusion) until the ONNX multilingual embedder (multilingual-e5-small) replaces it
/// behind the same interface.
/// </summary>
public sealed class HashingEmbedder : IEmbedder
{
    public string ModelId => "hashing-trigram-v1";
    public int Dims => 256;

    public float[] Embed(string text, bool isQuery)
    {
        var vector = new float[Dims];
        var normalized = $" {text.ToLowerInvariant()} ";

        for (var i = 0; i + 3 <= normalized.Length; i++)
        {
            var hash = Fnv1a(normalized.AsSpan(i, 3));
            var index = (int)(hash % (uint)Dims);
            var sign = ((hash >> 16) & 1) == 0 ? 1f : -1f;
            vector[index] += sign;
        }

        var norm = TensorPrimitives.Norm(vector);
        if (norm > 0) TensorPrimitives.Divide(vector, norm, vector);
        return vector;
    }

    private static uint Fnv1a(ReadOnlySpan<char> chars)
    {
        var hash = 2166136261u;
        foreach (var c in chars)
        {
            hash ^= c;
            hash *= 16777619u;
        }
        return hash;
    }
}
