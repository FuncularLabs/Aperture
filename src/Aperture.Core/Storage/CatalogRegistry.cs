using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aperture.Core.Settings;

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

/// <summary>What <see cref="CatalogRegistry.RemoveAndDeleteFiles"/> did.</summary>
public enum CatalogDeletionOutcome
{
    /// <summary>No catalog has that id; nothing was changed.</summary>
    NotFound,

    /// <summary>The library list couldn't be saved, so the catalog is still listed and nothing was deleted.</summary>
    NotForgotten,

    /// <summary>The catalog was removed from the list and all of Aperture's files for it were deleted.</summary>
    Deleted,

    /// <summary>The catalog was removed from the list, but some of its files couldn't be deleted (see FilesLeft).</summary>
    PartiallyDeleted,
}

/// <summary>The result of removing a catalog and deleting its files.</summary>
public sealed record CatalogDeletion(CatalogDeletionOutcome Outcome, IReadOnlyList<string> FilesLeft);

/// <summary>On-disk shape of the registry file.</summary>
internal sealed class CatalogRegistryState
{
    public List<CatalogEntry?>? Catalogs { get; set; } = [];
    public string? ActiveId { get; set; }

    /// <summary>True once the Default catalog has been registered, so removing it later sticks.</summary>
    public bool DefaultSeeded { get; set; }
}

