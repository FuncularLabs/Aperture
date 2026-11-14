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
    internal TimeSpan RescanAfter { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Test seam: runs between the count and the reading of the folder's rows (a move can land there).</summary>
    internal Action? AfterScan { get; set; }

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
        public bool IncludeVideos { get; init; } = true;
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
        AfterScan?.Invoke();

        return new Scan(root, files, new ItemStore(_db).GetExisting(root.Id), new ThumbnailStore(_db).GetInfo(), nowTicks)
        {
            IncludeVideos = includeVideos,
        };
    }

    private IndexResult Execute(Scan scan, OnlineOnlyChoice choice, IProgress<IndexProgress>? progress, CancellationToken ct)
    {
        var (root, files, existing, nowTicks, onlineOnly) = (scan.Root, scan.Files, scan.Existing, scan.NowTicks, scan.OnlineOnly);
        bool IsUnchanged(ScannedFile file, out ItemStore.ExistingItem row) => scan.IsUnchanged(file, out row);
        // Not there to read now: never read (a video's read doesn't throw for a missing file, and would rewrite its row
        // with no size: round 4, F4), and left out of what's seen. The prune decides whether it's gone or out of reach.
        bool Missing(string path) => !File.Exists(path);
        bool ThumbsFresh(long itemId, ScannedFile file) => scan.ThumbsFresh(itemId, file);
        bool Downloads(ScannedFile file) => scan.Downloads(file);

        // Asked before anything is read (0.9.2 C1): skip them (kept as they are, never pruned for it), read them after
        // the rest, or read them in order as before.
        ct.ThrowIfCancellationRequested();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // The files whose rows this run wrote or kept as they are, in any case: a row under another case of one of these
        // is the old name's, and goes. Not the files skipped, nor those whose read threw (in use, say): their old rows
        // stay (round 4, F3). A file whose thumbnails couldn't be made counts when its row is written (not when it was
        // gone as it was read).
        var indexed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                var unchanged = IsUnchanged(file, out var row);
                try
                {
                    if ((!unchanged || !ThumbsFresh(row.Id, file)) && Missing(file.FullPath))
                    {
                        // Not there since it was counted (moved, deleted, its drive gone): not read (round 2, R2-4).
                        seen.Remove(file.RelPath);
                    }
                    else if (unchanged)
                    {
                        // Unchanged file. Regenerate thumbnails only if the cache is incomplete
                        // OR its source mtime no longer matches this file — the latter catches a
                        // realigned cache (metadata rebuilt, ids now map to different files) that
                        // would otherwise serve the wrong image for this item.
                        var goneAsRead = false;
                        if (!ThumbsFresh(row.Id, file))
                        {
                            Reading?.Invoke(file.RelPath);
                            var set = GenerateThumbnails(file);
                            if (set is null && Missing(file.FullPath))
                                goneAsRead = true; // as below: left to the prune (round 7, F-5)
                            else if (set is null)
                                failed++;
                            else
                                thumbsGenerated += StoreThumbs(upsertThumb, row.Id, file.MTimeTicks, set, nowTicks);
                        }
                        if (goneAsRead)
                            seen.Remove(file.RelPath);
                        else
                        {
                            skipped++;
                            indexed.Add(file.RelPath);
                        }
                    }
                    else
                    {
                        // Videos carry no EXIF we rely on; use file times.
                        Reading?.Invoke(file.RelPath);
                        var md = file.Kind == MediaKind.Video ? default : _metadata.Read(file.FullPath);
                        var set = GenerateThumbnails(file);
                        if (set is null && Missing(file.FullPath))
                        {
                            // Gone as it was read (its drive unplugged, say): a video's read doesn't throw for that, and
                            // its row would be written with no size. Not written; left to the prune (round 5, L-2).
                            seen.Remove(file.RelPath);
                        }
                        else
                        {
                            if (set is null)
                                failed++;

                            var itemId = UpsertItem(upsertItem, root.Id, file, md, set, nowTicks);
                            indexed.Add(file.RelPath);
                            if (set is not null)
                                thumbsGenerated += StoreThumbs(upsertThumb, itemId, file.MTimeTicks, set, nowTicks);

                            if (existing.ContainsKey(file.RelPath))
                                updated++;
                            else
                                added++;
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A file that can't be read just now (in use, gone mid-read) no longer ends the run (OOR-1): one not
                    // there is left to the prune; one still there keeps its row as it was, for the next run to read.
                    if (Missing(file.FullPath))
                        seen.Remove(file.RelPath);
                    else
                        failed++;
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
        // A row not seen goes if it's out of the run's scope (a folder without subfolders, videos left out). In scope, a
        // row whose file is there after all stays (moved in by Move to… after the count passed its folder: round 2,
        // R2-4), unless this run indexed that file under another case (a rename of only the case: the old name's row,
        // round 3, M-2). A row whose file is missing goes only if its absence can be told (round 4, F1, F2).
        bool InScope(string relPath) =>
            (root.Recursive || !relPath.Contains(Path.DirectorySeparatorChar))
            && SupportedFormats.IsSupported(Path.GetExtension(relPath).ToLowerInvariant(), scan.IncludeVideos);
        var removed = new List<(long Id, string RelPath)>();
        var absence = new Absence();
        foreach (var (relPath, item) in existing)
        {
            ct.ThrowIfCancellationRequested(); // a big prune on a slow share stops when asked (round 3, N-3)
            if (seen.Contains(relPath))
                continue;
            var goes = !InScope(relPath) || absence.At(Path.Combine(root.Path, relPath)) switch
            {
                Absence.Look.There => indexed.Contains(relPath), // indexed under another case: the old name's row
                Absence.Look.Gone => true,
                _ => false, // out of reach: kept for when it's back
            };
            if (goes)
                removed.Add((item.Id, relPath));
        }
        var removedIds = removed.Count > 0 ? DeleteItems(meta, root.Id, removed) : [];
        if (removedIds.Count > 0)
            new ThumbnailStore(_db).DeleteForItems(removedIds);

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

    /// <summary>
    /// Where a row's file is, for the prune (I2): there; gone, and that can be told; or out of reach, where that can't be
    /// told. A missing file is gone when its folder, or the nearest one above it that's there, can be listed: a folder
    /// deleted or renamed on a drive that's there can be told, and its rows go, as in 0.9.1; a file in a folder's place
    /// is seen, so the folder below it is gone whatever is above. A link (a junction or symbolic link) that won't list is
    /// what its final target is: deleted on a drive that's there, its rows go (round 6, L-4); on an unplugged drive or a
    /// dropped share, they stay; a link Windows can't follow to its end is decided by where it points first (a file:
    /// gone; a loop: can't tell). A folder this user may not list keeps its files' rows (a folder below it is looked at
    /// itself, and may list: round 6, L-2), and so does a look that fails or is refused (round 5, F-4; round 7). "There"
    /// is the path, not the volume: another drive given an unplugged drive's letter is the drive that's there (round 5,
    /// L-4, an accepted residual). Each folder is looked at at most once per prune: once a folder is found missing, or
    /// its look fails, or it's a link to either, nothing below it is looked at again; no file is looked for in a folder
    /// that can't be listed; a refused look isn't passed down (a folder below may be readable). So a drive or share
    /// that's gone, reached directly or through a link, costs one look per level from the first row's folder up, then
    /// none for any other folder or file of it (each look at a server that doesn't answer can take seconds).
    /// </summary>
    internal sealed class Absence
    {
        internal enum Look { There, Gone, OutOfReach }

        // A folder: listed; there and not listed (this user may not, or a link whose target won't); missing; a look that
        // failed (a share or drive that doesn't answer); or a look refused (this user may not read it). With each, whether
        // a file missing from it can be told gone.
        private enum Reach { Listed, NotListed, Missing, Failed, Refused }

        private enum Found { Yes, No, CantTell, Refused }

        private readonly Dictionary<string, (Reach Reach, bool CanTell)> _known = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _open = new(StringComparer.OrdinalIgnoreCase); // folders being decided

        /// <summary>Test seam: how often this prune has looked at the disk (a path's attributes, or a folder's listing).</summary>
        internal int Looks { get; private set; }

        /// <summary>Test seam: how a path's attributes are read (a refused look, or a dropped call, can't be made with ACLs).</summary>
        internal Func<string, FileAttributes> ReadAttributes { get; init; } = File.GetAttributes;

        public Look At(string path)
        {
            var (reach, canTell) = Folder(Path.GetDirectoryName(path));
            if (reach != Reach.Listed)
                return canTell ? Look.Gone : Look.OutOfReach; // not looked for: it can't be there, or its folder won't say
            return LookFor(path, out var attributes) switch
            {
                Found.Yes when attributes.HasFlag(FileAttributes.Directory) => Look.Gone, // a folder in its place: L-1
                Found.Yes => Look.There,
                Found.No => Look.Gone,
                _ => Look.OutOfReach, // its look failed, or was refused
            };
        }

        private (Reach Reach, bool CanTell) Folder(string? folder)
        {
            if (folder is null)
                return (Reach.Missing, false); // above a drive's or share's root: nothing there to list
            if (_known.TryGetValue(folder, out var known))
                return known;
            for (var above = Path.GetDirectoryName(folder); above is not null; above = Path.GetDirectoryName(above))
            {
                if (!_known.TryGetValue(above, out var that))
                    continue;
                if (that.Reach is Reach.Missing or Reach.Failed)
                    return _known[folder] = that; // below a folder missing, or whose look failed: the same, unlooked
                break; // below one that's there, or whose look was refused: looked at itself (it may list: L-2, round 7)
            }
            if (!_open.Add(folder))
                return (Reach.Failed, false); // links round in a loop: can't tell, and not kept (round 7, R7-4)
            try
            {
                var answer = LookFor(folder, out var attributes) switch
                {
                    Found.No => (Reach.Missing, Folder(Path.GetDirectoryName(folder)).CanTell),
                    Found.Yes when !attributes.HasFlag(FileAttributes.Directory) => (Reach.Missing, true), // a file in its place
                    Found.Yes when Lists(folder) => (Reach.Listed, true),
                    Found.Yes when attributes.HasFlag(FileAttributes.ReparsePoint) => BehindALink(folder),
                    Found.Yes => (Reach.NotListed, false),
                    Found.Refused => (Reach.Refused, false),
                    _ => (Reach.Failed, Climb(folder)),
                };
                return _known[folder] = answer;
            }
            finally
            {
                _open.Remove(folder);
            }
        }

        // A failed look: the folders above are looked at too, as for a missing one, so a share that's gone answers for
        // every folder of it (round 6, N6-2). This one's answer stays "can't tell".
        private bool Climb(string folder)
        {
            Folder(Path.GetDirectoryName(folder));
            return false;
        }

        // A link that won't list is what its final target is: missing (deleted on a drive that's there: its files gone,
        // round 6, L-4; on a drive or share that's gone: out of reach), or a look that failed; and that's answered for
        // every folder below the link without a look (round 7, R7-5). One Windows can't follow to its end (to a file, or
        // round in a loop) is decided by where it points first: a file there is a folder gone (round 7, R7-2).
        private (Reach Reach, bool CanTell) BehindALink(string link)
        {
            string? target;
            try
            {
                Looks++;
                target = Directory.ResolveLinkTarget(link, returnFinalTarget: true)?.FullName;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return PointsAtAFile(link) ? (Reach.Missing, true) : (Reach.NotListed, false);
            }
            if (target is null)
                return (Reach.NotListed, false); // a reparse point that isn't a link (a cloud or deduplicated folder)
            return Folder(Path.TrimEndingDirectorySeparator(target)) switch
            {
                (Reach.Missing, var canTell) => (Reach.Missing, canTell),
                (Reach.Failed, _) => (Reach.Failed, false),
                _ => (Reach.NotListed, false), // there and not listed, refused, or listed a moment later: can't tell
            };
        }

        private bool PointsAtAFile(string link)
        {
            try
            {
                Looks++;
                var first = Directory.ResolveLinkTarget(link, returnFinalTarget: false)?.FullName;
                return first is not null && LookFor(first, out var attributes) == Found.Yes
                    && !attributes.HasFlag(FileAttributes.Directory);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        // There, not there, a look that failed for another reason (a share dropping a call, a drive not ready), or one
        // refused: neither of the last two is a verdict (round 5, F-4; taxonomy 13). File.Exists and Directory.Exists say
        // false for all three. A refused look isn't passed down: a folder below may be readable (round 7).
        private Found LookFor(string path, out FileAttributes attributes)
        {
            Looks++;
            attributes = default;
            try
            {
                attributes = ReadAttributes(path); // a junction's or link's own, not its target's
                return Found.Yes;
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                return Found.No;
            }
            catch (UnauthorizedAccessException)
            {
                return Found.Refused;
            }
            catch (IOException)
            {
                return Found.CantTell;
            }
        }

        private bool Lists(string folder)
        {
            Looks++;
            return CatalogRegistry.FolderIsReachable(folder);
        }
    }

    // Deletes each row only if it's still where the run read it: a row Move to… took elsewhere since is left alone
    // (round 3, L-1). Returns the ids it deleted (their thumbnails go too).
    private static List<long> DeleteItems(SqliteConnection meta, long rootId, IReadOnlyList<(long Id, string RelPath)> rows)
    {
        var deleted = new List<long>();
        using var tx = meta.BeginTransaction();
        using var cmd = meta.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM items WHERE id = @id AND root_id = @root AND rel_path = @rel;";
        var id = cmd.Parameters.Add("@id", SqliteType.Integer);
        cmd.Parameters.AddWithValue("@root", rootId);
        var rel = cmd.Parameters.Add("@rel", SqliteType.Text);
        foreach (var row in rows)
        {
            id.Value = row.Id;
            rel.Value = row.RelPath;
            if (cmd.ExecuteNonQuery() > 0)
                deleted.Add(row.Id);
        }
        tx.Commit();
        return deleted;
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
