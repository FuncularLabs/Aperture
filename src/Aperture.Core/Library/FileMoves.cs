using Aperture.Core.Storage;
using Microsoft.Data.Sqlite;

namespace Aperture.Core.Library;

/// <summary>What became of one file Move to… was given (0.9.2 M3).</summary>
public enum MoveOutcome
{
    Moved,

    /// <summary>A file of that name is already in the destination: neither is touched.</summary>
    NameTaken,

    /// <summary>It's in that folder already.</summary>
    AlreadyThere,

    /// <summary>It isn't there any more.</summary>
    SourceGone,

    /// <summary>The destination folder isn't there any more.</summary>
    DestinationGone,

    /// <summary>Another program has it open.</summary>
    InUse,

    /// <summary>Windows won't let Aperture move it (from there, or into there).</summary>
    AccessDenied,

    /// <summary>The library couldn't be written to move its tags (an index run held it): it stays, with them.</summary>
    LibraryBusy,

    Failed,

    /// <summary>Not moved: the move was stopped (a library switch, exit) before it came to it, or as it copied it.</summary>
    Stopped,

    /// <summary>Moved, but its tags and notes couldn't be moved with it: they stay under its old path.</summary>
    MovedTagsBehind,
}

public sealed record FileMove(string Source, string Destination, MoveOutcome Outcome);

/// <summary>
/// A moved file's row in the library, from where it was to where it goes (both in this library), so the next run finds
/// it unchanged: nothing read, nothing downloaded, no online-only question (round 1, lens 2, finding 2).
/// </summary>
public sealed record ItemMove(long FromRootId, string FromRelPath, long ToRootId, string ToRelPath);

/// <summary>A file for Move to…: its path, and its row (its folder of this library and its path in it) when it has one.</summary>
public sealed record MoveRequest(string Path, long? RootId = null, string? RelPath = null);

/// <summary>
/// Moves files into a folder, one at a time, with their tags and notes (0.9.2 M3, M4, M7). Never over another file. A
/// file's tags are moved in one short write on the library, made before the file is moved and kept only once it has
/// been: when the library can't be written (a run holds it for a batch), the file stays; when the file can't be moved,
/// the write is undone; when the write fails once the file has moved, it's made again, and if that fails too the file
/// is <see cref="MoveOutcome.MovedTagsBehind"/>. A file with no tags, and nothing kept under its new path, never waits
/// on the library, and once the library has been found busy, later tagged files given to the same instance aren't
/// waited for either. Across drives a file is copied first, in chunks on the move's token, to
/// <c>&lt;name&gt;.aperture-moving</c> (which no scan picks up), keeping its times and attributes, then renamed and
/// the original deleted (a read-only one too); a failure or a stop removes the copy, and an original that can't be
/// deleted keeps it from moving (the copy removed). Raw SQL on the tags' rows: a move changes no tag's recency or
/// count.
/// </summary>
public sealed class FileMoves(ApertureDatabase database)
{
    private const int ChunkBytes = 1 << 20;
    private bool _busy; // found busy once: later tagged files aren't waited for (review round 1, F5)

    /// <summary>What a copy across drives is called until it's renamed to its own name.</summary>
    public const string CopySuffix = ".aperture-moving";

    /// <summary>How long a move waits for the library's write lock before the file stays. Settable for tests.</summary>
    internal TimeSpan BusyTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Test seam: takes the across-drives path (copy, rename, delete) on one drive.</summary>
    internal bool ForceCopy { get; set; }

    /// <summary>Test seam: hears the temporary copy's path once it's made, before it's renamed.</summary>
    internal Action<string>? Copied { get; set; }

    /// <summary>Test seam: hears the bytes copied so far after each chunk of a copy across drives.</summary>
    internal Action<long>? CopyingChunk { get; set; }

    /// <summary>Test seam: runs just before the tags' write is committed (and before it's made again).</summary>
    internal Action? Committing { get; set; }

