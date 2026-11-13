using Microsoft.Data.Sqlite;
using Aperture.Core.Media;
using Aperture.Core.Models;
using Aperture.Core.Storage;

namespace Aperture.Core.Indexing;

/// <summary>
/// Scans a root and reconciles it into the database: inserts new files, updates
/// changed ones, prunes deleted ones, and (re)generates thumbnails. A file is
/// considered unchanged when its size and last-write time both match the stored
/// row, so a resume over an already-indexed tree does no decoding and stays fast.
/// </summary>
public sealed class Indexer(ApertureDatabase database, ThumbnailGenerator thumbnails, MetadataReader metadata)
{
    private const int BatchSize = 200;

    /// <summary>
    /// How often, at most, the run reports how far it is between the batches it saves (a quarter of a second): a folder
    /// of cloud files can take minutes over its first batch. Settable for tests only.
    /// </summary>
    internal TimeSpan ProgressInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Test seam: hears each file's relative path as the run is about to read it (decode or download it).</summary>
    internal Action<string>? Reading { get; set; }

    /// <summary>
    /// A question answered after longer than this counts the files again before reading them: the folder may have
    /// changed meanwhile, unwatched (round 1, lens 2, finding 10). Settable for tests.
    /// </summary>
    internal TimeSpan RescanAfter { get; set; } = TimeSpan.FromSeconds(2);

    private readonly ApertureDatabase _db = database;
    private readonly ThumbnailGenerator _thumbnails = thumbnails;
    private readonly MetadataReader _metadata = metadata;

    /// <param name="askOnlineOnly">
    /// Asked once, after the count and before any file is read, when the run would read online-only files (0.9.2 C1):
    /// skip them, read them after the rest, or read them now. <c>null</c> reads them now, as before. It's called on the
    /// run's thread with the run's token, and may throw <see cref="OperationCanceledException"/> to end the run.
    /// </param>
    public IndexResult IndexRoot(
        Root root, IProgress<IndexProgress>? progress = null, CancellationToken ct = default, bool includeVideos = true,
        Func<OnlineOnlyQuestion, CancellationToken, OnlineOnlyChoice>? askOnlineOnly = null)
    {
        var scan = Prepare(root, progress, ct, includeVideos);
        if (scan.OnlineOnly == 0 || askOnlineOnly is null)
            return Execute(scan, OnlineOnlyChoice.Now, progress, ct);
        var asked = System.Diagnostics.Stopwatch.StartNew();
        var choice = askOnlineOnly(new OnlineOnlyQuestion(root, scan.OnlineOnly), ct);
        if (asked.Elapsed >= RescanAfter)
            scan = Prepare(root, progress, ct, includeVideos);
        return Execute(scan, choice, progress, ct);
    }

    /// <summary>
    /// <see cref="IndexRoot"/> with a question answered later (the window's dialog): the run counts, then awaits the
    /// answer holding no thread (every folder can ask at once; review gate 3), then reads on a pool thread — never on
    /// the thread that answered.
    /// </summary>
    public async Task<IndexResult> IndexRootAsync(
        Root root, IProgress<IndexProgress>? progress, CancellationToken ct, bool includeVideos,
        Func<OnlineOnlyQuestion, CancellationToken, Task<OnlineOnlyChoice>> askOnlineOnly)
    {
        var scan = Prepare(root, progress, ct, includeVideos);
        if (scan.OnlineOnly == 0)
            return Execute(scan, OnlineOnlyChoice.Now, progress, ct);
        // WaitAsync: the token gives the question up even if the asker never hears it (a switch, exit: gate 3, C7).
        var asked = System.Diagnostics.Stopwatch.StartNew();
        var choice = await askOnlineOnly(new OnlineOnlyQuestion(root, scan.OnlineOnly), ct).WaitAsync(ct).ConfigureAwait(false);
        var waited = asked.Elapsed >= RescanAfter;
        return await Task.Run(() => Execute(waited ? Prepare(root, progress, ct, includeVideos) : scan, choice, progress, ct),
            CancellationToken.None).ConfigureAwait(false);
    }

