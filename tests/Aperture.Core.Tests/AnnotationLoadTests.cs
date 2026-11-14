using Aperture.Core.Storage;
using Aperture.Core.Tests.Support;

namespace Aperture.Core.Tests;

/// <summary>
/// The tags' load (<see cref="AnnotationStore.GetAll"/>) never fails on rows the window takes for one path (0.9.2 review
/// round 21): the column folds only ASCII letters' case, the window every letter's, so "Été\a.jpg" and "été\a.jpg" (a
/// folder renamed so in Explorer, the file tagged again), or "σ.jpg" and "ς.jpg", are two rows to the column and one key
/// to the window. Thrown, the load stopped the library opening. The row written last is kept; merging them is a task of
/// its own.
/// </summary>
public class AnnotationLoadTests
{
    [Theory]
    [InlineData("Été", "été")]
    [InlineData("été", "Été")]
    [InlineData("σ", "ς")]
    public void RowsTheWindowTakesForOnePath_Load_TheOneWrittenLastKept(string first, string last)
    {
        using var scope = new ApertureScope();
        var tags = new AnnotationStore(scope.Database);
        tags.Save(scope.Library.Combine(first, "a.jpg"), ["older"], "");
        Thread.Sleep(20); // their times apart
        tags.Save(scope.Library.Combine(last, "a.jpg"), ["newer"], "");

        var all = tags.GetAll();

        var only = Assert.Single(all);
        Assert.Equal(["newer"], only.Value.Tags);
        Assert.Equal(["newer"], all[scope.Library.Combine(first, "a.jpg")].Tags); // either spelling finds it
        Assert.Contains("newer", tags.GetAllTags());
    }

    [Fact]
    public void TheRowWrittenLast_IsKept_ThoughItWasMadeFirst()
    {
        // Read in the order they were made: the one written last isn't always the one read last.
        using var scope = new ApertureScope();
        var tags = new AnnotationStore(scope.Database);
        var first = scope.Library.Combine("Été", "a.jpg");
        tags.Save(first, ["older"], "");
        Thread.Sleep(20);
        tags.Save(scope.Library.Combine("été", "a.jpg"), ["middle"], "");
        Thread.Sleep(20);
        tags.Save(first, ["newest"], "");

        Assert.Equal(["newest"], Assert.Single(tags.GetAll()).Value.Tags);
    }

    [Fact]
    public void TheKeyIsTheKeptRowsOwnPath()
    {
        // Round 22, R F-2: the key was the spelling read first and the value the row written last, so writes made
        // through the key (a tag deleted or renamed, an export) reached the other row: for "σ.jpg" and "ς.jpg", two files.
        using var scope = new ApertureScope();
        var tags = new AnnotationStore(scope.Database);
        tags.Save(scope.Library.Combine("σ.jpg"), ["a"], "");
        Thread.Sleep(20);
        var last = scope.Library.Combine("ς.jpg");
        tags.Save(last, ["b"], "");

        Assert.Equal(last, Assert.Single(tags.GetAll()).Key);
    }

    [Fact]
    public void DeletingATag_LeavesTheRowsThatDontHaveIt()
    {
        using var scope = new ApertureScope();
        var tags = new AnnotationStore(scope.Database);
        var sigma = scope.Library.Combine("σ.jpg");
        tags.Save(sigma, ["a"], "");
        Thread.Sleep(20);
        var final = scope.Library.Combine("ς.jpg");
        tags.Save(final, ["b"], "");

        tags.DeleteTag("b");

        Assert.Equal(["a"], tags.Get(sigma).Tags);
        Assert.Empty(tags.Get(final).Tags);
    }

    [Fact]
    public void DeletingATag_DeletesItFromARowTheWindowDoesntShow()
    {
        // Round 22: deleted through what the window shows, a row it doesn't (the older of two it takes for one) kept the
        // tag, to show again once the other went.
        using var scope = new ApertureScope();
        var tags = new AnnotationStore(scope.Database);
        var hidden = scope.Library.Combine("σ.jpg");
        tags.Save(hidden, ["a", "b"], "");
        Thread.Sleep(20);
        tags.Save(scope.Library.Combine("ς.jpg"), ["c"], "");

        tags.DeleteTag("b");

        Assert.Equal(["a"], tags.Get(hidden).Tags);
    }

