using Aperture.Core.Library;
using Aperture.Core.Storage;
using Aperture.Core.Tests.Support;

namespace Aperture.Core.Tests;

/// <summary>
/// The window's reads in the background (0.9.1 plan, I5): tracked like index runs, so a library closing for a switch
/// (or Aperture closing) waits for a read in flight, and its files can be deleted straight after.
/// </summary>
public class LibraryServiceReadTests
{
    private static (TempDir Data, TempDir Photos, LibraryService Library, Models.Root Root) Library(int photos = 3)
    {
        var data = new TempDir();
        var lib = new TempDir();
        for (var i = 0; i < photos; i++)
            TestImages.Write(Path.Combine(lib.Path, $"p{i}.jpg"), 32, 24);
        var library = new LibraryService(data.Path);
        var root = library.AddRoot(lib.Path, "L");
        library.IndexRoot(root);
        return (data, lib, library, root);
    }

    [Fact]
    public void TheReads_RunOffTheCallersThread_AndGiveWhatTheSyncOnesGive()
    {
        var (data, photos, library, root) = Library();
        using var _ = data;
        using var __ = photos;
        var caller = Environment.CurrentManagedThreadId;
        int? readOn = null;
        library.ReadingContents = () => readOn = Environment.CurrentManagedThreadId;

        // The caller blocks on each: had the read run on the caller's thread, it would have run here.
        var contents = library.GetContentsAsync().GetAwaiter().GetResult();
        var count = library.CountForRootAsync(root.Id).GetAwaiter().GetResult();

        Assert.NotNull(readOn);
        Assert.NotEqual(caller, readOn);
        library.ReadingContents = null;
        Assert.Equal(library.GetContents().Rows.Count, contents.Rows.Count);
        Assert.Equal(3, count);
        library.ShutdownAsync().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task ShutdownWaitsForReadsInFlight()
    {
        var (data, photos, library, _) = Library();
        using var __ = data;
        using var ___ = photos;
        var reading = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        library.ReadingContents = () =>
        {
            reading.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        };
        var read = library.GetContentsAsync();
        Assert.True(reading.Wait(TimeSpan.FromSeconds(10)));

        var shutdown = library.ShutdownAsync();
        await Task.Delay(200);
        Assert.False(shutdown.IsCompleted); // the database stays open until the read is done with it

        release.Set();
        await shutdown;
        Assert.Equal(3, (await read).Rows.Count);
    }

    [Fact]
    public async Task ALibraryLeftDuringARead_CanBeDeletedRightAfter()
    {
        using var outer = new TempDir();
        using var photos = new TempDir();
        for (var i = 0; i < 3; i++)
            TestImages.Write(Path.Combine(photos.Path, $"p{i}.jpg"), 32, 24);
        var dataDir = Path.Combine(outer.Path, "catalog");
        var library = new LibraryService(dataDir);
        library.IndexRoot(library.AddRoot(photos.Path, "L"));
        var reading = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        library.ReadingContents = () =>
        {
            reading.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        };
        var read = library.GetContentsAsync();
        var count = library.CountForRootAsync(1);
        Assert.True(reading.Wait(TimeSpan.FromSeconds(10)));

        var shutdown = library.ShutdownAsync();
        release.Set();
        await shutdown;

        Directory.Delete(dataDir, recursive: true); // nothing of the reads holds the files
        Assert.False(Directory.Exists(dataDir));
        await Record.ExceptionAsync(() => read);
        await Record.ExceptionAsync(() => count);
    }

    [Fact]
    public async Task AReadAskedForOnceTheLibraryIsClosing_IsRefused()
    {
        var (data, photos, library, root) = Library();
        using var _ = data;
        using var __ = photos;
        await library.ShutdownAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(library.GetContentsAsync);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => library.CountForRootAsync(root.Id));
    }
}
