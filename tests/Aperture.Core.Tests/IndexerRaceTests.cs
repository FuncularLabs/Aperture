using Aperture.Core.Library;
using Aperture.Core.Models;
using Aperture.Core.Tests.Support;

namespace Aperture.Core.Tests;

/// <summary>
/// A run's count and its reads are apart in time (0.9.2 round 2, R2-4 and OOR-1): a file can vanish, be locked, or be
/// moved in or out by Move to… meanwhile, or the folder go away. None of that ends the run, loses a row it shouldn't,
/// or keeps one it shouldn't: each test of round 3 ends on <see cref="AssertTheRowsAreTheFiles"/>.
/// </summary>
public class IndexerRaceTests
{
    [Fact]
    public void AFileGoneBeforeItsRead_IsLeftOut_AndTheRunGoesOn()
    {
        using var scope = new ApertureScope();
        foreach (var name in new[] { "a.jpg", "b.jpg", "c.jpg" })
            TestImages.Write(scope.Library.Combine(name), 32, 24);
        var root = scope.AddLibraryRoot();
        scope.Indexer.AfterScan = () => File.Delete(scope.Library.Combine("b.jpg"));

        var result = scope.Indexer.IndexRoot(root); // it used to end here: File.OpenRead threw for the gone file

        Assert.Equal(["a.jpg", "c.jpg"], scope.Items.GetForRoot(root.Id).Select(i => i.FileName).Order());
        Assert.Equal(2, result.Added);
    }

    [Fact]
    public void AFileGoneMidRead_LosesItsRow_AndTheRunGoesOn()
    {
        // Round 3 (dispositions lens), N-5: the file vanishes as it's read (a read that throws), not before.
        using var scope = new ApertureScope();
        foreach (var name in new[] { "a.jpg", "b.jpg" })
            TestImages.Write(scope.Library.Combine(name), 32, 24);
        var root = scope.AddLibraryRoot();
        scope.Indexer.IndexRoot(root);
        var b = scope.Library.Combine("b.jpg");
        File.SetLastWriteTimeUtc(b, DateTime.UtcNow.AddMinutes(-3)); // to be read again
        scope.Indexer.Reading = rel =>
        {
            if (rel == "b.jpg")
                File.Delete(b); // after the run found it there, before its read
        };

        var result = scope.Indexer.IndexRoot(root);
        scope.Indexer.Reading = null;

        Assert.Equal(1, result.Removed);
        AssertTheRowsAreTheFiles(scope, root);
    }

    [Fact]
    public void AVideoGoneBeforeItsRead_GetsNoRow()
    {
        // A video's reads don't throw for a missing file (the shell's fallback answers), so the run checks first.
        using var scope = new ApertureScope();
        TestImages.Write(scope.Library.Combine("a.jpg"), 32, 24);
        File.WriteAllText(scope.Library.Combine("v.mp4"), "a video");
        var root = scope.AddLibraryRoot();
        scope.Indexer.AfterScan = () => File.Delete(scope.Library.Combine("v.mp4"));

        scope.Indexer.IndexRoot(root);

        Assert.Equal(["a.jpg"], scope.Items.GetForRoot(root.Id).Select(i => i.FileName));
    }

    [Fact]
    public void AFileLockedAsItsRead_KeepsItsRow_AndTheRunGoesOn()
    {
        using var scope = new ApertureScope();
        foreach (var name in new[] { "a.jpg", "b.jpg" })
            TestImages.Write(scope.Library.Combine(name), 32, 24);
        var root = scope.AddLibraryRoot();
        scope.Indexer.IndexRoot(root);
        var b = scope.Library.Combine("b.jpg");
        File.SetLastWriteTimeUtc(b, File.GetLastWriteTimeUtc(b).AddMinutes(1)); // changed: to be read again
        TestImages.Write(scope.Library.Combine("c.jpg"), 32, 24);

        IndexResult result;
        using (new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.None))
            result = scope.Indexer.IndexRoot(root);

