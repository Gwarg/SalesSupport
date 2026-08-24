using SalesSupport.Core.Contracts;
using SalesSupport.Core.Model;

namespace SalesSupport.ReplayHarness;

/// <summary>
/// Deterministic heuristic stand-in for real models so the whole loop runs offline:
/// proves plumbing (diffs, merging, damping, reconcile), not intelligence. Parses the
/// section layout produced by PromptBuilder (a stable contract).
/// </summary>
public sealed class FakeLlmProvider : ILlmProvider
{
    private int _customerUtterances;

    public Task<T> CompleteJsonAsync<T>(LlmRole role, LlmConversation conversation, CancellationToken ct = default)
        where T : class
    {
        var content = conversation.Messages[^1].Content;
        object result = role switch
        {
            LlmRole.Gate => BuildGateDiff(content),
            LlmRole.Advisor => BuildAdvisorResult(content),
            LlmRole.Summarizer => BuildSummary(content),
            _ => throw new NotSupportedException($"Fake has no behavior for role {role}"),
        };
        return Task.FromResult((T)result);
    }

    private GateDiff BuildGateDiff(string content)
    {
        var (speaker, text) = ParseNewUtterance(content);
        var activeQuestionIds = ParseIds(content, "ACTIVE_QUESTIONS:");
        var isCustomer = speaker == "customer";
        if (isCustomer) _customerUtterances++;

        var diff = new GateDiff
        {
            Advice = BuildAdvice(isCustomer, text),
        };

        if (isCustomer)
        {
            diff.FactsUpsert.Add(new FactUpsert(null, FactCategory.Other, text, Source.Call, Confidence.Medium));

            if (text.Contains('?'))
                diff.ThreadsUpsert.Add(new ThreadUpsert(null, Truncate(text, 6), ThreadKind.CustomerQuestion, ThreadStatus.Open, Salience.Medium, "obesvarad kundfråga"));
            else if (text.Contains("inte", StringComparison.OrdinalIgnoreCase))
                diff.ThreadsUpsert.Add(new ThreadUpsert(null, Truncate(text, 6), ThreadKind.Objection, ThreadStatus.Open, Salience.Medium, "invändning, ej hanterad"));

            if (_customerUtterances % 4 == 0)
                diff = new GateDiff
                {
                    Signals = diff.Signals, FactsUpsert = diff.FactsUpsert, ThreadsUpsert = diff.ThreadsUpsert,
                    Advice = diff.Advice, SummaryAppend = $"Kunden: {Truncate(text, 8)}",
                };
        }
        else if (text.EndsWith('?') && activeQuestionIds.Count > 0)
        {
            diff.QuestionsAddressed.Add(activeQuestionIds[0]);
        }

        return diff;
    }

    private static AdviceDecision BuildAdvice(bool isCustomer, string text)
    {
        var needed = isCustomer && (text.Contains('?') || text.Contains("inte", StringComparison.OrdinalIgnoreCase));
        return new AdviceDecision(needed, needed ? "kundfråga eller invändning" : "", needed ? [text] : []);
    }

    private static AdvisorResult BuildAdvisorResult(string content)
    {
        var onDemand = content.StartsWith("MODE: on_demand", StringComparison.Ordinal);
        var cards = ParseCards(content);
        var existingQuestionIds = ParseIds(content, "PANEL:")
            .Where(id => id.StartsWith('q'))
            .ToList();
        var firstProduct = cards.FirstOrDefault(c => c.Kind == "product") ?? cards.FirstOrDefault();

        var result = new AdvisorResult
        {
            Questions = existingQuestionIds.Take(2).Select(id => new PanelQuestion(id, "", null)).ToList(),
            Answer = onDemand && firstProduct is not null ? $"{firstProduct.Title}: {Truncate(firstProduct.Body, 20)}" : null,
        };

        if (!onDemand && cards.Count > 0)
            result.Questions.Add(new PanelQuestion(null, $"Kan du berätta mer om behovet kring {cards[0].Title.ToLowerInvariant()}?", null));

        if (firstProduct is not null && firstProduct.Kind == "product")
            result.Products.Add(new PanelProduct(null, firstProduct.DocId, firstProduct.Title, Truncate(firstProduct.Body, 12), null, "indikativt — se produktkort"));

        return result;
    }

    private static SummaryResult BuildSummary(string content)
    {
        var rollingLines = content.Split('\n').SkipWhile(l => !l.StartsWith("ROLLING_SUMMARY:", StringComparison.Ordinal)).Count(l => l.StartsWith("- ", StringComparison.Ordinal));
        return new SummaryResult
        {
            Summary = $"Testsamtal genomspelat. {rollingLines} noteringar i rullande summering.",
            NextSteps = [new NextStep("Skicka X60-datablad", ActionOwner.Rep)],
        };
    }

    private static (string Speaker, string Text) ParseNewUtterance(string content)
    {
        var lines = content.Split('\n', StringSplitOptions.TrimEntries);
        var newIndex = Array.FindIndex(lines, l => l == "NEW:");
        var line = lines[newIndex + 1];
        var close = line.IndexOf(']');
        return (line[1..close], line[(close + 1)..].Trim());
    }

    private static List<string> ParseIds(string content, string section)
    {
        var ids = new List<string>();
        var inSection = false;
        foreach (var raw in content.Split('\n', StringSplitOptions.TrimEntries))
        {
            if (raw == section) { inSection = true; continue; }
            if (!inSection) continue;
            var colon = raw.IndexOf(": ", StringComparison.Ordinal);
            if (colon <= 0 || colon > 6) break;
            ids.Add(raw[..colon]);
        }
        return ids;
    }

    private static List<RetrievedCard> ParseCards(string content)
    {
        var cards = new List<RetrievedCard>();
        var inSection = false;
        foreach (var raw in content.Split('\n', StringSplitOptions.TrimEntries))
        {
            if (raw == "CARDS:") { inSection = true; continue; }
            if (!inSection) continue;
            if (!raw.StartsWith("- ", StringComparison.Ordinal)) break;
            var parts = raw[2..].Split('|', StringSplitOptions.TrimEntries);
            if (parts.Length >= 4) cards.Add(new RetrievedCard(parts[0], parts[1], parts[2], parts[3], 0));
        }
        return cards;
    }

    private static string Truncate(string text, int words)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= words ? text.TrimEnd('?', '.') : string.Join(' ', parts.Take(words));
    }
}
