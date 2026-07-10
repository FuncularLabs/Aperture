using Microsoft.Data.Sqlite;

namespace Aperture.Core.Storage;

/// <summary>Raw per-tag usage row: when it was last applied and how many times.</summary>
public readonly record struct TagStat(string Name, long LastUsedTicks, int UseCount);

/// <summary>
/// Tracks per-tag usage so tag lists can be ordered by recency (and skew can be
/// judged for quick-picks). A tag's timestamp/count is bumped each time it is
/// <em>added</em> to an item — never on removal. Keyed case-insensitively.
/// </summary>
public sealed class TagStatsStore(ApertureDatabase database)
{
    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;
    private readonly ApertureDatabase _db = database;

    /// <summary>Bumps last-used = now and increments the count for each tag.</summary>
    public void RecordUse(IEnumerable<string> tags)
    {
        var clean = tags.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(Ci).ToList();
        if (clean.Count == 0)
            return;

        var now = DateTime.UtcNow.Ticks;
        using var conn = _db.OpenMetadata();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO tag_stats (name, last_used_ticks, use_count)
            VALUES (@n, @t, 1)
            ON CONFLICT(name) DO UPDATE SET last_used_ticks = @t, use_count = use_count + 1;
            """;
        var pName = cmd.CreateParameter(); pName.ParameterName = "@n"; cmd.Parameters.Add(pName);
        var pTicks = cmd.CreateParameter(); pTicks.ParameterName = "@t"; pTicks.Value = now; cmd.Parameters.Add(pTicks);
        foreach (var tag in clean)
        {
            pName.Value = tag;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public Dictionary<string, TagStat> GetAll()
    {
        using var conn = _db.OpenMetadata();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name, last_used_ticks, use_count FROM tag_stats;";
        using var reader = cmd.ExecuteReader();
        var map = new Dictionary<string, TagStat>(Ci);
        while (reader.Read())
            map[reader.GetString(0)] = new TagStat(reader.GetString(0), reader.GetInt64(1), reader.GetInt32(2));
        return map;
    }

    /// <summary>
    /// One-time backfill for libraries that already had tags before stats existed:
    /// seeds each tag with its item count and the most recent time any item carrying
    /// it was saved. No-op once any stats exist.
    /// </summary>
    public void EnsureSeeded(Func<string, IEnumerable<string>> parseTags)
    {
        using var conn = _db.OpenMetadata();
        using (var check = conn.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM tag_stats;";
            if ((long)check.ExecuteScalar()! > 0)
                return;
        }

        var counts = new Dictionary<string, int>(Ci);
        var last = new Dictionary<string, long>(Ci);
        using (var read = conn.CreateCommand())
        {
            read.CommandText = "SELECT tags, updated_ticks FROM annotations;";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                var ticks = reader.GetInt64(1);
                foreach (var tag in parseTags(reader.GetString(0)))
                {
                    var t = tag.Trim();
                    if (t.Length == 0)
                        continue;
                    counts[t] = counts.GetValueOrDefault(t) + 1;
                    if (!last.TryGetValue(t, out var existing) || ticks > existing)
                        last[t] = ticks;
                }
            }
        }
        if (counts.Count == 0)
            return;

        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO tag_stats (name, last_used_ticks, use_count) VALUES (@n, @t, @c);
            """;
        var pName = cmd.CreateParameter(); pName.ParameterName = "@n"; cmd.Parameters.Add(pName);
        var pTicks = cmd.CreateParameter(); pTicks.ParameterName = "@t"; cmd.Parameters.Add(pTicks);
        var pCount = cmd.CreateParameter(); pCount.ParameterName = "@c"; cmd.Parameters.Add(pCount);
        foreach (var (name, count) in counts)
        {
            pName.Value = name;
            pTicks.Value = last.GetValueOrDefault(name);
            pCount.Value = count;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }
}
