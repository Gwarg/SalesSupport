namespace SalesSupport.Core.Model;

public enum PanelItemStatus { Active, Asked, Dismissed }

public sealed record QuestionItem(string Id, string Text, string? ThreadId, PanelItemStatus Status);

public sealed record ProductItem(string Id, string ProductRef, string DisplayName, string Why, string? ThreadId, string? PriceNote, PanelItemStatus Status);

/// <summary>What changed on the panel this tick — kept items never move (docs/panel.md).</summary>
public sealed record PanelDelta(
    IReadOnlyList<QuestionItem> AddedQuestions,
    IReadOnlyList<string> KeptQuestionIds,
    IReadOnlyList<string> RemovedQuestionIds,
    IReadOnlyList<ProductItem> AddedProducts,
    IReadOnlyList<string> KeptProductIds,
    IReadOnlyList<string> RemovedProductIds)
{
    public bool IsEmpty =>
        AddedQuestions.Count == 0 && RemovedQuestionIds.Count == 0 &&
        AddedProducts.Count == 0 && RemovedProductIds.Count == 0;
}
