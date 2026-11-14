using System.Diagnostics;
using Aperture.Core.Library;
using Aperture.Core.Storage;
using Aperture.Core.Tests.Support;
using Microsoft.Data.Sqlite;

namespace Aperture.Core.Tests;

/// <summary>Runs only with APERTURE_OTHER_DRIVE_DIR set to a folder on another drive than the temp folder's.</summary>
public sealed class OtherDriveFactAttribute : FactAttribute
{
    public OtherDriveFactAttribute()
    {
        var other = Environment.GetEnvironmentVariable("APERTURE_OTHER_DRIVE_DIR");
        if (string.IsNullOrEmpty(other) || !Directory.Exists(other)
            || string.Equals(Path.GetPathRoot(Path.GetFullPath(other)), Path.GetPathRoot(Path.GetTempPath()),
                StringComparison.OrdinalIgnoreCase))
            Skip = "Needs APERTURE_OTHER_DRIVE_DIR: a folder on a drive other than the temp folder's.";
    }

    /// <summary>
    /// That folder, spelled as the code under test spells a path (D:/x is D:\x): read back under the variable's own
    /// spelling (forward slashes), a moved file's tags weren't found (plan revision 16).
    /// </summary>
    public static string Folder => Path.GetFullPath(Environment.GetEnvironmentVariable("APERTURE_OTHER_DRIVE_DIR")!);
}

/// <summary>
/// Runs only where the temp folder's drive's own share (\\localhost\C$, an administrator's) reaches the temp folder: one
/// volume by another route, which Windows can't rename across (round 15, R F-2 / D F-1).
/// </summary>
public sealed class LoopbackShareFactAttribute : FactAttribute
{
    public LoopbackShareFactAttribute()
    {
        if (!Directory.Exists(Of(Path.GetTempPath())))
            Skip = @"Needs \\localhost\<drive>$ (the drive's administrative share) to reach the temp folder.";
    }

    /// <summary>A local folder's path by its drive's share on this PC: C:\x is \\localhost\C$\x.</summary>
    public static string Of(string localPath)
    {
        var full = Path.GetFullPath(localPath);
        return @"\\localhost\" + full[0] + "$" + full[2..];
    }
}

/// <summary>
/// One file moved by Move to… (0.9.2 M3, M4, M7): never over another, tags and notes with it in one short write, a
/// move Windows can't make as a rename (across drives) through a temporary copy, and whatever fails leaves the file and
/// its tags where they were.
/// </summary>
public class FileMovesTests : IDisposable
{
    private readonly ApertureScope _scope = new();
    private readonly AnnotationStore _tags;
    private readonly FileMoves _moves;
    private readonly string _from;
    private readonly string _to;

    public FileMovesTests()
    {
        _tags = new AnnotationStore(_scope.Database);
        _moves = new FileMoves(_scope.Database);
        _from = _scope.Library.Combine("from");
        _to = _scope.Library.Combine("to");
        Directory.CreateDirectory(_from);
        Directory.CreateDirectory(_to);
    }

