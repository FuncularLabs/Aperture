using Microsoft.Data.Sqlite;
using Aperture.Core.Models;

namespace Aperture.Core.Storage;

/// <summary>Read/maintenance access to the thumbnail cache. Bulk writes live in the indexer.</summary>
public sealed class ThumbnailStore(ApertureDatabase database)
{
    private readonly ApertureDatabase _db = database;

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
