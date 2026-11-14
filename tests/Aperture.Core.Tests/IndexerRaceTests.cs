using Aperture.Core.Library;
using Aperture.Core.Models;
using Aperture.Core.Tests.Support;

namespace Aperture.Core.Tests;

/// <summary>
/// A run's count and its reads are apart in time (0.9.2 round 2, R2-4 and OOR-1): a file can vanish, be locked, or be
/// moved in or out by Move to… meanwhile, or the folder go away. None of that ends the run, loses a row it shouldn't,
/// or keeps one it shouldn't: from round 3 on, the tests end on <see cref="AssertTheRowsAreTheFiles"/> where the files
/// are all there to read.
/// </summary>
public class IndexerRaceTests
{
    [Fact]
    public void AFileGoneBeforeItsRead_IsLeftOut_AndTheRunGoesOn()
    {
        using var scope = new ApertureScope();
        foreach (var name in new[] { "a.jpg", "b.jpg", "c.jpg" })
            TestImages.Write(scope.Library.Combine(name), 32, 24);
        var root = scope.AddLibraryRoot();
        scope.Indexer.AfterScan = () => File.Delete(scope.Library.Combine("b.jpg"));

        var result = scope.Indexer.IndexRoot(root); // it used to end here: File.OpenRead threw for the gone file

        Assert.Equal(["a.jpg", "c.jpg"], scope.Items.GetForRoot(root.Id).Select(i => i.FileName).Order());
        Assert.Equal(2, result.Added);
    }

    [Fact]
    public void AFileGoneMidRead_LosesItsRow_AndTheRunGoesOn()
    {
        // Round 3 (dispositions lens), N-5: the file vanishes as it's read (a read that throws), not before.
        using var scope = new ApertureScope();
        foreach (var name in new[] { "a.jpg", "b.jpg" })
            TestImages.Write(scope.Library.Combine(name), 32, 24);
        var root = scope.AddLibraryRoot();
        scope.Indexer.IndexRoot(root);
        var b = scope.Library.Combine("b.jpg");
        File.SetLastWriteTimeUtc(b, DateTime.UtcNow.AddMinutes(-3)); // to be read again
        scope.Indexer.Reading = rel =>
        {
            if (rel == "b.jpg")
                File.Delete(b); // after the run found it there, before its read
        };

        var result = scope.Indexer.IndexRoot(root);
        scope.Indexer.Reading = null;

        Assert.Equal(1, result.Removed);
        AssertTheRowsAreTheFiles(scope, root);
    }

    [Fact]
    public void AVideoGoneBeforeItsRead_GetsNoRow()
    {
        // A video's reads don't throw for a missing file (the shell's fallback answers), so the run checks first.
        using var scope = new ApertureScope();
        TestImages.Write(scope.Library.Combine("a.jpg"), 32, 24);
        File.WriteAllText(scope.Library.Combine("v.mp4"), "a video");
        var root = scope.AddLibraryRoot();
        scope.Indexer.AfterScan = () => File.Delete(scope.Library.Combine("v.mp4"));

        scope.Indexer.IndexRoot(root);

        Assert.Equal(["a.jpg"], scope.Items.GetForRoot(root.Id).Select(i => i.FileName));
    }

    [Fact]
    public void AFileLockedAsItsRead_KeepsItsRow_AndTheRunGoesOn()
    {
        using var scope = new ApertureScope();
        foreach (var name in new[] { "a.jpg", "b.jpg" })
            TestImages.Write(scope.Library.Combine(name), 32, 24);
        var root = scope.AddLibraryRoot();
        scope.Indexer.IndexRoot(root);
        var b = scope.Library.Combine("b.jpg");
        File.SetLastWriteTimeUtc(b, File.GetLastWriteTimeUtc(b).AddMinutes(1)); // changed: to be read again
        TestImages.Write(scope.Library.Combine("c.jpg"), 32, 24);

        IndexResult result;
        using (new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.None))
            result = scope.Indexer.IndexRoot(root);

