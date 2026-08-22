using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Aperture.App.ViewModels;

namespace Aperture.App;

/// <summary>
/// Keyboard focus for the library: folder tree ↔ tile pane. Clicking a tile
/// already focuses that <see cref="ListBoxItem"/>; clicking the empty canvas
/// did not, so arrow / PageUp / PageDown / scroll keys stayed on the folder
/// tree. Tab onto either pane must not change its selection.
/// </summary>
internal static class LibraryPaneFocus
{
    internal enum Hit
    {
        /// <summary>Empty pane background, scrollbar, padding — claim the grid.</summary>
        Canvas,
        /// <summary>A media tile already focuses its <see cref="ListBoxItem"/>.</summary>
        Tile,
        /// <summary>A date-section header already focuses its <see cref="ToggleButton"/>.</summary>
        SectionHeader,
        /// <summary>Search, rename, notes — leave the caret where it is.</summary>
        TextInput,
        /// <summary>A dialog owns the click or the current focus.</summary>
        Dialog,
    }

    /// <summary>
    /// True when this click should move keyboard focus onto the contents pane.
    /// Tile and section-header clicks already focus themselves; text boxes and
    /// dialogs keep focus.
    /// </summary>
    public static bool ShouldFocusContentsPane(Hit hit) => hit is Hit.Canvas;

    public static bool ShouldFocusContentsPane(DependencyObject? originalSource, IInputElement? currentFocus) =>
        ShouldFocusContentsPane(Classify(originalSource, currentFocus));

    /// <summary>
    /// True when keyboard focus is arriving from outside a pane onto an item
    /// that is not the current selection. WPF <see cref="ListBox"/> /
    /// <see cref="TreeView"/> often focus (and therefore select) the first
    /// item on entry — redirect so Tab does not change the pane's selection.
    /// </summary>
    public static bool ShouldKeepExistingSelection(bool fromOutside, bool newFocusIsItem, bool newFocusIsSelected) =>
        fromOutside && newFocusIsItem && !newFocusIsSelected;

    /// <summary>Win32 <c>WM_MOUSEACTIVATE</c> — the click that activates a window.</summary>
    internal const int WmMouseActivate = 0x0021;

    /// <summary>Activate and deliver the click to the control (Explorer). Not <c>MA_ACTIVATEANDEAT</c>.</summary>
    internal const int MaActivate = 1;

    /// <summary>
    /// True when this is the click that activates the window. Return
    /// <see cref="MaActivate"/> so the same click focuses the folder / tile /
    /// canvas — an activation-only first click would swallow it.
    /// </summary>
    public static bool TryPassActivatingClick(int msg, out IntPtr result)
    {
        if (msg != WmMouseActivate)
        {
            result = IntPtr.Zero;
            return false;
        }
        result = new IntPtr(MaActivate);
        return true;
    }

    internal enum TreeFocusAction
    {
        /// <summary>Leave WPF's focus target alone (arrows inside the tree, or a click).</summary>
        Leave,
        /// <summary>Focus the already-selected folder so arrows move from there.</summary>
        FocusSelectedItem,
        /// <summary>
        /// Keep focus on the TreeView — "Everything" is showing; do not select the first folder.
        /// </summary>
        FocusTree,
    }

    /// <summary>What a left-click on the folder tree should do to keyboard focus.</summary>
    internal enum TreeClickAction
    {
        /// <summary>Folder row / name / icon — <c>TreeViewItem.Focus()</c> (which also selects).</summary>
        FocusItem,
        /// <summary>
        /// Expand chevron or include checkbox: do not <c>Focus()</c> the item.
        /// That call selects the folder; Explorer and 0.8.5 keep those hits off selection.
        /// Alias text boxes keep the caret.
        /// </summary>
        Leave,
        /// <summary>Empty tree chrome — focus the TreeView without changing selection.</summary>
        FocusTree,
    }

    internal enum TreeHit
    {
        Row,
        Expander,
        IncludeCheckBox,
        TextInput,
        Canvas,
    }

