using System.Text.Json;
using Aperture.Core.Library;
using Aperture.Core.Storage;
using Aperture.Core.Tests.Support;

namespace Aperture.Core.Tests;

/// <summary>
/// Catalog switch orchestration (Option B). CatalogService owns the registry and the one live
/// <see cref="LibraryService"/>. Invariants: <c>Current</c> always corresponds to <c>ActiveCatalog</c>;
/// a switch never leaves the app on a closed library; nothing the user owns is ever deleted.
/// </summary>
public class CatalogServiceTests
{
    private static string RegistryPath(TempDir dir) => dir.Combine("catalogs.json");

    /// <summary>Creates a real library on disk (aperture.db) and registers it, without switching.</summary>
    private static CatalogEntry AddLibrary(CatalogService service, string name, string dir)
    {
        using (new LibraryService(dir)) { }
        return service.Registry.Add(name, dir);
    }

    private static string WriteNotADatabase(string dir)
    {
        Directory.CreateDirectory(dir);
        var db = Path.Combine(dir, ApertureDatabase.MetadataFileName);
        File.WriteAllText(db, new string('x', 512)); // an aperture.db SQLite can't read
        return db;
    }

    // --- Opening at launch ------------------------------------------------------------------

    [Fact]
    public async Task Open_EmptyRegistry_RegistersDefault_AndOpensIt()
    {
        using var outer = new TempDir();
        var defaultDir = outer.Combine("default");

        await using var service = CatalogService.Open(RegistryPath(outer), defaultDir);

        Assert.Equal("Default", service.ActiveCatalog!.Name);
        Assert.Equal(Path.GetFullPath(defaultDir), service.ActiveCatalog!.Dir);
        Assert.Null(service.OpenWarning);
        Assert.Empty(service.Current.GetUnion());
    }

    [Fact]
    public async Task Open_ActiveLibraryUnavailable_FallsBackToDefault_OnEveryLaunch_AndSaysWhy()
    {
        using var outer = new TempDir();
        var regPath = RegistryPath(outer);
        var defaultDir = outer.Combine("default");
        string usbId;
        await using (var first = CatalogService.Open(regPath, defaultDir))
        {
            var usb = AddLibrary(first, "USB", outer.Combine("usb"));
            await first.SwitchToAsync(usb.Id);
            usbId = usb.Id;
        }
        Directory.Delete(outer.Combine("usb"), recursive: true); // the drive was unplugged

        for (var launch = 0; launch < 2; launch++)
        {
            await using var service = CatalogService.Open(regPath, defaultDir);
            Assert.Equal("Default", service.ActiveCatalog!.Name);
            Assert.Empty(service.Current.GetUnion());
            if (launch == 0)
                Assert.Contains("USB", service.OpenWarning);
            Assert.Contains(service.Registry.List(), c => c.Id == usbId); // still listed, so it can come back
        }
        Assert.False(Directory.Exists(outer.Combine("usb"))); // never silently recreated as an empty library
    }

    [Fact]
    public async Task Open_PrefersTheMostRecentlyOpenedLibrary_WhenDefaultIsGone()
    {
        using var outer = new TempDir();
        var regPath = RegistryPath(outer);
        var defaultDir = outer.Combine("default");
        string recentId;
        await using (var first = CatalogService.Open(regPath, defaultDir))
        {
            // Registration order (Older, Recent) differs from recency (Recent opened last), so a
            // fallback that just took the first registered library would pick the wrong one.
            var older = AddLibrary(first, "Older", outer.Combine("older"));
            var recent = AddLibrary(first, "Recent", outer.Combine("recent"));
            var travel = AddLibrary(first, "Travel", outer.Combine("travel"));
            await first.SwitchToAsync(older.Id);
            await first.SwitchToAsync(recent.Id);
            await first.RemoveAsync(first.Registry.List().Single(c => c.Name == "Default").Id);
            await first.SwitchToAsync(travel.Id);
            recentId = recent.Id;
        }
        Directory.Delete(outer.Combine("travel"), recursive: true);

        await using var service = CatalogService.Open(regPath, defaultDir);

        Assert.Equal(recentId, service.ActiveCatalog!.Id);
        Assert.Contains("Travel", service.OpenWarning);
    }

