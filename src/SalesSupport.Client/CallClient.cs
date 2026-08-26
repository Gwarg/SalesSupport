using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using SalesSupport.Core.Model;
using SalesSupport.Core.Serialization;

namespace SalesSupport.Client;

/// <summary>SignalR wrapper over the backend's CallHub (DESIGN.md §3 contract).</summary>
public sealed class CallClient : IAsyncDisposable
{
    private HubConnection? _connection;

    public event Action<TranscriptEntry>? TranscriptAppended;
    public event Action<CustomerPicture>? PictureUpdated;
    public event Action<PanelDelta>? PanelDeltaReceived;
    public event Action<TickStats>? TickCompleted;
    public event Action<AnswerEnvelope>? AnswerReady;
    public event Action<SummaryEnvelope>? SummaryReady;
    public event Action<string?>? ConnectionClosed;

    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    public async Task ConnectAsync(string baseUrl, CancellationToken ct = default)
    {
        if (IsConnected) return;
        if (_connection is not null) await _connection.DisposeAsync();

        _connection = new HubConnectionBuilder()
            .WithUrl(baseUrl.TrimEnd('/') + "/hub/call")
            .AddJsonProtocol(o => o.PayloadSerializerOptions = JsonDefaults.Options)
            .Build();

        _connection.On<TranscriptEntry>("TranscriptAppended", e => TranscriptAppended?.Invoke(e));
        _connection.On<CustomerPicture>("PictureUpdated", p => PictureUpdated?.Invoke(p));
        _connection.On<PanelDelta>("PanelDelta", d => PanelDeltaReceived?.Invoke(d));
        _connection.On<TickStats>("TickCompleted", s => TickCompleted?.Invoke(s));
        _connection.On<AnswerEnvelope>("AnswerReady", a => AnswerReady?.Invoke(a));
        _connection.On<SummaryEnvelope>("SummaryReady", s => SummaryReady?.Invoke(s));
        _connection.Closed += ex =>
        {
            ConnectionClosed?.Invoke(ex?.Message);
            return Task.CompletedTask;
        };

        await _connection.StartAsync(ct);
    }

    public Task<CallStarted> StartCallAsync(StartCallRequest request, CancellationToken ct = default) =>
        _connection!.InvokeAsync<CallStarted>("StartCall", request, ct);

    public Task SendUtteranceAsync(UtteranceIn utterance) =>
        _connection!.SendAsync("Utterance", utterance);

    public Task AskAsync(string query) =>
        _connection!.SendAsync("Ask", query);

    public Task EndCallAsync() =>
        _connection!.SendAsync("EndCall");

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null) await _connection.DisposeAsync();
    }
}
