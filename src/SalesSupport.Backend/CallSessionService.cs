using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using SalesSupport.Core.Contracts;
using SalesSupport.Core.Model;
using SalesSupport.Core.Serialization;
using SalesSupport.Orchestrator;

namespace SalesSupport.Backend;

/// <summary>
/// Hosts one CallOrchestrator per connected call (D20: the orchestrator lives here, the
/// client stays thin). Hub-agnostic and fully testable: methods return envelopes, the hub
/// fans them out as SignalR events. Utterance/ask processing is serialized per session;
/// when inference is slower than speech, utterances queue on the session lock — QueueMs
/// makes that visible, and ending a call turns still-queued ticks into no-ops so EndCall
/// never waits behind a long backlog.
/// </summary>
public sealed class CallSessionService(
    ILlmProvider llm,
    IKnowledgeSource knowledge,
    IReadOnlyList<string> sttVocabulary,
    StorageService storage,
    BackendOptions options,
    ILogger<CallSessionService>? logger = null)
{
    private readonly ILogger _log = logger ?? NullLogger<CallSessionService>.Instance;

    private sealed class Session
    {
        public required string CallId { get; init; }
        public required CallOrchestrator Orchestrator { get; init; }
        public required string Language { get; init; }
        public string? CustomerRef { get; init; }
        public DateTime StartedAt { get; } = DateTime.UtcNow;
        public SemaphoreSlim Lock { get; } = new(1, 1);
        public int Turn;
        public volatile bool Ending;
        public int Dropped;
    }

    private readonly ConcurrentDictionary<string, Session> _sessions = new();

    public async Task<CallStarted> StartCallAsync(string connectionId, StartCallRequest request, SttSession? stt, CancellationToken ct = default)
    {
        await EndCallInternalAsync(connectionId, generateSummary: false, ct);

        var language = string.IsNullOrWhiteSpace(request.Language) ? options.DefaultLanguage : request.Language!;
        var orchestrator = new CallOrchestrator(llm, knowledge, new OrchestratorOptions
        {
            CompanyName = options.CompanyName,
            CallLanguage = language,
            UiLanguage = options.UiLanguage,
            GateStrictness = options.GateStrictness,
            SalesGuidance = options.SalesGuidance,
            CatalogMapTier = options.ResolveCatalogMapTier(),
        });

        var session = new Session
        {
            CallId = Guid.NewGuid().ToString("N")[..12],
            Orchestrator = orchestrator,
            Language = language,
            CustomerRef = request.CustomerCompany,
        };
        _sessions[connectionId] = session;
        _log.LogInformation("call {CallId} started: language={Language} customer={Customer} stt={Stt}",
            session.CallId, language, request.CustomerCompany ?? "-", stt is null ? "none" : "token");

        if (!string.IsNullOrWhiteSpace(request.CustomerCompany) || !string.IsNullOrWhiteSpace(request.Goal))
        {
            var started = Environment.TickCount64;
            var brief = $"Kund: {request.CustomerCompany ?? "(okänd)"}\nMål med samtalet: {request.Goal ?? "(ej angivet)"}";
            var seed = await orchestrator.SeedFromBriefAsync(brief, ct);
            _log.LogInformation("call {CallId} seeded in {Ms} ms: {Changed} items", session.CallId, Environment.TickCount64 - started, seed.ChangedIds.Count);
        }

        return new CallStarted(session.CallId, language, sttVocabulary, orchestrator.Picture, stt);
    }

    /// <summary>Null when the call is ending or ended — the tick is dropped, not queued behind the summary.</summary>
    public async Task<TickEnvelope?> HandleUtteranceAsync(string connectionId, UtteranceIn utterance, CancellationToken ct = default)
    {
        var session = Require(connectionId);
        var received = Environment.TickCount64;
        if (session.Ending)
        {
            Interlocked.Increment(ref session.Dropped);
            return null;
        }

        await session.Lock.WaitAsync(ct);
        try
        {
            if (session.Ending)
            {
                Interlocked.Increment(ref session.Dropped);
                return null;
            }

            var queueMs = Environment.TickCount64 - received;
            var turn = ++session.Turn;
            var tick = await session.Orchestrator.OnUtteranceAsync(
                new Utterance(turn, utterance.Speaker, utterance.Text, utterance.TimestampMs), ct);

            _log.LogInformation(
                "call {CallId} T{Turn} [{Speaker}] {Chars} chars: queue={QueueMs}ms gate={GateMs}ms advisor={Advisor} picture+{Changed} panel+q{AddQ}-q{RemQ}+p{AddP} asked={Asked}{Notes}",
                session.CallId, turn, utterance.Speaker.ToString().ToLowerInvariant(), utterance.Text.Length,
                queueMs, tick.GateMs,
                tick.AdvisorRan ? $"{tick.AdvisorMs}ms" : (tick.Diff.Advice.Needed ? "damped" : "no"),
                tick.Merge.ChangedIds.Count,
                tick.PanelDelta?.AddedQuestions.Count ?? 0, tick.PanelDelta?.RemovedQuestionIds.Count ?? 0,
                tick.PanelDelta?.AddedProducts.Count ?? 0,
                tick.Diff.QuestionsAddressed.Count,
                tick.Merge.Notes.Count > 0 ? $" notes={string.Join(";", tick.Merge.Notes)}" : "");

            var pictureChanged = tick.Merge.ChangedIds.Count > 0 ||
                                 (tick.PanelDelta is not null && !tick.PanelDelta.IsEmpty);
            return new TickEnvelope(
                new TranscriptEntry(turn, utterance.Speaker, utterance.Text, utterance.TimestampMs),
                pictureChanged ? session.Orchestrator.Picture : null,
                tick.PanelDelta is { IsEmpty: false } delta ? delta : null,
                new TickStats(
                    tick.AdvisorRan,
                    tick.Diff.Advice.Needed && !tick.AdvisorRan,
                    tick.GateMs,
                    tick.AdvisorMs,
                    queueMs,
                    tick.Diff.QuestionsAddressed));
        }
        finally
        {
            session.Lock.Release();
        }
    }

    public async Task<AnswerEnvelope> AskAsync(string connectionId, string query, CancellationToken ct = default)
    {
        var session = Require(connectionId);
        await session.Lock.WaitAsync(ct);
        try
        {
            var started = Environment.TickCount64;
            var result = await session.Orchestrator.AskAsync(query, ct);
            _log.LogInformation("call {CallId} ask ({Chars} chars) answered in {Ms} ms", session.CallId, query.Length, Environment.TickCount64 - started);
            return new AnswerEnvelope(result.Answer, result.PanelDelta);
        }
        finally
        {
            session.Lock.Release();
        }
    }

    public Task<SummaryEnvelope?> EndCallAsync(string connectionId, CancellationToken ct = default) =>
        EndCallInternalAsync(connectionId, generateSummary: true, ct);

    /// <summary>On disconnect the call is stored without spending a summarizer call (reconnect handling is a later refinement).</summary>
    public Task AbandonAsync(string connectionId) =>
        EndCallInternalAsync(connectionId, generateSummary: false, CancellationToken.None);

    private async Task<SummaryEnvelope?> EndCallInternalAsync(string connectionId, bool generateSummary, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(connectionId, out var session)) return null;
        if (session.Ending) return null;
        session.Ending = true;

        await session.Lock.WaitAsync(ct);
        try
        {
            SummaryResult summary;
            var summaryStarted = Environment.TickCount64;
            if (generateSummary && session.Turn > 0)
            {
                try
                {
                    summary = await session.Orchestrator.EndCallAsync(ct);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "call {CallId} summary failed", session.CallId);
                    summary = new SummaryResult { Summary = $"(summary failed: {ex.Message})" };
                }
            }
            else
            {
                summary = new SummaryResult { Summary = generateSummary ? "(no utterances)" : "(disconnected)" };
            }

            if (session.Turn > 0)
            {
                storage.Save(new InteractionRecord(
                    session.CallId,
                    "call",
                    session.CustomerRef,
                    session.Language,
                    session.StartedAt,
                    DateTime.UtcNow,
                    JsonDefaults.Serialize(session.Orchestrator.Transcript),
                    JsonDefaults.Serialize(session.Orchestrator.Picture),
                    JsonDefaults.Serialize(summary)));
            }

            _log.LogInformation(
                "call {CallId} ended: turns={Turns} dropped={Dropped} summary={SummaryMs}ms facts={Facts} threads={Threads} actions={Actions}",
                session.CallId, session.Turn, session.Dropped,
                generateSummary && session.Turn > 0 ? Environment.TickCount64 - summaryStarted : 0,
                session.Orchestrator.Picture.Facts.Count,
                session.Orchestrator.Picture.Threads.Count,
                session.Orchestrator.Picture.ActionItems.Count);

            return new SummaryEnvelope(summary);
        }
        finally
        {
            session.Lock.Release();
            _sessions.TryRemove(connectionId, out _);
        }
    }

    private Session Require(string connectionId) =>
        _sessions.TryGetValue(connectionId, out var session)
            ? session
            : throw new InvalidOperationException("No active call for this connection — send StartCall first.");
}
