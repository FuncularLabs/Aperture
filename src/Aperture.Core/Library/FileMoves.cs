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
/// has been found busy, later files that need the write aren't waited for either (one that has moved already, whose tags
/// must follow it, is, once: round 17, R F-6). Windows is asked to rename the file only
/// (never to copy it itself, never over another file); where it can't (another drive, or another route to one: a share
/// of it, another share, a junction or link to another drive), the file is moved as across drives: copied first
/// (CopyFileEx, so its other streams and encryption go with it; stopped on the move's token) to
/// <c>&lt;name&gt;.aperture-moving</c> (which no scan picks up), keeping its times and its plain attributes (read-only,
/// hidden, system, archive; not a cloud placeholder's), then renamed and the original deleted (a read-only one too); a
/// failure or a stop removes the copy, and an original that can't be deleted keeps it from moving (the copy removed).
/// Tags added as a file moves, with no write kept for it, follow it once it has. A path Windows' path rules read as
/// another name (<see cref="IsReadAsAnotherName"/>) isn't moved. Raw SQL on the tags' rows: a move changes no tag's
/// recency or count.
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

    /// <summary>
    /// Test seam: the Windows error the rename is refused with (17: not the same device), as if Windows had refused it,
    /// instead of renaming.
    /// </summary>
    internal int? RenameRefusedWith { get; set; }

    /// <summary>Test seam: runs just before the rename is tried (after its checks and its wait); not under ForceCopy.</summary>
    internal Action? BeforeRenaming { get; set; }

    /// <summary>Test seam: runs just before the write is committed (and before it's made again).</summary>
    internal Action? Committing { get; set; }

    /// <param name="item">
    /// The file's row, to move with it when both places are this library's (M5). Moved in the same write as its tags.
    /// </param>
    public FileMove Move(string source, string destinationFolder, CancellationToken ct = default, ItemMove? item = null)
    {
        // A path Windows' path rules read as another name would move another file, or into another folder (round 16,
        // R F-3): refused. Move to… checks the folder picked before it's read too (round 17, R F-1 / D F-1).
        if (IsReadAsAnotherName(source) || IsReadAsAnotherName(destinationFolder))
            return new FileMove(source, destinationFolder, MoveOutcome.Failed);
        string from, folder;
        try
        {
            from = AsGiven(source, Path.GetFullPath(source));
            folder = Path.TrimEndingDirectorySeparator(AsGiven(destinationFolder, Path.GetFullPath(destinationFolder)));
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

        var acrossDrives = ForceCopy;
        var copy = CopyNameFor(to);
        var copyMade = false;
        var placed = false;
        var moved = false;
        var tagged = false;
        var needsWrite = false;
        Rekey? rekey = null;
        // The file has moved, and no write for it was kept (none made, or the one made failed). What must follow it is
        // made to, once more: a write that was needed (M6), and its tags, read now, not before, so ones added as it
        // moved follow it too (round 16, R F-2; round 17, R F-2 / D F-3); failing again, a tagged file is
        // MovedTagsBehind. A mere aid (an untagged file's row) is let go.
        MoveOutcome Settled()
        {
            var taggedNow = tagged || TaggedAt(from);
            return needsWrite || taggedNow ? RekeyAgain(from, to, item, taggedNow) : MoveOutcome.Moved;
        }
        Rekey? BeginTheWrite() => needsWrite ? BeginRekey(from, to, item, wait: true)
            : item is not null && !_busy ? TryBeginRekey(from, to, item)
            : null;
        try
        {
            tagged = HasTags(from, to);
            // A tagged file needs its write; so does an online-only one with a row (moved without it, the next run would
            // download it again, or leave it out under Skip: round 2, R2-2).
            needsWrite = tagged || (item is not null && Indexing.CloudFiles.IsOnlineOnly(File.GetAttributes(from)));
            if (needsWrite && _busy)
                return Result(MoveOutcome.LibraryBusy); // found busy already: neither copied nor waited for
            if (!acrossDrives)
            {
                ct.ThrowIfCancellationRequested();
                rekey = BeginTheWrite();
                BeforeRenaming?.Invoke();
                moved = Renamed(from, to);
                if (!moved)
                {
                    // Windows can't rename it there (round 15, R F-2 / D F-1): another drive, or another route to this
                    // one. Asked to rename with copying allowed, Windows copied it itself: not stopped, not flushed,
                    // under the file's own name. Moved as across drives instead, the write let go while it's copied.
                    rekey?.Dispose();
                    rekey = null;
                    acrossDrives = true;
                }
            }
            if (acrossDrives)
            {
                if (needsWrite)
                    ProbeTheWrite(); // busy: it stays without being copied (downloaded) first (round 3, N-2)
                copyMade = true; // before the copy: one that fails part-way is removed too (round 1, N3)
                CopyTo(from, copy, ct);
                Copied?.Invoke(copy);
                ct.ThrowIfCancellationRequested();
                rekey = BeginTheWrite();
                File.Move(copy, to, overwrite: false); // in one folder: always a rename
                copyMade = false;
                placed = true;
                File.SetAttributes(to, File.GetAttributes(from) & PlainAttributes);
                DeleteOriginal(from);
                moved = true;
            }
            if (rekey is null)
                return Result(Settled());
            Committing?.Invoke();
            rekey.Commit();
            return Result(MoveOutcome.Moved);
        }
        catch (Exception ex)
        {
            rekey?.Dispose(); // rolled back: the tags and the row stay where they were
            rekey = null;
            if (moved)
                return Result(Settled());
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
    // (round 3, L-4: no run moves a row.) Found busy, later files that need the write aren't waited for (round 18, R N-3).
    private MoveOutcome RekeyAgain(string from, string to, ItemMove? item, bool tagged)
    {
        try
        {
            using var again = BeginRekey(from, to, item, wait: true);
            Committing?.Invoke();
            again.Commit();
            return MoveOutcome.Moved;
        }
        catch (Exception ex)
        {
            if (ex is LibraryBusyException)
                _busy = true;
            return tagged ? MoveOutcome.MovedTagsBehind : MoveOutcome.Moved;
        }
    }

    /// <summary>
    /// Whether Windows' usual path rules (GetFullPath's) read <paramref name="path"/> as other names than it gives: they
    /// drop the last name's dots and spaces, and a folder name's one last dot (round 16, R F-3), so the move would take
    /// another file, or go into another folder; such names are made only with \\?\. Its names are compared with what
    /// those rules read, so what else they change is refused too (a relative or drive-relative path, `.` and `..`, a
    /// short 8.3 name; round 17, D F-2), but not case, which they change only by reading long names from the disk
    /// (round 18, R F-1).
    /// </summary>
    public static bool IsReadAsAnotherName(string path) => NameReadAsAnother(path) is not null;

    /// <summary>
    /// The first name in <paramref name="path"/> that Windows' usual path rules read as another (see
    /// <see cref="IsReadAsAnotherName"/>), to be named in what's said (round 18, D-2); null when there's none.
    /// </summary>
    public static string? NameReadAsAnother(string path)
    {
        string[] given, read;
        try
        {
            given = NamesOf(path);
            read = NamesOf(Path.GetFullPath(path));
        }
        catch (Exception)
        {
            return null; // not a path Windows takes at all: said where it's used
        }
        for (var i = 0; i < given.Length; i++)
            if (i >= read.Length || !string.Equals(given[i], read[i], StringComparison.OrdinalIgnoreCase))
                return i == 0 ? given[^1] : given[i]; // read from another root (a device such as NUL; a relative path, which no caller gives): its own last name
        return read.Length == given.Length ? null : path;
    }

    private static string[] NamesOf(string path) => path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);

    // A path in the spelling it was given, where GetFullPath's differs from it only in case: the tags are kept under the
    // library's own spelling (the window looks them up by it), and with a "~" GetFullPath gives the disk's (round 19,
    // D F-1; the library's SQL folds only ASCII letters' case).
    private static string AsGiven(string given, string full) =>
        string.Equals(given, full, StringComparison.OrdinalIgnoreCase) ? given : full;

    private const int MaxNameLength = 255;

    /// <summary>
    /// The temporary copy's path: <c>&lt;name&gt;.aperture-moving</c>; where that's longer than a name may be (255), the
    /// name cut short, then a mark of the whole name, so a leftover copy is still found and replaced (round 17, R F-4).
    /// Never cut between the two halves of a character.
    /// </summary>
    internal static string CopyNameFor(string to)
    {
        var name = Path.GetFileName(to);
        if (Fits(name + CopySuffix))
            return to + CopySuffix;
        var whole = name.ToUpperInvariant(); // one mark for what one folder takes for one name
        var mark = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(whole.AsSpan())), 0, 4);
        var keep = Math.Min(name.Length, MaxNameLength - CopySuffix.Length - mark.Length - 1);
        while (keep > 0 && (char.IsHighSurrogate(name[keep - 1]) || !Fits(name[..keep] + "~" + mark + CopySuffix)))
            keep--;
        return Path.Combine(Path.GetDirectoryName(to)!, name[..keep] + "~" + mark + CopySuffix);
    }

    // Within a name's 255 as NTFS, FAT and exFAT count it (UTF-16 units) and as some other drives do (a Samba share on
    // ext4, as many NAS boxes are: UTF-8 bytes; round 18, D-4).
    private static bool Fits(string name) =>
        name.Length <= MaxNameLength && System.Text.Encoding.UTF8.GetByteCount(name) <= MaxNameLength;

    // The rename, and only that: Windows isn't let copy the file itself, nor put it over another (round 15, R F-2 /
    // D F-1). False: Windows can't make it a rename ("not the same device": another drive, or another route to this one),
    // which is exactly where, asked with copying allowed, it would have copied it. Any other refusal is the move's answer.
    private bool Renamed(string from, string to)
    {
        var error = RenameRefusedWith
            ?? (MoveFileEx(LongPath(from), LongPath(to), 0) ? 0 : System.Runtime.InteropServices.Marshal.GetLastPInvokeError());
        return error switch
        {
            0 => true,
            ErrorNotSameDevice => false,
            _ => throw Win32Error(error, from),
        };
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "MoveFileExW",
        CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existingFileName, string newFileName, uint flags);

    /// <summary>
    /// A full path as Windows' own calls take it at any length (round 15, R F-1): Aperture.exe isn't long-path aware, so
    /// from MAX_PATH (260 with its end) on it's given in its \\?\ form (\\?\UNC\ for a share); shorter, as it is, as
    /// .NET's own calls do.
    /// </summary>
    internal static string LongPath(string fullPath) =>
        fullPath.Length < 260 || fullPath.StartsWith(@"\\?\", StringComparison.Ordinal)
            || fullPath.StartsWith(@"\\.\", StringComparison.Ordinal) ? fullPath
        : fullPath.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + fullPath[2..]
        : @"\\?\" + fullPath;

    // A Windows error as .NET's own calls throw it.
    private static Exception Win32Error(int error, string from)
    {
        var message = new System.ComponentModel.Win32Exception(error).Message;
        return error switch
        {
            ErrorRequestAborted => new OperationCanceledException(message),
            ErrorAccessDenied => new UnauthorizedAccessException(message),
            ErrorFileNotFound => new FileNotFoundException(message, from),
            ErrorPathNotFound => new DirectoryNotFoundException(message),
            _ => new IOException(message, unchecked((int)0x80070000 | error)),
        };
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
        ErrorEncryptionFailed = 6000, ErrorNotSameDevice = 17;

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
        var copied = CopyFileEx(LongPath(from), LongPath(copy), progress, IntPtr.Zero, ref cancel, 0);
        var error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
        GC.KeepAlive(progress);
        if (!copied)
        {
            ct.ThrowIfCancellationRequested();
            throw Win32Error(error, from);
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

    // Whether anything is kept under a moved file's old path now; one that can't be read is taken for none (the write
    // to move them would fail as well).
    private bool TaggedAt(string path)
    {
        try
        {
            return HasTags(path, path);
        }
        catch (Exception)
        {
            return false;
        }
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
            // Matched as the column matches (its NOCASE folds ASCII letters' case only), never by a wider fold: .NET's
            // takes "σ.jpg" and "ς.jpg" for one name where NTFS keeps two files, and round 20's took a neighbour's tags
            // (round 21, D F-1). Rows the window takes for one path load as one (AnnotationStore.GetAll).
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
