using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using System.Numerics.Tensors;

namespace SalesSupport.Knowledge;

/// <summary>
/// multilingual-e5-small via ONNX Runtime (docs/knowledge-pack.md): the real semantic,
/// cross-lingual embedder. Tokenization is XLM-RoBERTa SentencePiece with the fairseq id
/// mapping (&lt;s&gt;=0, &lt;pad&gt;=1, &lt;/s&gt;=2, &lt;unk&gt;=3, regular pieces = sp_id + 1) — get this
/// wrong and embeddings are silently garbage, hence the invariant checks in tests.
/// e5 requires "query: " / "passage: " prefixes and mean pooling + L2 normalization.
/// </summary>
public sealed class OnnxEmbedder : IEmbedder, IDisposable
{
    private const int MaxTokens = 512;

    private readonly InferenceSession _session;
    private readonly SentencePieceTokenizer _tokenizer;
    private readonly string[] _inputNames;

    public string ModelId { get; }
    public int Dims => 384;

    private OnnxEmbedder(InferenceSession session, SentencePieceTokenizer tokenizer, string modelId)
    {
        _session = session;
        _tokenizer = tokenizer;
        _inputNames = [.. session.InputMetadata.Keys];
        ModelId = modelId;
    }

    public static OnnxEmbedder Load(string modelDir, bool quantized = true)
    {
        var onnxFile = quantized ? "model_quantized.onnx" : "model.onnx";
        var onnxPath = Path.Combine(modelDir, onnxFile);
        var spPath = Path.Combine(modelDir, "sentencepiece.bpe.model");

        if (!File.Exists(onnxPath) || !File.Exists(spPath))
            throw new FileNotFoundException(
                $"Embedder model files missing in {modelDir} (need {onnxFile} + sentencepiece.bpe.model). " +
                "Run: dotnet run --project src/SalesSupport.Pipeline -- fetch-model");

        using var spStream = File.OpenRead(spPath);
        var tokenizer = SentencePieceTokenizer.Create(spStream, addBeginningOfSentence: false, addEndOfSentence: false);
        var session = new InferenceSession(onnxPath);
        return new OnnxEmbedder(session, tokenizer, quantized ? "multilingual-e5-small-q8" : "multilingual-e5-small-fp32");
    }

    public float[] Embed(string text, bool isQuery)
    {
        var ids = TokenizeXlmR((isQuery ? "query: " : "passage: ") + text);
        var seqLen = ids.Length;

        var inputIds = new DenseTensor<long>(ids, [1, seqLen]);
        var mask = new DenseTensor<long>(new long[seqLen], [1, seqLen]);
        mask.Fill(1);

        var inputs = new List<NamedOnnxValue>();
        foreach (var name in _inputNames)
        {
            inputs.Add(name switch
            {
                "input_ids" => NamedOnnxValue.CreateFromTensor(name, inputIds),
                "attention_mask" => NamedOnnxValue.CreateFromTensor(name, mask),
                "token_type_ids" => NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(new long[seqLen], [1, seqLen])),
                _ => throw new InvalidOperationException($"Unexpected model input '{name}'"),
            });
        }

        using var results = _session.Run(inputs);
        var hidden = results[0].AsTensor<float>();

        var vector = new float[Dims];
        for (var token = 0; token < seqLen; token++)
            for (var dim = 0; dim < Dims; dim++)
                vector[dim] += hidden[0, token, dim];
        TensorPrimitives.Divide(vector, seqLen, vector);

        var norm = TensorPrimitives.Norm(vector);
        if (norm > 0) TensorPrimitives.Divide(vector, norm, vector);
        return vector;
    }

    /// <summary>[&lt;s&gt;=0] + (sp_id 0 → 3, else sp_id + 1) + [&lt;/s&gt;=2], truncated to MaxTokens.</summary>
    internal long[] TokenizeXlmR(string text)
    {
        var spIds = _tokenizer.EncodeToIds(text);
        var ids = new List<long>(Math.Min(spIds.Count, MaxTokens - 2) + 2) { 0 };
        foreach (var id in spIds)
        {
            if (ids.Count >= MaxTokens - 1) break;
            ids.Add(id == 0 ? 3 : id + 1);
        }
        ids.Add(2);
        return [.. ids];
    }

    public void Dispose() => _session.Dispose();
}
