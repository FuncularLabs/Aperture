namespace Aperture.Core.Storage;

/// <summary>
/// The <c>hidden_folders</c> table — read and added to via FunkyORM, deleted from with one plain statement: which
/// subfolders the user hid. Folders are kept relative to their root, with the OS separator and no leading or
/// trailing one; matching ignores case (the column is NOCASE).
/// </summary>
public sealed class HiddenFolderStore(ApertureDatabase database)
{
    private readonly ApertureDatabase _db = database;

    public List<HiddenFolderRow> GetAll() => [.. _db.Provider.GetList<HiddenFolderRow>()];

    /// <summary>Hides a folder; hiding one that's already hidden changes nothing.</summary>
    public void Hide(long rootId, string relDir)
    {
        if (Find(rootId, relDir) is null)
            _db.Provider.Insert<HiddenFolderRow, long>(new HiddenFolderRow { RootId = rootId, RelDir = relDir });
    }

    /// <summary>Shows a folder again; showing one that isn't hidden changes nothing.</summary>
    public void Show(long rootId, string relDir)
    {
        // One statement, so no transaction (FunkyORM's Delete wants one); rel_dir is NOCASE, so case doesn't matter.
        using var conn = _db.OpenMetadata();
        using var delete = conn.CreateCommand();
        delete.CommandText = "DELETE FROM hidden_folders WHERE root_id = @root AND rel_dir = @dir;";
        delete.Parameters.AddWithValue("@root", rootId);
        delete.Parameters.AddWithValue("@dir", relDir);
        delete.ExecuteNonQuery();
    }

    // In memory, case-insensitively, like the column: a library hides a handful of folders at most.
    private HiddenFolderRow? Find(long rootId, string relDir) =>
        GetAll().FirstOrDefault(r => r.RootId == rootId && string.Equals(r.RelDir, relDir, StringComparison.OrdinalIgnoreCase));
}
