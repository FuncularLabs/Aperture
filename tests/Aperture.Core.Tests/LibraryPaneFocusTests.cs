using Aperture.App;

namespace Aperture.Core.Tests;

/// <summary>
/// Contract for when a contents-pane click should move keyboard focus onto the
/// tile grid. Live WPF focus (dispatcher, visual tree, Keyboard.Focus) is not
/// exercised here — verify the click path on a real window:
/// folder in the tree → click empty tile-pane canvas → arrows / PageUp /
/// PageDown / scroll keys move the grid, not the tree. Clicking a folder,
/// a tile, a section header, the search box, or a dialog must stay as today.
///
/// Tab / Shift+Tab (manual): tree arrows keep the folder; Tab to the grid
/// keeps the selected tile; Up/Down stay in column; Page is ~one screen
/// same column; Tab back to the tree keeps the folder; ribbon is never a
/// Tab stop. Search stays on / and Ctrl+F.
/// </summary>
public class LibraryPaneFocusTests
{
    [Fact]
    public void Canvas_ClaimsTheGrid() =>
        Assert.True(LibraryPaneFocus.ShouldFocusContentsPane(LibraryPaneFocus.Hit.Canvas));

    [Fact]
    public void Tile_DoesNotClaim() =>
        Assert.False(LibraryPaneFocus.ShouldFocusContentsPane(LibraryPaneFocus.Hit.Tile));

    [Fact]
    public void SectionHeader_DoesNotClaim() =>
        Assert.False(LibraryPaneFocus.ShouldFocusContentsPane(LibraryPaneFocus.Hit.SectionHeader));

    [Fact]
    public void TextInput_DoesNotClaim() =>
        Assert.False(LibraryPaneFocus.ShouldFocusContentsPane(LibraryPaneFocus.Hit.TextInput));

    [Fact]
    public void Dialog_DoesNotClaim() =>
        Assert.False(LibraryPaneFocus.ShouldFocusContentsPane(LibraryPaneFocus.Hit.Dialog));

    [Fact]
    public void Classify_NullSourceIsCanvas() =>
        Assert.Equal(LibraryPaneFocus.Hit.Canvas, LibraryPaneFocus.Classify(null, null));

    [Fact]
    public void TabIntoPane_KeepsSelection_WhenFirstItemWouldTakeFocus() =>
        Assert.True(LibraryPaneFocus.ShouldKeepExistingSelection(
            fromOutside: true, newFocusIsItem: true, newFocusIsSelected: false));

    [Fact]
    public void TabIntoPane_AllowsFocusOnAlreadySelectedItem() =>
        Assert.False(LibraryPaneFocus.ShouldKeepExistingSelection(
            fromOutside: true, newFocusIsItem: true, newFocusIsSelected: true));

    [Fact]
    public void ArrowsInsidePane_DoNotRedirect() =>
        Assert.False(LibraryPaneFocus.ShouldKeepExistingSelection(
            fromOutside: false, newFocusIsItem: true, newFocusIsSelected: false));
}