    // What a run knows once it has counted: the files, the rows and thumbnails it has, so which it will read.
    private sealed class Scan(
        Root root, List<ScannedFile> files, Dictionary<string, ItemStore.ExistingItem> existing,
        Dictionary<long, ThumbnailStore.ThumbCacheInfo> thumbInfo, long nowTicks)
    {
        private static readonly int ExpectedThumbs = ThumbSizes.All.Length;

        public Root Root { get; } = root;
        public List<ScannedFile> Files { get; } = files;
        public Dictionary<string, ItemStore.ExistingItem> Existing { get; } = existing;
        public long NowTicks { get; } = nowTicks;

        // The online-only files among those it will read: each is downloaded to be read (the slow part on a cloud drive).
        public int OnlineOnly { get; } = files.Count(f => Downloads(f, existing, thumbInfo));

        // A file is unchanged when its size and time match its row; its thumbnails are fresh when all of them are cached
        // from this version of it. The run reads every file but an unchanged one with fresh thumbnails.
        public bool IsUnchanged(ScannedFile file, out ItemStore.ExistingItem row) => IsUnchanged(file, Existing, out row);

        public bool ThumbsFresh(long itemId, ScannedFile file) => ThumbsFresh(itemId, file, thumbInfo);

        public bool Downloads(ScannedFile file) => Downloads(file, Existing, thumbInfo);

        private static bool IsUnchanged(
            ScannedFile file, Dictionary<string, ItemStore.ExistingItem> existing, out ItemStore.ExistingItem row) =>
            existing.TryGetValue(file.RelPath, out row) && row.MTimeTicks == file.MTimeTicks && row.SizeBytes == file.SizeBytes;

        private static bool ThumbsFresh(long itemId, ScannedFile file, Dictionary<long, ThumbnailStore.ThumbCacheInfo> info) =>
            info.TryGetValue(itemId, out var ti) && ti.Count >= ExpectedThumbs && ti.SrcMtimeTicks == file.MTimeTicks;

        private static bool Downloads(
            ScannedFile f, Dictionary<string, ItemStore.ExistingItem> existing,
            Dictionary<long, ThumbnailStore.ThumbCacheInfo> info) =>
            f.IsOnlineOnly && !(IsUnchanged(f, existing, out var r) && ThumbsFresh(r.Id, f, info));
    }

    private Scan Prepare(Root root, IProgress<IndexProgress>? progress, CancellationToken ct, bool includeVideos)
    {
        var nowTicks = DateTime.UtcNow.Ticks;

        progress?.Report(new IndexProgress { RootAlias = root.Alias, Phase = IndexPhase.Scanning });
        // Count first, and say how the count climbs: a big tree (or one on a slow or cloud drive) takes a while to walk.
        var files = FileScanner.Scan(root.Path, includeVideos, ct, root.Recursive, t =>
            progress?.Report(new IndexProgress { RootAlias = root.Alias, Phase = IndexPhase.Scanning, Processed = t.Found }));

        return new Scan(root, files, new ItemStore(_db).GetExisting(root.Id), new ThumbnailStore(_db).GetInfo(), nowTicks);
    }