    [Fact]
    public void RenamingATag_RenamesItOnEveryRow_ThoughTheWindowShowsOne()
    {
        // Round 22: a tag renamed is renamed on every file, a row the window doesn't show (the older of two it takes for
        // one) too; before, only the one shown was, and the other kept the old name to show again later.
        using var scope = new ApertureScope();
        var tags = new AnnotationStore(scope.Database);
        var sigma = scope.Library.Combine("σ.jpg");
        tags.Save(sigma, ["a"], "");
        Thread.Sleep(20);
        var final = scope.Library.Combine("ς.jpg");
        tags.Save(final, ["b"], "");

        tags.RenameTag("a", "z");

        Assert.Equal(["z"], tags.Get(sigma).Tags);
        Assert.Equal(["b"], tags.Get(final).Tags);
    }

    [Fact]
    public void TagNamesTheWindowTakesForOne_Load_AsOneTag()
    {
        // Round 22, R F-1 (older than 0.9.2): the tag stats' column folds only ASCII letters' case, so "Österreich" and
        // "österreich" were two rows, and their load (the window's quick picks, its tag list) failed on them: the window
        // closed as the tag dialog did, and the library wouldn't open after. They're one tag: the latest use, the uses
        // added up, under the latest spelling.
        using var scope = new ApertureScope();
        var stats = new TagStatsStore(scope.Database);
        stats.RecordUse(["Österreich"]);
        Thread.Sleep(20);
        stats.RecordUse(["österreich"]);

        var only = Assert.Single(stats.GetAll());

        Assert.Equal("österreich", only.Key);
        Assert.Equal(("österreich", 2), (only.Value.Name, only.Value.UseCount));
        var tags = new AnnotationStore(scope.Database);
        tags.Save(scope.Library.Combine("a.jpg"), ["Österreich"], "");
        Assert.Single(tags.GetTagUsage());
    }

    [Fact]
    public void TagStats_SeededFromTagsMadeBeforeThem_TakeNamesTheWindowTakesForOne_AsOne()
    {
        // The one-time backfill (a library tagged before tag stats existed) counts tags as the window compares them, so
        // it never makes two rows of one tag the load would then take for one (round 22: TagStatsStore's floor too).
        using var scope = new ApertureScope();
        var tags = new AnnotationStore(scope.Database);
        tags.Save(scope.Library.Combine("a.jpg"), ["Österreich", "trip"], "");
        Thread.Sleep(20);
        tags.Save(scope.Library.Combine("b.jpg"), ["österreich"], "");
        using (var connection = scope.Database.OpenMetadata())
        using (var forget = connection.CreateCommand())
        {
            forget.CommandText = "DELETE FROM tag_stats;"; // as a library from before tag stats
            forget.ExecuteNonQuery();
        }

        var usage = new AnnotationStore(scope.Database).GetTagUsage(); // seeds them

        Assert.Equal(2, usage.Count);
        var stats = new TagStatsStore(scope.Database).GetAll();
        Assert.Equal(2, stats.Count);
        Assert.Equal(2, stats["österreich"].UseCount);
        Assert.Equal(1, stats["trip"].UseCount);
    }

    [Fact]
    public void RecordingNoTags_RecordsNothing()
    {
        using var scope = new ApertureScope();
        var stats = new TagStatsStore(scope.Database);

        stats.RecordUse(["", "  "]);

        Assert.Empty(stats.GetAll());
    }

    [Fact]
    public void RowsForDifferentPaths_AllLoad()
    {
        using var scope = new ApertureScope();
        var tags = new AnnotationStore(scope.Database);
        tags.Save(scope.Library.Combine("a.jpg"), ["one"], "");
        tags.Save(scope.Library.Combine("b.jpg"), ["two"], "");

        Assert.Equal(2, tags.GetAll().Count);
    }
}