    [Fact]
    public async Task Open_NothingRegisteredOpens_ReRegistersDefault()
    {
        using var outer = new TempDir();
        var regPath = RegistryPath(outer);
        var defaultDir = outer.Combine("default");
        await using (var first = CatalogService.Open(regPath, defaultDir))
        {
            var work = AddLibrary(first, "Work", outer.Combine("work"));
            await first.SwitchToAsync(work.Id);
            await first.RemoveAsync(first.Registry.List().Single(c => c.Name == "Default").Id);
        }
        Directory.Delete(outer.Combine("work"), recursive: true);

        await using var service = CatalogService.Open(regPath, defaultDir);

        Assert.Equal(Path.GetFullPath(defaultDir), service.ActiveCatalog!.Dir);
        Assert.Contains("Work", service.OpenWarning);
    }

    [Fact]
    public async Task Open_DanglingActiveId_StillStarts()
    {
        using var outer = new TempDir();
        var regPath = RegistryPath(outer);
        File.WriteAllText(regPath, """{"catalogs":[],"activeId":"gone","defaultSeeded":true}""");

        await using var service = CatalogService.Open(regPath, outer.Combine("default"));

        Assert.Equal("Default", service.ActiveCatalog!.Name);
    }

    // --- Switching ----------------------------------------------------------------------------

    [Fact]
    public async Task SwitchToAsync_IsolatesCatalogs_BothDirections()
    {
        using var outer = new TempDir();
        using var libA = new TempDir();
        TestImages.Write(Path.Combine(libA.Path, "a.jpg"), 128, 128);

        await using var service = CatalogService.Open(RegistryPath(outer), outer.Combine("default"));
        var defaultId = service.ActiveCatalog!.Id;
        await service.Current.IndexRootAsync(service.Current.AddRoot(libA.Path, "A"));
        Assert.Single(service.Current.GetUnion());

        var work = AddLibrary(service, "Work", outer.Combine("work"));
        await service.SwitchToAsync(work.Id);
        Assert.Empty(service.Current.GetUnion());

        await service.SwitchToAsync(defaultId);
        Assert.Single(service.Current.GetUnion());
    }

    [Fact]
    public async Task SwitchToAsync_UpdatesActive_AndRaisesEventsInOrder()
    {
        using var outer = new TempDir();
        await using var service = CatalogService.Open(RegistryPath(outer), outer.Combine("default"));
        var work = AddLibrary(service, "Work", outer.Combine("work"));

        var events = new List<string>();
        service.PreparingToSwitch += (_, _) => events.Add("preparing");
        service.ActiveLibraryChanged += (_, _) => events.Add("changed");

        await service.SwitchToAsync(work.Id);

        Assert.Equal(work.Id, service.ActiveCatalog!.Id);
        Assert.Equal(["preparing", "changed"], events);
    }

    [Fact]
    public async Task SwitchToAsync_RaisesBothEventsOnTheCallersContext_WhileAnIndexIsRunning()
    {
        using var outer = new TempDir();
        using var lib = new TempDir();
        TestImages.Write(Path.Combine(lib.Path, "a.jpg"), 64, 64);
        await using var service = CatalogService.Open(RegistryPath(outer), outer.Combine("default"));
        var work = AddLibrary(service, "Work", outer.Combine("work"));
        var held = new BlockingProgress();
        var indexing = service.Current.IndexRootAsync(service.Current.AddRoot(lib.Path, "L"), held);
        Assert.True(held.Entered.Wait(5000));

        using var ui = new SingleThreadContext();
        int? preparing = null, changed = null;
        service.PreparingToSwitch += (_, _) => preparing = Environment.CurrentManagedThreadId;
        service.ActiveLibraryChanged += (_, _) => changed = Environment.CurrentManagedThreadId;

        var switching = ui.Run(() => service.SwitchToAsync(work.Id));
        await Task.Delay(200);
        Assert.False(switching.IsCompleted); // waiting for the held index to stop
        held.Release();
        await switching;
        await Record.ExceptionAsync(() => indexing);

        Assert.Equal(ui.ThreadId, preparing);
        Assert.Equal(ui.ThreadId, changed); // the host rebinds its window in this handler
    }

