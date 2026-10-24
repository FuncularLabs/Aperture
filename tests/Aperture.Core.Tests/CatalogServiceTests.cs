using Aperture.Core.Library;
using Aperture.Core.Tests.Support;

namespace Aperture.Core.Tests;

/// <summary>
/// Catalog switch orchestration (Option B increment 3). CatalogService owns the registry and the one
/// live <see cref="LibraryService"/>, and switches between catalogs by tearing the current library down
/// (cancel + await indexing, release the DB) and opening the target. The invariant it maintains:
/// <c>Current</c> always corresponds to <c>Registry.Active</c>.
/// </summary>
public class CatalogServiceTests
{
    private static string RegistryPath(TempDir dir) => dir.Combine("catalogs.json");

    [Fact]
    public async Task Open_EmptyRegistry_RegistersDefault_AndOpensIt()
    {
        using var outer = new TempDir();
        var defaultDir = outer.Combine("default");

        await using var service = CatalogService.Open(RegistryPath(outer), defaultDir);

        Assert.Equal("Default", service.ActiveCatalog!.Name);
        Assert.Equal(Path.GetFullPath(defaultDir), service.ActiveCatalog!.Dir);
        Assert.NotNull(service.Current);
        // The opened library is live and usable.
        Assert.Empty(service.Current.GetUnion());
    }

    [Fact]
    public async Task SwitchToAsync_IsolatesCatalogs_BothDirections()
    {
        using var outer = new TempDir();
        using var libA = new TempDir();
        TestImages.Write(Path.Combine(libA.Path, "a.jpg"), 128, 128);

        await using var service = CatalogService.Open(RegistryPath(outer), outer.Combine("default"));
        var defaultId = service.ActiveCatalog!.Id;

        // Index one image into the Default catalog.
        await service.Current.IndexRootAsync(service.Current.AddRoot(libA.Path, "A"));
        Assert.Single(service.Current.GetUnion());

        // Switch to a brand-new empty catalog — it sees none of Default's items.
        var work = service.Registry.Add("Work", outer.Combine("work"));
        await service.SwitchToAsync(work.Id);
        Assert.Empty(service.Current.GetUnion());

        // Switch back — Default still has its item (the switch did not lose data).
        await service.SwitchToAsync(defaultId);
        Assert.Single(service.Current.GetUnion());
    }

    [Fact]
    public async Task SwitchToAsync_UpdatesActive_AndRaisesEventsInOrder()
    {
        using var outer = new TempDir();
        await using var service = CatalogService.Open(RegistryPath(outer), outer.Combine("default"));
        var work = service.Registry.Add("Work", outer.Combine("work"));

        var events = new List<string>();
        service.PreparingToSwitch += (_, _) => events.Add("preparing");
        service.ActiveLibraryChanged += (_, _) => events.Add("changed");

        await service.SwitchToAsync(work.Id);

        Assert.Equal(work.Id, service.ActiveCatalog!.Id);
        Assert.Equal(["preparing", "changed"], events);
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

        Assert.Same(current, service.Current); // library not rebuilt
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
    public async Task SwitchToAsync_BadTargetDir_KeepsCurrentActive_AndDoesNotTearDown()
    {
        using var outer = new TempDir();
        await using var service = CatalogService.Open(RegistryPath(outer), outer.Combine("default"));
        var current = service.Current;
        var activeId = service.ActiveCatalog!.Id;

        // Register a catalog whose "dir" is actually an existing file — opening it must fail.
        var filePath = outer.Combine("not-a-dir");
        File.WriteAllText(filePath, "x");
        var bad = service.Registry.Add("Bad", filePath);

        var preparing = false;
        service.PreparingToSwitch += (_, _) => preparing = true;

        await Assert.ThrowsAnyAsync<Exception>(() => service.SwitchToAsync(bad.Id));

        // Validation happens before any teardown: the current catalog is untouched and still active.
        Assert.Same(current, service.Current);
        Assert.Equal(activeId, service.ActiveCatalog!.Id);
        Assert.False(preparing);
        Assert.Empty(service.Current.GetUnion()); // current library still live and readable, no throw
    }

    [Fact]
    public async Task CreateAndSwitchAsync_RegistersAndSwitches()
    {
        using var outer = new TempDir();
        await using var service = CatalogService.Open(RegistryPath(outer), outer.Combine("default"));

        var entry = await service.CreateAndSwitchAsync("Work", outer.Combine("work"));

        Assert.Equal("Work", service.ActiveCatalog!.Name);
        Assert.Equal(entry.Id, service.ActiveCatalog!.Id);
        Assert.Equal(2, service.Registry.List().Count);
        Assert.Empty(service.Current.GetUnion());
    }

    [Fact]
    public async Task DisposeAsync_ShutsDownCurrent_SoFilesAreReleased()
    {
        using var outer = new TempDir();
        var defaultDir = outer.Combine("default");
        var service = CatalogService.Open(RegistryPath(outer), defaultDir);

        await service.DisposeAsync();

        // Current library's DB handles are released, so the catalog dir can be removed.
        Directory.Delete(defaultDir, recursive: true);
        Assert.False(Directory.Exists(defaultDir));
    }
}
