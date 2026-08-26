using System.Text;
using SalesSupport.Core.Contracts;
using SalesSupport.Core.Model;
using SalesSupport.Core.Serialization;
using SalesSupport.Orchestrator;
using SalesSupport.Providers.Claude;
using SalesSupport.ReplayHarness;

Console.OutputEncoding = Encoding.UTF8;

var live = false;
var ollama = false;
string? ollamaModel = null;
var ollamaNoThink = false;
var useFixtures = false;
var runAll = false;
var quick = false;
string? dumpDir = null;
string? samplePath = null;
string? packPath = null;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--live": live = true; break;
        case "--ollama": ollama = true; break;
        case "--ollama-model": ollama = true; ollamaModel = args[++i]; break;
        case "--ollama-nothink": ollama = true; ollamaNoThink = true; break;
        case "--fixtures": useFixtures = true; break;
        case "--all": runAll = true; useFixtures = true; break;
        case "--quick": quick = true; break;
        case "--dump": dumpDir = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "prompt-dumps"; break;
        case "--pack": packPath = args[++i]; break;
        default: samplePath = args[i]; break;
    }
}

var repoRoot = FindRepoRoot();
var modeLabel = ollama ? "ollama" : live ? "live" : useFixtures ? "fixtures" : "fake";
var runDir = Path.Combine(repoRoot, "runs", $"{DateTime.Now:yyyyMMdd-HHmmss}-{modeLabel}");

