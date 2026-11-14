using Aperture.Core.Indexing;
using Aperture.Core.Tests.Support;
using Look = Aperture.Core.Indexing.Indexer.Absence.Look;

namespace Aperture.Core.Tests;

/// <summary>
/// The prune's rule (I2), on its own: a row's file is there; gone, when that can be told (its folder, or the nearest one
/// above it that's there, can be listed; a link's target decides for the link); or out of reach (an unplugged drive, a
/// link to one, a folder this user may not list, a look that failed). <see cref="IndexerRaceTests"/> shows the same
/// through a run.
/// </summary>
/// <remarks>
/// Round 6 redesign (the third round whose findings came from the last round's fixes): every case the rule must tell
/// apart is in one table, and each is asked alone, then all in one prune in order and in reverse, so an answer can't hang
/// on what the prune asked first (round 6, L-2) and a fix for one case that breaks another fails here. What a prune looks
/// at is counted (<c>Looks</c>), so what it costs is a test, not a claim.
/// </remarks>
public class IndexerAbsenceTests
{
    [Fact]
    public void EachCase_GetsItsAnswer_AskedAlone_InOrder_AndInReverse()
    {
        using var dir = new TempDir();
        string W(params string[] parts) => dir.Combine(parts);
        TestImages.Write(W("lists", "there.jpg"), 8, 8);
        Directory.CreateDirectory(W("lists", "afolder.jpg"));
        File.WriteAllText(W("lists", "afile"), "a file where a folder was");
        Directory.CreateDirectory(W("elsewhere", "Trip"));
        Junction.Create(W("lists", "todeleted"), W("elsewhere", "Trip"));
        Directory.Delete(W("elsewhere", "Trip"));
        Directory.CreateDirectory(W("card", "Photos"));
        var letter = Subst.CreateOnAFreeLetter(W("card"));
        try
        {
            Junction.Create(W("lists", "tocard"), letter + @"\Photos");
        }
        finally
        {
            Subst.Remove(letter);
        }
        TestImages.Write(W("locked", "x.jpg"), 8, 8);
        TestImages.Write(W("locked", "open", "there.jpg"), 8, 8);
        Directory.CreateDirectory(W("locked", "noattrs"));
        Acl.DenyReadAttributes(W("locked", "noattrs"));
        Acl.DenyListing(W("locked"));
        try
        {
            (string Path, Look Answer, string Case)[] cases =
            [
                (W("lists", "there.jpg"), Look.There, "a file there"),
                (W("lists", "gone.jpg"), Look.Gone, "a file gone from a folder that lists"),
                (W("lists", "afolder.jpg"), Look.Gone, "a folder where the file was (round 6, L-1)"),
                (W("lists", "deleted", "deeper", "a.jpg"), Look.Gone, "a file in a folder deleted, two down"),
                (W("lists", "afile", "a.jpg"), Look.Gone, "a file where its folder was"),
                (W("lists", "todeleted", "a.jpg"), Look.Gone, "a link to a folder deleted on a drive that's there (L-4)"),
                (W("lists", "todeleted", "sub", "b.jpg"), Look.Gone, "below such a link"),
                (W("lists", "tocard", "a.jpg"), Look.OutOfReach, "a link to a drive that's gone"),
                (W("lists", "tocard", "sub", "b.jpg"), Look.OutOfReach, "below a link to a drive that's gone"),
                (W("locked", "x.jpg"), Look.OutOfReach, "a file there, in a folder this user may not list"),
                (W("locked", "gone.jpg"), Look.OutOfReach, "a file gone, in a folder this user may not list"),
                (W("locked", "open", "there.jpg"), Look.There, "a file there, below a folder that can't be listed (L-2)"),
                (W("locked", "open", "gone.jpg"), Look.Gone, "a file gone, in a folder that lists below one that doesn't (L-2)"),
                (W("locked", "noattrs", "a.jpg"), Look.OutOfReach, "a folder whose look is refused (access denied)"),
                (W("lists", "bad|name", "a.jpg"), Look.OutOfReach, "a folder whose look failed (round 5, F-4)"),
                (W("lists", "bad|1.jpg"), Look.OutOfReach, "a file whose look failed"),
                (@"\\?\Volume{00000000-0000-0000-0000-000000000000}\Photos\a.jpg", Look.OutOfReach, "a volume that isn't there"),
            ];
            var wrong = new List<string>();
            foreach (var (path, answer, what) in cases)
            {
                if (new Indexer.Absence().At(path) is var alone && alone != answer)
                    wrong.Add($"alone: {what}: {alone}, not {answer}");
            }
            foreach (var (order, name) in new[] { (cases, "in order"), (cases.Reverse().ToArray(), "in reverse") })
            {
                var absence = new Indexer.Absence();
                foreach (var (path, answer, what) in order)
                {
                    if (absence.At(path) is var told && told != answer)
                        wrong.Add($"{name}: {what}: {told}, not {answer}");
                }
            }

            Assert.True(wrong.Count == 0, string.Join("\n", wrong));
        }
        finally
        {
            Acl.RestoreListing(W("locked"));
            Acl.RestoreReadAttributes(W("locked", "noattrs"));
        }
    }

