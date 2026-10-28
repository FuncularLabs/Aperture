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
    public async Task Open_ActiveLibraryUnavailable_FallsBackForThisLaunchOnly_AndOpensItAgainOnceItsBack()
    {
        using var outer = new TempDir();
        var regPath = RegistryPath(outer);
        var defaultDir = outer.Combine("default");
        var usbDir = outer.Combine("usb");
        var parked = outer.Combine("usb-unplugged");
        string usbId;
        await using (var first = CatalogService.Open(regPath, defaultDir))
        {
            var usb = AddLibrary(first, "USB", usbDir);
            await first.SwitchToAsync(usb.Id);
            usbId = usb.Id;
        }
        Directory.Move(usbDir, parked); // the drive was unplugged (say, not yet mounted at login)

        for (var launch = 0; launch < 2; launch++)
        {
            await using var service = CatalogService.Open(regPath, defaultDir);
            Assert.Equal("Default", service.ActiveCatalog!.Name);
            Assert.Empty(service.Current.GetUnion());
            Assert.Contains("USB", service.OpenWarning); // said every time, not just once
            Assert.Contains("Remove from list", service.OpenWarning); // and how to stop it if the drive is gone for good
            Assert.Contains(service.Registry.List(), c => c.Id == usbId);
        }
        Assert.False(Directory.Exists(usbDir)); // never silently recreated as an empty library

        Directory.Move(parked, usbDir); // plugged back in
        await using var later = CatalogService.Open(regPath, defaultDir);
        Assert.Equal(usbId, later.ActiveCatalog!.Id); // the user's choice, not the fallback, opens
        Assert.Null(later.OpenWarning);
    }

    [Fact]
    public async Task Open_UnreadableLibraryList_IsSetAside_AndSaysWhere()
    {
        using var outer = new TempDir();
        var regPath = RegistryPath(outer);
        File.WriteAllText(regPath, "{ broken");

        await using var service = CatalogService.Open(regPath, outer.Combine("default"));

        Assert.Equal("Default", service.ActiveCatalog!.Name);
        var bad = Assert.Single(Directory.GetFiles(outer.Path, "catalogs.json.bad-*"));
        Assert.Contains(bad, service.OpenWarning);
        Assert.Contains("Open library", service.OpenWarning);
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
    public async Task Open_FallsBackToDefault_BeforeAMoreRecentlyOpenedLibrary()
    {
        using var outer = new TempDir();
        var regPath = RegistryPath(outer);
        var defaultDir = outer.Combine("default");
        string defaultId;
        await using (var first = CatalogService.Open(regPath, defaultDir))
        {
            defaultId = first.ActiveCatalog!.Id;
            var work = AddLibrary(first, "Work", outer.Combine("work"));
            var travel = AddLibrary(first, "Travel", outer.Combine("travel"));
            await first.SwitchToAsync(work.Id);   // Work is more recent than Default…
            await first.SwitchToAsync(travel.Id);
        }
        Directory.Delete(outer.Combine("travel"), recursive: true);

        await using var service = CatalogService.Open(regPath, defaultDir);

        Assert.Equal(defaultId, service.ActiveCatalog!.Id); // …but the local Default is tried first
    }

    [Fact]
    public async Task Open_NothingOpens_AndTheDefaultFolderIsGoneToo_StillStarts()
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
        Directory.Delete(defaultDir, recursive: true);

        await using var service = CatalogService.Open(regPath, defaultDir);

        Assert.Equal(Path.GetFullPath(defaultDir), service.ActiveCatalog!.Dir);
        Assert.Contains("Work", service.OpenWarning);
    }

    [Fact]
    public async Task Open_WhileTheLibraryListIsLocked_KeepsTheList_AndSaysWhy()
    {
        using var outer = new TempDir();
        var regPath = RegistryPath(outer);
        var defaultDir = outer.Combine("default");
        await using (var first = CatalogService.Open(regPath, defaultDir))
        {
            AddLibrary(first, "Work", outer.Combine("work"));
            AddLibrary(first, "Travel", outer.Combine("travel"));
        }

        CatalogService launched;
        using (new FileStream(regPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) // locked for the whole launch
            launched = CatalogService.Open(regPath, defaultDir);

        await using (launched)
        {
            Assert.Equal("Default", launched.ActiveCatalog!.Name);
            Assert.Contains("couldn't read its list of libraries", launched.OpenWarning);
        }
        var onDisk = new CatalogRegistry(regPath).List().Select(c => c.Name).ToList();
        Assert.Contains("Work", onDisk);   // the list it couldn't read was never replaced
        Assert.Contains("Travel", onDisk);
    }

    [Fact]
    public async Task Open_WhenTheListIsLockedBriefly_TheUsersLibrariesSurvive()
    {
        using var outer = new TempDir();
        var regPath = RegistryPath(outer);
        var defaultDir = outer.Combine("default");
        await using (var first = CatalogService.Open(regPath, defaultDir))
        {
            AddLibrary(first, "Work", outer.Combine("work"));
            AddLibrary(first, "Travel", outer.Combine("travel"));
        }

        var locker = new FileStream(regPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var release = Task.Delay(420).ContinueWith(_ => locker.Dispose());
        var launched = CatalogService.Open(regPath, defaultDir);
        await release;
        await launched.DisposeAsync();

        var onDisk = new CatalogRegistry(regPath).List().Select(c => c.Name).ToList();
        Assert.Contains("Work", onDisk);
        Assert.Contains("Travel", onDisk);
    }

    [Fact]
    public async Task Open_LastResortDefault_IsForThisLaunchOnly_TheUsersLibraryOpensOnceItsBack()
    {
        using var outer = new TempDir();
        var regPath = RegistryPath(outer);
        var defaultDir = outer.Combine("default");
        var workDir = outer.Combine("work");
        var parked = outer.Combine("work-unplugged");
        string workId;
        await using (var first = CatalogService.Open(regPath, defaultDir))
        {
            var work = AddLibrary(first, "Work", workDir);
            await first.SwitchToAsync(work.Id);
            await first.RemoveAsync(first.Registry.List().Single(c => c.Name == "Default").Id);
            workId = work.Id;
        }
        Directory.Move(workDir, parked);
        await using (var fallback = CatalogService.Open(regPath, defaultDir)) // only Default can be opened
            Assert.Equal(Path.GetFullPath(defaultDir), fallback.ActiveCatalog!.Dir);
        Directory.Move(parked, workDir);

        await using var later = CatalogService.Open(regPath, defaultDir);

        Assert.Equal(workId, later.ActiveCatalog!.Id);
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
    public async Task SwitchToAsync_RaisesPreparingToSwitch_WhileTheOldLibraryIsStillOpen()
    {
        using var outer = new TempDir();
        await using var service = CatalogService.Open(RegistryPath(outer), outer.Combine("default"));
        var work = AddLibrary(service, "Work", outer.Combine("work"));
        Exception? readDuringPrepare = new("PreparingToSwitch never ran");
        service.PreparingToSwitch += (_, _) => readDuringPrepare = Record.Exception(() => service.Current.GetUnion());

        await service.SwitchToAsync(work.Id);

        Assert.Null(readDuringPrepare); // the host quiesces its view model before the library closes, not after
    }

    [Fact]
    public async Task QueuedSwitches_StillRaiseTheirEventsOnTheCallersContext()
    {
        using var outer = new TempDir();
        using var lib = new TempDir();
        TestImages.Write(Path.Combine(lib.Path, "a.jpg"), 64, 64);
        await using var service = CatalogService.Open(RegistryPath(outer), outer.Combine("default"));
        var a = AddLibrary(service, "A", outer.Combine("a"));
        var b = AddLibrary(service, "B", outer.Combine("b"));
        var held = new BlockingProgress();
        var indexing = service.Current.IndexRootAsync(service.Current.AddRoot(lib.Path, "L"), held);
        Assert.True(held.Entered.Wait(5000));
        var threads = new List<int>();
        service.ActiveLibraryChanged += (_, _) => { lock (threads) threads.Add(Environment.CurrentManagedThreadId); };

        using var ui = new SingleThreadContext();
        var first = ui.Run(() => service.SwitchToAsync(a.Id));
        var second = ui.Run(() => service.SwitchToAsync(b.Id)); // waits its turn behind the first
        await Task.Delay(200);
        held.Release();
        await Task.WhenAll(first, second);
        await Record.ExceptionAsync(() => indexing);

        Assert.Equal([ui.ThreadId, ui.ThreadId], threads);
    }

    [Fact]
    public async Task QueuedNewAndOpen_StillRaiseTheirEventsOnTheCallersContext()
    {
        using var outer = new TempDir();
        using var lib = new TempDir();
        TestImages.Write(Path.Combine(lib.Path, "a.jpg"), 64, 64);
        await using var service = CatalogService.Open(RegistryPath(outer), outer.Combine("default"));
        var a = AddLibrary(service, "A", outer.Combine("a"));
        var unlisted = outer.Combine("unlisted");
        using (new LibraryService(unlisted)) { }
        var held = new BlockingProgress();
        var indexing = service.Current.IndexRootAsync(service.Current.AddRoot(lib.Path, "L"), held);
        Assert.True(held.Entered.Wait(5000));
        var threads = new List<int>();
        service.ActiveLibraryChanged += (_, _) => { lock (threads) threads.Add(Environment.CurrentManagedThreadId); };

        using var ui = new SingleThreadContext();
        var first = ui.Run(() => service.SwitchToAsync(a.Id));
        var created = ui.Run(() => service.CreateLibraryAsync("Fresh", outer.Path)); // each waits its turn
        var opened = ui.Run(() => service.OpenLibraryAsync(unlisted));
        await Task.Delay(200);
        held.Release();
        await Task.WhenAll(first, created, opened);
        await Record.ExceptionAsync(() => indexing);

        Assert.Equal([ui.ThreadId, ui.ThreadId, ui.ThreadId], threads);
    }

    [Fact]
    public async Task CreateLibraryAsync_WhereTheFolderCantBeCreated_KeepsCurrentLive_AndRegistersNothing()
    {
        using var outer = new TempDir();
        await using var service = CatalogService.Open(RegistryPath(outer), outer.Combine("default"));
        var current = service.Current;
        var blocker = outer.Combine("blocker");
        File.WriteAllText(blocker, "a file where the parent folder would have to be");

        await Assert.ThrowsAnyAsync<Exception>(() => service.CreateLibraryAsync("Work", blocker));

        Assert.Single(service.Registry.List());
        Assert.Same(current, service.Current);
        Assert.Empty(service.Current.GetUnion()); // opened first, so the current library was never closed
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

    // Another window deletes the library in `dir` after this window has checked it's there and before this window
    // takes its lock (the hook runs at exactly that point). LibraryServiceShutdownTests covers the same delete
    // finishing while this window waits for the lock.
    private static void DeleteFromAnotherWindowJustAfterTheCheck(CatalogService service, string dir) =>
        service.TouchingLibraryFolder = () =>
        {
            service.TouchingLibraryFolder = null;
            File.Delete(Path.Combine(dir, ApertureDatabase.MetadataFileName));
        };

    [Fact]
    public async Task SwitchToAsync_ALibraryDeletedJustAfterItWasChecked_FailsAndRecreatesNothing()
    {
        using var outer = new TempDir();
        await using var service = CatalogService.Open(RegistryPath(outer), outer.Combine("default"));
        var work = AddLibrary(service, "Work", outer.Combine("work"));
        DeleteFromAnotherWindowJustAfterTheCheck(service, work.Dir);

        var ex = await Record.ExceptionAsync(() => service.SwitchToAsync(work.Id));

        var io = Assert.IsType<IOException>(ex); // found under the lock, not by the check before it
        Assert.Contains("any more", io.Message);
        Assert.False(File.Exists(Path.Combine(work.Dir, ApertureDatabase.MetadataFileName))); // not recreated empty
        Assert.Equal("Default", service.ActiveCatalog!.Name);
    }

    [Fact]
    public async Task OpenLibraryAsync_ALibraryDeletedJustAfterItWasChecked_FailsAndRegistersNothing()
    {
        using var outer = new TempDir();
        await using var service = CatalogService.Open(RegistryPath(outer), outer.Combine("default"));
        var found = outer.Combine("found");
        using (new LibraryService(found)) { }
        DeleteFromAnotherWindowJustAfterTheCheck(service, found);

        var ex = await Record.ExceptionAsync(() => service.OpenLibraryAsync(found));

        var io = Assert.IsType<IOException>(ex);
        Assert.Contains("any more", io.Message);
        Assert.False(File.Exists(Path.Combine(found, ApertureDatabase.MetadataFileName)));
        Assert.DoesNotContain(service.Registry.List(), c => CatalogRegistry.SameDir(c.Dir, found));
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
    public async Task RecordItemCount_KeepsTheOpenLibrarysCount()
    {
        using var outer = new TempDir();
        await using var service = CatalogService.Open(RegistryPath(outer), outer.Combine("default"));
        var work = AddLibrary(service, "Work", outer.Combine("work"));

        service.RecordItemCount(1234);

        Assert.Equal(1234, new CatalogRegistry(RegistryPath(outer)).List().Single(c => c.Id == service.ActiveCatalog!.Id).ItemCount);
        Assert.Null(service.Registry.List().Single(c => c.Id == work.Id).ItemCount); // not the others
    }

    [Fact]
    public async Task RecordItemCount_OfALibraryNotYetRead_KeepsItsLastCount()
    {
        using var outer = new TempDir();
        await using var service = CatalogService.Open(RegistryPath(outer), outer.Combine("default"));
        service.RecordItemCount(1234);

        service.RecordItemCount(null); // left (or closed) before its items were read: no count yet, which isn't none

        Assert.Equal(1234, new CatalogRegistry(RegistryPath(outer)).List().Single(c => c.Id == service.ActiveCatalog!.Id).ItemCount);
    }

    [Fact]
    public async Task RecordItemCount_AsASwitchPrepares_GoesToTheLibraryBeingLeft()
    {
        // The host records the open library's count as a switch prepares (the reviewer's probe, round 36): the library
        // being left is still the active one then.
        using var outer = new TempDir();
        await using var service = CatalogService.Open(RegistryPath(outer), outer.Combine("default"));
        var left = service.ActiveCatalog!.Id;
        var work = AddLibrary(service, "Work", outer.Combine("work"));
        service.PreparingToSwitch += (_, _) => service.RecordItemCount(5);

        await service.SwitchToAsync(work.Id);

        var saved = new CatalogRegistry(RegistryPath(outer)).List();
        Assert.Equal(5, saved.Single(c => c.Id == left).ItemCount);
        Assert.Null(saved.Single(c => c.Id == work.Id).ItemCount);
    }

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
    public async Task CreateLibrary_PassesOverATakenFolder_ForTheNextFreeOne()
    {
        using var outer = new TempDir();
        await using var service = CatalogService.Open(RegistryPath(outer), outer.Combine("default"));
        var photos = outer.Combine("Photos");
        Directory.CreateDirectory(photos);
        File.WriteAllText(Path.Combine(photos, "IMG_0001.jpg"), "a photo");
        Directory.CreateDirectory(outer.Combine("Photos (2)"));
        File.WriteAllText(Path.Combine(outer.Combine("Photos (2)"), "notes.txt"), "someone else's");

        var entry = await service.CreateLibraryAsync("Photos", outer.Path);

        Assert.Equal("Photos", entry.Name); // the library keeps the name it was given; only its folder differs
        Assert.Equal(outer.Combine("Photos (3)"), entry.Dir);
        Assert.Equal(entry.Id, service.ActiveCatalog!.Id);
        Assert.Equal(["IMG_0001.jpg"], Directory.GetFiles(photos).Select(Path.GetFileName));
        Assert.Equal(["notes.txt"], Directory.GetFiles(outer.Combine("Photos (2)")).Select(Path.GetFileName));
    }

    [Fact]
    public async Task CreateLibraryAsync_WhereAFileHasTheName_UsesTheNextFreeFolder()
    {
        using var outer = new TempDir();
        await using var service = CatalogService.Open(RegistryPath(outer), outer.Combine("default"));
        File.WriteAllText(outer.Combine("Work"), "a file, not a folder");

        var entry = await service.CreateLibraryAsync("Work", outer.Path);

        Assert.Equal(outer.Combine("Work (2)"), entry.Dir);
        Assert.Equal("a file, not a folder", File.ReadAllText(outer.Combine("Work")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a/b")]
    [InlineData("a:b")]
    [InlineData("what?")]
    [InlineData("trailing.")]
    [InlineData("NUL")]     // reserved device names Windows won't treat as ordinary folders
    [InlineData("con")]
    [InlineData("COM1")]
    [InlineData("lpt9")]
    [InlineData("aux.txt")]
    [InlineData("COM0")]
    [InlineData("LPT0")]
    [InlineData("COM¹")] // COM¹ — superscript digits count too
    [InlineData("CONIN$")]
    [InlineData("conout$")]
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

        var ex = await Assert.ThrowsAsync<CatalogException>(() => service.RemoveAsync(service.ActiveCatalog!.Id));

        Assert.Contains("Switch to another library first", ex.Message);
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

        var ex = await Assert.ThrowsAsync<CatalogException>(() => service.DeleteFilesAsync(service.ActiveCatalog!.Id));

        Assert.Contains("Switch to another library first", ex.Message);
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

    // --- A change that can't be saved is refused ----------------------------------------------

    [Fact]
    public async Task CreateLibraryAsync_WhenTheListCannotBeSaved_CreatesNothing_AndKeepsCurrent()
    {
        using var outer = new TempDir();
        var regPath = RegistryPath(outer);
        await using var service = CatalogService.Open(regPath, outer.Combine("default"));
        var current = service.Current;

        File.SetAttributes(regPath, FileAttributes.ReadOnly);
        try
        {
            await Assert.ThrowsAsync<CatalogException>(() => service.CreateLibraryAsync("Work", outer.Path));
        }
        finally
        {
            File.SetAttributes(regPath, FileAttributes.Normal);
        }

        Assert.Same(current, service.Current);
        Assert.Equal("Default", service.ActiveCatalog!.Name);
        Assert.Single(service.Registry.List());
        Assert.False(Directory.Exists(outer.Combine("Work"))); // the folder it made is gone again
    }

    [Fact]
    public async Task OpenLibraryAsync_WhenTheListCannotBeSaved_AddsNothing_AndKeepsCurrent()
    {
        using var outer = new TempDir();
        var regPath = RegistryPath(outer);
        var elsewhere = outer.Combine("Elsewhere");
        using (new LibraryService(elsewhere)) { }
        await using var service = CatalogService.Open(regPath, outer.Combine("default"));
        var current = service.Current;

        File.SetAttributes(regPath, FileAttributes.ReadOnly);
        try
        {
            await Assert.ThrowsAsync<CatalogException>(() => service.OpenLibraryAsync(elsewhere));
        }
        finally
        {
            File.SetAttributes(regPath, FileAttributes.Normal);
        }

        Assert.Same(current, service.Current);
        Assert.Single(service.Registry.List());
        Assert.False(LibraryService.IsOpen(elsewhere)); // the library it opened was closed again
    }

    [Fact]
    public async Task Rename_WhenTheListCannotBeSaved_Throws_AndKeepsTheName()
    {
        using var outer = new TempDir();
        var regPath = RegistryPath(outer);
        await using var service = CatalogService.Open(regPath, outer.Combine("default"));

        File.SetAttributes(regPath, FileAttributes.ReadOnly);
        try
        {
            Assert.Throws<CatalogException>(() => service.Rename(service.ActiveCatalog!.Id, "Home"));
        }
        finally
        {
            File.SetAttributes(regPath, FileAttributes.Normal);
        }

        Assert.Equal("Default", service.ActiveCatalog!.Name);
    }

    // --- Another Aperture window --------------------------------------------------------------

    [Fact]
    public async Task SwitchToAsync_ToALibraryAnotherWindowRemoved_KeepsTheOpenLibraryAndTheListInStep()
    {
        using var outer = new TempDir();
        var regPath = RegistryPath(outer);
        await using var service = CatalogService.Open(regPath, outer.Combine("default"));
        var work = AddLibrary(service, "Work", outer.Combine("work"));
        new CatalogRegistry(regPath).Remove(work.Id); // another window forgot it; this window's menu still shows it

        await service.SwitchToAsync(work.Id);

        Assert.Equal(work.Id, service.ActiveCatalog!.Id); // Current and ActiveCatalog agree
        Assert.Contains(new CatalogRegistry(regPath).List(), c => c.Id == work.Id);
    }

    [Fact]
    public async Task RemoveAsync_ALibraryOpenInAnotherWindow_IsRefused()
    {
        using var outer = new TempDir();
        await using var service = CatalogService.Open(RegistryPath(outer), outer.Combine("default"));
        var work = AddLibrary(service, "Work", outer.Combine("work"));
        using var otherWindow = new LibraryService(work.Dir);

        var ex = await Assert.ThrowsAsync<CatalogException>(() => service.RemoveAsync(work.Id));

        Assert.Contains("another copy of Aperture", ex.Message);
        Assert.Equal(2, service.Registry.List().Count);
    }

    [Fact]
    public async Task DeleteFilesAsync_ALibraryOpenInAnotherWindow_IsRefused_AndDeletesNothing()
    {
        using var outer = new TempDir();
        await using var service = CatalogService.Open(RegistryPath(outer), outer.Combine("default"));
        var work = AddLibrary(service, "Work", outer.Combine("work"));
        using (var otherWindow = new LibraryService(work.Dir))
        {
            otherWindow.Settings.Update(s => s.DefaultZoom = 3);
            await Assert.ThrowsAsync<CatalogException>(() => service.DeleteFilesAsync(work.Id));
        }

        Assert.True(File.Exists(Path.Combine(work.Dir, "settings.json")));
        Assert.True(File.Exists(Path.Combine(work.Dir, ApertureDatabase.MetadataFileName)));
    }

    [Fact]
    public async Task DeleteFilesAsync_OfTheOpenLibraryReachedByAnotherPath_IsRefused_AndDeletesNothing()
    {
        using var outer = new TempDir();
        await using var service = CatalogService.Open(RegistryPath(outer), outer.Combine("default"));
        var real = AddLibrary(service, "Real", outer.Combine("real"));
        var alias = outer.Combine("alias");
        Junction.Create(alias, real.Dir);
        await service.OpenLibraryAsync(alias); // the same folder, picked by another path: now open

        var ex = await Assert.ThrowsAsync<CatalogException>(() => service.DeleteFilesAsync(real.Id));

        Assert.Contains("is open", ex.Message);
        Assert.True(File.Exists(Path.Combine(real.Dir, ApertureDatabase.MetadataFileName)));
        Assert.Empty(service.Current.GetUnion());
    }

    [Fact]
    public async Task ALibraryWhoseLockCantBeRead_CanBeRemovedFromTheList_ButNotDeleted()
    {
        using var outer = new TempDir();
        await using var service = CatalogService.Open(RegistryPath(outer), outer.Combine("default"));
        var copied = AddLibrary(service, "Copied", outer.Combine("copied")); // say, a folder copied from another PC
        var lockFile = Path.Combine(copied.Dir, ApertureDatabase.LockFileName);
        Acl.DenyRead(lockFile);
        try
        {
            Assert.Equal(LibraryOpenState.Unknown, LibraryService.OpenState(copied.Dir));

            var refused = await Assert.ThrowsAsync<CatalogException>(() => service.DeleteFilesAsync(copied.Id));
            Assert.Contains("can't tell", refused.Message);
            Assert.True(File.Exists(Path.Combine(copied.Dir, ApertureDatabase.MetadataFileName)));

            await service.RemoveAsync(copied.Id); // forgetting a list entry is safe even if it might be open
            Assert.DoesNotContain(service.Registry.List(), c => c.Id == copied.Id);
        }
        finally
        {
            Acl.Restore(lockFile);
        }
    }

    [Fact]
    public async Task RemoveAndDelete_TouchTheLibraryFolderOffTheCallersThread()
    {
        using var outer = new TempDir();
        await using var service = CatalogService.Open(RegistryPath(outer), outer.Combine("default"));
        var one = AddLibrary(service, "One", outer.Combine("one"));
        var two = AddLibrary(service, "Two", outer.Combine("two"));
        var probes = new List<int>(); // the "is it open somewhere?" check
        var changes = new List<int>(); // the registry change, and the delete's own look at the folder
        service.TouchingLibraryFolder = () => { lock (probes) probes.Add(Environment.CurrentManagedThreadId); };
        service.Registry.BetweenReadAndChange = () => { lock (changes) changes.Add(Environment.CurrentManagedThreadId); };

        using var ui = new SingleThreadContext();
        await ui.Run(() => service.RemoveAsync(one.Id));      // an offline share can take tens of seconds to answer —
        await ui.Run(() => service.DeleteFilesAsync(two.Id)); // never on the UI thread
        service.TouchingLibraryFolder = null;
        service.Registry.BetweenReadAndChange = null;

        Assert.Equal(2, probes.Count);
        Assert.DoesNotContain(ui.ThreadId, probes);
        Assert.Equal(2, changes.Count);
        Assert.DoesNotContain(ui.ThreadId, changes);
    }

    [Fact]
    public async Task SwitchNewAndOpen_TouchLibraryFoldersOffTheCallersThread_AndNotifyOnIt()
    {
        using var outer = new TempDir();
        await using var service = CatalogService.Open(RegistryPath(outer), outer.Combine("default"));
        var one = AddLibrary(service, "One", outer.Combine("one"));
        var unlisted = outer.Combine("unlisted");
        using (new LibraryService(unlisted)) { }
        var touches = new List<int>();
        var notices = new List<int>();
        service.TouchingLibraryFolder = () => { lock (touches) touches.Add(Environment.CurrentManagedThreadId); };
        service.PreparingToSwitch += (_, _) => { lock (notices) notices.Add(Environment.CurrentManagedThreadId); };
        service.ActiveLibraryChanged += (_, _) => { lock (notices) notices.Add(Environment.CurrentManagedThreadId); };

        using var ui = new SingleThreadContext();
        await ui.Run(() => service.SwitchToAsync(one.Id));                   // a listed library
        await ui.Run(() => service.CreateLibraryAsync("Fresh", outer.Path)); // a new one
        await ui.Run(() => service.OpenLibraryAsync(unlisted));              // a folder not listed yet
        await ui.Run(() => service.OpenLibraryAsync(one.Dir));               // a listed one, by folder
        service.TouchingLibraryFolder = null;

        Assert.Equal(8, touches.Count); // each opens the next library and closes the one it leaves
        Assert.DoesNotContain(ui.ThreadId, touches);
        Assert.Equal(8, notices.Count); // the host rebinds its window in these, so they must arrive on its thread
        Assert.All(notices, id => Assert.Equal(ui.ThreadId, id));
    }

    [Fact]
    public async Task NewAndOpen_CleanUpAfterAFailedSave_OffTheCallersThread()
    {
        using var outer = new TempDir();
        var regPath = RegistryPath(outer);
        await using var service = CatalogService.Open(regPath, outer.Combine("default"));
        var elsewhere = outer.Combine("Elsewhere");
        using (new LibraryService(elsewhere)) { }
        var touches = new List<int>();
        service.TouchingLibraryFolder = () => { lock (touches) touches.Add(Environment.CurrentManagedThreadId); };

        using var ui = new SingleThreadContext();
        File.SetAttributes(regPath, FileAttributes.ReadOnly);
        try
        {
            await Assert.ThrowsAsync<CatalogException>(() => ui.Run(() => service.CreateLibraryAsync("Work", outer.Path)));
            await Assert.ThrowsAsync<CatalogException>(() => ui.Run(() => service.OpenLibraryAsync(elsewhere)));
        }
        finally
        {
            File.SetAttributes(regPath, FileAttributes.Normal);
        }
        service.TouchingLibraryFolder = null;

        Assert.Equal(5, touches.Count); // each opened its library and closed it again; New also removed its files
        Assert.DoesNotContain(ui.ThreadId, touches);
        Assert.False(Directory.Exists(outer.Combine("Work")));
    }

    [Fact]
    public async Task CreateLibraryAsync_InAFolderLeftByAnInterruptedNew_UsesIt()
    {
        using var outer = new TempDir();
        await using var service = CatalogService.Open(RegistryPath(outer), outer.Combine("default"));
        var dir = outer.Combine("Work");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, ApertureDatabase.LockFileName), []); // Aperture closed before it made the database

        await service.CreateLibraryAsync("Work", outer.Path);

        Assert.Equal("Work", service.ActiveCatalog!.Name);
        Assert.True(File.Exists(Path.Combine(dir, ApertureDatabase.MetadataFileName)));
    }

    [Fact]
    public async Task CreateLibraryAsync_PassesOverTheFolderOfAListedLibrary()
    {
        using var outer = new TempDir();
        await using var service = CatalogService.Open(RegistryPath(outer), outer.Combine("default"));
        var photos = AddLibrary(service, "Photos", outer.Combine("Work")); // listed under another name
        foreach (var file in Directory.GetFiles(photos.Dir).Where(f => Path.GetFileName(f) != ApertureDatabase.LockFileName))
            File.Delete(file); // its files deleted by hand: only the lock is left

        var entry = await service.CreateLibraryAsync("Work", outer.Path);

        Assert.Equal(outer.Combine("Work (2)"), entry.Dir);
        Assert.Equal("Photos", service.Registry.List().Single(c => c.Id == photos.Id).Name);
        Assert.Equal(photos.Dir, service.Registry.List().Single(c => c.Id == photos.Id).Dir);
        Assert.False(File.Exists(Path.Combine(photos.Dir, ApertureDatabase.MetadataFileName)));
    }

    [Fact]
    public async Task CreateLibraryAsync_WithANameAnotherWindowJustGaveALibrary_IsRefused()
    {
        using var outer = new TempDir();
        var regPath = RegistryPath(outer);
        await using var service = CatalogService.Open(regPath, outer.Combine("default"));
        new CatalogRegistry(regPath).Add("Trips", outer.Combine("elsewhere")); // another window, unseen by this one

        await Assert.ThrowsAsync<CatalogException>(() => service.CreateLibraryAsync("Trips", outer.Path));

        Assert.Single(service.Registry.List(), c => c.Name == "Trips");
        Assert.False(Directory.Exists(outer.Combine("Trips")));
    }

    [Fact]
    public async Task CreateLibraryAsync_PassesOverAFolderAnotherWindowJustListed()
    {
        using var outer = new TempDir();
        var regPath = RegistryPath(outer);
        await using var service = CatalogService.Open(regPath, outer.Combine("default"));
        Directory.CreateDirectory(outer.Combine("Work"));
        new CatalogRegistry(regPath).Add("Photos", outer.Combine("Work")); // another window, unseen by this one

        var entry = await service.CreateLibraryAsync("Work", outer.Path);

        Assert.Equal(outer.Combine("Work (2)"), entry.Dir);
        Assert.False(File.Exists(Path.Combine(outer.Combine("Work"), ApertureDatabase.MetadataFileName)));
    }

    [Fact]
    public async Task CreateLibraryAsync_PassesOverAFolderWhoseOnlyFileIsALockInUse()
    {
        using var outer = new TempDir();
        await using var service = CatalogService.Open(RegistryPath(outer), outer.Combine("default"));
        var dir = outer.Combine("Work");
        Directory.CreateDirectory(dir);
        using var creating = new FileStream(Path.Combine(dir, ApertureDatabase.LockFileName),
            FileMode.OpenOrCreate, FileAccess.Read, FileShare.Read); // another window is creating it right now

        var entry = await service.CreateLibraryAsync("Work", outer.Path);

        Assert.Equal(outer.Combine("Work (2)"), entry.Dir);
        Assert.False(File.Exists(Path.Combine(dir, ApertureDatabase.MetadataFileName)));
    }

    [Fact]
    public async Task DeleteFilesAsync_OfALibraryWhoseFolderCantBeReached_SaysNothingWasDeleted()
    {
        using var outer = new TempDir();
        await using var service = CatalogService.Open(RegistryPath(outer), outer.Combine("default"));
        var usb = AddLibrary(service, "USB", outer.Combine("usb"));
        var parked = outer.Combine("usb-unplugged");
        Directory.Move(usb.Dir, parked);

        var ex = await Assert.ThrowsAsync<CatalogException>(() => service.DeleteFilesAsync(usb.Id));

        Assert.Contains("nothing was deleted", ex.Message);
        Assert.Contains("can't reach", ex.Message);
        Assert.DoesNotContain("isn't allowed", ex.Message); // that's the unlistable folder's message
        Assert.Contains(service.Registry.List(), c => c.Id == usb.Id);
        Directory.Move(parked, usb.Dir);
        Assert.True(File.Exists(Path.Combine(usb.Dir, ApertureDatabase.MetadataFileName))); // tags and notes still there
    }

    [Fact]
    public async Task DeleteFilesAsync_OfALibraryWhoseFolderThisUserMayNotList_SaysSo_AndChangesNothing()
    {
        using var outer = new TempDir();
        await using var service = CatalogService.Open(RegistryPath(outer), outer.Combine("default"));
        var locked = AddLibrary(service, "Locked", outer.Combine("locked"));
        Acl.DenyListing(locked.Dir); // the library still opens and works; only what's in its folder can't be seen
        CatalogException ex;
        try
        {
            ex = await Assert.ThrowsAsync<CatalogException>(() => service.DeleteFilesAsync(locked.Id));
        }
        finally
        {
            Acl.RestoreListing(locked.Dir);
        }

        Assert.Contains("isn't allowed to see", ex.Message);
        Assert.DoesNotContain("can't reach", ex.Message);
        Assert.Contains(service.Registry.List(), c => c.Id == locked.Id);
        Assert.True(File.Exists(Path.Combine(locked.Dir, ApertureDatabase.MetadataFileName)));
    }

    [Fact]
    public async Task DeleteFilesAsync_WhenTheDriveGoesPartway_TheAdviceGivenWorks()
    {
        using var outer = new TempDir();
        await using var service = CatalogService.Open(RegistryPath(outer), outer.Combine("default"));
        var drive = outer.Combine("drive");
        using (new LibraryService(drive)) { }
        var link = outer.Combine("usb");
        Junction.Create(link, drive);
        var usb = service.Registry.Add("USB", link);
        service.Registry.DeletingFile = name =>
        {
            if (name != ApertureDatabase.MetadataFileName)
                return;
            Directory.Delete(link); // unplugged just before the database: the junction stays, pointing at nothing
            Junction.Create(link, outer.Combine("nowhere"));
        };

        var lost = await service.DeleteFilesAsync(usb.Id);
        service.Registry.DeletingFile = null;
        Assert.Equal(CatalogDeletionOutcome.FolderLostDuringDelete, lost.Outcome);
        Assert.False(File.Exists(Path.Combine(drive, ApertureDatabase.ThumbnailFileName))); // it did get partway

        Directory.Delete(link);
        Junction.Create(link, drive);                                  // "reconnect the drive,
        var back = await service.OpenLibraryAsync(link);               //  add the library again with Open library…,
        var defaultId = service.Registry.List().Single(c => c.Name == "Default").Id;
        await service.SwitchToAsync(defaultId);                        //  then switch to another library
        var deleted = await service.DeleteFilesAsync(back.Id);         //  and delete it."

        Assert.Equal(CatalogDeletionOutcome.Deleted, deleted.Outcome);
        Assert.DoesNotContain(service.Registry.List(), c => c.Id == back.Id);
        Assert.False(File.Exists(Path.Combine(drive, ApertureDatabase.MetadataFileName)));
    }

    [Fact]
    public async Task DeleteFilesAsync_OfALibraryAnotherWindowAlreadyDeleted_SaysItWasAlreadyRemoved()
    {
        using var outer = new TempDir();
        var regPath = RegistryPath(outer);
        await using var service = CatalogService.Open(regPath, outer.Combine("default"));
        var work = AddLibrary(service, "Work", outer.Combine("work"));
        new CatalogRegistry(regPath).RemoveAndDeleteFiles(work.Id); // another window; its folder goes too

        var ex = await Assert.ThrowsAsync<CatalogException>(() => service.DeleteFilesAsync(work.Id));

        Assert.Contains("already removed", ex.Message); // not "can't reach": there's nothing left to reach
        Assert.DoesNotContain(service.Registry.List(), c => c.Id == work.Id);
    }

    [Fact]
    public async Task Rename_ToANameAnotherWindowJustGaveALibrary_IsRefused()
    {
        using var outer = new TempDir();
        var regPath = RegistryPath(outer);
        await using var service = CatalogService.Open(regPath, outer.Combine("default"));
        new CatalogRegistry(regPath).Add("Travel", outer.Combine("travel")); // after this window's list was read

        Assert.Throws<CatalogException>(() => service.Rename(service.ActiveCatalog!.Id, "Travel"));

        Assert.Single(new CatalogRegistry(regPath).List(), c => c.Name == "Travel");
    }

    [Fact]
    public async Task RemoveAsync_AlreadyRemovedInAnotherWindow_SaysSo()
    {
        using var outer = new TempDir();
        var regPath = RegistryPath(outer);
        await using var service = CatalogService.Open(regPath, outer.Combine("default"));
        var work = AddLibrary(service, "Work", outer.Combine("work"));
        new CatalogRegistry(regPath).Remove(work.Id);

        var ex = await Assert.ThrowsAsync<CatalogException>(() => service.RemoveAsync(work.Id));

        Assert.Contains("already removed", ex.Message);
        Assert.Single(service.Registry.List()); // and this window's list caught up
    }

    [Fact]
    public async Task DeleteFilesAsync_AlreadyRemovedInAnotherWindow_SaysSo_AndDeletesNothing()
    {
        using var outer = new TempDir();
        var regPath = RegistryPath(outer);
        await using var service = CatalogService.Open(regPath, outer.Combine("default"));
        var work = AddLibrary(service, "Work", outer.Combine("work"));
        new CatalogRegistry(regPath).Remove(work.Id);

        var ex = await Assert.ThrowsAsync<CatalogException>(() => service.DeleteFilesAsync(work.Id));

        Assert.Contains("already removed", ex.Message);
        Assert.True(File.Exists(Path.Combine(work.Dir, ApertureDatabase.MetadataFileName)));
    }

    [Fact]
    public async Task Rename_AlreadyRemovedInAnotherWindow_SaysSo()
    {
        using var outer = new TempDir();
        var regPath = RegistryPath(outer);
        await using var service = CatalogService.Open(regPath, outer.Combine("default"));
        var work = AddLibrary(service, "Work", outer.Combine("work"));
        new CatalogRegistry(regPath).Remove(work.Id);

        var ex = Assert.Throws<CatalogException>(() => service.Rename(work.Id, "Office"));

        Assert.Contains("already removed", ex.Message);
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
