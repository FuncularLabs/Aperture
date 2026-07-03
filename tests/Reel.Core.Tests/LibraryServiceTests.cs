using Reel.Core.Library;
using Reel.Core.Tests.Support;

namespace Reel.Core.Tests;

public class LibraryServiceTests
{
    [Fact]
    public void GetUnion_ExcludesUnincludedRoots()
    {
        using var dataDir = new TempDir();
        using var libA = new TempDir();
        using var libB = new TempDir();
        TestImages.Write(Path.Combine(libA.Path, "a.jpg"), 200, 200);
        TestImages.Write(Path.Combine(libB.Path, "b.jpg"), 200, 200);

        using var service = new LibraryService(dataDir.Path);
        var rootA = service.AddRoot(libA.Path, "A");
        var rootB = service.AddRoot(libB.Path, "B");
        service.IndexRoot(rootA);
        service.IndexRoot(rootB);

        Assert.Equal(2, service.GetUnion().Count);

        service.SetIncluded(rootB.Id, false);
        var union = service.GetUnion();
        var row = Assert.Single(union);
        Assert.Equal("A", row.RootAlias);
    }

    [Fact]
    public void GetUnion_OrdersNewestFirst_AcrossRoots()
    {
        using var dataDir = new TempDir();
        using var libA = new TempDir();
        using var libB = new TempDir();
        var older = Path.Combine(libA.Path, "older.jpg");
        var newer = Path.Combine(libB.Path, "newer.jpg");
        TestImages.Write(older, 200, 200);
        TestImages.Write(newer, 200, 200);
        File.SetLastWriteTimeUtc(older, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(newer, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        using var service = new LibraryService(dataDir.Path);
        service.IndexRoot(service.AddRoot(libA.Path, "A"));
        service.IndexRoot(service.AddRoot(libB.Path, "B"));

        var union = service.GetUnion();
        Assert.Equal("newer.jpg", union[0].Item.FileName);
        Assert.Equal("older.jpg", union[1].Item.FileName);
    }

    [Fact]
    public void RemoveRoot_PurgesItemsAndThumbnails()
    {
        using var dataDir = new TempDir();
        using var lib = new TempDir();
        TestImages.Write(Path.Combine(lib.Path, "x.jpg"), 200, 200);

        using var service = new LibraryService(dataDir.Path);
        var root = service.AddRoot(lib.Path, "X");
        service.IndexRoot(root);
        Assert.NotEmpty(service.GetUnion());

        service.RemoveRoot(root.Id);

        Assert.Empty(service.GetUnion());
        Assert.Empty(service.GetRoots());
    }
}
