using SalesSupport.Core.Merging;
using SalesSupport.Core.Model;

namespace SalesSupport.Core.Tests;

public class PictureMergerTests
{
    private static GateDiff DiffWithFact(string? id, string text, Source source) => new()
    {
        FactsUpsert = [new FactUpsert(id, FactCategory.Situation, text, source, Confidence.Medium)],
    };

    [Fact]
    public void Add_assigns_sequential_ids_and_stamps_turn()
    {
        var picture = new CustomerPicture();

        var first = PictureMerger.Apply(picture, DiffWithFact(null, "a", Source.Call), turn: 3);
        var second = PictureMerger.Apply(picture, DiffWithFact(null, "b", Source.Call), turn: 5);

        Assert.Equal(["f1"], first.ChangedIds);
        Assert.Equal(["f2"], second.ChangedIds);
        Assert.Equal(3, picture.Facts[0].Turn);
        Assert.Equal(5, picture.Facts[1].Turn);
    }

    [Fact]
    public void Upsert_with_known_id_updates_in_place()
    {
        var picture = new CustomerPicture();
        PictureMerger.Apply(picture, DiffWithFact(null, "old", Source.Crm), turn: 0);

        var outcome = PictureMerger.Apply(picture, DiffWithFact("f1", "new", Source.Call), turn: 4);

        Assert.Single(picture.Facts);
        Assert.Equal("new", picture.Facts[0].Text);
        Assert.Equal(Source.Call, picture.Facts[0].Source);
        Assert.Equal(["f1"], outcome.ChangedIds);
    }

    [Fact]
    public void Call_sourced_update_never_overwrites_rep_sourced_item()
    {
        var picture = new CustomerPicture();
        PictureMerger.Apply(picture, DiffWithFact(null, "typed by rep", Source.Rep), turn: 1);

        var outcome = PictureMerger.Apply(picture, DiffWithFact("f1", "heard on call", Source.Call), turn: 2);

        Assert.Equal("typed by rep", picture.Facts[0].Text);
        Assert.Empty(outcome.ChangedIds);
        Assert.Contains(outcome.Notes, n => n.Contains("rep-sourced"));
    }

    [Fact]
    public void Rep_sourced_update_may_overwrite_rep_sourced_item()
    {
        var picture = new CustomerPicture();
        PictureMerger.Apply(picture, DiffWithFact(null, "first", Source.Rep), turn: 1);

        PictureMerger.Apply(picture, DiffWithFact("f1", "corrected", Source.Rep), turn: 2);

        Assert.Equal("corrected", picture.Facts[0].Text);
    }

    [Fact]
    public void Unknown_id_is_treated_as_add_with_note()
    {
        var picture = new CustomerPicture();

        var outcome = PictureMerger.Apply(picture, DiffWithFact("f99", "text", Source.Call), turn: 1);

        Assert.Single(picture.Facts);
        Assert.Equal("f1", picture.Facts[0].Id);
        Assert.Contains(outcome.Notes, n => n.Contains("unknown id f99"));
    }

    [Fact]
    public void Fact_cap_rejects_adds_beyond_limit()
    {
        var picture = new CustomerPicture();
        for (var i = 0; i < PictureMerger.MaxFacts; i++)
            PictureMerger.Apply(picture, DiffWithFact(null, $"fact {i}", Source.Call), turn: i);

        var outcome = PictureMerger.Apply(picture, DiffWithFact(null, "one too many", Source.Call), turn: 99);

        Assert.Equal(PictureMerger.MaxFacts, picture.Facts.Count);
        Assert.Contains(outcome.Notes, n => n.Contains("at cap"));
    }

    [Fact]
    public void Open_thread_cap_rejects_new_open_threads()
    {
        var picture = new CustomerPicture();
        for (var i = 0; i < PictureMerger.MaxOpenThreads; i++)
        {
            PictureMerger.Apply(picture, new GateDiff
            {
                ThreadsUpsert = [new ThreadUpsert(null, $"topic {i}", ThreadKind.Discovery, ThreadStatus.Open, Salience.Medium, "")],
            }, turn: i);
        }

        var outcome = PictureMerger.Apply(picture, new GateDiff
        {
            ThreadsUpsert = [new ThreadUpsert(null, "one more", ThreadKind.Discovery, ThreadStatus.Open, Salience.Medium, "")],
        }, turn: 9);

        Assert.Equal(PictureMerger.MaxOpenThreads, picture.Threads.Count);
        Assert.Contains(outcome.Notes, n => n.Contains("open cap"));
    }

