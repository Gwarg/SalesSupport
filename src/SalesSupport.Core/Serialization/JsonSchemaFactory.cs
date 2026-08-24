using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;

namespace SalesSupport.Core.Serialization;

/// <summary>
/// Derives JSON schemas from the model types using the same serializer options that parse
/// the responses — so schema and parser can never drift apart. Consumed by every provider
/// that enforces structured output (Claude structured outputs now, vLLM/Ollama guided
/// decoding later — D14).
/// </summary>
public static class JsonSchemaFactory
{
    private static readonly ConcurrentDictionary<Type, JsonNode> Cache = new();

    private static readonly JsonSchemaExporterOptions ExporterOptions = new()
    {
        TreatNullObliviousAsNonNullable = true,
        TransformSchemaNode = (_, schema) =>
        {
            if (schema is JsonObject obj
                && obj.TryGetPropertyValue("type", out var typeNode)
                && typeNode is JsonValue value
                && value.TryGetValue<string>(out var type)
                && type == "object"
                && !obj.ContainsKey("additionalProperties"))
            {
                obj["additionalProperties"] = false;
            }
            return schema;
        },
    };

    public static JsonNode For(Type type) =>
        Cache.GetOrAdd(type, t => JsonSchemaExporter.GetJsonSchemaAsNode(JsonDefaults.Options, t, ExporterOptions)).DeepClone();

    public static JsonNode For<T>() => For(typeof(T));
}
