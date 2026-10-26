using System.Runtime.ExceptionServices;
using Aperture.Core.Storage;

namespace Aperture.Core.Library;

/// <summary>A library operation was refused for a reason the user can act on; the message says what and why.</summary>
public sealed class CatalogException(string message) : Exception(message);

/// <summary>
/// Owns the catalog registry and the single live <see cref="LibraryService"/>, and switches the live
/// library between catalogs (Option B — separate catalogs).
///
/// Invariants: <see cref="Current"/> always corresponds to <see cref="ActiveCatalog"/>, and the active catalog
/// is always listed; a failed switch leaves the current library open and untouched (the target is opened
/// <em>before</em> the current one is torn down); one switch runs at a time; a library that's open — in this
/// window or any other Aperture window — can't be removed or deleted; and a library is only registered if the
/// list could be saved. Change the catalogs through this class — calling <see cref="Registry"/>'s
/// <c>SetActive</c> or <c>Remove</c> directly bypasses those rules.
///
/// The host (App) handles <see cref="PreparingToSwitch"/> (quiesce its view model before the old library
/// closes) and <see cref="ActiveLibraryChanged"/> (rebind to the new <see cref="Current"/>). Both are raised
/// on the caller's synchronization context, so a switch started from the UI thread notifies on it.
/// Don't start another switch from inside either handler: switches run one at a time and it would wait forever.
/// </summary>
public sealed class CatalogService : IAsyncDisposable
{
    private readonly CatalogRegistry _registry;
    private readonly string _defaultDir;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LibraryService _current;

    /// <summary>The set of catalogs and which is active. Read it freely; change it through this service.</summary>
    public CatalogRegistry Registry => _registry;

    /// <summary>The live library for the active catalog. Swapped by a switch.</summary>
    public LibraryService Current => _current;

    /// <summary>The active catalog's registry entry.</summary>
    public CatalogEntry? ActiveCatalog => _registry.Active;

    /// <summary>
    /// Set when <see cref="Open"/> couldn't open the last-used library and opened another instead: says
    /// which one failed and why, for the host to show once its window is up. Otherwise <c>null</c>.
    /// </summary>
    public string? OpenWarning { get; }

    /// <summary>
    /// Raised once the target has opened and just before the current library is torn down, so the host
    /// can quiesce its view model. Not raised for a no-op switch or a target that can't be opened.
    /// </summary>
    public event EventHandler? PreparingToSwitch;

    /// <summary>Raised after <see cref="Current"/> has been swapped to the new catalog's library.</summary>
    public event EventHandler? ActiveLibraryChanged;

    private CatalogService(CatalogRegistry registry, string defaultDir, LibraryService current, string? openWarning)
    {
        _registry = registry;
        _defaultDir = defaultDir;
        _current = current;
        OpenWarning = openWarning;
    }

    /// <summary>
    /// Opens the catalog service at launch: registers <paramref name="defaultDataDir"/> as the "Default"
    /// catalog on first run (zero migration for a pre-Option-B install), then opens the active catalog.
    /// If that can't be opened (an unplugged drive, a deleted folder), it opens Default, else the most
    /// recently used library that does open, else re-registers Default — Aperture always starts — and
    /// says so in <see cref="OpenWarning"/>. Throws only if not even the local Default can be opened.
    /// </summary>
    public static CatalogService Open(string registryPath, string defaultDataDir)
    {
        var registry = new CatalogRegistry(registryPath);
        registry.EnsureDefault(defaultDataDir);
        var defaultDir = CatalogRegistry.NormalizeDir(defaultDataDir);
        var active = registry.Active
            ?? throw new InvalidOperationException("EnsureDefault must leave an active catalog.");

        var candidates = new List<CatalogEntry> { active };
        candidates.AddRange(registry.List()
            .Where(c => c.Id != active.Id)
            .OrderByDescending(c => CatalogRegistry.SameDir(c.Dir, defaultDir))
            .ThenByDescending(c => c.LastOpenedTicks));

        // A fallback is for this launch only (rememberForNextLaunch: false): the user's own choice stays what the
        // next launch tries first, so a drive that wasn't mounted yet at login doesn't lose it for good.
        ExceptionDispatchInfo? firstFailure = null;
        foreach (var candidate in candidates)
        {
            try
            {
                var library = OpenLibrary(candidate.Dir, allowCreate: CatalogRegistry.SameDir(candidate.Dir, defaultDir));
                if (candidate.Id != active.Id)
                    registry.Activate(candidate, rememberForNextLaunch: false);
                return new CatalogService(registry, defaultDir, library, Warning(registry, active, candidate, firstFailure));
            }
            catch (Exception ex)
            {
                firstFailure ??= ExceptionDispatchInfo.Capture(ex);
            }
        }

        // Nothing registered opens (say every library is on a drive that isn't connected, and the user
        // removed Default): bring back the local Default, which can always be created.
        if (!registry.List().Any(c => CatalogRegistry.SameDir(c.Dir, defaultDir)))
        {
            var fallback = new CatalogEntry { Id = Guid.NewGuid().ToString("N"), Name = UniqueName(registry, "Default"), Dir = defaultDir };
            try
            {
                var library = OpenLibrary(fallback.Dir, allowCreate: true);
                fallback = registry.Activate(fallback, rememberForNextLaunch: false);
                return new CatalogService(registry, defaultDir, library, Warning(registry, active, fallback, firstFailure));
            }
            catch (Exception ex)
            {
                firstFailure ??= ExceptionDispatchInfo.Capture(ex);
            }
        }

        firstFailure!.Throw();
        throw new InvalidOperationException("unreachable");
    }

