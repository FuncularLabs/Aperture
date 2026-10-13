using Aperture.Core.Storage;
using Aperture.Core.Tests.Support;

namespace Aperture.Core.Tests;

/// <summary>
/// Annotation paths and tag names are FunkyORM string keys. FunkyORM 3.10.1 sends a key to the database as a
/// parameter; up to 3.10.0 it was written into the SQL, so a path or tag with an apostrophe ("Paul's photos")
/// failed to save, and a crafted key could match another row.
/// </summary>
public class StorageKeyTests
{
    private const string PathWithApostrophe = @"C:\Users\Paul\Pictures\Paul's photos\beach.jpg";

    [Fact]
    public void Annotation_PathWithApostrophe_SavesUpdatesAndClears()
    {
        using var scope = new ApertureScope();
        var store = new AnnotationStore(scope.Database);

        store.Save(PathWithApostrophe, ["beach"], "first");
        var saved = store.Get(PathWithApostrophe);
        Assert.Equal(["beach"], saved.Tags);
        Assert.Equal("first", saved.Note);

        store.Save(PathWithApostrophe, ["beach", "sunset"], "second");
        var updated = store.Get(PathWithApostrophe);
        Assert.Equal(["beach", "sunset"], updated.Tags);
        Assert.Equal("second", updated.Note);

        store.Save(PathWithApostrophe, [], "");
        var cleared = store.Get(PathWithApostrophe);
        Assert.Empty(cleared.Tags);
        Assert.Equal("", cleared.Note);
    }

    [Fact]
    public void Annotation_Get_IsCaseInsensitiveOnThePath()
    {
        using var scope = new ApertureScope();
        var store = new AnnotationStore(scope.Database);

        store.Save(@"C:\Pics\Paul's\A.JPG", ["x"], "");

        Assert.Equal(["x"], store.Get(@"c:\pics\paul's\a.jpg").Tags);
    }

    [Fact]
    public void Annotation_Get_KeyThatWouldAlterAnInlinedQuery_MatchesNoOtherRow()
    {
        using var scope = new ApertureScope();
        var store = new AnnotationStore(scope.Database);
        store.Save(@"C:\Pics\a.jpg", ["x"], "kept");

        var other = store.Get("x' OR '1'='1");

        Assert.Empty(other.Tags);
        Assert.Equal("", other.Note);
        Assert.Equal("kept", store.Get(@"C:\Pics\a.jpg").Note);
    }

    [Fact]
    public void Annotation_AddingATagWithApostrophe_SavesAndRecordsItsUse()
    {
        using var scope = new ApertureScope();
        var store = new AnnotationStore(scope.Database);

        store.Save(@"C:\Pics\a.jpg", ["mom's birthday"], "");

        Assert.Equal(["mom's-birthday"], store.Get(@"C:\Pics\a.jpg").Tags);
        Assert.Equal(1, new TagStatsStore(scope.Database).GetAll()["mom's-birthday"].UseCount);
    }

    [Fact]
    public void TagStats_TagWithApostrophe_CountsEachUse()
    {
        using var scope = new ApertureScope();
        var stats = new TagStatsStore(scope.Database);

        stats.RecordUse(["it's"]);
        stats.RecordUse(["it's"]);

        Assert.Equal(2, stats.GetAll()["it's"].UseCount);
    }

    [Fact]
    public void TagStats_IsCaseInsensitiveOnTheName()
    {
        using var scope = new ApertureScope();
        var stats = new TagStatsStore(scope.Database);

        stats.RecordUse(["Beach"]);
        stats.RecordUse(["beach"]);

        var all = stats.GetAll();
        Assert.Single(all);
        Assert.Equal(2, all["beach"].UseCount);
    }
}
