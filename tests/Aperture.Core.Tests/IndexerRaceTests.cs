using Aperture.Core.Library;
using Aperture.Core.Models;
using Aperture.Core.Tests.Support;

namespace Aperture.Core.Tests;

/// <summary>
/// A run's count and its reads are apart in time (0.9.2 round 2, R2-4 and OOR-1): a file can vanish, be locked, or be
/// moved in or out by Move to… meanwhile. None of that ends the run, loses a row it shouldn't, or keeps one it shouldn't.
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
}
