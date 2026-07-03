using Reel.Core.Indexing;
using Reel.Core.Media;
using Reel.Core.Models;
using Reel.Core.Storage;
using Reel.Core.Watching;

namespace Reel.Core.Library;

/// <summary>
/// The single entry point the UI talks to. Wraps the stores, the indexer and the
/// per-root watchers so a view model depends on one object, not six. All methods
/// are synchronous and cheap except <see cref="IndexRoot"/>, which the caller is
/// expected to run on a background thread.
/// </summary>
public sealed class LibraryService : IDisposable
{
    private readonly ReelDatabase _db;
    private readonly RootStore _roots;
    private readonly ItemStore _items;
    private readonly ThumbnailStore _thumbnails;
    private readonly Indexer _indexer;
    private readonly Dictionary<long, RootWatcher> _watchers = [];
    private readonly Lock _watchersLock = new();

    /// <summary>Raised (on a background thread) when a watched root's files change on disk. Argument is the root id.</summary>
    public event EventHandler<long>? RootChanged;

    public LibraryService() : this(ReelPaths.DefaultDataDir) { }

    public LibraryService(string dataDir)
    {
        _db = new ReelDatabase(dataDir);
        _db.Initialize();
        _roots = new RootStore(_db);
        _items = new ItemStore(_db);
        _thumbnails = new ThumbnailStore(_db);
        _indexer = new Indexer(_db, new ThumbnailGenerator(), new MetadataReader());
    }

    // --- Roots ---------------------------------------------------------------

    public List<Root> GetRoots() => _roots.GetAll();

    public Root AddRoot(string path, string alias) =>
        _roots.Add(new Root { Path = Path.GetFullPath(path), Alias = alias, AddedUtc = DateTime.UtcNow });

    public void SetIncluded(long rootId, bool included) => _roots.SetIncluded(rootId, included);

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

    public byte[]? GetThumbnail(long itemId, ThumbSize size) => _thumbnails.Get(itemId, size);

    // --- Indexing ------------------------------------------------------------

    /// <summary>Runs a full reconcile of a root. Call on a background thread.</summary>
    public IndexResult IndexRoot(Root root, IProgress<IndexProgress>? progress = null, CancellationToken ct = default) =>
        _indexer.IndexRoot(root, progress, ct);

    // --- Watching ------------------------------------------------------------

    public void Watch(Root root)
    {
        if (!Directory.Exists(root.Path))
            return;

        lock (_watchersLock)
        {
            if (_watchers.ContainsKey(root.Id))
                return;

            var watcher = new RootWatcher(root.Path);
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

    public void Dispose()
    {
        lock (_watchersLock)
        {
            foreach (var watcher in _watchers.Values)
                watcher.Dispose();
            _watchers.Clear();
        }
    }
}
