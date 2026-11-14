using Aperture.Core.Storage;
using Microsoft.Data.Sqlite;

namespace Aperture.Core.Library;

/// <summary>What became of one file Move to… was given (0.9.2 M3).</summary>
public enum MoveOutcome
{
    Moved,

    /// <summary>A file or folder of that name is already in the destination: neither is touched.</summary>
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

    /// <summary>It's encrypted, and the destination drive can't keep it encrypted (FAT32, exFAT): it stays.</summary>
    CantStayEncrypted,
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
/// Moves files into a folder, one at a time, with their tags and notes (0.9.2 M3, M4, M5, M7). Never over another file.
/// A file's tags, and its row in the library when it goes to a folder of this library, are moved in one short write on
/// the library, made before the file is moved and kept only once it has been: when the library can't be written (a run
/// holds it for a batch), a tagged or online-only file stays; when the file can't be moved, the write is undone; when
/// the write fails once the file has moved, it's made again, and if that fails too a tagged file is
/// <see cref="MoveOutcome.MovedTagsBehind"/>. For a local file with no tags (nothing kept under either path) the row is
/// an aid, not a condition: the write is tried briefly (about a second, and not again once the library has been found
/// busy), and without it the file moves anyway and the next run of the destination reads it as new. Once the library
/// has been found busy, later files that need the write aren't waited for either. Across drives a file is copied first
/// (CopyFileEx, so its other streams and encryption go with it; stopped on the move's token) to
/// <c>&lt;name&gt;.aperture-moving</c> (which no scan picks up), keeping its times and its plain attributes (read-only,
/// hidden, system, archive; not a cloud placeholder's), then renamed and the original deleted (a read-only one too); a
/// failure or a stop removes the copy, and an original that can't be deleted keeps it from moving (the copy removed).
/// Raw SQL on the tags' rows: a move changes no tag's recency or count.
/// </summary>
public sealed class FileMoves(ApertureDatabase database)
{
    private const int DefaultBusyTimeoutMs = 5000; // what ApertureDatabase gives every connection: restored before one goes back

    // The attributes a moved file keeps; not Offline, Pinned, Unpinned and the like, a sync client's (round 2, R2-6).
    private const FileAttributes PlainAttributes =
        FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive
        | FileAttributes.NotContentIndexed;

    private bool _busy; // found busy once: later files that need the write aren't waited for (round 1, F5)

    /// <summary>What a copy across drives is called until it's renamed to its own name.</summary>
    public const string CopySuffix = ".aperture-moving";

    /// <summary>How long a move waits for the library's write lock before the file stays. Settable for tests.</summary>
    internal TimeSpan BusyTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Test seam: takes the across-drives path (copy, rename, delete) on one drive.</summary>
    internal bool ForceCopy { get; set; }

    /// <summary>Test seam: hears the temporary copy's path once it's made, before it's renamed.</summary>
    internal Action<string>? Copied { get; set; }

    /// <summary>Test seam: hears the temporary copy's path once it's written, just before it's flushed to disk.</summary>
    internal Action<string>? BeforeFlush { get; set; }

    /// <summary>Test seam: hears the bytes copied so far as a copy across drives goes (on the copying thread).</summary>
    internal Action<long>? CopyingChunk { get; set; }

    /// <summary>Test seam: runs just before a move on one volume renames the file (after its checks and its wait).</summary>
    internal Action? BeforeRenaming { get; set; }

    /// <summary>Test seam: runs just before the write is committed (and before it's made again).</summary>
    internal Action? Committing { get; set; }

    /// <param name="item">
    /// The file's row, to move with it when both places are this library's (M5). Moved in the same write as its tags.
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

        if (ct.IsCancellationRequested)
            return Result(MoveOutcome.Stopped); // not begun: nothing read, the library's files not touched
        if (string.Equals(Path.GetDirectoryName(from), folder, StringComparison.OrdinalIgnoreCase))
            return Result(MoveOutcome.AlreadyThere);
        if (!File.Exists(from))
            return Result(MoveOutcome.SourceGone);
        if (!Directory.Exists(folder))
            return Result(MoveOutcome.DestinationGone);
        if (File.Exists(to) || Directory.Exists(to))
            return Result(MoveOutcome.NameTaken);

