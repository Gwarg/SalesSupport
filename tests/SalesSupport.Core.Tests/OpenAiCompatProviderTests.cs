using System.Text.Json.Nodes;
using SalesSupport.Core.Contracts;
using SalesSupport.Core.Model;
using SalesSupport.Core.Serialization;
using SalesSupport.Providers.OpenAiCompat;

namespace SalesSupport.Core.Tests;

public class OpenAiCompatProviderTests
{
    [Fact]
    public void Strict_request_carries_messages_and_json_schema_response_format()
    {
        var conversation = new LlmConversation(
            "system text",
            [LlmMessage.User("user text"), LlmMessage.Assistant("assistant text")]);
        var config = new OpenAiCompatRoleConfig { Model = "z-ai/glm-5.3-flash", Temperature = 0.1, MaxTokens = 1024 };

        var request = OpenAiCompatLlmProvider.BuildRequest(
            config, conversation, JsonSchemaFactory.For<GateDiff>(), nameof(GateDiff), strictSchema: true);

        Assert.Equal("z-ai/glm-5.3-flash", request["model"]!.GetValue<string>());
        Assert.False(request["stream"]!.GetValue<bool>());
        Assert.Equal(1024, request["max_tokens"]!.GetValue<int>());

        var messages = request["messages"]!.AsArray();
        Assert.Equal(3, messages.Count);
        Assert.Equal("system", messages[0]!["role"]!.GetValue<string>());
        Assert.Equal("system text", messages[0]!["content"]!.GetValue<string>());
        Assert.Equal("user", messages[1]!["role"]!.GetValue<string>());
        Assert.Equal("assistant", messages[2]!["role"]!.GetValue<string>());

        var format = request["response_format"]!.AsObject();
        Assert.Equal("json_schema", format["type"]!.GetValue<string>());
        var jsonSchema = format["json_schema"]!.AsObject();
        Assert.Equal("GateDiff", jsonSchema["name"]!.GetValue<string>());
        Assert.True(jsonSchema["strict"]!.GetValue<bool>());
        Assert.True(jsonSchema["schema"]!["properties"]!.AsObject().ContainsKey("facts_upsert"));
    }

    [Fact]
    public void Loose_request_uses_json_object_and_embeds_the_schema_in_the_system_prompt()
    {
        var request = OpenAiCompatLlmProvider.BuildRequest(
            new OpenAiCompatRoleConfig { Model = "m" },
            new LlmConversation("system text", []),
            JsonSchemaFactory.For<GateDiff>(), nameof(GateDiff), strictSchema: false);

        Assert.Equal("json_object", request["response_format"]!["type"]!.GetValue<string>());
        var system = request["messages"]![0]!["content"]!.GetValue<string>();
        Assert.StartsWith("system text", system, StringComparison.Ordinal);
        Assert.Contains("facts_upsert", system, StringComparison.Ordinal);
    }

    [Fact]
    public void Response_parse_extracts_content_and_splits_cached_from_prompt_tokens()
    {
        var (text, usage) = OpenAiCompatLlmProvider.ParseResponse(
            """
            {"choices":[{"message":{"role":"assistant","content":"{\"answer\":42}"}}],
             "usage":{"prompt_tokens":1200,"completion_tokens":80,
                      "prompt_tokens_details":{"cached_tokens":900}}}
            """);

        Assert.Equal("""{"answer":42}""", text);
        Assert.NotNull(usage);
        Assert.Equal(300, usage!.Value.Input);
        Assert.Equal(900, usage.Value.Cached);
        Assert.Equal(80, usage.Value.Output);
    }

    [Fact]
    public void Response_parse_tolerates_missing_usage_and_fenced_content()
    {
        var (text, usage) = OpenAiCompatLlmProvider.ParseResponse(
            """{"choices":[{"message":{"content":"```json\n{\"answer\":42}\n```"}}]}""");

        Assert.Equal("""{"answer":42}""", text);
        Assert.Null(usage);
    }

    [Fact]
    public void Fence_stripping_leaves_plain_json_untouched()
    {
        Assert.Equal("""{"a":1}""", OpenAiCompatLlmProvider.StripFences("""  {"a":1}  """));
        Assert.Equal("""{"a":1}""", OpenAiCompatLlmProvider.StripFences("```\n{\"a\":1}\n```"));
    }
}
