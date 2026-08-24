using SalesSupport.Core.Contracts;
using SalesSupport.Core.Merging;
using SalesSupport.Core.Model;

namespace SalesSupport.Orchestrator;

public sealed record TickResult(
    Utterance Utterance,
    GateDiff Diff,
    MergeOutcome Merge,
    bool AdvisorRan,
    PanelDelta? PanelDelta);

public sealed record AskResult(string Answer, PanelDelta PanelDelta);

/// <summary>
/// The tick loop (DESIGN.md §3): gate on every utterance → merge → damping floor →
/// per-topic retrieval → one advisor call → panel reconcile. Rep queries (ask lane, D15)
/// bypass the gate and the damping floor.
/// </summary>
public sealed class CallOrchestrator(ILlmProvider llm, IKnowledgeSource knowledge, OrchestratorOptions options)
{
    private readonly List<Utterance> _transcript = [];
    private int _lastAdviceTurn = -1_000_000;

    public CustomerPicture Picture { get; } = new();
    public PanelSession Panel { get; } = new();
    public List<string> RollingSummary { get; } = [];
    public List<Fact> Tombstones { get; } = [];

    public async Task<TickResult> OnUtteranceAsync(Utterance utterance, CancellationToken ct = default)
    {
        var turn = utterance.Index;
        var window = _transcript.TakeLast(options.GateWindow).ToList();
        _transcript.Add(utterance);

        var gateConversation = PromptBuilder.Gate(Picture, window, utterance, Panel.ActiveQuestions, options);
        var diff = await llm.CompleteJsonAsync<GateDiff>(LlmRole.Gate, gateConversation, ct);

        var merge = PictureMerger.Apply(Picture, diff, turn);
        Tombstones.AddRange(merge.RemovedFacts);
        Panel.MarkAsked(diff.QuestionsAddressed);
        if (!string.IsNullOrWhiteSpace(diff.SummaryAppend)) RollingSummary.Add(diff.SummaryAppend);

        var advisorRan = false;
        PanelDelta? delta = null;

        if (diff.Advice.Needed && turn - _lastAdviceTurn >= options.MinTurnsBetweenAdvice)
        {
            _lastAdviceTurn = turn;
            advisorRan = true;

            var cards = await RetrieveForTopicsAsync(diff.Advice.Topics, ct);
            var advisorConversation = PromptBuilder.Advisor(Picture, cards, Panel, knowledge.GetCatalogMap(), options);
            var result = await llm.CompleteJsonAsync<AdvisorResult>(LlmRole.Advisor, advisorConversation, ct);

            delta = Panel.Reconcile(result);
            PictureMerger.ApplyThreadUpdates(Picture, result.ThreadUpdates, turn);
        }

        return new TickResult(utterance, diff, merge, advisorRan, delta);
    }

    /// <summary>Ask lane (D15): fires the advisor directly; the answer renders in zone 4.</summary>
    public async Task<AskResult> AskAsync(string query, CancellationToken ct = default)
    {
        var cards = await knowledge.SearchAsync(query, options.RetrievalK, ct: ct);
        var conversation = PromptBuilder.Advisor(Picture, cards, Panel, knowledge.GetCatalogMap(), options, repQuery: query);
        var result = await llm.CompleteJsonAsync<AdvisorResult>(LlmRole.Advisor, conversation, ct);

        var delta = Panel.Reconcile(result);
        return new AskResult(result.Answer ?? "", delta);
    }

    public async Task<SummaryResult> EndCallAsync(CancellationToken ct = default)
    {
        var conversation = PromptBuilder.Summarizer(Picture, RollingSummary, options);
        return await llm.CompleteJsonAsync<SummaryResult>(LlmRole.Summarizer, conversation, ct);
    }

    private async Task<IReadOnlyList<RetrievedCard>> RetrieveForTopicsAsync(IEnumerable<string> topics, CancellationToken ct)
    {
        var seen = new HashSet<string>();
        var cards = new List<RetrievedCard>();
        foreach (var topic in topics)
        {
            var query = Picture.Threads.FirstOrDefault(t => t.Id == topic)?.Topic ?? topic;
            foreach (var card in await knowledge.SearchAsync(query, options.RetrievalK, ct: ct))
            {
                if (seen.Add(card.DocId)) cards.Add(card);
            }
        }
        return cards;
    }
}
