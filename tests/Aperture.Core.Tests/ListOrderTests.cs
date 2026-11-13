using Aperture.Core.Formatting;

namespace Aperture.Core.Tests;

/// <summary>
/// The order the user dragged a list into (0.9.1 batch, item 5: libraries and top-level folders "reordered by dragging,
/// and keep that order"). Until anything is dragged, a list is alphabetical, as it was.
/// </summary>
public class ListOrderTests
{
    private sealed record Thing(string Id, string Name);

    private static string Names(IEnumerable<Thing> things) => string.Join(",", things.Select(t => t.Name));

    private static readonly Thing[] Things = [new("3", "cherry"), new("1", "Apple"), new("2", "banana")];

    private static List<Thing> Apply(IReadOnlyList<string>? order) =>
        ListOrder.Apply(Things, order, t => t.Id, t => t.Name);

    [Fact]
    public void WithoutAnOrder_TheListIsAlphabetical_WhateverTheCase()
    {
        Assert.Equal("Apple,banana,cherry", Names(Apply(null)));
        Assert.Equal("Apple,banana,cherry", Names(Apply([])));
    }

    [Fact]
    public void TheOrder_IsFollowed()
    {
        Assert.Equal("cherry,Apple,banana", Names(Apply(["3", "1", "2"])));
    }

    [Fact]
    public void ThingsAddedAfterTheOrderWasSet_GoAtTheEnd_Alphabetically()
    {
        Thing[] things = [.. Things, new("5", "Date"), new("4", "apricot")];

        Assert.Equal("banana,Apple,apricot,cherry,Date",
            Names(ListOrder.Apply(things, ["2", "1"], t => t.Id, t => t.Name)));
    }

    [Fact]
    public void IdsNoLongerInTheList_AreIgnored_AndARepeatCountsOnce()
    {
        Assert.Equal("banana,cherry,Apple", Names(Apply(["gone", "2", "3", "2", "1"])));
    }

    [Theory]
    [InlineData("1", 0, "1,2,3,4")] // where it already is
    [InlineData("3", 0, "3,1,2,4")] // to the top
    [InlineData("1", 3, "2,3,4,1")] // to the bottom (the last place)
    [InlineData("1", 2, "2,3,1,4")]
    [InlineData("4", 1, "1,4,2,3")]
    [InlineData("2", 99, "1,3,4,2")] // past the end: the last place
    [InlineData("3", -5, "3,1,2,4")] // before the start: the first
    public void Move_PutsTheItemAtTheIndex_InTheListWithoutIt(string id, int toIndex, string expected) =>
        Assert.Equal(expected, string.Join(",", ListOrder.Move(["1", "2", "3", "4"], id, toIndex)));

    [Fact]
    public void Move_OfAnIdNotInTheList_ChangesNothing() =>
        Assert.Equal("1,2,3", string.Join(",", ListOrder.Move(["1", "2", "3"], "9", 0)));

    [Fact]
    public void Move_WorksOnNumbersToo() =>
        Assert.Equal([2L, 1L], ListOrder.Move([1L, 2L], 1L, 1));

    [Theory]
    // A drop's slot counts the gaps among all the rows, the dragged one included: 0 above the first, 3 below the last.
    [InlineData("1", 2, "2,1,3")] // the first dropped just below the second
    [InlineData("1", 3, "2,3,1")] // below the last
    [InlineData("3", 0, "3,1,2")] // the last dropped above the first
    [InlineData("3", 1, "1,3,2")]
    [InlineData("2", 1, "1,2,3")] // just above itself: its own place
    [InlineData("2", 2, "1,2,3")] // just below itself: its own place too
    [InlineData("2", 9, "1,3,2")] // past the end: the last place
    public void MoveToSlot_DropsTheItemInTheGap(string id, int slot, string expected) =>
        Assert.Equal(expected, string.Join(",", ListOrder.MoveToSlot(["1", "2", "3"], id, slot)));

    [Fact]
    public void Apply_SortsByTheComparerItIsGiven()
    {
        Thing[] things = [new("1", "Zoo"), new("2", "Été")];

        // Libraries: the user's culture puts Été before Zoo. Folders: ordinal, as they always were (É after Z).
        Assert.Equal("Été,Zoo", Names(ListOrder.Apply(things, null, t => t.Id, t => t.Name,
            StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("en-US"), ignoreCase: true))));
        Assert.Equal("Zoo,Été", Names(ListOrder.Apply(things, null, t => t.Id, t => t.Name, StringComparer.OrdinalIgnoreCase)));
    }
}
