using SalesSupport.Core.Model;

namespace SalesSupport.Orchestrator;

/// <summary>
/// Panel suggestion lifecycle (docs/panel.md) — session state, deliberately NOT part of the
/// customer picture. Reconciles the advisor's desired state (id reuse) into a delta:
/// kept items never move, asked/dismissed items feed the history the advisor sees.
/// </summary>
public sealed class PanelSession
{
    private readonly List<QuestionItem> _questions = [];
    private readonly List<ProductItem> _products = [];
    private int _questionSeq;
    private int _productSeq;

    public IReadOnlyList<QuestionItem> Questions => _questions;
    public IReadOnlyList<ProductItem> Products => _products;
    public List<string> AskedHistory { get; } = [];
    public List<string> DismissedHistory { get; } = [];

    public IEnumerable<QuestionItem> ActiveQuestions => _questions.Where(q => q.Status == PanelItemStatus.Active);

    public PanelDelta Reconcile(AdvisorResult desired)
    {
        var addedQ = new List<QuestionItem>();
        var keptQ = new List<string>();
        var desiredQIds = new HashSet<string>();

        foreach (var question in desired.Questions)
        {
            var existing = question.Id is null ? null : _questions.FirstOrDefault(q => q.Id == question.Id && q.Status == PanelItemStatus.Active);
            if (existing is not null)
            {
                desiredQIds.Add(existing.Id);
                keptQ.Add(existing.Id);
            }
            else if (string.IsNullOrWhiteSpace(question.Text))
            {
                // Unknown/stale id with no text — nothing renderable; ignore rather than show an empty slot.
            }
            else
            {
                var item = new QuestionItem($"q{++_questionSeq}", question.Text, question.Thread, PanelItemStatus.Active);
                _questions.Add(item);
                desiredQIds.Add(item.Id);
                addedQ.Add(item);
            }
        }

        var removedQ = _questions
            .Where(q => q.Status == PanelItemStatus.Active && !desiredQIds.Contains(q.Id))
            .Select(q => q.Id)
            .ToList();
        _questions.RemoveAll(q => removedQ.Contains(q.Id));

        var addedP = new List<ProductItem>();
        var keptP = new List<string>();
        var desiredPIds = new HashSet<string>();

        foreach (var product in desired.Products)
        {
            var existing = product.Id is null ? null : _products.FirstOrDefault(p => p.Id == product.Id && p.Status == PanelItemStatus.Active);
            if (existing is not null)
            {
                desiredPIds.Add(existing.Id);
                keptP.Add(existing.Id);
            }
            else if (string.IsNullOrWhiteSpace(product.DisplayName))
            {
                // Unknown/stale id with no content — ignore.
            }
            else
            {
                var item = new ProductItem($"pr{++_productSeq}", product.ProductRef, product.DisplayName, product.Why, product.Thread, product.PriceNote, PanelItemStatus.Active);
                _products.Add(item);
                desiredPIds.Add(item.Id);
                addedP.Add(item);
            }
        }

        var removedP = _products
            .Where(p => p.Status == PanelItemStatus.Active && !desiredPIds.Contains(p.Id))
            .Select(p => p.Id)
            .ToList();
        _products.RemoveAll(p => removedP.Contains(p.Id));

        return new PanelDelta(addedQ, keptQ, removedQ, addedP, keptP, removedP);
    }

    /// <summary>Gate-detected (questions_addressed) or manual click. Asked items leave the active set at the next reconcile.</summary>
    public void MarkAsked(IEnumerable<string> questionIds)
    {
        foreach (var id in questionIds)
        {
            var index = _questions.FindIndex(q => q.Id == id && q.Status == PanelItemStatus.Active);
            if (index < 0) continue;
            _questions[index] = _questions[index] with { Status = PanelItemStatus.Asked };
            AskedHistory.Add(_questions[index].Text);
        }
    }

    public void DismissQuestion(string id)
    {
        var index = _questions.FindIndex(q => q.Id == id && q.Status == PanelItemStatus.Active);
        if (index < 0) return;
        DismissedHistory.Add(_questions[index].Text);
        _questions.RemoveAt(index);
    }
}
