using Aperture.Core.Library;
using Aperture.Core.Tests.Support;

namespace Aperture.Core.Tests;

/// <summary>
/// Teardown contract for a catalog switch (Option B increment 2). A switch must cancel any in-flight
/// index, wait for it to settle, and release the database files before the next catalog opens — without
/// leaving a torn write. The indexer commits in batches and rolls back only its in-flight batch on
/// cancel, so the DB is always left consistent; these tests pin the service-level orchestration of that.
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
    public async Task ShutdownAsync_AwaitsInFlightIndex_AndLeavesDbConsistent()
    {
        using var dataDir = new TempDir();
        using var lib = new TempDir();
        WriteImages(lib.Path, 12);

        var service = new LibraryService(dataDir.Path);
        var root = service.AddRoot(lib.Path, "L");

        var indexTask = service.IndexRootAsync(root);
        await service.ShutdownAsync();

        // ShutdownAsync must not return while an index is still running.
        Assert.True(indexTask.IsCompleted);

        // The DB is readable and internally consistent from a fresh service on the same dir, whether the
        // index finished or was cancelled mid-batch (committed batches persist; the in-flight batch rolled
        // back — never a torn row).
        using var reopened = new LibraryService(dataDir.Path);
        var rows = reopened.GetUnion();
        Assert.InRange(rows.Count, 0, 12);
        Assert.Equal(rows.Count, reopened.CountForRoot(root.Id));
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

        // No open handle lingers on aperture.db / thumbs.db (provider disposed + pool cleared), so a
        // catalog switch — or an explicit "delete library files" — can remove the directory.
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
        await service.ShutdownAsync(); // second call is a no-op, not a throw
        service.Dispose();             // disposing after an async shutdown is safe
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
}
