using System.Text.Json;
using Microsoft.Data.Sqlite;
using Reel.Core.Models;

namespace Reel.Core.Storage;

/// <summary>
/// Tags + notes per file, keyed by absolute path (case-insensitive) so they
/// survive re-indexing and removing/re-adding a root. Tags are stored as a JSON
/// array. Deleting all tags and clearing the note removes the row.
/// </summary>
public sealed class AnnotationStore(ReelDatabase database)
{
    private readonly ReelDatabase _db = database;

    public Annotation Get(string path)
    {
        using var conn = _db.OpenMetadata();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT tags, note FROM annotations WHERE path = @p;";
        cmd.Parameters.AddWithValue("@p", path);
        using var reader = cmd.ExecuteReader();
        return reader.Read()
            ? new Annotation { Tags = ParseTags(reader.GetString(0)), Note = reader.GetString(1) }
            : Annotation.Empty;
    }

    /// <summary>Every annotation keyed by path — loaded once and cached by the UI.</summary>
    public Dictionary<string, Annotation> GetAll()
    {
        using var conn = _db.OpenMetadata();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT path, tags, note FROM annotations;";
        using var reader = cmd.ExecuteReader();
        var map = new Dictionary<string, Annotation>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            map[reader.GetString(0)] = new Annotation
            {
                Tags = ParseTags(reader.GetString(1)),
                Note = reader.GetString(2),
            };
        }
        return map;
    }

    /// <summary>Distinct tags across all files, for pick lists / autocomplete.</summary>
    public List<string> GetAllTags()
    {
        var tags = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var annotation in GetAll().Values)
            foreach (var tag in annotation.Tags)
                tags.Add(tag);
        return [.. tags];
    }

    public void Save(string path, IReadOnlyList<string> tags, string note)
    {
        var cleanTags = tags
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var cleanNote = note?.Trim() ?? "";

        using var conn = _db.OpenMetadata();
        using var cmd = conn.CreateCommand();

        if (cleanTags.Count == 0 && cleanNote.Length == 0)
        {
            cmd.CommandText = "DELETE FROM annotations WHERE path = @p;";
            cmd.Parameters.AddWithValue("@p", path);
            cmd.ExecuteNonQuery();
            return;
        }

        cmd.CommandText = """
            INSERT INTO annotations (path, tags, note, updated_ticks)
            VALUES (@p, @tags, @note, @ticks)
            ON CONFLICT(path) DO UPDATE SET tags = excluded.tags, note = excluded.note, updated_ticks = excluded.updated_ticks;
            """;
        cmd.Parameters.AddWithValue("@p", path);
        cmd.Parameters.AddWithValue("@tags", JsonSerializer.Serialize(cleanTags));
        cmd.Parameters.AddWithValue("@note", cleanNote);
        cmd.Parameters.AddWithValue("@ticks", DateTime.UtcNow.Ticks);
        cmd.ExecuteNonQuery();
    }

    private static List<string> ParseTags(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }
}
