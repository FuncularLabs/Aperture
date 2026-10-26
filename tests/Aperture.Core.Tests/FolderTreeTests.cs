using Aperture.Core.Library;
using Aperture.Core.Models;

namespace Aperture.Core.Tests;

/// <summary>
/// The folder tree's subfolders, worked out from the library's rows and its hidden folders: a hidden folder stays
/// in the tree, unticked, so it can be shown again (Paul, 2026-10-26).
/// </summary>
public class FolderTreeTests
{
    private const long Root = 1;
    private const long OtherRoot = 2;

    private static LibraryRow Row(string relPath, long rootId = Root) => new()
    {
        Item = new MediaItem { RootId = rootId, RelPath = relPath, FileName = Path.GetFileName(relPath), Ext = ".jpg" },
        RootAlias = "Photos",
        RootPath = @"C:\Photos",
    };

    private static List<LibraryRow> Rows(params string[] relPaths) => [.. relPaths.Select(p => Row(p))];

    private static string Show(IEnumerable<FolderEntry> entries) =>
        string.Join("; ", entries.Select(e => $"{e.Name}|{e.RelDir}|{e.Count}|{(e.HasChildren ? "nests" : "flat")}|{(e.IsShown ? "shown" : "hidden")}"));

    [Fact]
    public void Subfolders_AreListedWithTheirCounts_AndWhetherTheyNest()
    {
        var rows = Rows("a.jpg", @"2024\x.jpg", @"2024\06\y.jpg", @"2024\06\z.jpg", @"2023\w.jpg");

        Assert.Equal(@"2024|2024|3|nests|shown; 2023|2023|1|flat|shown", Show(FolderTree.Subfolders(rows, [], Root, "")));
        Assert.Equal(@"06|2024\06|2|flat|shown", Show(FolderTree.Subfolders(rows, [], Root, "2024")));
        Assert.Empty(FolderTree.Subfolders(rows, [], Root, @"2024\06"));
    }

    [Fact]
    public void AHiddenFolder_IsListedUnticked_WithItsCount_AndNoChildren()
    {
        var rows = Rows("a.jpg", @"Trip 2\x.jpg"); // Trip's rows are out of the union
        HiddenFolder[] hidden = [new(Root, "Trip", 3)];

        Assert.Equal("Trip 2|Trip 2|1|flat|shown; Trip|Trip|3|flat|hidden", Show(FolderTree.Subfolders(rows, hidden, Root, "")));
    }

    [Fact]
    public void AFolderThatOnlyLeadsToAHiddenOne_IsStillListed()
    {
        var rows = Rows("a.jpg");
        HiddenFolder[] hidden = [new(Root, @"Trip\Day 1", 1)];

        Assert.Equal("Trip|Trip|0|nests|shown", Show(FolderTree.Subfolders(rows, hidden, Root, "")));
        Assert.Equal(@"Day 1|Trip\Day 1|1|flat|hidden", Show(FolderTree.Subfolders(rows, hidden, Root, "Trip")));
    }

    [Fact]
    public void AHiddenFolderWithNoItemsLeft_IsNotListed()
    {
        var rows = Rows("a.jpg");
        HiddenFolder[] hidden = [new(Root, "Gone", 0), new(Root, @"Old\Gone too", 0)];

        Assert.Empty(FolderTree.Subfolders(rows, hidden, Root, ""));
    }

    [Fact]
    public void NamesThatOnlyStartTheSame_AreNotMixedUp()
    {
        var rows = Rows(@"Trip 2\x.jpg", @"Trip 2\Inner\y.jpg");
        HiddenFolder[] hidden = [new(Root, "Trip", 2)];

        Assert.Equal("Trip 2|Trip 2|2|nests|shown; Trip|Trip|2|flat|hidden", Show(FolderTree.Subfolders(rows, hidden, Root, "")));
        Assert.Equal(@"Inner|Trip 2\Inner|1|flat|shown", Show(FolderTree.Subfolders(rows, hidden, Root, "Trip 2")));
    }

    [Fact]
    public void AnotherFoldersHiddenFolders_AreNotListedHere()
    {
        var rows = Rows("a.jpg");
        HiddenFolder[] hidden = [new(OtherRoot, "Trip", 4)];

        Assert.Empty(FolderTree.Subfolders(rows, hidden, Root, ""));
    }

    [Fact]
    public void AFolderBothSeenAndHidden_IsListedOnce_AsHidden()
    {
        var rows = Rows(@"trip\x.jpg"); // a stale row the caller still holds
        HiddenFolder[] hidden = [new(Root, "Trip", 1)];

        Assert.Equal("Trip|Trip|1|flat|hidden", Show(FolderTree.Subfolders(rows, hidden, Root, "")));
    }
}
