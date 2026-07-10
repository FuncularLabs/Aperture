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

    /// <summary>Raised (on a background thread) when a watched root's files change on disk. Argument is the root id.</summary>
    public event EventHandler<long>? RootChanged;

    /// <summary>User settings (video inclusion, caption format, sort, etc.).</summary>
    public SettingsService Settings { get; }

    public LibraryService() : this(AperturePaths.DefaultDataDir) { }

    public LibraryService(string dataDir)
    {
        _db = new ApertureDatabase(dataDir);
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