    /// <summary>
    /// Switches the live library to another registered catalog. The target is opened first; if that fails
    /// the current library is left open and untouched and nothing is raised. A switch to the already-active
    /// catalog is a no-op. Raises <see cref="PreparingToSwitch"/> then <see cref="ActiveLibraryChanged"/>.
    /// </summary>
    public async Task SwitchToAsync(string catalogId)
    {
        await _gate.WaitAsync();
        try
        {
            var target = Find(catalogId);
            if (target.Id == _registry.Active?.Id)
                return;
            await CommitSwitchAsync(target, OpenLibrary(target.Dir));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Validates a name for a new library and returns it trimmed: not empty, usable as a folder name, and
    /// not already used by another library (case-insensitive). Throws <see cref="CatalogException"/> otherwise.
    /// </summary>
    public string CheckNewLibraryName(string name) => CheckName(name, exceptId: null, forFolder: true);

    /// <summary>
    /// Creates a new library in its own folder, <paramref name="parentDir"/>\<paramref name="name"/>, registers
    /// it and switches to it. The folder must not exist yet, or be empty — a new library never moves into a
    /// folder that already holds files. Nothing is registered unless the library opens.
    /// </summary>
    public async Task<CatalogEntry> CreateLibraryAsync(string name, string parentDir)
    {
        await _gate.WaitAsync();
        try
        {
            var trimmed = CheckNewLibraryName(name);
            var dir = CatalogRegistry.NormalizeDir(Path.Combine(parentDir, trimmed));
            if (File.Exists(dir))
                throw new CatalogException($"There's already a file called \"{trimmed}\" in {parentDir}. Choose another name.");
            var existed = Directory.Exists(dir);
            if (existed && Directory.EnumerateFileSystemEntries(dir).Any())
                throw new CatalogException(
                    $"The folder {dir} already exists and has files in it. A new library needs a folder of its own — choose another name or location.");

            LibraryService next;
            try
            {
                next = new LibraryService(dir);
            }
            catch
            {
                // Undo only what this attempt made: Aperture's files, and the folder if this attempt created it.
                CatalogRegistry.DeleteCatalogFiles(dir, removeEmptyFolder: !existed);
                throw;
            }

            CatalogEntry entry;
            try
            {
                entry = _registry.Add(trimmed, dir);
            }
            catch (IOException)
            {
                next.Dispose();
                CatalogRegistry.DeleteCatalogFiles(dir, removeEmptyFolder: !existed);
                throw new CatalogException("Aperture couldn't save its list of libraries, so the new library wasn't created.");
            }
            await CommitSwitchAsync(entry, next);
            return entry;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Opens an existing library — a folder that already holds <c>aperture.db</c> — registering it if it
    /// isn't listed yet (named after its folder, made unique), and switches to it. A folder without a
    /// library is refused rather than turned into an empty one. Nothing is registered unless it opens.
    /// </summary>
    public async Task<CatalogEntry> OpenLibraryAsync(string dir)
    {
        var full = CatalogRegistry.NormalizeDir(dir);
        await _gate.WaitAsync();
        try
        {
            _registry.Reload(); // another window may have added (or removed) this folder meanwhile
            var known = _registry.List().FirstOrDefault(c => CatalogRegistry.SameDir(c.Dir, full));
            if (known is not null)
            {
                if (known.Id != _registry.Active?.Id)
                    await CommitSwitchAsync(known, OpenLibrary(known.Dir));
                return known;
            }

            if (!File.Exists(Path.Combine(full, ApertureDatabase.MetadataFileName)))
                throw new CatalogException($"{full} doesn't contain an Aperture library. To start a new one, use New library….");

            var next = new LibraryService(full);
            var folderName = Path.GetFileName(full);
            CatalogEntry entry;
            try
            {
                entry = _registry.Add(UniqueName(_registry, string.IsNullOrEmpty(folderName) ? "Library" : folderName), full);
            }
            catch (IOException)
            {
                next.Dispose();
                throw new CatalogException("Aperture couldn't save its list of libraries, so the library wasn't added.");
            }
            await CommitSwitchAsync(entry, next);
            return entry;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Renames a library (trimmed; not empty; not another library's name, case-insensitive). Throws
    /// <see cref="CatalogException"/> if another window already removed it, or the list couldn't be saved.
    /// </summary>
    public void Rename(string catalogId, string name)
    {
        var entry = Find(catalogId);
        if (!_registry.Rename(entry.Id, CheckName(name, entry.Id, forFolder: false)))
            throw StillListed(entry)
                ? new CatalogException("Aperture couldn't save its list of libraries, so the library wasn't renamed.")
                : AlreadyRemoved(entry);
    }

    /// <summary>
    /// Forgets a library that isn't open — in this window or any other; its files stay where they are.
    /// Throws <see cref="CatalogException"/> if it's open, already removed, or the list couldn't be saved.
    /// </summary>
    public async Task RemoveAsync(string catalogId)
    {
        await _gate.WaitAsync();
        try
        {
            var entry = Find(catalogId);
            RefuseIfOpen(entry);
            if (!_registry.Remove(entry.Id))
                throw StillListed(entry)
                    ? new CatalogException("Aperture couldn't save its list of libraries, so nothing was removed.")
                    : AlreadyRemoved(entry);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Forgets a library that isn't open — in this window or any other — and deletes Aperture's own files for
    /// it (index, thumbnail cache, settings), never anything else in its folder. Throws
    /// <see cref="CatalogException"/> if it's open, already removed, or the list couldn't be saved (then nothing
    /// is deleted). The result lists any files that couldn't be deleted.
    /// </summary>
    public async Task<CatalogDeletion> DeleteFilesAsync(string catalogId)
    {
        await _gate.WaitAsync();
        try
        {
            var entry = Find(catalogId);
            RefuseIfOpen(entry);
            var result = _registry.RemoveAndDeleteFiles(entry.Id);
            return result.Outcome switch
            {
                CatalogDeletionOutcome.NotForgotten => throw new CatalogException(
                    "Aperture couldn't save its list of libraries, so nothing was removed or deleted."),
                CatalogDeletionOutcome.NotFound => throw AlreadyRemoved(entry),
                _ => result,
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Shuts the current library down (cancel + await indexing, release the DB). For app exit.</summary>
    public async ValueTask DisposeAsync()
    {
        // ConfigureAwait(false): App.OnExit blocks the UI thread on this, so nothing here may need it.
        await _current.ShutdownAsync().ConfigureAwait(false);
    }

    // The caller holds the gate and has already opened `next`, so nothing can fail between tearing the current
    // library down and showing the next one: Activate lists the target again if another window removed it, so
    // Current and ActiveCatalog always agree. Awaits on the caller's context (no ConfigureAwait(false)): the
    // host rebinds its window in ActiveLibraryChanged, which has to run on the UI thread.
    private async Task CommitSwitchAsync(CatalogEntry target, LibraryService next)
    {
        try
        {
            PreparingToSwitch?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            next.Dispose();
            throw;
        }

        var previous = _current;
        await previous.ShutdownAsync();
        _current = next;
        _registry.Activate(target, rememberForNextLaunch: true);
        ActiveLibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    private LibraryService OpenLibrary(string dir) =>
        OpenLibrary(dir, allowCreate: CatalogRegistry.SameDir(dir, _defaultDir));

    // Only the local Default may be created on the spot (a fresh install, or its folder was deleted). Any
    // other library must still be there: a missing one is reported, not silently recreated empty — that's
    // what a disconnected drive, or a drive letter now pointing at a different disk, would otherwise do.
    private static LibraryService OpenLibrary(string dir, bool allowCreate)
    {
        if (File.Exists(dir))
            throw new CatalogException($"{dir} is a file, not a library folder.");
        if (!allowCreate && !File.Exists(Path.Combine(dir, ApertureDatabase.MetadataFileName)))
            throw new CatalogException(
                $"There's no Aperture library in {dir} any more. It may have been moved or deleted, or be on a drive that isn't connected.");
        return new LibraryService(dir);
    }

    private CatalogEntry Find(string catalogId) =>
        _registry.List().FirstOrDefault(c => c.Id == catalogId)
        ?? throw new ArgumentException($"No library has the id '{catalogId}'.", nameof(catalogId));

    private void RefuseIfOpen(CatalogEntry entry)
    {
        if (entry.Id == _registry.Active?.Id)
            throw new CatalogException($"\"{entry.Name}\" is the library that's open. Switch to another library first.");
        if (LibraryService.IsOpen(entry.Dir))
            throw new CatalogException(
                $"\"{entry.Name}\" is open in another Aperture window, or in this one under a different folder path. Close it there first.");
    }

    // After a registry change reported failure: the change re-read the list under the cross-process lock, so if the
    // library is gone now, another window removed it — say so, rather than blaming a save that never ran.
    private bool StillListed(CatalogEntry entry) => _registry.List().Any(c => c.Id == entry.Id);

    private static CatalogException AlreadyRemoved(CatalogEntry entry) =>
        new($"\"{entry.Name}\" was already removed from the list (perhaps in another Aperture window), so nothing was changed.");

    // Names Windows reserves for devices: as a folder name they fail, or make a folder some tools can't open.
    // (The full list from Microsoft's "Naming files, paths, and namespaces", superscript digits included.)
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    private string CheckName(string name, string? exceptId, bool forFolder)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0)
            throw new CatalogException("Give the library a name.");
        if (forFolder && trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new CatalogException("A library name can't contain any of these characters: \\ / : * ? \" < > |");
        if (forFolder && trimmed.EndsWith('.'))
            throw new CatalogException("A library name can't end with a full stop.");
        if (forFolder && ReservedNames.Contains(trimmed.Split('.')[0].Trim()))
            throw new CatalogException($"\"{trimmed}\" is a name Windows reserves, so it can't be a folder name. Choose another.");
        if (_registry.List().Any(c => c.Id != exceptId && string.Equals(c.Name, trimmed, StringComparison.CurrentCultureIgnoreCase)))
            throw new CatalogException($"There's already a library named \"{trimmed}\".");
        return trimmed;
    }

    private static string UniqueName(CatalogRegistry registry, string baseName)
    {
        var taken = registry.List().Select(c => c.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        if (!taken.Contains(baseName))
            return baseName;
        for (var n = 2; ; n++)
        {
            var candidate = $"{baseName} ({n})";
            if (!taken.Contains(candidate))
                return candidate;
        }
    }

    private static string? Warning(CatalogRegistry registry, CatalogEntry failed, CatalogEntry opened, ExceptionDispatchInfo? failure)
    {
        var parts = new List<string>();
        if (registry.ReadFailed)
            parts.Add($"Aperture couldn't read its list of libraries just now (another program had the file open), so " +
                      $"it opened \"{opened.Name}\" and won't change the list until it can read it. Restart Aperture to " +
                      "get your other libraries back.");
        if (registry.QuarantinedFile is { } bad)
            parts.Add($"Aperture couldn't read its list of libraries, so it set the file aside as {bad} and started a " +
                      "new list. To get your other libraries back, use Open library… and choose each library's folder.");
        if (failure is not null)
            parts.Add($"Aperture couldn't open the library \"{failed.Name}\" ({failed.Dir}), so it opened \"{opened.Name}\" " +
                      $"for now. It will try \"{failed.Name}\" again next time — if it's gone for good, use Remove from " +
                      $"list on it.\n\n{failure.SourceException.Message}");
        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }
}
