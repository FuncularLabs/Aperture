using Microsoft.Data.Sqlite;
using Aperture.Core.Models;

namespace Aperture.Core.Storage;

/// <summary>Read/maintenance access to the thumbnail cache. Bulk writes live in the indexer.</summary>
public sealed class ThumbnailStore(ApertureDatabase database)
{
    private readonly ApertureDatabase _db = database;

    /// <summary>Returns the encoded JPEG bytes for a thumbnail, or null if not cached.</summary>
    public byte[]? Get(long itemId, ThumbSize size)
    {
        using var conn = _db.OpenThumbnails();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT data FROM thumbs WHERE item_id = @id AND size = @size;";
        cmd.Parameters.AddWithValue("@id", itemId);
        cmd.Parameters.AddWithValue("@size", (int)size);
        return cmd.ExecuteScalar() as byte[];
    }

    /// <summary>How many cached sizes exist per item id (used to detect incomplete thumbs on resume).</summary>
    public Dictionary<long, int> GetCounts()
    {
        using var conn = _db.OpenThumbnails();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT item_id, COUNT(*) FROM thumbs GROUP BY item_id;";
        using var reader = cmd.ExecuteReader();
        var counts = new Dictionary<long, int>();
        while (reader.Read())
            counts[reader.GetInt64(0)] = reader.GetInt32(1);
        return counts;
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
