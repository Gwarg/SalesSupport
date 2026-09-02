using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using SalesSupport.Core.Model;

namespace SalesSupport.Backend.Telephony;

/// <summary>Provider-neutral incoming-call signal — what every telephony adapter reduces to (D32).</summary>
public sealed record IncomingCallSignal(string? RawNumber, string? RepKey, string Provider, DateTime ReceivedAt);

/// <summary>
/// The stable half of D32: takes an adapter's signal, resolves the number against the
/// customer index, and pushes an IncomingCall notice to the rep's panel. A rep key
/// targets that rep's hub group; no key broadcasts to every connected panel, so a
/// single-rep installation needs no configuration beyond the webhook URL.
/// </summary>
public sealed class CallSignalService(
    CustomerIndex index,
    IHubContext<CallHub> hub,
    BackendOptions options,
    ILogger<CallSignalService>? logger = null)
{
    private readonly ILogger _log = logger ?? NullLogger<CallSignalService>.Instance;

    /// <summary>Open when no secret is configured (development); otherwise the webhook must carry it.</summary>
    public bool IsAuthorized(string? token)
    {
        if (string.IsNullOrEmpty(options.TelephonyWebhookSecret)) return true;
        var expected = Encoding.UTF8.GetBytes(options.TelephonyWebhookSecret);
        var actual = Encoding.UTF8.GetBytes(token ?? "");
        return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    public IncomingCallNotice Resolve(IncomingCallSignal signal)
    {
        var number = PhoneNumbers.Normalize(signal.RawNumber, options.DefaultCountryCode);
        var entry = index.Resolve(number);
        return new IncomingCallNotice(
            number,
            signal.Provider,
            signal.ReceivedAt,
            entry is null ? null : new ResolvedCustomer(entry.Company, entry.CrmId, entry.Notes));
    }

    public async Task HandleAsync(IncomingCallSignal signal, CancellationToken ct = default)
    {
        var notice = Resolve(signal);
        _log.LogInformation("incoming call via {Provider}: {Number} -> {Customer} rep={Rep}",
            signal.Provider, notice.Number ?? "(hidden)", notice.Customer?.Company ?? "(unresolved)", signal.RepKey ?? "*");

        var target = signal.RepKey is { Length: > 0 } rep
            ? hub.Clients.Group(CallHub.GroupFor(rep))
            : hub.Clients.All;
        await target.SendAsync("IncomingCall", notice, ct);
    }
}
