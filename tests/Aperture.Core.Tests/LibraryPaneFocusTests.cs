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
/// Tab / Shift+Tab (manual): only tree ↔ tile list; ribbon and Everything
/// are not stops. Tab to the tree keeps the folder (or Everything) and
/// arrows move folders immediately; Tab to the grid keeps the selected tile.
/// Click in the tree focuses it. First click on an unfocused window or pane
/// focuses the clicked folder / tile / canvas in that same click. Tree Up/Down
/// commit the node at once; the tile pane may spinner. Search stays on / and Ctrl+F.
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

    [Fact]
    public void TabIntoTree_RedirectsToSelectedFolder() =>
        Assert.Equal(LibraryPaneFocus.TreeFocusAction.FocusSelectedItem,
            LibraryPaneFocus.OnTreeKeyboardArrival(
                fromOutside: true, mousePressed: false,
                newFocusIsItem: true, newFocusIsSelected: false, hasSelectedItem: true));

    [Fact]
    public void TabOntoTree_WithSelection_FocusesSelectedItem() =>
        Assert.Equal(LibraryPaneFocus.TreeFocusAction.FocusSelectedItem,
            LibraryPaneFocus.OnTreeKeyboardArrival(
                fromOutside: true, mousePressed: false,
                newFocusIsItem: false, newFocusIsSelected: false, hasSelectedItem: true));

    [Fact]
    public void TabOntoTree_Everything_FocusesTreeNotFirstFolder() =>
        Assert.Equal(LibraryPaneFocus.TreeFocusAction.FocusTree,
            LibraryPaneFocus.OnTreeKeyboardArrival(
                fromOutside: true, mousePressed: false,
                newFocusIsItem: false, newFocusIsSelected: false, hasSelectedItem: false));

    [Fact]
    public void TabOntoFirstFolder_WhenEverything_DoesNotSelectIt() =>
        Assert.Equal(LibraryPaneFocus.TreeFocusAction.FocusTree,
            LibraryPaneFocus.OnTreeKeyboardArrival(
                fromOutside: true, mousePressed: false,
                newFocusIsItem: true, newFocusIsSelected: false, hasSelectedItem: false));

    [Fact]
    public void TabOntoAlreadySelectedFolder_LeavesIt() =>
        Assert.Equal(LibraryPaneFocus.TreeFocusAction.Leave,
            LibraryPaneFocus.OnTreeKeyboardArrival(
                fromOutside: true, mousePressed: false,
                newFocusIsItem: true, newFocusIsSelected: true, hasSelectedItem: true));

    [Fact]
    public void ClickTreeRow_LeavesFocusOnClickedItem() =>
        Assert.Equal(LibraryPaneFocus.TreeFocusAction.Leave,
            LibraryPaneFocus.OnTreeKeyboardArrival(
                fromOutside: true, mousePressed: true,
                newFocusIsItem: true, newFocusIsSelected: false, hasSelectedItem: true));

    [Fact]
    public void ArrowsInsideTree_DoNotRedirect() =>
        Assert.Equal(LibraryPaneFocus.TreeFocusAction.Leave,
            LibraryPaneFocus.OnTreeKeyboardArrival(
                fromOutside: false, mousePressed: false,
                newFocusIsItem: true, newFocusIsSelected: false, hasSelectedItem: true));

    [Fact]
    public void TreeClick_Row_FocusesItem() =>
        Assert.Equal(LibraryPaneFocus.TreeClickAction.FocusItem,
            LibraryPaneFocus.OnTreePreviewClick(LibraryPaneFocus.TreeHit.Row));

    [Fact]
    public void TreeClick_Expander_DoesNotSelectFolder() =>
        Assert.Equal(LibraryPaneFocus.TreeClickAction.Leave,
            LibraryPaneFocus.OnTreePreviewClick(LibraryPaneFocus.TreeHit.Expander));

    [Fact]
    public void TreeClick_IncludeCheckBox_DoesNotSelectFolder() =>
        Assert.Equal(LibraryPaneFocus.TreeClickAction.Leave,
            LibraryPaneFocus.OnTreePreviewClick(LibraryPaneFocus.TreeHit.IncludeCheckBox));

    [Fact]
    public void TreeClick_AliasEdit_LeavesCaret() =>
        Assert.Equal(LibraryPaneFocus.TreeClickAction.Leave,
            LibraryPaneFocus.OnTreePreviewClick(LibraryPaneFocus.TreeHit.TextInput));

    [Fact]
    public void TreeClick_EmptyChrome_FocusesTree() =>
        Assert.Equal(LibraryPaneFocus.TreeClickAction.FocusTree,
            LibraryPaneFocus.OnTreePreviewClick(LibraryPaneFocus.TreeHit.Canvas));

    [Fact]
    public void ClassifyTreeHit_NullIsCanvas() =>
        Assert.Equal(LibraryPaneFocus.TreeHit.Canvas, LibraryPaneFocus.ClassifyTreeHit(null));

    [Fact]
    public void ActivatingClick_IsPassedThrough_NotEaten()
    {
        Assert.True(LibraryPaneFocus.TryPassActivatingClick(LibraryPaneFocus.WmMouseActivate, out var result));
        Assert.Equal(LibraryPaneFocus.MaActivate, result.ToInt32());
    }

    [Fact]
    public void NonActivateMessage_IsNotHandled()
    {
        Assert.False(LibraryPaneFocus.TryPassActivatingClick(0x0201, out var result)); // WM_LBUTTONDOWN
        Assert.Equal(IntPtr.Zero, result);
    }
}
