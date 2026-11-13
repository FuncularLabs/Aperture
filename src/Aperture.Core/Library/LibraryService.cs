using Aperture.Core.Indexing;
using Aperture.Core.Media;
using Aperture.Core.Models;
using Aperture.Core.Settings;
using Aperture.Core.Storage;
using Aperture.Core.Watching;

namespace Aperture.Core.Library;

/// <summary>Whether some Aperture window has a library open (see <see cref="LibraryService.OpenState"/>).</summary>
public enum LibraryOpenState
{
    /// <summary>No Aperture window has it open (or its folder can't be reached).</summary>
    NotOpen,

    /// <summary>An Aperture window has it open.</summary>
    Open,

    /// <summary>Can't tell: its lock file exists but can't be read.</summary>
    Unknown,
}

/// <summary>
/// The single entry point the UI talks to. Wraps the stores, the indexer and the
/// per-root watchers so a view model depends on one object, not six. All methods
/// are synchronous and cheap except <see cref="IndexRoot"/>, which the caller is
/// expected to run on a background thread.
/// </summary>
public sealed class LibraryService : IDisposable
{
    private readonly ApertureDatabase _db;
    private readonly RootStore _roots;
    private readonly ItemStore _items;
    private readonly ThumbnailStore _thumbnails;
    private readonly AnnotationStore _annotations;
    private readonly HiddenFolderStore _hidden;
    private readonly Annotations.AnnotationTransfer _transfer;
    private readonly Indexer _indexer;
    private readonly ThumbnailGenerator _thumbnailGen = new();
    private readonly Dictionary<long, RootWatcher> _watchers = [];
    private readonly Lock _watchersLock = new();

    // Catalog-switch teardown: in-flight indexes are tracked so a shutdown can cancel them and wait for
    // them to settle before the database is disposed, rather than racing a half-written transaction.
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly List<Task> _indexTasks = [];
    private readonly List<Task> _readTasks = []; // the window's reads in the background: waited for too (guarded by _indexLock)
    private readonly Lock _indexLock = new();
    private volatile bool _shuttingDown; // read lock-free by GetOrCreateThumbnail; written under _indexLock
    private bool _tornDown;
    private bool _watchersClosed; // guarded by _watchersLock: no watcher may start once teardown stopped them

    // The library's lock file (ApertureDatabase.LockFileName), held for as long as this library is open: shared
    // with other holders (two windows may open one library), never with an exclusive opener. A file lock belongs
    // to the physical file, so every path to the folder (a junction, a mapped drive, an 8.3 name) and every
    // process (elevated or not) sees it, and Windows releases it if the process dies. See IsOpen.
    private readonly FileStream _openLock;

    /// <summary>Raised (on a background thread) when a watched root's files change on disk. Argument is the root id.</summary>
    public event EventHandler<long>? RootChanged;

    /// <summary>User settings (video inclusion, caption format, sort, etc.).</summary>
    public SettingsService Settings { get; }

    public LibraryService() : this(AperturePaths.DefaultDataDir) { }

    public LibraryService(string dataDir) : this(dataDir, mustExist: false) { }

