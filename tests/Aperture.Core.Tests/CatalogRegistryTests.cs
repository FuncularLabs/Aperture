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
    public void RemoveAndDeleteFiles_OfALibraryOpenAnywhere_DeletesNothing()
    {
        using var dir = new TempDir();
        var reg = new CatalogRegistry(RegistryPath(dir));
        reg.Add("Stay", dir.Combine("stay"));
        var catDir = dir.Combine("open");
        using var open = new LibraryService(catDir); // open in some window, by some path
        open.Settings.Update(s => s.DefaultZoom = 3);
        var entry = reg.Add("Open", catDir);

        var result = reg.RemoveAndDeleteFiles(entry.Id);

        Assert.Equal(CatalogDeletionOutcome.PartiallyDeleted, result.Outcome);
        Assert.Contains(Path.Combine(catDir, ApertureDatabase.MetadataFileName), result.FilesLeft);
        Assert.True(File.Exists(Path.Combine(catDir, ApertureDatabase.MetadataFileName)));
        Assert.True(File.Exists(Path.Combine(catDir, "settings.json")));
        Assert.Empty(open.GetUnion()); // the open library still works
    }

    [Fact]
    public void RemoveAndDeleteFiles_AlsoRemovesTheLockFile_SoNothingOfApertureIsLeft()
    {
        using var dir = new TempDir();
        var reg = new CatalogRegistry(RegistryPath(dir));
        reg.Add("Stay", dir.Combine("stay"));
        var catDir = dir.Combine("closed");
        using (new LibraryService(catDir)) { } // leaves its (released) lock file behind
        Assert.True(File.Exists(Path.Combine(catDir, ApertureDatabase.LockFileName)));
        File.WriteAllText(Path.Combine(catDir, "mine.txt"), "keep");
        var entry = reg.Add("Closed", catDir);

        reg.RemoveAndDeleteFiles(entry.Id);

        Assert.Equal(["mine.txt"], Directory.GetFiles(catDir).Select(Path.GetFileName));
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

    [Theory]
    [InlineData("""{"settings":{"theme":"dark"}}""")]  // another program's file that happens to have this name
    [InlineData("""{"catalog":[{"id":"x"}]}""")]       // a hand edit with a misspelled key
    [InlineData("null")]
    public void Load_ValidJsonThatIsNotALibraryList_IsKeptAsABadCopy_NotOverwritten(string json)
    {
        using var dir = new TempDir();
        var path = RegistryPath(dir);
        File.WriteAllText(path, json);

        var reg = new CatalogRegistry(path);
        reg.EnsureDefault(dir.Combine("Aperture"));

        Assert.NotNull(reg.QuarantinedFile);
        Assert.Equal(json, File.ReadAllText(reg.QuarantinedFile!));
        Assert.Single(reg.List());
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

    // --- A change that can't be saved is refused, not kept in memory and lost later -----------

    [Fact]
    public void Add_WhenTheListCannotBeSaved_Throws_AndAddsNothing()
    {
        using var dir = new TempDir();
        var path = RegistryPath(dir);
        var reg = new CatalogRegistry(path);
        reg.Add("One", dir.Combine("one"));

        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            Assert.Throws<IOException>(() => reg.Add("Two", dir.Combine("two")));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }

        Assert.Single(reg.List());
        Assert.Single(new CatalogRegistry(path).List());
    }

    [Fact]
    public void Rename_WhenTheListCannotBeSaved_ReturnsFalse_AndKeepsTheOldName()
    {
        using var dir = new TempDir();
        var path = RegistryPath(dir);
        var reg = new CatalogRegistry(path);
        var one = reg.Add("One", dir.Combine("one"));

        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            Assert.False(reg.Rename(one.Id, "Home"));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }

        Assert.Equal("One", reg.List().Single().Name);
        reg.Add("Two", dir.Combine("two")); // a later change doesn't resurrect or lose anything
        Assert.Equal(["One", "Two"], new CatalogRegistry(path).List().Select(c => c.Name));
    }

    [Fact]
    public void Save_WaitsOutAnotherWindowBrieflyReadingTheFile()
    {
        using var dir = new TempDir();
        var path = RegistryPath(dir);
        var reg = new CatalogRegistry(path);
        reg.Add("One", dir.Combine("one"));

        var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); // blocks the rename
        var release = new Thread(() => { Thread.Sleep(50); reader.Dispose(); });           // no thread-pool delays
        release.Start();
        var two = reg.Add("Two", dir.Combine("two"));
        release.Join();

        Assert.Contains(new CatalogRegistry(path).List(), c => c.Id == two.Id);
    }

    [Fact]
    public async Task AChangeWhileTheListCantBeRead_NeverSavesOverIt()
    {
        using var dir = new TempDir();
        var path = RegistryPath(dir);
        var a = new CatalogRegistry(path);
        a.Add("One", dir.Combine("one"));
        new CatalogRegistry(path).Add("Two", dir.Combine("two")); // another window's addition; A hasn't seen it

        var locker = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); // a backup tool, say
        var release = Task.Delay(260).ContinueWith(_ => locker.Dispose());
        Record.Exception(() => a.Add("Three", dir.Combine("three"))); // refused, or merged if the lock lifted in time
        await release;

        Assert.Contains("Two", new CatalogRegistry(path).List().Select(c => c.Name)); // never overwritten
    }

    [Fact]
    public void AListThatCouldntBeRead_IsNotOverwritten_EvenIfItBecomesWritableBeforeTheSave()
    {
        using var dir = new TempDir();
        var path = RegistryPath(dir);
        var a = new CatalogRegistry(path);
        a.Add("One", dir.Combine("one"));
        new CatalogRegistry(path).Add("Two", dir.Combine("two")); // another window's addition; A hasn't seen it

        var locker = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); // the read gives up…
        a.BetweenReadAndChange = locker.Dispose;                                               // …then the lock lifts
        Assert.Throws<IOException>(() => a.Add("Three", dir.Combine("three")));
        a.BetweenReadAndChange = null;

        Assert.Equal(["One", "Two"], new CatalogRegistry(path).List().Select(c => c.Name)); // not [One, Three]
    }

    [Fact]
    public void EnsureDefault_AfterAFailedRead_NeverSavesOverTheList()
    {
        using var dir = new TempDir();
        var path = RegistryPath(dir);
        var a = new CatalogRegistry(path);
        a.Add("One", dir.Combine("one"));
        new CatalogRegistry(path).Add("Two", dir.Combine("two"));

        var locker = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        a.BetweenReadAndChange = locker.Dispose; // the read gives up, then the lock lifts before the save
        a.EnsureDefault(dir.Combine("default"));
        a.BetweenReadAndChange = null;

        Assert.Equal(["One", "Two"], new CatalogRegistry(path).List().Select(c => c.Name));
    }

    [Fact]
    public void Remove_AfterAFailedRead_NeverSavesOverTheList()
    {
        using var dir = new TempDir();
        var path = RegistryPath(dir);
        var a = new CatalogRegistry(path);
        var one = a.Add("One", dir.Combine("one"));
        new CatalogRegistry(path).Add("Two", dir.Combine("two"));

        var locker = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        a.BetweenReadAndChange = locker.Dispose;
        Assert.False(a.Remove(one.Id));
        a.BetweenReadAndChange = null;

        Assert.Equal(["One", "Two"], new CatalogRegistry(path).List().Select(c => c.Name));
    }

    [Fact]
    public void AChange_WaitsOutABriefReadLock_AndMergesAsUsual()
    {
        using var dir = new TempDir();
        var path = RegistryPath(dir);
        var a = new CatalogRegistry(path);
        a.Add("One", dir.Combine("one"));
        new CatalogRegistry(path).Add("Two", dir.Combine("two"));

        var locker = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var release = new Thread(() => { Thread.Sleep(50); locker.Dispose(); }); // no thread-pool delays
        release.Start();
        a.Add("Three", dir.Combine("three")); // the read retries past the brief lock instead of refusing
        release.Join();

        Assert.Equal(["One", "Two", "Three"], new CatalogRegistry(path).List().Select(c => c.Name));
    }

    [Fact]
    public void AListThisUserMayNotRead_IsTreatedAsUnreadable_NotAsMissing()
    {
        using var dir = new TempDir();
        var path = RegistryPath(dir);
        new CatalogRegistry(path).Add("One", dir.Combine("one"));
        Acl.DenyRead(path);
        try
        {
            var reg = new CatalogRegistry(path);
            Assert.True(reg.ReadFailed);
            reg.EnsureDefault(dir.Combine("default")); // in memory only: nothing saved over a list it couldn't read
        }
        finally
        {
            Acl.Restore(path);
        }

        Assert.Equal(["One"], new CatalogRegistry(path).List().Select(c => c.Name));
    }

    [Fact]
    public void ALoadThatCouldntRead_ThenARereadThatCan_OpensTheUsersLibrary_NotDefault()
    {
        using var dir = new TempDir();
        var path = RegistryPath(dir);
        var first = new CatalogRegistry(path);
        first.EnsureDefault(dir.Combine("default"));
        var work = first.Add("Work", dir.Combine("work"));
        first.SetActive(work.Id);

        CatalogRegistry launch;
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            launch = new CatalogRegistry(path);      // the constructor's read fails…
        launch.EnsureDefault(dir.Combine("default")); // …but the launch's next read succeeds

        Assert.False(launch.ReadFailed);
        Assert.Equal(work.Id, launch.Active!.Id);
    }

    [Fact]
    public void AnUnreadableListThatCantBeSetAside_IsNeverSavedOver()
    {
        using var dir = new TempDir();
        var path = RegistryPath(dir);
        File.WriteAllText(path, "{ broken");
        var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); // readable, but can't be moved

        var reg = new CatalogRegistry(path);
        Assert.True(reg.ReadFailed);
        Assert.Null(reg.QuarantinedFile);
        reg.BetweenReadAndChange = holder.Dispose; // movable and writable again just before the save
        Assert.Throws<IOException>(() => reg.Add("One", dir.Combine("one")));
        reg.BetweenReadAndChange = null;

        Assert.Equal("{ broken", File.ReadAllText(path)); // the bytes it couldn't set aside are still there
    }

    private static Mutex ARegistryLockOnlyAdminsMayOpen(CatalogRegistry reg)
    {
        var adminsOnly = new System.Security.AccessControl.MutexSecurity();
        adminsOnly.AddAccessRule(new System.Security.AccessControl.MutexAccessRule(
            new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null),
            System.Security.AccessControl.MutexRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
        return System.Threading.MutexAcl.Create(false, reg.MutexName, out _, adminsOnly); // as an elevated window's change makes it
    }

    [Fact]
    public void ARegistryLockThisWindowMayNotOpen_IsWaitedFor_LikeAnyHeldLock()
    {
        using var dir = new TempDir();
        var reg = new CatalogRegistry(RegistryPath(dir)) { LockTimeout = TimeSpan.FromSeconds(3) };
        var elevatedWindowsChange = ARegistryLockOnlyAdminsMayOpen(reg);
        var finishes = new Thread(() => { Thread.Sleep(300); elevatedWindowsChange.Dispose(); }); // its change ends
        finishes.Start();

        var clock = System.Diagnostics.Stopwatch.StartNew();
        reg.Add("One", dir.Combine("one"));
        clock.Stop();
        finishes.Join();

        Assert.True(clock.ElapsedMilliseconds >= 200, $"went ahead after {clock.ElapsedMilliseconds} ms instead of waiting");
        Assert.Single(new CatalogRegistry(RegistryPath(dir)).List());
    }

    [Fact]
    public void ARegistryLockThisWindowMayNotOpen_StillGoesAheadAfterTheTimeout()
    {
        using var dir = new TempDir();
        var reg = new CatalogRegistry(RegistryPath(dir)) { LockTimeout = TimeSpan.FromMilliseconds(150) };
        using var neverEnds = ARegistryLockOnlyAdminsMayOpen(reg);

        reg.Add("One", dir.Combine("one")); // goes ahead without the lock rather than hang or fail

        Assert.Single(new CatalogRegistry(RegistryPath(dir)).List());
    }

    [Fact]
    public void RemoveAndDeleteFiles_WhenTheFolderGoesAwayMidDelete_SaysSo_InsteadOfReportingSuccess()
    {
        using var dir = new TempDir();
        var reg = new CatalogRegistry(RegistryPath(dir));
        reg.Add("Stay", dir.Combine("stay"));
        var catDir = dir.Combine("usb");
        using (new LibraryService(catDir)) { }
        var entry = reg.Add("USB", catDir);
        reg.BetweenReadAndChange = () => Directory.Move(catDir, dir.Combine("usb-unplugged")); // unplugged mid-delete

        var result = reg.RemoveAndDeleteFiles(entry.Id);
        reg.BetweenReadAndChange = null;

        Assert.Equal(CatalogDeletionOutcome.PartiallyDeleted, result.Outcome);
        Assert.Equal([catDir], result.FilesLeft);
        Assert.True(File.Exists(Path.Combine(dir.Combine("usb-unplugged"), ApertureDatabase.MetadataFileName)));
    }

    [Fact]
    public void RemoveAndDeleteFiles_ListsTheLockFileAsLeftOnlyIfThereIsOne()
    {
        using var dir = new TempDir();
        var reg = new CatalogRegistry(RegistryPath(dir));
        reg.Add("Stay", dir.Combine("stay"));
        var catDir = dir.Combine("readonly");
        using (new LibraryService(catDir)) { }
        File.Delete(Path.Combine(catDir, ApertureDatabase.LockFileName));
        var entry = reg.Add("Read-only", catDir);
        Acl.DenyCreateFiles(catDir); // the delete can't create the lock file to take it
        CatalogDeletion result;
        try
        {
            result = reg.RemoveAndDeleteFiles(entry.Id);
        }
        finally
        {
            Acl.RestoreCreateFiles(catDir);
        }

        Assert.Equal(CatalogDeletionOutcome.PartiallyDeleted, result.Outcome);
        Assert.Contains(Path.Combine(catDir, ApertureDatabase.MetadataFileName), result.FilesLeft);
        Assert.DoesNotContain(Path.Combine(catDir, ApertureDatabase.LockFileName), result.FilesLeft);
    }

    [Fact]
    public void RemoveAndDeleteFiles_WhenTheFolderCantBeReached_ForgetsNothing_AndSaysSo()
    {
        using var dir = new TempDir();
        var reg = new CatalogRegistry(RegistryPath(dir));
        reg.Add("Stay", dir.Combine("stay"));
        var catDir = dir.Combine("usb");
        using (new LibraryService(catDir)) { }
        var entry = reg.Add("USB", catDir);
        Directory.Move(catDir, dir.Combine("usb-unplugged"));

        var result = reg.RemoveAndDeleteFiles(entry.Id);

        Assert.Equal(CatalogDeletionOutcome.FolderNotFound, result.Outcome);
        Assert.Contains(reg.List(), c => c.Id == entry.Id); // still listed: nothing was deleted, so nothing is forgotten
    }

    [Fact]
    public void WhileTheListCantBeRead_ChangesAreRefused_AndReadFailedSaysWhy()
    {
        using var dir = new TempDir();
        var path = RegistryPath(dir);
        var a = new CatalogRegistry(path);
        var one = a.Add("One", dir.Combine("one"));

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Throws<IOException>(() => a.Add("Two", dir.Combine("two")));
            Assert.True(a.ReadFailed);
            Assert.False(a.Rename(one.Id, "First"));
        }

        Assert.Equal(["One"], new CatalogRegistry(path).List().Select(c => c.Name));
        a.Rename(one.Id, "First"); // readable again: changes go through
        Assert.False(a.ReadFailed);
        Assert.Equal(["First"], new CatalogRegistry(path).List().Select(c => c.Name));
    }

    // --- The cross-process lock ---------------------------------------------------------------

    [Fact]
    public async Task Changes_WaitForTheCrossProcessLock()
    {
        using var dir = new TempDir();
        var reg = new CatalogRegistry(RegistryPath(dir));
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            using var mutex = new Mutex(false, reg.MutexName); // another Aperture window mid-change
            mutex.WaitOne();
            held.Set();
            release.Wait();
            mutex.ReleaseMutex();
        });
        holder.Start();
        held.Wait();

        var adding = Task.Run(() => reg.Add("One", dir.Combine("one")));
        await Task.Delay(200);
        Assert.False(adding.IsCompleted);

        release.Set();
        await adding;
        holder.Join();
        Assert.Single(reg.List());
    }

    [Fact]
    public async Task Loading_WaitsForTheCrossProcessLock_SoItNeverReadsAHalfReplacedFile()
    {
        using var dir = new TempDir();
        var path = RegistryPath(dir);
        var first = new CatalogRegistry(path);
        first.Add("One", dir.Combine("one"));
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            using var mutex = new Mutex(false, first.MutexName); // another window is mid-change
            mutex.WaitOne();
            held.Set();
            release.Wait();
            mutex.ReleaseMutex();
        });
        holder.Start();
        held.Wait();

        var loading = Task.Run(() => new CatalogRegistry(path));
        await Task.Delay(200);
        Assert.False(loading.IsCompleted);

        release.Set();
        var loaded = await loading;
        holder.Join();
        Assert.Single(loaded.List());
    }

    [Fact]
    public async Task Changes_GoAheadAfterTheLockTimeout_RatherThanHang()
    {
        using var dir = new TempDir();
        var reg = new CatalogRegistry(RegistryPath(dir)) { LockTimeout = TimeSpan.FromMilliseconds(150) };
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            using var mutex = new Mutex(false, reg.MutexName);
            mutex.WaitOne();
            held.Set();
            release.Wait();
            mutex.ReleaseMutex();
        });
        holder.Start();
        held.Wait();

        var adding = Task.Run(() => reg.Add("One", dir.Combine("one")));
        var finished = await Task.WhenAny(adding, Task.Delay(5000));

        release.Set();
        holder.Join();
        Assert.Same(adding, finished);
        Assert.Single(reg.List());
    }

    [Fact]
    public void Changes_GoAheadWhenAnotherWindowDiedHoldingTheLock()
    {
        using var dir = new TempDir();
        var reg = new CatalogRegistry(RegistryPath(dir));
        var dead = new Thread(() => new Mutex(false, reg.MutexName).WaitOne()); // exits without releasing
        dead.Start();
        dead.Join();

        reg.Add("One", dir.Combine("one"));

        Assert.Single(new CatalogRegistry(RegistryPath(dir)).List());
    }

    // --- Which library opens next launch ------------------------------------------------------

    [Fact]
    public void ActivateForThisSessionOnly_DoesNotChangeWhichLibraryOpensNextLaunch()
    {
        using var dir = new TempDir();
        var path = RegistryPath(dir);
        var reg = new CatalogRegistry(path);
        var usb = reg.Add("USB", dir.Combine("usb"));
        var local = reg.Add("Local", dir.Combine("local"));
        reg.SetActive(usb.Id);

        reg.Activate(local, rememberForNextLaunch: false); // a launch fallback while the USB drive is away
        reg.Rename(local.Id, "Here");                      // and a later save in the same session

        Assert.Equal(local.Id, reg.Active!.Id);
        Assert.Equal(usb.Id, new CatalogRegistry(path).Active!.Id);
    }

    [Fact]
    public void Activate_ListsALibraryAgain_IfAnotherWindowRemovedIt()
    {
        using var dir = new TempDir();
        var path = RegistryPath(dir);
        var a = new CatalogRegistry(path);
        a.Add("One", dir.Combine("one"));
        var two = a.Add("Two", dir.Combine("two"));
        new CatalogRegistry(path).Remove(two.Id); // another window

        a.Activate(two, rememberForNextLaunch: true); // this window switches to it from its (older) menu

        Assert.Equal(two.Id, a.Active!.Id);
        Assert.Contains(new CatalogRegistry(path).List(), c => c.Id == two.Id);
    }

    [Fact]
    public void AnotherWindowsSwitch_IsWhatTheNextLaunchOpens_EvenAfterThisWindowSaves()
    {
        using var dir = new TempDir();
        var path = RegistryPath(dir);
        var a = new CatalogRegistry(path);
        var one = a.Add("One", dir.Combine("one"));
        var two = a.Add("Two", dir.Combine("two"));
        var b = new CatalogRegistry(path);

        b.SetActive(two.Id);       // the other window switched last…
        a.Rename(one.Id, "First"); // …then this window saved an unrelated change

        Assert.Equal(one.Id, a.Active!.Id);                         // this window still shows its own
        Assert.Equal(two.Id, new CatalogRegistry(path).Active!.Id); // the latest switch wins next launch
    }

    [Fact]
    public void AnotherWindow_ReAddingThisWindowsOpenFolderUnderANewId_KeepsItActiveHere()
    {
        using var dir = new TempDir();
        var path = RegistryPath(dir);
        var a = new CatalogRegistry(path);
        var one = a.Add("One", dir.Combine("one"));
        var two = a.Add("Two", dir.Combine("two"));
        var b = new CatalogRegistry(path);
        b.SetActive(two.Id);
        b.Remove(one.Id);
        var readded = b.Add("One again", dir.Combine("one")); // same folder, new id

        a.Rename(two.Id, "Second"); // this window's next change re-reads the list

        Assert.Equal(readded.Id, a.Active!.Id); // adopts the folder's new id instead of losing its open library
        Assert.Single(a.List(), c => CatalogRegistry.SameDir(c.Dir, one.Dir));
    }

    [Fact]
    public void Load_UnreadableFile_IsReportedAsQuarantined()
    {
        using var dir = new TempDir();
        var path = RegistryPath(dir);
        File.WriteAllText(path, "{ broken");

        var reg = new CatalogRegistry(path);

        Assert.NotNull(reg.QuarantinedFile);
        Assert.True(File.Exists(reg.QuarantinedFile));
        Assert.Null(new CatalogRegistry(RegistryPath(dir)).QuarantinedFile); // nothing left to set aside
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
