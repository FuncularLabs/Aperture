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

    private readonly ApertureDatabase _db = database;
    private readonly ThumbnailGenerator _thumbnails = thumbnails;
    private readonly MetadataReader _metadata = metadata;

    public IndexResult IndexRoot(
        Root root, IProgress<IndexProgress>? progress = null, CancellationToken ct = default, bool includeVideos = true)
    {
        var nowTicks = DateTime.UtcNow.Ticks;

        progress?.Report(new IndexProgress { RootAlias = root.Alias, Phase = IndexPhase.Scanning });
        var files = FileScanner.Scan(root.Path, includeVideos, ct);

        var existing = new ItemStore(_db).GetExisting(root.Id);
        var thumbCounts = new ThumbnailStore(_db).GetCounts();
        var expectedThumbs = ThumbSizes.All.Length;

        var seen = new HashSet<string>(StringComparer.Ordinal);
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
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                seen.Add(file.RelPath);
                processed++;

                if (existing.TryGetValue(file.RelPath, out var row)
                    && row.MTimeTicks == file.MTimeTicks
                    && row.SizeBytes == file.SizeBytes)
                {
                    // Unchanged. Only touch disk if thumbnails are incomplete.
                    if (!thumbCounts.TryGetValue(row.Id, out var count) || count < expectedThumbs)
                    {
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

                    progress?.Report(new IndexProgress
                    {
                        RootAlias = root.Alias,
                        Phase = IndexPhase.Indexing,
                        Processed = processed,
                        Total = files.Count,
                        CurrentFile = file.RelPath,
                    });
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
        progress?.Report(new IndexProgress { RootAlias = root.Alias, Phase = IndexPhase.Pruning });
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
            Processed = files.Count,
            Total = files.Count,
        });

        return new IndexResult
        {
            Added = added,
            Updated = updated,
            Skipped = skipped,
            Removed = removedIds.Count,
            ThumbnailsGenerated = thumbsGenerated,
            Failed = failed,
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
