using Microsoft.AspNetCore.SignalR;
using SalesSupport.Core.Model;

namespace SalesSupport.Backend;

/// <summary>
/// Thin SignalR adapter over CallSessionService — fans tick envelopes out as the events
/// the panel consumes (DESIGN.md §3): TranscriptAppended, PictureUpdated, PanelDelta,
/// TickCompleted, AnswerReady, SummaryReady.
/// </summary>
public sealed class CallHub(CallSessionService sessions, SttTokenService sttTokens) : Hub
{
    public async Task<CallStarted> StartCall(StartCallRequest request)
    {
        SttSession? stt = null;
        if (sttTokens.IsConfigured)
            stt = await sttTokens.IssueAsync(Context.ConnectionAborted);
        return await sessions.StartCallAsync(Context.ConnectionId, request, stt, Context.ConnectionAborted);
    }

    public async Task Utterance(UtteranceIn utterance)
    {
        TickEnvelope? envelope;
        try
        {
            envelope = await sessions.HandleUtteranceAsync(Context.ConnectionId, utterance);
        }
        catch (InvalidOperationException)
        {
            return; // call already ended — late finals from the merger are expected stragglers
        }
        catch (Exception ex)
        {
            // A failing tick must be visible on the panel, not just in the server log.
            await Clients.Caller.SendAsync("TickFailed", ex.Message);
            throw;
        }
        if (envelope is null) return;

        var caller = Clients.Caller;
        await caller.SendAsync("TranscriptAppended", envelope.Transcript);
        if (envelope.Picture is not null)
            await caller.SendAsync("PictureUpdated", envelope.Picture);
        if (envelope.PanelDelta is not null)
            await caller.SendAsync("PanelDelta", envelope.PanelDelta);
        await caller.SendAsync("TickCompleted", envelope.Stats);
    }

    public async Task Ask(string query)
    {
        var answer = await sessions.AskAsync(Context.ConnectionId, query);
        await Clients.Caller.SendAsync("AnswerReady", answer);
    }

    public async Task EndCall()
    {
        var summary = await sessions.EndCallAsync(Context.ConnectionId);
        if (summary is not null)
            await Clients.Caller.SendAsync("SummaryReady", summary);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await sessions.AbandonAsync(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}
