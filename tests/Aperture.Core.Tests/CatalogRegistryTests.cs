using Aperture.Core.Storage;
using Aperture.Core.Tests.Support;

namespace Aperture.Core.Tests;

/// <summary>
/// Catalog registry (Option B — separate libraries). The registry is a single JSON file that
/// lives outside any catalog and tracks the set of catalog directories + which one is active.
/// Mirrors the tolerant-config behaviour of <c>SettingsService</c>: a missing or corrupt file
/// yields an empty registry rather than a crash.
/// </summary>
public class CatalogRegistryTests
{
    private static string RegistryPath(TempDir dir) => dir.Combine("catalogs.json");

    [Fact]
    public void New_NoFile_IsEmpty()
    {
        using var dir = new TempDir();
        var reg = new CatalogRegistry(RegistryPath(dir));

        Assert.Empty(reg.List());
        Assert.Null(reg.Active);
    }

    [Fact]
    public void Add_GeneratesId_NormalizesDir_AndRoundTrips()
    {
        using var dir = new TempDir();
        var catDir = dir.Combine("Personal");
        Directory.CreateDirectory(catDir);

        var entry = new CatalogRegistry(RegistryPath(dir)).Add("Personal", catDir);

        Assert.False(string.IsNullOrWhiteSpace(entry.Id));
        Assert.Equal("Personal", entry.Name);
        // A fresh instance over the same file sees the persisted entry.
        var reloaded = new CatalogRegistry(RegistryPath(dir));
        var only = Assert.Single(reloaded.List());
        Assert.Equal(entry.Id, only.Id);
        Assert.Equal("Personal", only.Name);
        Assert.Equal(Path.GetFullPath(catDir), only.Dir);
    }

    [Fact]
    public void Add_FirstEntry_BecomesActive()
    {
        using var dir = new TempDir();
        var reg = new CatalogRegistry(RegistryPath(dir));

        var first = reg.Add("One", dir.Combine("one"));

        Assert.NotNull(reg.Active);
        Assert.Equal(first.Id, reg.Active!.Id);
    }

    [Fact]
    public void Add_SecondEntry_DoesNotStealActive()
    {
        using var dir = new TempDir();
        var reg = new CatalogRegistry(RegistryPath(dir));
        var first = reg.Add("One", dir.Combine("one"));

        reg.Add("Two", dir.Combine("two"));

        Assert.Equal(first.Id, reg.Active!.Id);
    }

    [Fact]
    public void Add_SameDir_IsDeduped_CaseAndSlashInsensitive()
    {
        using var dir = new TempDir();
        var reg = new CatalogRegistry(RegistryPath(dir));
        var catDir = dir.Combine("Work");

        var a = reg.Add("Work", catDir);
        var b = reg.Add("Work again", catDir.ToUpperInvariant() + Path.DirectorySeparatorChar);

        Assert.Equal(a.Id, b.Id);            // same entry returned, not a duplicate
        Assert.Single(reg.List());
    }

    [Fact]
    public void SetActive_ChangesActive_AndBumpsLastOpened()
    {
        using var dir = new TempDir();
        var reg = new CatalogRegistry(RegistryPath(dir));
        reg.Add("One", dir.Combine("one"));
        var two = reg.Add("Two", dir.Combine("two"));

        var ok = reg.SetActive(two.Id);

        Assert.True(ok);
        Assert.Equal(two.Id, reg.Active!.Id);
        Assert.True(reg.Active!.LastOpenedTicks > 0);
    }

    [Fact]
    public void SetActive_UnknownId_ReturnsFalse_AndLeavesActiveUnchanged()
    {
        using var dir = new TempDir();
        var reg = new CatalogRegistry(RegistryPath(dir));
        var one = reg.Add("One", dir.Combine("one"));

        var ok = reg.SetActive("nope");

        Assert.False(ok);
        Assert.Equal(one.Id, reg.Active!.Id);
    }

    [Fact]
    public void Rename_ChangesName_AndPersists()
    {
        using var dir = new TempDir();
        var reg = new CatalogRegistry(RegistryPath(dir));
        var one = reg.Add("One", dir.Combine("one"));

        var ok = reg.Rename(one.Id, "Renamed");

        Assert.True(ok);
        Assert.Equal("Renamed", new CatalogRegistry(RegistryPath(dir)).List().Single().Name);
    }

