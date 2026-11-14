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
    public void RowsForDifferentPaths_AllLoad()
    {
        using var scope = new ApertureScope();
        var tags = new AnnotationStore(scope.Database);
        tags.Save(scope.Library.Combine("a.jpg"), ["one"], "");
        tags.Save(scope.Library.Combine("b.jpg"), ["two"], "");

        Assert.Equal(2, tags.GetAll().Count);
    }
}