    private IndexResult Execute(Scan scan, OnlineOnlyChoice choice, IProgress<IndexProgress>? progress, CancellationToken ct)
    {
        var (root, files, existing, nowTicks, onlineOnly) = (scan.Root, scan.Files, scan.Existing, scan.NowTicks, scan.OnlineOnly);
        bool IsUnchanged(ScannedFile file, out ItemStore.ExistingItem row) => scan.IsUnchanged(file, out row);
        bool ThumbsFresh(long itemId, ScannedFile file) => scan.ThumbsFresh(itemId, file);
        bool Downloads(ScannedFile file) => scan.Downloads(file);

        // Asked before anything is read (0.9.2 C1): skip them (kept as they are, never pruned for it), read them after
        // the rest, or read them in order as before.
        ct.ThrowIfCancellationRequested();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var skippedOnlineOnly = 0;
        var work = files;
        if (choice == OnlineOnlyChoice.Skip)
        {
            work = [];
            foreach (var file in files)
            {
                if (Downloads(file))
                {
                    seen.Add(file.RelPath); // its row, if it has one, stays as it was
                    skippedOnlineOnly++;
                }
                else
                    work.Add(file);
            }
            onlineOnly = 0;
        }
        else if (choice == OnlineOnlyChoice.Background)
            work = [.. files.Where(f => !Downloads(f)), .. files.Where(Downloads)];
        var onlineOnlyLast = choice == OnlineOnlyChoice.Background && onlineOnly > 0;

        // How far it is, from the start: the count, and how many it will download (review R1-1).
        IndexProgress Indexing(int processed, bool saved, string? current) => new()
        {
            RootAlias = root.Alias, Phase = IndexPhase.Indexing, Processed = processed, Total = work.Count,
            OnlineOnly = onlineOnly, OnlineOnlyLast = onlineOnlyLast, CurrentFile = current, Saved = saved,
        };
        progress?.Report(Indexing(0, saved: false, current: null));
        var sinceReport = System.Diagnostics.Stopwatch.StartNew();

        int added = 0, updated = 0, skipped = 0, thumbsGenerated = 0, failed = 0;

        using var meta = _db.OpenMetadata();
        using var thumbs = _db.OpenThumbnails();
        using var upsertItem = CreateUpsertItemCommand(meta);
        using var upsertThumb = CreateUpsertThumbCommand(thumbs);

        var metaTx = meta.BeginTransaction();
        var thumbTx = thumbs.BeginTransaction();
        upsertItem.Transaction = metaTx;
        upsertThumb.Transaction = thumbTx;

        try
        {
            var processed = 0;
            foreach (var file in work)
            {
                ct.ThrowIfCancellationRequested();
                seen.Add(file.RelPath);
                processed++;

                if (IsUnchanged(file, out var row))
                {
                    // Unchanged file. Regenerate thumbnails only if the cache is incomplete
                    // OR its source mtime no longer matches this file — the latter catches a
                    // realigned cache (metadata rebuilt, ids now map to different files) that
                    // would otherwise serve the wrong image for this item.
                    if (!ThumbsFresh(row.Id, file))
                    {
                        Reading?.Invoke(file.RelPath);
                        var set = GenerateThumbnails(file);
                        if (set is null)
                            failed++;
                        else
                            thumbsGenerated += StoreThumbs(upsertThumb, row.Id, file.MTimeTicks, set, nowTicks);
                    }
                    skipped++;
                }
                else
                {
                    // Videos carry no EXIF we rely on; use file times.
                    Reading?.Invoke(file.RelPath);
                    var md = file.Kind == MediaKind.Video ? default : _metadata.Read(file.FullPath);
                    var set = GenerateThumbnails(file);
                    if (set is null)
                        failed++;

                    var itemId = UpsertItem(upsertItem, root.Id, file, md, set, nowTicks);
                    if (set is not null)
                        thumbsGenerated += StoreThumbs(upsertThumb, itemId, file.MTimeTicks, set, nowTicks);

                    if (existing.ContainsKey(file.RelPath))
                        updated++;
                    else
                        added++;
                }

                if (processed % BatchSize == 0)
                {
                    metaTx.Commit();
                    thumbTx.Commit();
                    metaTx.Dispose();
                    thumbTx.Dispose();
                    metaTx = meta.BeginTransaction();
                    thumbTx = thumbs.BeginTransaction();
                    upsertItem.Transaction = metaTx;
                    upsertThumb.Transaction = thumbTx;

                    progress?.Report(Indexing(processed, saved: true, file.RelPath)); // the window reads the batch now
                    sinceReport.Restart();
                }
                else if (sinceReport.Elapsed >= ProgressInterval)
                {
                    progress?.Report(Indexing(processed, saved: false, file.RelPath)); // a batch can take a while
                    sinceReport.Restart();
                }
            }

            metaTx.Commit();
            thumbTx.Commit();
        }
        catch
        {
            SafeRollback(metaTx);
            SafeRollback(thumbTx);
            throw;
        }
        finally
        {
            metaTx.Dispose();
            thumbTx.Dispose();
        }

        // Prune rows whose files disappeared from disk.
        progress?.Report(new IndexProgress
        {
            RootAlias = root.Alias, Phase = IndexPhase.Pruning, OnlineOnly = onlineOnly, OnlineOnlyLast = onlineOnlyLast,
        });
        var removedIds = existing
            .Where(kv => !seen.Contains(kv.Key))
            .Select(kv => kv.Value.Id)
            .ToList();
        if (removedIds.Count > 0)
        {
            DeleteItems(meta, removedIds);
            new ThumbnailStore(_db).DeleteForItems(removedIds);
        }

        progress?.Report(new IndexProgress
        {
            RootAlias = root.Alias,
            Phase = IndexPhase.Done,
            Processed = work.Count,
            Total = work.Count,
            OnlineOnly = onlineOnly,
            OnlineOnlyLast = onlineOnlyLast,
        });

        return new IndexResult
        {
            Added = added,
            Updated = updated,
            Skipped = skipped,
            Removed = removedIds.Count,
            ThumbnailsGenerated = thumbsGenerated,
            Failed = failed,
            SkippedOnlineOnly = skippedOnlineOnly,
        };
    }

    private ThumbnailSet? GenerateThumbnails(ScannedFile file) =>
        file.Kind == MediaKind.Video
            ? _thumbnails.GenerateVideo(file.FullPath, ThumbSizes.All)
            : _thumbnails.Generate(file.FullPath, ThumbSizes.All);