    // --- What a prune looks at (round 4's design, round 6, N6-2): each folder once; nothing below one missing or whose
    // look failed; no file in a folder that can't be listed.

    [Fact]
    public void ADriveThatsGone_IsLookedForOnce_NotOncePerFolderOrFile()
    {
        var absence = new Indexer.Absence();
        const string Gone = @"\\?\Volume{00000000-0000-0000-0000-000000000000}\Photos";
        Assert.Equal(Look.OutOfReach, absence.At(Gone + @"\2019\a.jpg"));
        var first = absence.Looks;

        foreach (var name in new[] { @"\2019\b.jpg", @"\2020\c.jpg", @"\2021\May\d.jpg" })
            Assert.Equal(Look.OutOfReach, absence.At(Gone + name));

        Assert.Equal(first, absence.Looks);
    }

    [Fact]
    public void AFailedLook_AnswersForTheFoldersBesideAndBelowIt()
    {
        // N6-2: a failed look climbs, as a missing folder's does; the folders beside it are then not looked at, each a
        // call to a server that doesn't answer.
        using var dir = new TempDir();
        var absence = new Indexer.Absence();
        Assert.Equal(Look.OutOfReach, absence.At(dir.Combine("share|gone", "2019", "a.jpg")));
        var first = absence.Looks;

        Assert.Equal(Look.OutOfReach, absence.At(dir.Combine("share|gone", "2020", "b.jpg")));
        Assert.Equal(Look.OutOfReach, absence.At(dir.Combine("share|gone", "2019", "May", "c.jpg")));

        Assert.Equal(first, absence.Looks);
    }

    [Fact]
    public void AFolderFoundMissing_IsntLookedAtAgain_NorAnythingBelowIt()
    {
        // Shown by what the same prune's later answers don't see, and by its count of looks.
        using var dir = new TempDir();
        var absence = new Indexer.Absence();
        var file = dir.Combine("trip", "a.jpg");
        var deeper = dir.Combine("trip", "day 1", "b.jpg");
        Assert.Equal(Look.Gone, absence.At(file));
        var first = absence.Looks;

        TestImages.Write(file, 8, 8);
        TestImages.Write(deeper, 8, 8);

        Assert.Equal(Look.Gone, absence.At(file)); // its folder isn't looked at again, nor the file in it
        Assert.Equal(Look.Gone, absence.At(deeper)); // nor a folder below it
        Assert.Equal(first, absence.Looks);
        Assert.Equal(Look.There, new Indexer.Absence().At(deeper)); // the next prune looks again
    }

    [Fact]
    public void InALinkThatWontList_NoFileIsLookedFor_ButEachFolderBelowIs()
    {
        // Round 5, N-1: no file in a folder that can't be listed is looked for. Round 6, L-2: a folder below it is looked
        // at itself (it may list).
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Combine("elsewhere", "Trip"));
        Junction.Create(dir.Combine("Trip"), dir.Combine("elsewhere", "Trip"));
        Directory.Delete(dir.Combine("elsewhere", "Trip"));
        var absence = new Indexer.Absence();
        Assert.Equal(Look.Gone, absence.At(dir.Combine("Trip", "a.jpg"))); // its target was deleted (L-4)

        TestImages.Write(dir.Combine("elsewhere", "Trip", "b.jpg"), 8, 8);
        TestImages.Write(dir.Combine("elsewhere", "Trip", "day 1", "c.jpg"), 8, 8);
        var looks = absence.Looks;

        Assert.Equal(Look.Gone, absence.At(dir.Combine("Trip", "b.jpg"))); // not looked for
        Assert.Equal(looks, absence.Looks);
        Assert.Equal(Look.There, absence.At(dir.Combine("Trip", "day 1", "c.jpg"))); // looked at
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
}
