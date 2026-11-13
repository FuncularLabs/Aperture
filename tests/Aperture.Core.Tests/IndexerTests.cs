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
    public void IndexRoot_NonRecursiveRoot_IndexesOnlyTheRootFolder()
    {
        using var scope = new ApertureScope();
        TestImages.Write(scope.Library.Combine("top.jpg"), 400, 300);
        TestImages.Write(scope.Library.Combine("sub", "deep.jpg"), 400, 300);

        var root = scope.AddLibraryRoot();
        root.Recursive = false;

        var result = scope.Indexer.IndexRoot(root);

        Assert.Equal(1, result.Added);
        var item = Assert.Single(scope.Items.GetForRoot(root.Id));
        Assert.Equal("top.jpg", item.RelPath);
    }

    /// <summary>
    /// Turning subfolder indexing off on an existing root drops the now-out-of-scope items: they are
    /// no longer scanned, so the prune step removes them. This is what makes the toggle safe to expose
    /// on a root that was already indexed recursively.
    /// </summary>
    [Fact]
    public void IndexRoot_TurningOffRecursion_PrunesSubfolderItemsOnReindex()
    {
        using var scope = new ApertureScope();
        TestImages.Write(scope.Library.Combine("top.jpg"), 400, 300);
        TestImages.Write(scope.Library.Combine("sub", "deep.jpg"), 400, 300);

        var root = scope.AddLibraryRoot();
        scope.Indexer.IndexRoot(root);
        Assert.Equal(2, scope.Items.CountForRoot(root.Id));

        root.Recursive = false;
        var result = scope.Indexer.IndexRoot(root);

        Assert.Equal(1, result.Removed);
        var item = Assert.Single(scope.Items.GetForRoot(root.Id));
        Assert.Equal("top.jpg", item.RelPath);
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

    [Fact]
    public void Scanning_ReportsTheCount_ThenIndexingCarriesTheTotal_AndTheOnlineOnlyFiles()
    {
        // 0.9.1 batch, item 4: count the files first, and show the count climbing while they're counted.
        using var scope = new ApertureScope();
        for (var i = 0; i < 300; i++)
            File.WriteAllText(scope.Library.Combine($"f{i}.jpg"), "x"); // not decodable: quick to "index" (a failure)
        var cloud = scope.Library.Combine("f0.jpg");
        File.SetAttributes(cloud, File.GetAttributes(cloud) | FileAttributes.Offline);
        var root = scope.AddLibraryRoot();

        var reports = new List<IndexProgress>();
        scope.Indexer.IndexRoot(root, new SynchronousSink(reports));

        // The start, every 250 found, the total. How many are online-only is said once the run knows which it will read.
        var scanning = reports.Where(r => r.Phase == IndexPhase.Scanning).Select(r => (r.Processed, r.OnlineOnly));
        Assert.Equal([(0, 0), (250, 0), (300, 0)], scanning);
        var indexing = reports.Where(r => r.Phase == IndexPhase.Indexing).ToList();
        Assert.NotEmpty(indexing);
        Assert.All(indexing, r => Assert.Equal((300, 1), (r.Total, r.OnlineOnly)));
        var done = Assert.Single(reports, r => r.Phase == IndexPhase.Done);
        Assert.Equal((300, 300, 1), (done.Processed, done.Total, done.OnlineOnly));
        Assert.Equal(IndexPhase.Scanning, reports[0].Phase);
    }

    [Fact]
    public void OnlineOnly_CountsTheFilesItWillRead_AndNotTheOnesItSkips()
    {
        // Each online-only file a run reads is downloaded to read it; one it skips (unchanged, thumbnails cached) isn't.
        using var scope = new ApertureScope();
        foreach (var name in new[] { "here.jpg", "cloud1.jpg", "cloud2.jpg" })
            TestImages.Write(scope.Library.Combine(name), 64, 48);
        foreach (var name in new[] { "cloud1.jpg", "cloud2.jpg" })
            MakeOnlineOnly(scope.Library.Combine(name));
        var root = scope.AddLibraryRoot();

        Assert.Equal(2, OnlineOnlyReported(scope, root));        // a first run reads every file
        Assert.Equal(0, OnlineOnlyReported(scope, root));        // nothing changed: nothing is read

        var touched = scope.Library.Combine("cloud2.jpg");
        File.SetLastWriteTimeUtc(touched, File.GetLastWriteTimeUtc(touched).AddMinutes(1));
        Assert.Equal(1, OnlineOnlyReported(scope, root));        // the one that changed

        scope.Thumbnails.DeleteForItems([.. scope.Items.GetForRoot(root.Id).Where(i => i.FileName == "cloud1.jpg").Select(i => i.Id)]);
        Assert.Equal(1, OnlineOnlyReported(scope, root));        // unchanged, but its thumbnails are gone: read again
    }

    [Fact]
    public void AFolderUnderABatch_SaysHowFarItIs_FromTheStart()
    {
        // Review R1-1: a folder of fewer than 200 files (and the first 200 of any) went from "counting" straight to
        // done, and said how many were online-only only once they were downloaded.
        using var scope = new ApertureScope();
        for (var i = 0; i < 150; i++)
            File.WriteAllText(scope.Library.Combine($"f{i}.jpg"), "x");
        MakeOnlineOnly(scope.Library.Combine("f3.jpg"));
        var root = scope.AddLibraryRoot();

        var reports = new List<IndexProgress>();
        scope.Indexer.IndexRoot(root, new SynchronousSink(reports));

        var first = reports.First(r => r.Phase == IndexPhase.Indexing); // straight after the count, before any file
        Assert.Equal((0, 150, 1, false), (first.Processed, first.Total, first.OnlineOnly, first.Saved));
        Assert.True(reports.IndexOf(first) < reports.FindIndex(r => r.Phase == IndexPhase.Pruning));
        Assert.DoesNotContain(reports, r => r.Phase == IndexPhase.Indexing && r.Saved); // no batch saved under 200
    }

    [Fact]
    public void BetweenBatches_ItReportsAsTheIntervalAllows_AndSaysWhenABatchIsSaved()
    {
        using var scope = new ApertureScope();
        for (var i = 0; i < 230; i++)
            File.WriteAllText(scope.Library.Combine($"f{i}.jpg"), "x");
        var root = scope.AddLibraryRoot();
        scope.Indexer.ProgressInterval = TimeSpan.Zero; // a report for every file

        var reports = new List<IndexProgress>();
        scope.Indexer.IndexRoot(root, new SynchronousSink(reports));

        var indexing = reports.Where(r => r.Phase == IndexPhase.Indexing).ToList();
        Assert.Equal(Enumerable.Range(0, 231), indexing.Select(r => r.Processed)); // 0, then each file
        Assert.Equal([200], indexing.Where(r => r.Saved).Select(r => r.Processed)); // the one batch saved in the loop
    }

    [Fact]
    public void TheIntervalBetweenReports_IsAQuarterOfASecond_ByDefault()
    {
        using var scope = new ApertureScope();
        Assert.Equal(TimeSpan.FromMilliseconds(250), scope.Indexer.ProgressInterval);
    }

    private static void MakeOnlineOnly(string path) => File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Offline);

    // What a run's indexing reports (and its last one) say about online-only files; every report after the scan agrees.
    private static int OnlineOnlyReported(ApertureScope scope, Root root)
    {
        var reports = new List<IndexProgress>();
        scope.Indexer.IndexRoot(root, new SynchronousSink(reports));
        var afterScan = reports.Where(r => r.Phase != IndexPhase.Scanning).Select(r => r.OnlineOnly).Distinct().ToList();
        return Assert.Single(afterScan);
    }

    private sealed class SynchronousProgress(List<IndexPhase> sink) : IProgress<IndexProgress>
    {
        public void Report(IndexProgress value) => sink.Add(value.Phase);
    }

    private sealed class SynchronousSink(List<IndexProgress> sink) : IProgress<IndexProgress>
    {
        public void Report(IndexProgress value) => sink.Add(value);
    }
}
