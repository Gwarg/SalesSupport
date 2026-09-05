using SalesSupport.Core.Contracts;
using SalesSupport.Core.Model;
using SalesSupport.Core.Recording;
using SalesSupport.Core.Serialization;

namespace SalesSupport.Core.Tests;

/// <summary>Record once, replay for free: the zero-cost demo path.</summary>
public class RecordingProviderTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"recording-{Guid.NewGuid():N}.jsonl");

    [Fact]
    public async Task Record_then_replay_serves_the_same_response_without_the_inner_provider()
    {
        var inner = new CountingProvider();
        var recorder = new RecordingLlmProvider(inner, _path, RecordingMode.Record);
        var conversation = new LlmConversation("system", [LlmMessage.User("PICTURE:\n{}\nNEW:\n[customer] batterierna dör i frysen")]);

        var recorded = await recorder.CompleteJsonAsync<GateDiff>(LlmRole.Gate, conversation);
        var again = await recorder.CompleteJsonAsync<GateDiff>(LlmRole.Gate, conversation);

        Assert.Equal(1, inner.Calls);                       // second identical prompt is served from the recording
        Assert.Equal(1, recorder.Count);
        Assert.Single(recorded.FactsUpsert);
        Assert.Equal(JsonDefaults.Serialize(recorded), JsonDefaults.Serialize(again));

        var replay = new RecordingLlmProvider(null, _path, RecordingMode.Replay, replayLatency: false);
        var replayed = await replay.CompleteJsonAsync<GateDiff>(LlmRole.Gate, conversation);

        Assert.Equal(JsonDefaults.Serialize(recorded), JsonDefaults.Serialize(replayed));
        Assert.Equal(0, replay.Misses);
    }

    [Fact]
    public async Task Replay_miss_degrades_to_a_neutral_response_and_is_counted()
    {
        var log = new List<string>();
        var replay = new RecordingLlmProvider(null, _path, RecordingMode.Replay, replayLatency: false, log.Add);
        var conversation = new LlmConversation("system", [LlmMessage.User("NEW:\n[rep] något oinspelat")]);

        var diff = await replay.CompleteJsonAsync<GateDiff>(LlmRole.Gate, conversation);
        var summary = await replay.CompleteJsonAsync<SummaryResult>(LlmRole.Summarizer, conversation);

        Assert.False(diff.Advice.Needed);
        Assert.Empty(diff.FactsUpsert);
        Assert.Contains("ingen inspelad", summary.Summary);
        Assert.Equal(2, replay.Misses);
        Assert.Contains(log, m => m.Contains("något oinspelat"));
    }

    [Fact]
    public void Record_mode_requires_an_inner_provider() =>
        Assert.Throws<ArgumentException>(() => new RecordingLlmProvider(null, _path, RecordingMode.Record));

    public void Dispose() => File.Delete(_path);

    private sealed class CountingProvider : ILlmProvider
    {
        public int Calls { get; private set; }

        public Task<T> CompleteJsonAsync<T>(LlmRole role, LlmConversation conversation, CancellationToken ct = default)
            where T : class
        {
            Calls++;
            object result = new GateDiff
            {
                FactsUpsert = [new FactUpsert(null, FactCategory.Pain, "batterierna dör i frysen", Source.Call, Confidence.High)],
                Advice = new AdviceDecision(true, "ny smärtpunkt", ["t1"]),
            };
            return Task.FromResult((T)result);
        }
    }
}
