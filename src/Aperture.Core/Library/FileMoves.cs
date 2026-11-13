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

    /// <summary>Not tried: the move was stopped (a library switch, exit) before it came to it.</summary>
    Stopped,
}

public sealed record FileMove(string Source, string Destination, MoveOutcome Outcome);

/// <summary>
/// Moves one file into a folder, with its tags and notes (0.9.2 M3, M4, M7). Never over another file. Its tags are moved
/// in one short write on the library, made before the file is moved and kept only once it has been: when the library
/// can't be written (a run holds it for a batch), the file stays; when the file can't be moved, the write is undone.
/// A file with no tags, and nothing kept under its new path, never waits on the library. Across drives it's copied
/// first, to <c>&lt;name&gt;.aperture-moving</c> (which no scan picks up), then renamed and the original deleted; a
/// failure or a stop removes the copy, and an original that can't be deleted keeps it from moving (the copy removed).
/// Raw SQL on the tags' rows: a move changes no tag's recency or count.
/// </summary>
public sealed class FileMoves(ApertureDatabase database)
{
    /// <summary>What a copy across drives is called until it's renamed to its own name.</summary>
    public const string CopySuffix = ".aperture-moving";

    /// <summary>How long a move waits for the library's write lock before the file stays. Settable for tests.</summary>
    internal TimeSpan BusyTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Test seam: takes the across-drives path (copy, rename, delete) on one drive.</summary>
    internal bool ForceCopy { get; set; }

    /// <summary>Test seam: hears the temporary copy's path once it's made, before it's renamed.</summary>
    internal Action<string>? Copied { get; set; }

    public FileMove Move(string source, string destinationFolder)
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
        Rekey? rekey = null;
        try
        {
            if (acrossDrives)
            {
                File.Copy(from, copy, overwrite: true); // a copy left by a move that was killed is replaced
                copyMade = true;
                Copied?.Invoke(copy);
            }
            rekey = BeginRekey(from, to);
            if (acrossDrives)
            {
                File.Move(copy, to, overwrite: false);
                copyMade = false;
                placed = true;
                File.Delete(from); // can't be deleted (in use, read-only): the copy is removed below
            }
            else
                File.Move(from, to, overwrite: false);
            rekey?.Commit();
            return Result(MoveOutcome.Moved);
        }
        catch (Exception ex)
        {
            rekey?.Dispose(); // rolled back: the tags stay with the file
            rekey = null;
            if (copyMade)
                TryDelete(copy);
            if (placed && File.Exists(from))
                TryDelete(to);
            return Result(Outcome(ex, from, to, placed));
        }
        finally
        {
            rekey?.Dispose();
        }
    }

    private static MoveOutcome Outcome(Exception ex, string from, string to, bool placed) => ex switch
    {
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

    private Rekey? BeginRekey(string from, string to)
    {
        // Nothing kept under either path: nothing to move, and no wait on a library a run holds (review gate 1).
        using (var read = database.OpenMetadata())
        using (var count = read.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM annotations WHERE path = @from OR path = @to;";
            count.Parameters.AddWithValue("@from", from);
            count.Parameters.AddWithValue("@to", to);
            if (Convert.ToInt64(count.ExecuteScalar()) == 0)
                return null;
        }

        var connection = database.OpenMetadata();
        try
        {
            // Bounded both ways: SQLite's own wait, and the driver's retries (its default timeout is 30 s).
            connection.DefaultTimeout = Math.Max(1, (int)Math.Ceiling(BusyTimeout.TotalSeconds));
            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = $"PRAGMA busy_timeout={(long)BusyTimeout.TotalMilliseconds};";
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
            return new Rekey(connection, transaction);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}
