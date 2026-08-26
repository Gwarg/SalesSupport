using SalesSupport.Core.Contracts;
using SalesSupport.Core.Merging;
using SalesSupport.Core.Model;

namespace SalesSupport.Orchestrator;

public sealed record TickResult(
    Utterance Utterance,
    GateDiff Diff,
    MergeOutcome Merge,
    bool AdvisorRan,
    PanelDelta? PanelDelta,
    long GateMs = 0,
    long AdvisorMs = 0);

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
    public IReadOnlyList<Utterance> Transcript => _transcript;

    /// <summary>
    /// Seeds the picture from a customer brief / pre-call card before the first utterance
    /// (D16/D28, docs/prompts.md "Seeder"). Reuses the gate schema; runs as turn 0.
    /// </summary>
    public async Task<MergeOutcome> SeedFromBriefAsync(string briefText, CancellationToken ct = default)
    {
        var conversation = PromptBuilder.Seeder(briefText, Picture, options);
        var diff = await llm.CompleteJsonAsync<GateDiff>(LlmRole.Gate, conversation, ct);
        return PictureMerger.Apply(Picture, diff, turn: 0);
    }

    public async Task<TickResult> OnUtteranceAsync(Utterance utterance, CancellationToken ct = default)
    {
        var turn = utterance.Index;
        var window = _transcript.TakeLast(options.GateWindow).ToList();
        _transcript.Add(utterance);

        var gateConversation = PromptBuilder.Gate(Picture, window, utterance, Panel.ActiveQuestions, options);
        var gateStarted = Environment.TickCount64;
        var diff = await llm.CompleteJsonAsync<GateDiff>(LlmRole.Gate, gateConversation, ct);
        var gateMs = Environment.TickCount64 - gateStarted;
        diff = CoerceSpokenSources(diff);

        var merge = PictureMerger.Apply(Picture, diff, turn);
        Tombstones.AddRange(merge.RemovedFacts);
        Panel.MarkAsked(diff.QuestionsAddressed);
        if (!string.IsNullOrWhiteSpace(diff.SummaryAppend)) RollingSummary.Add(diff.SummaryAppend);

        var advisorRan = false;
        PanelDelta? delta = null;
        long advisorMs = 0;

        if (diff.Advice.Needed && turn - _lastAdviceTurn >= options.MinTurnsBetweenAdvice)
        {
            _lastAdviceTurn = turn;
            advisorRan = true;

            var advisorStarted = Environment.TickCount64;
            var cards = await RetrieveForTopicsAsync(diff.Advice.Topics, ct);
            var advisorConversation = PromptBuilder.Advisor(Picture, cards, Panel, knowledge.GetCatalogMap(), options);
            var result = await llm.CompleteJsonAsync<AdvisorResult>(LlmRole.Advisor, advisorConversation, ct);
            advisorMs = Environment.TickCount64 - advisorStarted;

            delta = Panel.Reconcile(FilterProducts(result));
            PictureMerger.ApplyThreadUpdates(Picture, result.ThreadUpdates, turn);
        }

        return new TickResult(utterance, diff, merge, advisorRan, delta, gateMs, advisorMs);
    }

    /// <summary>Ask lane (D15): fires the advisor directly; the answer renders in zone 4.</summary>
    public async Task<AskResult> AskAsync(string query, CancellationToken ct = default)
    {
        var cards = await knowledge.SearchAsync(query, options.RetrievalK, ct: ct);
        var conversation = PromptBuilder.Advisor(Picture, cards, Panel, knowledge.GetCatalogMap(), options, repQuery: query);
        var result = await llm.CompleteJsonAsync<AdvisorResult>(LlmRole.Advisor, conversation, ct);

        var delta = Panel.Reconcile(FilterProducts(result));
        return new AskResult(result.Answer ?? "", delta);
    }

    public async Task<SummaryResult> EndCallAsync(CancellationToken ct = default)
    {
        var conversation = PromptBuilder.Summarizer(Picture, RollingSummary, options);
        return await llm.CompleteJsonAsync<SummaryResult>(LlmRole.Summarizer, conversation, ct);
    }

    /// <summary>
    /// Spoken-tick diffs can only carry source "call": "rep" is reserved for typed input and
    /// "crm" for the brief — and a model that mislabels sources as "rep" would otherwise lock
    /// items against future updates via the provenance guard. The code knows the channel;
    /// the model doesn't get a vote.
    /// </summary>
    internal static GateDiff CoerceSpokenSources(GateDiff diff) => new()
    {
        Signals = diff.Signals,
        CompanyUpdate = diff.CompanyUpdate is { Source: Source.Rep } company ? company with { Source = Source.Call } : diff.CompanyUpdate,
        FactsUpsert = diff.FactsUpsert.Select(f => f.Source == Source.Rep ? f with { Source = Source.Call } : f).ToList(),
        FactsRemove = diff.FactsRemove,
        ThreadsUpsert = diff.ThreadsUpsert,
        ProductInterestUpsert = diff.ProductInterestUpsert.Select(p => p.Source == Source.Rep ? p with { Source = Source.Call } : p).ToList(),
        ActionItemsUpsert = diff.ActionItemsUpsert.Select(a => a.Source == Source.Rep ? a with { Source = Source.Call } : a).ToList(),
        QuestionsAddressed = diff.QuestionsAddressed,
        SummaryAppend = diff.SummaryAppend,
        Advice = diff.Advice,
        LanguageFlag = diff.LanguageFlag,
    };

    /// <summary>Never suggest a product the customer owns or has rejected — enforced in code, not hoped for in prompt.</summary>
    private AdvisorResult FilterProducts(AdvisorResult result)
    {
        var blocked = Picture.ProductInterest
            .Where(p => p.Stance is Stance.Owns or Stance.Rejected)
            .Select(p => PictureMerger.NormalizeText(p.NameAsSaid))
            .ToHashSet();
        if (blocked.Count == 0) return result;

        var kept = result.Products
            .Where(p => p.Id is not null || !blocked.Contains(PictureMerger.NormalizeText(p.DisplayName)))
            .ToList();
        return kept.Count == result.Products.Count
            ? result
            : new AdvisorResult { Questions = result.Questions, Products = kept, ThreadUpdates = result.ThreadUpdates, Answer = result.Answer };
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
