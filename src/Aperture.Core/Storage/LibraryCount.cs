using Aperture.Core.Library;
using Funcular.Data.Orm.Sqlite;
using Microsoft.Data.Sqlite;

namespace Aperture.Core.Storage;

/// <summary>
/// How many items a library shows when it's open, read from its own files while it isn't, for the library list
/// (0.9.1 batch, item 6). The same rule the open library follows, by the same code: its included folders' items, less
/// those in hidden subfolders (<see cref="LibraryService.Contents"/>).
/// </summary>
public static class LibraryCount
{
    /// <summary>
    /// The library in <paramref name="dataDir"/>'s count, or <c>null</c> if it has no database there or it can't be
    /// read. Read only: it creates no database where none is, never writes the library's own (it's opened read-only),
    /// and keeps no connection open after it returns. SQLite may leave its two working files beside the database
    /// (<c>-wal</c>, <c>-shm</c>), as any open of the library does; the next open of it tidies them, and Delete library
    /// files removes them. A library without hidden subfolders is counted by SQL, without reading its rows.
    /// </summary>
    public static int? Read(string dataDir)
    {
        var path = Path.Combine(dataDir, ApertureDatabase.MetadataFileName);
        if (!File.Exists(path))
            return null; // not created: a missing library (an unplugged drive) stays missing

        try
        {
            using var provider = new SqliteOrmDataProvider(ConnectionStringFor(path));
            var hidden = HiddenFolderStore.All(provider);
            return hidden.Count == 0
                ? ItemStore.IncludedCount(provider) // the common case: nothing hidden, so no rows to read (review R1-A9)
                : LibraryService.Contents(ItemStore.IncludedUnion(provider), hidden).Rows.Count;
        }
        catch (Exception)
        {
            // Best effort, for a list's label: a file that isn't a library, one from a version without these tables,
            // a drive that went away mid-read. The list keeps the count it had.
            return null;
        }
    }

    /// <summary>How another library's database is opened to count it.</summary>
    internal static string ConnectionStringFor(string path) => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadOnly, // never creates the file, never writes it
        Pooling = false,                // no handle kept after the read: Delete library files can go straight on
    }.ToString();
}
