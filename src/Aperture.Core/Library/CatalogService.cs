using Aperture.Core.Storage;

namespace Aperture.Core.Library;

/// <summary>
/// Owns the catalog registry and the single live <see cref="LibraryService"/>, and switches the live
/// library between catalogs (Option B — separate catalogs). A switch tears the current library down
/// (cancel + await in-flight indexing, release the DB) and opens the target.
///
/// Invariant: <see cref="Current"/> always corresponds to <see cref="ActiveCatalog"/>. The host (App)
/// subscribes to <see cref="PreparingToSwitch"/> (to quiesce the view model before the old library is
/// disposed) and <see cref="ActiveLibraryChanged"/> (to rebind the UI to the new <see cref="Current"/>).
/// </summary>
public sealed class CatalogService : IAsyncDisposable
{
    private readonly CatalogRegistry _registry;
    private LibraryService _current;

    /// <summary>The set of catalogs and which is active.</summary>
    public CatalogRegistry Registry => _registry;

    /// <summary>The live library for the active catalog. Swapped by <see cref="SwitchToAsync"/>.</summary>
    public LibraryService Current => _current;

    /// <summary>The active catalog's registry entry.</summary>
    public CatalogEntry? ActiveCatalog => _registry.Active;

    /// <summary>
    /// Raised after a target is validated but before the current library is torn down, so the host can
    /// quiesce its view model (stop issuing index calls, drop subscriptions). Not raised for a no-op
    /// switch, nor when the target is invalid.
    /// </summary>
    public event EventHandler? PreparingToSwitch;

    /// <summary>Raised after <see cref="Current"/> has been swapped to the new catalog's library.</summary>
    public event EventHandler? ActiveLibraryChanged;

    private CatalogService(CatalogRegistry registry, LibraryService current)
    {
        _registry = registry;
        _current = current;
    }

    /// <summary>
    /// Opens the catalog service: registers <paramref name="defaultDataDir"/> as the "Default" catalog if
    /// the registry is empty (zero-migration for a pre-Option-B install), then opens the active catalog's
    /// library. The returned service's <see cref="Current"/> is live.
    /// </summary>
    public static CatalogService Open(string registryPath, string defaultDataDir)
    {
        var registry = new CatalogRegistry(registryPath);
        registry.EnsureDefault(defaultDataDir);
        var active = registry.Active
            ?? throw new InvalidOperationException("EnsureDefault must leave an active catalog.");
        return new CatalogService(registry, new LibraryService(active.Dir));
    }

    /// <summary>
    /// Switches the live library to another registered catalog. The target is validated <em>before</em> the
    /// current library is touched, so a bad target leaves the current catalog active and untouched. A switch
    /// to the already-active catalog is a no-op. Raises <see cref="PreparingToSwitch"/> then
    /// <see cref="ActiveLibraryChanged"/>.
    /// </summary>
    public async Task SwitchToAsync(string catalogId)
    {
        var target = _registry.List().FirstOrDefault(c => c.Id == catalogId)
            ?? throw new ArgumentException($"No catalog with id '{catalogId}'.", nameof(catalogId));
        if (target.Id == _registry.Active?.Id)
            return;

        EnsureUsable(target.Dir); // throws on a bad dir BEFORE any teardown, so the current catalog survives

        PreparingToSwitch?.Invoke(this, EventArgs.Empty);
        await _current.ShutdownAsync().ConfigureAwait(false);

        _current = new LibraryService(target.Dir);
        _registry.SetActive(target.Id);
        ActiveLibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Creates a new catalog at <paramref name="dir"/>, registers it, and switches to it.</summary>
    public async Task<CatalogEntry> CreateAndSwitchAsync(string name, string dir)
    {
        EnsureUsable(dir);
        var entry = _registry.Add(name, dir);
        await SwitchToAsync(entry.Id).ConfigureAwait(false);
        return entry;
    }

    private static void EnsureUsable(string dir)
    {
        if (File.Exists(dir))
            throw new InvalidOperationException($"'{dir}' is a file, not a catalog directory.");
        // Create-or-verify: a new catalog dir is created here; an unwritable/invalid path throws, and the
        // caller keeps the current catalog. (Opening still creates aperture.db on first use.)
        Directory.CreateDirectory(dir);
    }

    /// <summary>Shuts the current library down (cancel + await indexing, release the DB). For app exit.</summary>
    public async ValueTask DisposeAsync()
    {
        await _current.ShutdownAsync().ConfigureAwait(false);
    }
}
