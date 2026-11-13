using Aperture.Core.Library;
using Aperture.Core.Storage;
using Aperture.Core.Tests.Support;

namespace Aperture.Core.Tests;

/// <summary>
/// A library's item count read from its own files while it isn't open (0.9.1 batch, item 6: "Each library should show
/// its item count whether or not it's selected"). It is the number the library shows when open (its included folders'
/// items, less those in hidden subfolders), and reading it changes nothing and leaves nothing open.
/// </summary>
public class LibraryCountTests
{
    // A library of 2 + 3 + 4 photos: a ticked folder with a hidden subfolder (2 shown, 3 hidden) and an unticked one (4).
    private static int MakeLibrary(string dataDir, string photos)
    {
        Directory.CreateDirectory(Path.Combine(photos, "shown", "kept"));
        Directory.CreateDirectory(Path.Combine(photos, "shown", "away", "deeper"));
        Directory.CreateDirectory(Path.Combine(photos, "unticked"));
        TestImages.Write(Path.Combine(photos, "shown", "a.jpg"), 32, 24);
        TestImages.Write(Path.Combine(photos, "shown", "kept", "b.jpg"), 32, 24);
        TestImages.Write(Path.Combine(photos, "shown", "away", "c.jpg"), 32, 24);
        TestImages.Write(Path.Combine(photos, "shown", "away", "d.jpg"), 32, 24);
        TestImages.Write(Path.Combine(photos, "shown", "away", "deeper", "e.jpg"), 32, 24);
        for (var i = 0; i < 4; i++)
            TestImages.Write(Path.Combine(photos, "unticked", $"u{i}.jpg"), 32, 24);

        using var library = new LibraryService(dataDir);
        var shown = library.AddRoot(Path.Combine(photos, "shown"), "Shown");
        var unticked = library.AddRoot(Path.Combine(photos, "unticked"), "Unticked");
        library.IndexRoot(shown);
        library.IndexRoot(unticked);
        library.SetFolderShown(shown.Id, "AWAY", shown: false); // any case: it's the same folder
        library.SetIncluded(unticked.Id, false);
        var showing = library.GetContents().Rows.Count;
        library.ShutdownAsync().GetAwaiter().GetResult();
        return showing;
    }

    [Fact]
    public void MatchesWhatTheLibraryShows()
    {
        using var data = new TempDir();
        using var photos = new TempDir();
        var showing = MakeLibrary(data.Path, photos.Path);

        Assert.Equal(2, showing); // the shape the test means: hidden and unticked items are left out
        Assert.Equal(showing, LibraryCount.Read(data.Path));
    }

    [Fact]
    public void ReadsALibraryThatIsOpenElsewhere()
    {
        using var data = new TempDir();
        using var photos = new TempDir();
        var showing = MakeLibrary(data.Path, photos.Path);

        using var other = new LibraryService(data.Path); // another Aperture window has it open
        Assert.Equal(showing, other.GetContents().Rows.Count);

        Assert.Equal(showing, LibraryCount.Read(data.Path));
        other.ShutdownAsync().GetAwaiter().GetResult();
    }

    [Fact]
    public void AnEmptyLibrary_CountsNone()
    {
        using var data = new TempDir();
        using (var library = new LibraryService(data.Path))
            library.ShutdownAsync().GetAwaiter().GetResult();

        Assert.Equal(0, LibraryCount.Read(data.Path));
    }

    [Fact]
    public void AMissingDatabase_IsNotCreated()
    {
        using var data = new TempDir();
        var gone = data.Combine("gone");

        Assert.Null(LibraryCount.Read(data.Path));
        Assert.Null(LibraryCount.Read(gone));

        Assert.False(File.Exists(data.Combine(ApertureDatabase.MetadataFileName)));
        Assert.False(Directory.Exists(gone)); // a library on a drive that isn't connected: nothing made in its place
        Assert.Empty(Directory.GetFileSystemEntries(data.Path));
    }

    [Fact]
    public void AFileThatIsntALibrary_CountsAsUnknown()
    {
        using var data = new TempDir();
        File.WriteAllText(data.Combine(ApertureDatabase.MetadataFileName), "not a database");

        Assert.Null(LibraryCount.Read(data.Path));
        Assert.Equal("not a database", File.ReadAllText(data.Combine(ApertureDatabase.MetadataFileName)));
    }

    [Fact]
    public void WritesNothing()
    {
        using var data = new TempDir();
        using var photos = new TempDir();
        MakeLibrary(data.Path, photos.Path);
        var db = data.Combine(ApertureDatabase.MetadataFileName);
        var before = File.ReadAllBytes(db);
        var stamp = File.GetLastWriteTimeUtc(db);

        LibraryCount.Read(data.Path);

        Assert.Equal(before, File.ReadAllBytes(db));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(db));
    }

    [Fact]
    public void TheFiles_CanBeDeletedRightAfter()
    {
        // What Delete library files does next: nothing of the read may still hold the database open.
        using var data = new TempDir();
        using var photos = new TempDir();
        MakeLibrary(data.Path, photos.Path);
        using var registryDir = new TempDir();
        var registry = new CatalogRegistry(registryDir.Combine("catalogs.json"));
        var other = registry.Add("Other", data.Path);

        Assert.Equal(2, LibraryCount.Read(data.Path));

        var deletion = registry.RemoveAndDeleteFiles(other.Id);
        Assert.Equal(CatalogDeletionOutcome.Deleted, deletion.Outcome);
        Assert.False(File.Exists(data.Combine(ApertureDatabase.MetadataFileName)));
    }
}
