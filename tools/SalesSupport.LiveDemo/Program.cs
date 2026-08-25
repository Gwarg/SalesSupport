using System.Text;
using SalesSupport.Capture;
using SalesSupport.Core.Audio;
using SalesSupport.Core.Contracts;
using SalesSupport.Core.Model;
using SalesSupport.Core.Serialization;
using SalesSupport.Core.Transcription;
using SalesSupport.Knowledge;
using SalesSupport.Orchestrator;
using SalesSupport.Providers.Claude;
using SalesSupport.Providers.Ollama;
using SalesSupport.ReplayHarness;
using SalesSupport.Transcription.Azure;
using SalesSupport.Transcription.Speechmatics;

Console.OutputEncoding = Encoding.UTF8;

var engineName = "azure";
var llmName = "ollama";
var language = "sv";
var seconds = 300;
string? micSelector = null;
string? spkSelector = null;
string? packPath = null;
string? wavMic = null;
string? wavSpk = null;
var strictness = GateStrictness.Balanced;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--engine": engineName = args[++i]; break;
        case "--llm": llmName = args[++i]; break;
        case "--language": language = args[++i]; break;
        case "--seconds": seconds = int.Parse(args[++i]); break;
        case "--mic": micSelector = args[++i]; break;
        case "--spk": spkSelector = args[++i]; break;
        case "--pack": packPath = args[++i]; break;
        case "--wav-mic": wavMic = args[++i]; break;
        case "--wav-spk": wavSpk = args[++i]; break;
        case "--strictness": strictness = Enum.Parse<GateStrictness>(args[++i], ignoreCase: true); break;
        case "--help":
            Console.WriteLine("Full-loop live demo: capture -> STT -> merger -> orchestrator -> panel output.");
            Console.WriteLine("  --engine azure|speechmatics   STT engine (default azure)");
            Console.WriteLine("  --llm ollama|claude|fake      model backend (default ollama)");
            Console.WriteLine("  --language sv|en              call language (default sv)");
            Console.WriteLine("  --pack <file>                 knowledge pack (default: newest in packs/, else built-in demo)");
            Console.WriteLine("  --wav-mic/--wav-spk <file>    replay recorded channels instead of live capture");
            Console.WriteLine("  --seconds N --mic sel --spk sel --strictness strict|balanced|eager");
            return 0;
    }
}

var repoRoot = FindRepoRoot();

ITranscriptionEngine engine = engineName switch
{
    "azure" => new AzureSpeechEngine(AzureSpeechEngineOptions.FromEnvironment()),
    "speechmatics" => new SpeechmaticsEngine(SpeechmaticsEngineOptions.FromEnvironment()),
    _ => throw new ArgumentException($"Unknown engine '{engineName}'"),
};

ILlmProvider llm = llmName switch
{
    "ollama" => new OllamaLlmProvider(),
    "claude" => new ClaudeLlmProvider(),
    "fake" => new FakeLlmProvider(),
    _ => throw new ArgumentException($"Unknown llm '{llmName}'"),
};

IKnowledgeSource knowledge;
var hints = new List<string>();
packPath ??= Directory.Exists(Path.Combine(repoRoot, "packs"))
    ? Directory.GetFiles(Path.Combine(repoRoot, "packs"), "*.pack.sqlite").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
    : null;
if (packPath is not null)
{
    var pack = SqlitePackKnowledge.Load(packPath, EmbedderFactory.ForPack(packPath, EmbedderFactory.DefaultModelDir(repoRoot)));
    hints.AddRange(pack.SttVocabulary);
    knowledge = pack;
}
else
{
    knowledge = new InMemoryKnowledge();
}

var options = new OrchestratorOptions
{
    CompanyName = "Duab (demo)",
    CallLanguage = language,
    UiLanguage = language,
    GateStrictness = strictness,
};
var orchestrator = new CallOrchestrator(llm, knowledge, options);
var config = new TranscriptionConfig(language, hints);

Console.WriteLine($"Live call demo — engine={engineName} llm={llmName} lang={language} strictness={strictness.ToString().ToLowerInvariant()}");
Console.WriteLine($"Knowledge: {(packPath is not null ? Path.GetFileName(packPath) : "built-in demo catalog")}  ({hints.Count} STT phrase hints)");

using var cts = new CancellationTokenSource();
CaptureChannel? mic = null;
CaptureChannel? loopback = null;
var sources = new List<IAsyncEnumerable<TranscriptSegment>>();
var disposables = new List<IDisposable>();