    private static long UpsertItem(
        SqliteCommand cmd, long rootId, ScannedFile file, ImageMetadata md, ThumbnailSet? set, long nowTicks)
    {
        cmd.Parameters["@root"].Value = rootId;
        cmd.Parameters["@rel"].Value = file.RelPath;
        cmd.Parameters["@name"].Value = Path.GetFileName(file.RelPath);
        cmd.Parameters["@ext"].Value = file.Ext;
        cmd.Parameters["@size"].Value = file.SizeBytes;
        cmd.Parameters["@mtime"].Value = file.MTimeTicks;
        cmd.Parameters["@taken"].Value = (object?)md.TakenLocal?.Ticks ?? DBNull.Value;
        cmd.Parameters["@w"].Value = (object?)set?.SourceWidth ?? DBNull.Value;
        cmd.Parameters["@h"].Value = (object?)set?.SourceHeight ?? DBNull.Value;
        cmd.Parameters["@cam"].Value = (object?)md.Camera ?? DBNull.Value;
        cmd.Parameters["@orient"].Value = (object?)(md.Orientation ?? set?.Orientation) ?? DBNull.Value;
        cmd.Parameters["@indexed"].Value = nowTicks;
        cmd.Parameters["@kind"].Value = (int)file.Kind;
        return (long)cmd.ExecuteScalar()!;
    }

    private static int StoreThumbs(
        SqliteCommand cmd, long itemId, long srcMtimeTicks, ThumbnailSet set, long nowTicks)
    {
        var stored = 0;
        foreach (var (size, data) in set.Thumbs)
        {
            cmd.Parameters["@item"].Value = itemId;
            cmd.Parameters["@size"].Value = (int)size;
            cmd.Parameters["@src"].Value = srcMtimeTicks;
            cmd.Parameters["@w"].Value = data.Width;
            cmd.Parameters["@h"].Value = data.Height;
            cmd.Parameters["@data"].Value = data.Bytes;
            cmd.Parameters["@bytes"].Value = data.Bytes.Length;
            cmd.Parameters["@created"].Value = nowTicks;
            cmd.Parameters["@used"].Value = nowTicks;
            cmd.ExecuteNonQuery();
            stored++;
        }
        return stored;
    }

    private static void DeleteItems(SqliteConnection meta, IReadOnlyList<long> ids)
    {
        using var tx = meta.BeginTransaction();
        using var cmd = meta.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM items WHERE id = @id;";
        var p = cmd.Parameters.Add("@id", SqliteType.Integer);
        foreach (var id in ids)
        {
            p.Value = id;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private static SqliteCommand CreateUpsertItemCommand(SqliteConnection conn)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO items (root_id, rel_path, file_name, ext, size_bytes, mtime_ticks,
                               taken_ticks, width, height, camera, orientation, indexed_ticks, kind)
            VALUES (@root, @rel, @name, @ext, @size, @mtime, @taken, @w, @h, @cam, @orient, @indexed, @kind)
            ON CONFLICT(root_id, rel_path) DO UPDATE SET
                file_name = excluded.file_name,
                ext = excluded.ext,
                size_bytes = excluded.size_bytes,
                mtime_ticks = excluded.mtime_ticks,
                taken_ticks = excluded.taken_ticks,
                width = excluded.width,
                height = excluded.height,
                camera = excluded.camera,
                orientation = excluded.orientation,
                indexed_ticks = excluded.indexed_ticks,
                kind = excluded.kind
            RETURNING id;
            """;
        foreach (var name in new[] { "@root", "@size", "@mtime", "@taken", "@w", "@h", "@orient", "@indexed", "@kind" })
            cmd.Parameters.Add(name, SqliteType.Integer);
        foreach (var name in new[] { "@rel", "@name", "@ext", "@cam" })
            cmd.Parameters.Add(name, SqliteType.Text);
        return cmd;
    }

    private static SqliteCommand CreateUpsertThumbCommand(SqliteConnection conn)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO thumbs (item_id, size, src_mtime_ticks, width, height, data, bytes, created_ticks, last_used_ticks)
            VALUES (@item, @size, @src, @w, @h, @data, @bytes, @created, @used)
            ON CONFLICT(item_id, size) DO UPDATE SET
                src_mtime_ticks = excluded.src_mtime_ticks,
                width = excluded.width,
                height = excluded.height,
                data = excluded.data,
                bytes = excluded.bytes,
                created_ticks = excluded.created_ticks,
                last_used_ticks = excluded.last_used_ticks;
            """;
        foreach (var name in new[] { "@item", "@size", "@src", "@w", "@h", "@bytes", "@created", "@used" })
            cmd.Parameters.Add(name, SqliteType.Integer);
        cmd.Parameters.Add("@data", SqliteType.Blob);
        return cmd;
    }

    private static void SafeRollback(SqliteTransaction tx)
    {
        try { tx.Rollback(); }
        catch { /* connection may already be broken; nothing to salvage */ }
    }
}
