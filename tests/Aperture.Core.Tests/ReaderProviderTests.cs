using Aperture.Core.Library;
using Aperture.Core.Storage;
using Aperture.Core.Tests.Support;

namespace Aperture.Core.Tests;

/// <summary>
/// Reads off the UI thread never join its changes (0.9.1 plan, I5). The library's FunkyORM provider keeps one
/// transaction, and an operation that starts on any thread while it's open runs on its connection: the startup read
/// once met a folder's removal that way and ended Aperture (multiple-libraries plan, revisions 25–26). The reads the
/// window now makes in the background (its rows, a folder's count, the cache's size) go through a provider of their
/// own, which never begins a transaction.
/// </summary>
public class ReaderProviderTests
{
    private sealed class Fixture : IDisposable
    {
        public TempDir Data { get; } = new();
        public TempDir Photos { get; } = new();
        public LibraryService Library { get; }
        public Models.Root Root { get; }

        public Fixture()
        {
            foreach (var file in new[] { "a.jpg", @"sub\b.jpg", @"sub\c.jpg" })
                TestImages.Write(Path.Combine(Photos.Path, file), 32, 24);
            Library = new LibraryService(Data.Path);
            Root = Library.AddRoot(Photos.Path, "Photos");
            Library.IndexRoot(Root);
        }

        public void Dispose()
        {
            Library.ShutdownAsync().GetAwaiter().GetResult();
            Photos.Dispose();
            Data.Dispose();
        }
    }

    [Fact]
    public void AReadDuringAUiTransaction_SeesOnlyWhatIsCommitted()
    {
        using var f = new Fixture();
        var shared = f.Library.Database.Provider;

        shared.BeginTransaction(); // the UI thread's change, under way: it hides "sub", not yet committed
        try
        {
            shared.Insert<HiddenFolderRow, long>(new HiddenFolderRow { RootId = f.Root.Id, RelDir = "sub" });

            var contents = Task.Run(f.Library.GetContents).GetAwaiter().GetResult();   // the window's background read
            var count = Task.Run(() => f.Library.CountForRoot(f.Root.Id)).GetAwaiter().GetResult();

            Assert.Equal(3, contents.Rows.Count); // what's committed: nothing hidden
            Assert.Empty(contents.Hidden);
            Assert.Equal(3, count);
        }
        finally
        {
            shared.RollbackTransaction();
        }
    }

    [Fact]
    public void TheFolderList_ReadOffTheUiThread_SeesOnlyWhatIsCommitted()
    {
        // The watcher reads the folder list on its timer's thread when a folder's files change.
        using var f = new Fixture();
        var shared = f.Library.Database.Provider;

        shared.BeginTransaction(); // the UI thread removing the folder, not yet committed
        try
        {
            shared.Delete<RootRow>(f.Root.Id);

            var roots = Task.Run(f.Library.GetRoots).GetAwaiter().GetResult();

            Assert.Equal([f.Root.Id], roots.Select(r => r.Id));
        }
        finally
        {
            shared.RollbackTransaction();
        }
    }

    [Fact]
    public void ATagSave_WhileABackgroundReadRuns_Succeeds_AndSoDoesTheRead()
    {
        using var f = new Fixture();
        var reading = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        f.Library.ReadingContents = () =>
        {
            reading.Set();
            release.Wait(TimeSpan.FromSeconds(10)); // the read is under way when the save begins
        };
        var read = f.Library.GetContentsAsync();
        Assert.True(reading.Wait(TimeSpan.FromSeconds(10)));

        var photo = Path.Combine(f.Photos.Path, "a.jpg");
        f.Library.SaveAnnotation(photo, ["beach"], "a note"); // a transaction on the shared provider
        f.Library.SaveAnnotation(photo, [], "");             // and its delete, another
        release.Set();

        Assert.Equal(3, read.GetAwaiter().GetResult().Rows.Count);
        Assert.Empty(f.Library.GetAnnotation(photo).Tags);
    }

    [Fact]
    public void TheReadProvider_IsNotTheSharedOne_AndGoesWithTheDatabase()
    {
        using var data = new TempDir();
        var db = new ApertureDatabase(data.Path);
        db.Initialize();

        Assert.NotSame(db.Provider, db.ReadProvider);
        Assert.Same(db.ReadProvider, db.ReadProvider);
        db.Dispose();
        Assert.Throws<ObjectDisposedException>(() => db.ReadProvider);
    }
}
