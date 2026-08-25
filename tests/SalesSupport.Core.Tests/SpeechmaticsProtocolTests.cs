using System.Diagnostics;
using SalesSupport.Core.Audio;
using SalesSupport.Core.Contracts;
using SalesSupport.Core.Model;
using SalesSupport.Transcription.Speechmatics;

namespace SalesSupport.Core.Tests;

public class SpeechmaticsProtocolTests
{
    [Fact]
    public void Final_transcript_message_parses_with_timing()
    {
        var parsed = SpeechmaticsMessages.Parse(
            """{"message":"AddTranscript","format":"2.9","metadata":{"transcript":"hur många skannrar kör ni ","start_time":1.32,"end_time":3.1},"results":[]}""",
            Speaker.Customer);

        Assert.Equal(SpeechmaticsMessages.Kind.Segment, parsed.Kind);
        var segment = parsed.Segment!;
        Assert.True(segment.IsFinal);
        Assert.Equal(Speaker.Customer, segment.Speaker);
        Assert.Equal("hur många skannrar kör ni", segment.Text);
        Assert.Equal(1.32, segment.Offset.TotalSeconds, 2);
        Assert.Equal(1.78, segment.Duration.TotalSeconds, 2);
    }

    [Fact]
    public void Partial_transcript_message_parses_as_non_final()
    {
        var parsed = SpeechmaticsMessages.Parse(
            """{"message":"AddPartialTranscript","metadata":{"transcript":"hur många","start_time":1.32,"end_time":1.9},"results":[]}""",
            Speaker.Rep);

        Assert.Equal(SpeechmaticsMessages.Kind.Segment, parsed.Kind);
        Assert.False(parsed.Segment!.IsFinal);
    }

    [Fact]
    public void Control_and_error_messages_parse()
    {
        Assert.Equal(SpeechmaticsMessages.Kind.RecognitionStarted,
            SpeechmaticsMessages.Parse("""{"message":"RecognitionStarted","id":"abc"}""", Speaker.Rep).Kind);
        Assert.Equal(SpeechmaticsMessages.Kind.EndOfTranscript,
            SpeechmaticsMessages.Parse("""{"message":"EndOfTranscript"}""", Speaker.Rep).Kind);
        Assert.Equal(SpeechmaticsMessages.Kind.Ignore,
            SpeechmaticsMessages.Parse("""{"message":"AudioAdded","seq_no":12}""", Speaker.Rep).Kind);

        var error = SpeechmaticsMessages.Parse(
            """{"message":"Error","type":"not_authorised","reason":"invalid key"}""", Speaker.Rep);
        Assert.Equal(SpeechmaticsMessages.Kind.Error, error.Kind);
        Assert.Contains("not_authorised", error.ErrorDetail);
    }

    [Fact]
    public void Empty_transcripts_are_ignored()
    {
        var parsed = SpeechmaticsMessages.Parse(
            """{"message":"AddTranscript","metadata":{"transcript":" ","start_time":0,"end_time":0.5},"results":[]}""",
            Speaker.Rep);
        Assert.Equal(SpeechmaticsMessages.Kind.Ignore, parsed.Kind);
    }

    [Theory]
    [InlineData("sv", "sv")]
    [InlineData("sv-SE", "sv")]
    [InlineData("en-US", "en")]
    public void Languages_map_to_bare_iso_codes(string input, string expected) =>
        Assert.Equal(expected, SpeechmaticsEngineOptions.MapLanguage(input));

    [Fact]
    public async Task Audio_pump_delivers_all_bytes_at_real_time_pace_and_reports_end()
    {
        var source = new FixedAudioSource(AudioPump.BytesPerSecond / 2);
        var delivered = 0;
        var stopwatch = Stopwatch.StartNew();

        var ended = await AudioPump.RunAsync(source, (chunk, _) =>
        {
            delivered += chunk.Length;
            return Task.CompletedTask;
        }, CancellationToken.None);

        stopwatch.Stop();
        Assert.True(ended);
        Assert.Equal(AudioPump.BytesPerSecond / 2, delivered);
        Assert.True(stopwatch.ElapsedMilliseconds >= 300, $"pump ran ahead of real time: {stopwatch.ElapsedMilliseconds} ms for 500 ms of audio");
    }

    private sealed class FixedAudioSource(int totalBytes) : IAudioSource
    {
        private int _remaining = totalBytes;

        public int Read(byte[] buffer, int offset, int count)
        {
            var give = Math.Min(count, _remaining);
            _remaining -= give;
            return give;
        }
    }
}
