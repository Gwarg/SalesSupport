using System.Text;
using SalesSupport.Capture;
using SalesSupport.Core.Audio;
using SalesSupport.Core.Contracts;
using SalesSupport.Core.Model;
using SalesSupport.Knowledge;
using SalesSupport.Transcription.Azure;
using SalesSupport.Transcription.Speechmatics;

Console.OutputEncoding = Encoding.UTF8;

string? wavPath = null;
var role = Speaker.Rep;
var language = "sv";
var live = false;
var seconds = 20;
string? micSelector = null;
string? spkSelector = null;
string? packPath = null;
var engineName = "azure";

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--wav": wavPath = args[++i]; break;
        case "--role": role = args[++i] == "customer" ? Speaker.Customer : Speaker.Rep; break;
        case "--language": language = args[++i]; break;
        case "--live": live = true; break;
        case "--seconds": seconds = int.Parse(args[++i]); break;
        case "--mic": micSelector = args[++i]; break;
        case "--spk": spkSelector = args[++i]; break;
        case "--pack": packPath = args[++i]; break;
        case "--engine": engineName = args[++i]; break;
    }
}

if (wavPath is null && !live)
{
    Console.Error.WriteLine("Usage: SalesSupport.TranscribeSpike --wav <file> [--role rep|customer] [--language sv] [--pack <pack>] [--engine azure|speechmatics]");
    Console.Error.WriteLine("       SalesSupport.TranscribeSpike --live [--seconds 20] [--mic sel] [--spk sel] [--language sv] [--pack <pack>] [--engine azure|speechmatics]");
    Console.Error.WriteLine("azure: AZURE_SPEECH_KEY + AZURE_SPEECH_REGION.  speechmatics: SPEECHMATICS_API_KEY.");
    return 1;
}

ITranscriptionEngine engine = engineName switch
{
    "azure" => new AzureSpeechEngine(AzureSpeechEngineOptions.FromEnvironment()),
    "speechmatics" => new SpeechmaticsEngine(SpeechmaticsEngineOptions.FromEnvironment()),
    _ => throw new ArgumentException($"Unknown engine '{engineName}' (azure | speechmatics)"),
};
Console.WriteLine($"Engine: {engineName}");

var hints = new List<string>();
if (packPath is not null)
{
    var pack = SqlitePackKnowledge.Load(packPath, EmbedderFactory.ForPack(packPath, EmbedderFactory.DefaultModelDir(FindRepoRoot())));
    hints.AddRange(pack.SttVocabulary);
    Console.WriteLine($"Phrase hints: {hints.Count} terms from {Path.GetFileName(packPath)}");
}
var config = new TranscriptionConfig(language, hints);

var printLock = new object();
var partialShown = false;

if (wavPath is not null)
{
    Console.WriteLine($"Transcribing {wavPath} as [{role.ToString().ToLowerInvariant()}], language={language} (plays out at real-time rate)");
    using var source = new WavAudioSource(wavPath);
    await foreach (var segment in engine.TranscribeAsync(role, source, config))
        Print(segment);
    ClearPartial();
    Console.WriteLine("Done.");
    return 0;
}

Console.WriteLine($"Live dual-channel transcription for {seconds}s, language={language}. Speak, and play something through the speakers.");
Console.WriteLine("Note: the free F0 tier may reject the second concurrent session — if the customer channel errors, that is the tier, not the code.");

using var micDevice = AudioDevices.GetMicrophone(micSelector);
using var spkDevice = AudioDevices.GetSpeaker(spkSelector);
using var mic = CaptureChannel.Microphone(micDevice);
using var loopback = CaptureChannel.SpeakerLoopback(spkDevice);
Console.WriteLine($"Mic: {mic.DeviceName}  |  Loopback: {loopback.DeviceName}");
Console.WriteLine();

mic.Start();
loopback.Start();
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));

var repTask = ConsumeAsync(engine.TranscribeAsync(Speaker.Rep, mic, config, cts.Token));
var customerTask = ConsumeAsync(engine.TranscribeAsync(Speaker.Customer, loopback, config, cts.Token));
await Task.WhenAll(repTask, customerTask);

mic.Stop();
loopback.Stop();
ClearPartial();
Console.WriteLine("Done.");
return 0;

async Task ConsumeAsync(IAsyncEnumerable<TranscriptSegment> segments)
{
    try
    {
        await foreach (var segment in segments)
            Print(segment);
    }
    catch (OperationCanceledException)
    {
    }
    catch (Exception ex)
    {
        lock (printLock)
        {
            ClearPartialUnlocked();
            Console.WriteLine($"! {ex.Message}");
        }
    }
}

void Print(TranscriptSegment segment)
{
    lock (printLock)
    {
        ClearPartialUnlocked();
        var tag = $"[{segment.Speaker.ToString().ToLowerInvariant()}]";
        if (segment.IsFinal)
        {
            Console.WriteLine($"{segment.Offset:mm\\:ss} {tag,-10} {segment.Text}");
        }
        else
        {
            var line = $"~ {tag} {segment.Text}";
            Console.Write(line.Length > 110 ? line[..110] : line);
            partialShown = true;
        }
    }
}

void ClearPartial()
{
    lock (printLock) ClearPartialUnlocked();
}

void ClearPartialUnlocked()
{
    if (!partialShown) return;
    Console.Write("\r" + new string(' ', 112) + "\r");
    partialShown = false;
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SalesSupport.slnx")))
        dir = dir.Parent;
    return dir?.FullName ?? Directory.GetCurrentDirectory();
}
