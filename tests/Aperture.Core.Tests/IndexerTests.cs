using Aperture.Core.Models;
using Aperture.Core.Tests.Support;

namespace Aperture.Core.Tests;

public class IndexerTests
{
    [Fact]
    public void IndexRoot_IndexesSupportedImages_AndCachesThreeThumbnailsEach()
    {
        using var scope = new ApertureScope();
        TestImages.Write(scope.Library.Combine("a.jpg"), 800, 600);
        TestImages.Write(scope.Library.Combine("b.png"), 640, 480);
        TestImages.Write(scope.Library.Combine("sub", "c.webp"), 500, 500);
        File.WriteAllText(scope.Library.Combine("notes.txt"), "ignored");

        var root = scope.AddLibraryRoot();
        var result = scope.Indexer.IndexRoot(root);

        Assert.Equal(3, result.Added);
        Assert.Equal(0, result.Failed);
        Assert.Equal(3, scope.Items.CountForRoot(root.Id));
        Assert.Equal(9, scope.Thumbnails.TotalCount()); // 3 items x 3 sizes
    }

    [Fact]
    public void IndexRoot_CapturesDimensions_AndRelativePaths()
    {
        using var scope = new ApertureScope();
        TestImages.Write(scope.Library.Combine("sub", "deep.jpg"), 1024, 768);
        var root = scope.AddLibraryRoot();

        scope.Indexer.IndexRoot(root);

        var item = Assert.Single(scope.Items.GetForRoot(root.Id));
        Assert.Equal(Path.Combine("sub", "deep.jpg"), item.RelPath);
        Assert.Equal("deep.jpg", item.FileName);
        Assert.Equal(".jpg", item.Ext);
        Assert.Equal(1024, item.Width);
        Assert.Equal(768, item.Height);
    }

    [Fact]
    public void IndexRoot_Resume_SkipsUnchanged_WithNoAddsOrThumbnailWork()
    {
        using var scope = new ApertureScope();
        for (var i = 0; i < 5; i++)
            TestImages.Write(scope.Library.Combine($"img{i}.jpg"), 400, 300);
        var root = scope.AddLibraryRoot();

        var first = scope.Indexer.IndexRoot(root);
        Assert.Equal(5, first.Added);

        var second = scope.Indexer.IndexRoot(root);
        Assert.Equal(0, second.Added);
        Assert.Equal(0, second.Updated);
        Assert.Equal(5, second.Skipped);
        Assert.Equal(0, second.ThumbnailsGenerated); // cache hit — no decoding
    }

    [Fact]
    public void IndexRoot_DetectsNewFile_OnReindex()
    {
        using var scope = new ApertureScope();
        TestImages.Write(scope.Library.Combine("first.jpg"), 400, 300);
        var root = scope.AddLibraryRoot();
        scope.Indexer.IndexRoot(root);

        TestImages.Write(scope.Library.Combine("second.jpg"), 400, 300);
        var result = scope.Indexer.IndexRoot(root);

        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(2, scope.Items.CountForRoot(root.Id));
    }

    [Fact]
    public void IndexRoot_PrunesDeletedFile_AndItsThumbnails()
    {
        using var scope = new ApertureScope();
        TestImages.Write(scope.Library.Combine("keep.jpg"), 400, 300);
        var goner = scope.Library.Combine("goner.jpg");
        TestImages.Write(goner, 400, 300);
        var root = scope.AddLibraryRoot();
        scope.Indexer.IndexRoot(root);
        Assert.Equal(6, scope.Thumbnails.TotalCount());

        File.Delete(goner);
        var result = scope.Indexer.IndexRoot(root);

        Assert.Equal(1, result.Removed);
        Assert.Equal(1, scope.Items.CountForRoot(root.Id));
        Assert.Equal(3, scope.Thumbnails.TotalCount()); // orphaned thumbs purged too
    }

