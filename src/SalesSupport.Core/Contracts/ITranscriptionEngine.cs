using SalesSupport.Core.Model;

namespace SalesSupport.Core.Contracts;

public sealed record TranscriptionSession(string CallId, string Language, string ChannelDeviceHint);

/// <summary>
/// Streaming STT boundary (D8). Placeholder contract until L1 — the L0 replay harness
/// feeds recorded utterances directly and never touches audio. Implementations:
/// Azure Speech (v1), self-hosted engines (later), one session per audio channel.
/// </summary>
public interface ITranscriptionEngine
{
    IAsyncEnumerable<Utterance> StreamAsync(TranscriptionSession session, CancellationToken ct = default);
}
