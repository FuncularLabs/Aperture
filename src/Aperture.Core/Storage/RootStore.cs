using Aperture.Core.Models;

namespace Aperture.Core.Storage;

/// <summary>
/// CRUD for the <c>roots</c> table, via FunkyORM (Funcular Labs' own ORM). Maps the ticks-based
/// <see cref="RootRow"/> entity to/from the DateTime-based <see cref="Root"/> domain model.
/// </summary>
public sealed class RootStore(ApertureDatabase database)
{
    private readonly ApertureDatabase _db = database;

    private static Root ToModel(RootRow r) => new()
    {
        Id = r.Id,
        Path = r.Path,
        Alias = r.Alias,
        Included = r.Included,
        ColorTag = r.ColorTag,
        AddedUtc = new DateTime(r.AddedTicks, DateTimeKind.Utc),
    };

    /// <summary>Inserts a root and returns it with its assigned id.</summary>
    public Root Add(Root root)
    {
        var row = new RootRow
        {
            Path = root.Path,
            Alias = root.Alias,
            Included = root.Included,
            ColorTag = root.ColorTag,
            AddedTicks = root.AddedUtc.ToUniversalTime().Ticks,
        };
        root.Id = _db.Provider.Insert<RootRow, long>(row);
        return root;
    }

    public List<Root> GetAll() =>
        _db.Provider.GetList<RootRow>()
            .OrderBy(r => r.Alias, StringComparer.OrdinalIgnoreCase) // COLLATE NOCASE, in memory (roots are few)
            .Select(ToModel)
            .ToList();

    public void SetIncluded(long rootId, bool included)
    {
        if (_db.Provider.Get<RootRow>(rootId) is { } row)
        {
            row.Included = included;
            _db.Provider.Update(row);
        }
    }

    public void SetAlias(long rootId, string alias)
    {
        if (_db.Provider.Get<RootRow>(rootId) is { } row)
        {
            row.Alias = alias;
            _db.Provider.Update(row);
        }
    }

    /// <summary>
    /// Ids of all items owned by a root — callers purge the matching thumbnails before deleting the
    /// root (cross-DB cascade). Kept as a raw id projection: FunkyORM materializes whole entities, so
    /// a large root would mean pulling every column just to read the key.
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

    /// <summary>Deletes the root; its items cascade via foreign key. (FunkyORM requires a transaction for deletes.)</summary>
    public void Remove(long rootId)
    {
        _db.Provider.BeginTransaction();
        try
        {
            _db.Provider.Delete<RootRow>(rootId);
            _db.Provider.CommitTransaction();
        }
        catch
        {
            _db.Provider.RollbackTransaction();
            throw;
        }
    }
}
