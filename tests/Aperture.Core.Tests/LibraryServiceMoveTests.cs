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
        // …then stops: the file it had come to hadn't begun to move (a move starts only on a token not yet stopped).
        Assert.Equal([MoveOutcome.Stopped, MoveOutcome.Stopped, MoveOutcome.Stopped], moves.Select(m => m.Outcome));
        Assert.All(files, f => Assert.True(File.Exists(f)));
        Assert.Empty(Directory.GetFiles(photos.Combine("to")));
        Assert.NotEqual(caller, callers[0]); // off the caller's thread
        Assert.Single(callers); // once stopped, the move comes to no other file
    }

    [Fact]
    public void Exit_DuringACopy_StopsIt_AndLeavesNoCopy()
    {
        // Review round 1, F3: exit (a pinned library's Dispose; App waits 5 s at most) during a copy across drives.
        using var data = new TempDir();
        using var photos = new TempDir();
        var a = photos.Combine("from", "a.jpg");
        TestImages.Write(a, 32, 24);
        Directory.CreateDirectory(photos.Combine("to"));
        var library = new LibraryService(data.Path);
        using var copying = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        library.ConfigureMoves = moves =>
        {
            moves.ForceCopy = true;
            moves.CopyingChunk = _ =>
            {
                copying.Set();
                release.Wait(Wait);
            };
        };

        var move = library.MoveFilesAsync([a], photos.Combine("to"));
        Assert.True(copying.Wait(Wait));
        library.Dispose();
        release.Set();

        Assert.True(move.Wait(Wait));
        Assert.Equal(MoveOutcome.Stopped, Assert.Single(move.Result).Outcome);
        Assert.True(File.Exists(a));
        Assert.Empty(Directory.GetFiles(photos.Combine("to")));
    }

    [Fact]
    public async Task AFolderPickedInAnotherCase_GetsTheRowInTheDisksCase()
    {
        // Round 2 (dispositions lens), L-2c: MoveFilesAsync keys the moved row as a scan would.
        using var data = new TempDir();
        using var photos = new TempDir();
        TestImages.Write(photos.Combine("a.jpg"), 32, 24);
        Directory.CreateDirectory(photos.Combine("Trips"));
        var library = new LibraryService(data.Path);
        var root = library.AddRoot(photos.Path, "Photos");
        library.IndexRoot(root);
        var id = library.GetUnion().Single().Item.Id;

        var moves = await library.MoveFilesAsync([new MoveRequest(photos.Combine("a.jpg"), root.Id, "a.jpg")],
            photos.Combine("TRIPS"), root.Id);
        var again = library.IndexRoot(root);

        Assert.Equal(MoveOutcome.Moved, Assert.Single(moves).Outcome);
        var row = Assert.Single(library.GetUnion());
        Assert.Equal((id, Path.Combine("Trips", "a.jpg")), (row.Item.Id, row.Item.RelPath));
        Assert.Equal((0, 0, 0), (again.Added, again.Updated, again.Removed)); // nothing read again
        await library.ShutdownAsync();
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
    public void ScanRelDir_NamesTheFoldersAsOnDisk_WhateverCaseTheyWerePickedIn()
    {
        // A moved file's row is keyed as a scan writes it (exactly, by case): a folder picked as "TRIPS\sub" is "Trips\Sub".
        using var photos = new TempDir();
        Directory.CreateDirectory(photos.Combine("Trips", "Sub"));

        Assert.Equal(Path.Combine("Trips", "Sub"), LibraryService.ScanRelDir(photos.Path, photos.Combine("TRIPS", "sub")));
        Assert.Equal("", LibraryService.ScanRelDir(photos.Path, photos.Path));
        Assert.Equal(Path.Combine("Trips", "gone"), LibraryService.ScanRelDir(photos.Path, photos.Combine("trips", "gone"))); // as given, when not there
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
