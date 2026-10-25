using Aperture.Core.Indexing;
using Aperture.Core.Media;
using Aperture.Core.Models;
using Aperture.Core.Settings;
using Aperture.Core.Storage;
using Aperture.Core.Watching;

namespace Aperture.Core.Library;

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
    private readonly Annotations.AnnotationTransfer _transfer;
    private readonly Indexer _indexer;
    private readonly ThumbnailGenerator _thumbnailGen = new();
    private readonly Dictionary<long, RootWatcher> _watchers = [];
    private readonly Lock _watchersLock = new();

    // Catalog-switch teardown: in-flight indexes are tracked so a shutdown can cancel them and wait for
    // them to settle before the database is disposed, rather than racing a half-written transaction.
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly List<Task> _indexTasks = [];
    private readonly Lock _indexLock = new();
    private volatile bool _shuttingDown; // read lock-free by GetOrCreateThumbnail; written under _indexLock
    private bool _tornDown;
    private bool _watchersClosed; // guarded by _watchersLock: no watcher may start once teardown stopped them

    /// <summary>Raised (on a background thread) when a watched root's files change on disk. Argument is the root id.</summary>
    public event EventHandler<long>? RootChanged;

    /// <summary>User settings (video inclusion, caption format, sort, etc.).</summary>
    public SettingsService Settings { get; }

    public LibraryService() : this(AperturePaths.DefaultDataDir) { }

    public LibraryService(string dataDir)
    {
        _db = new ApertureDatabase(dataDir);
        try
        {
            _db.Initialize();
            _roots = new RootStore(_db);
            _items = new ItemStore(_db);
            _thumbnails = new ThumbnailStore(_db);
            _annotations = new AnnotationStore(_db);
            _annotations.EnsureHyphenated(); // migrate legacy "multi word" tags to "multi-word"
            _transfer = new Annotations.AnnotationTransfer(_annotations, _roots);
            _indexer = new Indexer(_db, _thumbnailGen, new MetadataReader());
            Settings = new SettingsService(dataDir);
        }
        catch
        {
            // A library that can't be opened (not a database, unwritable, locked) must not keep its
            // files open: the user may want to fix, move or delete them while Aperture keeps running.
            _db.Dispose();
            throw;
        }
    }

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

    /// <summary>Removes a root, its items (cascade) and its cached thumbnails (cross-DB).</summary>
    public void RemoveRoot(long rootId)
    {
        StopWatching(rootId);
        var itemIds = _roots.GetItemIds(rootId);
        _roots.Remove(rootId);
        _thumbnails.DeleteForItems(itemIds);
    }

    // --- Items / thumbnails --------------------------------------------------

    public List<LibraryRow> GetUnion() => _items.GetIncludedUnion();

    public int CountForRoot(long rootId) => _items.CountForRoot(rootId);

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
    /// realigned/partial cache. Returns null only if the source can't be decoded.
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

            var linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token, ct);
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
            pending = _indexTasks.Where(t => !t.IsCompleted).ToArray();
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

        StopAllWatchers();
        _db.Dispose();
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

    /// <summary>How many roots are being watched. For tests of the teardown contract.</summary>
    internal int WatcherCount
    {
        get { lock (_watchersLock) return _watchers.Count; }
    }
}