if (live && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")))
{
    Console.Error.WriteLine("--live requires ANTHROPIC_API_KEY to be set.");
    return 1;
}

IKnowledgeSource knowledge = packPath is not null
    ? SalesSupport.Knowledge.SqlitePackKnowledge.Load(
        packPath,
        SalesSupport.Knowledge.EmbedderFactory.ForPack(packPath, SalesSupport.Knowledge.EmbedderFactory.DefaultModelDir(repoRoot)))
    : new InMemoryKnowledge();
if (packPath is not null)
    Console.WriteLine($"Knowledge: pack {Path.GetFileName(packPath)}");

if (runAll)
{
    var samples = Directory.GetFiles(Path.Combine(repoRoot, "samples", "calls"), "*.jsonl").OrderBy(p => p, StringComparer.Ordinal).ToList();
    Console.WriteLine($"Corpus run: {samples.Count} calls");
    Console.WriteLine(new string('-', 104));
    var failures = 0;
    var tableLines = new List<string>();
    foreach (var sample in samples)
    {
        Console.WriteLine($"  → {Path.GetFileNameWithoutExtension(sample)} …");
        var stats = await RunCallAsync(sample, verbose: false);
        var flags = stats.FixturesUnused > 0 ? $" UNUSED-FIXTURES={stats.FixturesUnused}" : "";
        flags += stats.MergeNotes > 0 ? $" notes={stats.MergeNotes}" : "";
        flags += stats.TokensOut > 0 ? $" tok={stats.TokensIn}+{stats.TokensCached}c/{stats.TokensOut}" : "";
        var status = stats.Error is null ? "OK  " : "FAIL";
        var line =
            $"{status} {Path.GetFileNameWithoutExtension(sample),-28} [{stats.Mode,-8}] lang={stats.Language} " +
            $"ticks={stats.Ticks,2} advisor={stats.AdvisorRuns} +q={stats.AddedQuestions} -q={stats.RemovedQuestions} " +
            $"+p={stats.AddedProducts} asks={stats.Asks}{flags}";
        Console.WriteLine(line);
        tableLines.Add(line);
        if (stats.Error is not null) { Console.WriteLine($"     {stats.Error}"); failures++; }
    }
    Console.WriteLine(new string('-', 104));
    Console.WriteLine(failures == 0 ? "All calls completed." : $"{failures} call(s) failed.");
    if (Directory.Exists(runDir))
    {
        File.WriteAllLines(Path.Combine(runDir, "summary.txt"), tableLines);
        Console.WriteLine($"Detailed logs: {runDir}");
    }
    return failures == 0 ? 0 : 1;
}

if (quick) samplePath = Path.Combine(repoRoot, "samples", "calls", "snabbtest.jsonl");
samplePath ??= Path.Combine(repoRoot, "samples", "calls", "nordfrys-cold-storage.jsonl");
if (!File.Exists(samplePath))
{
    Console.Error.WriteLine($"Sample call not found: {samplePath}");
    return 1;
}

var result = await RunCallAsync(samplePath, verbose: true);
if (Directory.Exists(runDir))
    Console.WriteLine($"Log: {Path.Combine(runDir, Path.GetFileNameWithoutExtension(samplePath) + ".log")}");
if (result.Error is not null)
{
    Console.Error.WriteLine($"FAIL: {result.Error}");
    return 1;
}
if (result.TokensOut > 0)
    Console.WriteLine($"Tokens: {result.TokensIn} in + {result.TokensCached} cached / {result.TokensOut} out");
return 0;

async Task<CallStats> RunCallAsync(string path, bool verbose)
{
    var name = Path.GetFileNameWithoutExtension(path);
    var stats = new CallStats();
    var log = new System.Text.StringBuilder();

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
        if (ollama)
        {
            llm = ollamaModel is null && !ollamaNoThink
                ? new SalesSupport.Providers.Ollama.OllamaLlmProvider()
                : new SalesSupport.Providers.Ollama.OllamaLlmProvider(
                    SalesSupport.Providers.Ollama.OllamaProviderOptions.ForModel(
                        ollamaModel ?? "qwen3:8b", ollamaNoThink || ollamaModel is null ? false : null));
            stats.Mode = ollamaModel is null ? "ollama" : $"ollama:{ollamaModel}";
        }
        else if (live)
        {
            // Explicit model flags beat the fixtures that --all implies — fixtures are the
            // comparison baseline, not the runtime, when a real backend was requested.
            llm = new ClaudeLlmProvider(new ClaudeProviderOptions
            {
                UsageReported = usage =>
                {
                    stats.TokensIn += usage.InputTokens;
                    stats.TokensCached += usage.CacheReadTokens;
                    stats.TokensOut += usage.OutputTokens;
                    log.AppendLine($"   [usage] {usage.Role} {usage.Model}: in={usage.InputTokens} cached={usage.CacheReadTokens} out={usage.OutputTokens}");
                },
            });
            stats.Mode = "live";
        }
        else if (useFixtures && File.Exists(fixturesPath))
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
        log.AppendLine($"{name} [{stats.Mode}] lang={language} {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        log.AppendLine(new string('-', 72));

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
                log.AppendLine($"ASK: {query}");
                var ask = await orchestrator.AskAsync(query);
                CountDelta(ask.PanelDelta, stats);
                log.AppendLine($"   svar: {ask.Answer}");
                AppendDeltaLog(log, ask.PanelDelta);
                if (verbose)
                {
                    Console.WriteLine($"     svar: {ask.Answer}");
                    PrintDelta(ask.PanelDelta);
                }
                continue;
            }

            var utterance = new Utterance(++index, line.Speaker!.Value, line.Text!);
            var tick = await orchestrator.OnUtteranceAsync(utterance);
            AppendTickLog(log, utterance, tick);
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
        log.AppendLine(new string('-', 72));
        log.AppendLine($"SUMMARY: {summary.Summary}");
        foreach (var step in summary.NextSteps)
            log.AppendLine($"   next: {step.Text} ({step.Owner.ToString().ToLowerInvariant()})");
        log.AppendLine();
        log.AppendLine("FINAL PICTURE:");
        log.AppendLine(JsonDefaults.Serialize(orchestrator.Picture, pretty: true));

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
        log.AppendLine($"ERROR: {ex}");
    }

    if (log.Length > 0)
    {
        try
        {
            Directory.CreateDirectory(runDir);
            File.WriteAllText(Path.Combine(runDir, name + ".log"), log.ToString());
        }
        catch
        {
        }
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

static void AppendTickLog(System.Text.StringBuilder log, Utterance utterance, TickResult tick)
{
    log.AppendLine($"T{utterance.Index,2} [{utterance.Speaker.ToString().ToLowerInvariant()}] {utterance.Text}");
    foreach (var f in tick.Diff.FactsUpsert)
        log.AppendLine($"   +fact[{f.Category.ToString().ToLowerInvariant()}] {f.Text}");
    foreach (var id in tick.Diff.FactsRemove)
        log.AppendLine($"   -fact {id}");
    foreach (var t in tick.Diff.ThreadsUpsert)
        log.AppendLine($"   ~thread \"{t.Topic}\" [{t.Kind.ToString().ToLowerInvariant()}/{t.Status.ToString().ToLowerInvariant()}/{t.Salience.ToString().ToLowerInvariant()}] {t.Note}");
    foreach (var p in tick.Diff.ProductInterestUpsert)
        log.AppendLine($"   ~product {p.NameAsSaid} [{p.Stance.ToString().ToLowerInvariant()}] {p.Reason}");
    foreach (var a in tick.Diff.ActionItemsUpsert)
        log.AppendLine($"   +action ({a.Owner.ToString().ToLowerInvariant()}) {a.Text}");
    if (tick.Diff.QuestionsAddressed.Count > 0)
        log.AppendLine($"   asked: {string.Join(", ", tick.Diff.QuestionsAddressed)}");
    if (tick.Diff.SummaryAppend is { } summaryAppend)
        log.AppendLine($"   summary+: {summaryAppend}");
    log.AppendLine(tick.Diff.Advice.Needed
        ? $"   advice: NEEDED ({tick.Diff.Advice.Reason}) topics=[{string.Join("; ", tick.Diff.Advice.Topics)}]{(tick.AdvisorRan ? "" : " -> DAMPED")}"
        : $"   advice: no ({tick.Diff.Advice.Reason})");
    foreach (var note in tick.Merge.Notes)
        log.AppendLine($"   note: {note}");
    AppendDeltaLog(log, tick.PanelDelta);
    log.AppendLine($"   timing: gate {tick.GateMs} ms{(tick.AdvisorRan ? $", advisor {tick.AdvisorMs} ms" : "")}");
}

static void AppendDeltaLog(System.Text.StringBuilder log, PanelDelta? delta)
{
    if (delta is null) return;
    foreach (var q in delta.AddedQuestions)
        log.AppendLine($"   +q {q.Id}: {q.Text}");
    foreach (var p in delta.AddedProducts)
        log.AppendLine($"   +p {p.Id}: {p.DisplayName} — {p.Why}{(p.PriceNote is null ? "" : $" ({p.PriceNote})")}");
    foreach (var id in delta.RemovedQuestionIds)
        log.AppendLine($"   -q {id}");
    foreach (var id in delta.RemovedProductIds)
        log.AppendLine($"   -p {id}");
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
    public long TokensIn;
    public long TokensCached;
    public long TokensOut;
    public string? Error;
}