if (wavMic is null && wavSpk is null)
{
    var micDevice = AudioDevices.GetMicrophone(micSelector);
    var spkDevice = AudioDevices.GetSpeaker(spkSelector);
    disposables.Add(micDevice);
    disposables.Add(spkDevice);
    mic = CaptureChannel.Microphone(micDevice);
    loopback = CaptureChannel.SpeakerLoopback(spkDevice);
    disposables.Add(mic);
    disposables.Add(loopback);
    Console.WriteLine($"Mic: {mic.DeviceName}  |  Loopback: {loopback.DeviceName}");
    sources.Add(engine.TranscribeAsync(Speaker.Rep, mic, config, cts.Token));
    sources.Add(engine.TranscribeAsync(Speaker.Customer, loopback, config, cts.Token));
    mic.Start();
    loopback.Start();
}
else
{
    if (wavMic is not null)
    {
        var wav = new WavAudioSource(wavMic);
        disposables.Add(wav);
        sources.Add(engine.TranscribeAsync(Speaker.Rep, wav, config, cts.Token));
        Console.WriteLine($"Rep audio: {wavMic}");
    }
    if (wavSpk is not null)
    {
        var wav = new WavAudioSource(wavSpk);
        disposables.Add(wav);
        sources.Add(engine.TranscribeAsync(Speaker.Customer, wav, config, cts.Token));
        Console.WriteLine($"Customer audio: {wavSpk}");
    }
}

Console.WriteLine("Press Enter to end the call.");
Console.WriteLine(new string('-', 72));

_ = Task.Run(async () =>
{
    var enter = Task.Run(Console.ReadLine);
    await Task.WhenAny(enter, Task.Delay(TimeSpan.FromSeconds(seconds)));
    cts.Cancel();
});

var partialShown = false;

try
{
    await foreach (var transcriptEvent in TranscriptMerger.MergeAsync(sources, ct: cts.Token))
    {
        switch (transcriptEvent)
        {
            case TranscriptEvent.PartialUpdated(var speaker, var text):
            {
                ClearPartial();
                var line = $"~ [{speaker.ToString().ToLowerInvariant()}] {text}";
                Console.Write(line.Length > 110 ? line[..110] : line);
                partialShown = true;
                break;
            }
            case TranscriptEvent.UtteranceFinalized(var utterance):
            {
                ClearPartial();
                var tag = $"[{utterance.Speaker.ToString().ToLowerInvariant()}]";
                Console.WriteLine($"T{utterance.Index,2} {tag,-10} {utterance.Text}");

                var tick = await orchestrator.OnUtteranceAsync(utterance);
                var timing = tick.AdvisorRan
                    ? $"gate {tick.GateMs / 1000.0:F1}s + advisor {tick.AdvisorMs / 1000.0:F1}s"
                    : $"gate {tick.GateMs / 1000.0:F1}s";
                if (tick.Merge.ChangedIds.Count > 0)
                    Console.WriteLine($"     picture: {string.Join(", ", tick.Merge.ChangedIds)}  ({timing})");
                else
                    Console.WriteLine($"     ({timing})");
                if (tick.Diff.QuestionsAddressed.Count > 0)
                    Console.WriteLine($"     asked: {string.Join(", ", tick.Diff.QuestionsAddressed)}");
                if (tick.PanelDelta is { } delta)
                {
                    foreach (var q in delta.AddedQuestions) Console.WriteLine($"     + fråga  {q.Id}: {q.Text}");
                    foreach (var p in delta.AddedProducts) Console.WriteLine($"     + förslag {p.Id}: {p.DisplayName} — {p.Why}");
                    foreach (var id in delta.RemovedQuestionIds) Console.WriteLine($"     - fråga  {id}");
                    foreach (var id in delta.RemovedProductIds) Console.WriteLine($"     - förslag {id}");
                }
                else if (tick.Diff.Advice.Needed)
                {
                    Console.WriteLine("     (advice wanted, damped)");
                }
                break;
            }
        }
    }
}
catch (OperationCanceledException)
{
}
finally
{
    ClearPartial();
    mic?.Stop();
    loopback?.Stop();
}

Console.WriteLine(new string('-', 72));
Console.WriteLine("Call ended. Ask the advisor (empty line to continue):");
while (true)
{
    Console.Write("Ask> ");
    var query = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(query)) break;
    try
    {
        var ask = await orchestrator.AskAsync(query);
        Console.WriteLine($"  svar: {ask.Answer}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  ! {ex.Message}");
    }
}

try
{
    var summary = await orchestrator.EndCallAsync();
    Console.WriteLine();
    Console.WriteLine($"Summary: {summary.Summary}");
    foreach (var step in summary.NextSteps)
        Console.WriteLine($"  next: {step.Text} ({step.Owner.ToString().ToLowerInvariant()})");
}
catch (Exception ex)
{
    Console.WriteLine($"Summary failed: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("Final panel:");
foreach (var q in orchestrator.Panel.ActiveQuestions)
    Console.WriteLine($"  fråga  {q.Id}: {q.Text}");
foreach (var p in orchestrator.Panel.Products.Where(p => p.Status == PanelItemStatus.Active))
    Console.WriteLine($"  förslag {p.Id}: {p.DisplayName} — {p.Why}");
Console.WriteLine("Threads:");
foreach (var t in orchestrator.Picture.Threads)
    Console.WriteLine($"  {t.Id} [{t.Kind.ToString().ToLowerInvariant()}/{t.Status.ToString().ToLowerInvariant()}/{t.Salience.ToString().ToLowerInvariant()}] {t.Topic}");
Console.WriteLine();
Console.WriteLine("Final customer picture:");
Console.WriteLine(JsonDefaults.Serialize(orchestrator.Picture, pretty: true));

foreach (var disposable in disposables) disposable.Dispose();
return 0;

void ClearPartial()
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
