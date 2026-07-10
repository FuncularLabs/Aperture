namespace Aperture.Core.Storage;

/// <summary>
/// One-time move of the pre-rename data store (<c>%LOCALAPPDATA%\Reel</c>, <c>reel.db</c>)
/// into the Aperture location (<c>%LOCALAPPDATA%\Aperture</c>, <c>aperture.db</c>). Runs only
/// when the new store has no database yet and the old one exists, so it fires once and is a
/// no-op for fresh installs. Best-effort: any failure just leaves the app to start fresh.
/// </summary>
public static class LegacyDataMigration
{
    private const string LegacyMetadataFileName = "reel.db";

    /// <summary>Returns true if data was migrated on this call.</summary>
    public static bool Run(string newDataDir, string legacyDataDir)
    {
        try
        {
            if (PathsEqual(newDataDir, legacyDataDir))
                return false;

            var newDb = Path.Combine(newDataDir, ApertureDatabase.MetadataFileName); // aperture.db
            var legacyDb = Path.Combine(legacyDataDir, LegacyMetadataFileName);       // reel.db
            if (File.Exists(newDb) || !File.Exists(legacyDb))
                return false; // already migrated, or nothing to migrate (fresh install)

            Directory.CreateDirectory(newDataDir);

            // The metadata DB is renamed (reel.db → aperture.db); everything else keeps its name.
            MoveIfPresent(legacyDb, newDb);
            MoveIfPresent(legacyDb + "-wal", newDb + "-wal");
            MoveIfPresent(legacyDb + "-shm", newDb + "-shm");
            foreach (var name in new[]
                     {
                         ApertureDatabase.ThumbnailFileName, "thumbs.db-wal", "thumbs.db-shm", "settings.json",
                     })
                MoveIfPresent(Path.Combine(legacyDataDir, name), Path.Combine(newDataDir, name));

            return true;
        }
        catch
        {
            return false; // never block startup on a migration hiccup
        }
    }

    private static void MoveIfPresent(string from, string to)
    {
        if (File.Exists(from) && !File.Exists(to))
            File.Move(from, to);
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'),
                      Path.GetFullPath(b).TrimEnd('\\', '/'),
                      StringComparison.OrdinalIgnoreCase);
}
