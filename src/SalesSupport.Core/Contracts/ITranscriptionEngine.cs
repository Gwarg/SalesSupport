using SalesSupport.Core.Model;

namespace SalesSupport.Core.Contracts;

/// <summary>
/// Continuous 16 kHz mono 16-bit PCM audio. Read fills up to count bytes; live sources
/// (capture channels) always fill fully — silence-padded, unpaced, the consumer paces to
/// wall clock. A return of 0 means the source ended (file sources).
/// </summary>
public interface IAudioSource
{
    int Read(byte[] buffer, int offset, int count);
}

public sealed record TranscriptionConfig(string Language, IReadOnlyList<string> PhraseHints)
{
    public static TranscriptionConfig For(string language) => new(language, []);
}

/// <summary>One recognition result. IsFinal=false are partials (panel last-line display); finals become utterances.</summary>
public sealed record TranscriptSegment(Speaker Speaker, string Text, bool IsFinal, TimeSpan Offset, TimeSpan Duration);

/// <summary>
/// Streaming STT boundary (D8/D9): one session per audio channel (mic = Rep,
/// loopback = Customer), pluggable per installation. Audio flows through and is never
/// stored (D17). Implementations: Azure Speech (v1), self-hosted engines later.
/// </summary>
public interface ITranscriptionEngine
{
    IAsyncEnumerable<TranscriptSegment> TranscribeAsync(
        Speaker speaker, IAudioSource audio, TranscriptionConfig config, CancellationToken ct = default);
}
