using System.Text.Json;
using Microsoft.Data.Sqlite;
using Reel.Core.Annotations;
using Reel.Core.Models;

namespace Reel.Core.Storage;

/// <summary>
/// Tags + notes per file, keyed by absolute path (case-insensitive) so they
/// survive re-indexing and removing/re-adding a root. Tags are stored as a JSON
/// array. Deleting all tags and clearing the note removes the row.
/// </summary>
public sealed class AnnotationStore
{
    private readonly ReelDatabase _db;
    private readonly TagStatsStore _stats;
    private readonly Lock _seedLock = new();
    private bool _seeded;

    public AnnotationStore(ReelDatabase database)
    {
        _db = database;
        _stats = new TagStatsStore(database);
    }

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

    /// <summary>Tag -> number of files carrying it.</summary>
    public Dictionary<string, int> GetTagCounts()
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var annotation in GetAll().Values)
            foreach (var tag in annotation.Tags)
                counts[tag] = counts.GetValueOrDefault(tag) + 1;
        return counts;
    }

    /// <summary>Renames a tag across every file. Renaming onto an existing tag merges them.</summary>
    public void RenameTag(string oldTag, string newTag)
    {
        newTag = newTag.Trim();
        if (newTag.Length == 0 || string.Equals(oldTag, newTag, StringComparison.Ordinal))
            return;

        foreach (var (path, annotation) in GetAll())
        {
            if (!annotation.Tags.Any(t => Ci.Equals(t, oldTag)))
                continue;
            var tags = annotation.Tags
                .Select(t => Ci.Equals(t, oldTag) ? newTag : t)
                .Distinct(Ci)
                .ToList();
            Save(path, tags, annotation.Note);
        }
    }

    /// <summary>Removes a tag from every file.</summary>
    public void DeleteTag(string tag)
    {
        foreach (var (path, annotation) in GetAll())
        {
            if (!annotation.Tags.Any(t => Ci.Equals(t, tag)))
                continue;
            Save(path, annotation.Tags.Where(t => !Ci.Equals(t, tag)).ToList(), annotation.Note);
        }
    }

    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;

    /// <summary>Current tags across all files, ordered by recency of use (most recent first).</summary>
    public List<string> GetTagsByRecency()
    {
        EnsureSeeded();
        var stats = _stats.GetAll();
        return GetAllTags()
            .OrderByDescending(t => stats.TryGetValue(t, out var s) ? s.LastUsedTicks : 0L)
            .ThenByDescending(t => stats.TryGetValue(t, out var s) ? s.UseCount : 0)
            .ThenBy(t => t, Ci)
            .ToList();
    }

    /// <summary>Current tags with their item count and last-used timestamp (for quick-picks).</summary>
    public List<TagUsage> GetTagUsage()
    {
        EnsureSeeded();
        var stats = _stats.GetAll();
        return GetTagCounts()
            .Select(kv => new TagUsage(kv.Key, kv.Value, stats.TryGetValue(kv.Key, out var s) ? s.LastUsedTicks : 0L))
            .ToList();
    }

    /// <summary>One-time migration: rewrite existing tags to the hyphenated form. Guarded by user_version.</summary>
    public void EnsureHyphenated()
    {
        using var conn = _db.OpenMetadata();
        using (var check = conn.CreateCommand())
        {
            check.CommandText = "PRAGMA user_version;";
            if (Convert.ToInt64(check.ExecuteScalar()) >= 1)
                return;
        }

        foreach (var (path, annotation) in GetAll())
        {
            var hyphenated = annotation.Tags
                .Select(TagNormalizer.Normalize).Where(t => t.Length > 0).Distinct(Ci).ToList();
            if (!hyphenated.SequenceEqual(annotation.Tags, Ci))
                Save(path, hyphenated, annotation.Note);
        }

        using (var reset = conn.CreateCommand())
        {
            reset.CommandText = "DELETE FROM tag_stats; PRAGMA user_version = 1;";
            reset.ExecuteNonQuery();
        }
        _seeded = false; // re-seed the recency stats from the migrated (hyphenated) tags
    }

    private void EnsureSeeded()
    {
        if (_seeded)
            return;
        lock (_seedLock)
        {
            if (_seeded)
                return;
            _stats.EnsureSeeded(json => ParseTags(json));
            _seeded = true;
        }
    }

    public void Save(string path, IReadOnlyList<string> tags, string note)
    {
        var cleanTags = tags
            .Select(TagNormalizer.Normalize) // multi-word tags become hyphenated
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var cleanNote = note?.Trim() ?? "";

        // Tags newly present on this item get their recency bumped (never on removal).
        EnsureSeeded();
        var existing = Get(path).Tags;
        var added = cleanTags.Where(t => !existing.Any(e => Ci.Equals(e, t))).ToList();

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

        if (added.Count > 0)
            _stats.RecordUse(added);
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
