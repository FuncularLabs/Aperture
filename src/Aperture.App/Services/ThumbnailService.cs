using System.IO;
using System.Windows.Media.Imaging;
using Aperture.Core.Library;
using Aperture.Core.Models;

namespace Aperture.App.Services;

/// <summary>
/// Turns cached thumbnail bytes into frozen <see cref="BitmapSource"/>s off the UI
/// thread, with a bounded LRU of decoded images so scrolling a large library does
/// not grow memory without limit. Only visible tiles ever request a decode.
/// </summary>
public sealed class ThumbnailService(LibraryService library)
{
    // Cap the decoded (in-memory) set. Visible tiles hold their own references;
    // this bounds the retained-but-offscreen decodes.
    private const int MaxDecoded = 512;

    // Decode JPEGs down to this longest edge — plenty for tiles up to XL, keeps
    // per-bitmap memory modest.
    private const int DecodePixelWidth = 384;

    private readonly LibraryService _library = library;

    // Keyed by (itemId, source mtime) so that if a file's mtime changes (or a
    // realigned cache is healed), the stale decoded bitmap is not reused.
    private readonly LruCache<(long ItemId, long SrcMtime), BitmapSource> _cache = new(MaxDecoded);

    public async Task<BitmapSource?> LoadAsync(long itemId, string path, bool isVideo, ThumbSize size, long srcMtimeTicks)
    {
        var key = (itemId, srcMtimeTicks);
        if (_cache.TryGet(key, out var cached))
            return cached;

        var bitmap = await Task.Run(() => Decode(itemId, path, isVideo, size, srcMtimeTicks)).ConfigureAwait(true);
        if (bitmap is not null)
            _cache.Set(key, bitmap);
        return bitmap;
    }

    private BitmapSource? Decode(long itemId, string path, bool isVideo, ThumbSize size, long srcMtimeTicks)
    {
        // Regenerates on the spot if the cached thumb is missing/stale, so a just-shown tile isn't blank.
        var bytes = _library.GetOrCreateThumbnail(itemId, path, size, srcMtimeTicks, isVideo);
        if (bytes is null || bytes.Length == 0)
            return null;

        var bitmap = new BitmapImage();
        using var stream = new MemoryStream(bytes);
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = DecodePixelWidth;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze(); // cross-thread usable + cheaper to render
        return bitmap;
    }

    /// <summary>Simple thread-safe LRU keyed by item id.</summary>
    private sealed class LruCache<TKey, TValue>(int capacity) where TKey : notnull
    {
        private readonly int _capacity = capacity;
        private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _map = [];
        private readonly LinkedList<(TKey Key, TValue Value)> _order = new();
        private readonly Lock _gate = new();

        public bool TryGet(TKey key, out TValue value)
        {
            lock (_gate)
            {
                if (_map.TryGetValue(key, out var node))
                {
                    _order.Remove(node);
                    _order.AddFirst(node);
                    value = node.Value.Value;
                    return true;
                }
            }
            value = default!;
            return false;
        }

        public void Set(TKey key, TValue value)
        {
            lock (_gate)
            {
                if (_map.TryGetValue(key, out var existing))
                {
                    _order.Remove(existing);
                    _map.Remove(key);
                }

                var node = new LinkedListNode<(TKey, TValue)>((key, value));
                _order.AddFirst(node);
                _map[key] = node;

                while (_map.Count > _capacity && _order.Last is { } lru)
                {
                    _order.RemoveLast();
                    _map.Remove(lru.Value.Key);
                }
            }
        }
    }
}
