using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using SalesSupport.Core.Contracts;
using SalesSupport.Core.Model;

namespace SalesSupport.Transcription.Azure;

public sealed class AzureSpeechEngineOptions
{
    /// <summary>Subscription key — server-side use. Clients use FromToken (D9) so the key never reaches the desktop.</summary>
    public string? Key { get; init; }

    /// <summary>Short-lived authorization token issued by the backend (/api/stt-token).</summary>
    public string? AuthorizationToken { get; init; }

    public required string Region { get; init; }

    public static AzureSpeechEngineOptions FromEnvironment()
    {
        var key = Environment.GetEnvironmentVariable("AZURE_SPEECH_KEY");
        var region = Environment.GetEnvironmentVariable("AZURE_SPEECH_REGION");
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(region))
            throw new InvalidOperationException(
                "Set AZURE_SPEECH_KEY and AZURE_SPEECH_REGION (a free F0 Speech resource works — e.g. region 'swedencentral').");
        return new AzureSpeechEngineOptions { Key = key, Region = region };
    }

    public static AzureSpeechEngineOptions FromToken(string token, string region) =>
        new() { AuthorizationToken = token, Region = region };

    /// <summary>Installation language codes (D7) → Azure locales. Full locales pass through.</summary>
    internal static string MapLanguage(string language) => language switch
    {
        "sv" => "sv-SE",
        "en" => "en-US",
        "da" => "da-DK",
        "no" => "nb-NO",
        "fi" => "fi-FI",
        "de" => "de-DE",
        _ when language.Contains('-') => language,
        _ => throw new ArgumentException($"No Azure locale mapping for language '{language}'."),
    };
}

/// <summary>
/// ITranscriptionEngine over Azure AI Speech (D8): one continuous-recognition session per
/// call to TranscribeAsync, push-stream input paced to wall clock (live capture sources
/// zero-fill and never end; file sources end the session by returning 0). Phrase hints
/// from the knowledge pack boost product/brand names. Audio streams through — never stored
/// (D17). Backend-issued token auth replaces the key post-spike (D9).
/// </summary>
public sealed class AzureSpeechEngine(AzureSpeechEngineOptions options) : ITranscriptionEngine
{
    public async IAsyncEnumerable<TranscriptSegment> TranscribeAsync(
        Speaker speaker, IAudioSource audio, TranscriptionConfig config,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var speechConfig = options.AuthorizationToken is { } token
            ? SpeechConfig.FromAuthorizationToken(token, options.Region)
            : SpeechConfig.FromSubscription(
                options.Key ?? throw new InvalidOperationException("AzureSpeechEngineOptions needs a Key or an AuthorizationToken."),
                options.Region);
        speechConfig.SpeechRecognitionLanguage = AzureSpeechEngineOptions.MapLanguage(config.Language);

        using var pushStream = AudioInputStream.CreatePushStream(AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1));
        using var audioConfig = AudioConfig.FromStreamInput(pushStream);
        using var recognizer = new SpeechRecognizer(speechConfig, audioConfig);

        if (config.PhraseHints.Count > 0)
        {
            var grammar = PhraseListGrammar.FromRecognizer(recognizer);
            foreach (var phrase in config.PhraseHints.Take(500))
                grammar.AddPhrase(phrase);
        }

        var segments = Channel.CreateUnbounded<TranscriptSegment>();

        recognizer.Recognizing += (_, e) =>
        {
            if (e.Result.Text.Length > 0)
                segments.Writer.TryWrite(Segment(speaker, e.Result, isFinal: false));
        };
        recognizer.Recognized += (_, e) =>
        {
            if (e.Result.Reason == ResultReason.RecognizedSpeech && e.Result.Text.Length > 0)
                segments.Writer.TryWrite(Segment(speaker, e.Result, isFinal: true));
        };
        recognizer.Canceled += (_, e) =>
        {
            if (e.Reason == CancellationReason.Error)
                segments.Writer.TryComplete(new InvalidOperationException($"Azure Speech error {e.ErrorCode}: {e.ErrorDetails}"));
            else
                segments.Writer.TryComplete();
        };
        recognizer.SessionStopped += (_, _) => segments.Writer.TryComplete();

        await recognizer.StartContinuousRecognitionAsync().ConfigureAwait(false);
        var pump = Task.Run(async () =>
        {
            try
            {
                await Core.Audio.AudioPump.RunAsync(audio, (chunk, _) =>
                {
                    pushStream.Write(chunk.ToArray(), chunk.Length);
                    return Task.CompletedTask;
                }, ct).ConfigureAwait(false);
            }
            finally
            {
                pushStream.Close();
            }
        }, CancellationToken.None);

        try
        {
            await foreach (var segment in segments.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return segment;
        }
        finally
        {
            try { await recognizer.StopContinuousRecognitionAsync().ConfigureAwait(false); } catch { }
            try { await pump.ConfigureAwait(false); } catch { }
        }
    }

    private static TranscriptSegment Segment(Speaker speaker, SpeechRecognitionResult result, bool isFinal) =>
        new(speaker, result.Text, isFinal, TimeSpan.FromTicks(result.OffsetInTicks), result.Duration);
}
