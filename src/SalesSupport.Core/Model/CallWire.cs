namespace SalesSupport.Core.Model;

// The client↔backend wire contract (DESIGN.md §3), shared by the backend hub and the
// WPF client. Client→backend: Register (rep key, D32), StartCall (carries the pre-call
// card, D16), Utterance, Ask, EndCall. Backend→client events: IncomingCall (D32),
// TranscriptAppended, PictureUpdated, PanelDelta, TickCompleted, AnswerReady, SummaryReady.

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
    bool AdvisorRan, bool Damped, long GateMs, long AdvisorMs, long QueueMs, IReadOnlyList<string> QuestionsAddressed);

/// <summary>Everything one utterance produced — the hub fans these out as separate events.</summary>
public sealed record TickEnvelope(
    TranscriptEntry Transcript,
    CustomerPicture? Picture,
    PanelDelta? PanelDelta,
    TickStats Stats);

public sealed record AnswerEnvelope(string Answer, PanelDelta PanelDelta);

public sealed record SummaryEnvelope(SummaryResult Summary);

/// <summary>Customer resolved from the installation's phone index (D28/D30 customer index, D32).</summary>
public sealed record ResolvedCustomer(string Company, string? CrmId, string? Notes);

/// <summary>Backend→client: a call is ringing on the rep's phone (D32). Number is null for hidden callers.</summary>
public sealed record IncomingCallNotice(string? Number, string Provider, DateTime ReceivedAt, ResolvedCustomer? Customer);

/// <summary>URL contract between an installation's telephony setup and the backend (D32).</summary>
public static class TelephonyWire
{
    public const string TelavoxRingPath = "/api/telephony/telavox/ring";

    /// <summary>What a rep pastes into Telavox Personal Webhooks; {system.caller} is Telavox's own placeholder.</summary>
    public static string TelavoxWebhookUrl(string backendBaseUrl, string repKey) =>
        $"{backendBaseUrl.TrimEnd('/')}{TelavoxRingPath}?rep={Uri.EscapeDataString(repKey)}&caller={{system.caller}}";
}
