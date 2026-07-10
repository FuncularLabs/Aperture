namespace Aperture.Core.Storage;

/// <summary>Raw per-tag usage row: when it was last applied and how many times.</summary>
public readonly record struct TagStat(string Name, long LastUsedTicks, int UseCount);

/// <summary>
/// Tracks per-tag usage so tag lists can be ordered by recency (and skew can be
/// judged for quick-picks). A tag's timestamp/count is bumped each time it is
/// <em>added</em> to an item — never on removal. Keyed case-insensitively.
/// Data access via FunkyORM (name is a string <c>[Key]</c>).
/// </summary>
public sealed class TagStatsStore(ApertureDatabase database)
{
    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;
    private readonly ApertureDatabase _db = database;

    /// <summary>Bumps last-used = now and increments the count for each tag (get-then-insert/update upsert).</summary>
    public void RecordUse(IEnumerable<string> tags)
    {
        var clean = tags.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(Ci).ToList();
        if (clean.Count == 0)
            return;

        var now = DateTime.UtcNow.Ticks;
        var p = _db.Provider;
        p.BeginTransaction();
        try
        {
            foreach (var tag in clean)
            {
                if (p.Get<TagStatRow>(tag) is { } row)
                {
                    row.LastUsedTicks = now;
                    row.UseCount += 1;
                    p.Update(row);
                }
                else
                {
                    p.Insert(new TagStatRow { Name = tag, LastUsedTicks = now, UseCount = 1 });
                }
            }
            p.CommitTransaction();
        }
        catch
        {
            p.RollbackTransaction();
            throw;
        }
    }

    public Dictionary<string, TagStat> GetAll() =>
        _db.Provider.GetList<TagStatRow>()
            .ToDictionary(r => r.Name, r => new TagStat(r.Name, r.LastUsedTicks, r.UseCount), Ci);

    /// <summary>
    /// One-time backfill for libraries that already had tags before stats existed:
    /// seeds each tag with its item count and the most recent time any item carrying
    /// it was saved. No-op once any stats exist.
    /// </summary>
    public void EnsureSeeded(Func<string, IEnumerable<string>> parseTags)
    {
        var p = _db.Provider;
        if (p.Query<TagStatRow>().Any())
            return;

        var counts = new Dictionary<string, int>(Ci);
        var last = new Dictionary<string, long>(Ci);
        foreach (var a in p.GetList<AnnotationRow>())
        {
            foreach (var tag in parseTags(a.Tags))
            {
                var t = tag.Trim();
                if (t.Length == 0)
                    continue;
                counts[t] = counts.GetValueOrDefault(t) + 1;
                if (!last.TryGetValue(t, out var existing) || a.UpdatedTicks > existing)
                    last[t] = a.UpdatedTicks;
            }
        }
        if (counts.Count == 0)
            return;

        p.BeginTransaction();
        try
        {
            foreach (var (name, count) in counts)
                p.Insert(new TagStatRow { Name = name, LastUsedTicks = last.GetValueOrDefault(name), UseCount = count });
            p.CommitTransaction();
        }
        catch
        {
            p.RollbackTransaction();
            throw;
        }
    }
}
