using Microsoft.Data.Sqlite;
using Reel.Core.Models;

namespace Reel.Core.Storage;

/// <summary>Read queries over the <c>items</c> table. Bulk writes live in the indexer.</summary>
public sealed class ItemStore(ReelDatabase database)
{
    private readonly ReelDatabase _db = database;

    /// <summary>Lightweight row used to decide whether a file needs re-indexing.</summary>
    public readonly record struct ExistingItem(long Id, long MTimeTicks, long SizeBytes);

    /// <summary>Maps rel_path -> existing row for one root, for incremental diffing.</summary>
    public Dictionary<string, ExistingItem> GetExisting(long rootId)
    {
        using var conn = _db.OpenMetadata();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, rel_path, mtime_ticks, size_bytes FROM items WHERE root_id = @root;";
        cmd.Parameters.AddWithValue("@root", rootId);
        using var reader = cmd.ExecuteReader();
        var map = new Dictionary<string, ExistingItem>(StringComparer.Ordinal);
        while (reader.Read())
        {
            map[reader.GetString(1)] = new ExistingItem(
                reader.GetInt64(0), reader.GetInt64(2), reader.GetInt64(3));
        }
        return map;
    }

    public int CountForRoot(long rootId)
    {
        using var conn = _db.OpenMetadata();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM items WHERE root_id = @root;";
        cmd.Parameters.AddWithValue("@root", rootId);
        return (int)(long)cmd.ExecuteScalar()!;
    }

    /// <summary>All items for a root, newest first. Convenience for tests and M2 wiring.</summary>
    public List<MediaItem> GetForRoot(long rootId)
    {
        using var conn = _db.OpenMetadata();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, root_id, rel_path, file_name, ext, size_bytes, mtime_ticks,
                   taken_ticks, width, height, camera, orientation, indexed_ticks
            FROM items
            WHERE root_id = @root
            ORDER BY COALESCE(taken_ticks, mtime_ticks) DESC;
            """;
        cmd.Parameters.AddWithValue("@root", rootId);
        return Read(cmd);
    }

    private static List<MediaItem> Read(SqliteCommand cmd)
    {
        using var reader = cmd.ExecuteReader();
        var items = new List<MediaItem>();
        while (reader.Read())
        {
            items.Add(new MediaItem
            {
                Id = reader.GetInt64(0),
                RootId = reader.GetInt64(1),
                RelPath = reader.GetString(2),
                FileName = reader.GetString(3),
                Ext = reader.GetString(4),
                SizeBytes = reader.GetInt64(5),
                MTimeUtc = new DateTime(reader.GetInt64(6), DateTimeKind.Utc),
                TakenUtc = reader.IsDBNull(7) ? null : new DateTime(reader.GetInt64(7), DateTimeKind.Unspecified),
                Width = reader.IsDBNull(8) ? null : reader.GetInt32(8),
                Height = reader.IsDBNull(9) ? null : reader.GetInt32(9),
                Camera = reader.IsDBNull(10) ? null : reader.GetString(10),
                Orientation = reader.IsDBNull(11) ? null : reader.GetInt32(11),
                IndexedUtc = new DateTime(reader.GetInt64(12), DateTimeKind.Utc),
            });
        }
        return items;
    }
}
