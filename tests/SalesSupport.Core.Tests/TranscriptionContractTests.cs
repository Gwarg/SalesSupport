using SalesSupport.Core.Audio;
using SalesSupport.Transcription.Azure;

namespace SalesSupport.Core.Tests;

public class TranscriptionContractTests
{
    [Theory]
    [InlineData("sv", "sv-SE")]
    [InlineData("en", "en-US")]
    [InlineData("da", "da-DK")]
    [InlineData("pt-BR", "pt-BR")]
    public void Language_codes_map_to_azure_locales(string language, string expected) =>
        Assert.Equal(expected, AzureSpeechEngineOptions.MapLanguage(language));

    [Fact]
    public void Unknown_language_fails_loudly() =>
        Assert.Throws<ArgumentException>(() => AzureSpeechEngineOptions.MapLanguage("xx"));

    [Fact]
    public void Wav_source_reads_16k_mono_pcm_and_ends()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wav_{Guid.NewGuid():N}.wav");
        try
        {
            var samples = new byte[3200];
            for (var i = 0; i < samples.Length; i += 2)
                BitConverter.GetBytes((short)(1000 * Math.Sin(i / 20.0))).CopyTo(samples, i);
            File.WriteAllBytes(path, BuildWav(samples));

            using var source = new WavAudioSource(path);
            var buffer = new byte[1000];
            var total = 0;
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0) total += read;

            Assert.Equal(samples.Length, total);
            Assert.Equal(0, source.Read(buffer, 0, buffer.Length));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Wav_source_rejects_wrong_format()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wav_{Guid.NewGuid():N}.wav");
        try
        {
            File.WriteAllBytes(path, BuildWav(new byte[100], sampleRate: 44100));
            Assert.Throws<InvalidDataException>(() => new WavAudioSource(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] BuildWav(byte[] pcmData, int sampleRate = 16000)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.Write("RIFF"u8);
        writer.Write(36 + pcmData.Length);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(pcmData.Length);
        writer.Write(pcmData);
        return ms.ToArray();
    }
}
