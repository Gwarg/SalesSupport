using SalesSupport.Core.Merging;
using SalesSupport.Core.Model;
using SalesSupport.Orchestrator;

namespace SalesSupport.Core.Tests;

/// <summary>Code guards that hold regardless of how sloppy the model output is.</summary>
public class OrchestratorGuardTests
{
    [Fact]
    public void Asked_questions_are_not_resuggested()
    {
        var panel = new PanelSession();
        panel.Reconcile(new AdvisorResult
        {
            Questions = [new PanelQuestion(null, "Hur många enheter gäller det?", null)],
        });
        panel.MarkAsked(["q1"]);

        var delta = panel.Reconcile(new AdvisorResult
        {
            Questions = [new PanelQuestion(null, "hur många enheter gäller det?", null),
                         new PanelQuestion(null, "Vilken tidsram gäller?", null)],
        });

        Assert.Single(delta.AddedQuestions);
        Assert.Equal("Vilken tidsram gäller?", delta.AddedQuestions[0].Text);
    }

    [Fact]
    public void Spoken_tick_sources_are_coerced_to_call()
    {
        var diff = new GateDiff
        {
            CompanyUpdate = new CompanyInfo("Kund AB", null, null, null, Source.Rep),
            FactsUpsert = [new FactUpsert(null, FactCategory.Pain, "text", Source.Rep, Confidence.High)],
            ProductInterestUpsert = [new ProductInterestUpsert(null, null, "X40", Stance.Owns, "äger", Source.Rep)],
            ActionItemsUpsert = [new ActionItemUpsert(null, "skicka offert", ActionOwner.Rep, Source.Rep)],
        };

        var coerced = CallOrchestrator.CoerceSpokenSources(diff, "Duab (demo)");

        Assert.Equal(Source.Call, coerced.CompanyUpdate!.Source);
        Assert.Equal(Source.Call, coerced.FactsUpsert[0].Source);
        Assert.Equal(Source.Call, coerced.ProductInterestUpsert[0].Source);
        Assert.Equal(Source.Call, coerced.ActionItemsUpsert[0].Source);
        Assert.Equal(ActionOwner.Rep, coerced.ActionItemsUpsert[0].Owner);
    }

    [Fact]
    public void Crm_mislabels_on_spoken_ticks_are_also_coerced_to_call()
    {
        var diff = new GateDiff
        {
            FactsUpsert = [new FactUpsert(null, FactCategory.Pain, "text", Source.Crm, Confidence.High)],
        };

        var coerced = CallOrchestrator.CoerceSpokenSources(diff, "Duab (demo)");

        Assert.Equal(Source.Call, coerced.FactsUpsert[0].Source);
    }

    [Fact]
    public void Seller_hallucinated_as_customer_company_is_dropped()
    {
        var diff = new GateDiff
        {
            CompanyUpdate = new CompanyInfo("Duab", "skanningsteknik", "small", "Sverige", Source.Call),
        };

        var coerced = CallOrchestrator.CoerceSpokenSources(diff, "Duab (demo)");

        Assert.Null(coerced.CompanyUpdate);
    }

    [Fact]
    public void Real_customer_company_survives_the_seller_guard()
    {
        var diff = new GateDiff
        {
            CompanyUpdate = new CompanyInfo("Nordfrys AB", null, null, null, Source.Call),
        };

        var coerced = CallOrchestrator.CoerceSpokenSources(diff, "Duab (demo)");

        Assert.NotNull(coerced.CompanyUpdate);
        Assert.Equal("Nordfrys AB", coerced.CompanyUpdate!.Name);
    }

    [Fact]
    public void Ballooning_thread_notes_are_clipped()
    {
        var picture = new CustomerPicture();
        var longNote = string.Join(" ", Enumerable.Repeat("kunden sa något viktigt", 20));

        PictureMerger.Apply(picture, new GateDiff
        {
            ThreadsUpsert = [new ThreadUpsert(null, "ämne", ThreadKind.Discovery, ThreadStatus.Open, Salience.Medium, longNote)],
        }, turn: 1);

        Assert.True(picture.Threads[0].Note.Length <= 160);
        Assert.EndsWith("…", picture.Threads[0].Note);
    }
}
