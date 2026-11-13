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
    /// read. Read only: it creates nothing, writes nothing and keeps no connection open after it returns.
    /// </summary>
    public static int? Read(string dataDir)
    {
        var path = Path.Combine(dataDir, ApertureDatabase.MetadataFileName);
        if (!File.Exists(path))
            return null; // not created: a missing library (an unplugged drive) stays missing

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly, // never creates the file, never writes it
            Pooling = false,                // no handle kept after the read: Delete library files can go straight on
        }.ToString();
        try
        {
            using var provider = new SqliteOrmDataProvider(connectionString);
            return LibraryService.Contents(ItemStore.IncludedUnion(provider), HiddenFolderStore.All(provider)).Rows.Count;
        }
        catch (Exception)
        {
            // Best effort, for a list's label: a file that isn't a library, one from a version without these tables,
            // a drive that went away mid-read. The list keeps the count it had.
            return null;
        }
    }
}
