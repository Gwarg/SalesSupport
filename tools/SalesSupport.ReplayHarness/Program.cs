using System.Text;
using SalesSupport.Core.Contracts;
using SalesSupport.Core.Model;
using SalesSupport.Core.Serialization;
using SalesSupport.Orchestrator;
using SalesSupport.Providers.Claude;
using SalesSupport.ReplayHarness;

Console.OutputEncoding = Encoding.UTF8;

var live = false;
var useFixtures = false;
string? dumpDir = null;
string? samplePath = null;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--live": live = true; break;
        case "--fixtures": useFixtures = true; break;
        case "--dump": dumpDir = i + 1 < args.Length ? args[++i] : "prompt-dumps"; break;
        default: samplePath = args[i]; break;
    }
}

samplePath ??= Path.Combine(FindRepoRoot(), "samples", "calls", "nordfrys-cold-storage.jsonl");

if (!File.Exists(samplePath))
{
    Console.Error.WriteLine($"Sample call not found: {samplePath}");
    return 1;
}

var fixturesPath = Path.Combine(FindRepoRoot(), "samples", "fixtures",
    Path.GetFileNameWithoutExtension(samplePath) + ".fixtures.json");

if (useFixtures && !File.Exists(fixturesPath))
{
    Console.Error.WriteLine($"Fixture file not found: {fixturesPath}");
    return 1;
}

if (live && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")))
{
    Console.Error.WriteLine("--live requires ANTHROPIC_API_KEY to be set.");
    return 1;
}

ILlmProvider llm = useFixtures ? new FixtureLlmProvider(fixturesPath)
    : live ? new ClaudeLlmProvider()
    : new FakeLlmProvider();
if (dumpDir is not null) llm = new DumpingLlmProvider(llm, dumpDir);

var mode = useFixtures ? "fixtures (Claude-authored goldens)" : live ? "LIVE Claude API" : "fake models";
var options = new OrchestratorOptions { CompanyName = "Duab (demo)", CallLanguage = "sv" };
var orchestrator = new CallOrchestrator(llm, new InMemoryKnowledge(), options);

Console.WriteLine($"Replay: {Path.GetFileName(samplePath)}  [{mode}]");
Console.WriteLine(new string('-', 72));

var index = 0;
foreach (var line in File.ReadLines(samplePath))
{
    if (string.IsNullOrWhiteSpace(line)) continue;
    var scripted = JsonDefaults.Deserialize<ScriptedUtterance>(line);
    var utterance = new Utterance(++index, scripted.Speaker, scripted.Text);

    var tick = await orchestrator.OnUtteranceAsync(utterance);

    Console.WriteLine($"T{utterance.Index,2} [{utterance.Speaker.ToString().ToLowerInvariant(),8}] {utterance.Text}");
    if (tick.Merge.ChangedIds.Count > 0)
        Console.WriteLine($"     picture: {string.Join(", ", tick.Merge.ChangedIds)}");
    foreach (var note in tick.Merge.Notes)
        Console.WriteLine($"     note: {note}");
    if (tick.Diff.QuestionsAddressed.Count > 0)
        Console.WriteLine($"     asked: {string.Join(", ", tick.Diff.QuestionsAddressed)}");
    if (tick.AdvisorRan && tick.PanelDelta is { } delta)
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
}

Console.WriteLine(new string('-', 72));
Console.WriteLine("Ask lane demo: 'Fungerar X60 med våra befintliga dockor?'");
var ask = await orchestrator.AskAsync("Fungerar X60 med våra befintliga dockor?");
Console.WriteLine($"  svar: {ask.Answer}");

var summary = await orchestrator.EndCallAsync();
Console.WriteLine(new string('-', 72));
Console.WriteLine($"Summary: {summary.Summary}");
foreach (var step in summary.NextSteps)
    Console.WriteLine($"  next: {step.Text} ({step.Owner.ToString().ToLowerInvariant()})");

Console.WriteLine(new string('-', 72));
Console.WriteLine("Final customer picture:");
Console.WriteLine(JsonDefaults.Serialize(orchestrator.Picture, pretty: true));
return 0;

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SalesSupport.slnx")))
        dir = dir.Parent;
    return dir?.FullName ?? Directory.GetCurrentDirectory();
}

internal sealed record ScriptedUtterance(Speaker Speaker, string Text);