    /// <summary>
    /// Same-click folder focus applies to the row only. The expander
    /// (<see cref="ToggleButton"/>, <c>Focusable=False</c>) and the root include
    /// checkbox must not select the folder.
    /// </summary>
    public static TreeClickAction OnTreePreviewClick(TreeHit hit) => hit switch
    {
        TreeHit.Row => TreeClickAction.FocusItem,
        TreeHit.Canvas => TreeClickAction.FocusTree,
        _ => TreeClickAction.Leave,
    };

    public static TreeClickAction OnTreePreviewClick(DependencyObject? originalSource) =>
        OnTreePreviewClick(ClassifyTreeHit(originalSource));

    internal static TreeHit ClassifyTreeHit(DependencyObject? originalSource)
    {
        var node = originalSource;
        while (node is not null)
        {
            if (node is TextBox or PasswordBox or ComboBox or RichTextBox)
                return TreeHit.TextInput;
            // CheckBox before ToggleButton — CheckBox is a ToggleButton.
            if (node is CheckBox)
                return TreeHit.IncludeCheckBox;
            if (node is ToggleButton)
                return TreeHit.Expander;
            if (node is TreeViewItem)
                return TreeHit.Row;
            if (node is TreeView or Window)
                return TreeHit.Canvas;
            node = Parent(node);
        }
        return TreeHit.Canvas;
    }

    /// <summary>
    /// Where keyboard focus should land when it arrives on the folder tree.
    /// Tab must not change the selected folder; click must not yank focus back
    /// to a previous node while the new item's <c>IsSelected</c> is still catching up.
    /// </summary>
    public static TreeFocusAction OnTreeKeyboardArrival(
        bool fromOutside, bool mousePressed,
        bool newFocusIsItem, bool newFocusIsSelected,
        bool hasSelectedItem)
    {
        if (!fromOutside)
            return TreeFocusAction.Leave;

        // Row-click path focuses the item in PreviewMouseDown; IsSelected may still be false.
        if (mousePressed)
            return TreeFocusAction.Leave;

        if (newFocusIsItem && newFocusIsSelected)
            return TreeFocusAction.Leave;

        if (hasSelectedItem)
            return TreeFocusAction.FocusSelectedItem;

        // Tab onto the tree (or onto the first folder) while Everything is showing.
        return TreeFocusAction.FocusTree;
    }

    public static Hit Classify(DependencyObject? originalSource, IInputElement? currentFocus)
    {
        // A dialog keeps focus even if the click somehow reached the pane
        // (modeless / leftover hit-test). Modal dialogs never get here.
        if (IsInDialog(originalSource) || IsInDialog(currentFocus as DependencyObject))
            return Hit.Dialog;

        if (IsTextInput(originalSource))
            return Hit.TextInput;

        if (IsTile(originalSource))
            return Hit.Tile;

        if (IsSectionHeader(originalSource))
            return Hit.SectionHeader;

        return Hit.Canvas;
    }

    private static bool IsInDialog(DependencyObject? node)
    {
        if (node is null)
            return false;
        var window = Window.GetWindow(node);
        return window is not null and not MainWindow;
    }

    private static bool IsTextInput(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is TextBox or PasswordBox or ComboBox or RichTextBox)
                return true;
            if (node is ListBoxItem or ListBox or Window)
                break;
            node = Parent(node);
        }
        return false;
    }

    private static bool IsTile(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is ListBoxItem { DataContext: TileVm })
                return true;
            if (node is ListBox or Window)
                break;
            node = Parent(node);
        }
        return false;
    }

    private static bool IsSectionHeader(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is ToggleButton)
                return true;
            if (node is ListBoxItem or ListBox or Window)
                break;
            node = Parent(node);
        }
        return false;
    }

    private static DependencyObject? Parent(DependencyObject node) =>
        node is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(node)
            : LogicalTreeHelper.GetParent(node);
}
