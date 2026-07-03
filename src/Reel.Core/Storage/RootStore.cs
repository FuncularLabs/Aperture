using Microsoft.Data.Sqlite;
using Reel.Core.Models;

namespace Reel.Core.Storage;

/// <summary>CRUD for the <c>roots</c> table.</summary>
public sealed class RootStore(ReelDatabase database)
{
    private readonly ReelDatabase _db = database;

    /// <summary>Inserts a root and returns it with its assigned id.</summary>
    public Root Add(Root root)
    {
        using var conn = _db.OpenMetadata();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO roots (path, alias, included, color_tag, added_ticks)
            VALUES (@path, @alias, @included, @color, @added)
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("@path", root.Path);
        cmd.Parameters.AddWithValue("@alias", root.Alias);
        cmd.Parameters.AddWithValue("@included", root.Included ? 1 : 0);
        cmd.Parameters.AddWithValue("@color", (object?)root.ColorTag ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@added", root.AddedUtc.ToUniversalTime().Ticks);
        root.Id = (long)cmd.ExecuteScalar()!;
        return root;
    }

    public List<Root> GetAll()
    {
        using var conn = _db.OpenMetadata();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, path, alias, included, color_tag, added_ticks
            FROM roots
            ORDER BY alias COLLATE NOCASE;
            """;
        using var reader = cmd.ExecuteReader();
        var roots = new List<Root>();
        while (reader.Read())
        {
            roots.Add(new Root
            {
                Id = reader.GetInt64(0),
                Path = reader.GetString(1),
                Alias = reader.GetString(2),
                Included = reader.GetInt64(3) != 0,
                ColorTag = reader.IsDBNull(4) ? null : reader.GetString(4),
                AddedUtc = new DateTime(reader.GetInt64(5), DateTimeKind.Utc),
            });
        }
        return roots;
    }

    public void SetIncluded(long rootId, bool included)
    {
        using var conn = _db.OpenMetadata();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE roots SET included = @inc WHERE id = @id;";
        cmd.Parameters.AddWithValue("@inc", included ? 1 : 0);
        cmd.Parameters.AddWithValue("@id", rootId);
        cmd.ExecuteNonQuery();
    }

    public void SetAlias(long rootId, string alias)
    {
        using var conn = _db.OpenMetadata();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE roots SET alias = @alias WHERE id = @id;";
        cmd.Parameters.AddWithValue("@alias", alias);
        cmd.Parameters.AddWithValue("@id", rootId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Returns the ids of all items owned by a root — callers use this to purge
    /// the matching thumbnails before deleting the root (cross-DB cascade).
    /// </summary>
    public List<long> GetItemIds(long rootId)
    {
        using var conn = _db.OpenMetadata();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM items WHERE root_id = @id;";
        cmd.Parameters.AddWithValue("@id", rootId);
        using var reader = cmd.ExecuteReader();
        var ids = new List<long>();
        while (reader.Read())
            ids.Add(reader.GetInt64(0));
        return ids;
    }

    /// <summary>Deletes the root; its items cascade via foreign key.</summary>
    public void Remove(long rootId)
    {
        using var conn = _db.OpenMetadata();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM roots WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", rootId);
        cmd.ExecuteNonQuery();
    }
}
