using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Reel.App.ViewModels;

namespace Reel.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        PreviewKeyDown += OnWindowPreviewKeyDown;
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
            return;

        var (w, h, left, top, max) = ViewModel.RestoreWindow();
        if (w is > 200 && h is > 200)
        {
            Width = w.Value;
            Height = h.Value;
        }
        // Only restore position if it lands on a visible screen.
        if (left is { } l && top is { } t && IsOnScreen(l, t, Width, Height))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = l;
            Top = t;
        }
        if (max)
            WindowState = WindowState.Maximized;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (ViewModel is null)
            return;

        var bounds = RestoreBounds; // normal (un-maximized) bounds
        ViewModel.SaveWindow(bounds.Width, bounds.Height, bounds.Left, bounds.Top,
            WindowState == WindowState.Maximized);
    }

    private static bool IsOnScreen(double left, double top, double width, double height)
    {
        // The bounding rectangle of all monitors (device-independent units).
        var virtualScreen = new System.Windows.Rect(
            SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        return virtualScreen.IntersectsWith(new System.Windows.Rect(left, top, width, height));
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel is null)
            return;

        // While quick-look is open it owns the keyboard.
        if (ViewModel.QuickLookOpen)
        {
            switch (e.Key)
            {
                case Key.Escape or Key.Space:
                    ViewModel.CloseQuickLookCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.Left:
                    ViewModel.QuickLookPrevCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.Right:
                    ViewModel.QuickLookNextCommand.Execute(null);
                    e.Handled = true;
                    break;
            }
            return;
        }

        // Ctrl +/- zoom (anchored to the centre item).
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            if (e.Key is Key.OemPlus or Key.Add)
            {
                ZoomWithAnchor(true);
                e.Handled = true;
                return;
            }
            if (e.Key is Key.OemMinus or Key.Subtract)
            {
                ZoomWithAnchor(false);
                e.Handled = true;
                return;
            }
        }

        var focused = Keyboard.FocusedElement;

        // "/" focuses search (unless already typing).
        if (e.Key == Key.Oem2 && Keyboard.Modifiers == ModifierKeys.None
            && focused is not System.Windows.Controls.TextBox)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        // Space opens quick-look — but not when typing or toggling a section header.
        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None
            && focused is not System.Windows.Controls.TextBox
            && focused is not System.Windows.Controls.Primitives.ToggleButton)
        {
            ViewModel.OpenQuickLookCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnQuickLookBackgroundClick(object sender, MouseButtonEventArgs e)
    {
        // Close only when the dim background itself is clicked, not a button.
        if (ReferenceEquals(e.OriginalSource, sender))
            ViewModel?.CloseQuickLookCommand.Execute(null);
    }

    // --- Root alias inline rename ---

    private void OnAliasClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && sender is FrameworkElement { DataContext: RootVm root })
        {
            root.IsEditing = true;
            e.Handled = true;
        }
    }

    private void OnAliasEditVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox box && box.IsVisible)
        {
            box.Focus();
            box.SelectAll();
        }
    }

    private void OnAliasEditKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox box || sender is not FrameworkElement { DataContext: RootVm root })
            return;

        if (e.Key == Key.Enter)
        {
            root.IsEditing = false; // triggers LostFocus -> binding commit
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            box.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateTarget(); // revert
            root.IsEditing = false;
            e.Handled = true;
        }
    }

    private void OnAliasEditLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RootVm root })
            root.IsEditing = false;
    }

    private void OnItemsDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Ignore double-clicks that aren't on an item (e.g. section header / empty space).
        if ((e.OriginalSource as DependencyObject) is null || ViewModel?.SelectedItem is null)
            return;
        ViewModel.ActivateItemCommand.Execute(ViewModel.SelectedItem);
    }

    private void OnItemsPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
            return;

        e.Handled = true;
        ZoomWithAnchor(e.Delta > 0);
    }

    private void OnZoomInClick(object sender, RoutedEventArgs e) => ZoomWithAnchor(true);

    private void OnZoomOutClick(object sender, RoutedEventArgs e) => ZoomWithAnchor(false);

    /// <summary>Zoom while keeping the item near the viewport centre in view.</summary>
    private void ZoomWithAnchor(bool zoomIn)
    {
        if (ViewModel is null)
            return;

        var anchor = ItemNearCenter() ?? ViewModel.SelectedTile;

        if (zoomIn)
            ViewModel.ZoomIn();
        else
            ViewModel.ZoomOut();

        if (anchor is not null)
        {
            // Re-scroll after the new tile size has been laid out.
            Dispatcher.BeginInvoke(
                new Action(() => { try { ItemsList.ScrollIntoView(anchor); } catch { } }),
                System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private TileVm? ItemNearCenter()
    {
        if (ItemsList.ActualWidth <= 0 || ItemsList.ActualHeight <= 0)
            return null;

        var point = new Point(ItemsList.ActualWidth / 2, ItemsList.ActualHeight / 2);
        var hit = VisualTreeHelper.HitTest(ItemsList, point)?.VisualHit as DependencyObject;
        while (hit is not null and not System.Windows.Controls.ListBoxItem)
            hit = VisualTreeHelper.GetParent(hit);
        return (hit as System.Windows.Controls.ListBoxItem)?.DataContext as TileVm;
    }

    /// <summary>Explorer-style arrow navigation over the grid, plus Ctrl+C / Ctrl+X on the selection.</summary>
    private void OnGridKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel is null || ViewModel.QuickLookOpen)
            return;

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            if (e.Key == Key.C)
            {
                ViewModel.CopyFileCommand.Execute(ViewModel.SelectedTile);
                e.Handled = true;
            }
            else if (e.Key == Key.X)
            {
                ViewModel.CutFileCommand.Execute(ViewModel.SelectedTile);
                e.Handled = true;
            }
            return; // leave Ctrl+arrows / Ctrl+wheel alone
        }

        int delta;
        switch (e.Key)
        {
            case Key.Left: delta = -1; break;
            case Key.Right: delta = 1; break;
            case Key.Up: delta = -ColumnsPerRow(); break;
            case Key.Down: delta = ColumnsPerRow(); break;
            default: return;
        }

        var tile = ViewModel.MoveSelection(delta);
        if (tile is not null)
        {
            ItemsList.ScrollIntoView(tile);
            Dispatcher.BeginInvoke(
                new Action(() => (ItemsList.ItemContainerGenerator.ContainerFromItem(tile)
                    as System.Windows.Controls.ListBoxItem)?.Focus()),
                System.Windows.Threading.DispatcherPriority.Background);
        }
        e.Handled = true;
    }

    private int ColumnsPerRow()
    {
        var tileOuter = (ViewModel?.TileSize ?? 200) + 20; // tile + margin/padding/spacing
        var columns = (int)((ItemsList.ActualWidth - 12) / Math.Max(1, tileOuter));
        return Math.Max(1, columns);
    }
}