    /// <param name="dataDir">The library's folder.</param>
    /// <param name="mustExist">
    /// The library must already be there — true when opening an existing library; false for the local Default, a
    /// brand-new library, and the pinned <c>APERTURE_DATA_DIR</c> folder, which may be created on the spot. Checked
    /// again once its lock is held, so a library another window deleted while this one waited for the lock is
    /// reported, not recreated empty; and the folder is never created.
    /// </param>
    internal LibraryService(string dataDir, bool mustExist)
    {
        ApertureDatabase? db = null;
        FileStream? openLock = null;
        try
        {
            if (!mustExist)
                Directory.CreateDirectory(dataDir);
            openLock = HoldOpenLock(dataDir); // before any database file is opened; fails if the folder is gone
            if (mustExist && !File.Exists(Path.Combine(dataDir, ApertureDatabase.MetadataFileName)))
            {
                openLock.Dispose();
                openLock = null;
                TryDeleteStrayLock(dataDir);
                throw new IOException($"There's no Aperture library in {dataDir} any more.");
            }
            db = new ApertureDatabase(dataDir);
            db.Initialize();
            _db = db;
            _openLock = openLock;
            _roots = new RootStore(_db);
            _items = new ItemStore(_db);
            _thumbnails = new ThumbnailStore(_db);
            _annotations = new AnnotationStore(_db);
            _hidden = new HiddenFolderStore(_db);
            _annotations.EnsureHyphenated(); // migrate legacy "multi word" tags to "multi-word"
            _transfer = new Annotations.AnnotationTransfer(_annotations, _roots);
            _indexer = new Indexer(_db, _thumbnailGen, new MetadataReader());
            Settings = new SettingsService(dataDir);
        }
        catch
        {
            // A library that can't be opened (not a database, unwritable, locked) must not keep its
            // files open, or look open to other windows: the user may want to fix, move or delete them.
            db?.Dispose();
            openLock?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Whether any Aperture window has the library in <paramref name="dataDir"/> open — this process or another,
    /// elevated or not, and whatever path it used to reach the folder (case, trailing separator, a junction, a
    /// mapped drive, an 8.3 name). <see cref="LibraryOpenState.NotOpen"/> also for a folder that doesn't exist or
    /// can't be reached; <see cref="LibraryOpenState.Unknown"/> when its lock file exists but can't be read
    /// (access denied — say, a folder copied from another PC). This touches the folder, which on a network share
    /// that has gone away can take tens of seconds: don't call it on the UI thread.
    /// </summary>
    public static LibraryOpenState OpenState(string dataDir)
    {
        try
        {
            using var probe = new FileStream(Path.Combine(dataDir, ApertureDatabase.LockFileName),
                FileMode.Open, FileAccess.Read, FileShare.None);
            return LibraryOpenState.NotOpen; // nobody holds it
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return LibraryOpenState.NotOpen;
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            return LibraryOpenState.Open;
        }
        catch (UnauthorizedAccessException)
        {
            return LibraryOpenState.Unknown;
        }
        catch (IOException)
        {
            return LibraryOpenState.NotOpen; // the folder can't be reached (drive not ready): nothing has it open from here
        }
    }

    /// <summary>
    /// The conservative yes/no of <see cref="OpenState"/>: true unless the library is known not to be open, so
    /// nothing is ever deleted on a guess.
    /// </summary>
    public static bool IsOpen(string dataDir) => OpenState(dataDir) != LibraryOpenState.NotOpen;

    // A lock file left by a failed open of a library that turned out to be gone: not anyone's, safe to remove.
    private static void TryDeleteStrayLock(string dataDir)
    {
        try { File.Delete(Path.Combine(dataDir, ApertureDatabase.LockFileName)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static FileStream HoldOpenLock(string dataDir)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return new FileStream(Path.Combine(dataDir, ApertureDatabase.LockFileName),
                    FileMode.OpenOrCreate, FileAccess.Read, FileShare.Read);
            }
            catch (IOException ex) when (IsSharingViolation(ex) && attempt < 5)
            {
                Thread.Sleep(50); // another window's OpenState check holds it for an instant
            }
            catch (IOException ex) when (IsSharingViolation(ex))
            {
                throw new IOException(
                    $"The library in {dataDir} can't be opened right now: it's being deleted, or another program has its {ApertureDatabase.LockFileName} open.", ex);
            }
        }
    }

    // ERROR_SHARING_VIOLATION (32) / ERROR_LOCK_VIOLATION (33): another handle's sharing mode forbids ours.
    private static bool IsSharingViolation(IOException ex) => (ex.HResult & 0xFFFF) is 32 or 33;

    // --- Roots ---------------------------------------------------------------

    public List<Root> GetRoots() => _roots.GetAll();

    public Root AddRoot(string path, string alias, bool recursive = true) =>
        _roots.Add(new Root
        {
            Path = Path.GetFullPath(path),
            Alias = alias,
            Recursive = recursive,
            AddedUtc = DateTime.UtcNow,
        });

    public void SetIncluded(long rootId, bool included) => _roots.SetIncluded(rootId, included);

    /// <summary>Turns subfolder indexing on/off for a root. Re-index afterwards to apply it.</summary>
    public void SetRecursive(long rootId, bool recursive) => _roots.SetRecursive(rootId, recursive);

    public void SetAlias(long rootId, string alias) => _roots.SetAlias(rootId, alias);

    /// <summary>
    /// Shows or hides a subfolder of a root — its checkbox in the folder tree. A hidden folder, and every folder
    /// inside it, is left out of <see cref="GetContents"/> (and <see cref="GetUnion"/>) the way an unticked root is;
    /// it is still indexed. <paramref name="relDir"/> is relative to the root; case and separators don't matter.
    /// A root's own folder is shown or hidden with <see cref="SetIncluded"/>, not here.
    /// </summary>
    public void SetFolderShown(long rootId, string relDir, bool shown)
    {
        var folder = (relDir ?? "").Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .Trim(Path.DirectorySeparatorChar);
        if (folder.Length == 0)
            throw new ArgumentException("That is the folder itself: show or hide it with SetIncluded.", nameof(relDir));
        if (shown)
            _hidden.Show(rootId, folder);
        else
            _hidden.Hide(rootId, folder);
    }

    /// <summary>Removes a root, its items (cascade) and its cached thumbnails (cross-DB).</summary>
    public void RemoveRoot(long rootId)
    {
        StopWatching(rootId);
        var itemIds = _roots.GetItemIds(rootId);
        _roots.Remove(rootId);
        _thumbnails.DeleteForItems(itemIds);
    }

    // --- Items / thumbnails --------------------------------------------------

    /// <summary>The items the library shows: those in included roots, outside any hidden subfolder.</summary>
    public List<LibraryRow> GetUnion() => GetContents().Rows;

    /// <summary>
    /// What the library shows — the items of included roots, newest first, less those inside a hidden subfolder —
    /// and the hidden subfolders, each with how many items it holds (counting items inside nested hidden folders
    /// for each of them).
    /// </summary>
    public LibraryContents GetContents()
    {
        // Both through the reader provider: this is read off the UI thread, and must never join a change the UI thread
        // has under way on the shared provider (a folder's removal, a tag save).
        var contents = Contents(_items.GetIncludedUnion(), HiddenFolderStore.All(_db.ReadProvider));
        ReadingContents?.Invoke(); // after the reads: a test that holds here holds rows read before whatever it does next
        return contents;
    }

    /// <summary>
    /// What a library shows, from its included folders' rows and its hidden subfolders: the rule
    /// <see cref="GetContents"/> follows, and <see cref="Storage.LibraryCount"/> for a library that isn't open.
    /// </summary>
    internal static LibraryContents Contents(List<LibraryRow> rows, List<Storage.HiddenFolderRow> hidden)
    {
        if (hidden.Count == 0)
            return new LibraryContents(rows, []);

        var counts = new int[hidden.Count];
        var shown = new List<LibraryRow>(rows.Count);
        foreach (var row in rows)
        {
            var dir = Path.GetDirectoryName(row.Item.RelPath) ?? "";
            var inHidden = false;
            for (var i = 0; i < hidden.Count; i++)
            {
                if (hidden[i].RootId != row.Item.RootId || !IsInside(dir, hidden[i].RelDir))
                    continue;
                counts[i]++;
                inHidden = true;
            }
            if (!inHidden)
                shown.Add(row);
        }
        return new LibraryContents(shown,
            [.. hidden.Select((h, i) => new HiddenFolder(h.RootId, h.RelDir, counts[i]))
                .OrderBy(h => h.RootId).ThenBy(h => h.RelDir, StringComparer.OrdinalIgnoreCase)]);
    }

    // The folder itself, or one inside it — not a sibling whose name merely starts the same ("Trip 2" isn't in "Trip").
    private static bool IsInside(string dir, string folder) =>
        dir.Equals(folder, StringComparison.OrdinalIgnoreCase)
        || (dir.Length > folder.Length && dir[folder.Length] == Path.DirectorySeparatorChar
            && dir.StartsWith(folder, StringComparison.OrdinalIgnoreCase));

    public int CountForRoot(long rootId)
    {
        var count = _items.CountForRoot(rootId);
        ReadingCount?.Invoke(rootId);
        return count;
    }

    /// <summary>
    /// <see cref="GetContents"/> on a background thread, tracked like an index run: <see cref="ShutdownAsync"/> waits for
    /// it. Throws <see cref="ObjectDisposedException"/> once the library is closing.
    /// </summary>
    public Task<LibraryContents> GetContentsAsync() => ReadInBackground(GetContents);

    /// <summary><see cref="CountForRoot"/> on a background thread, tracked as <see cref="GetContentsAsync"/> is.</summary>
    public Task<int> CountForRootAsync(long rootId) => ReadInBackground(() => CountForRoot(rootId));

    private Task<T> ReadInBackground<T>(Func<T> read)
    {
        lock (_indexLock)
        {
            ObjectDisposedException.ThrowIf(_shuttingDown, this);
            var task = Task.Run(read);
            _readTasks.RemoveAll(t => t.IsCompleted);
            _readTasks.Add(task);
            return task;
        }
    }

    /// <summary>The library's database (for tests).</summary>
    internal ApertureDatabase Database => _db;

    /// <summary>Test seam: runs inside every read of the library's contents, after its queries, on the thread reading.</summary>
    internal Action? ReadingContents { get; set; }

    /// <summary>Test seam: runs inside every read of a folder's count, after its query, on the thread reading.</summary>
    internal Action<long>? ReadingCount { get; set; }

    /// <summary>
    /// Cached thumbnail bytes for an item. Pass <paramref name="expectedSrcMtimeTicks"/>
    /// (the item's own mtime) so a stale/realigned cache entry is skipped rather than
    /// served for the wrong file.
    /// </summary>
    public byte[]? GetThumbnail(long itemId, ThumbSize size, long? expectedSrcMtimeTicks = null) =>
        _thumbnails.Get(itemId, size, expectedSrcMtimeTicks);

    /// <summary>
    /// Returns a valid thumbnail for a tile, regenerating it from the source on the spot when the cache
    /// is missing or stale (src mtime mismatch). This lets a just-shown tile paint immediately instead of
    /// waiting for the background reconcile to reach it in scan order — key to instant-feeling startup on a
    /// realigned/partial cache. Returns null if the source file is missing or can't be decoded, or if this
    /// library has been shut down (a catalog switch closed it while the request was queued).
    /// </summary>
    public byte[]? GetOrCreateThumbnail(long itemId, string path, ThumbSize size, long mtimeTicks, bool isVideo)
    {
        try
        {
            var bytes = _thumbnails.Get(itemId, size, mtimeTicks);
            if (bytes is { Length: > 0 })
                return bytes;

            if (!File.Exists(path))
                return null;
            var set = isVideo ? _thumbnailGen.GenerateVideo(path, ThumbSizes.All) : _thumbnailGen.Generate(path, ThumbSizes.All);
            if (set is null)
                return null;
            _thumbnails.Put(itemId, mtimeTicks, set);
            return set.Thumbs.TryGetValue(size, out var data) ? data.Bytes : null;
        }
        catch (ObjectDisposedException) when (_shuttingDown)
        {
            // A catalog switch closed this library while the request was queued (a tile scrolled into view,
            // or a preview started, just before the switch). The view asking is going away; there is no
            // thumbnail to give it. Tile loads and previews are async void, so a throw here would end the app.
            return null;
        }
    }

    /// <summary>
    /// Enforces the thumbnail cache size cap (auto-sized to the library) by evicting least-recently-used
    /// thumbnails. Call occasionally on a background thread — a no-op when the cache is within budget.
    /// Evicted thumbnails regenerate on next view.
    /// </summary>
    public ThumbnailStore.PruneResult PruneThumbnailCache()
    {
        var cap = ThumbnailStore.EffectiveCapBytes(Settings.Current.ThumbnailCacheCapMb, _items.TotalCount());
        var lowWater = cap - cap / 10; // 90% — leave headroom so eviction runs in batches, not per-tile
        return _thumbnails.Prune(cap, lowWater);
    }

    // --- Annotations (tags + notes) -----------------------------------------

    public Annotation GetAnnotation(string path) => _annotations.Get(path);

    public Dictionary<string, Annotation> GetAllAnnotations() => _annotations.GetAll();

    public List<string> GetAllTags() => _annotations.GetAllTags();

    /// <summary>Current tags ordered by recency of use (most recently applied first).</summary>
    public List<string> GetTagsByRecency() => _annotations.GetTagsByRecency();

    /// <summary>Current tags with item count + last-used, for search quick-picks.</summary>
    public List<Annotations.TagUsage> GetTagUsage() => _annotations.GetTagUsage();

    public void SaveAnnotation(string path, IReadOnlyList<string> tags, string note) =>
        _annotations.Save(path, tags, note);

    public Dictionary<string, int> GetTagCounts() => _annotations.GetTagCounts();

    public void RenameTag(string oldTag, string newTag) => _annotations.RenameTag(oldTag, newTag);

    public void DeleteTag(string tag) => _annotations.DeleteTag(tag);

    /// <summary>Writes all tags + notes to a portable JSON file. Returns the entry count.</summary>
    public int ExportAnnotations(string filePath) => _transfer.Export(filePath);

    /// <summary>Upserts tags + notes from a portable JSON file (root-relative remap + merge).</summary>
    public Annotations.ImportSummary ImportAnnotations(string filePath) => _transfer.Import(filePath);

    // --- Indexing ------------------------------------------------------------

    /// <summary>Runs a full reconcile of a root. Call on a background thread.</summary>
    public IndexResult IndexRoot(Root root, IProgress<IndexProgress>? progress = null, CancellationToken ct = default) =>
        _indexer.IndexRoot(root, progress, ct, Settings.Current.IncludeVideos);

    /// <summary>
    /// Runs a full reconcile of a root as a tracked background task. Prefer this over <see cref="IndexRoot"/>
    /// when the caller is the live app: the task is linked to the service's shutdown token and awaited by
    /// <see cref="ShutdownAsync"/>, so a catalog switch (or app exit) cancels it and waits for it to settle
    /// before the database is disposed — no index write can race a teardown. The external
    /// <paramref name="ct"/> still drives per-root pause/cancel; the two tokens are linked.
    /// </summary>
    public Task<IndexResult> IndexRootAsync(Root root, IProgress<IndexProgress>? progress = null, CancellationToken ct = default)
    {
        lock (_indexLock)
        {
            ObjectDisposedException.ThrowIf(_shuttingDown, this);
            IndexStarting?.Invoke(root);

            var linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token, ct);
            progress = WrapIndexProgress?.Invoke(root, progress) ?? progress;
            // The token is observed inside IndexRoot (per-file + per-batch); it is deliberately NOT passed
            // to Task.Run, so the delegate always runs its finally and disposes the linked source — a
            // pre-cancelled Task.Run would skip the body and leak it.
            var task = Task.Run(() =>
            {
                try { return _indexer.IndexRoot(root, progress, linked.Token, Settings.Current.IncludeVideos); }
                finally { linked.Dispose(); }
            });
            _indexTasks.RemoveAll(t => t.IsCompleted); // prune settled tasks so the list can't grow unbounded
            _indexTasks.Add(task);
            return task;
        }
    }

    // --- Watching ------------------------------------------------------------

    public void Watch(Root root)
    {
        if (!Directory.Exists(root.Path))
            return;

        lock (_watchersLock)
        {
            // After teardown a late caller (an index that finished just as the switch began) must not arm
            // a live watcher on a closed library: it would keep raising RootChanged forever.
            if (_watchersClosed || _watchers.ContainsKey(root.Id))
                return;

            var watcher = new RootWatcher(root.Path, includeSubdirectories: root.Recursive);
            watcher.Changed += (_, _) => RootChanged?.Invoke(this, root.Id);
            watcher.Start();
            _watchers[root.Id] = watcher;
        }
    }

    public void StopWatching(long rootId)
    {
        lock (_watchersLock)
        {
            if (_watchers.Remove(rootId, out var watcher))
                watcher.Dispose();
        }
    }

    // --- Teardown ------------------------------------------------------------

    /// <summary>
    /// Cancels every in-flight index, waits for them to settle, stops the watchers, and disposes the
    /// database. Await this before switching catalogs (and on a clean exit): the indexer commits in
    /// batches and rolls back only its in-flight batch on cancel, so the database is always left
    /// consistent and its files are released for the next catalog. Idempotent.
    /// </summary>
    public async Task ShutdownAsync()
    {
        Task[] pending;
        lock (_indexLock)
        {
            _shuttingDown = true;
            pending = [.. _indexTasks.Concat(_readTasks).Where(t => !t.IsCompleted)];
        }

        TryCancelShutdown();
        try
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        catch
        {
            // A cancelled index surfaces OperationCanceledException (and a failed one its own error);
            // committed batches persist and the in-flight batch rolled back, so the DB stays consistent.
        }

        TearDownCore();
    }

    /// <summary>
    /// Synchronous teardown for process exit: cancels in-flight indexes (without awaiting them — the
    /// process is ending), stops the watchers and disposes the database. Prefer <see cref="ShutdownAsync"/>
    /// for a catalog switch, where the in-flight index must be awaited before the next catalog opens.
    /// Idempotent and safe to call after <see cref="ShutdownAsync"/>.
    /// </summary>
    public void Dispose()
    {
        lock (_indexLock)
            _shuttingDown = true;

        TryCancelShutdown();
        TearDownCore();
    }

    private void TryCancelShutdown()
    {
        try
        {
            if (!_shutdownCts.IsCancellationRequested)
                _shutdownCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already torn down by a prior Shutdown/Dispose.
        }
    }

    private void TearDownCore()
    {
        lock (_indexLock)
        {
            if (_tornDown)
                return;
            _tornDown = true;
        }

        TearingDown?.Invoke();
        StopAllWatchers();
        _db.Dispose();
        _openLock.Dispose();
        _shutdownCts.Dispose();
    }

    private void StopAllWatchers()
    {
        lock (_watchersLock)
        {
            _watchersClosed = true;
            foreach (var watcher in _watchers.Values)
                watcher.Dispose();
            _watchers.Clear();
        }
    }

    /// <summary>Test seam: wraps the progress a run of <see cref="IndexRootAsync"/> reports to (to hold the run).</summary>
    internal Func<Root, IProgress<IndexProgress>?, IProgress<IndexProgress>?>? WrapIndexProgress { get; set; }

    /// <summary>Test seam: runs as <see cref="IndexRootAsync"/> starts an index of a root, on the caller's thread.</summary>
    internal Action<Root>? IndexStarting { get; set; }

    /// <summary>Test seam: runs on the thread that closes this library (its database, watchers and lock), as it starts.</summary>
    internal Action? TearingDown { get; set; }

    /// <summary>How many roots are being watched. For tests of the teardown contract.</summary>
    internal int WatcherCount
    {
        get { lock (_watchersLock) return _watchers.Count; }
    }
}