    public void Dispose()
    {
        // A read-only file stops the temp folder's own clean-up.
        foreach (var file in Directory.EnumerateFiles(_scope.Library.Path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        _scope.Dispose();
    }

    private static bool IsReadOnly(string path) => (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0;

    private string ReadOnlyPhoto(string name)
    {
        var path = Photo(name);
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly); // a camera's "protect"
        return path;
    }

    private string Photo(string name, string folder = "from")
    {
        var path = _scope.Library.Combine(folder, name);
        TestImages.Write(path, 32, 24);
        return path;
    }

    private string To(string name) => Path.Combine(_to, name);

    // The thumbnails' file held, as a run's open batch holds it.
    private IDisposable HoldTheThumbnails()
    {
        var connection = _scope.Database.OpenThumbnails();
        var transaction = connection.BeginTransaction(deferred: false);
        return new Held(connection, transaction);
    }

    // Holds the library's write lock, as a run's open batch does; let go (rolled back) when disposed, so the pooled
    // connection goes back to the pool without a transaction.
    private IDisposable HoldTheLibrary()
    {
        var connection = _scope.Database.OpenMetadata();
        var transaction = connection.BeginTransaction(deferred: false); // BEGIN IMMEDIATE
        return new Held(connection, transaction);
    }

    private sealed class Held(SqliteConnection connection, SqliteTransaction transaction) : IDisposable
    {
        public void Dispose()
        {
            transaction.Rollback();
            transaction.Dispose();
            connection.Dispose();
        }
    }

    // Whether the library's write lock can be had within a second (nobody holds it); taken and let go at once, and the
    // pooled connection goes back with the library's usual busy timeout. DefaultTimeout too: Microsoft.Data.Sqlite tries
    // a busy write again for its command timeout (30 s) whatever SQLite's own busy timeout is (round 16, R F-5).
    private bool CanTakeTheLibraryAtOnce()
    {
        using var connection = _scope.Database.OpenMetadata();
        connection.DefaultTimeout = 1;
        using (var none = connection.CreateCommand())
        {
            none.CommandText = "PRAGMA busy_timeout=0;";
            none.ExecuteNonQuery();
        }
        try
        {
            using var transaction = connection.BeginTransaction(deferred: false); // BEGIN IMMEDIATE
            transaction.Rollback();
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
        finally
        {
            using var usual = connection.CreateCommand();
            usual.CommandText = "PRAGMA busy_timeout=5000;";
            usual.ExecuteNonQuery();
        }
    }

    // One move in tests/Aperture.MoveProbe, a process that isn't long-path aware, as Aperture.exe isn't (round 15,
    // R F-1): what it says of itself, and the move's outcome.
    private static (string Aware, string Outcome) MoveInAProbe(string data, string file, string folder, bool copy)
    {
        var exe = AppContext.BaseDirectory.Replace(@"\Aperture.Core.Tests\bin\", @"\Aperture.MoveProbe\bin\")
            + "Aperture.MoveProbe.exe";
        Assert.True(File.Exists(exe), $"the probe isn't built: {exe}");
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { data, file, folder, copy ? "copy" : "rename" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEndAsync(); // read aside, so a hung probe is timed out, then killed
        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("the probe didn't finish in 60 s");
        }
        Assert.True(process.ExitCode == 0, $"the probe failed ({process.ExitCode}): {errors.Result}");
        var lines = output.Result.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        return (lines[0], lines[1]);
    }

    [Fact]
    public void AFile_IsMoved()
    {
        var a = Photo("a.jpg");
        var bytes = File.ReadAllBytes(a);
        var written = File.GetLastWriteTimeUtc(a);

        var move = _moves.Move(a, _to);

        Assert.Equal(MoveOutcome.Moved, move.Outcome);
        Assert.Equal(To("a.jpg"), move.Destination);
        Assert.False(File.Exists(a));
        Assert.Equal(bytes, File.ReadAllBytes(To("a.jpg")));
        Assert.Equal(written, File.GetLastWriteTimeUtc(To("a.jpg")));
    }

    [Fact]
    public void ANameTaken_TouchesNeither()
    {
        var a = Photo("a.jpg");
        File.WriteAllText(To("a.jpg"), "theirs");
        var mine = File.ReadAllBytes(a);

        var move = _moves.Move(a, _to);

        Assert.Equal(MoveOutcome.NameTaken, move.Outcome);
        Assert.Equal(mine, File.ReadAllBytes(a));
        Assert.Equal("theirs", File.ReadAllText(To("a.jpg")));
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"\")]
    public void AFileAlreadyThere_IsLeftAsItIs(string trailing)
    {
        var a = Photo("a.jpg");

        var move = _moves.Move(a, _from.ToUpperInvariant() + trailing);

        Assert.Equal(MoveOutcome.AlreadyThere, move.Outcome);
        Assert.True(File.Exists(a));
    }

    [Fact]
    public void AFileGone_IsSaid()
    {
        Assert.Equal(MoveOutcome.SourceGone, _moves.Move(Path.Combine(_from, "gone.jpg"), _to).Outcome);
    }

    [Fact]
    public void ADestinationGone_IsSaid()
    {
        var a = Photo("a.jpg");

        Assert.Equal(MoveOutcome.DestinationGone, _moves.Move(a, _scope.Library.Combine("nowhere")).Outcome);
        Assert.True(File.Exists(a));
    }

    [Fact]
    public void AFileInUse_Stays_AndKeepsItsTags()
    {
        var a = Photo("a.jpg");
        _tags.Save(a, ["trip"], "note");
        using (new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var move = _moves.Move(a, _to);

            Assert.Equal(MoveOutcome.InUse, move.Outcome);
        }

        Assert.True(File.Exists(a));
        Assert.False(File.Exists(To("a.jpg")));
        Assert.Equal(["trip"], _tags.Get(a).Tags);
        Assert.Empty(_tags.Get(To("a.jpg")).Tags);
    }

    [Fact]
    public void ADestinationItCantWriteTo_IsSaid()
    {
        var a = Photo("a.jpg");
        Acl.DenyCreateFiles(_to);
        try
        {
            Assert.Equal(MoveOutcome.AccessDenied, _moves.Move(a, _to).Outcome);
        }
        finally
        {
            Acl.RestoreCreateFiles(_to);
        }
        Assert.True(File.Exists(a));
    }

    [Fact]
    public void Tags_GoWithTheFile()
    {
        var a = Photo("a.jpg");
        _tags.Save(a, ["trip", "rome"], "the forum");

        Assert.Equal(MoveOutcome.Moved, _moves.Move(a, _to).Outcome);

        var moved = _tags.Get(To("a.jpg"));
        Assert.Equal(["trip", "rome"], moved.Tags);
        Assert.Equal("the forum", moved.Note);
        Assert.Empty(_tags.Get(a).Tags);
        Assert.Equal("", _tags.Get(a).Note);
    }

    [Fact]
    public void AStaleEntryAtTheNewPath_IsReplaced()
    {
        var a = Photo("a.jpg");
        _tags.Save(a, ["new"], "");
        _tags.Save(To("a.jpg"), ["old"], "left by a file that was there once");

        _moves.Move(a, _to);

        Assert.Equal(["new"], _tags.Get(To("a.jpg")).Tags);
        Assert.Equal("", _tags.Get(To("a.jpg")).Note);
    }

    [Fact]
    public void AFileWithoutTags_MovedOntoAStaleEntry_ClearsIt()
    {
        var a = Photo("a.jpg");
        _tags.Save(To("a.jpg"), ["old"], "stale");

        Assert.Equal(MoveOutcome.Moved, _moves.Move(a, _to).Outcome);

        Assert.Empty(_tags.Get(To("a.jpg")).Tags);
        Assert.Equal("", _tags.Get(To("a.jpg")).Note);
    }

    [Fact]
    public void AMove_ChangesNoTagsRecencyOrCount()
    {
        var a = Photo("a.jpg");
        var b = Photo("b.jpg");
        _tags.Save(a, ["trip"], "");
        _tags.Save(b, ["home"], ""); // the newest tag: first by recency
        var recency = _tags.GetTagsByRecency();
        var counts = _tags.GetTagCounts();

        _moves.Move(a, _to);

        Assert.Equal(recency, _tags.GetTagsByRecency());
        Assert.Equal(counts, _tags.GetTagCounts());
    }

    [Fact]
    public void ALibraryBusy_TheFileStays_WithItsTags()
    {
        // Review gate 1: a run holds the library's write lock for a batch; a move that can't re-key its tags doesn't move.
        var a = Photo("a.jpg");
        _tags.Save(a, ["trip"], "");
        _moves.BusyTimeout = TimeSpan.FromMilliseconds(200);
        using (HoldTheLibrary())
        {
            Assert.Equal(MoveOutcome.LibraryBusy, _moves.Move(a, _to).Outcome);
        }

        Assert.True(File.Exists(a));
        Assert.False(File.Exists(To("a.jpg")));
        Assert.Equal(["trip"], _tags.Get(a).Tags);
    }

    [Fact]
    public void AFileWithoutTags_DoesNotWaitOnABusyLibrary()
    {
        var a = Photo("a.jpg");
        var watch = Stopwatch.StartNew();
        using (HoldTheLibrary())
        {
            Assert.Equal(MoveOutcome.Moved, _moves.Move(a, _to).Outcome);
        }

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"it waited {watch.Elapsed}"); // busy_timeout is 5 s
    }

    [Fact]
    public void AcrossDrives_CopiesThenRenames_AndLeavesNoCopyBehind()
    {
        var a = Photo("a.jpg");
        _tags.Save(a, ["trip"], "");
        var written = File.GetLastWriteTimeUtc(a);
        var copied = new List<string>();
        _moves.ForceCopy = true;
        _moves.Copied = copied.Add;

        Assert.Equal(MoveOutcome.Moved, _moves.Move(a, _to).Outcome);

        Assert.Equal([To("a.jpg") + FileMoves.CopySuffix], copied); // under a name no scan takes up, first
        Assert.False(File.Exists(a));
        Assert.True(File.Exists(To("a.jpg")));
        Assert.Equal(written, File.GetLastWriteTimeUtc(To("a.jpg")));
        Assert.Empty(Directory.GetFiles(_to, "*" + FileMoves.CopySuffix));
        Assert.Equal(["trip"], _tags.Get(To("a.jpg")).Tags);
    }

    [Fact]
    public void ACopyStoppedPartWay_LeavesNothingUnderTheName_AndTheTagsWhereTheyWere()
    {
        var a = Photo("a.jpg");
        _tags.Save(a, ["trip"], "");
        _moves.ForceCopy = true;
        _moves.Copied = _ => throw new IOException("the drive went away");

        Assert.Equal(MoveOutcome.Failed, _moves.Move(a, _to).Outcome);

        Assert.True(File.Exists(a));
        Assert.Empty(Directory.GetFiles(_to));
        Assert.Equal(["trip"], _tags.Get(a).Tags);
    }

    [Fact]
    public void TheOriginalCantBeDeleted_TheCopyIsRemoved()
    {
        // MoveFileEx across drives succeeds, leaving the source, when it can't delete it; this move undoes its copy.
        var a = Photo("a.jpg");
        _tags.Save(a, ["trip"], "");
        _moves.ForceCopy = true;
        using (new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.Read)) // readable, not deletable
        {
            Assert.Equal(MoveOutcome.InUse, _moves.Move(a, _to).Outcome);
        }

        Assert.True(File.Exists(a));
        Assert.Empty(Directory.GetFiles(_to));
        Assert.Equal(["trip"], _tags.Get(a).Tags);
        Assert.Empty(_tags.Get(To("a.jpg")).Tags);
    }

    [Fact]
    public void AReadOnlyFile_IsMoved_AndStaysReadOnly()
    {
        var a = ReadOnlyPhoto("a.jpg");

        Assert.Equal(MoveOutcome.Moved, _moves.Move(a, _to).Outcome);

        Assert.False(File.Exists(a));
        Assert.True(IsReadOnly(To("a.jpg")));
    }

    [Fact]
    public void AReadOnlyFile_AcrossDrives_IsMoved_AndStaysReadOnly()
    {
        // Review round 1, F2: the copy kept the attribute, the original couldn't be deleted, nor the copy undone.
        var a = ReadOnlyPhoto("a.jpg");
        _tags.Save(a, ["trip"], "");
        _moves.ForceCopy = true;

        Assert.Equal(MoveOutcome.Moved, _moves.Move(a, _to).Outcome);

        Assert.False(File.Exists(a));
        Assert.True(IsReadOnly(To("a.jpg")));
        Assert.Equal(["a.jpg"], Directory.GetFiles(_to).Select(Path.GetFileName));
        Assert.Equal(["trip"], _tags.Get(To("a.jpg")).Tags);
    }

    [Fact]
    public void AReadOnlyFile_AcrossDrives_WithTheLibraryBusy_LeavesNoCopy()
    {
        var a = ReadOnlyPhoto("a.jpg");
        _tags.Save(a, ["trip"], "");
        _moves.ForceCopy = true;
        _moves.BusyTimeout = TimeSpan.FromMilliseconds(200);
        using (HoldTheLibrary())
        {
            Assert.Equal(MoveOutcome.LibraryBusy, _moves.Move(a, _to).Outcome);
        }

        Assert.Empty(Directory.GetFiles(_to));
        Assert.True(IsReadOnly(a));
        Assert.Equal(["trip"], _tags.Get(a).Tags);
    }

    [Fact]
    public void AReadOnlyFileInUse_AcrossDrives_LeavesNoCopy_AndStaysReadOnly()
    {
        var a = ReadOnlyPhoto("a.jpg");
        _moves.ForceCopy = true;
        using (new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Equal(MoveOutcome.InUse, _moves.Move(a, _to).Outcome);
        }

        Assert.Empty(Directory.GetFiles(_to));
        Assert.True(File.Exists(a));
        Assert.True(IsReadOnly(a));
    }

    [Fact]
    public void AReadOnlyLeftoverCopy_IsReplaced()
    {
        var a = Photo("a.jpg");
        var leftover = To("a.jpg") + FileMoves.CopySuffix;
        File.WriteAllText(leftover, "half a file");
        File.SetAttributes(leftover, FileAttributes.ReadOnly);
        _moves.ForceCopy = true;

        Assert.Equal(MoveOutcome.Moved, _moves.Move(a, _to).Outcome);

        Assert.Equal(["a.jpg"], Directory.GetFiles(_to).Select(Path.GetFileName));
    }

    [Fact]
    public void ACopyStopped_IsUndone_AndTheFileStays_WithItsTags()
    {
        // Review round 1, F3: a copy across drives stops on the move's token (a switch, exit), its copy removed.
        var a = Photo("a.jpg");
        _tags.Save(a, ["trip"], "");
        _moves.ForceCopy = true;
        using var stop = new CancellationTokenSource();
        _moves.CopyingChunk = _ => stop.Cancel();

        var move = _moves.Move(a, _to, stop.Token);

        Assert.Equal(MoveOutcome.Stopped, move.Outcome);
        Assert.True(File.Exists(a));
        Assert.Empty(Directory.GetFiles(_to));
        Assert.Equal(["trip"], _tags.Get(a).Tags);
    }

    [Fact]
    public void ACopyStopped_PartWay_StopsThere_NotAtItsEnd()
    {
        // Round 2 (dispositions lens), M-2: a photo fits in one chunk, so only the check after the copy was ever reached.
        var big = _scope.Library.Combine("from", "big.mp4");
        var bytes = new byte[24 << 20];
        new Random(7).NextBytes(bytes);
        File.WriteAllBytes(big, bytes);
        _moves.ForceCopy = true;
        using var stop = new CancellationTokenSource();
        var chunks = new List<long>();
        _moves.CopyingChunk = done =>
        {
            chunks.Add(done);
            stop.Cancel();
        };

        Assert.Equal(MoveOutcome.Stopped, _moves.Move(big, _to, stop.Token).Outcome);

        Assert.Single(chunks); // stopped at the first chunk it heard
        Assert.True(chunks[0] < bytes.Length, $"the whole file was copied first ({chunks[0]} bytes)");
        Assert.Empty(Directory.GetFiles(_to));
        Assert.Equal(bytes.Length, new FileInfo(big).Length);
    }

    [Fact]
    public void ACopy_KeepsTheFilesTimes()
    {
        var a = Photo("a.jpg");
        File.SetCreationTimeUtc(a, new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(a, new DateTime(2021, 6, 7, 8, 9, 10, DateTimeKind.Utc));
        _moves.ForceCopy = true;

        Assert.Equal(MoveOutcome.Moved, _moves.Move(a, _to).Outcome);

        Assert.Equal(new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc), File.GetCreationTimeUtc(To("a.jpg")));
        Assert.Equal(new DateTime(2021, 6, 7, 8, 9, 10, DateTimeKind.Utc), File.GetLastWriteTimeUtc(To("a.jpg")));
    }

    [Fact]
    public void ATagWriteThatFails_AfterTheFileMoved_IsTriedAgain()
    {
        // Review round 1, F4: the file had moved; its tags were left under a path that no longer exists, and it said "failed".
        var a = Photo("a.jpg");
        _tags.Save(a, ["trip"], "");
        var commits = 0;
        _moves.Committing = () =>
        {
            if (++commits == 1)
                throw new SqliteException("disk I/O error", 10);
        };

        var move = _moves.Move(a, _to);

        Assert.Equal(MoveOutcome.Moved, move.Outcome);
        Assert.Equal(2, commits);
        Assert.True(File.Exists(To("a.jpg")));
        Assert.Equal(["trip"], _tags.Get(To("a.jpg")).Tags);
    }

    [Theory]
    [InlineData(false)] // across drives
    [InlineData(true)] // a rename Windows refuses as another device (17)
    public void ATagWriteThatFails_AfterACopiedFileMoved_IsTriedAgain(bool refused)
    {
        // Round 16, D F-1: a copied move sets "moved" itself; without it, a write that failed once the file had moved
        // was undone and never made again, and the move said Failed (against M6).
        var a = Photo("a.jpg");
        _tags.Save(a, ["trip"], "");
        if (refused)
            _moves.RenameRefusedWith = 17;
        else
            _moves.ForceCopy = true;
        var commits = 0;
        _moves.Committing = () =>
        {
            if (++commits == 1)
                throw new SqliteException("disk I/O error", 10);
        };

        Assert.Equal(MoveOutcome.Moved, _moves.Move(a, _to).Outcome);

        Assert.Equal(2, commits);
        Assert.False(File.Exists(a));
        Assert.True(File.Exists(To("a.jpg")));
        Assert.Equal(["trip"], _tags.Get(To("a.jpg")).Tags);
        Assert.Empty(_tags.Get(a).Tags);
    }

    [Fact]
    public void ATagWriteThatKeepsFailing_AfterTheFileMoved_SaysItsTagsStayedBehind()
    {
        var a = Photo("a.jpg");
        _tags.Save(a, ["trip"], "");
        _moves.Committing = () => throw new SqliteException("disk I/O error", 10);

        var move = _moves.Move(a, _to);

        Assert.Equal(MoveOutcome.MovedTagsBehind, move.Outcome);
        Assert.False(File.Exists(a));
        Assert.True(File.Exists(To("a.jpg")));
        Assert.Equal(["trip"], _tags.Get(a).Tags); // where they were: an export or a move back finds them
    }

    [Fact]
    public void OnceTheLibraryIsBusy_LaterTaggedFiles_DontWaitForIt()
    {
        // Review round 1, F5: each tagged file waited out the busy timeout while a run held the library.
        var files = new[] { Photo("a.jpg"), Photo("b.jpg"), Photo("c.jpg") };
        foreach (var file in files)
            _tags.Save(file, ["trip"], "");
        _moves.BusyTimeout = TimeSpan.FromSeconds(1);
        var watch = Stopwatch.StartNew();
        using (HoldTheLibrary())
        {
            Assert.All(files, f => Assert.Equal(MoveOutcome.LibraryBusy, _moves.Move(f, _to).Outcome));
        }

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2.5), $"it waited {watch.Elapsed}"); // once, not three times
        Assert.All(files, f => Assert.True(File.Exists(f)));
    }

    [Fact]
    public void OnceTheLibraryIsBusy_AFileWithoutTags_StillMoves()
    {
        var a = Photo("a.jpg");
        var b = Photo("b.jpg");
        _tags.Save(a, ["trip"], "");
        _moves.BusyTimeout = TimeSpan.FromMilliseconds(200);
        using (HoldTheLibrary())
        {
            Assert.Equal(MoveOutcome.LibraryBusy, _moves.Move(a, _to).Outcome);
            Assert.Equal(MoveOutcome.Moved, _moves.Move(b, _to).Outcome);
        }
    }

    [Fact]
    public void ALeftoverCopy_FromAMoveThatWasKilled_IsReplaced()
    {
        var a = Photo("a.jpg");
        File.WriteAllText(To("a.jpg") + FileMoves.CopySuffix, "half a file");
        _moves.ForceCopy = true;

        Assert.Equal(MoveOutcome.Moved, _moves.Move(a, _to).Outcome);

        Assert.Empty(Directory.GetFiles(_to, "*" + FileMoves.CopySuffix));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)] // across drives, a file of that name: found before it's copied (round 14, R F-2, D F-1)
    [InlineData(true, true)] // a folder of that name (round 13, D F-1)
    public void ANameTaken_ByAFileOrAFolder_IsSaid_AndNothingIsCopied(bool acrossDrives, bool aFolder)
    {
        var a = Photo("a.jpg");
        _tags.Save(a, ["trip"], "");
        if (aFolder)
            Directory.CreateDirectory(To("a.jpg"));
        else
            File.WriteAllText(To("a.jpg"), "theirs");
        var copied = new List<string>();
        _moves.ForceCopy = acrossDrives;
        _moves.Copied = copied.Add;

        Assert.Equal(MoveOutcome.NameTaken, _moves.Move(a, _to).Outcome);

        Assert.Empty(copied);
        Assert.True(File.Exists(a));
        if (aFolder)
            Assert.Empty(Directory.EnumerateFileSystemEntries(To("a.jpg")));
        else
            Assert.Equal("theirs", File.ReadAllText(To("a.jpg")));
        Assert.Equal(["trip"], _tags.Get(a).Tags);
    }

