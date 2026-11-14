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
        TestImages.Write(W("refused", "open", "there.jpg"), 8, 8);
        Directory.CreateDirectory(W("share"));
        Junction.Create(W("lists", "toshare"), W("share"));
        Directory.Delete(W("share"));
        Junction.Create(W("lists", "loop"), W("x", "y")); // round and round: loop → x\y, x → loop\z
        Junction.Create(W("lists", "toloop"), W("lists", "loop")); // made before the loop closes (after, mklink can't)
        Junction.Create(W("x"), W("lists", "loop", "z"));
        File.WriteAllText(W("afile2"), "a file a link points at");
        Junction.Create(W("lists", "tofile"), W("afile2")); // round 7, R7-2
        Junction.Create(W("lists", "tolink"), W("lists", "tofile")); // round 8, F-2: a link to a link to a file
        Junction.Create(W("lists", "tolocked"), W("locked")); // round 8, F-1: a link to a folder this user may not list
        File.WriteAllText(W("lists", "placeholder"), "read as a reparse point that isn't a link: a cloud placeholder");
        File.WriteAllText(W("placeholder2"), "the same, a link's target");
        Junction.Create(W("lists", "toplaceholder"), W("placeholder2")); // round 9, R9-1
        Directory.CreateDirectory(W("elsewhere", "Gone2"));
        Junction.Create(W("locked", "linkgone"), W("elsewhere", "Gone2")); // round 9, R9-2: a link inside a folder that won't list
        Directory.Delete(W("elsewhere", "Gone2"));
        File.WriteAllText(W("filelink"), "read as a file-type symbolic link, to a folder deleted");
        Junction.Create(W("lists", "tofilelink"), W("filelink")); // round 8, R's observation; round 9, N9-2
        File.WriteAllText(W("lists", "afilelink"), "read as a file-type symbolic link, to a volume that isn't there");
        Directory.CreateDirectory(W("elsewhere", "GoneR"));
        Junction.Create(W("lists", "raced"), W("elsewhere", "GoneR")); // its resolutions made to fail: round 9, N9-3
        Junction.Create(W("lists", "raced2"), W("elsewhere", "GoneR"));
        Directory.Delete(W("elsewhere", "GoneR"));
        File.WriteAllText(W("locked", "Trip"), "a file where a folder was, in a folder that can't be listed");
        Directory.CreateDirectory(W("elsewhere", "Gone"));
        Directory.CreateDirectory(W("chain"));
        for (var n = 1; n <= 10; n++) // links to links, ten deep, to a folder deleted (round 7, R7-4)
            Junction.Create(W("chain", $"j{n}"), n < 10 ? W("chain", $"j{n + 1}", "x") : W("elsewhere", "Gone"));
        Directory.Delete(W("elsewhere", "Gone"));
        // Windows answers a folder's attributes from its parent's listing when its own are refused, so a refused look
        // (a share this user has lost rights to) and a dropped call can't be made here with ACLs: the look itself is
        // made to fail as Windows fails it (round 7, F-1).
        Indexer.Absence New() => new()
        {
            IsALink = path => string.Equals(path, W("filelink"), StringComparison.OrdinalIgnoreCase)
                || string.Equals(path, W("lists", "afilelink"), StringComparison.OrdinalIgnoreCase)
                || new FileInfo(path).LinkTarget is not null,
            ResolveLink = (link, final) =>
                string.Equals(link, W("filelink"), StringComparison.OrdinalIgnoreCase) ? W("elsewhere", "GoneF")
                : string.Equals(link, W("lists", "afilelink"), StringComparison.OrdinalIgnoreCase) ? @"\\?\Volume{00000000-0000-0000-0000-000000000000}\Photos"
                : string.Equals(link, W("lists", "raced"), StringComparison.OrdinalIgnoreCase)
                    ? throw new IOException("The specified network name is no longer available.", unchecked((int)0x80070040))
                : string.Equals(link, W("lists", "raced2"), StringComparison.OrdinalIgnoreCase)
                    ? (final ? throw new IOException("The name of the file cannot be resolved by the system.", unchecked((int)0x80070781))
                        : throw new UnauthorizedAccessException($"Access to the path '{link}' is denied."))
                : Directory.ResolveLinkTarget(link, final)?.FullName,
            ReadAttributes = path =>
                string.Equals(path, W("refused"), StringComparison.OrdinalIgnoreCase)
                    ? throw new UnauthorizedAccessException($"Access to the path '{path}' is denied.")
                    : string.Equals(path, W("lists", "refused.jpg"), StringComparison.OrdinalIgnoreCase)
                        ? throw new UnauthorizedAccessException($"Access to the path '{path}' is denied.")
                    : string.Equals(path, W("filelink"), StringComparison.OrdinalIgnoreCase)
                      || string.Equals(path, W("lists", "afilelink"), StringComparison.OrdinalIgnoreCase)
                        ? FileAttributes.Archive | FileAttributes.ReparsePoint // what a file-type symbolic link reads as
                    : string.Equals(path, W("lists", "placeholder"), StringComparison.OrdinalIgnoreCase)
                      || string.Equals(path, W("placeholder2"), StringComparison.OrdinalIgnoreCase)
                        ? FileAttributes.Archive | FileAttributes.ReparsePoint | FileAttributes.Offline // a cloud placeholder
                    : string.Equals(path, W("share"), StringComparison.OrdinalIgnoreCase) || path.StartsWith(W("lists", "flaky"), StringComparison.OrdinalIgnoreCase)
                        ? throw new IOException("The specified network name is no longer available.", unchecked((int)0x80070040))
                        : File.GetAttributes(path),
        };
        try
        {
            Acl.DenyListing(W("locked"));
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
                (W("refused", "a.jpg"), Look.OutOfReach, "a folder whose look is refused (access denied)"),
                (W("refused", "open", "gone.jpg"), Look.Gone, "a file gone, in a folder that lists below one whose look is refused (round 7)"),
                (W("refused", "open", "there.jpg"), Look.There, "a file there, below a folder whose look is refused"),
                (W("lists", "toshare", "a.jpg"), Look.OutOfReach, "a link to a share whose look fails (round 7, F-2)"),
                (W("lists", "flaky", "2019", "a.jpg"), Look.OutOfReach, "a folder of a share that dropped the call"),
                (W("lists", "loop", "a.jpg"), Look.OutOfReach, "a link round in a loop (round 7, F-3)"),
                (W("lists", "tofile", "a.jpg"), Look.Gone, "a link to a file: a folder gone (round 7, R7-2)"),
                (W("lists", "tolink", "a.jpg"), Look.Gone, "a link to a link to a file (round 8, F-2)"),
                (W("lists", "toloop", "a.jpg"), Look.OutOfReach, "a link to a link round in a loop (round 8, F-1)"),
                (W("lists", "tolocked", "x.jpg"), Look.OutOfReach, "a file there, behind a link to a folder this user may not list (F-1)"),
                (W("lists", "tolocked", "gone.jpg"), Look.OutOfReach, "a file gone, behind such a link"),
                (W("lists", "tolocked", "open", "there.jpg"), Look.There, "a file there, in a folder that lists behind such a link (R8-1)"),
                (W("lists", "tolocked", "open", "gone.jpg"), Look.Gone, "a file gone, in a folder that lists behind such a link"),
                (W("lists", "tofilelink", "a.jpg"), Look.Gone, "a link to a file-type link to a folder deleted: followed (round 9, N9-2)"),
                (W("lists", "afilelink", "a.jpg"), Look.OutOfReach, "a file-type link to a volume that isn't there, in a folder's place"),
                (W("lists", "raced", "a.jpg"), Look.OutOfReach, "a link whose both resolutions fail (round 9, N9-3)"),
                (W("lists", "raced2", "a.jpg"), Look.OutOfReach, "a link whose first target is refused"),
                (W("lists", "placeholder", "a.jpg"), Look.Gone, "a reparse point that isn't a link (a cloud placeholder) in a folder's place (round 9, R9-1)"),
                (W("lists", "toplaceholder", "a.jpg"), Look.Gone, "a link to a reparse point that isn't a link"),
                (W("locked", "linkgone", "a.jpg"), Look.Gone, "a link to a folder deleted, inside a folder this user may not list (round 9, R9-2)"),
                (W("lists", "refused.jpg"), Look.OutOfReach, "a file whose look is refused (round 8, F-5)"),
                (W("locked", "Trip", "a.jpg"), Look.Gone, "a file where its folder was, in a folder that can't be listed (R7-3)"),
                (W("chain", "j1", "a.jpg"), Look.Gone, "ten links deep to a folder deleted (R7-4)"),
                (W("chain", "j5", "sub", "b.jpg"), Look.Gone, "five links deep, below"),
                (W("chain", "j10", "a.jpg"), Look.Gone, "the last of the ten"),
                (W("lists", "bad|name", "a.jpg"), Look.OutOfReach, "a folder whose look failed (round 5, F-4)"),
                (W("lists", "bad|1.jpg"), Look.OutOfReach, "a file whose look failed"),
                (@"\\?\Volume{00000000-0000-0000-0000-000000000000}\Photos\a.jpg", Look.OutOfReach, "a volume that isn't there"),
            ];
            var wrong = new List<string>();
            foreach (var (path, answer, what) in cases)
            {
                if (New().At(path) is var alone && alone != answer)
                    wrong.Add($"alone: {what}: {alone}, not {answer}");
            }
            foreach (var (order, name) in new[] { (cases, "in order"), (cases.Reverse().ToArray(), "in reverse") })
            {
                var absence = New();
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
        }
    }

    // --- What a prune looks at (round 4's design, round 6, N6-2): each folder once; nothing below one missing or whose
    // look failed; no file in a folder that can't be listed.

    [Fact]
    public void BehindALinkToADriveThatsGone_NoFolderIsLookedAtTwice()
    {
        // Round 7, R7-5: a link is what its target is; to a drive that's gone, everything behind it is missing, and is
        // answered for every folder below the link without a look.
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Combine("card", "Photos"));
        var letter = Subst.CreateOnAFreeLetter(dir.Combine("card"));
        try
        {
            Junction.Create(dir.Combine("Camera"), letter + @"\Photos");
        }
        finally
        {
            Subst.Remove(letter);
        }
        var absence = new Indexer.Absence();
        Assert.Equal(Look.OutOfReach, absence.At(dir.Combine("Camera", "2019", "a.jpg")));
        var first = absence.Looks;

        foreach (var path in new[] { dir.Combine("Camera", "2020", "b.jpg"), dir.Combine("Camera", "c.jpg"), dir.Combine("Camera", "2021", "May", "d.jpg") })
            Assert.Equal(Look.OutOfReach, absence.At(path));

        Assert.Equal(first, absence.Looks);
    }

    [Theory]
    [InlineData("loop")] // Windows can't follow it to its end: round 8, F-4 (b)
    [InlineData("toshare")] // its target's look fails: F-4 (a)
    public void BehindALinkThatCantTell_NoFolderIsLookedAtTwice(string link)
    {
        using var dir = new TempDir();
        string W(params string[] parts) => dir.Combine(parts);
        Directory.CreateDirectory(W("share"));
        Junction.Create(W("toshare"), W("share"));
        Directory.Delete(W("share"));
        Junction.Create(W("loop"), W("x", "y"));
        Junction.Create(W("x"), W("loop", "z"));
        var absence = new Indexer.Absence
        {
            ReadAttributes = path => string.Equals(path, W("share"), StringComparison.OrdinalIgnoreCase)
                ? throw new IOException("The semaphore timeout period has expired.", unchecked((int)0x80070079))
                : File.GetAttributes(path),
        };
        Assert.Equal(Look.OutOfReach, absence.At(W(link, "2019", "a.jpg")));
        var first = absence.Looks;

        foreach (var path in new[] { W(link, "2020", "b.jpg"), W(link, "c.jpg"), W(link, "2021", "May", "d.jpg") })
            Assert.Equal(Look.OutOfReach, absence.At(path));

        Assert.Equal(first, absence.Looks);
    }

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
    public void BehindALinkToAFolderDeleted_NothingIsLookedAtAgain()
    {
        // Round 5, N-1: no file in a folder that can't be listed is looked for. Round 7, R7-5: a link is what its target
        // is, so behind a link to a folder deleted, every folder below is missing too, unlooked. (A folder this user may
        // not list is another matter: one below it is looked at itself, and may list — the table's "locked\open".)
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Combine("elsewhere", "Trip"));
        Junction.Create(dir.Combine("Trip"), dir.Combine("elsewhere", "Trip"));
        Directory.Delete(dir.Combine("elsewhere", "Trip"));
        var absence = new Indexer.Absence();
        Assert.Equal(Look.Gone, absence.At(dir.Combine("Trip", "a.jpg"))); // its target was deleted (L-4)
        // The link's attributes and listing, its resolution, its target's look, and the target's folder's look and listing.
        Assert.Equal(6, absence.Looks);

        TestImages.Write(dir.Combine("elsewhere", "Trip", "b.jpg"), 8, 8);
        TestImages.Write(dir.Combine("elsewhere", "Trip", "day 1", "c.jpg"), 8, 8);
        var looks = absence.Looks;

        Assert.Equal(Look.Gone, absence.At(dir.Combine("Trip", "b.jpg")));
        Assert.Equal(Look.Gone, absence.At(dir.Combine("Trip", "day 1", "c.jpg")));
        Assert.Equal(looks, absence.Looks);
        Assert.Equal(Look.There, new Indexer.Absence().At(dir.Combine("Trip", "day 1", "c.jpg"))); // the next prune looks
    }

    [Fact]
    public void ALinkWindowsCantFollow_CountsBothResolutions()
    {
        // Round 9, R9-3.
        using var dir = new TempDir();
        File.WriteAllText(dir.Combine("afile"), "a file a link points at");
        Junction.Create(dir.Combine("tofile"), dir.Combine("afile"));
        var absence = new Indexer.Absence();

        Assert.Equal(Look.Gone, absence.At(dir.Combine("tofile", "a.jpg")));
        // The link's attributes and listing, its final resolution (it ends at a file: refused), its first, the file's look.
        Assert.Equal(5, absence.Looks);
    }

    [Fact]
    public void AFolderThatLists_IsLookedAtAndListedOnce_AndEachFileLookedFor()
    {
        // Round 7, F-4: the listing counts too.
        using var dir = new TempDir();
        TestImages.Write(dir.Combine("b.jpg"), 8, 8);
        var absence = new Indexer.Absence();

        Assert.Equal(Look.Gone, absence.At(dir.Combine("a.jpg")));
        Assert.Equal(3, absence.Looks); // the folder's attributes, its listing, the file's attributes
        Assert.Equal(Look.There, absence.At(dir.Combine("b.jpg")));
        Assert.Equal(4, absence.Looks);
    }
}
