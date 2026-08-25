using System.Collections.Concurrent;
using SalesSupport.Core.Contracts;
using SalesSupport.Core.Model;
using SalesSupport.Core.Serialization;
using SalesSupport.Orchestrator;

namespace SalesSupport.Backend;

/// <summary>
/// Hosts one CallOrchestrator per connected call (D20: the orchestrator lives here, the
/// client stays thin). Hub-agnostic and fully testable: methods return envelopes, the hub
/// fans them out as SignalR events. Utterance/ask processing is serialized per session;
/// ask-preempts-tick (D15) is a later refinement, noted where it belongs.
/// </summary>
public sealed class CallSessionService(
    ILlmProvider llm,
    IKnowledgeSource knowledge,
    IReadOnlyList<string> sttVocabulary,
    StorageService storage,
    BackendOptions options)
{
    private sealed class Session
    {
        public required string CallId { get; init; }
        public required CallOrchestrator Orchestrator { get; init; }
        public required string Language { get; init; }
        public string? CustomerRef { get; init; }
        public DateTime StartedAt { get; } = DateTime.UtcNow;
        public SemaphoreSlim Lock { get; } = new(1, 1);
        public int Turn;
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
        });

        var session = new Session
        {
            CallId = Guid.NewGuid().ToString("N")[..12],
            Orchestrator = orchestrator,
            Language = language,
            CustomerRef = request.CustomerCompany,
        };
        _sessions[connectionId] = session;

        if (!string.IsNullOrWhiteSpace(request.CustomerCompany) || !string.IsNullOrWhiteSpace(request.Goal))
        {
            var brief = $"Kund: {request.CustomerCompany ?? "(okänd)"}\nMål med samtalet: {request.Goal ?? "(ej angivet)"}";
            await orchestrator.SeedFromBriefAsync(brief, ct);
        }

        return new CallStarted(session.CallId, language, sttVocabulary, orchestrator.Picture, stt);
    }

    public async Task<TickEnvelope> HandleUtteranceAsync(string connectionId, UtteranceIn utterance, CancellationToken ct = default)
    {
        var session = Require(connectionId);
        await session.Lock.WaitAsync(ct);
        try
        {
            var turn = ++session.Turn;
            var tick = await session.Orchestrator.OnUtteranceAsync(
                new Utterance(turn, utterance.Speaker, utterance.Text, utterance.TimestampMs), ct);

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
            var result = await session.Orchestrator.AskAsync(query, ct);
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
        if (!_sessions.TryRemove(connectionId, out var session)) return null;

        await session.Lock.WaitAsync(ct);
        try
        {
            SummaryResult summary;
            if (generateSummary && session.Turn > 0)
            {
                try
                {
                    summary = await session.Orchestrator.EndCallAsync(ct);
                }
                catch (Exception ex)
                {
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

            return new SummaryEnvelope(summary);
        }
        finally
        {
            session.Lock.Release();
        }
    }

    private Session Require(string connectionId) =>
        _sessions.TryGetValue(connectionId, out var session)
            ? session
            : throw new InvalidOperationException("No active call for this connection — send StartCall first.");
}
