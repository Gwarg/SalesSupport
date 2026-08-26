namespace SalesSupport.Core.Model;

// The client↔backend wire contract (DESIGN.md §3), shared by the backend hub and the
// WPF client. Client→backend: StartCall (carries the pre-call card, D16), Utterance,
// Ask, EndCall. Backend→client events: TranscriptAppended, PictureUpdated, PanelDelta,
// TickCompleted, AnswerReady, SummaryReady.

public sealed record StartCallRequest(string? Language, string? CustomerCompany, string? Goal);

public sealed record SttSession(string Token, string Region, int ExpiresInSeconds);

public sealed record CallStarted(
    string CallId,
    string Language,
    IReadOnlyList<string> PhraseHints,
    CustomerPicture Picture,
    SttSession? Stt);

public sealed record UtteranceIn(Speaker Speaker, string Text, long TimestampMs);

public sealed record TranscriptEntry(int Turn, Speaker Speaker, string Text, long TimestampMs);

public sealed record TickStats(
    bool AdvisorRan, bool Damped, long GateMs, long AdvisorMs, IReadOnlyList<string> QuestionsAddressed);

/// <summary>Everything one utterance produced — the hub fans these out as separate events.</summary>
public sealed record TickEnvelope(
    TranscriptEntry Transcript,
    CustomerPicture? Picture,
    PanelDelta? PanelDelta,
    TickStats Stats);

public sealed record AnswerEnvelope(string Answer, PanelDelta PanelDelta);

public sealed record SummaryEnvelope(SummaryResult Summary);
