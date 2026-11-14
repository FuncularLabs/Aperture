using System.Diagnostics;
using Aperture.Core.Library;
using Aperture.Core.Storage;
using Aperture.Core.Tests.Support;
using Microsoft.Data.Sqlite;

namespace Aperture.Core.Tests;

/// <summary>
/// One file moved by Move to… (0.9.2 M3, M4, M7): never over another, tags and notes with it in one short write, a
/// move across drives through a temporary copy, and whatever fails leaves the file and its tags where they were.
/// </summary>
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
}

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

    // Holds the library's write lock, as a run's open batch does; let go (rolled back) when disposed, so the pooled
    // connection goes back to the pool without a transaction.
    // The thumbnails' file held, as a run's open batch holds it.
    private IDisposable HoldTheThumbnails()
    {
        var connection = _scope.Database.OpenThumbnails();
        var transaction = connection.BeginTransaction(deferred: false);
        return new Held(connection, transaction);
    }

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
        var other = Environment.GetEnvironmentVariable("APERTURE_OTHER_DRIVE_DIR")!;
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
