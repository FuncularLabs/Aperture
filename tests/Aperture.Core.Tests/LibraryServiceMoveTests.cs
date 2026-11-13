using Aperture.Core.Library;
using Aperture.Core.Tests.Support;

namespace Aperture.Core.Tests;

/// <summary>
/// Move to… through the library (0.9.2 M3–M6): each file moved in turn, off the caller's thread, tracked so a switch or
/// an exit waits for it, and stopped between files when asked.
/// </summary>
public class LibraryServiceMoveTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task EachFile_IsMoved_AndWhatHappenedIsSaid_WithTheirTags()
    {
        using var data = new TempDir();
        using var photos = new TempDir();
        foreach (var name in new[] { "a.jpg", "b.jpg", "c.jpg" })
            TestImages.Write(photos.Combine("from", name), 32, 24);
        TestImages.Write(photos.Combine("to", "b.jpg"), 32, 24); // b's name is taken there
        var library = new LibraryService(data.Path);
        library.SaveAnnotation(photos.Combine("from", "a.jpg"), ["trip"], "");

        var moves = await library.MoveFilesAsync(
            [photos.Combine("from", "a.jpg"), photos.Combine("from", "b.jpg"), photos.Combine("from", "c.jpg")],
            photos.Combine("to"));

        Assert.Equal([MoveOutcome.Moved, MoveOutcome.NameTaken, MoveOutcome.Moved], moves.Select(m => m.Outcome));
        Assert.Equal(["trip"], library.GetAnnotation(photos.Combine("to", "a.jpg")).Tags);
        Assert.True(File.Exists(photos.Combine("from", "b.jpg")));
        await library.ShutdownAsync();
    }

    [Fact]
    public async Task Shutdown_WaitsForAMove_AndStopsBetweenFiles()
    {
        using var data = new TempDir();
        using var photos = new TempDir();
        string[] files = [photos.Combine("from", "a.jpg"), photos.Combine("from", "b.jpg"), photos.Combine("from", "c.jpg")];
        foreach (var file in files)
            TestImages.Write(file, 32, 24);
        Directory.CreateDirectory(photos.Combine("to"));
        var library = new LibraryService(data.Path);
        var caller = Environment.CurrentManagedThreadId; // blocked below while the first file moves
        using var first = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var callers = new List<int>();
        library.MovingFile = path =>
        {
            callers.Add(Environment.CurrentManagedThreadId);
            if (path == files[0])
            {
                first.Set();
                release.Wait(Wait); // a long copy in progress
            }
        };

        var move = library.MoveFilesAsync(files, photos.Combine("to"));
        Assert.True(first.Wait(Wait));
        var shutdown = library.ShutdownAsync();
        await Task.Delay(300);
        Assert.False(shutdown.IsCompleted); // the switch waits for the file in progress…
        release.Set();
        await shutdown.WaitAsync(Wait);

        var moves = await move.WaitAsync(Wait);
        Assert.Equal([MoveOutcome.Moved, MoveOutcome.Stopped, MoveOutcome.Stopped], moves.Select(m => m.Outcome)); // …then stops
        Assert.True(File.Exists(photos.Combine("to", "a.jpg")));
        Assert.True(File.Exists(files[1]));
        Assert.NotEqual(caller, callers[0]); // off the caller's thread
    }

    [Fact]
    public async Task AMove_AfterShutdown_IsRefused()
    {
        using var data = new TempDir();
        var library = new LibraryService(data.Path);
        await library.ShutdownAsync();

        Assert.Throws<ObjectDisposedException>(() => { _ = library.MoveFilesAsync([@"C:\x.jpg"], @"C:\y"); });
    }

    [Fact]
    public async Task IsWatching_SaysWhetherAFoldersWatched()
    {
        using var data = new TempDir();
        using var photos = new TempDir();
        var library = new LibraryService(data.Path);
        var root = library.AddRoot(photos.Path, "Photos");

        Assert.False(library.IsWatching(root.Id));
        library.Watch(root);
        Assert.True(library.IsWatching(root.Id));
        library.StopWatching(root.Id);
        Assert.False(library.IsWatching(root.Id));
        await library.ShutdownAsync();
    }
}