/// <summary>
/// The set of libraries (catalogs) the user can switch between (Option B — separate catalogs).
/// Persisted as one JSON file outside every catalog (<see cref="DefaultPath"/>). Config, not domain
/// data — plain JSON, no FunkyORM.
///
/// Safety rules: a missing, corrupt or hand-edited file never stops Aperture from starting, and the
/// registry never deletes or overwrites anything that isn't Aperture's own — an unreadable file is kept
/// as a <c>.bad-*</c> copy, and "delete files" removes only <see cref="CatalogFileNames"/>.
///
/// Several Aperture windows may share the file: every change re-reads it under a cross-process lock and
/// merges, so one window can't drop another's additions. Reads (<see cref="List"/>, <see cref="Active"/>)
/// use this instance's copy, and the active catalog is this instance's own — the file records the one
/// most recently switched to, which the next launch opens.
/// </summary>
public sealed class CatalogRegistry
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _path;
    private readonly string _mutexName;
    private readonly Lock _gate = new();
    private List<CatalogEntry> _catalogs = [];
    private string? _activeId;
    private bool _defaultSeeded;

    /// <summary><c>%LOCALAPPDATA%\Aperture Libraries\catalogs.json</c> — outside every catalog's folder.</summary>
    public static string DefaultPath => Path.Combine(AperturePaths.LibrariesDir, "catalogs.json");

    /// <summary>
    /// The files Aperture keeps in a catalog's folder — the only files "delete library files" removes.
    /// Anything else in the folder (the user's photos, their documents, a nested catalog) is left alone.
    /// </summary>
    public static IReadOnlyList<string> CatalogFileNames { get; } =
    [
        ApertureDatabase.MetadataFileName,
        ApertureDatabase.MetadataFileName + "-wal",
        ApertureDatabase.MetadataFileName + "-shm",
        ApertureDatabase.MetadataFileName + "-journal",
        ApertureDatabase.ThumbnailFileName,
        ApertureDatabase.ThumbnailFileName + "-wal",
        ApertureDatabase.ThumbnailFileName + "-shm",
        ApertureDatabase.ThumbnailFileName + "-journal",
        SettingsService.FileName,
    ];

    public CatalogRegistry(string registryPath)
    {
        _path = Path.GetFullPath(registryPath);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(_path.ToUpperInvariant()));
        _mutexName = @"Local\Aperture.CatalogRegistry." + Convert.ToHexString(hash, 0, 16);
        RefreshFromDisk(keepActive: false);
    }

    /// <summary>All registered catalogs, in registration order. Snapshots — mutating them does nothing.</summary>
    public IReadOnlyList<CatalogEntry> List()
    {
        lock (_gate) return _catalogs.Select(Clone).ToList();
    }

    /// <summary>The active catalog, or <c>null</c> if none is registered.</summary>
    public CatalogEntry? Active
    {
        get
        {
            lock (_gate)
            {
                var active = _catalogs.FirstOrDefault(c => c.Id == _activeId);
                return active is null ? null : Clone(active);
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
        return Mutate(() =>
        {
            var existing = _catalogs.FirstOrDefault(c => SameDir(c.Dir, full));
            if (existing is not null)
                return Clone(existing);

            var entry = new CatalogEntry { Id = NewId(), Name = name, Dir = full };
            _catalogs.Add(entry);
            _activeId ??= entry.Id;
            Save();
            return Clone(entry);
        });
    }

    /// <summary>Renames a catalog. Returns <c>false</c> if no catalog has that id.</summary>
    public bool Rename(string id, string name) => Mutate(() =>
    {
        var entry = _catalogs.FirstOrDefault(c => c.Id == id);
        if (entry is null)
            return false;
        entry.Name = name;
        Save();
        return true;
    });

    /// <summary>Makes a catalog active and records the open time. Returns <c>false</c> if no catalog has that id.</summary>
    public bool SetActive(string id) => Mutate(() =>
    {
        var entry = _catalogs.FirstOrDefault(c => c.Id == id);
        if (entry is null)
            return false;
        _activeId = id;
        entry.LastOpenedTicks = DateTime.UtcNow.Ticks;
        Save();
        return true;
    });

    /// <summary>
    /// Forgets a catalog; its files stay on disk. Removing the active catalog promotes another (or leaves
    /// none active). Returns <c>false</c> if no catalog has that id, or if the list couldn't be saved — then
    /// the catalog is still listed, here and on disk.
    /// </summary>
    public bool Remove(string id) => Mutate(() => Forget(id) is not null);

    /// <summary>
    /// Forgets a catalog, then deletes Aperture's own files for it (<see cref="CatalogFileNames"/>) and its
    /// folder if that leaves it empty. Nothing else in the folder is touched. Files are deleted only after
    /// the forget is saved, so a failure can never leave a listed catalog whose files are gone.
    /// </summary>
    public CatalogDeletion RemoveAndDeleteFiles(string id)
    {
        var outcome = Mutate(() =>
        {
            if (_catalogs.All(c => c.Id != id))
                return (Entry: (CatalogEntry?)null, Outcome: CatalogDeletionOutcome.NotFound);
            var forgotten = Forget(id);
            return (Entry: forgotten, Outcome: forgotten is null ? CatalogDeletionOutcome.NotForgotten : CatalogDeletionOutcome.Deleted);
        });
        if (outcome.Entry is null)
            return new CatalogDeletion(outcome.Outcome, []);

        var left = DeleteCatalogFiles(outcome.Entry.Dir);
        return new CatalogDeletion(left.Count == 0 ? CatalogDeletionOutcome.Deleted : CatalogDeletionOutcome.PartiallyDeleted, left);
    }

    /// <summary>
    /// Registers <paramref name="dir"/> as the "Default" catalog on first run (so a pre-Option-B install
    /// becomes the Default catalog with zero migration), and makes something active if nothing is. Once
    /// seeded, a Default the user removed is not brought back — unless no catalog at all is registered,
    /// because Aperture always needs one library to open. An already-registered dir keeps its name.
    /// </summary>
    public void EnsureDefault(string dir, string name = "Default")
    {
        var full = NormalizeDir(dir);
        Mutate(() =>
        {
            if (!_defaultSeeded || _catalogs.Count == 0)
            {
                if (!_catalogs.Any(c => SameDir(c.Dir, full)))
                    _catalogs.Add(new CatalogEntry { Id = NewId(), Name = name, Dir = full });
                _defaultSeeded = true;
            }
            _activeId ??= (_catalogs.FirstOrDefault(c => SameDir(c.Dir, full)) ?? _catalogs.FirstOrDefault())?.Id;
            Save();
            return true;
        });
    }

    /// <summary>A directory path made absolute, with no trailing separator — the form entries are stored in.</summary>
    internal static string NormalizeDir(string dir) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));

    internal static bool SameDir(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Deletes Aperture's files in <paramref name="dir"/>, then (if <paramref name="removeEmptyFolder"/>) the
    /// folder if nothing else is in it. Returns the files that couldn't be deleted (in use, no permission).
    /// Never deletes anything else.
    /// </summary>
    internal static IReadOnlyList<string> DeleteCatalogFiles(string dir, bool removeEmptyFolder = true)
    {
        var left = new List<string>();
        if (!Directory.Exists(dir))
            return left;

        foreach (var name in CatalogFileNames)
        {
            var file = Path.Combine(dir, name);
            if (!File.Exists(file))
                continue;
            try { File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { left.Add(file); }
        }

        if (left.Count == 0 && removeEmptyFolder)
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The folder stays (a drive root, or something appeared in it); Aperture's files are gone.
            }
        }
        return left;
    }

    // Removes the entry and saves; on a failed save puts it back so memory matches disk. Caller holds _gate.
    private CatalogEntry? Forget(string id)
    {
        var index = _catalogs.FindIndex(c => c.Id == id);
        if (index < 0)
            return null;
        var entry = _catalogs[index];
        var previousActive = _activeId;
        _catalogs.RemoveAt(index);
        if (_activeId == id)
            _activeId = _catalogs.FirstOrDefault()?.Id;
        if (Save())
            return entry;

        _catalogs.Insert(index, entry);
        _activeId = previousActive;
        return null;
    }

    // Every change: this process's lock, then the cross-process one, then the latest file, then the change.
    private T Mutate<T>(Func<T> change)
    {
        lock (_gate)
        {
            using var mutex = new Mutex(false, _mutexName);
            var owned = false;
            try
            {
                try { owned = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
                catch (AbandonedMutexException) { owned = true; } // the other window died mid-change; carry on
                RefreshFromDisk(keepActive: true);
                return change();
            }
            finally
            {
                if (owned)
                    mutex.ReleaseMutex();
            }
        }
    }

    // Loads the file into this instance. keepActive: after startup the active catalog is this instance's own
    // (another window switching must not change what this one is showing). Caller holds _gate.
    private void RefreshFromDisk(bool keepActive)
    {
        var disk = ReadDisk();
        if (disk is null)
            return;

        var catalogs = new List<CatalogEntry>();
        foreach (var c in disk.Catalogs ?? [])
        {
            if (c is null || string.IsNullOrWhiteSpace(c.Id) || string.IsNullOrWhiteSpace(c.Dir) || !Path.IsPathFullyQualified(c.Dir))
                continue; // can't be opened safely: a relative dir would resolve against the working directory
            string full;
            try { full = NormalizeDir(c.Dir); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            if (catalogs.Any(x => x.Id == c.Id || SameDir(x.Dir, full)))
                continue;
            var name = string.IsNullOrWhiteSpace(c.Name) ? Path.GetFileName(full) : c.Name;
            catalogs.Add(new CatalogEntry { Id = c.Id, Name = name, Dir = full, LastOpenedTicks = c.LastOpenedTicks });
        }

        // The library this window has open stays listed even if another window removed it meanwhile:
        // its entry is what ties this window's open library to the list.
        var ours = keepActive ? _catalogs.FirstOrDefault(c => c.Id == _activeId) : null;
        if (ours is not null && !catalogs.Any(c => c.Id == ours.Id || SameDir(c.Dir, ours.Dir)))
            catalogs.Add(ours);

        _catalogs = catalogs;
        _defaultSeeded |= disk.DefaultSeeded;
        var active = keepActive ? _activeId : disk.ActiveId;
        _activeId = catalogs.Any(c => c.Id == active) ? active : null;
    }

    private CatalogRegistryState? ReadDisk()
    {
        for (var attempt = 0; ; attempt++)
        {
            string json;
            try
            {
                if (!File.Exists(_path))
                    return null;
                json = File.ReadAllText(_path);
            }
            catch (IOException) when (attempt < 3)
            {
                Thread.Sleep(50); // another window is replacing the file right now
                continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null; // unreadable for now; this instance's copy stands
            }

            try
            {
                return JsonSerializer.Deserialize<CatalogRegistryState>(json, ReadOptions) ?? new CatalogRegistryState();
            }
            catch (JsonException)
            {
                QuarantineUnreadableFile();
                return null;
            }
        }
    }

    // Not JSON we can read (corrupt, or another program's file with this name): set it aside, never overwrite it.
    private void QuarantineUnreadableFile()
    {
        try { File.Move(_path, $"{_path}.bad-{DateTime.Now:yyyyMMdd-HHmmss-fff}"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // Write-then-rename, flushed: a crash or power loss mid-write can never replace a good list with a torn
    // one. Returns false if the list couldn't be saved (callers that delete files check this first).
    private bool Save()
    {
        var tmp = _path + ".tmp";
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var state = new CatalogRegistryState
            {
                Catalogs = _catalogs.Select(c => (CatalogEntry?)Clone(c)).ToList(),
                ActiveId = _activeId,
                DefaultSeeded = _defaultSeeded,
            };
            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, state, WriteOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(tmp, _path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A read-only or locked list file: nothing changed on disk; the next successful change rewrites it.
            try { if (File.Exists(tmp)) File.Delete(tmp); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            return false;
        }
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static CatalogEntry Clone(CatalogEntry e) =>
        new() { Id = e.Id, Name = e.Name, Dir = e.Dir, LastOpenedTicks = e.LastOpenedTicks };
}
