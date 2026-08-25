using SalesSupport.Core.Contracts;

namespace SalesSupport.Core.Audio;

/// <summary>
/// Real-time paced audio pump shared by transcription engines: pushes exactly as many
/// bytes as wall clock owes, so live silence-padded sources stream 1:1 with real time
/// and recorded files play out at real-time rate. Returns true when the source ended
/// (Read returned 0), false when cancelled.
/// </summary>
public static class AudioPump
{
    public const int BytesPerSecond = 32000;

    public static async Task<bool> RunAsync(
        IAudioSource audio,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> writeAsync,
        CancellationToken ct)
    {
        var buffer = new byte[BytesPerSecond / 10];
        var started = Environment.TickCount64;
        long sent = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var owed = (Environment.TickCount64 - started) * BytesPerSecond / 1000 - sent;
                while (owed >= buffer.Length)
                {
                    var read = audio.Read(buffer, 0, buffer.Length);
                    if (read <= 0) return true;
                    await writeAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    sent += read;
                    owed -= read;
                }
                await Task.Delay(50, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        return false;
    }
}
