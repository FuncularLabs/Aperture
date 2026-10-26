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

    /// <summary>
    /// The catalog's folder couldn't be found or reached (say, a drive that isn't connected), so nothing was
    /// deleted and it is still listed.
    /// </summary>
    FolderNotFound,
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
/// registry never deletes or overwrites anything that isn't Aperture's own — a file that isn't a readable
/// library list (corrupt, not JSON, or JSON without a <c>catalogs</c> list) is kept as a <c>.bad-*</c> copy
/// and reported in <see cref="QuarantinedFile"/>, and "delete files" removes only <see cref="CatalogFileNames"/>.
///
/// Every change is saved before it counts: <see cref="Add"/>, <see cref="Rename"/> and the removals report a
/// list they couldn't save and leave nothing changed, so memory and disk never disagree for later changes
/// to trip over. (<see cref="SetActive"/> and <see cref="Activate"/> keep this window's choice even if it
/// can't be saved; only the next launch's choice is lost.)
///
/// Several Aperture windows may share the file: every change (and every read of the file) runs under a
/// cross-process lock, and every change re-reads and merges first, so one window can't drop another's
/// additions. Reads (<see cref="List"/>, <see cref="Active"/>) use this instance's copy; <see cref="Reload"/>
/// refreshes it. The active catalog is this window's own; the file records the library the user most
/// recently switched to in any window, which the next launch opens. A fallback chosen only for this launch
/// (<see cref="Activate"/> with <c>rememberForNextLaunch: false</c>) is never written as that choice.
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
    private readonly Lock _gate = new();
    private List<CatalogEntry> _catalogs = [];
    private string? _activeId;    // the library this window has open
    private string? _preferredId; // the user's latest switch (any window) — what the next launch opens
    private bool _defaultSeeded;
    private bool _readFailed;     // the last read found the file but couldn't read it: never save over it

    /// <summary><c>%LOCALAPPDATA%\Aperture Libraries\catalogs.json</c> — outside every catalog's folder.</summary>
    public static string DefaultPath => Path.Combine(AperturePaths.LibrariesDir, "catalogs.json");

    /// <summary>
    /// The files Aperture keeps in a catalog's folder — with the lock file (<see cref="ApertureDatabase.LockFileName"/>),
    /// the only files "delete library files" removes. Anything else in the folder (the user's photos, their
    /// documents, a nested catalog) is left alone.
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
        MutexName = @"Local\Aperture.CatalogRegistry." + Convert.ToHexString(hash, 0, 16);
        Mutate(() => { RefreshFromDisk(keepActive: false); return true; }, refresh: false);
    }

    /// <summary>
    /// Set when this instance found the list file unreadable and moved it aside: the <c>.bad-*</c> copy's path,
    /// for the host to tell the user about. Otherwise <c>null</c>.
    /// </summary>
    public string? QuarantinedFile { get; private set; }

    /// <summary>
    /// True when the list file exists but this instance couldn't read it the last time it looked: another program
    /// had it locked for longer than a moment, access was denied, or it isn't a library list and couldn't be set
    /// aside. Until it can be read again nothing is saved over it: changes are refused, as for a read-only file,
    /// so a list this window couldn't see is never replaced by what it has in memory.
    /// </summary>
    public bool ReadFailed
    {
        get { lock (_gate) return _readFailed; }
    }

    /// <summary>The list file's full path.</summary>
    public string FilePath => _path;

    /// <summary>The cross-process lock every Aperture window takes before reading or writing the file.</summary>
    internal string MutexName { get; }

    /// <summary>How long to wait for another window's change before going ahead without the lock.</summary>
    internal TimeSpan LockTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Test seam: runs inside every change, after the file is re-read and before the change is made — the one
    /// window where a file that couldn't be read becoming writable again would let a stale copy be saved.
    /// </summary>
    internal Action? BetweenReadAndChange { get; set; }

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
    /// no duplicate. The first catalog registered becomes active. Throws <see cref="IOException"/> if the
    /// list can't be saved; then nothing was added.
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
            var (previousActive, previousPreferred) = (_activeId, _preferredId);
            _catalogs.Add(entry);
            _activeId ??= entry.Id;
            _preferredId ??= entry.Id;
            if (!Save())
            {
                _catalogs.Remove(entry);
                (_activeId, _preferredId) = (previousActive, previousPreferred);
                throw new IOException($"Couldn't save the list of libraries ({_path}).");
            }
            return Clone(entry);
        });
    }

    /// <summary>
    /// Renames a catalog. Returns <c>false</c> if no catalog has that id, or if the list couldn't be saved —
    /// then the old name stands, here and on disk.
    /// </summary>
    public bool Rename(string id, string name) => Mutate(() =>
    {
        var entry = _catalogs.FirstOrDefault(c => c.Id == id);
        if (entry is null)
            return false;
        var previous = entry.Name;
        entry.Name = name;
        if (Save())
            return true;
        entry.Name = previous;
        return false;
    });

    /// <summary>
    /// The user switched to this catalog: it becomes this window's active catalog and the one the next launch
    /// opens, and its open time is recorded. Returns <c>false</c> if no catalog has that id.
    /// </summary>
    public bool SetActive(string id) => Mutate(() =>
    {
        var entry = _catalogs.FirstOrDefault(c => c.Id == id);
        if (entry is null)
            return false;
        MakeActive(entry, rememberForNextLaunch: true);
        return true;
    });

    /// <summary>
    /// Makes <paramref name="entry"/> this window's active catalog — listing it again first if another window
    /// removed it meanwhile (the user just opened it here), so the active catalog is always listed. With
    /// <paramref name="rememberForNextLaunch"/> it also becomes what the next launch opens; without it (a launch
    /// fallback) the user's own choice is kept for next time. Returns the entry as listed.
    /// </summary>
    internal CatalogEntry Activate(CatalogEntry entry, bool rememberForNextLaunch) => Mutate(() =>
    {
        var listed = _catalogs.FirstOrDefault(c => c.Id == entry.Id)
                     ?? _catalogs.FirstOrDefault(c => SameDir(c.Dir, entry.Dir));
        if (listed is null)
        {
            listed = Clone(entry);
            _catalogs.Add(listed);
        }
        MakeActive(listed, rememberForNextLaunch);
        return Clone(listed);
    });

    /// <summary>Re-reads the list file, so this instance sees changes made in other Aperture windows.</summary>
    public void Reload() => Mutate(() => true);

    // Caller holds _gate. Best-effort save: this window's choice stands even if it can't be written.
    private void MakeActive(CatalogEntry entry, bool rememberForNextLaunch)
    {
        _activeId = entry.Id;
        if (rememberForNextLaunch)
        {
            _preferredId = entry.Id;
            entry.LastOpenedTicks = DateTime.UtcNow.Ticks;
        }
        Save();
    }

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
        // A folder that can't be found or reached (an unplugged drive) has nothing to delete — and forgetting the
        // library anyway would leave its tags and notes on that drive after the user confirmed deleting them.
        var known = List().FirstOrDefault(c => c.Id == id);
        if (known is not null && !Directory.Exists(known.Dir))
            return new CatalogDeletion(CatalogDeletionOutcome.FolderNotFound, []);

        var outcome = Mutate(() =>
        {
            if (_catalogs.All(c => c.Id != id))
                return (Entry: (CatalogEntry?)null, Outcome: CatalogDeletionOutcome.NotFound);
            var forgotten = Forget(id);
            return (Entry: forgotten, Outcome: forgotten is null ? CatalogDeletionOutcome.NotForgotten : CatalogDeletionOutcome.Deleted);
        });
        if (outcome.Entry is null)
            return new CatalogDeletion(outcome.Outcome, []);

        // The folder may have gone away since the check above (a drive unplugged mid-delete): then nothing could be
        // deleted, and the user must hear so rather than see a success while the tags and notes stay on that drive.
        if (!Directory.Exists(outcome.Entry.Dir))
            return new CatalogDeletion(CatalogDeletionOutcome.PartiallyDeleted, [outcome.Entry.Dir]);

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
            _preferredId ??= _activeId;
            Save(); // best effort: at launch an unwritable list must not stop Aperture opening its library
            return true;
        });
    }

    /// <summary>A directory path made absolute, with no trailing separator — the form entries are stored in.</summary>
    internal static string NormalizeDir(string dir) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));

    internal static bool SameDir(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Deletes Aperture's files in <paramref name="dir"/>, then (if <paramref name="removeEmptyFolder"/>) the
    /// folder if nothing else is in it. Returns the files that couldn't be deleted (in use, no permission) — all
    /// of them, untouched, if the library is open in any Aperture window. Never deletes anything else.
    /// </summary>
    internal static IReadOnlyList<string> DeleteCatalogFiles(string dir, bool removeEmptyFolder = true)
    {
        var left = new List<string>();
        if (!Directory.Exists(dir))
            return left;

        // Hold the library's lock exclusively while deleting: if any Aperture window has the library open — by
        // any path to this folder, in any process — delete nothing at all; otherwise none can open it meanwhile.
        // DeleteOnClose removes the lock file itself once the rest are gone.
        var lockFile = Path.Combine(dir, ApertureDatabase.LockFileName);
        FileStream exclusive;
        try
        {
            exclusive = new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.Read, FileShare.None,
                bufferSize: 1, FileOptions.DeleteOnClose);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing deleted. Name what's left — or, if the folder can't even be seen now, the folder itself, so the
            // caller never mistakes this for a delete with nothing left over.
            List<string> untouched = [.. CatalogFileNames.Append(ApertureDatabase.LockFileName).Select(n => Path.Combine(dir, n)).Where(File.Exists)];
            return untouched.Count > 0 ? untouched : [dir];
        }

        using (exclusive)
        {
            foreach (var name in CatalogFileNames)
            {
                var file = Path.Combine(dir, name);
                if (!File.Exists(file))
                    continue;
                try { File.Delete(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { left.Add(file); }
            }
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
        var (previousActive, previousPreferred) = (_activeId, _preferredId);
        _catalogs.RemoveAt(index);
        if (_activeId == id)
            _activeId = _catalogs.FirstOrDefault()?.Id;
        if (_preferredId == id)
            _preferredId = _activeId;
        if (Save())
            return entry;

        _catalogs.Insert(index, entry);
        (_activeId, _preferredId) = (previousActive, previousPreferred);
        return null;
    }

    // Every change: this process's lock, then the cross-process one, then (unless told not to) the latest file,
    // then the change. If another window holds the lock past LockTimeout it goes ahead anyway rather than hang.
    private T Mutate<T>(Func<T> change, bool refresh = true)
    {
        lock (_gate)
        {
            Mutex? mutex = null;
            var owned = false;
            try
            {
                var deadline = DateTime.UtcNow + LockTimeout;
                while (true)
                {
                    try
                    {
                        mutex = new Mutex(false, MutexName);
                        var left = deadline - DateTime.UtcNow;
                        owned = mutex.WaitOne(left > TimeSpan.Zero ? left : TimeSpan.Zero);
                        break;
                    }
                    catch (AbandonedMutexException)
                    {
                        owned = true; // the other window died mid-change; carry on
                        break;
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
                    {
                        // An elevated Aperture window is mid-change: the lock exists only while it holds it, and this
                        // window isn't allowed to open it. Wait for it to finish, as for any held lock — and after
                        // LockTimeout go ahead without it, rather than hang or fail the change (or the launch).
                        mutex?.Dispose();
                        mutex = null;
                        if (DateTime.UtcNow >= deadline)
                            break;
                        Thread.Sleep(25);
                    }
                }
                if (refresh)
                    RefreshFromDisk(keepActive: true);
                BetweenReadAndChange?.Invoke();
                return change();
            }
            finally
            {
                if (owned)
                    mutex!.ReleaseMutex();
                mutex?.Dispose();
            }
        }
    }

    // Loads the file into this instance. keepActive: after startup the active catalog is this instance's own
    // (another window switching must not change what this one is showing). Caller holds _gate.
    private void RefreshFromDisk(bool keepActive)
    {
        var (disk, unreadable) = ReadDisk();
        _readFailed = unreadable;
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

        // The library this window has open stays listed even if another window removed it meanwhile (its entry
        // is what ties this window's open library to the list); if another window re-added its folder under a
        // new id, this window adopts that id rather than losing track of what it has open.
        // (A window with no active library yet — its first read failed — takes the file's choice, not Default.)
        var active = keepActive && _activeId is not null ? _activeId : disk.ActiveId;
        var ours = keepActive ? _catalogs.FirstOrDefault(c => c.Id == _activeId) : null;
        if (ours is not null && catalogs.All(c => c.Id != ours.Id))
        {
            var sameFolder = catalogs.FirstOrDefault(c => SameDir(c.Dir, ours.Dir));
            if (sameFolder is not null)
                active = sameFolder.Id;
            else
                catalogs.Add(ours);
        }

        _catalogs = catalogs;
        _defaultSeeded |= disk.DefaultSeeded;
        _activeId = catalogs.Any(c => c.Id == active) ? active : null;
        // What the next launch opens is the user's latest switch in any window — the file's choice, while listed.
        if (catalogs.Any(c => c.Id == disk.ActiveId))
            _preferredId = disk.ActiveId;
        else if (catalogs.All(c => c.Id != _preferredId))
            _preferredId = _activeId;
    }

    // Returns the file's contents, or null with Unreadable = false when there is no file (or it was set aside as
    // not a library list), or null with Unreadable = true when it exists but can't be read right now — then the
    // caller must not save over it (see _readFailed).
    private (CatalogRegistryState? State, bool Unreadable) ReadDisk()
    {
        for (var attempt = 0; ; attempt++)
        {
            string json;
            try
            {
                json = File.ReadAllText(_path);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return (null, false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 5)
            {
                Thread.Sleep(50); // something (antivirus, a backup tool) has it open for a moment
                continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (null, true);
            }

            try
            {
                using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });
                // Valid JSON that isn't a library list (another program's file, a hand edit with a misspelled key)
                // would read as an empty list and be overwritten by the next save, so it's set aside too.
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object
                    && root.EnumerateObject().Any(p => string.Equals(p.Name, "catalogs", StringComparison.OrdinalIgnoreCase)))
                    return (root.Deserialize<CatalogRegistryState>(ReadOptions) ?? new CatalogRegistryState(), false);
            }
            catch (JsonException)
            {
                // Not JSON at all (corrupt, truncated); set aside below.
            }
            // Set aside, a fresh list starts. If it can't be moved aside, treat it as unreadable so it's never saved over.
            return (null, !QuarantineUnreadableFile());
        }
    }

    // Not a library list we can read (corrupt, or another program's file with this name): set it aside, never
    // overwrite it. Returns false if it couldn't be moved.
    private bool QuarantineUnreadableFile()
    {
        var bad = $"{_path}.bad-{DateTime.Now:yyyyMMdd-HHmmss-fff}";
        try
        {
            File.Move(_path, bad);
            QuarantinedFile = bad;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Write-then-rename, flushed: a crash or power loss mid-write can never replace a good list with a torn
    // one. Returns false if the list couldn't be saved; callers undo their change (or, for a switch, keep it
    // for this window only), so memory never holds a change the next re-read would silently drop.
    private bool Save()
    {
        if (_readFailed)
            return false; // what's on disk wasn't read, so writing this copy could drop another window's changes

        var tmp = _path + ".tmp";
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var state = new CatalogRegistryState
            {
                Catalogs = _catalogs.Select(c => (CatalogEntry?)Clone(c)).ToList(),
                ActiveId = _preferredId ?? _activeId,
                DefaultSeeded = _defaultSeeded,
            };
            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, state, WriteOptions);
                stream.Flush(flushToDisk: true);
            }
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(tmp, _path, overwrite: true);
                    return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 5)
                {
                    // Something (antivirus, a backup tool) has the file open for a moment — replacing it then
                    // fails with access denied. A genuinely read-only file fails the same way, just ~250 ms later.
                    Thread.Sleep(50);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A read-only, locked or unwritable list file: nothing changed on disk.
            try { if (File.Exists(tmp)) File.Delete(tmp); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            return false;
        }
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static CatalogEntry Clone(CatalogEntry e) =>
        new() { Id = e.Id, Name = e.Name, Dir = e.Dir, LastOpenedTicks = e.LastOpenedTicks };
}