    /// <param name="item">
    /// The file's row, to move with it when both places are this library's. Moved in the same write as its tags; for a
    /// file with no tags the write isn't waited for (a run holds the library): the file moves, and the next run of its
    /// folders moves its row instead.
    /// </param>
    public FileMove Move(string source, string destinationFolder, CancellationToken ct = default, ItemMove? item = null)
    {
        string from, folder;
        try
        {
            from = Path.GetFullPath(source);
            folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationFolder));
        }
        catch (Exception)
        {
            return new FileMove(source, destinationFolder, MoveOutcome.Failed);
        }
        var to = Path.Combine(folder, Path.GetFileName(from));
        FileMove Result(MoveOutcome outcome) => new(from, to, outcome);

        if (string.Equals(Path.GetDirectoryName(from), folder, StringComparison.OrdinalIgnoreCase))
            return Result(MoveOutcome.AlreadyThere);
        if (!File.Exists(from))
            return Result(MoveOutcome.SourceGone);
        if (!Directory.Exists(folder))
            return Result(MoveOutcome.DestinationGone);
        if (File.Exists(to) || Directory.Exists(to))
            return Result(MoveOutcome.NameTaken);

        var acrossDrives = ForceCopy
            || !string.Equals(Path.GetPathRoot(from), Path.GetPathRoot(folder), StringComparison.OrdinalIgnoreCase);
        var copy = to + CopySuffix;
        var copyMade = false;
        var placed = false;
        var moved = false;
        Rekey? rekey = null;
        try
        {
            var tagged = HasTags(from, to);
            if (tagged && _busy)
                return Result(MoveOutcome.LibraryBusy); // found busy already: neither copied nor waited for
            if (acrossDrives)
            {
                copyMade = true; // before the copy: one that fails part-way is removed too (round 1, N3)
                CopyTo(from, copy, ct);
                Copied?.Invoke(copy);
            }
            ct.ThrowIfCancellationRequested();
            rekey = tagged ? BeginRekey(from, to, item, wait: true)
                : item is not null && !_busy ? TryBeginRekey(from, to, item)
                : null;
            if (acrossDrives)
            {
                File.Move(copy, to, overwrite: false);
                copyMade = false;
                placed = true;
                File.SetAttributes(to, File.GetAttributes(from)); // read-only stays read-only, and so on
                DeleteOriginal(from);
            }
            else
                File.Move(from, to, overwrite: false);
            moved = true;
            if (rekey is not null)
            {
                Committing?.Invoke();
                rekey.Commit();
            }
            return Result(MoveOutcome.Moved);
        }
        catch (Exception ex)
        {
            rekey?.Dispose(); // rolled back: the tags stay where they were
            rekey = null;
            if (moved)
                return Result(RekeyAgain(from, to, item)); // the file has moved: its tags are made to follow, once more
            if (copyMade)
                TryDelete(copy);
            if (placed && File.Exists(from))
                TryDelete(to);
            if (ex is LibraryBusyException)
                _busy = true;
            return Result(Outcome(ex, from, to, placed));
        }
        finally
        {
            rekey?.Dispose();
        }
    }

    // The tags' write failed once the file had moved (a full disk, an I/O error): made again in a write of its own (the
    // row too, if it was to move; if that fails, the next run of its folders moves it).
    private MoveOutcome RekeyAgain(string from, string to, ItemMove? item)
    {
        try
        {
            using var again = BeginRekey(from, to, item, wait: true);
            Committing?.Invoke();
            again.Commit();
            return MoveOutcome.Moved;
        }
        catch (Exception)
        {
            return MoveOutcome.MovedTagsBehind;
        }
    }

    // A copy across drives, in chunks so the move's token stops it; the file's times kept.
    private void CopyTo(string from, string copy, CancellationToken ct)
    {
        if (File.Exists(copy))
            File.SetAttributes(copy, FileAttributes.Normal); // a read-only leftover would refuse to be replaced
        using (var source = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkBytes))
        using (var target = new FileStream(copy, FileMode.Create, FileAccess.Write, FileShare.None, ChunkBytes))
        {
            var buffer = new byte[ChunkBytes];
            long copied = 0;
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                target.Write(buffer, 0, read);
                copied += read;
                CopyingChunk?.Invoke(copied);
            }
        }
        ct.ThrowIfCancellationRequested();
        File.SetCreationTimeUtc(copy, File.GetCreationTimeUtc(from));
        File.SetLastWriteTimeUtc(copy, File.GetLastWriteTimeUtc(from));
    }

    // Deletes the original once its copy is in place; a read-only one is made writable for it, and read-only again if
    // it can't be deleted after all (review round 1, F2).
    private static void DeleteOriginal(string from)
    {
        var attributes = File.GetAttributes(from);
        var readOnly = (attributes & FileAttributes.ReadOnly) != 0;
        if (readOnly)
            File.SetAttributes(from, attributes & ~FileAttributes.ReadOnly);
        try
        {
            File.Delete(from);
        }
        catch
        {
            if (readOnly)
                TryRestore(from, attributes);
            throw;
        }
    }

    private static void TryRestore(string path, FileAttributes attributes)
    {
        try { File.SetAttributes(path, attributes); }
        catch (Exception) { /* best effort: it stays where it was either way */ }
    }

    private static MoveOutcome Outcome(Exception ex, string from, string to, bool placed) => ex switch
    {
        OperationCanceledException => MoveOutcome.Stopped,
        LibraryBusyException => MoveOutcome.LibraryBusy,
        UnauthorizedAccessException => MoveOutcome.AccessDenied,
        FileNotFoundException when !File.Exists(from) => MoveOutcome.SourceGone,
        DirectoryNotFoundException when !File.Exists(from) => MoveOutcome.SourceGone,
        DirectoryNotFoundException => MoveOutcome.DestinationGone,
        IOException when !placed && File.Exists(to) => MoveOutcome.NameTaken, // taken meanwhile
        IOException io when (io.HResult & 0xFFFF) is 32 or 33 => MoveOutcome.InUse, // sharing or lock violation
        _ => MoveOutcome.Failed,
    };

    private static void TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path))
                return;
            File.SetAttributes(path, FileAttributes.Normal); // a read-only copy (of a read-only original) too
            File.Delete(path);
        }
        catch (Exception)
        {
            // Best effort: a .aperture-moving copy left behind is replaced by the next move of that name.
        }
    }

    private sealed class LibraryBusyException(Exception inner) : Exception("The library couldn't be written.", inner);

    // The tags' row moved from one path to the other in a write of its own: kept by Commit, undone by Dispose.
    private sealed class Rekey(SqliteConnection connection, SqliteTransaction transaction) : IDisposable
    {
        private bool _done;

        public void Commit()
        {
            transaction.Commit();
            _done = true;
        }

        public void Dispose()
        {
            if (!_done)
            {
                try { transaction.Rollback(); }
                catch (Exception) { /* the connection is going anyway */ }
                _done = true;
            }
            transaction.Dispose();
            connection.Dispose();
        }
    }

    // Whether anything is kept under either path: if not, there's nothing to move, and no wait on a library a run holds
    // (review gate 1). A read (WAL): it never waits.
    private bool HasTags(string from, string to)
    {
        using var read = database.OpenMetadata();
        using var count = read.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM annotations WHERE path = @from OR path = @to;";
        count.Parameters.AddWithValue("@from", from);
        count.Parameters.AddWithValue("@to", to);
        return Convert.ToInt64(count.ExecuteScalar()) > 0;
    }

    // An untagged file's row: the write tried once, briefly; busy, the file moves without it (and later ones don't try).
    private Rekey? TryBeginRekey(string from, string to, ItemMove item)
    {
        try
        {
            return BeginRekey(from, to, item, wait: false);
        }
        catch (LibraryBusyException)
        {
            _busy = true;
            return null;
        }
    }

    private Rekey BeginRekey(string from, string to, ItemMove? item, bool wait)
    {
        var connection = database.OpenMetadata();
        try
        {
            // Bounded both ways: SQLite's own wait, and the driver's retries (its default timeout is 30 s; 1 s at least).
            connection.DefaultTimeout = wait ? Math.Max(1, (int)Math.Ceiling(BusyTimeout.TotalSeconds)) : 1;
            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = $"PRAGMA busy_timeout={(wait ? (long)BusyTimeout.TotalMilliseconds : 0)};";
                pragma.ExecuteNonQuery();
            }
            SqliteTransaction transaction;
            try
            {
                transaction = connection.BeginTransaction(deferred: false); // BEGIN IMMEDIATE: the write lock, or busy
            }
            catch (SqliteException busy) when (busy.SqliteErrorCode is 5 or 6) // SQLITE_BUSY, SQLITE_LOCKED
            {
                throw new LibraryBusyException(busy);
            }
            using var move = connection.CreateCommand();
            move.Transaction = transaction;
            // What's under the new path was left by a file that was there once: this file's tags (or none) replace it.
            move.CommandText = "DELETE FROM annotations WHERE path = @to; UPDATE annotations SET path = @to WHERE path = @from;";
            move.Parameters.AddWithValue("@from", from);
            move.Parameters.AddWithValue("@to", to);
            move.ExecuteNonQuery();
            if (item is not null)
            {
                using var row = connection.CreateCommand();
                row.Transaction = transaction;
                // A row left where the file goes (a file once there, not pruned yet) gives way; then the file's own row
                // moves, keeping its id, so its cached thumbnails are its still.
                row.CommandText =
                    "DELETE FROM items WHERE root_id = @toRoot AND rel_path = @toRel " +
                    "AND NOT (root_id = @fromRoot AND rel_path = @fromRel); " +
                    "UPDATE items SET root_id = @toRoot, rel_path = @toRel, file_name = @name " +
                    "WHERE root_id = @fromRoot AND rel_path = @fromRel;";
                row.Parameters.AddWithValue("@fromRoot", item.FromRootId);
                row.Parameters.AddWithValue("@fromRel", item.FromRelPath);
                row.Parameters.AddWithValue("@toRoot", item.ToRootId);
                row.Parameters.AddWithValue("@toRel", item.ToRelPath);
                row.Parameters.AddWithValue("@name", Path.GetFileName(item.ToRelPath));
                row.ExecuteNonQuery();
            }
            return new Rekey(connection, transaction);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}
