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
    public void AFileOnADriveLetterNoDriveHas_IsOutOfReach()
    {
        Assert.Equal(Look.OutOfReach, At(Subst.FreeLetter() + @"\Photos\a.jpg"));
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
    public void InAFolderThatCantBeListed_AFileThere_IsThere_AndOneMissing_IsOutOfReach()
    {
        using var dir = new TempDir();
        TestImages.Write(dir.Combine("locked", "a.jpg"), 8, 8);
        Acl.DenyListing(dir.Combine("locked"));
        try
        {
            Assert.Equal(Look.There, At(dir.Combine("locked", "a.jpg")));
            Assert.Equal(Look.OutOfReach, At(dir.Combine("locked", "b.jpg"))); // can't tell: it may be there
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
