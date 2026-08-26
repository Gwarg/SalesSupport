using System.Text.Json.Nodes;
using SalesSupport.Core.Contracts;
using SalesSupport.Core.Model;
using SalesSupport.Core.Serialization;
using SalesSupport.Providers.Ollama;

namespace SalesSupport.Core.Tests;

public class OllamaProviderTests
{
    [Fact]
    public void Request_carries_messages_schema_and_options()
    {
        var conversation = new LlmConversation(
            "system text",
            [LlmMessage.User("user text"), LlmMessage.Assistant("assistant text")]);
        var config = new OllamaRoleConfig { Model = "qwen2.5:7b", NumCtx = 8192, Temperature = 0.1 };

        var request = OllamaLlmProvider.BuildRequest(config, "15m", conversation, JsonSchemaFactory.For<GateDiff>());

        Assert.Equal("qwen2.5:7b", request["model"]!.GetValue<string>());
        Assert.False(request["stream"]!.GetValue<bool>());
        Assert.Equal("15m", request["keep_alive"]!.GetValue<string>());
        Assert.Equal(8192, request["options"]!["num_ctx"]!.GetValue<int>());

        var messages = request["messages"]!.AsArray();
        Assert.Equal(3, messages.Count);
        Assert.Equal("system", messages[0]!["role"]!.GetValue<string>());
        Assert.Equal("user", messages[1]!["role"]!.GetValue<string>());
        Assert.Equal("assistant", messages[2]!["role"]!.GetValue<string>());

        var format = request["format"]!.AsObject();
        Assert.Equal("object", format["type"]!.GetValue<string>());
        Assert.True(format["properties"]!.AsObject().ContainsKey("facts_upsert"));
        Assert.False(request.ContainsKey("think"));
    }

    [Fact]
    public void Think_false_is_sent_for_thinking_models_and_omitted_otherwise()
    {
        var conversation = new LlmConversation("s", []);
        var schema = JsonSchemaFactory.For<GateDiff>();

        var thinking = OllamaLlmProvider.BuildRequest(
            new OllamaRoleConfig { Model = "qwen3:8b", Think = false }, "5m", conversation, schema);
        Assert.False(thinking["think"]!.GetValue<bool>());

        var plain = OllamaLlmProvider.BuildRequest(
            new OllamaRoleConfig { Model = "gemma3:4b" }, "5m", conversation, JsonSchemaFactory.For<GateDiff>());
        Assert.False(plain.ContainsKey("think"));
    }

    [Fact]
    public void Response_content_extracts_the_assistant_json()
    {
        var content = OllamaLlmProvider.ParseResponseContent(
            """{"model":"qwen2.5:7b","created_at":"2026-08-25T10:00:00Z","message":{"role":"assistant","content":"{\"answer\":42}"},"done":true}""");

        Assert.Equal("""{"answer":42}""", content);
    }

    [Fact]
    public void Schema_node_serializes_into_request_without_mutation()
    {
        var schema = JsonSchemaFactory.For<AdvisorResult>();
        var request = OllamaLlmProvider.BuildRequest(
            new OllamaRoleConfig { Model = "m" }, "5m", new LlmConversation("s", []), schema);

        var roundtripped = JsonNode.Parse(request.ToJsonString())!["format"]!.AsObject();
        Assert.True(roundtripped["properties"]!.AsObject().ContainsKey("questions"));
        Assert.True(roundtripped["properties"]!.AsObject().ContainsKey("thread_updates"));
    }
}
