namespace SalesSupport.Backend.Telephony;

/// <summary>
/// The Telavox edge of D32. Telavox Personal Webhooks call a user-configured URL on
/// "ringing" with {system.caller} substituted in — GET or POST form, no fixed body
/// schema. Everything provider-specific (parameter names, the URL template in
/// TelephonyWire) stays here; the rest of the pipeline sees an IncomingCallSignal.
/// </summary>
public static class TelavoxAdapter
{
    public const string Provider = "telavox";

    /// <summary>get(key) reads a query or form parameter; null when absent.</summary>
    public static IncomingCallSignal Parse(Func<string, string?> get, DateTime? receivedAt = null) =>
        new(get("caller") ?? get("number"), get("rep"), Provider, receivedAt ?? DateTime.UtcNow);
}