    [Fact]
    public void AFileArrivingAfterTheCheck_IsNotOverwritten()
    {
        // Round 14, D F-2: the check is made before the move waits (for the library's write, BusyTimeout); a file that
        // arrives meanwhile, on one drive, is refused by the rename itself, which must never overwrite.
        var a = Photo("a.jpg");
        _tags.Save(a, ["trip"], "");
        _moves.BeforeRenaming = () => File.WriteAllText(To("a.jpg"), "theirs");

        Assert.Equal(MoveOutcome.NameTaken, _moves.Move(a, _to).Outcome);

        Assert.Equal("theirs", File.ReadAllText(To("a.jpg")));
        Assert.True(File.Exists(a));
        Assert.Equal(["trip"], _tags.Get(a).Tags);
    }

    [Fact]
    public void ARenameWindowsRefuses_AsAnotherDevice_IsCopiedByAperture_WithTheLibraryLetGoMeanwhile()
    {
        // Round 15, R F-2 / D F-1: Windows is asked to rename only; where it can't (another drive, or another route to
        // this one: a share, another share of it), it says "not the same device" (17), exactly where, asked to rename
        // with copying allowed, it would copy the file itself (not stopped, not flushed, under its own name). Aperture
        // then copies it, as across drives; the library's write, taken for the rename, isn't held while it copies.
        var a = Photo("a.jpg");
        _tags.Save(a, ["trip"], "");
        _moves.RenameRefusedWith = 17;
        var copied = new List<string>();
        var heldWhileCopying = true;
        _moves.Copied = copy =>
        {
            copied.Add(copy);
            heldWhileCopying = !CanTakeTheLibraryAtOnce();
        };

        Assert.Equal(MoveOutcome.Moved, _moves.Move(a, _to).Outcome);

        Assert.Single(copied);
        Assert.False(heldWhileCopying);
        Assert.False(File.Exists(a));
        Assert.True(File.Exists(To("a.jpg")));
        Assert.Equal(["trip"], _tags.Get(To("a.jpg")).Tags);
        Assert.Empty(_tags.Get(a).Tags);
    }