        // By the volume, not the drive letter (round 14, R F-1): renamed onto another volume (a junction or link on this
        // drive's letter to another drive), Windows copies the file itself, not stopped, not flushed, under its own name.
        var acrossDrives = ForceCopy || !OnOneVolume(Path.GetDirectoryName(from)!, folder);
        var copy = to + CopySuffix;
        var copyMade = false;
        var placed = false;
        var moved = false;
        var tagged = false;
        var needsWrite = false;
        Rekey? rekey = null;
        try
        {
            tagged = HasTags(from, to);
            // A tagged file needs its write; so does an online-only one with a row (moved without it, the next run would
            // download it again, or leave it out under Skip: round 2, R2-2).
            needsWrite = tagged || (item is not null && Indexing.CloudFiles.IsOnlineOnly(File.GetAttributes(from)));
            if (needsWrite && _busy)
                return Result(MoveOutcome.LibraryBusy); // found busy already: neither copied nor waited for
            if (acrossDrives && needsWrite)
                ProbeTheWrite(); // busy: it stays without being copied (downloaded) first (round 3, N-2)
            if (acrossDrives)
            {
                copyMade = true; // before the copy: one that fails part-way is removed too (round 1, N3)
                CopyTo(from, copy, ct);
                Copied?.Invoke(copy);
            }
            ct.ThrowIfCancellationRequested();
            rekey = needsWrite ? BeginRekey(from, to, item, wait: true)
                : item is not null && !_busy ? TryBeginRekey(from, to, item)
                : null;
            if (acrossDrives)
            {
                File.Move(copy, to, overwrite: false);
                copyMade = false;
                placed = true;
                File.SetAttributes(to, File.GetAttributes(from) & PlainAttributes);
                DeleteOriginal(from);
            }
            else
            {
                BeforeRenaming?.Invoke();
                File.Move(from, to, overwrite: false);
            }
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
            rekey?.Dispose(); // rolled back: the tags and the row stay where they were
            rekey = null;
            if (moved) // the file has moved: what needs to follow it is made to, once more; a mere aid is let go
                return Result(needsWrite ? RekeyAgain(from, to, item, tagged) : MoveOutcome.Moved);
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

    // The write failed once the file had moved (a full disk, an I/O error): made again in a write of its own. Failing
    // again, a tagged file's tags stay under its old path (said: MovedTagsBehind); an untagged online-only file is just
    // Moved, and the next run of its destination reads it as new (downloads it; under Skip, leaves it out) — accepted.
    // (round 3, L-4: no run moves a row.)
    private MoveOutcome RekeyAgain(string from, string to, ItemMove? item, bool tagged)
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
            return tagged ? MoveOutcome.MovedTagsBehind : MoveOutcome.Moved;
        }
    }

    /// <summary>
    /// Whether two folders are on one volume, by what they lead to (through any junction, link, mount point or drive
    /// letter), not by their drive letters (round 14, R F-1). Can't tell: not one, so the copy path, which is right on
    /// one volume too, only slower. Two volumes with one serial number (a disk cloned whole) are taken for one.
    /// </summary>
    internal static bool OnOneVolume(string a, string b) => VolumeOf(a) is { } x && VolumeOf(b) is { } y && x == y;

    private static uint? VolumeOf(string folder)
    {
        using var handle = CreateFile(folder, 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open,
            FileFlagBackupSemantics, IntPtr.Zero); // opens the folder a link leads to; reads nothing
        return !handle.IsInvalid && GetFileInformationByHandle(handle, out var info) ? info.VolumeSerialNumber : null;
    }

