using Aperture.Core.Library;
using Aperture.Core.Models;
using Aperture.Core.Storage;
using Aperture.Core.Tests.Support;
using Microsoft.Data.Sqlite;

namespace Aperture.Core.Tests;

/// <summary>
/// A subfolder's show/hide checkbox (Paul, 2026-10-26): hiding a folder takes it, and every folder inside it, out
/// of the library's union — exactly as unticking a top-level folder does — and the choice is kept with the
/// library. Real indexed files in a real library.
/// </summary>
public class HiddenFoldersTests
{
    private sealed class Fixture : IDisposable
    {
        public TempDir Data { get; } = new();
        public TempDir Photos { get; } = new();
        public LibraryService Service { get; private set; }
        public Root Root { get; }

        public Fixture()
        {
            foreach (var file in new[] { "a.jpg", @"Trip\t1.jpg", @"Trip\t2.jpg", @"Trip\Day 1\d1.jpg", @"Trip 2\x.jpg" })
                TestImages.Write(Path.Combine(Photos.Path, file), 64, 48);
            Service = new LibraryService(Data.Path);
            Root = Service.AddRoot(Photos.Path, "Photos");
            Service.IndexRoot(Root);
        }

        public List<string> Shown() =>
            Service.GetContents().Rows.Select(r => r.Item.RelPath).Order(StringComparer.OrdinalIgnoreCase).ToList();

        public void Reopen()
        {
            Service.Dispose();
            Service = new LibraryService(Data.Path);
        }

        public void Dispose()
        {
            Service.Dispose();
            Photos.Dispose();
            Data.Dispose();
        }
    }

    [Fact]
    public void HidingAFolder_TakesItAndEverythingInsideItOutOfTheUnion()
    {
        using var f = new Fixture();

        f.Service.SetFolderShown(f.Root.Id, "Trip", shown: false);

        Assert.Equal(["a.jpg", @"Trip 2\x.jpg"], f.Shown());
        var hidden = Assert.Single(f.Service.GetContents().Hidden);
        Assert.Equal((f.Root.Id, "Trip", 3), (hidden.RootId, hidden.RelDir, hidden.ItemCount));
        Assert.Equal(f.Shown(), f.Service.GetUnion().Select(r => r.Item.RelPath).Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void ShowingItAgain_BringsItAllBack()
    {
        using var f = new Fixture();
        f.Service.SetFolderShown(f.Root.Id, "Trip", shown: false);

        f.Service.SetFolderShown(f.Root.Id, "Trip", shown: true);

        Assert.Equal(5, f.Shown().Count);
        Assert.Empty(f.Service.GetContents().Hidden);
    }

    [Fact]
    public void ShowingAFolderThatIsntHidden_ChangesNothing()
    {
        using var f = new Fixture();
        f.Service.SetFolderShown(f.Root.Id, @"Trip\Day 1", shown: false);

        f.Service.SetFolderShown(f.Root.Id, "Trip", shown: true);      // never hidden
        f.Service.SetFolderShown(f.Root.Id, "Nowhere", shown: true);   // not even there

        Assert.Equal(@"Trip\Day 1", Assert.Single(f.Service.GetContents().Hidden).RelDir);
    }

    [Fact]
    public void AFolderWhoseNameOnlyStartsTheSame_IsNotHidden()
    {
        using var f = new Fixture();

        f.Service.SetFolderShown(f.Root.Id, "Trip", shown: false);

        Assert.Contains(@"Trip 2\x.jpg", f.Shown()); // "Trip 2" is not inside "Trip"
    }

    [Fact]
    public void AHiddenFolder_StaysHidden_WhenTheLibraryIsOpenedAgain()
    {
        using var f = new Fixture();
        f.Service.SetFolderShown(f.Root.Id, "Trip", shown: false);

        f.Reopen();

        Assert.Equal(["a.jpg", @"Trip 2\x.jpg"], f.Shown());
    }

    [Fact]
    public void ANestedHiddenFolder_StaysHidden_WhenItsParentIsShownAgain()
    {
        using var f = new Fixture();
        f.Service.SetFolderShown(f.Root.Id, @"Trip\Day 1", shown: false);
        f.Service.SetFolderShown(f.Root.Id, "Trip", shown: false);

        f.Service.SetFolderShown(f.Root.Id, "Trip", shown: true);

        Assert.Equal(["a.jpg", @"Trip 2\x.jpg", @"Trip\t1.jpg", @"Trip\t2.jpg"], f.Shown());
        Assert.Equal(@"Trip\Day 1", Assert.Single(f.Service.GetContents().Hidden).RelDir);
    }

    [Fact]
    public void CaseAndSeparators_DontMatter_AndHidingTwiceKeepsOneEntry()
    {
        using var f = new Fixture();

        f.Service.SetFolderShown(f.Root.Id, "trip/day 1/", shown: false);
        f.Service.SetFolderShown(f.Root.Id, @"TRIP\Day 1", shown: false);

        Assert.DoesNotContain(@"Trip\Day 1\d1.jpg", f.Shown());
        Assert.Equal(4, f.Shown().Count);
        var hidden = Assert.Single(f.Service.GetContents().Hidden);
        Assert.Equal(1, hidden.ItemCount);

        f.Service.SetFolderShown(f.Root.Id, @"Trip\DAY 1", shown: true);
        Assert.Equal(5, f.Shown().Count);
    }

    [Fact]
    public void RemovingTheRoot_ForgetsItsHiddenFolders()
    {
        using var f = new Fixture();
        f.Service.SetFolderShown(f.Root.Id, "Trip", shown: false);

        f.Service.RemoveRoot(f.Root.Id);
        Assert.Equal(0, HiddenRowsOnDisk(f)); // gone with the root, not just out of sight under a new root id
        var again = f.Service.AddRoot(f.Photos.Path, "Photos");
        f.Service.IndexRoot(again);

        Assert.Empty(f.Service.GetContents().Hidden);
        Assert.Equal(5, f.Shown().Count);
    }

    private static long HiddenRowsOnDisk(Fixture f)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(f.Data.Path, ApertureDatabase.MetadataFileName), Pooling = false }.ToString());
        conn.Open();
        using var count = conn.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM hidden_folders;";
        return (long)count.ExecuteScalar()!;
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"\")]
    [InlineData("/")]
    public void TheFolderItself_CantBeHiddenThisWay(string relDir)
    {
        using var f = new Fixture();

        Assert.Throws<ArgumentException>(() => f.Service.SetFolderShown(f.Root.Id, relDir, shown: false));
        Assert.Equal(5, f.Shown().Count);
    }

    [Fact]
    public void ALibraryFromBeforeHiddenFolders_GetsTheTableWhenOpened()
    {
        using var f = new Fixture();
        f.Service.Dispose();
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder
               { DataSource = Path.Combine(f.Data.Path, ApertureDatabase.MetadataFileName), Pooling = false }.ToString()))
        {
            conn.Open();
            using var drop = conn.CreateCommand();
            drop.CommandText = "DROP TABLE hidden_folders;";
            drop.ExecuteNonQuery();
        }

        f.Reopen();
        f.Service.SetFolderShown(f.Root.Id, "Trip", shown: false);

        Assert.Equal(["a.jpg", @"Trip 2\x.jpg"], f.Shown());
    }
}