    [Theory]
    [InlineData(5, MoveOutcome.AccessDenied)]
    [InlineData(32, MoveOutcome.InUse)] // a sharing violation
    [InlineData(1, MoveOutcome.Failed)]
    public void ARenameWindowsRefusesOtherwise_IsSaid_AndNothingIsCopied(int error, MoveOutcome outcome)
    {
        // Round 15: only "not the same device" makes a copy; any other refusal is the move's answer.
        var a = Photo("a.jpg");
        _tags.Save(a, ["trip"], "");
        _moves.RenameRefusedWith = error;
        var copied = new List<string>();
        _moves.Copied = copied.Add;

        Assert.Equal(outcome, _moves.Move(a, _to).Outcome);

        Assert.Empty(copied);
        Assert.True(File.Exists(a));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_to));
        Assert.Equal(["trip"], _tags.Get(a).Tags);
    }

    [Theory]
    [InlineData("copy")] // across drives: added as the file is copied (minutes, for a large video)
    [InlineData("refused")] // a rename Windows refuses as another device (17), so copied
    [InlineData("rename")] // added just before the rename
    public void ATagAddedWhileTheFileMoves_GoesWithIt(string how)
    {
        // Round 16, R F-2: whether a file has tags was read once, before the move; one with none and no row in this
        // library made no write, so a tag added meanwhile stayed under the old path, and the move said Moved.
        var a = Photo("a.jpg");
        Action late = () => _tags.Save(a, ["late"], "");
        if (how == "copy")
            _moves.ForceCopy = true;
        if (how == "refused")
            _moves.RenameRefusedWith = 17;
        if (how == "rename")
            _moves.BeforeRenaming = late;
        else
            _moves.Copied = _ => late();

        Assert.Equal(MoveOutcome.Moved, _moves.Move(a, _to).Outcome);

        Assert.True(File.Exists(To("a.jpg")));
        Assert.Equal(["late"], _tags.Get(To("a.jpg")).Tags);
        Assert.Empty(_tags.Get(a).Tags);
    }

    [Theory]
    [InlineData(false)] // across drives
    [InlineData(true)] // a rename Windows refuses as another device (17), so copied
    public void ATagAddedAsItsCopied_WhenTheRowsWriteFails_IsTriedAgain(bool refused)
    {
        // Round 17, R F-2 / D F-3: an untagged file with a row; a tag added as it's copied; the row's brief write (which
        // carries the tag) fails once the file has moved. What follows was decided from "tagged" as read before the
        // copy: Moved, never tried again, the tag under the old path.
        var a = Photo("a.jpg");
        var (root, id) = Indexed("a.jpg");
        if (refused)
            _moves.RenameRefusedWith = 17;
        else
            _moves.ForceCopy = true;
        _moves.Copied = _ => _tags.Save(a, ["late"], "");
        var commits = 0;
        _moves.Committing = () =>
        {
            if (++commits == 1)
                throw new SqliteException("disk I/O error", 10);
        };

        Assert.Equal(MoveOutcome.Moved, _moves.Move(a, _to, default, Row(root, "a.jpg")).Outcome);

        Assert.Equal(2, commits);
        Assert.Equal(["late"], _tags.Get(To("a.jpg")).Tags);
        Assert.Empty(_tags.Get(a).Tags);
        var row = Assert.Single(_scope.Items.GetForRoot(root.Id));
        Assert.Equal((id, Path.Combine("to", "a.jpg")), (row.Id, row.RelPath));
    }

    [Theory]
    [InlineData("no row")] // the late write is the only one
    [InlineData("online-only, with a row")] // its write was needed, and fails again
    public void ATagAddedAsItsCopied_WhenEveryWriteFails_IsSaidToHaveStayedBehind(string kind)
    {
        // Round 17, R F-3 / D F-4, R F-2: the tags of a file that has moved, whose write can't be made, are said to have
        // stayed behind (MovedTagsBehind), never Moved with nothing said, whenever they were added.
        var a = Photo("a.jpg");
        ItemMove? item = null;
        if (kind != "no row")
        {
            File.SetAttributes(a, File.GetAttributes(a) | FileAttributes.Offline);
            var (root, _) = Indexed("a.jpg");
            item = Row(root, "a.jpg");
        }
        _moves.ForceCopy = true;
        _moves.Copied = _ => _tags.Save(a, ["late"], "");
        _moves.Committing = () => throw new SqliteException("disk I/O error", 10);

        Assert.Equal(MoveOutcome.MovedTagsBehind, _moves.Move(a, _to, default, item).Outcome);

        Assert.True(File.Exists(To("a.jpg")));
        Assert.Equal(["late"], _tags.Get(a).Tags);
    }

    [Fact]
    public void ATagAdded_OnceTheLibraryWasFoundBusy_TakesTheFilesRowWithIt()
    {
        // Round 17, R F-3: with the library found busy once, an untagged file's row isn't tried for; a tag added as it
        // moves is made to follow it, and its row with it.
        var x = Photo("x.jpg");
        _tags.Save(x, ["held"], "");
        _moves.BusyTimeout = TimeSpan.FromMilliseconds(200);
        using (HoldTheLibrary())
            Assert.Equal(MoveOutcome.LibraryBusy, _moves.Move(x, _to).Outcome); // found busy
        var a = Photo("a.jpg");
        var (root, id) = Indexed("a.jpg");
        _moves.BeforeRenaming = () => _tags.Save(a, ["late"], "");

        Assert.Equal(MoveOutcome.Moved, _moves.Move(a, _to, default, Row(root, "a.jpg")).Outcome);

        Assert.Equal(["late"], _tags.Get(To("a.jpg")).Tags);
        var row = _scope.Items.GetForRoot(root.Id).Single(i => i.Id == id);
        Assert.Equal(Path.Combine("to", "a.jpg"), row.RelPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)] // across drives
    public void AnOnlineOnlyFile_WhoseWriteFailsOnceItHasMoved_IsTriedAgain_AndTakesItsRow(bool acrossDrives)
    {
        // Round 18, D-1: an untagged online-only file with a row needs its write (moved without its row, the next run
        // would download it again, or leave it out under Skip: M5). Failing once the file has moved, it's made again.
        var a = Photo("a.jpg");
        File.SetAttributes(a, File.GetAttributes(a) | FileAttributes.Offline);
        var (root, id) = Indexed("a.jpg");
        _moves.ForceCopy = acrossDrives;
        var commits = 0;
        _moves.Committing = () =>
        {
            if (++commits == 1)
                throw new SqliteException("disk I/O error", 10);
        };

        Assert.Equal(MoveOutcome.Moved, _moves.Move(a, _to, default, Row(root, "a.jpg")).Outcome);

        Assert.Equal(2, commits);
        var row = Assert.Single(_scope.Items.GetForRoot(root.Id));
        Assert.Equal((id, Path.Combine("to", "a.jpg")), (row.Id, row.RelPath));
    }

    [Theory]
    [InlineData(239)] // the copy's name, 16 longer, still fits
    [InlineData(245)]
    [InlineData(255)] // the longest a name may be
    public void AFileWithALongName_IsMovedAcrossDrives_AndALeftoverCopyOfItIsReplaced(int length)
    {
        // Round 17, R F-4: "<name>.aperture-moving" is 16 longer than the name; past 255 (a name's limit) the copy
        // couldn't be made, and the file "couldn't be moved", where Windows would have moved it.
        var name = new string('a', length - 4) + ".jpg";
        var a = Photo(name);
        _tags.Save(a, ["trip"], "");
        var leftover = FileMoves.CopyNameFor(To(name));
        File.WriteAllText(leftover, "left by a move that was killed");
        _moves.ForceCopy = true;
        var copies = new List<string>();
        _moves.Copied = copies.Add;

        Assert.Equal(MoveOutcome.Moved, _moves.Move(a, _to).Outcome);

        Assert.Equal([leftover], copies);
        Assert.True(Path.GetFileName(leftover).Length <= 255);
        if (length == 239)
            Assert.Equal(To(name) + FileMoves.CopySuffix, leftover); // not cut (round 18, D)
        Assert.True(File.Exists(To(name)));
        Assert.False(File.Exists(a));
        Assert.Empty(Directory.EnumerateFiles(_to, "*" + FileMoves.CopySuffix));
        Assert.Equal(["trip"], _tags.Get(To(name)).Tags);
    }

    [Fact]
    public void CopyNameFor_IsTheNameAndTheSuffix_OrACutNameThatsStillTheFilesOwn()
    {
        // Round 17, R F-4: a predictable name (a leftover copy is found by it), within 255, never a split character.
        var folder = @"C:\x";
        Assert.Equal(@"C:\x\a.jpg" + FileMoves.CopySuffix, FileMoves.CopyNameFor(@"C:\x\a.jpg"));
        var longOne = Path.Combine(folder, new string('a', 251) + ".jpg");
        var another = Path.Combine(folder, new string('a', 250) + "b.jpg");
        var cut = FileMoves.CopyNameFor(longOne);
        Assert.Equal(cut, FileMoves.CopyNameFor(longOne));
        Assert.NotEqual(cut, FileMoves.CopyNameFor(another));
        Assert.Equal(folder, Path.GetDirectoryName(cut));
        Assert.EndsWith(FileMoves.CopySuffix, cut);
        Assert.True(Path.GetFileName(cut).Length <= 255);
        // 253: a letter, then characters made of two halves each, so a cut at an even length splits one.
        var faces = Path.Combine(folder, "b" + string.Concat(Enumerable.Repeat("😀", 124)) + ".jpg");
        var name = Path.GetFileName(FileMoves.CopyNameFor(faces));
        Assert.True(name.Length <= 255);
        Assert.Equal(name, System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(name))); // no half left
        // Round 18, D-4: some drives (a Samba share on ext4, so many NAS boxes) count a name's 255 in UTF-8 bytes:
        // 85 such characters are 259 bytes and 89 characters, so the copy's name is cut by its bytes too.
        var wide = Path.Combine(folder, new string('写', 85) + ".jpg");
        var bytesCut = Path.GetFileName(FileMoves.CopyNameFor(wide));
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(bytesCut) <= 255, $"{System.Text.Encoding.UTF8.GetByteCount(bytesCut)} bytes");
        Assert.EndsWith(FileMoves.CopySuffix, bytesCut);
        Assert.Equal(bytesCut, Path.GetFileName(FileMoves.CopyNameFor(wide)));
        // 227 letters, then one character of two halves: by its bytes the cut first fits between the halves.
        var edge = Path.Combine(folder, new string('a', 227) + "😀" + new string('a', 20) + ".jpg");
        var edged = Path.GetFileName(FileMoves.CopyNameFor(edge));
        Assert.Equal(edged, System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(edged)));
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(edged) <= 255);
        // Round 18, R N-2: one mark for one name, whatever its case (a folder that takes "A" for "a" finds the leftover).
        Assert.Equal(cut, FileMoves.CopyNameFor(longOne.ToUpperInvariant()), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void APathWithATilde_InAnotherCaseThanOnDisk_IsntReadAsAnotherName_AndMoves()
    {
        // Round 18, R F-1: with a "~" in a path, GetFullPath asks Windows for its long names, which come back in the
        // disk's case: compared by case, a library folder renamed only in case ("photos" kept, "Photos" on disk) made
        // every file under a "~" unmovable, and every move into it.
        var onDisk = _scope.Library.Combine("My~Trip");
        Directory.CreateDirectory(onDisk);
        var a = Photo("a.jpg", "My~Trip");
        var asKept = _scope.Library.Combine("my~trip");
        Assert.False(FileMoves.IsReadAsAnotherName(Path.Combine(asKept, "a.jpg")));

        Assert.Equal(MoveOutcome.Moved, _moves.Move(Path.Combine(asKept, "a.jpg"), _to).Outcome);
        Assert.Equal(MoveOutcome.Moved, _moves.Move(To("a.jpg"), asKept).Outcome);

        Assert.True(File.Exists(a));
    }

    [Fact]
    public void AMovedFile_WhoseTagsCantBeReadOnceItHas_IsMoved_NotSaidToHaveLostThem()
    {
        // Round 18, R N-2: once a file with no write has moved, its old path is read for tags added meanwhile; a library
        // that can't be read then is taken for none (the write would fail too), not for tags left behind.
        var a = Photo("a.jpg");
        _moves.BeforeRenaming = () => _scope.Database.Dispose(); // the library closed under the move

        Assert.Equal(MoveOutcome.Moved, _moves.Move(a, _to).Outcome);

        Assert.True(File.Exists(To("a.jpg")));
    }

    [Fact]
    public void ALateTagsWrite_FoundBusy_TellsTheNextFileTheLibraryIsBusy()
    {
        // Round 18, R N-3: once a file's tags couldn't follow it for a busy library, later files that need the write
        // aren't waited for (they stay, LibraryBusy, at once), as after any write found busy.
        var a = Photo("a.jpg");
        var b = Photo("b.jpg");
        _tags.Save(b, ["trip"], "");
        _moves.ForceCopy = true;
        _moves.BusyTimeout = TimeSpan.FromSeconds(1);
        IDisposable? held = null;
        _moves.Copied = _ =>
        {
            _tags.Save(a, ["late"], "");
            held ??= HoldTheLibrary(); // a run takes the library as the first file is copied
        };
        try
        {
            Assert.Equal(MoveOutcome.MovedTagsBehind, _moves.Move(a, _to).Outcome);
            var watch = Stopwatch.StartNew();

            Assert.Equal(MoveOutcome.LibraryBusy, _moves.Move(b, _to).Outcome);

            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(0.6), $"it waited {watch.Elapsed}");
        }
        finally
        {
            held?.Dispose();
        }
        Assert.True(File.Exists(b));
        Assert.Equal(["trip"], _tags.Get(b).Tags);
    }

    [Theory]
    [InlineData(@"C:\x\a.jpg", null)]
    [InlineData(@"C:\x\a.jpg ", "a.jpg ")]
    [InlineData(@"C:\x\to2.\sub", "to2.")] // the first name read as another, not the last (round 18, D-2)
    [InlineData(@"C:\x\to2.\sub.", "to2.")]
    [InlineData(@"C:\x\sp \sub", null)]
    public void NameReadAsAnother_IsTheFirstNameWindowsPathRulesChange(string path, string? name)
    {
        Assert.Equal(name, FileMoves.NameReadAsAnother(path));
    }

    [Theory]
    [InlineData("the file")] // "a.jpg " beside "a.jpg"
    [InlineData("a folder above the file")] // "from2.\a.jpg" beside "from2\a.jpg"
    [InlineData("the destination folder")] // "to2." beside "to2"
    [InlineData("a folder above the destination")] // "to2.\sub" beside "to2\sub" (round 17, D F-5)
    public void ANameWindowsReadsAsAnother_IsntMoved(string where)
    {
        // Round 16, R F-3: Windows' path rules (GetFullPath's) drop the last name's dots and spaces, and a folder name's
        // last dot, so Move to… acted on another file (its twin went, with its tags) or into another folder. Made only
        // with \\?\ (WSL, some tools; not Explorer), a path those rules change is refused: nothing is touched.
        var twinFrom = _scope.Library.Combine("from2");
        var twinTo = _scope.Library.Combine("to2");
        var other = Photo("a.jpg", "from2");
        _tags.Save(other, ["theirs"], "");
        var dotted = where switch
        {
            "the file" => other + " ",
            "a folder above the file" => twinFrom + ".",
            _ => twinTo + ".",
        };
        var asked = where switch
        {
            "the file" => dotted,
            "a folder above the file" => Path.Combine(dotted, "a.jpg"),
            _ => Photo("b.jpg"),
        };
        var below = where == "a folder above the destination";
        var into = where == "the destination folder" ? dotted : below ? Path.Combine(dotted, "sub") : twinTo;
        var twinInto = below ? Path.Combine(twinTo, "sub") : twinTo;
        Directory.CreateDirectory(twinInto);
        try
        {
            if (where == "the file")
                File.WriteAllText(@"\\?\" + asked, "mine");
            else
                Directory.CreateDirectory(@"\\?\" + dotted);
            if (where == "a folder above the file")
                File.WriteAllText(@"\\?\" + asked, "mine");
            if (below)
                Directory.CreateDirectory(@"\\?\" + into);

            Assert.Equal(MoveOutcome.Failed, _moves.Move(asked, into).Outcome);

            Assert.True(File.Exists(other));
            Assert.Equal(["theirs"], _tags.Get(other).Tags);
            Assert.Empty(Directory.EnumerateFileSystemEntries(twinInto));
            Assert.True(File.Exists(@"\\?\" + asked));
            if (where is "the destination folder" or "a folder above the destination")
                Assert.Empty(Directory.EnumerateFileSystemEntries(@"\\?\" + into));
        }
        finally
        {
            // .NET's own calls can't reach these names except in their \\?\ form.
            if (where == "the file" && File.Exists(@"\\?\" + asked))
                File.Delete(@"\\?\" + asked);
            if (where != "the file" && Directory.Exists(@"\\?\" + dotted))
                Directory.Delete(@"\\?\" + dotted, recursive: true);
        }
    }

    [Theory]
    [InlineData("the file's folder")] // "sp \a.jpg"
    [InlineData("a folder above the destination")] // "sp \sub"
    public void ANameWindowsKeeps_IsMoved(string where)
    {
        // Round 17, D F-2: a folder name ending in a space, not the last name, is kept by Windows' path rules; refused
        // all the same, it made such a folder's files, or a destination under it, unmovable.
        var spaced = _scope.Library.Combine("sp ");
        Directory.CreateDirectory(@"\\?\" + spaced); // as the last name, "sp " would be made as "sp"
        try
        {
            var a = where == "the file's folder" ? Path.Combine(spaced, "a.jpg") : Photo("a.jpg");
            if (where == "the file's folder")
                TestImages.Write(a, 32, 24);
            var into = where == "the file's folder" ? _to : Path.Combine(spaced, "sub");
            Directory.CreateDirectory(into);
            _tags.Save(a, ["trip"], "");

            Assert.Equal(MoveOutcome.Moved, _moves.Move(a, into).Outcome);

            Assert.False(File.Exists(a));
            Assert.True(File.Exists(Path.Combine(into, "a.jpg")));
            Assert.Equal(["trip"], _tags.Get(Path.Combine(into, "a.jpg")).Tags);
        }
        finally
        {
            Directory.Delete(@"\\?\" + spaced, recursive: true); // .NET reaches it only in its \\?\ form
        }
    }

    [Theory]
    [InlineData(@"C:\x\a.jpg", false)]
    [InlineData(@"C:\x\sp \a.jpg", false)] // a folder's last space is kept
    [InlineData(@"C:\x\to2..\a.jpg", false)] // so are two dots
    [InlineData(@"C:\x\to2. \a.jpg", false)]
    [InlineData(@"C:\x\to\", false)] // a separator at the end
    [InlineData(@"C:/x/a.jpg", false)] // forward ones
    [InlineData(@"C:\", false)]
    [InlineData(@"\\server\share\x", false)]
    [InlineData(@"\\?\C:\x\a.jpg.", false)] // the \\?\ form isn't read by those rules
    [InlineData(@"C:\x\a.jpg ", true)]
    [InlineData(@"C:\x\a.jpg.", true)]
    [InlineData(@"C:\x\to2.\a.jpg", true)] // a folder's last dot is dropped
    [InlineData(@"C:\x\to2..", true)] // and the last name's dots
    [InlineData(@"C:\x\to2 ", true)]
    [InlineData(@"\\server\share\x.", true)]
    public void IsReadAsAnotherName_IsWhatWindowsPathRulesChange(string path, bool another)
    {
        // Round 17, D F-2: refused is exactly what GetFullPath reads as another name, no more (R F-3's rule was wider).
        Assert.Equal(another, FileMoves.IsReadAsAnotherName(path));
    }

    [Theory]
    [InlineData(false, MoveOutcome.SourceGone)]
    [InlineData(true, MoveOutcome.DestinationGone)]
    public void WhatGoesDuringTheWait_IsSaidByTheRename(bool theFolder, MoveOutcome outcome)
    {
        // Round 16, D F-3: the rename's own errors (Windows' 2 and 3, mapped by Aperture since round 15, not .NET): the
        // file or the destination folder gone while a tagged move waited for the library.
        var a = Photo("a.jpg");
        _tags.Save(a, ["trip"], "");
        _moves.BeforeRenaming = () =>
        {
            if (theFolder)
                Directory.Delete(_to);
            else
                File.Delete(a);
        };

        Assert.Equal(outcome, _moves.Move(a, _to).Outcome);

        Assert.Equal(["trip"], _tags.Get(a).Tags);
    }

    [Fact]
    public void AnotherLetterForThisFolder_IsARename()
    {
        // Round 15, R F-3: another drive letter for this same folder (subst) is renamed onto by Windows: no copy.
        var a = Photo("a.jpg");
        var copied = new List<string>();
        _moves.Copied = copied.Add;
        var renaming = false;
        _moves.BeforeRenaming = () => renaming = true;
        var letter = Subst.CreateOnAFreeLetter(_to);
        try
        {
            Assert.Equal(MoveOutcome.Moved, _moves.Move(a, letter + @"\").Outcome);

            Assert.True(renaming);
            Assert.Empty(copied);
            Assert.False(File.Exists(a));
            Assert.True(File.Exists(To("a.jpg")));
        }
        finally
        {
            Subst.Remove(letter);
        }
    }

    [Fact]
    public void LongPath_IsWindowsLongForm_OnlyPastMaxPath()
    {
        // Round 15, R F-1: Aperture.exe isn't long-path aware, so Windows' own calls need the \\?\ form past MAX_PATH
        // (260 with its end); as .NET's own calls make it, only then.
        var at259 = @"C:\" + new string('a', 256);
        var at260 = at259 + "b";
        var shared = @"\\server\share\" + new string('a', 250);

        Assert.Equal(at259, FileMoves.LongPath(at259));
        Assert.Equal(@"\\?\" + at260, FileMoves.LongPath(at260));
        Assert.Equal(@"\\?\UNC\server\share\" + new string('a', 250), FileMoves.LongPath(shared));
        Assert.Equal(@"\\?\" + at260, FileMoves.LongPath(@"\\?\" + at260));
        Assert.Equal(@"\\.\" + at260, FileMoves.LongPath(@"\\.\" + at260));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)] // across drives: the copy's name is longer still
    public void ALongPath_IsMoved_ByAProcessThatIsntLongPathAware(bool acrossDrives)
    {
        // Round 15, R F-1: this test host is long-path aware and Aperture.exe isn't, so the move runs in a probe that
        // isn't (tests/Aperture.MoveProbe). Without the \\?\ form, a move on one volume said "the folder isn't there
        // any more", and one across drives always had.
        var deep = _scope.Library.Combine(new string('d', 120), new string('e', 120));
        var from = Path.Combine(deep, "from");
        var to = Path.Combine(deep, "to");
        Directory.CreateDirectory(from);
        Directory.CreateDirectory(to);
        var a = Path.Combine(from, "a.jpg");
        TestImages.Write(a, 32, 24);
        Assert.True(Path.Combine(to, "a.jpg").Length > 260);
        using var data = new TempDir();

        var (aware, outcome) = MoveInAProbe(data.Path, a, to, acrossDrives);

        Assert.Equal("not long-path aware", aware);
        Assert.Equal(nameof(MoveOutcome.Moved), outcome);
        Assert.False(File.Exists(a));
        Assert.True(File.Exists(Path.Combine(to, "a.jpg")));
        Assert.Empty(Directory.EnumerateFiles(to, "*" + FileMoves.CopySuffix));
    }

    [Fact]
    public void ANameTaken_OnOneDrive_IsSaidAtOnce_WithTheLibraryBusy()
    {
        // Round 15, R F-4: on one drive the rename refuses a taken name too, so the early check's own work there is not
        // waiting: a tagged file whose name is taken is said at once, not LibraryBusy after the wait (which would take
        // the next tagged file for busy as well).
        var a = Photo("a.jpg");
        var b = Photo("b.jpg");
        _tags.Save(a, ["trip"], "");
        _tags.Save(b, ["trip"], "");
        File.WriteAllText(To("a.jpg"), "theirs");
        _moves.BusyTimeout = TimeSpan.FromSeconds(2);
        var watch = Stopwatch.StartNew();
        using (HoldTheLibrary())
        {
            Assert.Equal(MoveOutcome.NameTaken, _moves.Move(a, _to).Outcome);
        }

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1.5), $"it waited {watch.Elapsed}");
        Assert.Equal(MoveOutcome.Moved, _moves.Move(b, _to).Outcome);
        Assert.Equal("theirs", File.ReadAllText(To("a.jpg")));
        Assert.Equal(["trip"], _tags.Get(a).Tags);
    }

    [LoopbackShareFact]
    public void AShareOfThisFolder_IsMovedAsAcrossDrives()
    {
        // Round 15, R F-2 / D F-1: one volume by another route (here the drive's own share): its volume serial number is
        // this one's, and Windows can't rename across: renamed with copying allowed, it copied the file itself.
        var a = Photo("a.jpg");
        _tags.Save(a, ["trip"], "");
        var copied = new List<string>();
        _moves.Copied = copied.Add;
        var share = LoopbackShareFactAttribute.Of(_to);

        Assert.Equal(MoveOutcome.Moved, _moves.Move(a, share).Outcome);

        Assert.Single(copied);
        Assert.False(File.Exists(a));
        Assert.True(File.Exists(To("a.jpg")));
        Assert.Empty(Directory.EnumerateFiles(_to, "*" + FileMoves.CopySuffix));
        Assert.Equal(["trip"], _tags.Get(Path.Combine(share, "a.jpg")).Tags);
    }

    [OtherDriveFact]
    public void AFileBehindALinkToAnotherDrive_IsMovedAsAcrossDrives()
    {
        // Round 15, R F-3 / D F-3: R F-1's "(or source)": the file's own folder is a junction to another drive.
        var target = Path.Combine(OtherDriveFactAttribute.Folder, "aperture-move-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        var link = _scope.Library.Combine("elsewhere");
        Junction.Create(link, target);
        try
        {
            var a = Path.Combine(link, "a.jpg");
            TestImages.Write(a, 32, 24);
            var copied = new List<string>();
            _moves.Copied = copied.Add;

            Assert.Equal(MoveOutcome.Moved, _moves.Move(a, _to).Outcome);

            Assert.Single(copied);
            Assert.False(File.Exists(a));
            Assert.True(File.Exists(To("a.jpg")));
        }
        finally
        {
            Directory.Delete(link);
            Directory.Delete(target, recursive: true);
        }
    }

    [OtherDriveFact]
    public void AFolderBehindALinkToAnotherDrive_IsMovedAsAcrossDrives()
    {
        // Round 14, R F-1: the destination has this drive's letter and another volume (a junction). A rename made Windows
        // copy the file itself: not stopped by the token, not flushed, under the file's own name (a move cut short left a
        // full-size broken file there). Now it's copied as across drives.
        var other = OtherDriveFactAttribute.Folder;
        var target = Path.Combine(other, "aperture-move-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        var link = _scope.Library.Combine("elsewhere");
        Junction.Create(link, target);
        try
        {
            var a = Photo("a.jpg");
            var copied = new List<string>();
            _moves.Copied = copied.Add;

            Assert.Equal(MoveOutcome.Moved, _moves.Move(a, link).Outcome);

            Assert.Single(copied);
            Assert.False(File.Exists(a));
            Assert.True(File.Exists(Path.Combine(link, "a.jpg")));
        }
        finally
        {
            Directory.Delete(link);
            Directory.Delete(target, recursive: true);
        }
    }

    [Fact]
    public void AFolderOfTheFilesName_MadeDuringTheCopy_IsANameTaken()
    {
        // The catch's "taken meanwhile" arm: a folder too, not only a file (round 13, D F-1).
        var a = Photo("a.jpg");
        _moves.ForceCopy = true;
        _moves.Copied = _ => Directory.CreateDirectory(To("a.jpg"));

        Assert.Equal(MoveOutcome.NameTaken, _moves.Move(a, _to).Outcome);

        Assert.True(File.Exists(a));
        Assert.Empty(Directory.GetFiles(_to)); // the copy removed
    }

    [Fact]
    public void ANameTakenDuringTheCopy_IsNotOverwritten()
    {
        // Round 1 (lens 2, finding 1): a copy across drives can take minutes; a file of that name arriving meanwhile wins.
        var a = Photo("a.jpg");
        _tags.Save(a, ["trip"], "");
        _moves.ForceCopy = true;
        _moves.Copied = _ => File.WriteAllText(To("a.jpg"), "theirs");

        Assert.Equal(MoveOutcome.NameTaken, _moves.Move(a, _to).Outcome);

        Assert.Equal("theirs", File.ReadAllText(To("a.jpg")));
        Assert.True(File.Exists(a));
        Assert.Equal(["a.jpg"], Directory.GetFiles(_to).Select(Path.GetFileName)); // the copy removed
        Assert.Equal(["trip"], _tags.Get(a).Tags);
        Assert.Empty(_tags.Get(To("a.jpg")).Tags);
    }

    // The library's own folder, with from\ and to\ inside it, indexed; and the row of from\<name>.
    private (Models.Root Root, long Id) Indexed(string name)
    {
        var root = _scope.AddLibraryRoot();
        _scope.Indexer.IndexRoot(root);
        return (root, _scope.Items.GetForRoot(root.Id).Single(i => i.RelPath == Path.Combine("from", name)).Id);
    }

    private ItemMove Row(Models.Root root, string name) =>
        new(root.Id, Path.Combine("from", name), root.Id, Path.Combine("to", name));

    [Fact]
    public void TheLibraryRowGoesWithTheFile_SoTheNextRunReadsNothing()
    {
        // Round 1 (lens 2, finding 2): moved without its row, the file was new to the next run: read again, and, online
        // only, downloaded again or (Skip) left out of the library.
        var a = Photo("a.jpg");
        var (root, id) = Indexed("a.jpg");
        var reads = new List<string>();
        _scope.Indexer.Reading = reads.Add;

        Assert.Equal(MoveOutcome.Moved, _moves.Move(a, _to, default, Row(root, "a.jpg")).Outcome);

        var row = Assert.Single(_scope.Items.GetForRoot(root.Id));
        Assert.Equal((id, Path.Combine("to", "a.jpg")), (row.Id, row.RelPath));
        var again = _scope.Indexer.IndexRoot(root);
        Assert.Empty(reads);
        Assert.Equal((0, 0, 0), (again.Added, again.Updated, again.Removed));
    }

    [Theory]
    [InlineData(false)] // across drives
    [InlineData(true)] // a rename Windows refuses as another device (17)
    public void ARowGoesWithACopiedFile_AndTheLibraryIsntHeldWhileItsCopied(bool refused)
    {
        // Round 16, D F-2: an untagged file with a row, copied: its row goes with it (M5), the brief write for it isn't
        // held through the copy (a run would wait, and the next try would find the library "busy" by its own lock),
        // and nothing holds the library once it's moved.
        var a = Photo("a.jpg");
        var (root, id) = Indexed("a.jpg");
        if (refused)
            _moves.RenameRefusedWith = 17;
        else
            _moves.ForceCopy = true;
        var heldWhileCopying = true;
        _moves.Copied = _ => heldWhileCopying = !CanTakeTheLibraryAtOnce();

        Assert.Equal(MoveOutcome.Moved, _moves.Move(a, _to, default, Row(root, "a.jpg")).Outcome);

        Assert.False(heldWhileCopying);
        var row = Assert.Single(_scope.Items.GetForRoot(root.Id));
        Assert.Equal((id, Path.Combine("to", "a.jpg")), (row.Id, row.RelPath));
        Assert.True(CanTakeTheLibraryAtOnce());
    }

    [Fact]
    public void AnOnlineOnlyFileMoved_IsntDownloadedAgain_NorAskedAbout()
    {
        var a = Photo("a.jpg");
        File.SetAttributes(a, File.GetAttributes(a) | FileAttributes.Offline);
        var (root, _) = Indexed("a.jpg");
        var asked = 0;

        _moves.Move(a, _to, default, Row(root, "a.jpg"));
        _scope.Indexer.IndexRoot(root, null, default, true, (_, _) => { asked++; return Indexing.OnlineOnlyChoice.Skip; });

        Assert.Equal(0, asked);
        Assert.Equal(Path.Combine("to", "a.jpg"), Assert.Single(_scope.Items.GetForRoot(root.Id)).RelPath); // still there
    }

    [Fact]
    public void AStaleRowWhereTheFileGoes_IsReplacedByItsOwn()
    {
        var a = Photo("a.jpg");
        var stale = Photo("a.jpg", "to");
        var (root, id) = Indexed("a.jpg");
        File.Delete(stale); // its row is left until the next run prunes it

        Assert.Equal(MoveOutcome.Moved, _moves.Move(a, _to, default, Row(root, "a.jpg")).Outcome);

        var row = Assert.Single(_scope.Items.GetForRoot(root.Id));
        Assert.Equal((id, Path.Combine("to", "a.jpg")), (row.Id, row.RelPath));
    }

    [Fact]
    public void ALocalUntaggedFile_WithTheLibraryBusy_StillMoves_AndTheNextRunReadsItAsNew()
    {
        // The row is an aid for a local file with no tags (round 2, R2-2 / M-1, accepted): without it, the next run reads
        // the file again, as a new one (a new row; its old one pruned).
        var a = Photo("a.jpg");
        var (root, id) = Indexed("a.jpg");
        var watch = Stopwatch.StartNew();
        using (HoldTheLibrary())
        {
            Assert.Equal(MoveOutcome.Moved, _moves.Move(a, _to, default, Row(root, "a.jpg")).Outcome);
        }

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2.5), $"it waited {watch.Elapsed}");
        Assert.Equal(Path.Combine("from", "a.jpg"), Assert.Single(_scope.Items.GetForRoot(root.Id)).RelPath); // not moved
        var next = _scope.Indexer.IndexRoot(root);
        var row = Assert.Single(_scope.Items.GetForRoot(root.Id));
        Assert.Equal(Path.Combine("to", "a.jpg"), row.RelPath);
        Assert.NotEqual(id, row.Id);
        Assert.Equal((1, 1), (next.Added, next.Removed));
    }

    [Fact]
    public void AnOnlineOnlyFile_WithTheLibraryBusy_Stays_AndKeepsItsRow()
    {
        // Round 2, R2-2: moved without its row, an online-only file is new to the next run: downloaded again, or left out
        // under Skip. It waits for the write as a tagged file does, and stays if it can't be had.
        var a = Photo("a.jpg");
        File.SetAttributes(a, File.GetAttributes(a) | FileAttributes.Offline);
        var (root, _) = Indexed("a.jpg");
        _moves.BusyTimeout = TimeSpan.FromMilliseconds(200);
        using (HoldTheLibrary())
        {
            Assert.Equal(MoveOutcome.LibraryBusy, _moves.Move(a, _to, default, Row(root, "a.jpg")).Outcome);
        }

        Assert.True(File.Exists(a));
        Assert.Empty(Directory.GetFiles(_to));
        Assert.Equal(Path.Combine("from", "a.jpg"), Assert.Single(_scope.Items.GetForRoot(root.Id)).RelPath);
    }

    [Fact]
    public void ATaggedFile_TakesItsRowWithItsTags()
    {
        var a = Photo("a.jpg");
        var (root, id) = Indexed("a.jpg");
        _tags.Save(a, ["trip"], "");

        Assert.Equal(MoveOutcome.Moved, _moves.Move(a, _to, default, Row(root, "a.jpg")).Outcome);

        var row = Assert.Single(_scope.Items.GetForRoot(root.Id));
        Assert.Equal((id, Path.Combine("to", "a.jpg")), (row.Id, row.RelPath));
        Assert.Equal(["trip"], _tags.Get(To("a.jpg")).Tags);
    }

    [Fact]
    public void ATagWriteThatFails_AfterTheFileMoved_TakesTheRowToo()
    {
        var a = Photo("a.jpg");
        var (root, id) = Indexed("a.jpg");
        _tags.Save(a, ["trip"], "");
        var commits = 0;
        _moves.Committing = () =>
        {
            if (++commits == 1)
                throw new SqliteException("disk I/O error", 10);
        };

        Assert.Equal(MoveOutcome.Moved, _moves.Move(a, _to, default, Row(root, "a.jpg")).Outcome);

        Assert.Equal((id, Path.Combine("to", "a.jpg")), (_scope.Items.GetForRoot(root.Id).Single().Id,
            _scope.Items.GetForRoot(root.Id).Single().RelPath));
        Assert.Equal(["trip"], _tags.Get(To("a.jpg")).Tags);
    }

    [Fact]
    public void OnceTheLibraryIsBusy_LaterUntaggedFiles_DontTryAgain()
    {
        var files = new[] { Photo("a.jpg"), Photo("b.jpg"), Photo("c.jpg") };
        var root = _scope.AddLibraryRoot();
        _scope.Indexer.IndexRoot(root);
        var watch = Stopwatch.StartNew();
        using (HoldTheLibrary())
        {
            foreach (var file in files)
                Assert.Equal(MoveOutcome.Moved, _moves.Move(file, _to, default, Row(root, Path.GetFileName(file))).Outcome);
        }

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2.5), $"it waited {watch.Elapsed}"); // about 1 s, once
    }

    [Fact]
    public void AnUntaggedFile_WhoseRowCantBeWritten_StillMoves()
    {
        // Round 2, R2-5: with nothing kept under either path, the row is an aid, not a condition.
        var a = Photo("a.jpg");
        var (root, _) = Indexed("a.jpg");

        var move = _moves.Move(a, _to, default, new ItemMove(root.Id, Path.Combine("from", "a.jpg"), 999_999, "a.jpg")); // no such folder

        Assert.Equal(MoveOutcome.Moved, move.Outcome);
        Assert.True(File.Exists(To("a.jpg")));
    }

    [Fact]
    public void AnUntaggedFile_WhoseRowsCommitFails_IsMoved_NotMovedWithoutTags()
    {
        var a = Photo("a.jpg");
        var (root, _) = Indexed("a.jpg");
        _moves.Committing = () => throw new SqliteException("disk I/O error", 10);

        Assert.Equal(MoveOutcome.Moved, _moves.Move(a, _to, default, Row(root, "a.jpg")).Outcome);
        Assert.True(File.Exists(To("a.jpg")));
    }

    [Fact]
    public void ACopy_DoesntCarryTheSourcesCloudState()
    {
        // Round 2, R2-6: Offline, Pinned and Unpinned stick on an ordinary file, and Aperture reads Offline as online only.
        var a = ReadOnlyPhoto("a.jpg");
        File.SetAttributes(a, File.GetAttributes(a) | FileAttributes.Offline | (FileAttributes)0x80000);
        _moves.ForceCopy = true;

        Assert.Equal(MoveOutcome.Moved, _moves.Move(a, _to).Outcome);

        var attributes = File.GetAttributes(To("a.jpg"));
        Assert.True((attributes & FileAttributes.ReadOnly) != 0);
        Assert.Equal((FileAttributes)0, attributes & (FileAttributes.Offline | (FileAttributes)0x80000 | (FileAttributes)0x100000));
    }

    [Fact]
    public void ACopy_KeepsTheFilesOtherStreams()
    {
        // Round 2, R2-7: a stream copy lost what File.Copy kept (alternate data streams; EFS likewise).
        var a = Photo("a.jpg");
        File.WriteAllText(a + ":aperture.test", "kept");
        _moves.ForceCopy = true;

        Assert.Equal(MoveOutcome.Moved, _moves.Move(a, _to).Outcome);

        Assert.Equal("kept", File.ReadAllText(To("a.jpg") + ":aperture.test"));
    }

    [Fact]
    public void TheWrite_LeavesNoQuickGiveUpBehind_ForTheLibrarysOtherWrites()
    {
        // Round 2, nit 5: the untagged file's try sets busy_timeout 0 on a pooled connection.
        var a = Photo("a.jpg");
        var (root, _) = Indexed("a.jpg");
        _moves.Move(a, _to, default, Row(root, "a.jpg"));

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _scope.Database.MetadataPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = true, ForeignKeys = true,
        }.ToString());
        connection.Open(); // from the pool, as FunkyORM's own connections are
        using var read = connection.CreateCommand();
        read.CommandText = "PRAGMA busy_timeout;";
        Assert.Equal(5000L, Convert.ToInt64(read.ExecuteScalar()));
    }

    [Fact]
    public void AStaleRowsThumbnails_DontHoldTheMoveUp_WhileARunHoldsThem()
    {
        // Round 3, N-1: their delete waited the thumbnails' busy timeout and the driver's retries (up to 30 s), unstopped.
        var a = Photo("a.jpg");
        var stale = Photo("a.jpg", "to");
        var (root, _) = Indexed("a.jpg");
        File.Delete(stale);
        var watch = Stopwatch.StartNew();
        using (HoldTheThumbnails())
        {
            Assert.Equal(MoveOutcome.Moved, _moves.Move(a, _to, default, Row(root, "a.jpg")).Outcome);
        }

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"it waited {watch.Elapsed}"); // left to the cache: ids aren't reused
    }

    [Fact]
    public void AnOnlineOnlyFile_AcrossDrives_WithTheLibraryBusy_IsntCopied()
    {
        // Round 3, N-2: it was copied (downloaded) first, then found it couldn't take its row, and the copy thrown away.
        var a = Photo("a.jpg");
        File.SetAttributes(a, File.GetAttributes(a) | FileAttributes.Offline);
        var (root, _) = Indexed("a.jpg");
        _moves.ForceCopy = true;
        _moves.BusyTimeout = TimeSpan.FromMilliseconds(200);
        var copied = 0;
        _moves.CopyingChunk = _ => copied++;
        using (HoldTheLibrary())
        {
            Assert.Equal(MoveOutcome.LibraryBusy, _moves.Move(a, _to, default, Row(root, "a.jpg")).Outcome);
        }

        Assert.Equal(0, copied);
        Assert.Empty(Directory.GetFiles(_to));
    }

    [Fact]
    public void AnEncryptedFile_ForADriveThatCantKeepItEncrypted_IsSaid()
    {
        // Round 3, L-3: CopyFileEx refuses (ERROR_ENCRYPTION_FAILED) rather than write a decrypted copy; said as such.
        Assert.Equal(MoveOutcome.CantStayEncrypted,
            FileMoves.OutcomeOf(new IOException("The specified file could not be encrypted.", unchecked((int)0x80071770)),
                @"C:\a.jpg", @"E:\a.jpg", placed: false));
        Assert.Equal(MoveOutcome.InUse,
            FileMoves.OutcomeOf(new IOException("in use", unchecked((int)0x80070020)), @"C:\a.jpg", @"E:\a.jpg", placed: false));
    }

    [Theory]
    [InlineData(System.Security.AccessControl.FileSystemRights.Read)] // a virus scanner, the search indexer
    [InlineData(System.Security.AccessControl.FileSystemRights.Read | System.Security.AccessControl.FileSystemRights.Write)] // a sync client
    [InlineData(System.Security.AccessControl.FileSystemRights.Read | System.Security.AccessControl.FileSystemRights.Delete)] // one that may move it
    public void ACopy_HeldOpenByAnotherProgramAsItsFlushed_StillMoves(System.Security.AccessControl.FileSystemRights holds)
    {
        // Round 4, F5: the flush opened the new copy for itself alone, so another program holding it just then turned a
        // good move into "in use", and the copy was thrown away. Round 5, N-3 / F-5: a reader alone let a flush shared
        // only for reading pass; one holding it to write or delete needs those shared too.
        var a = Photo("a.jpg");
        FileStream? reader = null;
        _moves.ForceCopy = true;
        _moves.BeforeFlush = copy => reader = new FileInfo(copy).Create(
            FileMode.Open, holds, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.None, null);
        _moves.Copied = _ => reader?.Dispose();

        var outcome = _moves.Move(a, _to).Outcome;
        reader?.Dispose();

        Assert.NotNull(reader);
        Assert.Equal(MoveOutcome.Moved, outcome);
        Assert.False(File.Exists(a));
        Assert.True(File.Exists(To("a.jpg")));
    }

    [Fact]
    public void AStaleRowsThumbnails_GoWithIt()
    {
        // Round 2, nit 7: the row left where the file goes gives way; its cached thumbnails went nowhere.
        var a = Photo("a.jpg");
        var stale = Photo("a.jpg", "to");
        var (root, _) = Indexed("a.jpg");
        var staleId = _scope.Items.GetForRoot(root.Id).Single(i => i.RelPath == Path.Combine("to", "a.jpg")).Id;
        File.Delete(stale);
        Assert.True(_scope.Thumbnails.GetInfo().ContainsKey(staleId));

        _moves.Move(a, _to, default, Row(root, "a.jpg"));

        Assert.False(_scope.Thumbnails.GetInfo().ContainsKey(staleId));
    }

    [OtherDriveFact]
    public void AcrossRealDrives_Works()
    {
        // Opt-in: APERTURE_OTHER_DRIVE_DIR names a folder on another drive. Never chosen for you; skipped without it.
        var other = OtherDriveFactAttribute.Folder;
        var target = Path.Combine(other, "aperture-move-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        try
        {
            var a = Photo("a.jpg");
            _tags.Save(a, ["trip"], "");

            Assert.Equal(MoveOutcome.Moved, _moves.Move(a, target).Outcome);

            Assert.False(File.Exists(a));
            Assert.True(File.Exists(Path.Combine(target, "a.jpg")));
            Assert.Equal(["trip"], _tags.Get(Path.Combine(target, "a.jpg")).Tags);
        }
        finally
        {
            Directory.Delete(target, recursive: true);
        }
    }
}
