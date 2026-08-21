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
}