        Assert.Equal(["a.jpg", "b.jpg", "c.jpg"], scope.Items.GetForRoot(root.Id).Select(i => i.FileName).Order());
        Assert.Equal((1, 1), (result.Added, result.Failed)); // c added; b kept as it was, its read failed
    }

    [Fact]
    public void AFileMovedInAfterItsFolderWasCounted_KeepsItsRow()
    {
        // R2-4, destination side: the move's row is in the run's rows, but the file wasn't on disk when it counted.
        using var scope = new ApertureScope();
        var tags = new Storage.AnnotationStore(scope.Database);
        TestImages.Write(scope.Library.Combine("A", "a.jpg"), 32, 24);
        TestImages.Write(scope.Library.Combine("B", "b.jpg"), 32, 24);
        var rootA = scope.Roots.Add(new Models.Root { Path = scope.Library.Combine("A"), Alias = "A", AddedUtc = DateTime.UtcNow });
        var rootB = scope.Roots.Add(new Models.Root { Path = scope.Library.Combine("B"), Alias = "B", AddedUtc = DateTime.UtcNow });
        scope.Indexer.IndexRoot(rootA);
        scope.Indexer.IndexRoot(rootB);
        var moves = new FileMoves(scope.Database);
        scope.Indexer.AfterScan = () =>
        {
            scope.Indexer.AfterScan = null;
            Assert.Equal(MoveOutcome.Moved,
                moves.Move(scope.Library.Combine("B", "b.jpg"), scope.Library.Combine("A"), default, new ItemMove(rootB.Id, "b.jpg", rootA.Id, "b.jpg")).Outcome);
        };

        var result = scope.Indexer.IndexRoot(rootA);

        Assert.Equal(0, result.Removed);
        Assert.Equal(["a.jpg", "b.jpg"], scope.Items.GetForRoot(rootA.Id).Select(i => i.FileName).Order());
    }

    [Fact]
    public void AFileMovedOutAfterItsFolderWasCounted_EndsNoRun_AndKeepsItsNewRow()
    {
        // R2-4, source side: the file was counted, its row moved away with it; the run doesn't read a gone file.
        using var scope = new ApertureScope();
        TestImages.Write(scope.Library.Combine("A", "a.jpg"), 32, 24);
        Directory.CreateDirectory(scope.Library.Combine("B"));
        var rootA = scope.Roots.Add(new Models.Root { Path = scope.Library.Combine("A"), Alias = "A", AddedUtc = DateTime.UtcNow });
        var rootB = scope.Roots.Add(new Models.Root { Path = scope.Library.Combine("B"), Alias = "B", AddedUtc = DateTime.UtcNow });
        scope.Indexer.IndexRoot(rootA);
        var moves = new FileMoves(scope.Database);
        scope.Indexer.AfterScan = () =>
        {
            scope.Indexer.AfterScan = null;
            moves.Move(scope.Library.Combine("A", "a.jpg"), scope.Library.Combine("B"), default, new ItemMove(rootA.Id, "a.jpg", rootB.Id, "a.jpg"));
        };

        scope.Indexer.IndexRoot(rootA); // no exception

        Assert.Empty(scope.Items.GetForRoot(rootA.Id));
        Assert.Equal(["a.jpg"], scope.Items.GetForRoot(rootB.Id).Select(i => i.FileName));
    }

    [Fact]
    public void WhatsOutOfScope_IsStillPruned_ThoughItsOnDisk()
    {
        // The prune's check that a file is gone mustn't keep rows a run leaves out on purpose.
        using var scope = new ApertureScope();
        TestImages.Write(scope.Library.Combine("top.jpg"), 32, 24);
        TestImages.Write(scope.Library.Combine("sub", "deep.jpg"), 32, 24);
        File.WriteAllText(scope.Library.Combine("clip.mp4"), "a video");
        var root = scope.AddLibraryRoot();
        scope.Indexer.IndexRoot(root);
        Assert.Equal(3, scope.Items.GetForRoot(root.Id).Count);

        root.Recursive = false;
        var result = scope.Indexer.IndexRoot(root, includeVideos: false);

        Assert.Equal(["top.jpg"], scope.Items.GetForRoot(root.Id).Select(i => i.FileName));
        Assert.Equal(2, result.Removed);
    }

    // --- Round 3: the rows are the files (review round 3, M-2, M-3, L-1; C0). Each test ends on the same rule, so a
    // fix for one case that breaks another fails here: one row per file a scan finds now, in its case, and a second run
    // changes nothing.

    private static void AssertTheRowsAreTheFiles(ApertureScope scope, Root root)
    {
        var files = Indexing.FileScanner.Scan(root.Path, recursive: root.Recursive).Select(f => f.RelPath).Order(StringComparer.Ordinal);
        var rows = scope.Items.GetForRoot(root.Id).Select(i => i.RelPath).Order(StringComparer.Ordinal);
        Assert.Equal(files, rows);
        var again = scope.Indexer.IndexRoot(root);
        Assert.Equal((0, 0, 0), (again.Added, again.Updated, again.Removed));
    }

    [Fact]
    public void ACaseOnlyRename_LeavesOneRow()
    {
        // M-2: the prune's "its file is there after all" found the old name's file, whatever its case: two rows for good.
        using var scope = new ApertureScope();
        TestImages.Write(scope.Library.Combine("IMG_1.JPG"), 32, 24);
        var root = scope.AddLibraryRoot();
        scope.Indexer.IndexRoot(root);

        File.Move(scope.Library.Combine("IMG_1.JPG"), scope.Library.Combine("img_1.jpg"));
        scope.Indexer.IndexRoot(root);

        Assert.Equal(["img_1.jpg"], scope.Items.GetForRoot(root.Id).Select(i => i.RelPath));
        AssertTheRowsAreTheFiles(scope, root);
    }

    [Fact]
    public void AFolderRenamedOnlyInCase_LeavesOneRowEach()
    {
        using var scope = new ApertureScope();
        TestImages.Write(scope.Library.Combine("trips", "a.jpg"), 32, 24);
        TestImages.Write(scope.Library.Combine("trips", "b.jpg"), 32, 24);
        var root = scope.AddLibraryRoot();
        scope.Indexer.IndexRoot(root);

        Directory.Move(scope.Library.Combine("trips"), scope.Library.Combine("trips-"));
        Directory.Move(scope.Library.Combine("trips-"), scope.Library.Combine("Trips"));
        scope.Indexer.IndexRoot(root);

        AssertTheRowsAreTheFiles(scope, root);
    }

    [Fact]
    public void AFolderGoneMidRun_KeepsItsRows()
    {
        // M-3: a drive unplugged or a share dropped mid-run made every later file "gone", and the prune took their rows.
        using var scope = new ApertureScope();
        foreach (var name in new[] { "a.jpg", "b.jpg", "c.jpg" })
            TestImages.Write(scope.Library.Combine("Photos", name), 32, 24);
        var root = scope.Roots.Add(new Root { Path = scope.Library.Combine("Photos"), Alias = "Photos", AddedUtc = DateTime.UtcNow });
        scope.Indexer.IndexRoot(root);
        foreach (var name in new[] { "a.jpg", "b.jpg", "c.jpg" }) // all changed: each to be read again
            File.SetLastWriteTimeUtc(scope.Library.Combine("Photos", name), DateTime.UtcNow.AddMinutes(-3));
        scope.Indexer.Reading = _ =>
        {
            scope.Indexer.Reading = null;
            Directory.Move(scope.Library.Combine("Photos"), scope.Library.Combine("Photos-away")); // as the first is read
        };

        var result = scope.Indexer.IndexRoot(root);

        Assert.Equal(0, result.Removed);
        Assert.Equal(3, scope.Items.GetForRoot(root.Id).Count);
        Directory.Move(scope.Library.Combine("Photos-away"), scope.Library.Combine("Photos")); // back
        scope.Indexer.IndexRoot(root);
        AssertTheRowsAreTheFiles(scope, root);
    }

    [Fact]
    public void AFolderMissingWhenItsRunStarts_KeepsItsRows()
    {
        // C0 (from before 0.9.2): a USB drive unplugged at launch; the count came back empty and the prune took every row.
        using var scope = new ApertureScope();
        foreach (var name in new[] { "a.jpg", "b.jpg" })
            TestImages.Write(scope.Library.Combine("USB", name), 32, 24);
        var root = scope.Roots.Add(new Root { Path = scope.Library.Combine("USB"), Alias = "USB", AddedUtc = DateTime.UtcNow });
        scope.Indexer.IndexRoot(root);
        var ids = scope.Items.GetForRoot(root.Id).Select(i => i.Id).Order().ToList();
        Directory.Move(scope.Library.Combine("USB"), scope.Library.Combine("USB-unplugged"));

        var result = scope.Indexer.IndexRoot(root);

        Assert.Equal(0, result.Removed);
        Assert.Equal(ids, scope.Items.GetForRoot(root.Id).Select(i => i.Id).Order());
        Directory.Move(scope.Library.Combine("USB-unplugged"), scope.Library.Combine("USB"));
        AssertTheRowsAreTheFiles(scope, root);
    }

    [Fact]
    public void AMoveLandingDuringTheRun_KeepsTheRowItTook()
    {
        // L-1: the run's rows were read before the move; the file it was to read was gone, and the prune deleted its row
        // by id — the row the move had just taken to the other folder. The window is the online-only question: the run
        // has counted and read its rows, and holds no write yet (inside a batch, the move's row write waits instead).
        using var scope = new ApertureScope();
        TestImages.Write(scope.Library.Combine("A", "a.jpg"), 32, 24);
        TestImages.Write(scope.Library.Combine("A", "b.jpg"), 32, 24);
        Directory.CreateDirectory(scope.Library.Combine("B"));
        var rootA = scope.Roots.Add(new Root { Path = scope.Library.Combine("A"), Alias = "A", AddedUtc = DateTime.UtcNow });
        var rootB = scope.Roots.Add(new Root { Path = scope.Library.Combine("B"), Alias = "B", AddedUtc = DateTime.UtcNow });
        scope.Indexer.IndexRoot(rootA);
        var b = scope.Library.Combine("A", "b.jpg");
        File.SetLastWriteTimeUtc(b, DateTime.UtcNow.AddMinutes(-3)); // b is to be read again
        var cloud = scope.Library.Combine("A", "c.jpg");
        TestImages.Write(cloud, 32, 24);
        File.SetAttributes(cloud, File.GetAttributes(cloud) | FileAttributes.Offline); // so the run asks
        var moved = scope.Items.GetForRoot(rootA.Id).Single(i => i.FileName == "b.jpg").Id;
        var moves = new FileMoves(scope.Database);
        scope.Indexer.RescanAfter = TimeSpan.FromMinutes(5); // answered at once: no second count

        scope.Indexer.IndexRoot(rootA, null, default, true, (_, _) =>
        {
            Assert.Equal(MoveOutcome.Moved, moves.Move(b, scope.Library.Combine("B"), default,
                new ItemMove(rootA.Id, "b.jpg", rootB.Id, "b.jpg")).Outcome); // while the question waits
            return Indexing.OnlineOnlyChoice.Now;
        });

        Assert.Equal(["a.jpg", "c.jpg"], scope.Items.GetForRoot(rootA.Id).Select(i => i.RelPath).Order());
        Assert.Equal(moved, Assert.Single(scope.Items.GetForRoot(rootB.Id)).Id);
        AssertTheRowsAreTheFiles(scope, rootA);
        Assert.Equal(1, scope.Indexer.IndexRoot(rootB).Updated); // b had changed before it moved: its row is brought up to date
        Assert.Equal(moved, Assert.Single(scope.Items.GetForRoot(rootB.Id)).Id);
        AssertTheRowsAreTheFiles(scope, rootB);
    }
}
