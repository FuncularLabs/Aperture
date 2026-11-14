using Aperture.Core.Indexing;
using Aperture.Core.Tests.Support;
using Look = Aperture.Core.Indexing.Indexer.Absence.Look;

namespace Aperture.Core.Tests;

/// <summary>
/// The prune's rule (review round 4, F1, F2), on its own: a row's file is there; gone, when that can be told (its folder,
/// or the nearest one above it that's there, can be listed); or out of reach (an unplugged drive, a link to one, a folder
/// this user may not list). <see cref="IndexerRaceTests"/> shows the same through a run.
/// </summary>
public class IndexerAbsenceTests
{
    private static Look At(string path) => new Indexer.Absence().At(path);

    [Fact]
    public void AFileThere_IsThere()
    {
        using var dir = new TempDir();
        TestImages.Write(dir.Combine("a.jpg"), 8, 8);

        Assert.Equal(Look.There, At(dir.Combine("a.jpg")));
    }

    [Fact]
    public void AFileMissing_FromAFolderThatLists_IsGone()
    {
        using var dir = new TempDir();
        TestImages.Write(dir.Combine("b.jpg"), 8, 8);

        Assert.Equal(Look.Gone, At(dir.Combine("a.jpg")));
    }

    [Fact]
    public void AFileInAFolderDeleted_OnADriveThatsThere_IsGone()
    {
        using var dir = new TempDir();

        Assert.Equal(Look.Gone, At(dir.Combine("trip", "day 1", "a.jpg"))); // two folders up to one that lists
    }

    [Fact]
    public void AFileWhereItsFolderWas_IsGone()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.Combine("trip"), "a file where the folder was");

        Assert.Equal(Look.Gone, At(dir.Combine("trip", "a.jpg")));
    }

    [Fact]
    public void AFileOnAVolumeThatIsntThere_IsOutOfReach()
    {
        // A drive that isn't there (IndexerRaceTests unplugs a drive letter; a free letter here could be taken by
        // another test run on this PC meanwhile: round 5, F-9).
        Assert.Equal(Look.OutOfReach, At(@"\\?\Volume{00000000-0000-0000-0000-000000000000}\Photos\a.jpg"));
    }

    [Fact]
    public void AFolderThatCantBeLookedAt_ForAReasonOtherThanNotThere_IsOutOfReach()
    {
        // A failed look isn't a verdict (round 5, F-4; taxonomy 13): a share that drops a call for a moment reads as an
        // error, not as "not there", and must not take a subtree's rows. An invalid name stands in for that error here
        // (Windows: ERROR_INVALID_NAME, not "not found"); Directory.Exists and File.Exists say false for both.
        using var dir = new TempDir();
        TestImages.Write(dir.Combine("b.jpg"), 8, 8);

        Assert.Equal(Look.OutOfReach, At(dir.Combine("trip|1", "a.jpg")));
        Assert.Equal(Look.OutOfReach, At(dir.Combine("a|1.jpg")));
    }

    [Fact]
    public void BelowAFolderThatCantBeListed_NothingIsLookedAt()
    {
        // Round 5, F-3 / N-1: behind a link to a share that's gone, each look is a call to a server that doesn't answer;
        // a folder below one that can't be listed takes its answer, as one below a missing folder does. Shown by what
        // the same prune's later answers don't see once the link's target is back.
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Combine("drive"));
        Junction.Create(dir.Combine("USB"), dir.Combine("drive"));
        Directory.Move(dir.Combine("drive"), dir.Combine("drive-away"));
        var absence = new Indexer.Absence();
        Assert.Equal(Look.OutOfReach, absence.At(dir.Combine("USB", "a.jpg")));

        Directory.Move(dir.Combine("drive-away"), dir.Combine("drive"));
        TestImages.Write(dir.Combine("drive", "b.jpg"), 8, 8);
        TestImages.Write(dir.Combine("drive", "trip", "c.jpg"), 8, 8);

        Assert.Equal(Look.OutOfReach, absence.At(dir.Combine("USB", "b.jpg"))); // not looked for
        Assert.Equal(Look.OutOfReach, absence.At(dir.Combine("USB", "trip", "c.jpg"))); // nor anything below
        Assert.Equal(Look.There, new Indexer.Absence().At(dir.Combine("USB", "trip", "c.jpg"))); // the next prune looks
    }

    [Fact]
    public void BelowAFolderThatLists_EachFolderIsLookedAt()
    {
        using var dir = new TempDir();
        TestImages.Write(dir.Combine("b.jpg"), 8, 8);
        var absence = new Indexer.Absence();
        Assert.Equal(Look.Gone, absence.At(dir.Combine("a.jpg")));

        TestImages.Write(dir.Combine("trip", "c.jpg"), 8, 8);

        Assert.Equal(Look.There, absence.At(dir.Combine("trip", "c.jpg")));
        Assert.Equal(Look.Gone, absence.At(dir.Combine("trip", "d.jpg")));
    }

    [Fact]
    public void AFileBehindALinkToAFolderThatsGone_IsOutOfReach()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Combine("drive"));
        Junction.Create(dir.Combine("USB"), dir.Combine("drive"));
        Directory.Delete(dir.Combine("drive"));

        Assert.True(Directory.Exists(dir.Combine("USB"))); // the premise: it says it's there
        Assert.Equal(Look.OutOfReach, At(dir.Combine("USB", "a.jpg")));
        Assert.Equal(Look.OutOfReach, At(dir.Combine("USB", "trip", "a.jpg")));
    }

    [Fact]
    public void InAFolderThatCantBeListed_AFile_IsOutOfReach_ThereOrNot()
    {
        // Can't tell what's in it, so its files aren't looked for one by one (round 5, N-1: behind a link to a share
        // that's gone, each look is a call to a server that doesn't answer). A scan couldn't have read them either.
        using var dir = new TempDir();
        TestImages.Write(dir.Combine("locked", "a.jpg"), 8, 8);
        Acl.DenyListing(dir.Combine("locked"));
        try
        {
            Assert.Equal(Look.OutOfReach, At(dir.Combine("locked", "a.jpg")));
            Assert.Equal(Look.OutOfReach, At(dir.Combine("locked", "b.jpg")));
        }
        finally
        {
            Acl.RestoreListing(dir.Combine("locked"));
        }
    }

    [Fact]
    public void AFolderFoundMissing_IsntLookedAtAgain_NorAnythingBelowIt()
    {
        // One look per folder per prune, and none below a folder found missing: a share that's gone is looked for once,
        // not once per folder or file of it (each look at a server that doesn't answer can take seconds). Shown by what
        // the same prune's later answers don't see.
        using var dir = new TempDir();
        var absence = new Indexer.Absence();
        var file = dir.Combine("trip", "a.jpg");
        var deeper = dir.Combine("trip", "day 1", "b.jpg");
        Assert.Equal(Look.Gone, absence.At(file));

        TestImages.Write(file, 8, 8);
        TestImages.Write(deeper, 8, 8);

        Assert.Equal(Look.Gone, absence.At(file)); // its folder isn't looked at again, nor the file in it
        Assert.Equal(Look.Gone, absence.At(deeper)); // nor a folder below it
        Assert.Equal(Look.There, new Indexer.Absence().At(deeper)); // the next prune looks again
    }
}
