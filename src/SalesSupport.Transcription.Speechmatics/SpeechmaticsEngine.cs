using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using SalesSupport.Core.Audio;
using SalesSupport.Core.Contracts;
using SalesSupport.Core.Model;

namespace SalesSupport.Transcription.Speechmatics;

public sealed class SpeechmaticsEngineOptions
{
    public required string ApiKey { get; init; }

    /// <summary>EU real-time endpoint by default; override via SPEECHMATICS_RT_URL if the region differs.</summary>
    public Uri Endpoint { get; init; } = new("wss://eu2.rt.speechmatics.com/v2");

    /// <summary>"enhanced" (accuracy) or "standard" (latency/cost).</summary>
    public string OperatingPoint { get; init; } = "enhanced";

    /// <summary>Max seconds before a final is forced — the finalization-latency dial.</summary>
    public double MaxDelaySeconds { get; init; } = 2.0;

    public static SpeechmaticsEngineOptions FromEnvironment()
    {
        var key = Environment.GetEnvironmentVariable("SPEECHMATICS_API_KEY");
        if (string.IsNullOrEmpty(key))
            throw new InvalidOperationException("Set SPEECHMATICS_API_KEY (free tier available at portal.speechmatics.com).");
        var url = Environment.GetEnvironmentVariable("SPEECHMATICS_RT_URL");
        return url is null
            ? new SpeechmaticsEngineOptions { ApiKey = key }
            : new SpeechmaticsEngineOptions { ApiKey = key, Endpoint = new Uri(url) };
    }

    /// <summary>Speechmatics uses bare ISO 639-1 codes — "sv-SE" and "sv" both become "sv".</summary>
    internal static string MapLanguage(string language) =>
        language.Split('-')[0].ToLowerInvariant();
}

