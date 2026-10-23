using System.Text.Json;

namespace Aperture.Core.Storage;

/// <summary>One registered library (catalog): a stable id, a display name, and its data directory.</summary>
public sealed class CatalogEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>The catalog's data directory (holds <c>aperture.db</c> + <c>thumbs.db</c> + <c>settings.json</c>).</summary>
    public string Dir { get; set; } = "";

    /// <summary>When this catalog was last made active (UTC ticks); 0 if never opened yet.</summary>
    public long LastOpenedTicks { get; set; }
}

/// <summary>On-disk shape of the registry file.</summary>
internal sealed class CatalogRegistryState
{
    public List<CatalogEntry> Catalogs { get; set; } = [];
    public string? ActiveId { get; set; }
}

/// <summary>
/// The set of libraries (catalogs) the user can switch between (Option B — separate catalogs).
/// Persisted as a single JSON file that lives <em>outside</em> any one catalog
/// (<c>%LOCALAPPDATA%\catalogs.json</c>), so it is shared across catalogs and a per-catalog
/// "delete library files" never wipes it. Config, not domain data — plain JSON, no FunkyORM,
/// tolerant of a missing or corrupt file, exactly like <c>SettingsService</c>.
/// </summary>
public sealed class CatalogRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly CatalogRegistryState _state;

    /// <summary><c>%LOCALAPPDATA%\catalogs.json</c> — the parent of the default data dir, not inside any catalog.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "catalogs.json");

    public CatalogRegistry(string registryPath)
    {
        _path = registryPath;
        _state = Load();
    }

    /// <summary>All registered catalogs, in registration order. Snapshots — mutating them does nothing.</summary>
    public IReadOnlyList<CatalogEntry> List()
    {
        lock (_gate) return _state.Catalogs.Select(Clone).ToList();
    }

    /// <summary>The active catalog, or <c>null</c> if none is registered.</summary>
    public CatalogEntry? Active
    {
        get
        {
            lock (_gate)
            {
                var a = _state.Catalogs.FirstOrDefault(c => c.Id == _state.ActiveId);
                return a is null ? null : Clone(a);
            }
        }
    }

    /// <summary>
    /// Registers <paramref name="dir"/> as a catalog and returns it. If the directory is already
    /// registered (path-normalized, case-insensitive) the existing entry is returned unchanged —
    /// no duplicate. The first catalog registered becomes active.
    /// </summary>
    public CatalogEntry Add(string name, string dir)
    {
        var full = NormalizeDir(dir);
        lock (_gate)
        {
            var existing = _state.Catalogs.FirstOrDefault(c => DirsEqual(c.Dir, full));
            if (existing is not null)
                return Clone(existing);

            var entry = new CatalogEntry { Id = NewId(), Name = name, Dir = full };
            _state.Catalogs.Add(entry);
            _state.ActiveId ??= entry.Id;
            Save();
            return Clone(entry);
        }
    }

    /// <summary>Renames a catalog. Returns <c>false</c> if no catalog has that id.</summary>
    public bool Rename(string id, string name)
    {
        lock (_gate)
        {
            var entry = _state.Catalogs.FirstOrDefault(c => c.Id == id);
            if (entry is null)
                return false;
            entry.Name = name;
            Save();
            return true;
        }
    }

    /// <summary>
    /// Removes a catalog from the registry. By default the files are left on disk (the catalog is
    /// only "forgotten"); pass <paramref name="deleteFiles"/> to also delete its directory — a
    /// destructive step the UI confirms separately. Removing the active catalog promotes another
    /// (or leaves none active). Returns <c>false</c> if no catalog has that id.
    /// </summary>
    public bool Remove(string id, bool deleteFiles = false)
    {
        string? dirToDelete = null;
        lock (_gate)
        {
            var entry = _state.Catalogs.FirstOrDefault(c => c.Id == id);
            if (entry is null)
                return false;

            _state.Catalogs.Remove(entry);
            if (_state.ActiveId == id)
                _state.ActiveId = _state.Catalogs.FirstOrDefault()?.Id;
            // Persist the forget first, so a failed/partial file delete can't leave a next run
            // showing a catalog whose files are gone.
            Save();
            if (deleteFiles)
                dirToDelete = entry.Dir;
        }
        if (dirToDelete is not null)
            TryDeleteDir(dirToDelete);
        return true;
    }

    /// <summary>Makes a catalog active and records the open time. Returns <c>false</c> if no catalog has that id.</summary>
    public bool SetActive(string id)
    {
        lock (_gate)
        {
            var entry = _state.Catalogs.FirstOrDefault(c => c.Id == id);
            if (entry is null)
                return false;
            _state.ActiveId = id;
            entry.LastOpenedTicks = DateTime.UtcNow.Ticks;
            Save();
            return true;
        }
    }

    /// <summary>
    /// Registers <paramref name="dir"/> as the "Default" catalog if it isn't registered yet, and
    /// makes it active if nothing is active. Idempotent — an already-registered dir keeps its name
    /// and is not duplicated. Called at startup with <see cref="AperturePaths.DefaultDataDir"/> so a
    /// pre-upgrade single-library install becomes the Default catalog with zero migration.
    /// </summary>
    public void EnsureDefault(string dir, string name = "Default")
    {
        var full = NormalizeDir(dir);
        lock (_gate)
        {
            var existing = _state.Catalogs.FirstOrDefault(c => DirsEqual(c.Dir, full));
            if (existing is null)
            {
                existing = new CatalogEntry { Id = NewId(), Name = name, Dir = full };
                _state.Catalogs.Add(existing);
            }
            _state.ActiveId ??= existing.Id;
            Save();
        }
    }

    private CatalogRegistryState Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<CatalogRegistryState>(File.ReadAllText(_path)) ?? new CatalogRegistryState();
        }
        catch
        {
            // Corrupt or unreadable — start empty rather than failing. Atomic Save (below) makes a
            // torn write that would land us here almost impossible in the first place.
        }
        return new CatalogRegistryState();
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            // Write-then-rename: the registry lists every library the user owns, so a half-written
            // file (crash/power loss mid-write) must never replace a good one and orphan catalogs.
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_state, JsonOptions));
            File.Move(tmp, _path, overwrite: true);
        }
        catch
        {
            // Persisting must never crash the app; the next successful mutation rewrites it.
        }
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static CatalogEntry Clone(CatalogEntry e) =>
        new() { Id = e.Id, Name = e.Name, Dir = e.Dir, LastOpenedTicks = e.LastOpenedTicks };

    private static string NormalizeDir(string dir) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));

    private static bool DirsEqual(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static void TryDeleteDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir))
                return;
            var full = Path.GetFullPath(dir);
            if (string.Equals(full, Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase))
                return; // never delete a drive root
            if (Directory.Exists(full))
                Directory.Delete(full, recursive: true);
        }
        catch
        {
            // Best-effort; the catalog is already forgotten in the registry.
        }
    }
}
