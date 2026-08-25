using SalesSupport.Core.Contracts;

namespace SalesSupport.Core.Audio;

/// <summary>
/// IAudioSource over a RIFF/WAV file — must already be 16 kHz mono 16-bit PCM (the
/// capture spike's output format). Used to run recorded audio through a transcription
/// engine without live capture; returns 0 at end of data.
/// </summary>
public sealed class WavAudioSource : IAudioSource, IDisposable
{
    private readonly FileStream _stream;
    private long _remaining;

    public WavAudioSource(string path)
    {
        _stream = File.OpenRead(path);
        try
        {
            ParseHeader(path);
        }
        catch
        {
            _stream.Dispose();
            throw;
        }
    }

    private void ParseHeader(string path)
    {
        using var reader = new BinaryReader(_stream, System.Text.Encoding.ASCII, leaveOpen: true);

        if (new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException($"{path}: not a RIFF file");
        reader.ReadInt32();
        if (new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException($"{path}: not a WAVE file");

        while (true)
        {
            var chunkId = new string(reader.ReadChars(4));
            var chunkSize = reader.ReadInt32();

            if (chunkId == "fmt ")
            {
                var formatTag = reader.ReadInt16();
                var channels = reader.ReadInt16();
                var sampleRate = reader.ReadInt32();
                reader.ReadInt32();
                reader.ReadInt16();
                var bits = reader.ReadInt16();
                if (formatTag != 1 || channels != 1 || sampleRate != 16000 || bits != 16)
                    throw new InvalidDataException(
                        $"{path}: expected 16 kHz mono 16-bit PCM, got tag={formatTag} ch={channels} rate={sampleRate} bits={bits}");
                _stream.Seek(chunkSize - 16, SeekOrigin.Current);
            }
            else if (chunkId == "data")
            {
                _remaining = chunkSize;
                return;
            }
            else
            {
                _stream.Seek(chunkSize, SeekOrigin.Current);
            }
        }
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        if (_remaining <= 0) return 0;
        var read = _stream.Read(buffer, offset, (int)Math.Min(count, _remaining));
        _remaining -= read;
        return read;
    }

    public void Dispose() => _stream.Dispose();
}
