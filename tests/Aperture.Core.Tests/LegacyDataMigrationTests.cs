using Aperture.Core.Storage;
using Aperture.Core.Tests.Support;

namespace Aperture.Core.Tests;

public class LegacyDataMigrationTests
{
    [Fact]
    public void Moves_legacy_reel_data_renaming_the_metadata_db()
    {
        using var legacy = new TempDir();
        using var parent = new TempDir();
        var newDir = Path.Combine(parent.Path, "Aperture"); // must not exist yet

        File.WriteAllText(legacy.Combine("reel.db"), "meta");
        File.WriteAllText(legacy.Combine("reel.db-wal"), "wal");
        File.WriteAllText(legacy.Combine("thumbs.db"), "thumbs");
        File.WriteAllText(legacy.Combine("settings.json"), "{}");

        var migrated = LegacyDataMigration.Run(newDir, legacy.Path);

        Assert.True(migrated);
        Assert.Equal("meta", File.ReadAllText(Path.Combine(newDir, "aperture.db")));   // reel.db → aperture.db
        Assert.Equal("wal", File.ReadAllText(Path.Combine(newDir, "aperture.db-wal")));
        Assert.True(File.Exists(Path.Combine(newDir, "thumbs.db")));                    // kept name
        Assert.True(File.Exists(Path.Combine(newDir, "settings.json")));
        Assert.False(File.Exists(legacy.Combine("reel.db")));                           // moved, not copied
    }

    [Fact]
    public void No_op_when_target_already_has_a_database()
    {
        using var legacy = new TempDir();
        using var target = new TempDir();
        File.WriteAllText(legacy.Combine("reel.db"), "old");
        File.WriteAllText(target.Combine("aperture.db"), "existing");

        Assert.False(LegacyDataMigration.Run(target.Path, legacy.Path));
        Assert.Equal("existing", File.ReadAllText(target.Combine("aperture.db"))); // not clobbered
        Assert.True(File.Exists(legacy.Combine("reel.db")));                       // legacy untouched
    }

    [Fact]
    public void No_op_when_there_is_no_legacy_data()
    {
        using var legacy = new TempDir();
        using var parent = new TempDir();
        Assert.False(LegacyDataMigration.Run(Path.Combine(parent.Path, "Aperture"), legacy.Path));
    }
}
