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
}
