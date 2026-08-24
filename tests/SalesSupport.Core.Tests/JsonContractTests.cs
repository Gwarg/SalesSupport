using System.Text.Json.Nodes;
using SalesSupport.Core.Model;
using SalesSupport.Core.Serialization;

namespace SalesSupport.Core.Tests;

public class JsonContractTests
{
    [Fact]
    public void Enums_serialize_as_snake_case_strings()
    {
        Assert.Equal("\"customer_question\"", JsonDefaults.Serialize(ThreadKind.CustomerQuestion));
        Assert.Equal("\"buying_signal\"", JsonDefaults.Serialize(SignalType.BuyingSignal));
        Assert.Equal("\"rep\"", JsonDefaults.Serialize(Source.Rep));
    }

    [Fact]
    public void GateDiff_roundtrips_through_snake_case_json()
    {
        var diff = new GateDiff
        {
            FactsUpsert = [new FactUpsert(null, FactCategory.Pain, "batterier dör i frysen", Source.Call, Confidence.High)],
            ThreadsUpsert = [new ThreadUpsert("t1", "kyllager", ThreadKind.Objection, ThreadStatus.Open, Salience.High, "obehandlad")],
            Advice = new AdviceDecision(true, "ny invändning", ["t1"]),
        };

        var json = JsonDefaults.Serialize(diff);
        var back = JsonDefaults.Deserialize<GateDiff>(json);

        Assert.Contains("\"facts_upsert\"", json);
        Assert.Contains("\"advice_needed\"", json.Replace("\"needed\"", "\"advice_needed\""));
        Assert.Single(back.FactsUpsert);
        Assert.Equal(ThreadKind.Objection, back.ThreadsUpsert[0].Kind);
        Assert.True(back.Advice.Needed);
    }

    [Fact]
    public void Schema_for_GateDiff_is_a_closed_object_with_expected_properties()
    {
        var schema = JsonSchemaFactory.For<GateDiff>().AsObject();

        Assert.Equal("object", schema["type"]!.GetValue<string>());
        Assert.False(schema["additionalProperties"]!.GetValue<bool>());
        var properties = schema["properties"]!.AsObject();
        Assert.True(properties.ContainsKey("facts_upsert"));
        Assert.True(properties.ContainsKey("advice"));
        Assert.True(properties.ContainsKey("questions_addressed"));
    }

    [Fact]
    public void Schema_for_AdvisorResult_contains_panel_lists()
    {
        var schema = JsonSchemaFactory.For<AdvisorResult>().AsObject();

        var properties = schema["properties"]!.AsObject();
        Assert.True(properties.ContainsKey("questions"));
        Assert.True(properties.ContainsKey("products"));
        Assert.True(properties.ContainsKey("thread_updates"));
        Assert.True(properties.ContainsKey("answer"));
    }

    [Fact]
    public void Schema_encodes_enum_values_as_snake_case()
    {
        var schema = JsonSchemaFactory.For<ConversationThread>().AsObject();
        var kind = schema["properties"]!.AsObject()["kind"]!.AsObject();
        var values = kind["enum"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();

        Assert.Contains("customer_question", values);
        Assert.Contains("objection", values);
    }
}