    [Fact]
    public void IndexRoot_ReindexesModifiedFile()
    {
        using var scope = new ApertureScope();
        var path = scope.Library.Combine("edit.jpg");
        TestImages.Write(path, 400, 300);
        var root = scope.AddLibraryRoot();
        scope.Indexer.IndexRoot(root);

        // Rewrite with different content + bump the write time so size/mtime differ.
        TestImages.Write(path, 800, 600);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));
        var result = scope.Indexer.IndexRoot(root);

        Assert.Equal(1, result.Updated);
        Assert.Equal(0, result.Added);
        var item = Assert.Single(scope.Items.GetForRoot(root.Id));
        Assert.Equal(800, item.Width);
    }

    [Fact]
    public void IndexRoot_SelfHeals_MissingThumbnails_OnResume()
    {
        using var scope = new ApertureScope();
        TestImages.Write(scope.Library.Combine("p.jpg"), 400, 300);
        var root = scope.AddLibraryRoot();
        scope.Indexer.IndexRoot(root);

        // Simulate a partially-populated cache (e.g. interrupted first run).
        var itemId = scope.Items.GetForRoot(root.Id).Single().Id;
        scope.Thumbnails.DeleteForItems([itemId]);
        Assert.Equal(0, scope.Thumbnails.TotalCount());

        var result = scope.Indexer.IndexRoot(root);

        Assert.Equal(1, result.Skipped);
        Assert.Equal(3, result.ThumbnailsGenerated);
        Assert.Equal(3, scope.Thumbnails.TotalCount());
    }

    [Fact]
    public void Thumbnails_Get_WithheldWhenSourceMtimeMismatches()
    {
        using var scope = new ApertureScope();
        TestImages.Write(scope.Library.Combine("p.jpg"), 400, 300);
        var root = scope.AddLibraryRoot();
        scope.Indexer.IndexRoot(root);
        var item = scope.Items.GetForRoot(root.Id).Single();
        var mtime = item.MTimeUtc.Ticks;

        Assert.NotNull(scope.Thumbnails.Get(item.Id, ThumbSize.Large, mtime));     // fresh → served
        Assert.Null(scope.Thumbnails.Get(item.Id, ThumbSize.Large, mtime + 1));    // stale → withheld, not wrong image
        Assert.NotNull(scope.Thumbnails.Get(item.Id, ThumbSize.Large));            // unguarded call still served
    }

    [Fact]
    public void IndexRoot_RegeneratesThumbnails_WhenCachedSourceMtimeIsStale()
    {
        using var scope = new ApertureScope();
        TestImages.Write(scope.Library.Combine("p.jpg"), 400, 300);
        var root = scope.AddLibraryRoot();
        scope.Indexer.IndexRoot(root);
        var item = scope.Items.GetForRoot(root.Id).Single();

        // Simulate a realigned cache: thumbs exist for this id but were built from a
        // different source (their src mtime no longer matches the item's mtime).
        using (var conn = scope.Database.OpenThumbnails())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE thumbs SET src_mtime_ticks = src_mtime_ticks - 1 WHERE item_id = @id;";
            cmd.Parameters.AddWithValue("@id", item.Id);
            cmd.ExecuteNonQuery();
        }
        Assert.Null(scope.Thumbnails.Get(item.Id, ThumbSize.Large, item.MTimeUtc.Ticks)); // guard withholds stale

        // Re-indexing the unchanged file heals the cache instead of leaving the wrong image.
        var result = scope.Indexer.IndexRoot(root);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(3, result.ThumbnailsGenerated);
        Assert.NotNull(scope.Thumbnails.Get(item.Id, ThumbSize.Large, item.MTimeUtc.Ticks));
    }

    [Fact]
    public void IndexRoot_ReportsProgress_ThroughToDone()
    {
        using var scope = new ApertureScope();
        TestImages.Write(scope.Library.Combine("p.jpg"), 400, 300);
        var root = scope.AddLibraryRoot();

        // Progress<T> posts asynchronously; use a synchronous sink for a deterministic test.
        var phases = new List<IndexPhase>();
        scope.Indexer.IndexRoot(root, new SynchronousProgress(phases));

        Assert.Contains(IndexPhase.Scanning, phases);
        Assert.Contains(IndexPhase.Done, phases);
    }

    private sealed class SynchronousProgress(List<IndexPhase> sink) : IProgress<IndexProgress>
    {
        public void Report(IndexProgress value) => sink.Add(value.Phase);
    }
}
