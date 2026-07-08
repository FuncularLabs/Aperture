using Reel.Core.Formatting;
using Reel.Core.Models;
using Reel.Core.Tests.Support;

namespace Reel.Core.Tests;

public class SearchAndAnnotationTests
{
    private static LibraryRow Row(
        string name = "IMG_1.jpg", string alias = "Camera", string? camera = "Pixel 8", MediaKind kind = MediaKind.Image) =>
        new()
        {
            Item = new MediaItem
            {
                Id = 1,
                RelPath = name,
                FileName = name,
                Ext = Path.GetExtension(name),
                MTimeUtc = DateTime.UtcNow,
                Camera = camera,
                Kind = kind,
            },
            RootAlias = alias,
            RootPath = @"C:\x",
        };

    private static Annotation Ann(string[]? tags = null, string note = "") =>
        new() { Tags = tags ?? [], Note = note };

    // --- SearchQuery ---

    [Fact]
    public void Search_TagOperator_MatchesTag()
    {
        var q = SearchQuery.Parse("tag:vacation");
        Assert.True(q.Matches(Row(), Ann(["Vacation", "beach"])));
        Assert.False(q.Matches(Row(), Ann(["work"])));
    }

    [Fact]
    public void Search_NoteOperator_MatchesNoteSubstring()
    {
        var q = SearchQuery.Parse("note:birthday");
        Assert.True(q.Matches(Row(), Ann(note: "Mom's birthday party")));
        Assert.False(q.Matches(Row(), Ann(note: "just a note")));
    }

    [Fact]
    public void Search_TermsAreAnded()
    {
        var q = SearchQuery.Parse("tag:trip is:video");
        Assert.True(q.Matches(Row(kind: MediaKind.Video), Ann(["trip"])));
        Assert.False(q.Matches(Row(kind: MediaKind.Image), Ann(["trip"]))); // not a video
        Assert.False(q.Matches(Row(kind: MediaKind.Video), Ann(["other"]))); // no trip tag
    }

    [Fact]
    public void Search_HasAndType()
    {
        Assert.True(SearchQuery.Parse("has:note").Matches(Row(), Ann(note: "x")));
        Assert.False(SearchQuery.Parse("has:tag").Matches(Row(), Ann()));
        Assert.True(SearchQuery.Parse("type:mp4").Matches(Row("clip.mp4", kind: MediaKind.Video), Ann()));
        Assert.False(SearchQuery.Parse("type:png").Matches(Row("clip.mp4"), Ann()));
    }

    [Fact]
    public void Search_FreeText_MatchesNameAliasCameraTagsNote()
    {
        Assert.True(SearchQuery.Parse("pixel").Matches(Row(camera: "Pixel 8"), Ann()));
        Assert.True(SearchQuery.Parse("sunset").Matches(Row(), Ann(["Sunset"])));
        Assert.True(SearchQuery.Parse("wow").Matches(Row(), Ann(note: "wow nice")));
    }

    [Fact]
    public void Search_QuotedValue_KeepsSpaces()
    {
        var q = SearchQuery.Parse("tag:\"date night\"");
        Assert.True(q.Matches(Row(), Ann(["Date Night"])));
    }

    // --- AnnotationStore ---

    [Fact]
    public void Annotations_SaveAndGet_Roundtrip()
    {
        using var dir = new TempDir();
        using var library = new Reel.Core.Library.LibraryService(dir.Path);
        var path = @"C:\photos\a.jpg";

        library.SaveAnnotation(path, ["Beach", "2024"], "great trip");

        var got = library.GetAnnotation(path);
        Assert.Equal(["Beach", "2024"], got.Tags);
        Assert.Equal("great trip", got.Note);
        Assert.Contains("Beach", library.GetAllTags());
    }

    [Fact]
    public void Annotations_ClearingAll_RemovesRow()
    {
        using var dir = new TempDir();
        using var library = new Reel.Core.Library.LibraryService(dir.Path);
        var path = @"C:\photos\b.jpg";
        library.SaveAnnotation(path, ["x"], "note");

        library.SaveAnnotation(path, [], "");

        Assert.True(library.GetAnnotation(path).IsEmpty);
        Assert.Empty(library.GetAllAnnotations());
    }

    // --- Tag manager ---

    [Fact]
    public void TagCounts_CountFilesPerTag()
    {
        using var dir = new TempDir();
        using var library = new Reel.Core.Library.LibraryService(dir.Path);
        library.SaveAnnotation(@"C:\p\a.jpg", ["beach", "2024"], "");
        library.SaveAnnotation(@"C:\p\b.jpg", ["Beach"], ""); // same tag, different case
        library.SaveAnnotation(@"C:\p\c.jpg", ["work"], "");

        var counts = library.GetTagCounts();

        Assert.Equal(2, counts["beach"]); // case-insensitive
        Assert.Equal(1, counts["2024"]);
        Assert.Equal(1, counts["work"]);
    }

    [Fact]
    public void RenameTag_UpdatesEveryFile()
    {
        using var dir = new TempDir();
        using var library = new Reel.Core.Library.LibraryService(dir.Path);
        library.SaveAnnotation(@"C:\p\a.jpg", ["holiday", "kids"], "");
        library.SaveAnnotation(@"C:\p\b.jpg", ["Holiday"], "");

        library.RenameTag("holiday", "vacation");

        Assert.Equal(["vacation", "kids"], library.GetAnnotation(@"C:\p\a.jpg").Tags);
        Assert.Equal(["vacation"], library.GetAnnotation(@"C:\p\b.jpg").Tags);
        Assert.DoesNotContain("holiday", library.GetAllTags());
    }

    [Fact]
    public void RenameTag_OntoExistingTag_Merges()
    {
        using var dir = new TempDir();
        using var library = new Reel.Core.Library.LibraryService(dir.Path);
        library.SaveAnnotation(@"C:\p\a.jpg", ["car", "auto"], "");

        library.RenameTag("auto", "car"); // merge auto -> car, no duplicate

        Assert.Equal(["car"], library.GetAnnotation(@"C:\p\a.jpg").Tags);
        Assert.Equal(1, library.GetTagCounts()["car"]);
    }

    [Fact]
    public void DeleteTag_RemovesFromAllFilesAndDropsEmptyRows()
    {
        using var dir = new TempDir();
        using var library = new Reel.Core.Library.LibraryService(dir.Path);
        library.SaveAnnotation(@"C:\p\a.jpg", ["temp", "keep"], "");
        library.SaveAnnotation(@"C:\p\b.jpg", ["temp"], ""); // only tag, no note

        library.DeleteTag("temp");

        Assert.Equal(["keep"], library.GetAnnotation(@"C:\p\a.jpg").Tags);
        Assert.True(library.GetAnnotation(@"C:\p\b.jpg").IsEmpty); // row removed
        Assert.DoesNotContain("temp", library.GetAllTags());
    }
}
