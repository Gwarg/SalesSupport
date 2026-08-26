using SalesSupport.Capture;
using SalesSupport.Core.Model;
using SalesSupport.Core.Contracts;
using SalesSupport.Core.Transcription;
using SalesSupport.Transcription.Azure;

namespace SalesSupport.Client;

/// <summary>
/// The client-side audio chain (D9): mic + loopback capture → token-authed Azure STT
/// (audio never transits our backend) → transcript merger. Partials go to the live line;
/// finals go up the hub. Token refresh for calls beyond ~10 minutes is a later refinement.
/// </summary>
public sealed class AudioSession : IDisposable
{
    private readonly CaptureChannel _mic;
    private readonly CaptureChannel _loopback;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<IDisposable> _devices;

    public float MicPeak => _mic.Peak;
    public float SpeakerPeak => _loopback.Peak;
    public string MicName => _mic.DeviceName;
    public string SpeakerName => _loopback.DeviceName;

    private AudioSession(CaptureChannel mic, CaptureChannel loopback, List<IDisposable> devices)
    {
        _mic = mic;
        _loopback = loopback;
        _devices = devices;
    }

    public static AudioSession Start(
        string? micSelector,
        string? speakerSelector,
        AzureSpeechEngineOptions sttOptions,
        string language,
        IReadOnlyList<string> phraseHints,
        Action<Speaker, string> onPartial,
        Func<Utterance, Task> onFinal,
        Action<string> onError)
    {
        var micDevice = AudioDevices.GetMicrophone(micSelector);
        var speakerDevice = AudioDevices.GetSpeaker(speakerSelector);
        var mic = CaptureChannel.Microphone(micDevice);
        var loopback = CaptureChannel.SpeakerLoopback(speakerDevice);
        var session = new AudioSession(mic, loopback, [micDevice, speakerDevice, mic, loopback]);

        var engine = new AzureSpeechEngine(sttOptions);
        var config = new TranscriptionConfig(language, phraseHints);
        var sources = new List<IAsyncEnumerable<TranscriptSegment>>
        {
            engine.TranscribeAsync(Speaker.Rep, mic, config, session._cts.Token),
            engine.TranscribeAsync(Speaker.Customer, loopback, config, session._cts.Token),
        };

        mic.Start();
        loopback.Start();

        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var transcriptEvent in TranscriptMerger.MergeAsync(sources, ct: session._cts.Token))
                {
                    switch (transcriptEvent)
                    {
                        case TranscriptEvent.PartialUpdated(var speaker, var text):
                            onPartial(speaker, text);
                            break;
                        case TranscriptEvent.UtteranceFinalized(var utterance):
                            await onFinal(utterance);
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                onError(ex.Message);
            }
        });

        return session;
    }

    public void Stop()
    {
        _cts.Cancel();
        _mic.Stop();
        _loopback.Stop();
    }

    public void Dispose()
    {
        Stop();
        foreach (var disposable in _devices) disposable.Dispose();
        _cts.Dispose();
    }
}
