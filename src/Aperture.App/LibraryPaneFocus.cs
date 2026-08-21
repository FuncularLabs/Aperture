using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Aperture.App.ViewModels;

namespace Aperture.App;

/// <summary>
/// Keyboard focus for the library contents/tile pane. Clicking a tile already
/// focuses that <see cref="ListBoxItem"/>; clicking the empty canvas did not,
/// so arrow / PageUp / PageDown / scroll keys stayed on the folder tree.
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