    [Fact]
    public void Rename_UnknownId_ReturnsFalse()
    {
        using var dir = new TempDir();
        var reg = new CatalogRegistry(RegistryPath(dir));
        reg.Add("One", dir.Combine("one"));

        Assert.False(reg.Rename("nope", "x"));
    }

    [Fact]
    public void Remove_ForgetsEntry_ButKeepsFilesByDefault()
    {
        using var dir = new TempDir();
        var reg = new CatalogRegistry(RegistryPath(dir));
        var catDir = dir.Combine("Keep");
        Directory.CreateDirectory(catDir);
        var entry = reg.Add("Keep", catDir);

        var ok = reg.Remove(entry.Id);

        Assert.True(ok);
        Assert.Empty(reg.List());
        Assert.True(Directory.Exists(catDir));   // files untouched
    }

    [Fact]
    public void Remove_Active_PromotesAnotherCatalogToActive()
    {
        using var dir = new TempDir();
        var reg = new CatalogRegistry(RegistryPath(dir));
        var one = reg.Add("One", dir.Combine("one"));
        var two = reg.Add("Two", dir.Combine("two"));
        reg.SetActive(two.Id);

        reg.Remove(two.Id);

        Assert.Equal(one.Id, reg.Active!.Id);
    }

    [Fact]
    public void Remove_LastCatalog_LeavesNoActive()
    {
        using var dir = new TempDir();
        var reg = new CatalogRegistry(RegistryPath(dir));
        var one = reg.Add("One", dir.Combine("one"));

        reg.Remove(one.Id);

        Assert.Null(reg.Active);
    }

    [Fact]
    public void Remove_WithDeleteFiles_DeletesTheCatalogDir()
    {
        using var dir = new TempDir();
        var reg = new CatalogRegistry(RegistryPath(dir));
        var catDir = dir.Combine("Trash");
        Directory.CreateDirectory(catDir);
        File.WriteAllText(Path.Combine(catDir, "aperture.db"), "x");
        var entry = reg.Add("Trash", catDir);

        var ok = reg.Remove(entry.Id, deleteFiles: true);

        Assert.True(ok);
        Assert.False(Directory.Exists(catDir));
    }

    [Fact]
    public void Remove_UnknownId_ReturnsFalse()
    {
        using var dir = new TempDir();
        var reg = new CatalogRegistry(RegistryPath(dir));
        reg.Add("One", dir.Combine("one"));

        Assert.False(reg.Remove("nope"));
    }

    [Fact]
    public void EnsureDefault_RegistersExistingDir_AsActiveDefault()
    {
        using var dir = new TempDir();
        var reg = new CatalogRegistry(RegistryPath(dir));
        var defaultDir = dir.Combine("Aperture");
        Directory.CreateDirectory(defaultDir);

        reg.EnsureDefault(defaultDir);

        var only = Assert.Single(reg.List());
        Assert.Equal("Default", only.Name);
        Assert.Equal(Path.GetFullPath(defaultDir), only.Dir);
        Assert.Equal(only.Id, reg.Active!.Id);
    }

    [Fact]
    public void EnsureDefault_IsIdempotent_AndDoesNotRenameOrDuplicate()
    {
        using var dir = new TempDir();
        var reg = new CatalogRegistry(RegistryPath(dir));
        var defaultDir = dir.Combine("Aperture");

        // User already registered this dir under their own name and added a second catalog.
        var mine = reg.Add("My Photos", defaultDir);
        reg.Add("Work", dir.Combine("work"));

        reg.EnsureDefault(defaultDir);

        Assert.Equal(2, reg.List().Count);                               // no new entry
        Assert.Equal("My Photos", reg.List().Single(c => c.Id == mine.Id).Name); // not renamed
    }

    [Fact]
    public void Load_ToleratesCorruptFile()
    {
        using var dir = new TempDir();
        File.WriteAllText(RegistryPath(dir), "{ this is not valid json ");

        var reg = new CatalogRegistry(RegistryPath(dir));

        Assert.Empty(reg.List());
        Assert.Null(reg.Active);
    }
}
