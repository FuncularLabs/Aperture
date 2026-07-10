using Microsoft.Data.Sqlite;
using Aperture.Core.Models;
using Aperture.Core.Storage;
using Aperture.Core.Tests.Support;

namespace Aperture.Core.Tests;

public class StorageTests
{
    [Fact]
    public void Initialize_CreatesExpectedTables()
    {
        using var scope = new ApertureScope();

        Assert.True(TableExists(scope.Database.OpenMetadata(), "roots"));
        Assert.True(TableExists(scope.Database.OpenMetadata(), "items"));
        Assert.True(TableExists(scope.Database.OpenThumbnails(), "thumbs"));
    }

    [Fact]
    public void RootStore_Add_AssignsId_And_GetAll_Roundtrips()
    {
        using var scope = new ApertureScope();

        var added = scope.Roots.Add(new Root
        {
            Path = @"C:\photos\camera",
            Alias = "Camera",
            ColorTag = "#ff8800",
            AddedUtc = DateTime.UtcNow,
        });

        Assert.True(added.Id > 0);

        var all = scope.Roots.GetAll();
        var root = Assert.Single(all);
        Assert.Equal("Camera", root.Alias);
        Assert.Equal(@"C:\photos\camera", root.Path);
        Assert.Equal("#ff8800", root.ColorTag);
        Assert.True(root.Included);
    }

    [Fact]
    public void RootStore_SetIncluded_Persists()
    {
        using var scope = new ApertureScope();
        var root = scope.Roots.Add(new Root { Path = @"C:\x", Alias = "X", AddedUtc = DateTime.UtcNow });

        scope.Roots.SetIncluded(root.Id, false);

        Assert.False(scope.Roots.GetAll().Single().Included);
    }

    private static bool TableExists(SqliteConnection conn, string table)
    {
        using (conn)
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@n;";
            cmd.Parameters.AddWithValue("@n", table);
            return (long)cmd.ExecuteScalar()! == 1;
        }
    }
}
