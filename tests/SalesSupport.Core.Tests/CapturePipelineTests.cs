using NAudio.Wave;
using SalesSupport.Capture;

namespace SalesSupport.Core.Tests;

/// <summary>Hardware-free: verifies the format-conversion chain, not WASAPI itself.</summary>
public class CapturePipelineTests
{
    [Fact]
    public void Converts_48k_stereo_float_to_continuous_16k_mono_pcm()
    {
        var sourceFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        var buffer = new BufferedWaveProvider(sourceFormat, TimeSpan.FromSeconds(5))
        {
            ReadFully = true,
        };

        var oneSecond = new byte[sourceFormat.AverageBytesPerSecond];
        var samples = oneSecond.Length / 4;
        for (var i = 0; i < samples; i++)
        {
            var frame = i / 2;
            var value = (float)(0.5 * Math.Sin(2 * Math.PI * 440 * frame / 48000.0));
            BitConverter.GetBytes(value).CopyTo(oneSecond, i * 4);
        }
        buffer.AddSamples(oneSecond, 0, oneSecond.Length);

        var pipeline = CaptureChannel.BuildPipeline(buffer);
        Assert.Equal(16000, pipeline.WaveFormat.SampleRate);
        Assert.Equal(1, pipeline.WaveFormat.Channels);
        Assert.Equal(16, pipeline.WaveFormat.BitsPerSample);

        var output = new byte[CaptureChannel.TargetFormat.AverageBytesPerSecond];
        var read = pipeline.Read(output.AsSpan());
        Assert.Equal(output.Length, read);

        long energy = 0;
        for (var i = 0; i + 2 <= output.Length; i += 2)
            energy += Math.Abs((int)BitConverter.ToInt16(output, i));
        Assert.True(energy / (output.Length / 2) > 1000, "expected audible signal after resampling");

        var silent = new byte[3200];
        Assert.Equal(silent.Length, pipeline.Read(silent.AsSpan()));
        long tailEnergy = 0;
        for (var i = 0; i + 2 <= silent.Length; i += 2)
            tailEnergy += Math.Abs((int)BitConverter.ToInt16(silent, i));
        Assert.True(tailEnergy / (silent.Length / 2) < 50, "expected zero-fill silence once the source is drained");
    }
}
