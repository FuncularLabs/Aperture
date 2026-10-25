using System.Text.Json;
using Aperture.Core.Library;
using Aperture.Core.Storage;
using Aperture.Core.Tests.Support;

namespace Aperture.Core.Tests;

/// <summary>
/// Catalog registry (Option B — separate libraries). The registry is a single JSON file that
/// lives outside any catalog and tracks the set of catalog directories + which one is active.
/// A missing, corrupt or hand-edited file must never stop Aperture from starting, and the registry
/// must never delete or overwrite anything that isn't Aperture's own.
/// </summary>
public class CatalogRegistryTests
{
    private static string RegistryPath(TempDir dir) => dir.Combine("catalogs.json");

    [Fact]
    public void DefaultPath_LivesInTheLibrariesFolder_OutsideTheDefaultCatalog()
    {
        Assert.Equal(Path.Combine(AperturePaths.LibrariesDir, "catalogs.json"), CatalogRegistry.DefaultPath);
        Assert.False(AperturePaths.LibrariesDir.StartsWith(
            AperturePaths.DefaultDataDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        Assert.NotEqual(AperturePaths.DefaultDataDir, AperturePaths.LibrariesDir, StringComparer.OrdinalIgnoreCase);
    }

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

        Assert.Equal(a.Id, b.Id);
        Assert.Single(reg.List());
    }

    [Fact]
    public void List_ReturnsSnapshots_MutatingThemChangesNothing()
    {
        using var dir = new TempDir();
        var reg = new CatalogRegistry(RegistryPath(dir));
        reg.Add("One", dir.Combine("one"));

        reg.List()[0].Name = "Mutated";
        reg.Active!.Name = "Mutated too";

        Assert.Equal("One", reg.List()[0].Name);
    }

    [Fact]
    public void SetActive_ChangesActive_BumpsLastOpened_AndPersists()
    {
        using var dir = new TempDir();
        var reg = new CatalogRegistry(RegistryPath(dir));
        reg.Add("One", dir.Combine("one"));
        var two = reg.Add("Two", dir.Combine("two"));

        Assert.True(reg.SetActive(two.Id));

        Assert.Equal(two.Id, reg.Active!.Id);
        Assert.True(reg.Active!.LastOpenedTicks > 0);
        var reloaded = new CatalogRegistry(RegistryPath(dir));
        Assert.Equal(two.Id, reloaded.Active!.Id);
        Assert.True(reloaded.Active!.LastOpenedTicks > 0);
    }

    [Fact]
    public void SetActive_UnknownId_ReturnsFalse_AndLeavesActiveUnchanged()
    {
        using var dir = new TempDir();
        var reg = new CatalogRegistry(RegistryPath(dir));
        var one = reg.Add("One", dir.Combine("one"));

        Assert.False(reg.SetActive("nope"));
        Assert.Equal(one.Id, reg.Active!.Id);
    }

    [Fact]
    public void Rename_ChangesName_AndPersists()
    {
        using var dir = new TempDir();
        var reg = new CatalogRegistry(RegistryPath(dir));
        var one = reg.Add("One", dir.Combine("one"));

        Assert.True(reg.Rename(one.Id, "Renamed"));
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
    public void Remove_ForgetsEntry_KeepsItsFiles_AndPersists()
    {
        using var dir = new TempDir();
        var reg = new CatalogRegistry(RegistryPath(dir));
        reg.Add("Stay", dir.Combine("stay"));
        var catDir = dir.Combine("Keep");
        Directory.CreateDirectory(catDir);
        File.WriteAllText(Path.Combine(catDir, ApertureDatabase.MetadataFileName), "x");
        var entry = reg.Add("Keep", catDir);

        Assert.True(reg.Remove(entry.Id));

        Assert.DoesNotContain(reg.List(), c => c.Id == entry.Id);
        Assert.DoesNotContain(new CatalogRegistry(RegistryPath(dir)).List(), c => c.Id == entry.Id);
        Assert.True(File.Exists(Path.Combine(catDir, ApertureDatabase.MetadataFileName))); // files untouched
    }

    [Fact]
    public void Remove_WhenTheForgetCannotBeSaved_ReturnsFalse_AndKeepsTheEntry()
    {
        using var dir = new TempDir();
        var path = RegistryPath(dir);
        var reg = new CatalogRegistry(path);
        reg.Add("One", dir.Combine("one"));
        var two = reg.Add("Two", dir.Combine("two"));

        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            Assert.False(reg.Remove(two.Id));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }

        Assert.Contains(reg.List(), c => c.Id == two.Id); // in memory matches disk: still listed
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
    public void Remove_UnknownId_ReturnsFalse()
    {
        using var dir = new TempDir();
        var reg = new CatalogRegistry(RegistryPath(dir));
        reg.Add("One", dir.Combine("one"));

        Assert.False(reg.Remove("nope"));
    }

    // --- Deleting a library's files: only Aperture's own, never the user's ---------------------

    [Fact]
    public void RemoveAndDeleteFiles_DeletesOnlyApertureFiles_NeverTheUsersPhotos()
    {
        using var dir = new TempDir();
        var reg = new CatalogRegistry(RegistryPath(dir));
        reg.Add("Stay", dir.Combine("stay"));
        var photos = dir.Combine("Photos");
        Directory.CreateDirectory(photos);
        TestImages.Write(Path.Combine(photos, "IMG_0001.jpg"), 64, 64);
        File.WriteAllText(Path.Combine(photos, "notes.txt"), "mine");
        using (var lib = new LibraryService(photos))
            lib.Settings.Update(s => s.DefaultZoom = 3); // writes settings.json beside the photos
        var entry = reg.Add("Photos", photos);

        var result = reg.RemoveAndDeleteFiles(entry.Id);

        Assert.Equal(CatalogDeletionOutcome.Deleted, result.Outcome);
        Assert.True(File.Exists(Path.Combine(photos, "IMG_0001.jpg")));
        Assert.True(File.Exists(Path.Combine(photos, "notes.txt")));
        Assert.False(File.Exists(Path.Combine(photos, ApertureDatabase.MetadataFileName)));
        Assert.False(File.Exists(Path.Combine(photos, ApertureDatabase.ThumbnailFileName)));
        Assert.False(File.Exists(Path.Combine(photos, "settings.json")));
        Assert.True(Directory.Exists(photos)); // not empty, so the folder stays
    }

    [Fact]
    public void RemoveAndDeleteFiles_RemovesTheFolder_WhenOnlyApertureFilesWereInIt()
    {
        using var dir = new TempDir();
        var reg = new CatalogRegistry(RegistryPath(dir));
        reg.Add("Stay", dir.Combine("stay"));
        var catDir = dir.Combine("Trash");
        using (new LibraryService(catDir)) { }
        var entry = reg.Add("Trash", catDir);

        var result = reg.RemoveAndDeleteFiles(entry.Id);

        Assert.Equal(CatalogDeletionOutcome.Deleted, result.Outcome);
        Assert.Empty(result.FilesLeft);
        Assert.False(Directory.Exists(catDir));
        Assert.DoesNotContain(new CatalogRegistry(RegistryPath(dir)).List(), c => c.Id == entry.Id);
    }

    [Fact]
    public void RemoveAndDeleteFiles_LeavesANestedCatalogAlone()
    {
        using var dir = new TempDir();
        var reg = new CatalogRegistry(RegistryPath(dir));
        var outer = dir.Combine("Aperture");
        using (new LibraryService(outer)) { }
        var outerEntry = reg.Add("Outer", outer);
        var nestedDir = Path.Combine(outer, "Catalogs", "Work");
        using (new LibraryService(nestedDir)) { }
        var nested = reg.Add("Work", nestedDir);

        reg.RemoveAndDeleteFiles(outerEntry.Id);

        Assert.Contains(reg.List(), c => c.Id == nested.Id);
        Assert.True(File.Exists(Path.Combine(nestedDir, ApertureDatabase.MetadataFileName)));
    }

    [Fact]
    public void RemoveAndDeleteFiles_WhenTheForgetCannotBeSaved_DeletesNothing()
    {
        using var dir = new TempDir();
        var path = RegistryPath(dir);
        var reg = new CatalogRegistry(path);
        reg.Add("Stay", dir.Combine("stay"));
        var catDir = dir.Combine("work");
        using (new LibraryService(catDir)) { }
        var work = reg.Add("Work", catDir);

        File.SetAttributes(path, FileAttributes.ReadOnly);
        CatalogDeletion result;
        try
        {
            result = reg.RemoveAndDeleteFiles(work.Id);
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }

        Assert.Equal(CatalogDeletionOutcome.NotForgotten, result.Outcome);
        Assert.True(File.Exists(Path.Combine(catDir, ApertureDatabase.MetadataFileName)));
        Assert.Contains(new CatalogRegistry(path).List(), c => c.Id == work.Id);
    }

    [Fact]
    public void RemoveAndDeleteFiles_ReportsFilesItCouldNotDelete()
    {
        using var dir = new TempDir();
        var reg = new CatalogRegistry(RegistryPath(dir));
        reg.Add("Stay", dir.Combine("stay"));
        var catDir = dir.Combine("busy");
        Directory.CreateDirectory(catDir);
        File.WriteAllText(Path.Combine(catDir, ApertureDatabase.MetadataFileName), "x");
        var settings = Path.Combine(catDir, "settings.json");
        File.WriteAllText(settings, "{}");
        var entry = reg.Add("Busy", catDir);

        CatalogDeletion result;
        using (new FileStream(settings, FileMode.Open, FileAccess.Read, FileShare.None)) // held open, e.g. by another app
            result = reg.RemoveAndDeleteFiles(entry.Id);

        Assert.Equal(CatalogDeletionOutcome.PartiallyDeleted, result.Outcome);
        Assert.Equal([settings], result.FilesLeft);
        Assert.False(File.Exists(Path.Combine(catDir, ApertureDatabase.MetadataFileName)));
        Assert.True(Directory.Exists(catDir));
    }

    [Fact]
    public void RemoveAndDeleteFiles_UnknownId_IsNotFound()
    {
        using var dir = new TempDir();
        var reg = new CatalogRegistry(RegistryPath(dir));
        reg.Add("One", dir.Combine("one"));

        Assert.Equal(CatalogDeletionOutcome.NotFound, reg.RemoveAndDeleteFiles("nope").Outcome);
    }

    // --- The Default catalog ------------------------------------------------------------------

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
        var mine = reg.Add("My Photos", defaultDir);
        reg.Add("Work", dir.Combine("work"));

        reg.EnsureDefault(defaultDir);
        reg.EnsureDefault(defaultDir);

        Assert.Equal(2, reg.List().Count);
        Assert.Equal("My Photos", reg.List().Single(c => c.Id == mine.Id).Name);
    }

    [Fact]
    public void EnsureDefault_AfterTheUserRemovedDefault_DoesNotBringItBack()
    {
        using var dir = new TempDir();
        var path = RegistryPath(dir);
        var defaultDir = dir.Combine("Aperture");
        var reg = new CatalogRegistry(path);
        reg.EnsureDefault(defaultDir);
        var work = reg.Add("Work", dir.Combine("work"));
        reg.SetActive(work.Id);
        reg.Remove(reg.List().Single(c => c.Name == "Default").Id);

        var nextLaunch = new CatalogRegistry(path);
        nextLaunch.EnsureDefault(defaultDir);

        Assert.Equal([work.Id], nextLaunch.List().Select(c => c.Id));
    }

    [Fact]
    public void EnsureDefault_WithNothingRegistered_ReSeedsDefault_EvenAfterTheFirstRun()
    {
        using var dir = new TempDir();
        var path = RegistryPath(dir);
        var defaultDir = dir.Combine("Aperture");
        var reg = new CatalogRegistry(path);
        reg.EnsureDefault(defaultDir);
        reg.Remove(reg.Active!.Id); // a library list must never be empty

        var nextLaunch = new CatalogRegistry(path);
        nextLaunch.EnsureDefault(defaultDir);

        Assert.Equal(Path.GetFullPath(defaultDir), Assert.Single(nextLaunch.List()).Dir);
        Assert.NotNull(nextLaunch.Active);
    }

    // --- Loading a missing, corrupt or hand-edited file --------------------------------------

    [Fact]
    public void Load_UnreadableFile_IsKeptAsABadCopy_NotOverwritten()
    {
        using var dir = new TempDir();
        var path = RegistryPath(dir);
        const string garbage = "{ this is not valid json ";
        File.WriteAllText(path, garbage);

        var reg = new CatalogRegistry(path);
        reg.EnsureDefault(dir.Combine("Aperture"));

        Assert.Single(reg.List());
        var bad = Assert.Single(Directory.GetFiles(dir.Path, "catalogs.json.bad-*"));
        Assert.Equal(garbage, File.ReadAllText(bad)); // the user's (or another app's) bytes survive
    }

    [Fact]
    public void Load_NullCatalogs_IsTolerated()
    {
        using var dir = new TempDir();
        var path = RegistryPath(dir);
        File.WriteAllText(path, "{\"catalogs\":null}");

        var reg = new CatalogRegistry(path);
        reg.EnsureDefault(dir.Combine("Aperture"));

        Assert.Single(reg.List());
    }

    [Fact]
    public void Load_DanglingActiveId_IsCleared_AndEnsureDefaultPicksARegisteredCatalog()
    {
        using var dir = new TempDir();
        var path = RegistryPath(dir);
        var work = dir.Combine("work");
        File.WriteAllText(path,
            $$"""{"catalogs":[{"id":"w","name":"Work","dir":{{JsonSerializer.Serialize(work)}}}],"activeId":"gone","defaultSeeded":true}""");

        var reg = new CatalogRegistry(path);
        Assert.Null(reg.Active);

        reg.EnsureDefault(dir.Combine("Aperture"));
        Assert.Equal("w", reg.Active!.Id);
    }

    [Fact]
    public void Load_DropsEntriesThatCannotBeUsed()
    {
        using var dir = new TempDir();
        var path = RegistryPath(dir);
        var good = dir.Combine("good");
        File.WriteAllText(path, $$"""
            {"catalogs":[
              {"id":"g","name":"Good","dir":{{JsonSerializer.Serialize(good)}}},
              {"id":"rel","name":"Relative","dir":"relative\\path"},
              {"id":"","name":"No id","dir":{{JsonSerializer.Serialize(dir.Combine("x"))}}},
              {"id":"nodir","name":"No dir","dir":""},
              {"id":"g","name":"Same id","dir":{{JsonSerializer.Serialize(dir.Combine("y"))}}},
              {"id":"dup","name":"Same dir","dir":{{JsonSerializer.Serialize(good.ToUpperInvariant())}}}
            ],"activeId":"g"}
            """);

        var reg = new CatalogRegistry(path);

        Assert.Equal(["g"], reg.List().Select(c => c.Id));
        Assert.Equal("g", reg.Active!.Id);
    }

    [Fact]
    public void Load_AcceptsHandEditedJson_CommentsTrailingCommasAndAnyCase()
    {
        using var dir = new TempDir();
        var path = RegistryPath(dir);
        var home = dir.Combine("home");
        File.WriteAllText(path, $$"""
            // my libraries
            {
              "Catalogs": [
                { "ID": "h", "Name": "Home", "Dir": {{JsonSerializer.Serialize(home)}}, },
              ],
              "activeid": "h",
            }
            """);

        var reg = new CatalogRegistry(path);

        Assert.Equal("Home", Assert.Single(reg.List()).Name);
        Assert.Equal("h", reg.Active!.Id);
    }

    [Fact]
    public void Save_WritesThenRenames_SoAFailedWriteLeavesTheExistingFileIntact()
    {
        using var dir = new TempDir();
        var path = RegistryPath(dir);
        var reg = new CatalogRegistry(path);
        var one = reg.Add("One", dir.Combine("one"));

        Directory.CreateDirectory(path + ".tmp"); // the temp file can't be written
        reg.Rename(one.Id, "Two");

        Assert.Equal("One", new CatalogRegistry(path).List().Single().Name); // the good file was not touched
    }

    [Fact]
    public void TwoInstances_DoNotLoseEachOthersChanges()
    {
        using var dir = new TempDir();
        var path = RegistryPath(dir);
        var a = new CatalogRegistry(path);
        a.EnsureDefault(dir.Combine("Aperture"));
        var b = new CatalogRegistry(path); // a second Aperture window, loaded before a's next change

        a.Add("Work", dir.Combine("work"));
        b.Rename(b.Active!.Id, "Home");

        var onDisk = new CatalogRegistry(path).List();
        Assert.Equal(2, onDisk.Count);
        Assert.Contains(onDisk, c => c.Name == "Home");
        Assert.Contains(onDisk, c => c.Name == "Work");
    }

    [Fact]
    public void AnotherWindow_RemovingTheLibraryThisWindowHasOpen_DoesNotUnlistItHere()
    {
        using var dir = new TempDir();
        var path = RegistryPath(dir);
        var a = new CatalogRegistry(path);
        a.EnsureDefault(dir.Combine("Aperture")); // window A has Default open
        var work = a.Add("Work", dir.Combine("work"));
        var b = new CatalogRegistry(path);
        b.SetActive(work.Id);                     // window B is on Work…
        b.Remove(a.Active!.Id);                   // …and removes Default, which A still has open

        a.Rename(work.Id, "Office");              // A's next change re-reads the list

        Assert.Equal("Default", a.Active!.Name);
        Assert.Contains(new CatalogRegistry(path).List(), c => c.Name == "Default");
    }
}
