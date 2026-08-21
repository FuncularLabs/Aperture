using Aperture.App;

namespace Aperture.Core.Tests;

/// <summary>
/// Contract for when a contents-pane click should move keyboard focus onto the
/// tile grid. Live WPF focus (dispatcher, visual tree, Keyboard.Focus) is not
/// exercised here — verify the click path on a real window:
/// folder in the tree → click empty tile-pane canvas → arrows / PageUp /
/// PageDown / scroll keys move the grid, not the tree. Clicking a folder,
/// a tile, a section header, the search box, or a dialog must stay as today.
/// </summary>
public class LibraryPaneFocusTests
{
    [Theory]
    [InlineData(LibraryPaneFocus.Hit.Canvas, true)]
    [InlineData(LibraryPaneFocus.Hit.Tile, false)]
    [InlineData(LibraryPaneFocus.Hit.SectionHeader, false)]
    [InlineData(LibraryPaneFocus.Hit.TextInput, false)]
    [InlineData(LibraryPaneFocus.Hit.Dialog, false)]
    public void ShouldFocusContentsPane_OnlyCanvasClaimsTheGrid(LibraryPaneFocus.Hit hit, bool expected) =>
        Assert.Equal(expected, LibraryPaneFocus.ShouldFocusContentsPane(hit));

    [Fact]
    public void Classify_NullSourceIsCanvas() =>
        Assert.Equal(LibraryPaneFocus.Hit.Canvas, LibraryPaneFocus.Classify(null, null));
}
