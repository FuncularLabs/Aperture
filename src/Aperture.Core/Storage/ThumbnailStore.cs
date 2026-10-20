using Microsoft.Data.Sqlite;
using Aperture.Core.Media;
using Aperture.Core.Models;

namespace Aperture.Core.Storage;

/// <summary>Read/maintenance access to the thumbnail cache. Bulk writes live in the indexer.</summary>
public sealed class ThumbnailStore(ApertureDatabase database)
{
    private readonly ApertureDatabase _db = database;

    /// <summary>
    /// Upserts a freshly-generated thumbnail set for one item (used by on-demand regeneration when a
    /// visible tile's cached thumbnail is missing or stale, so it needn't wait for the background reconcile).
    /// </summary>
    public void Put(long itemId, long srcMtimeTicks, ThumbnailSet set)
    {
        var now = DateTime.UtcNow.Ticks;
        using var conn = _db.OpenThumbnails();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO thumbs (item_id, size, src_mtime_ticks, width, height, data, bytes, created_ticks, last_used_ticks)
            VALUES (@item, @size, @src, @w, @h, @data, @bytes, @created, @used)
            ON CONFLICT(item_id, size) DO UPDATE SET
                src_mtime_ticks = excluded.src_mtime_ticks, width = excluded.width, height = excluded.height,
                data = excluded.data, bytes = excluded.bytes, created_ticks = excluded.created_ticks,
                last_used_ticks = excluded.last_used_ticks;
            """;
        var item = cmd.Parameters.Add("@item", SqliteType.Integer);
        var size = cmd.Parameters.Add("@size", SqliteType.Integer);
        var src = cmd.Parameters.Add("@src", SqliteType.Integer);
        var w = cmd.Parameters.Add("@w", SqliteType.Integer);
        var h = cmd.Parameters.Add("@h", SqliteType.Integer);
        var data = cmd.Parameters.Add("@data", SqliteType.Blob);
        var bytes = cmd.Parameters.Add("@bytes", SqliteType.Integer);
        var created = cmd.Parameters.Add("@created", SqliteType.Integer);
        var used = cmd.Parameters.Add("@used", SqliteType.Integer);
        foreach (var (sz, d) in set.Thumbs)
        {
            item.Value = itemId; size.Value = (int)sz; src.Value = srcMtimeTicks;
            w.Value = d.Width; h.Value = d.Height; data.Value = d.Bytes; bytes.Value = d.Bytes.Length;
            created.Value = now; used.Value = now;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>
    /// Returns the encoded JPEG bytes for a thumbnail, or null if not cached. When
    /// <paramref name="expectedSrcMtimeTicks"/> is supplied, a cached thumbnail is
    /// only returned if it was generated from a source with that exact mtime — so a
    /// stale entry (e.g. after the metadata DB was rebuilt and item ids realigned to
    /// different files) is treated as a miss rather than served for the wrong file.
    /// </summary>
    public byte[]? Get(long itemId, ThumbSize size, long? expectedSrcMtimeTicks = null)
    {
        using var conn = _db.OpenThumbnails();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = expectedSrcMtimeTicks is null
            ? "SELECT data FROM thumbs WHERE item_id = @id AND size = @size;"
            : "SELECT data FROM thumbs WHERE item_id = @id AND size = @size AND src_mtime_ticks = @src;";
        cmd.Parameters.AddWithValue("@id", itemId);
        cmd.Parameters.AddWithValue("@size", (int)size);
        if (expectedSrcMtimeTicks is { } src)
            cmd.Parameters.AddWithValue("@src", src);
        return cmd.ExecuteScalar() as byte[];
    }

    /// <summary>Cached-size count and source mtime for one item id.</summary>
    public readonly record struct ThumbCacheInfo(int Count, long SrcMtimeTicks);

    /// <summary>
    /// Per item id: how many cached sizes exist and the source mtime they were built from
    /// (MIN, so a partially-stale item reads as stale). The indexer uses this to detect both
    /// incomplete thumbs on resume and thumbs whose source no longer matches (regenerate).
    /// </summary>
    public Dictionary<long, ThumbCacheInfo> GetInfo()
    {
        using var conn = _db.OpenThumbnails();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT item_id, COUNT(*), MIN(src_mtime_ticks) FROM thumbs GROUP BY item_id;";
        using var reader = cmd.ExecuteReader();
        var info = new Dictionary<long, ThumbCacheInfo>();
        while (reader.Read())
            info[reader.GetInt64(0)] = new ThumbCacheInfo(reader.GetInt32(1), reader.GetInt64(2));
        return info;
    }

    public int TotalCount()
    {
        using var conn = _db.OpenThumbnails();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM thumbs;";
        return (int)(long)cmd.ExecuteScalar()!;
    }

    /// <summary>Sum of stored thumbnail bytes — the figure <see cref="Prune"/> caps.</summary>
    public long TotalBytes()
    {
        using var conn = _db.OpenThumbnails();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(SUM(bytes), 0) FROM thumbs;";
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>Rough on-disk budget per item across its cached sizes (~3 JPEGs).</summary>
    public const long PerItemBudgetBytes = 48 * 1024;

    /// <summary>
    /// The cache ceiling actually enforced: the configured cap, but never below what the current
    /// library legitimately needs (<paramref name="itemCount"/> × <see cref="PerItemBudgetBytes"/>).
    /// Auto-sizing up keeps a large library from sitting above a too-small cap and thrashing —
    /// evicting a thumbnail only to regenerate it the next time its tile scrolls into view.
    /// </summary>
    public static long EffectiveCapBytes(long configuredCapMb, int itemCount) =>
        Math.Max(configuredCapMb * 1024L * 1024L, (long)itemCount * PerItemBudgetBytes);

    /// <summary>Outcome of a <see cref="Prune"/> pass.</summary>
    public readonly record struct PruneResult(int ItemsEvicted, long BytesFreed);

    /// <summary>
    /// When the total exceeds <paramref name="capBytes"/>, evicts whole items' thumbnails
    /// least-recently-used first (by the most recent use across an item's cached sizes) until the
    /// total is at or below <paramref name="lowWaterBytes"/>. Evicting down to a low-water mark (not
    /// exactly the cap) batches the work instead of churning one item at a time at the boundary.
    /// Evicted items simply regenerate on next view. No-op when already within cap.
    /// </summary>
    public PruneResult Prune(long capBytes, long lowWaterBytes)
    {
        if (TotalBytes() is var total && total <= capBytes)
            return default;

        var toFree = total - lowWaterBytes;
        using var conn = _db.OpenThumbnails();

        var victims = new List<long>();
        long freed = 0;
        using (var pick = conn.CreateCommand())
        {
            pick.CommandText = """
                SELECT item_id, SUM(bytes) AS b
                FROM thumbs
                GROUP BY item_id
                ORDER BY MAX(last_used_ticks) ASC, item_id ASC;
                """;
            using var reader = pick.ExecuteReader();
            while (freed < toFree && reader.Read())
            {
                victims.Add(reader.GetInt64(0));
                freed += reader.GetInt64(1);
            }
        }
        if (victims.Count == 0)
            return default;

        using (var tx = conn.BeginTransaction())
        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM thumbs WHERE item_id = @id;";
            var p = del.Parameters.Add("@id", SqliteType.Integer);
            foreach (var id in victims)
            {
                p.Value = id;
                del.ExecuteNonQuery();
            }
            tx.Commit();
        }
        return new PruneResult(victims.Count, freed);
    }

    /// <summary>Deletes all cached thumbnails for the given item ids (cross-DB cascade helper).</summary>
    public void DeleteForItems(IEnumerable<long> itemIds)
    {
        using var conn = _db.OpenThumbnails();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM thumbs WHERE item_id = @id;";
        var p = cmd.Parameters.Add("@id", SqliteType.Integer);
        foreach (var id in itemIds)
        {
            p.Value = id;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }
}
