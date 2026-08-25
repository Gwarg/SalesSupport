namespace SalesSupport.Knowledge;

/// <summary>
/// Text embedding contract shared by pipeline (passages) and backend (queries).
/// The pack records ModelId/Dims in meta; the reader refuses a pack whose embedder
/// it cannot match (docs/knowledge-pack.md — a hard contract, D14).
/// </summary>
public interface IEmbedder
{
    string ModelId { get; }
    int Dims { get; }

    /// <summary>Vectors must come back L2-normalized so dot product equals cosine similarity.</summary>
    float[] Embed(string text, bool isQuery);
}
