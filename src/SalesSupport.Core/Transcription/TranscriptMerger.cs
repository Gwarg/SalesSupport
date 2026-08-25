using System.Runtime.CompilerServices;
using System.Threading.Channels;
using SalesSupport.Core.Contracts;
using SalesSupport.Core.Model;

namespace SalesSupport.Core.Transcription;

public abstract record TranscriptEvent
{
    /// <summary>Transient in-progress text for the panel's live line — never becomes state.</summary>
    public sealed record PartialUpdated(Speaker Speaker, string Text) : TranscriptEvent;

    /// <summary>A finalized, indexed utterance — the orchestrator's tick input.</summary>
    public sealed record UtteranceFinalized(Utterance Utterance) : TranscriptEvent;
}

public sealed class TranscriptMergerOptions
{
    /// <summary>How long a finalized utterance waits for a same-speaker continuation before dispatch.</summary>
    public TimeSpan CoalesceWindow { get; init; } = TimeSpan.FromMilliseconds(400);

    /// <summary>Max audio-time gap between finals that still counts as one utterance.</summary>
    public TimeSpan CoalesceMaxGap { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// Fans in per-channel TranscriptSegment streams (mic = rep, loopback = customer, D9) into
/// one ordered utterance feed. Finals are emitted in arrival order (no reordering buffer —
/// zero added latency; rare inversions on overlapping speech are tolerated downstream) and
/// consecutive same-speaker finals within a short window and gap are coalesced into one
/// utterance, because STT engines split spoken thoughts at pauses and each utterance costs
/// a gate tick (D27). A different-speaker final flushes immediately, preserving turn order.
/// </summary>
public static class TranscriptMerger
{
    public static async IAsyncEnumerable<TranscriptEvent> MergeAsync(
        IReadOnlyList<IAsyncEnumerable<TranscriptSegment>> sources,
        TranscriptMergerOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        options ??= new TranscriptMergerOptions();
        var channel = Channel.CreateUnbounded<TranscriptSegment>();
        var remaining = sources.Count;

        foreach (var source in sources)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await foreach (var segment in source.WithCancellation(ct).ConfigureAwait(false))
                        channel.Writer.TryWrite(segment);
                    if (Interlocked.Decrement(ref remaining) == 0)
                        channel.Writer.TryComplete();
                }
                catch (OperationCanceledException)
                {
                    channel.Writer.TryComplete();
                }
                catch (Exception ex)
                {
                    channel.Writer.TryComplete(ex);
                }
            }, CancellationToken.None);
        }

        var index = 0;
        Pending? pending = null;

        while (true)
        {
            TranscriptSegment? segment;
            try
            {
                segment = await ReadNextAsync(channel.Reader, pending?.Deadline, ct).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                break;
            }

            if (segment is null)
            {
                if (pending is not null)
                {
                    yield return Flush(pending, ++index);
                    pending = null;
                }
                continue;
            }

            if (!segment.IsFinal)
            {
                yield return new TranscriptEvent.PartialUpdated(segment.Speaker, segment.Text);
                continue;
            }

            if (pending is not null)
            {
                if (segment.Speaker == pending.Speaker &&
                    segment.Offset - pending.End <= options.CoalesceMaxGap)
                {
                    pending = pending with
                    {
                        Text = pending.Text + " " + segment.Text,
                        End = segment.Offset + segment.Duration,
                        Deadline = Environment.TickCount64 + (long)options.CoalesceWindow.TotalMilliseconds,
                    };
                    continue;
                }

                yield return Flush(pending, ++index);
            }

            pending = new Pending(
                segment.Speaker, segment.Text, segment.Offset, segment.Offset + segment.Duration,
                Environment.TickCount64 + (long)options.CoalesceWindow.TotalMilliseconds);
        }

        if (pending is not null)
            yield return Flush(pending, ++index);
    }

    private sealed record Pending(Speaker Speaker, string Text, TimeSpan Start, TimeSpan End, long Deadline);

    private static TranscriptEvent Flush(Pending pending, int index) =>
        new TranscriptEvent.UtteranceFinalized(
            new Utterance(index, pending.Speaker, pending.Text, (long)pending.Start.TotalMilliseconds));

    /// <summary>Next segment, or null when the pending deadline passes first. Throws ChannelClosedException when done.</summary>
    private static async Task<TranscriptSegment?> ReadNextAsync(
        ChannelReader<TranscriptSegment> reader, long? deadline, CancellationToken ct)
    {
        if (deadline is null)
            return await reader.ReadAsync(ct).ConfigureAwait(false);

        var remainingMs = deadline.Value - Environment.TickCount64;
        if (remainingMs <= 0) return null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter((int)remainingMs);
        try
        {
            return await reader.ReadAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }
}
