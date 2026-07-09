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
        PreviewMouseDown += OnWindowPreviewMouseDown;
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

        _lastLocationKey = ViewModel.LocationKey;
        ViewModel.ViewApplying += OnViewApplying;
        ViewModel.ViewApplied += OnViewApplied;
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
            if (e.Key == Key.F)
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
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

        // Space: toggle the section when the cursor is on a header, else quick-look.
        // (Skip when a TextBox/ToggleButton has focus, or the folder tree — it toggles
        // the focused tree node itself in OnTreeKeyDown.)
        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None
            && focused is not System.Windows.Controls.TextBox
            && focused is not System.Windows.Controls.Primitives.ToggleButton
            && !IsInTree(focused as DependencyObject))
        {
            if (ViewModel.CursorIsHeader)
                ViewModel.ToggleCursorSection();
            else
                ViewModel.OpenQuickLookCommand.Execute(null);
            e.Handled = true;
        }
    }

    private bool IsInTree(DependencyObject? o)
    {
        while (o is not null)
        {
            if (ReferenceEquals(o, FolderTreeView))
                return true;
            o = o is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(o)
                : LogicalTreeHelper.GetParent(o);
        }
        return false;
    }

    /// <summary>Space on a folder-tree node expands/collapses it (Right/Left already do so by default).</summary>
    private void OnTreeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space
            && FolderTreeView.SelectedItem is FolderNodeVm { HasChildren: true } node)
        {
            node.IsExpanded = !node.IsExpanded;
            e.Handled = true;
        }
    }

    private void OnSearchFocus(object sender, RoutedEventArgs e) => ViewModel?.OpenQuickPicks();

    private void OnSearchBlur(object sender, RoutedEventArgs e) => ViewModel?.CloseQuickPicks();

    /// <summary>Any click outside the search box (and its popup) dismisses the tag quick-picks.</summary>
    private void OnWindowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is not { QuickPicksOpen: true })
            return;
        if (!IsDescendantOf(e.OriginalSource as DependencyObject, SearchBorder))
            ViewModel.CloseQuickPicks();
    }

    private static bool IsDescendantOf(DependencyObject? node, DependencyObject ancestor)
    {
        while (node is not null)
        {
            if (ReferenceEquals(node, ancestor))
                return true;
            node = node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    // --- Grid scroll memory: start at top on navigation, remember per-folder position ---

    private readonly System.Collections.Generic.Dictionary<string, double> _scrollByLocation = new();
    private string _lastLocationKey = "";
    private System.Windows.Controls.ScrollViewer? _gridScroll;

    private System.Windows.Controls.ScrollViewer? GridScroll() =>
        _gridScroll ??= FindDescendant<System.Windows.Controls.ScrollViewer>(ItemsList);

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                return match;
            if (FindDescendant<T>(child) is { } found)
                return found;
        }
        return null;
    }

    private void OnViewApplying()
    {
        // Capture the scroll position for the view we're leaving (before its items change).
        if (GridScroll() is { } sv)
            _scrollByLocation[_lastLocationKey] = sv.VerticalOffset;
    }

    private void OnViewApplied()
    {
        if (ViewModel is null)
            return;
        _lastLocationKey = ViewModel.LocationKey;
        var target = _scrollByLocation.GetValueOrDefault(_lastLocationKey, 0); // 0 = top for a fresh view
        Dispatcher.BeginInvoke(new Action(() => GridScroll()?.ScrollToVerticalOffset(target)),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // --- Preview pane: zoom + pan ---

    private double _previewZoom = 1;
    private bool _panning;
    private Point _panStart;
    private double _panH, _panV;

    private void SetPreviewZoom(double z)
    {
        _previewZoom = Math.Clamp(z, 0.1, 8);
        PreviewScale.ScaleX = _previewZoom;
        PreviewScale.ScaleY = _previewZoom;
    }

    private void FitPreview()
    {
        if (PreviewImg.Source is not System.Windows.Media.Imaging.BitmapSource bmp || bmp.PixelWidth <= 0)
        {
            SetPreviewZoom(1);
            return;
        }
        var vw = PreviewScroll.ViewportWidth;
        var vh = PreviewScroll.ViewportHeight;
        if (vw <= 0 || vh <= 0)
        {
            SetPreviewZoom(1);
            return;
        }
        SetPreviewZoom(Math.Min(1, Math.Min(vw / bmp.PixelWidth, vh / bmp.PixelHeight)));
    }

    private void OnPreviewWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        SetPreviewZoom(_previewZoom * (e.Delta > 0 ? 1.15 : 1 / 1.15));
    }

    private void OnPreviewZoomIn(object sender, RoutedEventArgs e) => SetPreviewZoom(_previewZoom * 1.2);
    private void OnPreviewZoomOut(object sender, RoutedEventArgs e) => SetPreviewZoom(_previewZoom / 1.2);
    private void OnPreviewActual(object sender, RoutedEventArgs e) => SetPreviewZoom(1);
    private void OnPreviewFit(object sender, RoutedEventArgs e) => FitPreview();

    private void OnPreviewImageChanged(object sender, System.Windows.Data.DataTransferEventArgs e) =>
        Dispatcher.BeginInvoke(new Action(FitPreview), System.Windows.Threading.DispatcherPriority.Loaded);

    private void OnPreviewPanStart(object sender, MouseButtonEventArgs e)
    {
        _panning = true;
        _panStart = e.GetPosition(PreviewScroll);
        _panH = PreviewScroll.HorizontalOffset;
        _panV = PreviewScroll.VerticalOffset;
        PreviewScroll.CaptureMouse();
    }

    private void OnPreviewPanMove(object sender, MouseEventArgs e)
    {
        if (!_panning)
            return;
        var p = e.GetPosition(PreviewScroll);
        PreviewScroll.ScrollToHorizontalOffset(_panH - (p.X - _panStart.X));
        PreviewScroll.ScrollToVerticalOffset(_panV - (p.Y - _panStart.Y));
    }

    private void OnPreviewPanEnd(object sender, MouseButtonEventArgs e)
    {
        _panning = false;
        PreviewScroll.ReleaseMouseCapture();
    }

    // --- Drag-and-drop folders to add as watched roots ---

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnWindowDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;
        var folders = ((string[])e.Data.GetData(DataFormats.FileDrop))
            .Where(System.IO.Directory.Exists).ToArray();
        if (folders.Length > 0)
            ViewModel?.AddFolders(folders);
    }

    private void OnQuickLookBackgroundClick(object sender, MouseButtonEventArgs e)
    {
        // Close only when the dim background itself is clicked, not a button.
        if (ReferenceEquals(e.OriginalSource, sender))
            ViewModel?.CloseQuickLookCommand.Execute(null);
    }

    // --- Folder tree ---

    private void OnTreeSelectedChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is FolderNodeVm node)
            ViewModel?.NavigateToNode(node);
    }

    // --- Root alias inline rename (from the tree's root nodes) ---

    private static RootVm? RootOf(object? dataContext) =>
        dataContext as RootVm ?? (dataContext as FolderNodeVm)?.Root;

    private void OnAliasClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && sender is FrameworkElement fe && RootOf(fe.DataContext) is { } root)
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
        if (sender is not System.Windows.Controls.TextBox box || RootOf(box.DataContext) is not { } root)
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
        if (sender is FrameworkElement fe && RootOf(fe.DataContext) is { } root)
            root.IsEditing = false;
    }

    /// <summary>Populate the shell "Open with" submenu for the right-clicked tile.</summary>
    private void OnOpenWithSubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem menu
            || menu.DataContext is not TileVm tile)
            return;

        menu.Items.Clear();
        var path = tile.FullPath;

        foreach (var handler in Services.ShellOpenWith.GetHandlers(path))
        {
            var item = new System.Windows.Controls.MenuItem { Header = handler.Name };
            var captured = handler;
            item.Click += (_, _) => captured.Invoke(path);
            menu.Items.Add(item);
        }

        if (menu.Items.Count > 0)
            menu.Items.Add(new System.Windows.Controls.Separator());

        var chooser = new System.Windows.Controls.MenuItem { Header = "Choose another app…" };
        chooser.Click += (_, _) => Services.ShellOpenWith.ChooseAnotherApp(path);
        menu.Items.Add(chooser);
    }

    private void OnGridSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        ViewModel?.SetSelectedItems(ItemsList.SelectedItems);

    /// <summary>
    /// Explorer-style right-click: clicking a tile that isn't in the current selection
    /// selects just it; clicking within the selection keeps it, so the context menu
    /// (Tags &amp; notes) can act on every selected item.
    /// </summary>
    private void OnGridRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ContainerFrom(e.OriginalSource as DependencyObject) is not { } item)
            return;
        if (!item.IsSelected)
        {
            ItemsList.SelectedItems.Clear();
            item.IsSelected = true;
        }
    }

    private static System.Windows.Controls.ListBoxItem? ContainerFrom(DependencyObject? source)
    {
        while (source is not null and not System.Windows.Controls.ListBoxItem)
            source = VisualTreeHelper.GetParent(source);
        return source as System.Windows.Controls.ListBoxItem;
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
            else if (e.Key == Key.T)
            {
                ViewModel.EditAnnotationCommand.Execute(ViewModel.SelectedTile);
                e.Handled = true;
            }
            else if (e.Key == Key.Home)
            {
                MoveCursorAndScroll(ViewModel.MoveCursorToStart());
                e.Handled = true;
            }
            else if (e.Key == Key.End)
            {
                MoveCursorAndScroll(ViewModel.MoveCursorToEnd());
                e.Handled = true;
            }
            return; // leave Ctrl+arrows / Ctrl+wheel alone
        }

        if (e.Key is Key.Home or Key.End)
        {
            MoveCursorAndScroll(e.Key == Key.Home ? ViewModel.MoveCursorToStart() : ViewModel.MoveCursorToEnd());
            e.Handled = true;
            return;
        }

        // On a section header, Left/Right collapse/expand it (rather than moving the cursor).
        if (ViewModel.CursorIsHeader && (e.Key == Key.Left || e.Key == Key.Right))
        {
            ViewModel.SetCursorSectionExpanded(e.Key == Key.Right);
            ItemsList.Focus();
            e.Handled = true;
            return;
        }

        var direction = e.Key switch
        {
            Key.Left => "left",
            Key.Right => "right",
            Key.Up => "up",
            Key.Down => "down",
            _ => null,
        };

        if (direction is not null)
        {
            // Keyboard focus follows the scroll: if the cursor tile was scrolled out of
            // view (wheel, scrollbar, Ctrl+End…), re-anchor to a visible tile first so the
            // arrow doesn't jump back to wherever the cursor used to be.
            if (!ViewModel.CursorIsHeader && !IsSelectedVisible() && TopVisibleItem() is { } top)
                ViewModel.SetCursorToItem(top);

            var target = ViewModel.MoveGrid(direction, ColumnsPerRow());
            if (target is not null)
                try { ItemsList.ScrollIntoView(target); } catch { }
            ItemsList.Focus(); // keep focus on the grid so arrows keep firing (even on a header)
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            var target = ViewModel.ActivateCursor();
            if (target is not null)
                try { ItemsList.ScrollIntoView(target); } catch { }
            e.Handled = true;
        }
    }

    private void MoveCursorAndScroll(object? target)
    {
        if (target is not null)
            try { ItemsList.ScrollIntoView(target); } catch { }
        ItemsList.Focus();
    }

    /// <summary>True if the cursor's tile is among the grid's realized (roughly on-screen) containers.</summary>
    private bool IsSelectedVisible()
    {
        if (ViewModel?.SelectedItem is not { } sel)
            return false;
        var realized = new System.Collections.Generic.HashSet<object>();
        CollectRealizedItems(ItemsList, realized);
        return realized.Contains(sel);
    }

    private static void CollectRealizedItems(DependencyObject root, System.Collections.Generic.HashSet<object> set)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is System.Windows.Controls.ListBoxItem { IsVisible: true } li && li.DataContext is { } dc)
                set.Add(dc);
            else
                CollectRealizedItems(child, set);
        }
    }

    private object? TopVisibleItem()
    {
        for (double y = 6; y < ItemsList.ActualHeight - 4; y += 20)
        {
            var hit = VisualTreeHelper.HitTest(ItemsList, new Point(24, y))?.VisualHit as DependencyObject;
            while (hit is not null and not System.Windows.Controls.ListBoxItem)
                hit = VisualTreeHelper.GetParent(hit);
            if ((hit as System.Windows.Controls.ListBoxItem)?.DataContext is { } dc)
                return dc;
        }
        return null;
    }

    private int ColumnsPerRow()
    {
        var tileOuter = (ViewModel?.TileSize ?? 200) + 20; // tile + margin/padding/spacing
        var columns = (int)((ItemsList.ActualWidth - 12) / Math.Max(1, tileOuter));
        return Math.Max(1, columns);
    }
}
