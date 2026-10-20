using Aperture.Core.Storage;
using Aperture.Core.Tests.Support;
using Microsoft.Data.Sqlite;

namespace Aperture.Core.Tests;

/// <summary>Thumbnail cache size cap + LRU eviction (the cap setting used to be unenforced).</summary>
public class ThumbnailCacheTests
{
    [Fact]
    public void EffectiveCapBytes_UsesConfiguredFloor_ForSmallLibraries()
    {
        // 100 items * 48 KB = 4.8 MB, well under the 2048 MB configured floor → floor wins.
        long cap = ThumbnailStore.EffectiveCapBytes(configuredCapMb: 2048, itemCount: 100);
        Assert.Equal(2048L * 1024 * 1024, cap);
    }

    [Fact]
    public void EffectiveCapBytes_ScalesWithItemCount_ForLargeLibraries()
    {
        // 100k items would need ~4.6 GB, above the 2 GB floor → auto-sizes up so a legit library
        // isn't capped below its own working set (which would thrash: evict then regenerate).
        long cap = ThumbnailStore.EffectiveCapBytes(configuredCapMb: 2048, itemCount: 100_000);
        Assert.Equal(100_000L * ThumbnailStore.PerItemBudgetBytes, cap);
        Assert.True(cap > 2048L * 1024 * 1024);
    }

    [Fact]
    public void Prune_NoOp_WhenUnderCap()
    {
        using var scope = new ApertureScope();
        for (int i = 1; i <= 3; i++) InsertThumb(scope.Database, i, 1_000_000, lastUsed: i);

        var result = scope.Thumbnails.Prune(capBytes: 6_000_000, lowWaterBytes: 5_000_000);

        Assert.Equal(0, result.ItemsEvicted);
        Assert.Equal(3_000_000, scope.Thumbnails.TotalBytes());
    }

    [Fact]
    public void Prune_EvictsLeastRecentlyUsed_DownToLowWater()
    {
        using var scope = new ApertureScope();
        // 10 items * 1 MB = 10 MB; item i was last used at tick i (1 = oldest).
        for (int i = 1; i <= 10; i++) InsertThumb(scope.Database, i, 1_000_000, lastUsed: i);

        var result = scope.Thumbnails.Prune(capBytes: 6_000_000, lowWaterBytes: 5_000_000);

        // Over cap (10 MB > 6 MB) → evict oldest until <= low-water (5 MB): items 1..5 go, 6..10 stay.
        Assert.Equal(5, result.ItemsEvicted);
        Assert.Equal(5_000_000, scope.Thumbnails.TotalBytes());
        Assert.Null(scope.Thumbnails.Get(1, Aperture.Core.Models.ThumbSize.Large));
        Assert.Null(scope.Thumbnails.Get(5, Aperture.Core.Models.ThumbSize.Large));
        Assert.NotNull(scope.Thumbnails.Get(6, Aperture.Core.Models.ThumbSize.Large));
        Assert.NotNull(scope.Thumbnails.Get(10, Aperture.Core.Models.ThumbSize.Large));
    }

    private static void InsertThumb(ApertureDatabase db, long itemId, int bytes, long lastUsed)
    {
        using var conn = db.OpenThumbnails();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO thumbs (item_id, size, src_mtime_ticks, width, height, data, bytes, created_ticks, last_used_ticks)
            VALUES (@i, 2, 0, 10, 10, @d, @b, @c, @u);
            """;
        cmd.Parameters.AddWithValue("@i", itemId);
        cmd.Parameters.Add("@d", SqliteType.Blob).Value = new byte[bytes];
        cmd.Parameters.AddWithValue("@b", bytes);
        cmd.Parameters.AddWithValue("@c", lastUsed);
        cmd.Parameters.AddWithValue("@u", lastUsed);
        cmd.ExecuteNonQuery();
    }
}
