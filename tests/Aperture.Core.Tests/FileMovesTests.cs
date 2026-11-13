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

    public void Dispose() => _scope.Dispose();

    private string Photo(string name, string folder = "from")
    {
        var path = _scope.Library.Combine(folder, name);
        TestImages.Write(path, 32, 24);
        return path;
    }

    private string To(string name) => Path.Combine(_to, name);

    // Holds the library's write lock, as a run's open batch does.
    private SqliteConnection HoldTheLibrary()
    {
        var connection = _scope.Database.OpenMetadata();
        using var begin = connection.CreateCommand();
        begin.CommandText = "BEGIN IMMEDIATE;";
        begin.ExecuteNonQuery();
        return connection;
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
    public void ALeftoverCopy_FromAMoveThatWasKilled_IsReplaced()
    {
        var a = Photo("a.jpg");
        File.WriteAllText(To("a.jpg") + FileMoves.CopySuffix, "half a file");
        _moves.ForceCopy = true;

        Assert.Equal(MoveOutcome.Moved, _moves.Move(a, _to).Outcome);

        Assert.Empty(Directory.GetFiles(_to, "*" + FileMoves.CopySuffix));
    }

    [Fact]
    public void AcrossRealDrives_Works()
    {
        // Opt-in: APERTURE_OTHER_DRIVE_DIR names a folder on another drive (CI: RUNNER_TEMP). Never chosen for you.
        var other = Environment.GetEnvironmentVariable("APERTURE_OTHER_DRIVE_DIR");
        if (string.IsNullOrEmpty(other) || !Directory.Exists(other)
            || string.Equals(Path.GetPathRoot(other), Path.GetPathRoot(_from), StringComparison.OrdinalIgnoreCase))
            return;
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
