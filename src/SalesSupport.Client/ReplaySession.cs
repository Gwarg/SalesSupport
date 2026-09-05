using System.IO;
using SalesSupport.Core.Model;
using SalesSupport.Core.Serialization;

namespace SalesSupport.Client;

/// <summary>
/// Scripted-call source for the panel: feeds a samples/calls/*.jsonl file up the hub at
/// conversation pace instead of live capture + STT. The backend cannot tell the
/// difference (D20 — it only ever receives utterance events), so the panel behaves
/// exactly as in a live call. Development QA and screen-share demos without a phone.
/// </summary>
public sealed class ReplaySession : IDisposable
{
    // Speech-rate pacing: the partial stays up for as long as the line takes to say
    // (~50 ms per character ≈ 150 words/min, clamped 2–9 s), then the final lands and a
    // short pause follows. A fixed 4 s per utterance fed the backend faster than anyone
    // talks and left the panel 30–50 s behind the conversation in the first recorded demo.
    private static readonly TimeSpan TurnGap = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan AskDwell = TimeSpan.FromMilliseconds(2500);

    private static TimeSpan SpeakingTime(string text) =>
        TimeSpan.FromMilliseconds(Math.Clamp(text.Length * 50, 2000, 9000));

    private readonly CancellationTokenSource _cts = new();

    private sealed record ScriptLine(Speaker? Speaker, string? Text, string? Ask, string? Language, string? Customer);

    public sealed record SampleMeta(string? Language, string? Customer);

    public static SampleMeta ReadMeta(string samplePath)
    {
        var meta = ParseLines(samplePath).FirstOrDefault(l => l.Language is not null || l.Customer is not null);
        return new SampleMeta(meta?.Language, meta?.Customer);
    }

    public static ReplaySession Start(
        string samplePath,
        Action<Speaker, string> onPartial,
        Func<Utterance, Task> onFinal,
        Func<string, Task> onAsk,
        Action onCompleted)
    {
        var session = new ReplaySession();
        var ct = session._cts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                var index = 0;
                var started = Environment.TickCount64;
                foreach (var line in ParseLines(samplePath))
                {
                    if (line.Language is not null || line.Customer is not null) continue;
                    ct.ThrowIfCancellationRequested();

                    if (line.Ask is { } query)
                    {
                        await onAsk(query).ConfigureAwait(false);
                        await Task.Delay(AskDwell, ct).ConfigureAwait(false);
                        continue;
                    }

                    onPartial(line.Speaker!.Value, line.Text!);
                    await Task.Delay(SpeakingTime(line.Text!), ct).ConfigureAwait(false);
                    await onFinal(new Utterance(++index, line.Speaker!.Value, line.Text!, Environment.TickCount64 - started)).ConfigureAwait(false);
                    await Task.Delay(TurnGap, ct).ConfigureAwait(false);
                }
                onCompleted();
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);

        return session;
    }

    private static List<ScriptLine> ParseLines(string samplePath) =>
        File.ReadLines(samplePath)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(JsonDefaults.Deserialize<ScriptLine>)
            .ToList();

    public void Stop() => _cts.Cancel();

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