    [Fact]
    public async Task SwitchToAsync_SameCatalog_IsNoOp_NoEvents()
    {
        using var outer = new TempDir();
        await using var service = CatalogService.Open(RegistryPath(outer), outer.Combine("default"));
        var current = service.Current;
        var raised = false;
        service.PreparingToSwitch += (_, _) => raised = true;
        service.ActiveLibraryChanged += (_, _) => raised = true;

        await service.SwitchToAsync(service.ActiveCatalog!.Id);

        Assert.Same(current, service.Current);
        Assert.False(raised);
    }

    [Fact]
    public async Task SwitchToAsync_UnknownId_Throws_AndKeepsCurrent()
    {
        using var outer = new TempDir();
        await using var service = CatalogService.Open(RegistryPath(outer), outer.Combine("default"));
        var current = service.Current;
        var activeId = service.ActiveCatalog!.Id;

        await Assert.ThrowsAsync<ArgumentException>(() => service.SwitchToAsync("nope"));

        Assert.Same(current, service.Current);
        Assert.Equal(activeId, service.ActiveCatalog!.Id);
    }

    [Fact]
    public async Task SwitchToAsync_TargetThatCannotOpen_LeavesTheCurrentLibraryLive_RaisesNothing_AndReleasesIt()
    {
        using var outer = new TempDir();
        await using var service = CatalogService.Open(RegistryPath(outer), outer.Combine("default"));
        var current = service.Current;
        var activeId = service.ActiveCatalog!.Id;
        var badDb = WriteNotADatabase(outer.Combine("bad"));
        var bad = service.Registry.Add("Bad", outer.Combine("bad"));
        var raised = false;
        service.PreparingToSwitch += (_, _) => raised = true;
        service.ActiveLibraryChanged += (_, _) => raised = true;

        await Assert.ThrowsAnyAsync<Exception>(() => service.SwitchToAsync(bad.Id));

        Assert.Same(current, service.Current);
        Assert.Equal(activeId, service.ActiveCatalog!.Id);
        Assert.False(raised);
        Assert.Empty(service.Current.GetUnion()); // opened first, so the current library was never torn down
        File.Delete(badDb);                       // and the failed open let go of the bad file
    }

    [Fact]
    public async Task SwitchToAsync_LibraryWhoseFolderIsGone_IsRefused_NotRecreatedEmpty()
    {
        using var outer = new TempDir();
        await using var service = CatalogService.Open(RegistryPath(outer), outer.Combine("default"));
        var work = AddLibrary(service, "Work", outer.Combine("work"));
        Directory.Delete(work.Dir, recursive: true);

        var ex = await Assert.ThrowsAsync<CatalogException>(() => service.SwitchToAsync(work.Id));

        Assert.Contains(work.Dir, ex.Message);
        Assert.False(Directory.Exists(work.Dir));
        Assert.Equal("Default", service.ActiveCatalog!.Name);
    }

    [Fact]
    public async Task SwitchToAsync_TargetIsAFile_IsRefused_AndKeepsCurrent()
    {
        using var outer = new TempDir();
        await using var service = CatalogService.Open(RegistryPath(outer), outer.Combine("default"));
        var current = service.Current;
        var filePath = outer.Combine("not-a-dir");
        File.WriteAllText(filePath, "x");
        var bad = service.Registry.Add("Bad", filePath);

        await Assert.ThrowsAsync<CatalogException>(() => service.SwitchToAsync(bad.Id));

        Assert.Same(current, service.Current);
    }

    [Fact]
    public async Task SwitchToAsync_BackToADefaultWhoseFolderIsGone_RecreatesIt()
    {
        using var outer = new TempDir();
        var defaultDir = outer.Combine("default");
        await using var service = CatalogService.Open(RegistryPath(outer), defaultDir);
        var defaultId = service.ActiveCatalog!.Id;
        var work = AddLibrary(service, "Work", outer.Combine("work"));
        await service.SwitchToAsync(work.Id);
        Directory.Delete(defaultDir, recursive: true);

        await service.SwitchToAsync(defaultId); // the local default is always available

        Assert.Equal(defaultId, service.ActiveCatalog!.Id);
        Assert.True(File.Exists(Path.Combine(defaultDir, ApertureDatabase.MetadataFileName)));
    }