    private const uint FileFlagBackupSemantics = 0x02000000;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "CreateFileW",
        CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(
        string name, uint access, FileShare share, IntPtr security, FileMode mode, uint flags, IntPtr template);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        Microsoft.Win32.SafeHandles.SafeFileHandle file, out ByHandleFileInformation info);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint Attributes;
        public uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint VolumeSerialNumber;
        public uint SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    // --- Copying across drives (CopyFileEx: its other streams, encryption and times go with it; stopped on the token)

    private delegate uint CopyProgressRoutine(
        long totalFileSize, long totalBytesTransferred, long streamSize, long streamBytesTransferred, uint streamNumber,
        uint callbackReason, IntPtr sourceFile, IntPtr destinationFile, IntPtr data);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "CopyFileExW",
        CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CopyFileEx(
        string existingFileName, string newFileName, CopyProgressRoutine? progress, IntPtr data, ref int cancel, uint flags);

    private const uint ProgressContinue = 0, ProgressCancel = 1, CallbackChunkFinished = 0;
    private const int ErrorRequestAborted = 1235, ErrorAccessDenied = 5, ErrorFileNotFound = 2, ErrorPathNotFound = 3,
        ErrorEncryptionFailed = 6000;

    private void CopyTo(string from, string copy, CancellationToken ct)
    {
        TryDelete(copy); // a copy left by a move that was killed (read-only or not) is replaced
        var cancel = 0;
        CopyProgressRoutine progress = (_, transferred, _, _, _, reason, _, _, _) =>
        {
            if (reason == CallbackChunkFinished)
                CopyingChunk?.Invoke(transferred);
            return ct.IsCancellationRequested ? ProgressCancel : ProgressContinue; // cancelled: the copy is deleted
        };
        var copied = CopyFileEx(from, copy, progress, IntPtr.Zero, ref cancel, 0);
        var error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
        GC.KeepAlive(progress);
        if (!copied)
        {
            ct.ThrowIfCancellationRequested();
            var message = new System.ComponentModel.Win32Exception(error).Message;
            throw error switch
            {
                ErrorRequestAborted => new OperationCanceledException(message),
                ErrorAccessDenied => new UnauthorizedAccessException(message),
                ErrorFileNotFound => new FileNotFoundException(message, from),
                ErrorPathNotFound => new DirectoryNotFoundException(message),
                _ => new IOException(message, unchecked((int)0x80070000 | error)),
            };
        }
        ct.ThrowIfCancellationRequested();
        // On disk before the original goes (CopyFileEx doesn't flush): writable for the flush, its attributes set later.
        // Shared: a virus scanner, the search indexer or a sync client may be reading the new file (round 4, F5).
        File.SetAttributes(copy, File.GetAttributes(copy) & ~FileAttributes.ReadOnly);
        BeforeFlush?.Invoke(copy);
        using (var written = new FileStream(copy, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
            written.Flush(flushToDisk: true); // round 3, L-2
        File.SetCreationTimeUtc(copy, File.GetCreationTimeUtc(from)); // CopyFileEx keeps the last-write time, not this
        File.SetLastWriteTimeUtc(copy, File.GetLastWriteTimeUtc(from));
    }

    // Whether the library's write can be had now (waiting BusyTimeout), before a copy across drives that the write must
    // follow: a run holding it would make the copy for nothing. Taken and let go at once.
    private void ProbeTheWrite()
    {
        using var probe = BeginRekey(@"\\?\probe-from", @"\\?\probe-to", null, wait: true); // rolled back on dispose
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

    internal static MoveOutcome OutcomeOf(Exception ex, string from, string to, bool placed) => Outcome(ex, from, to, placed);

    private static MoveOutcome Outcome(Exception ex, string from, string to, bool placed) => ex switch
    {
        OperationCanceledException => MoveOutcome.Stopped,
        LibraryBusyException => MoveOutcome.LibraryBusy,
        // CopyFileEx won't write a decrypted copy where encryption can't be kept (FAT32, exFAT): round 3, L-3.
        IOException io when (io.HResult & 0xFFFF) == ErrorEncryptionFailed => MoveOutcome.CantStayEncrypted,
        UnauthorizedAccessException => MoveOutcome.AccessDenied,
        FileNotFoundException when !File.Exists(from) => MoveOutcome.SourceGone,
        DirectoryNotFoundException when !File.Exists(from) => MoveOutcome.SourceGone,
        DirectoryNotFoundException => MoveOutcome.DestinationGone,
        IOException when !placed && (File.Exists(to) || Directory.Exists(to)) => MoveOutcome.NameTaken, // taken meanwhile (a folder too: round 13)
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

    // The write that moves a file's tags (and row) from one path to the other: kept by Commit, undone by Dispose. Its
    // connection goes back to the pool with the library's usual busy timeout (round 2, nit 5).
    private sealed class Rekey(ApertureDatabase database, SqliteConnection connection, SqliteTransaction transaction, long? staleId)
        : IDisposable
    {
        private bool _done;
        private bool _closed;

        public void Commit()
        {
            transaction.Commit();
            _done = true;
            if (staleId is { } id)
                DeleteThumbnailsBriefly(database, id);
        }

        public void Dispose()
        {
            if (_closed)
                return;
            _closed = true;
            if (!_done)
            {
                try { transaction.Rollback(); }
                catch (Exception) { /* the connection is going anyway */ }
                _done = true;
            }
            transaction.Dispose();
            Release(connection);
        }
    }

    // The stale row's thumbnails, if the thumbnails' file can be written at once; else left (a run holds it): ids aren't
    // reused, so they never show for another file, and the cache's own pruning takes them in time (round 3, N-1).
    private static void DeleteThumbnailsBriefly(ApertureDatabase database, long itemId)
    {
        SqliteConnection? connection = null;
        try
        {
            connection = database.OpenThumbnails();
            connection.DefaultTimeout = 1;
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA busy_timeout=0; DELETE FROM thumbs WHERE item_id = @id;";
            command.Parameters.AddWithValue("@id", itemId);
            command.ExecuteNonQuery();
        }
        catch (Exception)
        {
            // Busy or broken: best effort.
        }
        finally
        {
            if (connection is not null)
                Release(connection);
        }
    }

    // Back to the pool as ApertureDatabase gives it: this one's busy timeout was changed for the write.
    private static void Release(SqliteConnection connection)
    {
        try
        {
            using var pragma = connection.CreateCommand();
            pragma.CommandText = $"PRAGMA busy_timeout={DefaultBusyTimeoutMs};";
            pragma.ExecuteNonQuery();
        }
        catch (Exception)
        {
            // A connection that can't run a pragma is broken; disposing it below is all that's left.
        }
        connection.Dispose();
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

    // A local, untagged file's row: the write tried once, briefly. Busy, or failing in any way (round 2, R2-5), the file
    // moves without it (and later ones don't try once busy).
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
        catch (Exception)
        {
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
            long? staleId = null;
            if (item is not null)
            {
                using var stale = connection.CreateCommand();
                stale.Transaction = transaction;
                stale.CommandText = "SELECT id FROM items WHERE root_id = @toRoot AND rel_path = @toRel " +
                                    "AND NOT (root_id = @fromRoot AND rel_path = @fromRel);";
                Bind(stale, item);
                if (stale.ExecuteScalar() is long id)
                    staleId = id;
                using var row = connection.CreateCommand();
                row.Transaction = transaction;
                // A row left where the file goes (a file once there, not pruned yet) gives way; then the file's own row
                // moves, keeping its id, so its cached thumbnails are its still.
                row.CommandText =
                    "DELETE FROM items WHERE root_id = @toRoot AND rel_path = @toRel " +
                    "AND NOT (root_id = @fromRoot AND rel_path = @fromRel); " +
                    "UPDATE items SET root_id = @toRoot, rel_path = @toRel, file_name = @name " +
                    "WHERE root_id = @fromRoot AND rel_path = @fromRel;";
                Bind(row, item);
                row.Parameters.AddWithValue("@name", Path.GetFileName(item.ToRelPath));
                row.ExecuteNonQuery();
            }
            return new Rekey(database, connection, transaction, staleId);
        }
        catch
        {
            Release(connection);
            throw;
        }
    }

    private static void Bind(SqliteCommand command, ItemMove item)
    {
        command.Parameters.AddWithValue("@fromRoot", item.FromRootId);
        command.Parameters.AddWithValue("@fromRel", item.FromRelPath);
        command.Parameters.AddWithValue("@toRoot", item.ToRootId);
        command.Parameters.AddWithValue("@toRel", item.ToRelPath);
    }
}
