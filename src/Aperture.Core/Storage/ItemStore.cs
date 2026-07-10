using Aperture.Core.Models;

namespace Aperture.Core.Storage;

/// <summary>Read queries over the <c>items</c> table, via FunkyORM. Bulk writes live in the indexer.</summary>
public sealed class ItemStore(ApertureDatabase database)
{
    private readonly ApertureDatabase _db = database;

    /// <summary>Lightweight row used to decide whether a file needs re-indexing.</summary>
    public readonly record struct ExistingItem(long Id, long MTimeTicks, long SizeBytes);

    private static MediaItem ToModel(ItemRow r) => new()
    {
        Id = r.Id,
        RootId = r.RootId,
        RelPath = r.RelPath,
        FileName = r.FileName,
        Ext = r.Ext,
        SizeBytes = r.SizeBytes,
        MTimeUtc = new DateTime(r.MtimeTicks, DateTimeKind.Utc),
        TakenUtc = r.TakenTicks is { } t ? new DateTime(t, DateTimeKind.Unspecified) : null,
        Width = r.Width,
        Height = r.Height,
        Camera = r.Camera,
        Orientation = r.Orientation,
        IndexedUtc = new DateTime(r.IndexedTicks, DateTimeKind.Utc),
        Kind = (MediaKind)r.Kind,
    };

    // Default view order: EXIF-taken date (falling back to mtime) desc, then alias, then file name.
    private static long SortKey(ItemRow r) => r.TakenTicks ?? r.MtimeTicks;

    /// <summary>
    /// Maps rel_path -> existing row for one root, for incremental diffing. Kept as a raw, four-column
    /// projection: this runs on every index reconcile, so materializing whole entities (all columns,
    /// for tens of thousands of rows) would be needlessly heavy.
    /// </summary>
    public Dictionary<string, ExistingItem> GetExisting(long rootId)
    {
        using var conn = _db.OpenMetadata();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, rel_path, mtime_ticks, size_bytes FROM items WHERE root_id = @root;";
        cmd.Parameters.AddWithValue("@root", rootId);
        using var reader = cmd.ExecuteReader();
        var map = new Dictionary<string, ExistingItem>(StringComparer.Ordinal);
        while (reader.Read())
            map[reader.GetString(1)] = new ExistingItem(reader.GetInt64(0), reader.GetInt64(2), reader.GetInt64(3));
        return map;
    }

    public int CountForRoot(long rootId) =>
        _db.Provider.Query<ItemRow>().Where(i => i.RootId == rootId).Count();

    /// <summary>All items for a root, newest first. Convenience for tests and M2 wiring.</summary>
    public List<MediaItem> GetForRoot(long rootId) =>
        _db.Provider.Query<ItemRow>().Where(i => i.RootId == rootId).ToList()
            .OrderByDescending(SortKey)
            .Select(ToModel)
            .ToList();

    /// <summary>
    /// All items belonging to <em>included</em> roots, joined with their root's alias/color/path,
    /// newest first — the query that backs the grid. FunkyORM resolves the roots join from the
    /// <c>[RemoteProperty]</c> attributes on <see cref="LibraryRowEntity"/> (no hand-written JOIN).
    /// The multi-level default order is applied in memory (the app re-sorts per the user's chosen sort).
    /// </summary>
    public List<LibraryRow> GetIncludedUnion() =>
        _db.Provider.Query<LibraryRowEntity>().Where(x => x.RootIncluded).ToList()
            .OrderByDescending(SortKey)
            .ThenBy(x => x.RootAlias, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.FileName, StringComparer.OrdinalIgnoreCase)
            .Select(x => new LibraryRow
            {
                Item = ToModel(x),
                RootAlias = x.RootAlias,
                RootColor = x.RootColor,
                RootPath = x.RootPath,
            })
            .ToList();
}