    [Fact]
    public async Task ConcurrentSwitches_RunOneAtATime_TheLastRequestWins_AndNoLibraryIsLeaked()
    {
        using var outer = new TempDir();
        using var lib = new TempDir();
        TestImages.Write(Path.Combine(lib.Path, "a.jpg"), 64, 64);
        var service = CatalogService.Open(RegistryPath(outer), outer.Combine("default"));
        var a = AddLibrary(service, "A", outer.Combine("a"));
        var b = AddLibrary(service, "B", outer.Combine("b"));
        var held = new BlockingProgress();
        var indexing = service.Current.IndexRootAsync(service.Current.AddRoot(lib.Path, "L"), held);
        Assert.True(held.Entered.Wait(5000));
        var opened = new List<LibraryService>();
        service.ActiveLibraryChanged += (_, _) => { lock (opened) opened.Add(service.Current); };

        var first = service.SwitchToAsync(a.Id);
        var second = service.SwitchToAsync(b.Id);
        held.Release();
        await Task.WhenAll(first, second);
        await Record.ExceptionAsync(() => indexing);

        Assert.Equal(b.Id, service.ActiveCatalog!.Id);
        Assert.Equal(2, opened.Count);
        await service.DisposeAsync();
        Directory.Delete(a.Dir, recursive: true); // A's library was shut down by the switch to B
        Directory.Delete(b.Dir, recursive: true);
    }

    // --- New library: a dedicated folder ------------------------------------------------------

    [Fact]
    public async Task CreateLibraryAsync_CreatesItsOwnFolder_RegistersIt_AndSwitches()
    {
        using var outer = new TempDir();
        await using var service = CatalogService.Open(RegistryPath(outer), outer.Combine("default"));
        var parent = outer.Combine("Libraries");
        Directory.CreateDirectory(parent);

        var entry = await service.CreateLibraryAsync("  Work  ", parent);

        Assert.Equal("Work", entry.Name);
        Assert.Equal(Path.Combine(parent, "Work"), entry.Dir);
        Assert.True(File.Exists(Path.Combine(parent, "Work", ApertureDatabase.MetadataFileName)));
        Assert.Equal(entry.Id, service.ActiveCatalog!.Id);
        Assert.Equal(2, service.Registry.List().Count);
        Assert.Empty(service.Current.GetUnion());
    }

    [Fact]
    public async Task CreateLibraryAsync_IntoAnExistingEmptyFolder_IsAllowed()
    {
        using var outer = new TempDir();
        await using var service = CatalogService.Open(RegistryPath(outer), outer.Combine("default"));
        Directory.CreateDirectory(outer.Combine("Work"));

        var entry = await service.CreateLibraryAsync("Work", outer.Path);

        Assert.Equal(entry.Id, service.ActiveCatalog!.Id);
    }

    [Fact]
    public async Task CreateLibraryAsync_ExistingFolderWithFilesInIt_IsRefused_AndLeftUntouched()
    {
        using var outer = new TempDir();
        await using var service = CatalogService.Open(RegistryPath(outer), outer.Combine("default"));
        var photos = outer.Combine("Photos");
        Directory.CreateDirectory(photos);
        File.WriteAllText(Path.Combine(photos, "IMG_0001.jpg"), "a photo");

        var ex = await Assert.ThrowsAsync<CatalogException>(() => service.CreateLibraryAsync("Photos", outer.Path));

        Assert.Contains(photos, ex.Message);
        Assert.Single(service.Registry.List());
        Assert.Equal(["IMG_0001.jpg"], Directory.GetFiles(photos).Select(Path.GetFileName));
    }

