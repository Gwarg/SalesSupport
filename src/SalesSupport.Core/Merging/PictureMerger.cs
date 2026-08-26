using SalesSupport.Core.Model;

namespace SalesSupport.Core.Merging;

public sealed class MergeOutcome
{
    public List<string> ChangedIds { get; } = [];
    public List<Fact> RemovedFacts { get; } = [];
    public List<string> Notes { get; } = [];
}

/// <summary>
/// Applies a gate diff to the picture per the merge rules in docs/customer-picture.md:
/// merger-assigned ids, turn stamping, provenance guard (a call-sourced change never
/// overwrites a rep-sourced item), list caps as hard backstops, archival removal.
/// </summary>
public static class PictureMerger
{
    public const int MaxFacts = 30;
    public const int MaxThreads = 15;
    public const int MaxOpenThreads = 8;
    public const int MaxProductInterest = 15;
    public const int MaxActionItems = 10;

    public static MergeOutcome Apply(CustomerPicture picture, GateDiff diff, int turn)
    {
        var outcome = new MergeOutcome();

        if (diff.CompanyUpdate is { } company)
        {
            if (picture.Company is { Source: Source.Rep } && company.Source != Source.Rep)
            {
                outcome.Notes.Add("company: rejected non-rep overwrite of rep-sourced company");
            }
            else if (!company.Equals(picture.Company))
            {
                picture.Company = company;
                outcome.ChangedIds.Add("company");
            }
        }

        foreach (var up in diff.FactsUpsert)
        {
            var existing = up.Id is null ? null : picture.Facts.FirstOrDefault(f => f.Id == up.Id);
            if (up.Id is not null && existing is null)
                outcome.Notes.Add($"facts: unknown id {up.Id}, treated as add");
            if (existing is null && picture.Facts.FirstOrDefault(f => Normalize(f.Text) == Normalize(up.Text)) is { } duplicateFact)
            {
                existing = duplicateFact;
                outcome.Notes.Add($"facts: duplicate of {duplicateFact.Id}, merged");
            }
            if (existing is null)
            {
                // Paraphrase guard: same category, token subset one way or the other.
                var newTokens = TokenSet(up.Text);
                var related = picture.Facts.FirstOrDefault(f =>
                    f.Category == up.Category &&
                    (newTokens.IsSubsetOf(TokenSet(f.Text)) || TokenSet(f.Text).IsSubsetOf(newTokens)));
                if (related is not null)
                {
                    if (newTokens.IsSubsetOf(TokenSet(related.Text)) && !TokenSet(related.Text).SetEquals(newTokens))
                    {
                        outcome.Notes.Add($"facts: subsumed by {related.Id}, skipped");
                        continue;
                    }
                    existing = related;
                    outcome.Notes.Add($"facts: paraphrase of {related.Id}, merged");
                }
            }

            if (existing is null)
            {
                if (picture.Facts.Count >= MaxFacts) { outcome.Notes.Add("facts: at cap, add rejected"); continue; }
                var fact = new Fact(NextId(picture.Facts.Select(f => f.Id), "f"), up.Category, up.Text, up.Source, up.Confidence, turn);
                picture.Facts.Add(fact);
                outcome.ChangedIds.Add(fact.Id);
            }
            else
            {
                if (existing.Source == Source.Rep && up.Source != Source.Rep)
                { outcome.Notes.Add($"facts: {existing.Id} rep-sourced, non-rep update rejected"); continue; }
                var updated = existing with { Category = up.Category, Text = up.Text, Source = up.Source, Confidence = up.Confidence, Turn = turn };
                if (updated != existing)
                {
                    picture.Facts[picture.Facts.IndexOf(existing)] = updated;
                    outcome.ChangedIds.Add(updated.Id);
                }
            }
        }

        foreach (var id in diff.FactsRemove)
        {
            var existing = picture.Facts.FirstOrDefault(f => f.Id == id);
            if (existing is null) { outcome.Notes.Add($"facts: remove of unknown id {id} ignored"); continue; }
            picture.Facts.Remove(existing);
            outcome.RemovedFacts.Add(existing);
            outcome.ChangedIds.Add(id);
        }

        foreach (var up in diff.ThreadsUpsert)
        {
            var existing = up.Id is null ? null : picture.Threads.FirstOrDefault(t => t.Id == up.Id);
            if (up.Id is not null && existing is null)
                outcome.Notes.Add($"threads: unknown id {up.Id}, treated as add");
            if (existing is null && picture.Threads.FirstOrDefault(t => Normalize(t.Topic) == Normalize(up.Topic)) is { } duplicateThread)
            {
                existing = duplicateThread;
                outcome.Notes.Add($"threads: duplicate of {duplicateThread.Id}, merged");
            }

            if (existing is null)
            {
                if (picture.Threads.Count >= MaxThreads) { outcome.Notes.Add("threads: at cap, add rejected"); continue; }
                if (up.Status == ThreadStatus.Open && picture.Threads.Count(t => t.Status == ThreadStatus.Open) >= MaxOpenThreads)
                { outcome.Notes.Add("threads: open cap reached, add rejected"); continue; }
                var thread = new ConversationThread(NextId(picture.Threads.Select(t => t.Id), "t"), up.Topic, up.Kind, up.Status, up.Salience, Clip(up.Note), turn);
                picture.Threads.Add(thread);
                outcome.ChangedIds.Add(thread.Id);
            }
            else
            {
                var updated = existing with { Topic = up.Topic, Kind = up.Kind, Status = up.Status, Salience = up.Salience, Note = Clip(up.Note), Turn = turn };
                if (updated != existing)
                {
                    picture.Threads[picture.Threads.IndexOf(existing)] = updated;
                    outcome.ChangedIds.Add(updated.Id);
                }
            }
        }

        foreach (var up in diff.ProductInterestUpsert)
        {
            var existing = up.Id is null ? null : picture.ProductInterest.FirstOrDefault(p => p.Id == up.Id);
            if (up.Id is not null && existing is null)
                outcome.Notes.Add($"product_interest: unknown id {up.Id}, treated as add");
            if (existing is null && picture.ProductInterest.FirstOrDefault(p => Normalize(p.NameAsSaid) == Normalize(up.NameAsSaid)) is { } duplicateProduct)
            {
                existing = duplicateProduct;
                outcome.Notes.Add($"product_interest: duplicate of {duplicateProduct.Id}, merged");
            }

            if (existing is null)
            {
                if (picture.ProductInterest.Count >= MaxProductInterest) { outcome.Notes.Add("product_interest: at cap, add rejected"); continue; }
                var item = new ProductInterest(NextId(picture.ProductInterest.Select(p => p.Id), "p"), up.ProductRef, up.NameAsSaid, up.Stance, Clip(up.Reason), up.Source, turn);
                picture.ProductInterest.Add(item);
                outcome.ChangedIds.Add(item.Id);
            }
            else
            {
                if (existing.Source == Source.Rep && up.Source != Source.Rep)
                { outcome.Notes.Add($"product_interest: {existing.Id} rep-sourced, non-rep update rejected"); continue; }
                var updated = existing with { ProductRef = up.ProductRef ?? existing.ProductRef, NameAsSaid = up.NameAsSaid, Stance = up.Stance, Reason = Clip(up.Reason), Source = up.Source, Turn = turn };
                if (updated != existing)
                {
                    picture.ProductInterest[picture.ProductInterest.IndexOf(existing)] = updated;
                    outcome.ChangedIds.Add(updated.Id);
                }
            }
        }

        foreach (var up in diff.ActionItemsUpsert)
        {
            var existing = up.Id is null ? null : picture.ActionItems.FirstOrDefault(a => a.Id == up.Id);
            if (up.Id is not null && existing is null)
                outcome.Notes.Add($"action_items: unknown id {up.Id}, treated as add");
            if (existing is null && picture.ActionItems.FirstOrDefault(a => Normalize(a.Text) == Normalize(up.Text)) is { } duplicateAction)
            {
                existing = duplicateAction;
                outcome.Notes.Add($"action_items: duplicate of {duplicateAction.Id}, merged");
            }

            if (existing is null)
            {
                if (picture.ActionItems.Count >= MaxActionItems) { outcome.Notes.Add("action_items: at cap, add rejected"); continue; }
                var item = new ActionItem(NextId(picture.ActionItems.Select(a => a.Id), "a"), up.Text, up.Owner, up.Source, turn);
                picture.ActionItems.Add(item);
                outcome.ChangedIds.Add(item.Id);
            }
            else
            {
                if (existing.Source == Source.Rep && up.Source != Source.Rep)
                { outcome.Notes.Add($"action_items: {existing.Id} rep-sourced, non-rep update rejected"); continue; }
                var updated = existing with { Text = up.Text, Owner = up.Owner, Source = up.Source, Turn = turn };
                if (updated != existing)
                {
                    picture.ActionItems[picture.ActionItems.IndexOf(existing)] = updated;
                    outcome.ChangedIds.Add(updated.Id);
                }
            }
        }

        return outcome;
    }

