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

    /// <summary>Sum of stored thumbnail bytes — the figure LRU eviction caps.</summary>
    public long TotalBytes()
    {
        using var conn = _db.OpenThumbnails();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(SUM(bytes), 0) FROM thumbs;";
        return (long)cmd.ExecuteScalar()!;
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
