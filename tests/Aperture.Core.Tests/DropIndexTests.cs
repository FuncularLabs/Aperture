using Aperture.App;

namespace Aperture.Core.Tests;

/// <summary>
/// Where a dragged library or folder lands (0.9.1 batch, item 5): the gap the pointer is nearest, by the row it's over
/// (its upper half: above it; its lower half: below it).
/// </summary>
public class DropIndexTests
{
    // Three rows 20 high, with 2 between them: 0–20, 22–42, 44–64.
    private static readonly (double Top, double Height)[] Rows = [(0, 20), (22, 20), (44, 20)];

    [Theory]
    [InlineData(-5, 0)]   // above the list
    [InlineData(0, 0)]    // the first row's top
    [InlineData(9.9, 0)]  // its upper half
    [InlineData(10, 1)]   // its middle and below: after it
    [InlineData(19, 1)]
    [InlineData(21, 1)]   // the gap between the first and second: before the second
    [InlineData(31.9, 1)]
    [InlineData(32, 2)]
    [InlineData(53.9, 2)]
    [InlineData(54, 3)]   // the last row's lower half
    [InlineData(64, 3)]
    [InlineData(500, 3)]  // below the list
    public void TheSlot_IsByTheRowThePointerIsOver(double y, int slot) =>
        Assert.Equal(slot, DropIndex.At(y, Rows));

    [Fact]
    public void AnEmptyList_HasOneSlot() => Assert.Equal(0, DropIndex.At(10, []));
}
