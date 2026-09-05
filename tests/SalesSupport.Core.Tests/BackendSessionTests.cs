using SalesSupport.Backend;
using SalesSupport.Core.Contracts;
using SalesSupport.Core.Model;

namespace SalesSupport.Core.Tests;

public class BackendSessionTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"backend_{Guid.NewGuid():N}.db");

    private CallSessionService NewService(StorageService? storage = null) => new(
        new StubLlm(),
        new StubKnowledge(),
        ["X60", "LP-dock"],
        storage ?? new StorageService(_dbPath, retentionDays: 90),
        new BackendOptions());

    [Fact]
    public async Task StartCall_seeds_picture_from_the_pre_call_card()
    {
        var service = NewService();

        var started = await service.StartCallAsync("conn1", new StartCallRequest("sv", "Nordfrys AB", "följa upp skannrar"), null);

        Assert.Equal("sv", started.Language);
        Assert.Equal(["X60", "LP-dock"], started.PhraseHints);
        Assert.Single(started.Picture.Facts);
        Assert.Equal(Source.Crm, started.Picture.Facts[0].Source);
        Assert.Equal(0, started.Picture.Facts[0].Turn);
    }

    [Fact]
    public async Task Utterances_produce_envelopes_with_turns_picture_and_panel()
    {
        var service = NewService();
        await service.StartCallAsync("conn1", new StartCallRequest(null, null, null), null);

        var first = await service.HandleUtteranceAsync("conn1", new UtteranceIn(Speaker.Rep, "hej", 100));
        Assert.NotNull(first);
        Assert.Equal(1, first!.Transcript.Turn);
        Assert.NotNull(first.Picture);
        Assert.Null(first.PanelDelta);
        Assert.True(first.Stats.GateMs >= 0);
        Assert.True(first.Stats.QueueMs >= 0);

        var second = await service.HandleUtteranceAsync("conn1", new UtteranceIn(Speaker.Customer, "batterierna dör", 5000));
        Assert.NotNull(second);
        Assert.Equal(2, second!.Transcript.Turn);
        Assert.True(second.Stats.AdvisorRan);
        Assert.NotNull(second.PanelDelta);
        Assert.Single(second.PanelDelta!.AddedQuestions);
    }

    [Fact]
    public async Task Ask_returns_an_answer()
    {
        var service = NewService();
        await service.StartCallAsync("conn1", new StartCallRequest(null, null, null), null);

        var answer = await service.AskAsync("conn1", "vad kostar X60?");

        Assert.Equal("stub answer", answer.Answer);
    }

    [Fact]
    public async Task EndCall_generates_summary_and_stores_the_interaction()
    {
        var storage = new StorageService(_dbPath, retentionDays: 90);
        var service = NewService(storage);
        await service.StartCallAsync("conn1", new StartCallRequest(null, "Kund AB", null), null);
        await service.HandleUtteranceAsync("conn1", new UtteranceIn(Speaker.Rep, "hej", 0));

        var summary = await service.EndCallAsync("conn1");

        Assert.NotNull(summary);
        Assert.Equal("stub summary", summary!.Summary.Summary);
        Assert.Equal(1, storage.Count());

        Assert.Null(await service.EndCallAsync("conn1"));
    }

    [Fact]
    public async Task Utterance_without_a_call_fails_loudly()
    {
        var service = NewService();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.HandleUtteranceAsync("nobody", new UtteranceIn(Speaker.Rep, "hej", 0)));
    }

    [Fact]
    public async Task Ending_drops_queued_utterances_instead_of_processing_them()
    {
        var stub = new StubLlm { SummarizerDelayMs = 250 };
        var service = new CallSessionService(stub, new StubKnowledge(), [], new StorageService(_dbPath, 90), new BackendOptions());
        await service.StartCallAsync("conn1", new StartCallRequest(null, null, null), null);
        await service.HandleUtteranceAsync("conn1", new UtteranceIn(Speaker.Rep, "hej", 0));

        var ending = service.EndCallAsync("conn1");
        await Task.Delay(50);

        var dropped = await service.HandleUtteranceAsync("conn1", new UtteranceIn(Speaker.Customer, "för sent", 100));
        Assert.Null(dropped);

        var summary = await ending;
        Assert.NotNull(summary);
    }

    [Fact]
    public void Storage_purges_beyond_retention()
    {
        var storage = new StorageService(_dbPath, retentionDays: 30);
        storage.Save(Record("old", DateTime.UtcNow.AddDays(-40)));
        storage.Save(Record("fresh", DateTime.UtcNow));

        Assert.Equal(1, storage.Count());

        static InteractionRecord Record(string id, DateTime endedAt) =>
            new(id, "call", null, "sv", endedAt.AddMinutes(-10), endedAt, "[]", "{}", "{}");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { }
    }

    private sealed class StubLlm : ILlmProvider
    {
        private int _gateCalls;
        public int SummarizerDelayMs { get; init; }

        public async Task<T> CompleteJsonAsync<T>(LlmRole role, LlmConversation conversation, CancellationToken ct = default)
            where T : class
        {
            if (role == LlmRole.Summarizer && SummarizerDelayMs > 0)
                await Task.Delay(SummarizerDelayMs, ct);
            object result = (role, typeof(T).Name) switch
            {
                (LlmRole.Gate, nameof(GateDiff)) => NextGateDiff(),
                (LlmRole.Advisor, nameof(AdvisorResult)) => new AdvisorResult
                {
                    Questions = [new PanelQuestion(null, "stub fråga?", null)],
                    Answer = "stub answer",
                },
                (LlmRole.Summarizer, nameof(SummaryResult)) => new SummaryResult { Summary = "stub summary" },
                _ => throw new NotSupportedException($"{role}/{typeof(T).Name}"),
            };
            return (T)result;
        }

        private GateDiff NextGateDiff()
        {
            var call = ++_gateCalls;
            return new GateDiff
            {
                FactsUpsert = [new FactUpsert(null, FactCategory.Other, $"fakta {call}",
                    call == 1 ? Source.Crm : Source.Call, Confidence.Medium)],
                Advice = new AdviceDecision(call >= 2, "stub", ["ämne"]),
            };
        }
    }

    private sealed class StubKnowledge : IKnowledgeSource
    {
        public Task<IReadOnlyList<RetrievedCard>> SearchAsync(string query, int k, IReadOnlyList<string>? boostFamilies = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RetrievedCard>>([]);

        public string? ResolveAlias(string text) => null;

        public string GetCatalogMap(CatalogMapTier tier = CatalogMapTier.Full) => "stubkatalog";
    }
}
