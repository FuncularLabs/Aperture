namespace Aperture.Core.Storage;

/// <summary>
/// The <c>hidden_folders</c> table — read and added to via FunkyORM, deleted from with one plain statement: which
/// subfolders the user hid. Folders are kept relative to their root, with the OS separator and no leading or
/// trailing one. A folder is found ignoring case — any letter's, as the union's filter does (the column's NOCASE
/// only folds ASCII, so matching is done here, not in SQL).
/// </summary>
public sealed class HiddenFolderStore(ApertureDatabase database)
{
    private readonly ApertureDatabase _db = database;

    public List<HiddenFolderRow> GetAll() => All(_db.Provider);

    /// <summary><see cref="GetAll"/> over any provider (see <see cref="ItemStore.IncludedUnion"/>).</summary>
    internal static List<HiddenFolderRow> All(Funcular.Data.Orm.Sqlite.SqliteOrmDataProvider provider) =>
        [.. provider.GetList<HiddenFolderRow>()];

    /// <summary>Hides a folder; hiding one that's already hidden changes nothing.</summary>
    public void Hide(long rootId, string relDir)
    {
        if (Find(rootId, relDir) is null)
            _db.Provider.Insert<HiddenFolderRow, long>(new HiddenFolderRow { RootId = rootId, RelDir = relDir });
    }

    /// <summary>Shows a folder again; showing one that isn't hidden changes nothing.</summary>
    public void Show(long rootId, string relDir)
    {
        if (Find(rootId, relDir) is not { } row)
            return;
        // One statement, so no transaction (FunkyORM's Delete wants one).
        using var conn = _db.OpenMetadata();
        using var delete = conn.CreateCommand();
        delete.CommandText = "DELETE FROM hidden_folders WHERE id = @id;";
        delete.Parameters.AddWithValue("@id", row.Id);
        delete.ExecuteNonQuery();
    }

    // In memory, ignoring case: a library hides a handful of folders at most.
    private HiddenFolderRow? Find(long rootId, string relDir) =>
        GetAll().FirstOrDefault(r => r.RootId == rootId && string.Equals(r.RelDir, relDir, StringComparison.OrdinalIgnoreCase));
}
