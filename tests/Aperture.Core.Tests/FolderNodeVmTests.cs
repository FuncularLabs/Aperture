using Aperture.App.ViewModels;

namespace Aperture.Core.Tests;

/// <summary>A folder-tree node: lazy children, and the subfolder's show/hide checkbox (Paul, 2026-10-26).</summary>
public class FolderNodeVmTests
{
    private static FolderNodeVm Node(bool hasChildren = false, bool isShown = true,
        Func<long, string, List<FolderNodeVm>>? load = null, Action<FolderNodeVm, bool>? onShown = null,
        Action<FolderNodeVm, bool>? onExpanded = null) =>
        new(7, @"Trip\Day 1", "Day 1", isRoot: false, hasChildren, root: null, count: 3,
            load ?? ((_, _) => []), onExpanded, isShown, onShown);

    [Fact]
    public void TickingOrUnticking_IsReportedOnce_PerChange()
    {
        var reports = new List<(string RelDir, bool Shown)>();
        var node = Node(onShown: (n, shown) => reports.Add((n.RelDir, shown)));

        node.IsShown = true;  // no change
        node.IsShown = false;
        node.IsShown = false; // no change
        node.IsShown = true;

        Assert.Equal([(@"Trip\Day 1", false), (@"Trip\Day 1", true)], reports);
    }

    [Fact]
    public void AHiddenFolder_StartsUnticked_WithoutReportingIt()
    {
        var reports = 0;

        var node = Node(isShown: false, onShown: (_, _) => reports++);

        Assert.False(node.IsShown);
        Assert.Equal(0, reports);
        Assert.Equal("7|Trip\\Day 1", node.Key);
    }

    [Fact]
    public void Children_LoadOnFirstExpand_Only()
    {
        var loads = 0;
        var expanded = new List<bool>();
        var node = Node(hasChildren: true,
            load: (rootId, relDir) =>
            {
                loads++;
                return [new FolderNodeVm(rootId, relDir + @"\Morning", "Morning", false, false, null, 1, (_, _) => [])];
            },
            onExpanded: (_, isExpanded) => expanded.Add(isExpanded));

        var placeholder = Assert.Single(node.Children); // keeps the expander arrow until it's opened
        Assert.NotEqual("Morning", placeholder.Name);

        node.IsExpanded = true;
        node.IsExpanded = false;
        node.IsExpanded = true;

        Assert.Equal(1, loads);
        Assert.Equal(@"Trip\Day 1\Morning", Assert.Single(node.Children).RelDir);
        Assert.Equal([true, false, true], expanded);
    }

    [Fact]
    public void Count_ReportsAChange()
    {
        var node = Node();
        var changes = new List<string?>();
        node.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        node.Count = 200;
        node.Count = 200; // no change

        Assert.Equal(200, node.Count);
        Assert.Equal([nameof(FolderNodeVm.Count)], changes);
    }

    [Fact]
    public void ANodeWithoutSubfolders_HasNothingToExpand()
    {
        var node = Node(hasChildren: false);

        node.IsSelected = true;

        Assert.Empty(node.Children);
        Assert.True(node.IsSelected);
        Assert.Equal(3, node.Count);
        Assert.False(node.IsRoot);
        Assert.Null(node.Root);
    }
}
