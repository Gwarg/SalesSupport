using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SalesSupport.Core.Contracts;

namespace SalesSupport.Capture;

/// <summary>
/// One capture side (mic = rep voice, speaker loopback = customer voice, D1) delivering
/// a continuous 16 kHz mono 16-bit stream — the format STT consumes. Event-driven WASAPI
/// data lands in a buffer; the caller pulls at wall-clock pace via Read, and the buffer
/// zero-fills when the source is silent (WASAPI loopback delivers nothing during silence —
/// zero-fill keeps the stream continuous for STT). Audio exists only in transit: nothing
/// here persists anything (D17).
/// </summary>
public sealed class CaptureChannel : IAudioSource, IDisposable
{
    public static readonly WaveFormat TargetFormat = new(16000, 16, 1);

    private readonly WasapiCapture _capture;
    private readonly BufferedWaveProvider _buffer;
    private readonly IWaveProvider _pipeline;
    private long _sourceBytes;
    private float _peak;

    public string DeviceName { get; }
    public WaveFormat SourceFormat => _capture.WaveFormat;
    public long SourceBytesReceived => Interlocked.Read(ref _sourceBytes);

    /// <summary>Peak level [0..1] of the most recent source buffer — the panel's meter signal (docs/panel.md).</summary>
    public float Peak => _peak;

    private CaptureChannel(WasapiCapture capture, string deviceName)
    {
        _capture = capture;
        DeviceName = deviceName;
        _buffer = new BufferedWaveProvider(capture.WaveFormat, TimeSpan.FromSeconds(10))
        {
            ReadFully = true,
            DiscardOnBufferOverflow = true,
        };
        _pipeline = BuildPipeline(_buffer);
        _capture.DataAvailable += OnDataAvailable;
    }

    public static CaptureChannel Microphone(MMDevice device) =>
        new(new WasapiCapture(device), device.FriendlyName);

    public static CaptureChannel SpeakerLoopback(MMDevice device) =>
        new(new WasapiLoopbackCapture(device), device.FriendlyName);

    /// <summary>Converts any WASAPI mix format (float/PCM, any channel count, any rate) to 16 kHz mono 16-bit.
    /// Gaming headsets expose surround mix formats (the Corsair HS80 renders 8 channels), so the
    /// downmix must handle arbitrary channel counts, not just stereo.</summary>
    internal static IWaveProvider BuildPipeline(IWaveProvider source)
    {
        ISampleProvider samples = source.ToSampleProvider();
        if (source.WaveFormat.Channels > 1)
            samples = new DownmixToMonoSampleProvider(samples);
        samples = new WdlResamplingSampleProvider(samples, TargetFormat.SampleRate);
        return new SampleToWaveProvider16(samples);
    }

    /// <summary>Averages all channels of each frame into one — works for stereo through 8-channel surround.</summary>
    private sealed class DownmixToMonoSampleProvider(ISampleProvider source) : ISampleProvider
    {
        private readonly int _channels = source.WaveFormat.Channels;
        private float[] _frames = [];

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);

        public int Read(Span<float> buffer)
        {
            var needed = buffer.Length * _channels;
            if (_frames.Length < needed) _frames = new float[needed];
            var read = source.Read(_frames.AsSpan(0, needed));
            var frames = read / _channels;
            for (var frame = 0; frame < frames; frame++)
            {
                var sum = 0f;
                for (var channel = 0; channel < _channels; channel++)
                    sum += _frames[frame * _channels + channel];
                buffer[frame] = sum / _channels;
            }
            return frames;
        }
    }

    public void Start() => _capture.StartRecording();
    public void Stop() => _capture.StopRecording();

    /// <summary>
    /// Pull converted audio. Call at wall-clock pace (e.g. 100 ms worth every 100 ms);
    /// always fills the requested count, with silence when the source has no data.
    /// </summary>
    public int Read(byte[] buffer, int offset, int count) => _pipeline.Read(buffer.AsSpan(offset, count));

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        Interlocked.Add(ref _sourceBytes, e.BytesRecorded);
        _peak = ComputePeak(e.Buffer, e.BytesRecorded, _capture.WaveFormat);
        _buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
    }

    private static float ComputePeak(byte[] buffer, int bytes, WaveFormat format)
    {
        var peak = 0f;
        // WASAPI shared-mode mix format is 32-bit float (plain or wrapped in Extensible).
        if (format.BitsPerSample == 32)
        {
            for (var i = 0; i + 4 <= bytes; i += 4)
            {
                var sample = Math.Abs(BitConverter.ToSingle(buffer, i));
                if (sample > peak) peak = sample;
            }
        }
        else if (format.BitsPerSample == 16)
        {
            for (var i = 0; i + 2 <= bytes; i += 2)
            {
                var sample = Math.Abs(BitConverter.ToInt16(buffer, i) / 32768f);
                if (sample > peak) peak = sample;
            }
        }
        return Math.Min(peak, 1f);
    }

    public void Dispose()
    {
        _capture.DataAvailable -= OnDataAvailable;
        _capture.Dispose();
    }
}