        Assert.Equal(["a.jpg", "b.jpg", "c.jpg"], scope.Items.GetForRoot(root.Id).Select(i => i.FileName).Order());
        Assert.Equal((1, 1), (result.Added, result.Failed)); // c added; b kept as it was, its read failed
    }

    [Fact]
    public void AFileMovedInAfterItsFolderWasCounted_KeepsItsRow()
    {
        // R2-4, destination side: the move's row is in the run's rows, but the file wasn't on disk when it counted.
        using var scope = new ApertureScope();
        var tags = new Storage.AnnotationStore(scope.Database);
        TestImages.Write(scope.Library.Combine("A", "a.jpg"), 32, 24);
        TestImages.Write(scope.Library.Combine("B", "b.jpg"), 32, 24);
        var rootA = scope.Roots.Add(new Models.Root { Path = scope.Library.Combine("A"), Alias = "A", AddedUtc = DateTime.UtcNow });
        var rootB = scope.Roots.Add(new Models.Root { Path = scope.Library.Combine("B"), Alias = "B", AddedUtc = DateTime.UtcNow });
        scope.Indexer.IndexRoot(rootA);
        scope.Indexer.IndexRoot(rootB);
        var moves = new FileMoves(scope.Database);
        scope.Indexer.AfterScan = () =>
        {
            scope.Indexer.AfterScan = null;
            Assert.Equal(MoveOutcome.Moved,
                moves.Move(scope.Library.Combine("B", "b.jpg"), scope.Library.Combine("A"), default, new ItemMove(rootB.Id, "b.jpg", rootA.Id, "b.jpg")).Outcome);
        };

        var result = scope.Indexer.IndexRoot(rootA);

        Assert.Equal(0, result.Removed);
        Assert.Equal(["a.jpg", "b.jpg"], scope.Items.GetForRoot(rootA.Id).Select(i => i.FileName).Order());
    }

    [Fact]
    public void AFileMovedOutAfterItsFolderWasCounted_EndsNoRun_AndKeepsItsNewRow()
    {
        // R2-4, source side: the file was counted, its row moved away with it; the run doesn't read a gone file.
        using var scope = new ApertureScope();
        TestImages.Write(scope.Library.Combine("A", "a.jpg"), 32, 24);
        Directory.CreateDirectory(scope.Library.Combine("B"));
        var rootA = scope.Roots.Add(new Models.Root { Path = scope.Library.Combine("A"), Alias = "A", AddedUtc = DateTime.UtcNow });
        var rootB = scope.Roots.Add(new Models.Root { Path = scope.Library.Combine("B"), Alias = "B", AddedUtc = DateTime.UtcNow });
        scope.Indexer.IndexRoot(rootA);
        var moves = new FileMoves(scope.Database);
        scope.Indexer.AfterScan = () =>
        {
            scope.Indexer.AfterScan = null;
            moves.Move(scope.Library.Combine("A", "a.jpg"), scope.Library.Combine("B"), default, new ItemMove(rootA.Id, "a.jpg", rootB.Id, "a.jpg"));
        };

        scope.Indexer.IndexRoot(rootA); // no exception

        Assert.Empty(scope.Items.GetForRoot(rootA.Id));
        Assert.Equal(["a.jpg"], scope.Items.GetForRoot(rootB.Id).Select(i => i.FileName));
    }

    [Fact]
    public void WhatsOutOfScope_IsStillPruned_ThoughItsOnDisk()
    {
        // The prune's check that a file is gone mustn't keep rows a run leaves out on purpose.
        using var scope = new ApertureScope();
        TestImages.Write(scope.Library.Combine("top.jpg"), 32, 24);
        TestImages.Write(scope.Library.Combine("sub", "deep.jpg"), 32, 24);
        File.WriteAllText(scope.Library.Combine("clip.mp4"), "a video");
        var root = scope.AddLibraryRoot();
        scope.Indexer.IndexRoot(root);
        Assert.Equal(3, scope.Items.GetForRoot(root.Id).Count);

        root.Recursive = false;
        var result = scope.Indexer.IndexRoot(root, includeVideos: false);

        Assert.Equal(["top.jpg"], scope.Items.GetForRoot(root.Id).Select(i => i.FileName));
        Assert.Equal(2, result.Removed);
    }

    // --- Round 3: the rows are the files (review round 3, M-2, M-3, L-1; C0). Each test ends on the same rule, so a
    // fix for one case that breaks another fails here: one row per file a scan finds now, in its case, and a second run
    // changes nothing.

    private static void AssertTheRowsAreTheFiles(ApertureScope scope, Root root)
    {
        var files = Indexing.FileScanner.Scan(root.Path, recursive: root.Recursive).Select(f => f.RelPath).Order(StringComparer.Ordinal);
        var rows = scope.Items.GetForRoot(root.Id).Select(i => i.RelPath).Order(StringComparer.Ordinal);
        Assert.Equal(files, rows);
        var again = scope.Indexer.IndexRoot(root);
        Assert.Equal((0, 0, 0), (again.Added, again.Updated, again.Removed));
    }

    [Fact]
    public void ACaseOnlyRename_LeavesOneRow()
    {
        // M-2: the prune's "its file is there after all" found the old name's file, whatever its case: two rows for good.
        using var scope = new ApertureScope();
        TestImages.Write(scope.Library.Combine("IMG_1.JPG"), 32, 24);
        var root = scope.AddLibraryRoot();
        scope.Indexer.IndexRoot(root);

        File.Move(scope.Library.Combine("IMG_1.JPG"), scope.Library.Combine("img_1.jpg"));
        scope.Indexer.IndexRoot(root);

        Assert.Equal(["img_1.jpg"], scope.Items.GetForRoot(root.Id).Select(i => i.RelPath));
        AssertTheRowsAreTheFiles(scope, root);
    }

    [Fact]
    public void AFolderRenamedOnlyInCase_LeavesOneRowEach()
    {
        using var scope = new ApertureScope();
        TestImages.Write(scope.Library.Combine("trips", "a.jpg"), 32, 24);
        TestImages.Write(scope.Library.Combine("trips", "b.jpg"), 32, 24);
        var root = scope.AddLibraryRoot();
        scope.Indexer.IndexRoot(root);

        Directory.Move(scope.Library.Combine("trips"), scope.Library.Combine("trips-"));
        Directory.Move(scope.Library.Combine("trips-"), scope.Library.Combine("Trips"));
        scope.Indexer.IndexRoot(root);

        AssertTheRowsAreTheFiles(scope, root);
    }

    // --- Round 4 (F1, F2): a row goes only when its file's absence can be told. A folder deleted or renamed on a drive
    // that's there goes, as in 0.9.1; one whose drive or share is gone, or reached through a link to one, keeps its rows
    // for when it's back. (Round 3 kept them for any folder missing, deleted ones too, and pruned them behind a link.)

    // A folder on a drive that can be unplugged and plugged back in: a junction to a folder moved away and back. Away,
    // the junction is what Windows shows for a link to a drive that's gone: it's there, and it won't list.
    private sealed class Drive
    {
        private readonly string _target;

        public Drive(string link, string target)
        {
            Directory.CreateDirectory(target);
            Junction.Create(link, target);
            (Link, _target) = (link, target);
        }

        public string Link { get; }

        public string Combine(string name) => Path.Combine(Link, name);

        public void Unplug() => Directory.Move(_target, _target + "-unplugged");

        public void PlugBackIn() => Directory.Move(_target + "-unplugged", _target);
    }

    private static Root AddRoot(ApertureScope scope, string path) =>
        scope.Roots.Add(new Root { Path = path, Alias = Path.GetFileName(path), AddedUtc = DateTime.UtcNow });

    [Fact]
    public void AFolderWhoseDriveGoesAwayMidRun_KeepsItsRows()
    {
        // M-3 (round 3), and F2: the drive goes as the run reads; behind a link it said the folder was there, and the
        // files it hadn't read yet were "gone".
        using var scope = new ApertureScope();
        var drive = new Drive(scope.Library.Combine("Photos"), scope.Library.Combine("drive"));
        foreach (var name in new[] { "a.jpg", "b.jpg", "c.jpg" })
            TestImages.Write(drive.Combine(name), 32, 24);
        var root = AddRoot(scope, drive.Link);
        scope.Indexer.IndexRoot(root);
        foreach (var name in new[] { "a.jpg", "b.jpg", "c.jpg" }) // all changed: each to be read again
            File.SetLastWriteTimeUtc(drive.Combine(name), DateTime.UtcNow.AddMinutes(-3));
        scope.Indexer.Reading = _ =>
        {
            scope.Indexer.Reading = null;
            drive.Unplug(); // as the first is read
        };

        var result = scope.Indexer.IndexRoot(root);

        Assert.Equal(0, result.Removed);
        Assert.Equal(3, scope.Items.GetForRoot(root.Id).Count);
        drive.PlugBackIn();
        scope.Indexer.IndexRoot(root);
        AssertTheRowsAreTheFiles(scope, root);
    }

    [Fact]
    public void AFolderWhoseDriveIsGoneWhenItsRunStarts_KeepsItsRows()
    {
        // C0 (from before 0.9.2), and F2: the count came back empty and the prune took every row.
        using var scope = new ApertureScope();
        var drive = new Drive(scope.Library.Combine("USB"), scope.Library.Combine("drive"));
        foreach (var name in new[] { "a.jpg", "b.jpg" })
            TestImages.Write(drive.Combine(name), 32, 24);
        var root = AddRoot(scope, drive.Link);
        scope.Indexer.IndexRoot(root);
        var ids = scope.Items.GetForRoot(root.Id).Select(i => i.Id).Order().ToList();
        drive.Unplug();

        var result = scope.Indexer.IndexRoot(root);

        Assert.Equal(0, result.Removed);
        Assert.Equal(ids, scope.Items.GetForRoot(root.Id).Select(i => i.Id).Order());
        drive.PlugBackIn();
        AssertTheRowsAreTheFiles(scope, root);
    }

    // A drive letter (subst) for a folder, as a USB drive has: unplugged, the letter itself is gone, so nothing above
    // the folder is there to list. Only its own letter is taken away again on dispose (round 5, N-4: another suite on
    // the same PC may have taken the letter since).
    private sealed class LetterDrive : IDisposable
    {
        private readonly string _target, _letter;
        private bool _ours;

        public LetterDrive(string target, string folder)
        {
            Directory.CreateDirectory(Path.Combine(target, folder));
            (_target, _letter) = (target, Subst.CreateOnAFreeLetter(target));
            _ours = true;
            Link = _letter + @"\" + folder;
        }

        public string Link { get; }

        public string Combine(string name) => Path.Combine(Link, name);

        public void Unplug()
        {
            Subst.Remove(_letter);
            _ours = false;
        }

        public void PlugBackIn()
        {
            Subst.Create(_letter, _target);
            _ours = true;
        }

        public void Dispose()
        {
            if (_ours)
                Subst.Remove(_letter);
        }
    }

    [Fact]
    public void AFolderOnADriveLetterThatsGone_KeepsItsRows()
    {
        using var scope = new ApertureScope();
        using var drive = new LetterDrive(scope.Library.Combine("usb-drive"), "Photos");
        foreach (var name in new[] { "a.jpg", "b.jpg" })
            TestImages.Write(drive.Combine(name), 32, 24);
        var root = AddRoot(scope, drive.Link);
        scope.Indexer.IndexRoot(root);
        Assert.Equal(2, scope.Items.GetForRoot(root.Id).Count);
        drive.Unplug();

        var result = scope.Indexer.IndexRoot(root);

        Assert.Equal(0, result.Removed);
        Assert.Equal(2, scope.Items.GetForRoot(root.Id).Count);
        drive.PlugBackIn();
        AssertTheRowsAreTheFiles(scope, root);
    }

    [Fact]
    public void ASubfolderWhoseDriveIsGone_KeepsItsRows()
    {
        // F2, one level down: a folder of the root that's a link to a drive, unplugged.
        using var scope = new ApertureScope();
        TestImages.Write(scope.Library.Combine("Photos", "a.jpg"), 32, 24);
        var camera = new Drive(scope.Library.Combine("Photos", "Camera"), scope.Library.Combine("camera-drive"));
        foreach (var name in new[] { "b.jpg", "c.jpg" })
            TestImages.Write(camera.Combine(name), 32, 24);
        var root = AddRoot(scope, scope.Library.Combine("Photos"));
        scope.Indexer.IndexRoot(root);
        Assert.Equal(3, scope.Items.GetForRoot(root.Id).Count);
        camera.Unplug();

        var result = scope.Indexer.IndexRoot(root);

        Assert.Equal(0, result.Removed);
        Assert.Equal(3, scope.Items.GetForRoot(root.Id).Count);
        camera.PlugBackIn();
        AssertTheRowsAreTheFiles(scope, root);
    }

    [Fact]
    public void ARootDeletedOnADriveThatsThere_LosesItsRows()
    {
        // F1: deleted or renamed in Explorer, it kept tiles for files that were gone, for good, and nothing said so.
        using var scope = new ApertureScope();
        foreach (var name in new[] { "a.jpg", Path.Combine("2019", "b.jpg"), Path.Combine("2019", "c.jpg"), Path.Combine("2020", "d.jpg") })
            TestImages.Write(Path.Combine(scope.Library.Combine("Photos"), name), 32, 24);
        var root = AddRoot(scope, scope.Library.Combine("Photos"));
        scope.Indexer.IndexRoot(root);
        Directory.Move(scope.Library.Combine("Photos"), scope.Library.Combine("Photos, renamed"));

        var result = scope.Indexer.IndexRoot(root);

        Assert.Equal(4, result.Removed);
        Assert.Empty(scope.Items.GetForRoot(root.Id));
    }

    [Fact]
    public void ASubfolderDeleted_LosesItsRows()
    {
        using var scope = new ApertureScope();
        TestImages.Write(scope.Library.Combine("a.jpg"), 32, 24);
        foreach (var name in new[] { "b.jpg", "c.jpg" })
            TestImages.Write(scope.Library.Combine("trip", name), 32, 24);
        var root = scope.AddLibraryRoot();
        scope.Indexer.IndexRoot(root);
        Directory.Delete(scope.Library.Combine("trip"), recursive: true);

        var result = scope.Indexer.IndexRoot(root);

        Assert.Equal(2, result.Removed);
        AssertTheRowsAreTheFiles(scope, root);
    }

    [Theory]
    [InlineData(false, "a.jpg")] // a link to the drive; the drive goes as the photo before it is read
    [InlineData(false, "v.mp4")] // ... as the video itself is read (round 5, L-2)
    [InlineData(true, "a.jpg")] // the drive letter itself goes (round 5, L-1: round 3's check missed only this)
    [InlineData(true, "v.mp4")]
    public void AVideoWhoseDriveGoesAwayMidRun_KeepsItsRowAsItWas(bool itsLetter, string goesAsItsRead)
    {
        // F4 / N-2: a video's read doesn't throw for a missing file (the shell's fallback answers), so a video due a
        // read after its drive went was read anyway: its row took the new time and lost its size, for good. Round 5,
        // L-2: the same if the drive went as the video's own read began.
        using var scope = new ApertureScope();
        using var letter = itsLetter ? new LetterDrive(scope.Library.Combine("camera-card"), "Camera") : null;
        var link = itsLetter ? null : new Drive(scope.Library.Combine("Camera"), scope.Library.Combine("drive"));
        Action unplug = itsLetter ? letter!.Unplug : link!.Unplug, plugBackIn = itsLetter ? letter!.PlugBackIn : link!.PlugBackIn;
        string In(string name) => itsLetter ? letter!.Combine(name) : link!.Combine(name);
        TestImages.Write(In("a.jpg"), 32, 24);
        File.WriteAllText(In("v.mp4"), "a video");
        var root = AddRoot(scope, itsLetter ? letter!.Link : link!.Link);
        scope.Indexer.IndexRoot(root);
        var video = scope.Items.GetForRoot(root.Id).Single(i => i.IsVideo);
        using (var connection = scope.Database.OpenMetadata())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE items SET width = 512, height = 288 WHERE id = @id;"; // a real video's size
            command.Parameters.AddWithValue("@id", video.Id);
            command.ExecuteNonQuery();
        }
        foreach (var name in new[] { "a.jpg", "v.mp4" }) // both changed: each to be read again
            File.SetLastWriteTimeUtc(In(name), DateTime.UtcNow.AddMinutes(-3));
        scope.Indexer.Reading = rel =>
        {
            if (rel != goesAsItsRead)
                return;
            scope.Indexer.Reading = null;
            unplug();
        };

        var result = scope.Indexer.IndexRoot(root);

        var kept = scope.Items.GetForRoot(root.Id).Single(i => i.IsVideo);
        Assert.Equal((video.MTimeUtc, 512, 288), (kept.MTimeUtc, kept.Width ?? 0, kept.Height ?? 0)); // as it was
        Assert.Equal(0, result.Removed);
        plugBackIn();
        scope.Indexer.IndexRoot(root);
        AssertTheRowsAreTheFiles(scope, root);
    }

    [Fact]
    public void ACaseOnlyRename_OfAFileSkippedAsOnlineOnly_KeepsItsRow()
    {
        // F3: the any-case check counted a file skipped under Skip as indexed, and its only row (under the old name)
        // went: the file left the view, against C2. The row stays until a run reads the file under its new name.
        using var scope = new ApertureScope();
        TestImages.Write(scope.Library.Combine("local.jpg"), 32, 24);
        TestImages.Write(scope.Library.Combine("CLOUD.JPG"), 32, 24);
        var root = scope.AddLibraryRoot();
        scope.Indexer.IndexRoot(root);
        var cloud = scope.Library.Combine("cloud.jpg");
        File.Move(scope.Library.Combine("CLOUD.JPG"), cloud);
        File.SetAttributes(cloud, File.GetAttributes(cloud) | FileAttributes.Offline);
        scope.Indexer.RescanAfter = TimeSpan.FromMinutes(5); // answered at once: no second count

        var result = scope.Indexer.IndexRoot(root, null, default, true, (_, _) => Indexing.OnlineOnlyChoice.Skip);

        Assert.Equal(0, result.Removed);
        Assert.Equal(["CLOUD.JPG", "local.jpg"], scope.Items.GetForRoot(root.Id).Select(i => i.RelPath).Order(StringComparer.Ordinal));
        scope.Indexer.IndexRoot(root, null, default, true, (_, _) => Indexing.OnlineOnlyChoice.Now);
        AssertTheRowsAreTheFiles(scope, root);
    }

    private sealed class Heard(Action<IndexProgress> heard) : IProgress<IndexProgress>
    {
        public void Report(IndexProgress value) => heard(value);
    }

    [Fact]
    public void ACaseOnlyRename_ThenARunStoppedAtItsPrune_TheNextRunLeavesOneRow()
    {
        // Round 5, L-3: the new name's row was saved, the old one's not yet pruned (a pause, Stop, switch or exit). The
        // next run finds the new name unchanged: kept as it is, it still counts as indexed, so the old name's row goes.
        using var scope = new ApertureScope();
        TestImages.Write(scope.Library.Combine("IMG_1.JPG"), 32, 24);
        var root = scope.AddLibraryRoot();
        scope.Indexer.IndexRoot(root);
        File.Move(scope.Library.Combine("IMG_1.JPG"), scope.Library.Combine("img_1.jpg"));
        using var stop = new CancellationTokenSource();

        Assert.ThrowsAny<OperationCanceledException>(() => scope.Indexer.IndexRoot(
            root, new Heard(p => { if (p.Phase == IndexPhase.Pruning) stop.Cancel(); }), stop.Token));
        Assert.Equal(["IMG_1.JPG", "img_1.jpg"], scope.Items.GetForRoot(root.Id).Select(i => i.RelPath).Order(StringComparer.Ordinal));

        Assert.Equal(1, scope.Indexer.IndexRoot(root).Removed);
        AssertTheRowsAreTheFiles(scope, root);
    }

    [Fact]
    public void ACaseOnlyRename_OfAFileLockedAsItsRead_KeepsItsRow()
    {
        // F3: the same for a file whose read failed (OOR-1: one still there keeps its row).
        using var scope = new ApertureScope();
        TestImages.Write(scope.Library.Combine("IMG_1.JPG"), 32, 24);
        var root = scope.AddLibraryRoot();
        scope.Indexer.IndexRoot(root);
        var renamed = scope.Library.Combine("img_1.jpg");
        File.Move(scope.Library.Combine("IMG_1.JPG"), renamed);

        IndexResult result;
        using (new FileStream(renamed, FileMode.Open, FileAccess.Read, FileShare.None))
            result = scope.Indexer.IndexRoot(root);

        Assert.Equal((1, 0), (result.Failed, result.Removed));
        Assert.Equal(["IMG_1.JPG"], scope.Items.GetForRoot(root.Id).Select(i => i.RelPath));
        scope.Indexer.IndexRoot(root);
        AssertTheRowsAreTheFiles(scope, root);
    }

    [Fact]
    public void AMoveLandingDuringTheRun_KeepsTheRowItTook()
    {
        // L-1: the run's rows were read before the move; the file it was to read was gone, and the prune deleted its row
        // by id — the row the move had just taken to the other folder. The window is the online-only question: the run
        // has counted and read its rows, and holds no write yet (inside a batch, the move's row write waits instead).
        using var scope = new ApertureScope();
        TestImages.Write(scope.Library.Combine("A", "a.jpg"), 32, 24);
        TestImages.Write(scope.Library.Combine("A", "b.jpg"), 32, 24);
        Directory.CreateDirectory(scope.Library.Combine("B"));
        var rootA = scope.Roots.Add(new Root { Path = scope.Library.Combine("A"), Alias = "A", AddedUtc = DateTime.UtcNow });
        var rootB = scope.Roots.Add(new Root { Path = scope.Library.Combine("B"), Alias = "B", AddedUtc = DateTime.UtcNow });
        scope.Indexer.IndexRoot(rootA);
        var b = scope.Library.Combine("A", "b.jpg");
        File.SetLastWriteTimeUtc(b, DateTime.UtcNow.AddMinutes(-3)); // b is to be read again
        var cloud = scope.Library.Combine("A", "c.jpg");
        TestImages.Write(cloud, 32, 24);
        File.SetAttributes(cloud, File.GetAttributes(cloud) | FileAttributes.Offline); // so the run asks
        var moved = scope.Items.GetForRoot(rootA.Id).Single(i => i.FileName == "b.jpg").Id;
        var moves = new FileMoves(scope.Database);
        scope.Indexer.RescanAfter = TimeSpan.FromMinutes(5); // answered at once: no second count

        scope.Indexer.IndexRoot(rootA, null, default, true, (_, _) =>
        {
            Assert.Equal(MoveOutcome.Moved, moves.Move(b, scope.Library.Combine("B"), default,
                new ItemMove(rootA.Id, "b.jpg", rootB.Id, "b.jpg")).Outcome); // while the question waits
            return Indexing.OnlineOnlyChoice.Now;
        });

        Assert.Equal(["a.jpg", "c.jpg"], scope.Items.GetForRoot(rootA.Id).Select(i => i.RelPath).Order());
        Assert.Equal(moved, Assert.Single(scope.Items.GetForRoot(rootB.Id)).Id);
        AssertTheRowsAreTheFiles(scope, rootA);
        Assert.Equal(1, scope.Indexer.IndexRoot(rootB).Updated); // b had changed before it moved: its row is brought up to date
        Assert.Equal(moved, Assert.Single(scope.Items.GetForRoot(rootB.Id)).Id);
        AssertTheRowsAreTheFiles(scope, rootB);
    }
}
