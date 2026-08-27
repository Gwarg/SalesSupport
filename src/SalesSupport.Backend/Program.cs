using SalesSupport.Backend;
using SalesSupport.Core.Contracts;
using SalesSupport.Core.Serialization;
using SalesSupport.Knowledge;
using SalesSupport.Providers.Claude;
using SalesSupport.Providers.Ollama;
using SalesSupport.Providers.OpenAiCompat;

var builder = WebApplication.CreateBuilder(args);

var options = builder.Configuration.GetSection("Backend").Get<BackendOptions>() ?? new BackendOptions();
builder.Services.AddSingleton(options);

var logFile = Path.Combine("logs", $"backend-{DateTime.Now:yyyyMMdd}.log");
builder.Logging.AddProvider(new FileLoggerProvider(logFile));

var packPath = options.PackPath;
if (string.IsNullOrEmpty(packPath))
{
    packPath = Directory.Exists(options.PacksDirectory)
        ? Directory.GetFiles(options.PacksDirectory, "*.pack.sqlite").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
        : null;
}
if (packPath is null)
    throw new InvalidOperationException(
        $"No knowledge pack found (Backend:PackPath unset, no *.pack.sqlite in '{options.PacksDirectory}'). " +
        "Build one: dotnet run --project src/SalesSupport.Pipeline -- --input samples/catalog/duab-demo.canonical.jsonl --company duab-demo");

var pack = SqlitePackKnowledge.Load(packPath, EmbedderFactory.ForPack(packPath, options.ModelDirectory));
builder.Services.AddSingleton<IKnowledgeSource>(pack);

builder.Services.AddSingleton<ILlmProvider>(sp => options.LlmProvider switch
{
    "ollama" => new OllamaLlmProvider(OllamaProviderOptions.ForModel(
        options.OllamaModel,
        options.OllamaNoThink ? false : null,
        diagnostics: message => sp.GetRequiredService<ILoggerFactory>().CreateLogger("Ollama").LogInformation("{Stats}", message))),
    "claude" => new ClaudeLlmProvider(new ClaudeProviderOptions
    {
        UsageReported = UsageLogger(sp, "Claude"),
    }),
    "openai-compat" => new OpenAiCompatLlmProvider(OpenAiCompatProviderOptions.ForModel(
        new Uri(options.OpenAiCompatBaseUrl
            ?? throw new InvalidOperationException("Backend:LlmProvider is 'openai-compat' but Backend:OpenAiCompatBaseUrl is not set.")),
        Environment.GetEnvironmentVariable(options.OpenAiCompatApiKeyEnv),
        options.OpenAiCompatModel
            ?? throw new InvalidOperationException("Backend:LlmProvider is 'openai-compat' but Backend:OpenAiCompatModel is not set."),
        UsageLogger(sp, "OpenAiCompat"),
        strictSchema: options.OpenAiCompatStrictSchema)),
    var other => throw new InvalidOperationException($"Unknown Backend:LlmProvider '{other}' (ollama | claude | openai-compat)"),
});

builder.Services.AddHttpClient();
builder.Services.AddSingleton<SttTokenService>();
builder.Services.AddSingleton(sp => new StorageService(options.DatabasePath, options.RetentionDays));
builder.Services.AddSingleton(sp => new CallSessionService(
    sp.GetRequiredService<ILlmProvider>(),
    sp.GetRequiredService<IKnowledgeSource>(),
    pack.SttVocabulary,
    sp.GetRequiredService<StorageService>(),
    options,
    sp.GetRequiredService<ILogger<CallSessionService>>()));

builder.Services.AddSignalR(o =>
{
    // Slow inference queues ticks on the session lock; parallel invocations let EndCall
    // and Ask overtake queued Utterance calls instead of waiting behind the whole backlog.
    o.MaximumParallelInvocationsPerClient = 8;
}).AddJsonProtocol(o =>
{
    o.PayloadSerializerOptions = JsonDefaults.Options;
});

var app = builder.Build();

app.MapHub<CallHub>("/hub/call");

app.MapGet("/healthz", (IKnowledgeSource knowledge) => Results.Ok(new
{
    status = "ok",
    pack = pack.PackVersion,
    company = pack.CompanyId,
    llm = options.LlmProvider,
}));

app.MapGet("/api/stt-token", async (SttTokenService tokens, CancellationToken ct) =>
{
    if (!tokens.IsConfigured)
        return Results.Problem("Azure Speech is not configured on the backend.", statusCode: 503);
    return Results.Ok(await tokens.IssueAsync(ct));
});

app.Logger.LogInformation("SalesSupport backend up — pack {Pack} ({Company}), llm {Llm}",
    Path.GetFileName(packPath), pack.CompanyId, options.LlmProvider);

app.Run();

static Action<LlmUsage> UsageLogger(IServiceProvider sp, string category) =>
    usage => sp.GetRequiredService<ILoggerFactory>().CreateLogger(category).LogInformation(
        "{Role} {Model}: in={Input} cached={CacheRead} out={Output} tokens",
        usage.Role, usage.Model, usage.InputTokens, usage.CacheReadTokens, usage.OutputTokens);

public partial class Program;
