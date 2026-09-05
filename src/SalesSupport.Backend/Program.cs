using SalesSupport.Backend;
using SalesSupport.Backend.Telephony;
using SalesSupport.Core.Contracts;
using SalesSupport.Core.Model;
using SalesSupport.Core.Recording;
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

var recordingMode = options.Recording.ToLowerInvariant() switch
{
    "record" => RecordingMode.Record,
    "replay" => RecordingMode.Replay,
    _ => RecordingMode.Off,
};

builder.Services.AddSingleton<ILlmProvider>(sp =>
{
    ILlmProvider Live() => options.LlmProvider switch
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
            strictSchema: options.OpenAiCompatStrictSchema,
            reasoningEffort: options.OpenAiCompatReasoning)),
        var other => throw new InvalidOperationException($"Unknown Backend:LlmProvider '{other}' (ollama | claude | openai-compat)"),
    };

    // Replay never constructs the live provider — no keys needed, no calls possible.
    if (recordingMode == RecordingMode.Off) return Live();
    var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Recording");
    return new RecordingLlmProvider(
        recordingMode == RecordingMode.Record ? Live() : null,
        options.RecordingPath, recordingMode, options.ReplayRecordedLatency,
        message => log.LogWarning("{Message}", message));
});

builder.Services.AddHttpClient();
builder.Services.AddSingleton<SttTokenService>();
builder.Services.AddSingleton(sp => new CustomerIndex(options.CustomerIndexPath, options.DefaultCountryCode));
builder.Services.AddSingleton<CallSignalService>();
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

// D32 telephony edge: Telavox Personal Webhooks hit this on "ringing" (GET or POST form).
app.MapMethods(TelephonyWire.TelavoxRingPath, ["GET", "POST"], async (HttpRequest request, CallSignalService signals) =>
{
    var form = request.HasFormContentType ? await request.ReadFormAsync() : null;
    string? Get(string key) =>
        request.Query.TryGetValue(key, out var fromQuery) ? fromQuery.ToString()
        : form is not null && form.TryGetValue(key, out var fromForm) ? fromForm.ToString()
        : null;

    if (!signals.IsAuthorized(Get("token"))) return Results.Unauthorized();
    await signals.HandleAsync(TelavoxAdapter.Parse(Get), request.HttpContext.RequestAborted);
    return Results.NoContent();
});

app.MapGet("/healthz", (IKnowledgeSource knowledge) => Results.Ok(new
{
    status = "ok",
    pack = pack.PackVersion,
    company = pack.CompanyId,
    llm = options.LlmProvider,
    recording = options.Recording,
}));

app.MapGet("/api/stt-token", async (SttTokenService tokens, CancellationToken ct) =>
{
    if (!tokens.IsConfigured)
        return Results.Problem("Azure Speech is not configured on the backend.", statusCode: 503);
    return Results.Ok(await tokens.IssueAsync(ct));
});

app.Logger.LogInformation("SalesSupport backend up — pack {Pack} ({Company}), llm {Llm}",
    Path.GetFileName(packPath), pack.CompanyId, options.LlmProvider);
if (app.Services.GetRequiredService<ILlmProvider>() is RecordingLlmProvider recording)
    app.Logger.LogWarning("{Mode} MODE — {Count} recorded responses in {Path}{Hint}",
        recording.Mode.ToString().ToUpperInvariant(), recording.Count, recording.Path,
        recording.Mode == RecordingMode.Replay ? " — no live inference; unrecorded prompts get neutral responses" : " — live responses are being saved");
var customerIndex = app.Services.GetRequiredService<CustomerIndex>();
app.Logger.LogInformation("telephony: {Count} numbers in customer index {Path}; Telavox ring endpoint {Endpoint} ({Auth})",
    customerIndex.Count, customerIndex.Path, TelephonyWire.TelavoxRingPath,
    string.IsNullOrEmpty(options.TelephonyWebhookSecret) ? "open — set Backend:TelephonyWebhookSecret" : "token required");

app.Run();

static Action<LlmUsage> UsageLogger(IServiceProvider sp, string category) =>
    usage => sp.GetRequiredService<ILoggerFactory>().CreateLogger(category).LogInformation(
        "{Role} {Model}: in={Input} cached={CacheRead} out={Output} tokens",
        usage.Role, usage.Model, usage.InputTokens, usage.CacheReadTokens, usage.OutputTokens);

public partial class Program;
