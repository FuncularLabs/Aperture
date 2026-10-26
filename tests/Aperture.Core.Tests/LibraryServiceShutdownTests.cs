using Aperture.Core.Library;
using Aperture.Core.Models;
using Aperture.Core.Storage;
using Aperture.Core.Tests.Support;

namespace Aperture.Core.Tests;

/// <summary>
/// Teardown contract for a catalog switch (Option B increment 2). A switch must cancel any in-flight
/// index, wait for it to stop, and release the database files before the next catalog opens. The tests
/// that matter hold an index mid-run (<see cref="BlockingProgress"/>) so they can tell "cancelled and
/// awaited" apart from "it happened to finish first".
/// </summary>
public class LibraryServiceShutdownTests
{
    private static void WriteImages(string dir, int count)
    {
        for (var i = 0; i < count; i++)
            TestImages.Write(Path.Combine(dir, $"img{i:D3}.jpg"), 128, 128);
    }

    [Fact]
    public async Task IndexRootAsync_IndexesLikeTheSyncPath()
    {
        using var dataDir = new TempDir();
        using var lib = new TempDir();
        WriteImages(lib.Path, 3);

        using var service = new LibraryService(dataDir.Path);
        var root = service.AddRoot(lib.Path, "L");

        var result = await service.IndexRootAsync(root);

        Assert.Equal(3, result.Added);
        Assert.Equal(3, service.GetUnion().Count);
    }

    [Fact]
    public async Task ShutdownAsync_CancelsAndWaitsFor_AnIndexHeldMidRun()
    {
        using var dataDir = new TempDir();
        using var lib = new TempDir();
        WriteImages(lib.Path, 3);
        var service = new LibraryService(dataDir.Path);
        var held = new BlockingProgress();
        var indexing = service.IndexRootAsync(service.AddRoot(lib.Path, "L"), held);
        Assert.True(held.Entered.Wait(5000));

        var shutdown = service.ShutdownAsync();
        await Task.Delay(200);
        Assert.False(shutdown.IsCompleted); // it waits for the running index before tearing down

        held.Release();
        await shutdown;

        Assert.True(indexing.IsCompleted);
        Assert.IsAssignableFrom<OperationCanceledException>(await Record.ExceptionAsync(() => indexing)); // cancelled, not finished
    }

    [Fact]
    public async Task IndexRootAsync_DuringTheShutdownWindow_IsRefused()
    {
        using var dataDir = new TempDir();
        using var lib = new TempDir();
        WriteImages(lib.Path, 1);
        var service = new LibraryService(dataDir.Path);
        var root = service.AddRoot(lib.Path, "L");
        var held = new BlockingProgress();
        var indexing = service.IndexRootAsync(root, held);
        Assert.True(held.Entered.Wait(5000));

        var shutdown = service.ShutdownAsync(); // waiting on the held index: not yet torn down
        Assert.False(shutdown.IsCompleted);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.IndexRootAsync(root));

