using System.Text;
using NAudio.Wave;
using SalesSupport.Capture;

Console.OutputEncoding = Encoding.UTF8;

var list = false;
var seconds = 8;
string? micSelector = null;
string? speakerSelector = null;
var outDir = "captures";

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--list": list = true; break;
        case "--seconds": seconds = int.Parse(args[++i]); break;
        case "--mic": micSelector = args[++i]; break;
        case "--speaker": speakerSelector = args[++i]; break;
        case "--out": outDir = args[++i]; break;
    }
}

if (list)
{
    Console.WriteLine("Microphones (capture):");
    foreach (var d in AudioDevices.ListMicrophones())
        Console.WriteLine($"  [{d.Index}] {Mark(d)} {d.Name}");
    Console.WriteLine("Speakers (render, loopback source):");
    foreach (var d in AudioDevices.ListSpeakers())
        Console.WriteLine($"  [{d.Index}] {Mark(d)} {d.Name}");
    Console.WriteLine("Markers: C = default communications device (what Teams uses), M = default multimedia.");
    return 0;
}

using var micDevice = AudioDevices.GetMicrophone(micSelector);
using var speakerDevice = AudioDevices.GetSpeaker(speakerSelector);

using var mic = CaptureChannel.Microphone(micDevice);
using var loopback = CaptureChannel.SpeakerLoopback(speakerDevice);

Console.WriteLine($"Mic:      {mic.DeviceName}  [{Describe(mic.SourceFormat)}]");
Console.WriteLine($"Loopback: {loopback.DeviceName}  [{Describe(loopback.SourceFormat)}]");
Console.WriteLine($"Capturing {seconds}s → {outDir}/  (speak, and play something through the speakers — a video, a Teams test call)");
Console.WriteLine();

Directory.CreateDirectory(outDir);
using var micWriter = new WaveFileWriter(Path.Combine(outDir, "mic_16k.wav"), CaptureChannel.TargetFormat);
using var loopbackWriter = new WaveFileWriter(Path.Combine(outDir, "loopback_16k.wav"), CaptureChannel.TargetFormat);

var chunkBytes = CaptureChannel.TargetFormat.AverageBytesPerSecond / 10;
var micChunk = new byte[chunkBytes];
var loopbackChunk = new byte[chunkBytes];

mic.Start();
loopback.Start();
var started = DateTime.UtcNow;

while ((DateTime.UtcNow - started).TotalSeconds < seconds)
{
    await Task.Delay(100);
    micWriter.Write(micChunk, 0, mic.Read(micChunk, 0, chunkBytes));
    loopbackWriter.Write(loopbackChunk, 0, loopback.Read(loopbackChunk, 0, chunkBytes));

    var elapsed = (DateTime.UtcNow - started).TotalSeconds;
    Console.Write($"\r  mic {Meter(mic.Peak)}  spk {Meter(loopback.Peak)}  t={elapsed,4:F1}s ");
}

mic.Stop();
loopback.Stop();
Console.WriteLine("\n");

Report("mic", mic, micWriter, seconds);
Report("loopback", loopback, loopbackWriter, seconds);
Console.WriteLine($"\nListen to the result: {Path.GetFullPath(outDir)}\\mic_16k.wav and loopback_16k.wav");
Console.WriteLine("A silent loopback file means nothing was playing — that is expected, not a bug (zero-fill keeps the stream continuous).");
return 0;

static string Mark(AudioDeviceInfo d) =>
    (d.DefaultCommunications ? "C" : " ") + (d.DefaultMultimedia ? "M" : " ");

static string Describe(WaveFormat f) => $"{f.SampleRate} Hz, {f.Channels} ch, {f.Encoding}";

static string Meter(float peak)
{
    var filled = (int)Math.Clamp(peak * 10, 0, 10);
    return "[" + new string('#', filled) + new string('-', 10 - filled) + $"] {peak * 100,3:F0}%";
}

static void Report(string label, CaptureChannel channel, WaveFileWriter writer, int seconds)
{
    var sourceSeconds = channel.SourceBytesReceived / (double)channel.SourceFormat.AverageBytesPerSecond;
    var outputSeconds = writer.Length / (double)CaptureChannel.TargetFormat.AverageBytesPerSecond;
    Console.WriteLine($"{label,-9} source: {sourceSeconds,5:F1}s of audio received ({channel.SourceBytesReceived / 1024} KB @ source format)");
    Console.WriteLine($"{"",9} output: {outputSeconds,5:F1}s written at 16 kHz mono ({writer.Length / 1024} KB), wall clock {seconds}s");
}
