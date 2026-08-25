using SalesSupport.Core.Contracts;
using SalesSupport.Core.Model;
using SalesSupport.Core.Transcription;

namespace SalesSupport.Core.Tests;

public class TranscriptMergerTests
{
    // Long window: these tests flush via speaker change or stream completion, never via
    // window expiry — a generous window makes coalescing immune to JIT/GC stalls between
    // segments (the observed first-run-after-build flake). Only the expiry test uses a
    // short window, with its own options.
    private static readonly TranscriptMergerOptions FastOptions = new()
    {
        CoalesceWindow = TimeSpan.FromSeconds(2),
        CoalesceMaxGap = TimeSpan.FromSeconds(1),
    };

    private static TranscriptSegment Final(Speaker speaker, string text, double startSeconds, double durationSeconds = 1) =>
        new(speaker, text, true, TimeSpan.FromSeconds(startSeconds), TimeSpan.FromSeconds(durationSeconds));

    private static TranscriptSegment Partial(Speaker speaker, string text) =>
        new(speaker, text, false, TimeSpan.Zero, TimeSpan.Zero);

    private static async IAsyncEnumerable<TranscriptSegment> Source(params TranscriptSegment[] segments)
    {
        foreach (var segment in segments)
        {
            await Task.Delay(10);
            yield return segment;
        }
    }

    private static async Task<List<TranscriptEvent>> Collect(
        TranscriptMergerOptions options, params IAsyncEnumerable<TranscriptSegment>[] sources)
    {
        var events = new List<TranscriptEvent>();
        await foreach (var e in TranscriptMerger.MergeAsync(sources, options))
            events.Add(e);
        return events;
    }

    private static List<Utterance> Utterances(IEnumerable<TranscriptEvent> events) =>
        events.OfType<TranscriptEvent.UtteranceFinalized>().Select(e => e.Utterance).ToList();

    [Fact]
    public async Task Finals_from_both_channels_get_sequential_indexes_with_speakers()
    {
        var events = await Collect(FastOptions,
            Source(Final(Speaker.Rep, "hej", 0)),
            Source(Final(Speaker.Customer, "hejsan", 2)));

        var utterances = Utterances(events);
        Assert.Equal(2, utterances.Count);
        Assert.Equal([1, 2], utterances.Select(u => u.Index));
        Assert.Equal(2, utterances.Select(u => u.Speaker).Distinct().Count());
    }

    [Fact]
    public async Task Rapid_same_speaker_finals_coalesce_into_one_utterance()
    {
        var events = await Collect(FastOptions,
            Source(
                Final(Speaker.Customer, "vi har tolv skannrar", 0, 2),
                Final(Speaker.Customer, "och två är utbytta", 2.3, 1.5)));

        var utterances = Utterances(events);
        Assert.Single(utterances);
        Assert.Equal("vi har tolv skannrar och två är utbytta", utterances[0].Text);
        Assert.Equal(0, utterances[0].TimestampMs);
    }

    [Fact]
    public async Task Different_speaker_final_flushes_pending_and_preserves_order()
    {
        var events = await Collect(FastOptions,
            Source(
                Final(Speaker.Customer, "en fråga", 0),
                Final(Speaker.Rep, "svar", 1.2),
                Final(Speaker.Customer, "följdfråga", 2.4)));

        var utterances = Utterances(events);
        Assert.Equal(3, utterances.Count);
        Assert.Equal([Speaker.Customer, Speaker.Rep, Speaker.Customer], utterances.Select(u => u.Speaker));
        Assert.Equal([1, 2, 3], utterances.Select(u => u.Index));
    }

    [Fact]
    public async Task Large_audio_gap_prevents_coalescing()
    {
        var events = await Collect(FastOptions,
            Source(
                Final(Speaker.Rep, "första tanken", 0, 1),
                Final(Speaker.Rep, "ny tanke långt senare", 10, 1)));

        Assert.Equal(2, Utterances(events).Count);
    }

    [Fact]
    public async Task Window_expiry_flushes_pending_without_more_input()
    {
        var expiryOptions = new TranscriptMergerOptions
        {
            CoalesceWindow = TimeSpan.FromMilliseconds(400),
            CoalesceMaxGap = TimeSpan.FromSeconds(1),
        };
        var slowTail = SlowTailSource();
        var events = new List<TranscriptEvent>();
        await foreach (var e in TranscriptMerger.MergeAsync([slowTail], expiryOptions))
            events.Add(e);

        var utterances = Utterances(events);
        Assert.Equal(2, utterances.Count);
        Assert.Equal("snabb mening", utterances[0].Text);

        static async IAsyncEnumerable<TranscriptSegment> SlowTailSource()
        {
            yield return Final(Speaker.Rep, "snabb mening", 0, 1);
            await Task.Delay(1000);
            yield return Final(Speaker.Rep, "kommer efter fönstret", 1.5, 1);
        }
    }

    [Fact]
    public async Task Partials_pass_through_without_affecting_indexing()
    {
        var events = await Collect(FastOptions,
            Source(
                Partial(Speaker.Customer, "vi har"),
                Partial(Speaker.Customer, "vi har tolv"),
                Final(Speaker.Customer, "vi har tolv skannrar", 0, 2)));

        Assert.Equal(2, events.OfType<TranscriptEvent.PartialUpdated>().Count());
        var utterances = Utterances(events);
        Assert.Single(utterances);
        Assert.Equal(1, utterances[0].Index);
    }

    [Fact]
    public async Task Completion_flushes_the_last_pending_utterance()
    {
        var events = await Collect(FastOptions,
            Source(Final(Speaker.Rep, "sista ordet", 0)));

        Assert.Single(Utterances(events));
    }
}