    [Fact]
    public async Task CreateLibraryAsync_NameTakenByAFile_IsRefused()
    {
        using var outer = new TempDir();
        await using var service = CatalogService.Open(RegistryPath(outer), outer.Combine("default"));
        File.WriteAllText(outer.Combine("Work"), "a file, not a folder");

        await Assert.ThrowsAsync<CatalogException>(() => service.CreateLibraryAsync("Work", outer.Path));

        Assert.Single(service.Registry.List());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a/b")]
    [InlineData("a:b")]
    [InlineData("what?")]
    [InlineData("trailing.")]
    [InlineData("default")] // case-insensitive duplicate of the existing "Default"
    public async Task CreateLibraryAsync_BadName_IsRefused_BeforeAnythingIsCreated(string name)
    {
        using var outer = new TempDir();
        await using var service = CatalogService.Open(RegistryPath(outer), outer.Combine("default"));
        var parent = outer.Combine("Libraries");
        Directory.CreateDirectory(parent);

        await Assert.ThrowsAsync<CatalogException>(() => service.CreateLibraryAsync(name, parent));
        Assert.Throws<CatalogException>(() => service.CheckNewLibraryName(name));

        Assert.Empty(Directory.GetFileSystemEntries(parent));
        Assert.Single(service.Registry.List());
    }

    [Fact]
    public async Task CheckNewLibraryName_ReturnsTheTrimmedName()
    {
        using var outer = new TempDir();
        await using var service = CatalogService.Open(RegistryPath(outer), outer.Combine("default"));

        Assert.Equal("Work", service.CheckNewLibraryName("  Work "));
    }

    // --- Open library: an existing library only -----------------------------------------------

    [Fact]
    public async Task OpenLibraryAsync_ExistingLibrary_RegistersAndSwitches()
    {
        using var outer = new TempDir();
        using var lib = new TempDir();
        TestImages.Write(Path.Combine(lib.Path, "a.jpg"), 64, 64);
        var elsewhere = outer.Combine("Elsewhere");
        using (var made = new LibraryService(elsewhere))
            made.IndexRoot(made.AddRoot(lib.Path, "L")); // a library made earlier, on another drive or machine
        await using var service = CatalogService.Open(RegistryPath(outer), outer.Combine("default"));

        var entry = await service.OpenLibraryAsync(elsewhere);

        Assert.Equal("Elsewhere", entry.Name);
        Assert.Equal(entry.Id, service.ActiveCatalog!.Id);
        Assert.Single(service.Current.GetUnion());
    }

    [Fact]
    public async Task OpenLibraryAsync_AlreadyRegistered_JustSwitches_WithoutADuplicate()
    {
        using var outer = new TempDir();
        await using var service = CatalogService.Open(RegistryPath(outer), outer.Combine("default"));
        var work = AddLibrary(service, "Work", outer.Combine("work"));

        var entry = await service.OpenLibraryAsync(outer.Combine("work"));

        Assert.Equal(work.Id, entry.Id);
        Assert.Equal(work.Id, service.ActiveCatalog!.Id);
        Assert.Equal(2, service.Registry.List().Count);
    }

    [Fact]
    public async Task OpenLibraryAsync_TheOpenLibrary_IsANoOp()
    {
        using var outer = new TempDir();
        var defaultDir = outer.Combine("default");
        await using var service = CatalogService.Open(RegistryPath(outer), defaultDir);
        var current = service.Current;

        var entry = await service.OpenLibraryAsync(defaultDir);

        Assert.Equal(service.ActiveCatalog!.Id, entry.Id);
        Assert.Same(current, service.Current);
    }

    [Fact]
    public async Task OpenLibraryAsync_FolderWithoutALibrary_IsRefused_NothingCreatedOrRegistered()
    {
        using var outer = new TempDir();
        await using var service = CatalogService.Open(RegistryPath(outer), outer.Combine("default"));
        var photos = outer.Combine("Photos");
        Directory.CreateDirectory(photos);
        File.WriteAllText(Path.Combine(photos, "IMG_0001.jpg"), "a photo");

        await Assert.ThrowsAsync<CatalogException>(() => service.OpenLibraryAsync(photos));

        Assert.Single(service.Registry.List());
        Assert.Equal(["IMG_0001.jpg"], Directory.GetFiles(photos).Select(Path.GetFileName));
    }

    [Fact]
    public async Task OpenLibraryAsync_DatabaseThatCannotOpen_RegistersNothing_AndKeepsCurrentLive()
    {
        using var outer = new TempDir();
        await using var service = CatalogService.Open(RegistryPath(outer), outer.Combine("default"));
        var current = service.Current;
        WriteNotADatabase(outer.Combine("bad"));

        await Assert.ThrowsAnyAsync<Exception>(() => service.OpenLibraryAsync(outer.Combine("bad")));

        Assert.Single(service.Registry.List());
        Assert.Same(current, service.Current);
        Assert.Empty(service.Current.GetUnion());
    }

    [Fact]
    public async Task OpenLibraryAsync_NameClash_GetsAUniqueName()
    {
        using var outer = new TempDir();
        await using var service = CatalogService.Open(RegistryPath(outer), outer.Combine("default"));
        var first = Path.Combine(outer.Path, "x", "Photos");
        var second = Path.Combine(outer.Path, "y", "Photos");
        using (new LibraryService(first)) { }
        using (new LibraryService(second)) { }

        await service.OpenLibraryAsync(first);
        var entry = await service.OpenLibraryAsync(second);

        Assert.Equal("Photos (2)", entry.Name);
    }

    // --- Rename, remove, delete ---------------------------------------------------------------

    [Fact]
    public async Task Rename_ValidName_IsTrimmedAndPersisted()
    {
        using var outer = new TempDir();
        await using var service = CatalogService.Open(RegistryPath(outer), outer.Combine("default"));
        var id = service.ActiveCatalog!.Id;

        service.Rename(id, " Home ");

        Assert.Equal("Home", new CatalogRegistry(RegistryPath(outer)).List().Single(c => c.Id == id).Name);
    }

    [Fact]
    public async Task Rename_EmptyOrTakenName_IsRefused()
    {
        using var outer = new TempDir();
        await using var service = CatalogService.Open(RegistryPath(outer), outer.Combine("default"));
        AddLibrary(service, "Work", outer.Combine("work"));
        var id = service.ActiveCatalog!.Id;

        Assert.Throws<CatalogException>(() => service.Rename(id, "  "));
        Assert.Throws<CatalogException>(() => service.Rename(id, "WORK"));
        service.Rename(id, "default"); // a case-only change of its own name is fine
        Assert.Equal("default", service.ActiveCatalog!.Name);
    }

    [Fact]
    public async Task Rename_UnknownId_Throws()
    {
        using var outer = new TempDir();
        await using var service = CatalogService.Open(RegistryPath(outer), outer.Combine("default"));

        Assert.Throws<ArgumentException>(() => service.Rename("nope", "Name"));
    }

    [Fact]
    public async Task RemoveAsync_TheOpenLibrary_IsRefused()
    {
        using var outer = new TempDir();
        await using var service = CatalogService.Open(RegistryPath(outer), outer.Combine("default"));
        AddLibrary(service, "Work", outer.Combine("work"));

        await Assert.ThrowsAsync<CatalogException>(() => service.RemoveAsync(service.ActiveCatalog!.Id));

        Assert.Equal(2, service.Registry.List().Count);
    }

    [Fact]
    public async Task RemoveAsync_AnotherLibrary_ForgetsIt_AndKeepsItsFiles()
    {
        using var outer = new TempDir();
        await using var service = CatalogService.Open(RegistryPath(outer), outer.Combine("default"));
        var work = AddLibrary(service, "Work", outer.Combine("work"));

        await service.RemoveAsync(work.Id);

        Assert.Single(service.Registry.List());
        Assert.True(File.Exists(Path.Combine(work.Dir, ApertureDatabase.MetadataFileName)));
    }

    [Fact]
    public async Task RemoveAsync_UnknownId_Throws()
    {
        using var outer = new TempDir();
        await using var service = CatalogService.Open(RegistryPath(outer), outer.Combine("default"));

        await Assert.ThrowsAsync<ArgumentException>(() => service.RemoveAsync("nope"));
    }

    [Fact]
    public async Task RemoveAsync_WhenTheListCannotBeSaved_Throws_AndKeepsTheLibrary()
    {
        using var outer = new TempDir();
        var regPath = RegistryPath(outer);
        await using var service = CatalogService.Open(regPath, outer.Combine("default"));
        var work = AddLibrary(service, "Work", outer.Combine("work"));

        File.SetAttributes(regPath, FileAttributes.ReadOnly);
        try
        {
            await Assert.ThrowsAsync<CatalogException>(() => service.RemoveAsync(work.Id));
        }
        finally
        {
            File.SetAttributes(regPath, FileAttributes.Normal);
        }

        Assert.Contains(service.Registry.List(), c => c.Id == work.Id);
    }

    [Fact]
    public async Task DeleteFilesAsync_TheOpenLibrary_IsRefused_AndNothingIsDeleted()
    {
        using var outer = new TempDir();
        var defaultDir = outer.Combine("default");
        await using var service = CatalogService.Open(RegistryPath(outer), defaultDir);
        AddLibrary(service, "Work", outer.Combine("work"));

        await Assert.ThrowsAsync<CatalogException>(() => service.DeleteFilesAsync(service.ActiveCatalog!.Id));

        Assert.True(File.Exists(Path.Combine(defaultDir, ApertureDatabase.MetadataFileName)));
    }

    [Fact]
    public async Task DeleteFilesAsync_AnotherLibrary_DeletesOnlyApertureFiles()
    {
        using var outer = new TempDir();
        await using var service = CatalogService.Open(RegistryPath(outer), outer.Combine("default"));
        var work = AddLibrary(service, "Work", outer.Combine("work"));
        File.WriteAllText(Path.Combine(work.Dir, "keep-me.txt"), "not Aperture's");

        var result = await service.DeleteFilesAsync(work.Id);

        Assert.Equal(CatalogDeletionOutcome.Deleted, result.Outcome);
        Assert.Single(service.Registry.List());
        Assert.False(File.Exists(Path.Combine(work.Dir, ApertureDatabase.MetadataFileName)));
        Assert.True(File.Exists(Path.Combine(work.Dir, "keep-me.txt")));
    }

    [Fact]
    public async Task DeleteFilesAsync_UnknownId_Throws()
    {
        using var outer = new TempDir();
        await using var service = CatalogService.Open(RegistryPath(outer), outer.Combine("default"));

        await Assert.ThrowsAsync<ArgumentException>(() => service.DeleteFilesAsync("nope"));
    }

    [Fact]
    public async Task DeleteFilesAsync_WhenTheListCannotBeSaved_Throws_AndDeletesNothing()
    {
        using var outer = new TempDir();
        var regPath = RegistryPath(outer);
        await using var service = CatalogService.Open(regPath, outer.Combine("default"));
        var work = AddLibrary(service, "Work", outer.Combine("work"));

        File.SetAttributes(regPath, FileAttributes.ReadOnly);
        try
        {
            await Assert.ThrowsAsync<CatalogException>(() => service.DeleteFilesAsync(work.Id));
        }
        finally
        {
            File.SetAttributes(regPath, FileAttributes.Normal);
        }

        Assert.True(File.Exists(Path.Combine(work.Dir, ApertureDatabase.MetadataFileName)));
    }

    [Fact]
    public async Task DisposeAsync_ShutsDownCurrent_SoFilesAreReleased()
    {
        using var outer = new TempDir();
        var defaultDir = outer.Combine("default");
        var service = CatalogService.Open(RegistryPath(outer), defaultDir);

        await service.DisposeAsync();

        Directory.Delete(defaultDir, recursive: true);
        Assert.False(Directory.Exists(defaultDir));
    }

    [Fact]
    public async Task Registry_PersistsCamelCaseJson_AsTheDesignDocumentShows()
    {
        using var outer = new TempDir();
        var regPath = RegistryPath(outer);
        await using (CatalogService.Open(regPath, outer.Combine("default"))) { }

        using var doc = JsonDocument.Parse(File.ReadAllText(regPath));
        Assert.True(doc.RootElement.TryGetProperty("catalogs", out var catalogs));
        Assert.True(catalogs[0].TryGetProperty("lastOpenedTicks", out _));
        Assert.True(doc.RootElement.TryGetProperty("activeId", out _));
    }
}