/// <summary>
/// ITranscriptionEngine over the Speechmatics real-time WebSocket API (v2) — the D8
/// benchmark challenger, and proof that the STT seam is genuinely pluggable. Protocol:
/// StartRecognition (JSON) → RecognitionStarted → binary audio frames (paced by
/// AudioPump) → AddPartialTranscript/AddTranscript events → EndOfStream → EndOfTranscript.
/// No official .NET SDK exists; this speaks the documented wire protocol directly.
/// </summary>
public sealed class SpeechmaticsEngine(SpeechmaticsEngineOptions options) : ITranscriptionEngine
{
    public async IAsyncEnumerable<TranscriptSegment> TranscribeAsync(
        Speaker speaker, IAudioSource audio, TranscriptionConfig config,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {options.ApiKey}");
        await socket.ConnectAsync(options.Endpoint, ct).ConfigureAwait(false);

        await SendJsonAsync(socket, BuildStartRecognition(config), ct).ConfigureAwait(false);

        var segments = Channel.CreateUnbounded<TranscriptSegment>();
        long audioFramesSent = 0;
        Task? pump = null;
        using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var receiver = Task.Run(async () =>
        {
            var buffer = new byte[64 * 1024];
            var message = new MemoryStream();
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    message.SetLength(0);
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            segments.Writer.TryComplete();
                            return;
                        }
                        message.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    var parsed = SpeechmaticsMessages.Parse(Encoding.UTF8.GetString(message.ToArray()), speaker);
                    switch (parsed.Kind)
                    {
                        case SpeechmaticsMessages.Kind.RecognitionStarted:
                            pump ??= Task.Run(async () =>
                            {
                                var ended = await AudioPump.RunAsync(audio, async (chunk, token) =>
                                {
                                    await socket.SendAsync(chunk, WebSocketMessageType.Binary, endOfMessage: true, token).ConfigureAwait(false);
                                    Interlocked.Increment(ref audioFramesSent);
                                }, pumpCts.Token).ConfigureAwait(false);
                                if (ended && socket.State == WebSocketState.Open)
                                    await SendJsonAsync(socket, new { message = "EndOfStream", last_seq_no = Interlocked.Read(ref audioFramesSent) }, CancellationToken.None).ConfigureAwait(false);
                            }, CancellationToken.None);
                            break;
                        case SpeechmaticsMessages.Kind.Segment when parsed.Segment is { } segment:
                            segments.Writer.TryWrite(segment);
                            break;
                        case SpeechmaticsMessages.Kind.EndOfTranscript:
                            segments.Writer.TryComplete();
                            return;
                        case SpeechmaticsMessages.Kind.Error:
                            segments.Writer.TryComplete(new InvalidOperationException($"Speechmatics error: {parsed.ErrorDetail}"));
                            return;
                    }
                }
                segments.Writer.TryComplete();
            }
            catch (OperationCanceledException)
            {
                segments.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                segments.Writer.TryComplete(ex);
            }
        }, CancellationToken.None);

        try
        {
            await foreach (var segment in segments.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return segment;
        }
        finally
        {
            pumpCts.Cancel();
            try { if (pump is not null) await pump.ConfigureAwait(false); } catch { }
            try
            {
                if (socket.State == WebSocketState.Open)
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None).ConfigureAwait(false);
            }
            catch { }
            try { await receiver.ConfigureAwait(false); } catch { }
        }
    }

    private object BuildStartRecognition(TranscriptionConfig config)
    {
        var transcriptionConfig = new Dictionary<string, object?>
        {
            ["language"] = SpeechmaticsEngineOptions.MapLanguage(config.Language),
            ["enable_partials"] = true,
            ["max_delay"] = options.MaxDelaySeconds,
            ["operating_point"] = options.OperatingPoint,
        };
        if (config.PhraseHints.Count > 0)
            transcriptionConfig["additional_vocab"] = config.PhraseHints.Take(500).Select(p => new { content = p }).ToArray();

        return new
        {
            message = "StartRecognition",
            audio_format = new { type = "raw", encoding = "pcm_s16le", sample_rate = 16000 },
            transcription_config = transcriptionConfig,
        };
    }

    private static Task SendJsonAsync(ClientWebSocket socket, object payload, CancellationToken ct) =>
        socket.SendAsync(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)),
            WebSocketMessageType.Text, endOfMessage: true, ct);
}

/// <summary>Wire-message parsing, separated for testability.</summary>
internal static class SpeechmaticsMessages
{
    internal enum Kind { Ignore, RecognitionStarted, Segment, EndOfTranscript, Error }

    internal readonly record struct Parsed(Kind Kind, TranscriptSegment? Segment, string? ErrorDetail);

    internal static Parsed Parse(string json, Speaker speaker)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var message = root.GetProperty("message").GetString();

        switch (message)
        {
            case "RecognitionStarted":
                return new Parsed(Kind.RecognitionStarted, null, null);
            case "AddTranscript":
            case "AddPartialTranscript":
            {
                var metadata = root.GetProperty("metadata");
                var text = metadata.GetProperty("transcript").GetString() ?? "";
                if (string.IsNullOrWhiteSpace(text)) return new Parsed(Kind.Ignore, null, null);
                var start = metadata.TryGetProperty("start_time", out var s) ? s.GetDouble() : 0;
                var end = metadata.TryGetProperty("end_time", out var e) ? e.GetDouble() : start;
                var segment = new TranscriptSegment(
                    speaker, text.Trim(), message == "AddTranscript",
                    TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(Math.Max(0, end - start)));
                return new Parsed(Kind.Segment, segment, null);
            }
            case "EndOfTranscript":
                return new Parsed(Kind.EndOfTranscript, null, null);
            case "Error":
            {
                var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                var reason = root.TryGetProperty("reason", out var r) ? r.GetString() : null;
                return new Parsed(Kind.Error, null, $"{type}: {reason}");
            }
            default:
                return new Parsed(Kind.Ignore, null, null);
        }
    }
}