        held.Release();
        await shutdown;
        await Record.ExceptionAsync(() => indexing);
    }

    [Fact]
    public async Task ShutdownAsync_AfterAnIndex_LeavesADatabaseAFreshServiceCanRead()
    {
        using var dataDir = new TempDir();
        using var lib = new TempDir();
        WriteImages(lib.Path, 4);
        var service = new LibraryService(dataDir.Path);
        var root = service.AddRoot(lib.Path, "L");
        await service.IndexRootAsync(root);

        await service.ShutdownAsync();

        using var reopened = new LibraryService(dataDir.Path);
        Assert.Equal(4, reopened.GetUnion().Count);
        Assert.Equal(4, reopened.CountForRoot(root.Id));
    }

    [Fact]
    public async Task ShutdownAsync_ReleasesDatabaseFiles_SoTheCatalogDirCanBeDeleted()
    {
        using var outer = new TempDir();
        using var lib = new TempDir();
        var dataDir = Path.Combine(outer.Path, "catalog");
        WriteImages(lib.Path, 4);

        var service = new LibraryService(dataDir);
        await service.IndexRootAsync(service.AddRoot(lib.Path, "L"));
        await service.ShutdownAsync();

        Directory.Delete(dataDir, recursive: true);
        Assert.False(Directory.Exists(dataDir));
    }

    [Fact]
    public async Task IndexRootAsync_AfterShutdown_Throws()
    {
        using var dataDir = new TempDir();
        using var lib = new TempDir();
        TestImages.Write(Path.Combine(lib.Path, "a.jpg"), 64, 64);

        var service = new LibraryService(dataDir.Path);
        var root = service.AddRoot(lib.Path, "L");
        await service.ShutdownAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.IndexRootAsync(root));
    }

    [Fact]
    public async Task ShutdownAsync_IsIdempotent_AndDisposeAfterIsSafe()
    {
        using var dataDir = new TempDir();
        var service = new LibraryService(dataDir.Path);

        await service.ShutdownAsync();
        await service.ShutdownAsync();
        service.Dispose();
    }

    [Fact]
    public void Dispose_ReleasesDatabaseFiles_SoTheCatalogDirCanBeDeleted()
    {
        using var outer = new TempDir();
        using var lib = new TempDir();
        var dataDir = Path.Combine(outer.Path, "catalog");
        TestImages.Write(Path.Combine(lib.Path, "a.jpg"), 64, 64);

        var service = new LibraryService(dataDir);
        service.IndexRoot(service.AddRoot(lib.Path, "L"));
        service.Dispose();

        Directory.Delete(dataDir, recursive: true);
        Assert.False(Directory.Exists(dataDir));
    }

    // --- Watchers -----------------------------------------------------------------------------

    [Fact]
    public void Watch_TracksOneWatcherPerExistingRoot_AndStopWatchingRemovesIt()
    {
        using var dataDir = new TempDir();
        using var lib = new TempDir();
        using var service = new LibraryService(dataDir.Path);
        var root = service.AddRoot(lib.Path, "L");
        var missing = service.AddRoot(Path.Combine(lib.Path, "not-there"), "Missing");

        service.Watch(root);
        service.Watch(root);    // already watched
        service.Watch(missing); // no folder, nothing to watch
        Assert.Equal(1, service.WatcherCount);

        service.StopWatching(root.Id);
        Assert.Equal(0, service.WatcherCount);
    }

    [Fact]
    public async Task ShutdownAsync_StopsEveryWatcher()
    {
        using var dataDir = new TempDir();
        using var lib = new TempDir();
        var service = new LibraryService(dataDir.Path);
        service.Watch(service.AddRoot(lib.Path, "L"));
        Assert.Equal(1, service.WatcherCount);

        await service.ShutdownAsync();

        Assert.Equal(0, service.WatcherCount);
    }

    [Fact]
    public async Task Watch_AfterShutdown_DoesNotArmANewWatcher()
    {
        using var dataDir = new TempDir();
        using var lib = new TempDir();
        var service = new LibraryService(dataDir.Path);
        var root = service.AddRoot(lib.Path, "L");
        await service.ShutdownAsync();

        service.Watch(root); // a late index completion re-arming its watcher

        Assert.Equal(0, service.WatcherCount);
    }

    // --- Reads that race a switch -------------------------------------------------------------

    [Fact]
    public async Task GetOrCreateThumbnail_AfterShutdown_ReturnsNull_InsteadOfThrowing()
    {
        using var dataDir = new TempDir();
        using var lib = new TempDir();
        TestImages.Write(Path.Combine(lib.Path, "a.jpg"), 64, 64);
        var service = new LibraryService(dataDir.Path);
        await service.IndexRootAsync(service.AddRoot(lib.Path, "L"));
        var row = service.GetUnion().Single();

        await service.ShutdownAsync(); // a catalog switch closed the library while a tile was queued

        Assert.Null(service.GetOrCreateThumbnail(row.Item.Id, row.FullPath, ThumbSize.Large, row.Item.MTimeUtc.Ticks, isVideo: false));
    }

    [Fact]
    public async Task TileThumbnailLoad_QueuedAcrossASwitch_CompletesWithoutThrowing()
    {
        using var dataDir = new TempDir();
        using var lib = new TempDir();
        TestImages.Write(Path.Combine(lib.Path, "a.jpg"), 64, 64);
        var service = new LibraryService(dataDir.Path);
        await service.IndexRootAsync(service.AddRoot(lib.Path, "L"));
        var row = service.GetUnion().Single();
        var thumbnails = new Aperture.App.Services.ThumbnailService(service); // what TileVm.BeginLoad (async void) awaits

        await service.ShutdownAsync();

        var ex = await Record.ExceptionAsync(() =>
            thumbnails.LoadAsync(row.Item.Id, row.FullPath, false, ThumbSize.Large, row.Item.MTimeUtc.Ticks));
        Assert.Null(ex); // an escape here would end the app via the dispatcher's unhandled-exception handler
    }

    // --- "Is this library open in some Aperture window?" ---------------------------------------

    [Fact]
    public async Task IsOpen_IsTrueWhileAnyServiceHasTheFolderOpen_WhateverTheSpelling()
    {
        using var outer = new TempDir();
        var dir = outer.Combine("lib");
        Assert.False(LibraryService.IsOpen(dir));

        var service = new LibraryService(dir);
        Assert.True(LibraryService.IsOpen(dir));
        Assert.True(LibraryService.IsOpen(dir.ToUpperInvariant() + Path.DirectorySeparatorChar));

        await service.ShutdownAsync();
        Assert.False(LibraryService.IsOpen(dir));
    }

    [Fact]
    public void IsOpen_SeesTheSameFolderReachedThroughAJunction()
    {
        using var outer = new TempDir();
        var real = outer.Combine("real");
        Directory.CreateDirectory(real);
        var alias = outer.Combine("alias");
        Junction.Create(alias, real);

        using var open = new LibraryService(real);

        Assert.True(LibraryService.IsOpen(alias)); // a different spelling of the same physical folder
    }

    [Fact]
    public void IsOpen_IsTrueWhileAnotherProgramHoldsTheLibrarysLock()
    {
        using var dir = new TempDir();
        using (new LibraryService(dir.Path)) { }
        using var otherWindow = new FileStream(
            Path.Combine(dir.Path, ApertureDatabase.LockFileName), FileMode.Open, FileAccess.Read, FileShare.Read);

        Assert.True(LibraryService.IsOpen(dir.Path)); // e.g. an elevated Aperture window: same file, same lock
    }

    [Fact]
    public void IsOpen_IsFalseForAFolderThatDoesNotExist()
    {
        using var dir = new TempDir();

        Assert.False(LibraryService.IsOpen(dir.Combine("not-there")));
    }

    [Fact]
    public void TwoServicesCanHaveTheSameLibraryOpen()
    {
        using var dir = new TempDir();
        using var first = new LibraryService(dir.Path);

        using var second = new LibraryService(dir.Path); // two windows on one library: allowed, as before

        Assert.True(LibraryService.IsOpen(dir.Path));
    }

    [Fact]
    public void Constructor_WaitsOutAnotherWindowsMomentaryIsOpenCheck()
    {
        using var dir = new TempDir();
        using (new LibraryService(dir.Path)) { }
        var probe = new FileStream(
            Path.Combine(dir.Path, ApertureDatabase.LockFileName), FileMode.Open, FileAccess.Read, FileShare.None);
        var release = new Thread(() => { Thread.Sleep(50); probe.Dispose(); }); // a dedicated thread: no pool delays
        release.Start();

        using var opened = new LibraryService(dir.Path); // must not fail just because a check was in progress
        release.Join();

        Assert.True(LibraryService.IsOpen(dir.Path));
    }

    [Fact]
    public void OpeningALibraryThatWasDeletedWhileItWaited_FailsInsteadOfRecreatingItEmpty()
    {
        using var dir = new TempDir();
        using (new LibraryService(dir.Path)) { }
        var db = Path.Combine(dir.Path, ApertureDatabase.MetadataFileName);
        var lockFile = Path.Combine(dir.Path, ApertureDatabase.LockFileName);
        var deleting = new FileStream(lockFile,
            FileMode.Open, FileAccess.Read, FileShare.None, 1, FileOptions.DeleteOnClose); // another window's delete…
        File.Delete(db);
        var finishes = new Thread(() => { Thread.Sleep(50); deleting.Dispose(); });         // …finishing while this one waits
        finishes.Start();

        var ex = Assert.ThrowsAny<IOException>(() => new LibraryService(dir.Path, mustExist: true));
        finishes.Join();

        Assert.Contains("no Aperture library", ex.Message); // it got the lock and re-checked; not just "can't open"
        Assert.False(File.Exists(db));                      // not recreated as an empty library
        Assert.False(File.Exists(lockFile));                // and the lock file it made is gone again
    }

    [Fact]
    public void OpeningALibraryThatMustExist_NeverCreatesItsFolder()
    {
        using var outer = new TempDir();
        var gone = outer.Combine("gone");

        Assert.ThrowsAny<IOException>(() => new LibraryService(gone, mustExist: true));

        Assert.False(Directory.Exists(gone));
    }

    [Fact]
    public void OpenState_IsUnknown_WhenTheLockFileCantBeRead()
    {
        using var dir = new TempDir();
        using (new LibraryService(dir.Path)) { }
        var lockFile = Path.Combine(dir.Path, ApertureDatabase.LockFileName);
        Acl.DenyRead(lockFile);
        try
        {
            Assert.Equal(LibraryOpenState.Unknown, LibraryService.OpenState(dir.Path));
            Assert.True(LibraryService.IsOpen(dir.Path)); // the conservative yes/no
        }
        finally
        {
            Acl.Restore(lockFile);
        }
        Assert.Equal(LibraryOpenState.NotOpen, LibraryService.OpenState(dir.Path));
    }

    [Fact]
    public void OpenState_IsNotOpen_ForAFolderThatCantBeReached()
    {
        // A share that isn't there fails with a network error, not "not found" — and nothing has it open from here.
        Assert.Equal(LibraryOpenState.NotOpen, LibraryService.OpenState(@"\\localhost\aperture-no-such-share-7f3e\library"));
    }

    [Fact]
    public void Constructor_WhileTheLibraryIsBeingDeleted_CreatesNoDatabaseFiles()
    {
        using var dir = new TempDir();
        using var deleting = new FileStream(Path.Combine(dir.Path, ApertureDatabase.LockFileName),
            FileMode.OpenOrCreate, FileAccess.Read, FileShare.None, 1, FileOptions.DeleteOnClose);

        Assert.ThrowsAny<IOException>(() => new LibraryService(dir.Path));

        Assert.False(File.Exists(Path.Combine(dir.Path, ApertureDatabase.MetadataFileName))); // the lock comes first
        Assert.False(File.Exists(Path.Combine(dir.Path, ApertureDatabase.ThumbnailFileName)));
    }

    [Fact]
    public void Constructor_WhileTheLibraryIsBeingDeleted_Fails()
    {
        using var dir = new TempDir();
        using (new LibraryService(dir.Path)) { }
        using var deleting = new FileStream(
            Path.Combine(dir.Path, ApertureDatabase.LockFileName), FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.ThrowsAny<IOException>(() => new LibraryService(dir.Path));
    }

    [Fact]
    public void IsOpen_IsFalseAfterDispose_AndAfterAFailedOpen()
    {
        using var dir = new TempDir();
        new LibraryService(dir.Path).Dispose();
        Assert.False(LibraryService.IsOpen(dir.Path));

        var bad = dir.Combine("bad");
        Directory.CreateDirectory(bad);
        File.WriteAllText(Path.Combine(bad, ApertureDatabase.MetadataFileName), new string('x', 512));
        Assert.ThrowsAny<Exception>(() => new LibraryService(bad));
        Assert.False(LibraryService.IsOpen(bad));
    }

    // --- The database ---------------------------------------------------------------------------

    [Fact]
    public void Database_AfterDispose_RefusesUse_InsteadOfReopeningFiles()
    {
        using var dir = new TempDir();
        var db = new ApertureDatabase(dir.Path);
        db.Initialize();

        db.Dispose();

        Assert.Throws<ObjectDisposedException>(() => db.OpenMetadata());
        Assert.Throws<ObjectDisposedException>(() => db.OpenThumbnails());
        Assert.Throws<ObjectDisposedException>(() => db.Provider);
        db.Dispose(); // idempotent
    }

    [Fact]
    public void Constructor_ThatCannotOpenTheDatabase_Throws_AndLetsGoOfTheFiles()
    {
        using var dir = new TempDir();
        var db = Path.Combine(dir.Path, ApertureDatabase.MetadataFileName);
        File.WriteAllText(db, new string('x', 512)); // not a SQLite database

        Assert.ThrowsAny<Exception>(() => new LibraryService(dir.Path));

        File.Delete(db); // nothing is left holding it open
        Assert.False(File.Exists(db));
    }
}