    /// <summary>Advisor-side thread re-prioritization (status/salience only — creation is the gate's job).</summary>
    public static List<string> ApplyThreadUpdates(CustomerPicture picture, IEnumerable<ThreadUpdate> updates, int turn)
    {
        var changed = new List<string>();
        foreach (var update in updates)
        {
            var existing = picture.Threads.FirstOrDefault(t => t.Id == update.Id);
            if (existing is null) continue;
            var updated = existing with
            {
                Status = update.Status ?? existing.Status,
                Salience = update.Salience ?? existing.Salience,
                Turn = turn,
            };
            if (updated != existing)
            {
                picture.Threads[picture.Threads.IndexOf(existing)] = updated;
                changed.Add(updated.Id);
            }
        }
        return changed;
    }

    /// <summary>Dedup key: small models re-emit items with invented ids; identical text means the same item.</summary>
    public static string NormalizeText(string text) =>
        string.Join(' ', text.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.', '!', '?');

    private static string Normalize(string text) => NormalizeText(text);

    /// <summary>Notes and reasons are one-liners; models that append running history get clipped.</summary>
    private static string Clip(string text) => text.Length <= 160 ? text : text[..159] + "…";

    private static HashSet<string> TokenSet(string text) =>
        [.. NormalizeText(text).Split(' ', StringSplitOptions.RemoveEmptyEntries)];

    private static string NextId(IEnumerable<string> existingIds, string prefix)
    {
        var max = 0;
        foreach (var id in existingIds)
        {
            if (id.StartsWith(prefix, StringComparison.Ordinal)
                && int.TryParse(id.AsSpan(prefix.Length), out var n) && n > max)
            {
                max = n;
            }
        }
        return $"{prefix}{max + 1}";
    }
}
