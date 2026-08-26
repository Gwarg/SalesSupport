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
            // Nullable object properties export as "type": ["object","null"] — they need
            // the same closing as plain objects (the Claude API rejects any object schema
            // without an explicit additionalProperties: false).
            if (schema is JsonObject obj && IsObjectSchema(obj))
            {
                if (!obj.ContainsKey("additionalProperties"))
                    obj["additionalProperties"] = false;

                // Every property is required (nullable ones may still be null). Optional
                // fields are a grammar escape hatch for small models under constrained
                // decoding: observed live — a 7B gate emitted ~11-token near-empty objects
                // for an entire call because the shortest valid path skipped every field.
                if (obj.TryGetPropertyValue("properties", out var props) && props is JsonObject propsObj)
                {
                    var required = new JsonArray();
                    foreach (var (name, _) in propsObj)
                        required.Add(name);
                    obj["required"] = required;
                }
            }
            return schema;
        },
    };

    private static bool IsObjectSchema(JsonObject obj)
    {
        if (!obj.TryGetPropertyValue("type", out var typeNode)) return false;
        return typeNode switch
        {
            JsonValue value when value.TryGetValue<string>(out var type) => type == "object",
            JsonArray types => types.Any(t => t is JsonValue v && v.TryGetValue<string>(out var s) && s == "object"),
            _ => false,
        };
    }

    public static JsonNode For(Type type) =>
        Cache.GetOrAdd(type, t => JsonSchemaExporter.GetJsonSchemaAsNode(JsonDefaults.Options, t, ExporterOptions)).DeepClone();

    public static JsonNode For<T>() => For(typeof(T));
}
