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
var runAll = false;
string? dumpDir = null;
string? samplePath = null;
string? packPath = null;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--live": live = true; break;
        case "--fixtures": useFixtures = true; break;
        case "--all": runAll = true; useFixtures = true; break;
        case "--dump": dumpDir = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "prompt-dumps"; break;
        case "--pack": packPath = args[++i]; break;
        default: samplePath = args[i]; break;
    }
}

var repoRoot = FindRepoRoot();

if (live && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")))
{
    Console.Error.WriteLine("--live requires ANTHROPIC_API_KEY to be set.");
    return 1;
}

IKnowledgeSource knowledge = packPath is not null
    ? SalesSupport.Knowledge.SqlitePackKnowledge.Load(packPath, new SalesSupport.Knowledge.HashingEmbedder())
    : new InMemoryKnowledge();
if (packPath is not null)
    Console.WriteLine($"Knowledge: pack {Path.GetFileName(packPath)}");

if (runAll)
{
    var samples = Directory.GetFiles(Path.Combine(repoRoot, "samples", "calls"), "*.jsonl").OrderBy(p => p, StringComparer.Ordinal).ToList();
    Console.WriteLine($"Corpus run: {samples.Count} calls");
    Console.WriteLine(new string('-', 104));
    var failures = 0;
    foreach (var sample in samples)
    {
        var stats = await RunCallAsync(sample, verbose: false);
        var flags = stats.FixturesUnused > 0 ? $" UNUSED-FIXTURES={stats.FixturesUnused}" : "";
        flags += stats.MergeNotes > 0 ? $" notes={stats.MergeNotes}" : "";
        var status = stats.Error is null ? "OK  " : "FAIL";
        Console.WriteLine(
            $"{status} {Path.GetFileNameWithoutExtension(sample),-28} [{stats.Mode,-8}] lang={stats.Language} " +
            $"ticks={stats.Ticks,2} advisor={stats.AdvisorRuns} +q={stats.AddedQuestions} -q={stats.RemovedQuestions} " +
            $"+p={stats.AddedProducts} asks={stats.Asks}{flags}");
        if (stats.Error is not null) { Console.WriteLine($"     {stats.Error}"); failures++; }
    }
    Console.WriteLine(new string('-', 104));
    Console.WriteLine(failures == 0 ? "All calls completed." : $"{failures} call(s) failed.");
    return failures == 0 ? 0 : 1;
}

samplePath ??= Path.Combine(repoRoot, "samples", "calls", "nordfrys-cold-storage.jsonl");
if (!File.Exists(samplePath))
{
    Console.Error.WriteLine($"Sample call not found: {samplePath}");
    return 1;
}

var result = await RunCallAsync(samplePath, verbose: true);
if (result.Error is not null)
{
    Console.Error.WriteLine($"FAIL: {result.Error}");
    return 1;
}
return 0;

async Task<CallStats> RunCallAsync(string path, bool verbose)
{
    var name = Path.GetFileNameWithoutExtension(path);
    var stats = new CallStats();

    try
    {
        var lines = File.ReadLines(path)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(JsonDefaults.Deserialize<ScriptLine>)
            .ToList();

        var meta = lines.FirstOrDefault(l => l.Language is not null || l.Customer is not null);
        var language = meta?.Language ?? "sv";
        stats.Language = language;

        var fixturesPath = Path.Combine(repoRoot, "samples", "fixtures", name + ".fixtures.json");
        FixtureLlmProvider? fixtures = null;

        ILlmProvider llm;
        if (useFixtures && File.Exists(fixturesPath))
        {
            fixtures = new FixtureLlmProvider(fixturesPath);
            llm = fixtures;
            stats.Mode = "fixtures";
        }
        else if (useFixtures && !runAll)
        {
            throw new FileNotFoundException($"Fixture file not found: {fixturesPath}");
        }
        else if (live)
        {
            llm = new ClaudeLlmProvider();
            stats.Mode = "live";
        }
        else
        {
            llm = new FakeLlmProvider();
            stats.Mode = "fake";
        }

        if (dumpDir is not null)
            llm = new DumpingLlmProvider(llm, runAll ? Path.Combine(dumpDir, name) : dumpDir);

        var options = new OrchestratorOptions
        {
            CompanyName = "Duab (demo)",
            CallLanguage = language,
            UiLanguage = language,
        };
        var orchestrator = new CallOrchestrator(llm, knowledge, options);

        if (verbose)
        {
            Console.WriteLine($"Replay: {Path.GetFileName(path)}  [{stats.Mode}]  lang={language}{(meta?.Customer is { } c ? $"  customer={c}" : "")}");
            Console.WriteLine(new string('-', 72));
        }

        var index = 0;
        foreach (var line in lines)
        {
            if (line.Language is not null || line.Customer is not null) continue;

            if (line.Ask is { } query)
            {
                stats.Asks++;
                if (verbose) Console.WriteLine($"    [rep typed] {query}");
                var ask = await orchestrator.AskAsync(query);
                CountDelta(ask.PanelDelta, stats);
                if (verbose)
                {
                    Console.WriteLine($"     svar: {ask.Answer}");
                    PrintDelta(ask.PanelDelta);
                }
                continue;
            }

            var utterance = new Utterance(++index, line.Speaker!.Value, line.Text!);
            var tick = await orchestrator.OnUtteranceAsync(utterance);
            stats.Ticks++;
            stats.MergeNotes += tick.Merge.Notes.Count;
            if (tick.AdvisorRan) stats.AdvisorRuns++;
            CountDelta(tick.PanelDelta, stats);

            if (verbose)
            {
                Console.WriteLine($"T{utterance.Index,2} [{utterance.Speaker.ToString().ToLowerInvariant(),8}] {utterance.Text}");
                if (tick.Merge.ChangedIds.Count > 0) Console.WriteLine($"     picture: {string.Join(", ", tick.Merge.ChangedIds)}");
                foreach (var note in tick.Merge.Notes) Console.WriteLine($"     note: {note}");
                if (tick.Diff.QuestionsAddressed.Count > 0) Console.WriteLine($"     asked: {string.Join(", ", tick.Diff.QuestionsAddressed)}");
                if (tick.AdvisorRan) PrintDelta(tick.PanelDelta);
                else if (tick.Diff.Advice.Needed) Console.WriteLine("     (advice wanted, damped)");
            }
        }

        var summary = await orchestrator.EndCallAsync();
        stats.FixturesUnused = fixtures?.Remaining ?? 0;

        if (verbose)
        {
            Console.WriteLine(new string('-', 72));
            Console.WriteLine($"Summary: {summary.Summary}");
            foreach (var step in summary.NextSteps) Console.WriteLine($"  next: {step.Text} ({step.Owner.ToString().ToLowerInvariant()})");
            if (stats.FixturesUnused > 0) Console.WriteLine($"WARNING: {stats.FixturesUnused} unused fixture(s) — orchestrator/fixture drift.");
            Console.WriteLine(new string('-', 72));
            Console.WriteLine("Final customer picture:");
            Console.WriteLine(JsonDefaults.Serialize(orchestrator.Picture, pretty: true));
        }
    }
    catch (Exception ex)
    {
        stats.Error = ex.Message;
    }

    return stats;
}

static void CountDelta(PanelDelta? delta, CallStats stats)
{
    if (delta is null) return;
    stats.AddedQuestions += delta.AddedQuestions.Count;
    stats.RemovedQuestions += delta.RemovedQuestionIds.Count;
    stats.AddedProducts += delta.AddedProducts.Count;
}

static void PrintDelta(PanelDelta? delta)
{
    if (delta is null) return;
    foreach (var q in delta.AddedQuestions) Console.WriteLine($"     + fråga  {q.Id}: {q.Text}");
    foreach (var p in delta.AddedProducts) Console.WriteLine($"     + förslag {p.Id}: {p.DisplayName} — {p.Why}");
    foreach (var id in delta.RemovedQuestionIds) Console.WriteLine($"     - fråga  {id}");
    foreach (var id in delta.RemovedProductIds) Console.WriteLine($"     - förslag {id}");
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SalesSupport.slnx")))
        dir = dir.Parent;
    return dir?.FullName ?? Directory.GetCurrentDirectory();
}

internal sealed record ScriptLine(Speaker? Speaker, string? Text, string? Ask, string? Language, string? Customer);

internal sealed class CallStats
{
    public string Mode = "fake";
    public string Language = "sv";
    public int Ticks;
    public int AdvisorRuns;
    public int AddedQuestions;
    public int RemovedQuestions;
    public int AddedProducts;
    public int Asks;
    public int MergeNotes;
    public int FixturesUnused;
    public string? Error;
}
