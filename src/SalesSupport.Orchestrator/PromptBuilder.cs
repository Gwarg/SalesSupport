using System.Text;
using SalesSupport.Core.Contracts;
using SalesSupport.Core.Model;
using SalesSupport.Core.Serialization;

namespace SalesSupport.Orchestrator;

/// <summary>
/// Assembles model conversations. The section layout (PICTURE / ACTIVE_QUESTIONS / TRANSCRIPT / NEW,
/// MODE / QUERY / CARDS / PANEL) is a stable contract the replay harness fakes parse.
/// TODO: replace placeholder system texts with the full drafts in docs/prompts.md when the
/// Claude provider lands, keeping the prefix-stable caching layout described there.
/// </summary>
public static class PromptBuilder
{
    public static LlmConversation Gate(
        CustomerPicture picture,
        IReadOnlyList<Utterance> window,
        Utterance newUtterance,
        IEnumerable<QuestionItem> activeQuestions,
        OrchestratorOptions options)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"CONTEXT: company={options.CompanyName} call_language={options.CallLanguage}");
        sb.AppendLine("PICTURE:");
        sb.AppendLine(JsonDefaults.Serialize(picture));
        sb.AppendLine("ACTIVE_QUESTIONS:");
        foreach (var q in activeQuestions) sb.AppendLine($"{q.Id}: {q.Text}");
        sb.AppendLine("TRANSCRIPT:");
        foreach (var u in window) sb.AppendLine($"[{u.Speaker.ToString().ToLowerInvariant()}] {u.Text}");
        sb.AppendLine("NEW:");
        sb.AppendLine($"[{newUtterance.Speaker.ToString().ToLowerInvariant()}] {newUtterance.Text}");

        return new LlmConversation(
            System: "Gate placeholder system prompt — see docs/prompts.md.",
            Messages: [LlmMessage.User(sb.ToString())]);
    }

    public static LlmConversation Advisor(
        CustomerPicture picture,
        IReadOnlyList<RetrievedCard> cards,
        PanelSession panel,
        OrchestratorOptions options,
        string? repQuery = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"MODE: {(repQuery is null ? "proactive" : "on_demand")}");
        if (repQuery is not null) sb.AppendLine($"QUERY: {repQuery}");
        sb.AppendLine("PICTURE:");
        sb.AppendLine(JsonDefaults.Serialize(picture));
        sb.AppendLine("CARDS:");
        foreach (var card in cards) sb.AppendLine($"- {card.DocId} | {card.Kind} | {card.Title} | {card.Body}");
        sb.AppendLine("PANEL:");
        foreach (var q in panel.ActiveQuestions) sb.AppendLine($"{q.Id}: {q.Text}");
        foreach (var p in panel.Products.Where(p => p.Status == PanelItemStatus.Active)) sb.AppendLine($"{p.Id}: {p.DisplayName}");
        sb.AppendLine("ASKED_OR_DISMISSED:");
        foreach (var text in panel.AskedHistory.Concat(panel.DismissedHistory)) sb.AppendLine($"- {text}");

        return new LlmConversation(
            System: "Advisor placeholder system prompt — see docs/prompts.md.",
            Messages: [LlmMessage.User(sb.ToString())]);
    }

    public static LlmConversation Summarizer(
        CustomerPicture picture,
        IReadOnlyList<string> rollingSummary,
        OrchestratorOptions options)
    {
        var sb = new StringBuilder();
        sb.AppendLine("PICTURE:");
        sb.AppendLine(JsonDefaults.Serialize(picture));
        sb.AppendLine("ROLLING_SUMMARY:");
        foreach (var line in rollingSummary) sb.AppendLine($"- {line}");

        return new LlmConversation(
            System: "Summarizer placeholder system prompt — see docs/prompts.md.",
            Messages: [LlmMessage.User(sb.ToString())]);
    }
}