    [Fact]
    public void Duplicate_text_with_invented_id_merges_instead_of_adding()
    {
        var picture = new CustomerPicture();
        PictureMerger.Apply(picture, DiffWithFact(null, "Vill ha det löst före november", Source.Call), turn: 1);

        var outcome = PictureMerger.Apply(picture,
            DiffWithFact("fact_007", "vill ha det löst före november.", Source.Call), turn: 3);

        Assert.Single(picture.Facts);
        Assert.Equal("f1", picture.Facts[0].Id);
        Assert.Contains(outcome.Notes, n => n.Contains("duplicate of f1"));
    }

    [Fact]
    public void Duplicate_thread_topic_merges_and_updates_status()
    {
        var picture = new CustomerPicture();
        PictureMerger.Apply(picture, new GateDiff
        {
            ThreadsUpsert = [new ThreadUpsert(null, "Batteriproblem i frysen", ThreadKind.Discovery, ThreadStatus.Open, Salience.Medium, "")],
        }, turn: 1);

        PictureMerger.Apply(picture, new GateDiff
        {
            ThreadsUpsert = [new ThreadUpsert("thread_002", "batteriproblem i frysen", ThreadKind.Discovery, ThreadStatus.Addressed, Salience.High, "besvarad")],
        }, turn: 4);

        Assert.Single(picture.Threads);
        Assert.Equal(ThreadStatus.Addressed, picture.Threads[0].Status);
    }

    [Fact]
    public void Poorer_paraphrase_is_skipped_richer_paraphrase_updates()
    {
        var picture = new CustomerPicture();
        PictureMerger.Apply(picture, DiffWithFact(null, "X40-skannrarna tappar batteri i frysen och måste lösas före november", Source.Call), turn: 1);

        var poorer = PictureMerger.Apply(picture, DiffWithFact(null, "X40-skannrarna tappar batteri i frysen", Source.Call), turn: 2);
        Assert.Single(picture.Facts);
        Assert.Contains(poorer.Notes, n => n.Contains("subsumed"));

        var richer = PictureMerger.Apply(picture,
            DiffWithFact(null, "X40-skannrarna tappar batteri i frysen och måste lösas före november helst redan i oktober", Source.Call), turn: 3);
        Assert.Single(picture.Facts);
        Assert.Contains(richer.Notes, n => n.Contains("paraphrase"));
        Assert.Contains("oktober", picture.Facts[0].Text);
    }

    [Fact]
    public void Removal_is_archival_removed_fact_is_returned()
    {
        var picture = new CustomerPicture();
        PictureMerger.Apply(picture, DiffWithFact(null, "temp", Source.Call), turn: 1);

        var outcome = PictureMerger.Apply(picture, new GateDiff { FactsRemove = ["f1"] }, turn: 2);

        Assert.Empty(picture.Facts);
        Assert.Single(outcome.RemovedFacts);
        Assert.Equal("temp", outcome.RemovedFacts[0].Text);
    }

    [Fact]
    public void Advisor_thread_updates_change_status_and_salience_only()
    {
        var picture = new CustomerPicture();
        PictureMerger.Apply(picture, new GateDiff
        {
            ThreadsUpsert = [new ThreadUpsert(null, "expansion", ThreadKind.Discovery, ThreadStatus.Open, Salience.High, "")],
        }, turn: 1);

        var changed = PictureMerger.ApplyThreadUpdates(picture,
            [new ThreadUpdate("t1", ThreadStatus.Parked, Salience.Low)], turn: 5);

        Assert.Equal(["t1"], changed);
        Assert.Equal(ThreadStatus.Parked, picture.Threads[0].Status);
        Assert.Equal(Salience.Low, picture.Threads[0].Salience);
        Assert.Equal("expansion", picture.Threads[0].Topic);
    }
}
