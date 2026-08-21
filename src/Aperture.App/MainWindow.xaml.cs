using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Aperture.App.ViewModels;

namespace Aperture.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        PreviewKeyDown += OnWindowPreviewKeyDown;
        PreviewMouseDown += OnWindowPreviewMouseDown;
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    /// <summary>Restore window geometry before the window is shown, so there's no reposition flash.</summary>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (ViewModel?.GetWindowPlacement() is { Length: 5 } p)
            ApplyWindowPlacement(p);
        if (ViewModel?.GetNavWidth() is { } w && w >= NavColumn.MinWidth)
            NavColumn.Width = new GridLength(w);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
            return;

        _lastLocationKey = ViewModel.LocationKey;
        ViewModel.ViewApplying += OnViewApplying;
        ViewModel.ViewApplied += OnViewApplied;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ConfigurePreviewLayout(ViewModel.PreviewMode);

        // Land keyboard focus on the "Everything" library so a single Tab drops into the grid
        // without changing either pane's selection (ribbon is not a Tab stop).
        Dispatcher.BeginInvoke(new Action(() => EverythingButton.Focus()),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.PreviewMode) && ViewModel is not null)
            ConfigurePreviewLayout(ViewModel.PreviewMode);
    }

    private double _previewWidth = 380;
    private double _previewHeight = 340;

    /// <summary>
    /// Lays out the browser grid + preview pane as real, resizable grid cells so the
    /// thumbnail wrap panel reflows (and scrollbars reset) exactly like a window resize.
    /// A <see cref="GridSplitter"/> sits on the preview's left (right dock) or top (bottom dock).
    /// </summary>
    private void ConfigurePreviewLayout(PreviewMode mode)
    {
        // Remember the user's dragged size before we rebuild the definitions.
        if (ContentGrid.ColumnDefinitions.Count == 3 && ContentGrid.ColumnDefinitions[2].ActualWidth > 0)
            _previewWidth = ContentGrid.ColumnDefinitions[2].ActualWidth;
        if (ContentGrid.RowDefinitions.Count == 3 && ContentGrid.RowDefinitions[2].ActualHeight > 0)
            _previewHeight = ContentGrid.RowDefinitions[2].ActualHeight;

        ContentGrid.ColumnDefinitions.Clear();
        ContentGrid.RowDefinitions.Clear();

        if (mode == PreviewMode.Off)
        {
            ContentGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition());
            ContentGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition());
            System.Windows.Controls.Grid.SetColumn(BrowserHost, 0);
            System.Windows.Controls.Grid.SetRow(BrowserHost, 0);
            PreviewSplitter.Visibility = Visibility.Collapsed;
            PreviewPane.Visibility = Visibility.Collapsed;
            return;
        }

        PreviewSplitter.Visibility = Visibility.Visible;
        PreviewPane.Visibility = Visibility.Visible;

        if (mode == PreviewMode.Right)
        {
            ContentGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition());
            ContentGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 220 });
            ContentGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
            ContentGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(_previewWidth), MinWidth = 260 });

            Place(BrowserHost, 0, 0);
            Place(PreviewSplitter, 0, 1);
            Place(PreviewPane, 0, 2);

            PreviewSplitter.Width = 5;
            PreviewSplitter.Height = double.NaN;
            PreviewSplitter.HorizontalAlignment = HorizontalAlignment.Center;
            PreviewSplitter.VerticalAlignment = VerticalAlignment.Stretch;
            PreviewSplitter.ResizeDirection = System.Windows.Controls.GridResizeDirection.Columns;
            PreviewSplitter.Cursor = Cursors.SizeWE;
        }
        else // Bottom
        {
            ContentGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition());
            ContentGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 160 });
            ContentGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            ContentGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(_previewHeight), MinHeight = 200 });

            Place(BrowserHost, 0, 0);
            Place(PreviewSplitter, 1, 0);
            Place(PreviewPane, 2, 0);

            PreviewSplitter.Height = 5;
            PreviewSplitter.Width = double.NaN;
            PreviewSplitter.VerticalAlignment = VerticalAlignment.Center;
            PreviewSplitter.HorizontalAlignment = HorizontalAlignment.Stretch;
            PreviewSplitter.ResizeDirection = System.Windows.Controls.GridResizeDirection.Rows;
            PreviewSplitter.Cursor = Cursors.SizeNS;
        }
    }

    private static void Place(UIElement element, int row, int column)
    {
        System.Windows.Controls.Grid.SetRow(element, row);
        System.Windows.Controls.Grid.SetColumn(element, column);
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (ViewModel is null)
            return;

        if (TryGetWindowPlacement() is { } placement)
            ViewModel.SaveWindowPlacement(placement);
        ViewModel.SaveNavWidth(NavColumn.ActualWidth);
    }

    // --- Native window placement (robust across monitors + DPI) ---

    private const int SW_SHOWNORMAL = 1;
    private const int SW_SHOWMINIMIZED = 2;
    private const int SW_SHOWMAXIMIZED = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int length, flags, showCmd;
        public int minX, minY, maxX, maxY;
        public int left, top, right, bottom; // rcNormalPosition
    }

    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);
    [DllImport("user32.dll")] private static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    /// <summary>Current placement as [left, top, right, bottom, showCmd], or null if unavailable.</summary>
    private int[]? TryGetWindowPlacement()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return null;
        var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(hwnd, ref wp))
            return null;
        // Never persist "minimized" — restore to a normal window next launch.
        var show = wp.showCmd == SW_SHOWMINIMIZED ? SW_SHOWNORMAL : wp.showCmd;
        return [wp.left, wp.top, wp.right, wp.bottom, show];
    }

    private void ApplyWindowPlacement(int[] p)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;
        var wp = new WINDOWPLACEMENT
        {
            length = Marshal.SizeOf<WINDOWPLACEMENT>(),
            showCmd = p[4] == SW_SHOWMINIMIZED ? SW_SHOWNORMAL : p[4],
            left = p[0], top = p[1], right = p[2], bottom = p[3],
        };
        // SetWindowPlacement clamps to a visible monitor, so an unplugged screen is handled for us.
        SetWindowPlacement(hwnd, ref wp);
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
                case Key.Enter:
                    ViewModel.OpenSelectedCommand.Execute(null); // launch the viewed item, like a tile
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

        // F5 — reload + rebuild the grid (also the recovery from a wonky tile display).
        if (e.Key == Key.F5)
        {
            ViewModel.RefreshCommand.Execute(null);
            e.Handled = true;
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

        // Space: open the quick-look preview (on the selected tile, or the cursor section's first tile).
        // It never collapses a section — that's Left/Right on a header, the chevron, or a header click.
        // (Skip when a TextBox/ToggleButton has focus, or the folder tree — it toggles the tree node.)
        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None
            && focused is not System.Windows.Controls.TextBox
            && focused is not System.Windows.Controls.Primitives.ToggleButton
            && !IsInTree(focused as DependencyObject))
        {
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

    /// <summary>
    /// Tabbing into the tree must not change its selection. WPF TreeView focuses
    /// (and selects) the first node when nothing is selected — keep Everything /
    /// the current folder.
    /// </summary>
    private void OnTreePreviewGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is not System.Windows.Controls.TreeViewItem item)
            return;
        if (!LibraryPaneFocus.ShouldKeepExistingSelection(
                fromOutside: !IsInTree(e.OldFocus as DependencyObject),
                newFocusIsItem: true,
                newFocusIsSelected: item.IsSelected))
            return;

        e.Handled = true;
        if (FolderTreeView.SelectedItem is FolderNodeVm selected
            && FindTreeViewItem(FolderTreeView, selected) is { } keep)
            keep.Focus();
        else
            FolderTreeView.Focus();
    }

    private static System.Windows.Controls.TreeViewItem? FindTreeViewItem(
        System.Windows.Controls.ItemsControl parent, FolderNodeVm node)
    {
        if (parent.ItemContainerGenerator.ContainerFromItem(node) is System.Windows.Controls.TreeViewItem match)
            return match;
        var count = parent.Items.Count;
        for (var i = 0; i < count; i++)
        {
            if (parent.ItemContainerGenerator.ContainerFromIndex(i) is not System.Windows.Controls.TreeViewItem child)
                continue;
            if (FindTreeViewItem(child, node) is { } found)
                return found;
        }
        return null;
    }

    /// <summary>
    /// Tabbing into the grid must not change its selection. WPF ListBox focuses
    /// (and selects) the first tile when focus lands on an unselected container.
    /// </summary>
    private void OnGridPreviewGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is not System.Windows.Controls.ListBoxItem item)
            return;
        if (!LibraryPaneFocus.ShouldKeepExistingSelection(
                fromOutside: !IsDescendantOf(e.OldFocus as DependencyObject, ItemsList),
                newFocusIsItem: true,
                newFocusIsSelected: item.IsSelected))
            return;

        e.Handled = true;
        if (ItemsList.SelectedItem is { } selected
            && ItemsList.ItemContainerGenerator.ContainerFromItem(selected) is System.Windows.Controls.ListBoxItem keep)
            keep.Focus();
        else
            ItemsList.Focus();
    }

    private void OnSearchFocus(object sender, RoutedEventArgs e) => ViewModel?.OpenQuickPicks();

    private void OnSearchBlur(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Don't close if focus moved onto a quick-pick chip — let its command run.
        if (e.NewFocus is DependencyObject d && IsDescendantOf(d, QuickPickPanel))
            return;
        ViewModel?.CloseQuickPicks();
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ViewModel?.CommitSearch(); // apply the filter now instead of waiting out the debounce
            e.Handled = true;
        }
    }

    /// <summary>Any click outside the search box (and its popup) dismisses the tag quick-picks.</summary>
    private void OnWindowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is not { QuickPicksOpen: true })
            return;
        // Keep the picker open when the click is in the search box or on a quick-pick
        // chip (the popup) — otherwise the chip's command never runs.
        var src = e.OriginalSource as DependencyObject;
        if (!IsDescendantOf(src, SearchBorder) && !IsDescendantOf(src, QuickPickPanel))
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
    private bool _panDragged;
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
        _panDragged = false;
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
        if (Math.Abs(p.X - _panStart.X) > 3 || Math.Abs(p.Y - _panStart.Y) > 3)
            _panDragged = true;
        PreviewScroll.ScrollToHorizontalOffset(_panH - (p.X - _panStart.X));
        PreviewScroll.ScrollToVerticalOffset(_panV - (p.Y - _panStart.Y));
    }

    private void OnPreviewPanEnd(object sender, MouseButtonEventArgs e)
    {
        var wasPanning = _panning;
        _panning = false;
        PreviewScroll.ReleaseMouseCapture();
        // A click on the preview canvas (no drag) dismisses the pane; a drag pans the zoomed image.
        if (wasPanning && !_panDragged)
            ViewModel?.ClosePreviewCommand.Execute(null);
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
        // Left-click on the dim background (not a button/image) closes. Right-click must
        // NOT close — it opens the image's context menu instead.
        if (e.ChangedButton == MouseButton.Left && ReferenceEquals(e.OriginalSource, sender))
            ViewModel?.CloseQuickLookCommand.Execute(null);
    }

    /// <summary>
    /// Quick-look image clicks: double-click launches the item; a single click on the image
    /// itself is swallowed (so the double-click can land); a single click in the letterbox
    /// around the image dismisses the overlay, just like clicking the outer margin.
    /// </summary>
    private void OnQuickLookImageClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ViewModel?.OpenSelectedCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (!ClickIsOnImage(e.GetPosition(QuickLookImageArea)))
            ViewModel?.CloseQuickLookCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>True if a point (in QuickLookImageArea coordinates) lands on the letterboxed image itself.</summary>
    private bool ClickIsOnImage(Point p)
    {
        if (QuickLookImg.Source is not System.Windows.Media.Imaging.BitmapSource bmp || bmp.PixelWidth <= 0)
            return false;
        double sw = QuickLookImageArea.ActualWidth, sh = QuickLookImageArea.ActualHeight;
        if (sw <= 0 || sh <= 0)
            return false;
        var scale = Math.Min(sw / bmp.PixelWidth, sh / bmp.PixelHeight);
        double dw = bmp.PixelWidth * scale, dh = bmp.PixelHeight * scale;
        double ox = (sw - dw) / 2, oy = (sh - dh) / 2;
        return p.X >= ox && p.X <= ox + dw && p.Y >= oy && p.Y <= oy + dh;
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
        if (sender is not System.Windows.Controls.MenuItem menu)
            return;
        // Grid menu: DataContext is the tile. Preview menu: DataContext is the VM,
        // so fall back to the previewed item.
        var tile = menu.DataContext as TileVm ?? ViewModel?.PreviewTile;
        if (tile is null)
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

    /// <summary>Closes the settings/overflow popup after Refresh runs (it opens no dialog to steal focus).</summary>
    private void OnRefreshMenuClick(object sender, RoutedEventArgs e) => SettingsToggle.IsChecked = false;

    /// <summary>Hamburger opened: refresh Start-with-Windows from the Run key and check GitHub for updates.</summary>
    private void OnSettingsOpened(object sender, EventArgs e) => ViewModel?.OnSettingsOpened();

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

    /// <summary>
    /// The tag/note badge acts on its own tile only: reset the selection to that tile, then open the
    /// dialog. Multi-select edits go through the right-click "Tags &amp; notes…" menu instead.
    /// </summary>
    private void OnTileBadgeClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: TileVm tile } && ViewModel is not null)
        {
            ItemsList.SelectedItems.Clear();
            ItemsList.SelectedItem = tile;
            ViewModel.SetSelectedItems(ItemsList.SelectedItems);
            ViewModel.EditAnnotationCommand.Execute(tile);
        }
    }

    /// <summary>
    /// Empty-canvas click in the tile pane: move keyboard focus onto the grid so
    /// arrows / PageUp / PageDown / scroll keys apply here, not the folder tree.
    /// Tile and section-header clicks already focus themselves; text boxes and
    /// dialogs are left alone.
    /// </summary>
    private void OnContentsPanePreviewLeftDown(object sender, MouseButtonEventArgs e)
    {
        if (LibraryPaneFocus.ShouldFocusContentsPane(
                e.OriginalSource as DependencyObject, Keyboard.FocusedElement))
            ItemsList.Focus();
    }

    // --- Drag a tile (or the whole selection) out to the shell as real file(s) ---
    // Lets you drop images straight onto an email, a folder, or any file-drop target.
    private Point _tileDragStart;
    private bool _tileDragArmed;
    private string[] _pendingDragPaths = [];

    private void OnGridPreviewLeftDown(object sender, MouseButtonEventArgs e)
    {
        _tileDragArmed = false;
        _pendingDragPaths = [];

        // Arm only when the press lands on a media tile — not a header, empty space, or a
        // button (the tag badge handles its own click). Selection still happens normally.
        if (ContainerFrom(e.OriginalSource as DependencyObject) is not { DataContext: TileVm pressed } container
            || IsWithinButton(e.OriginalSource as DependencyObject))
            return;

        // Capture the drag set BEFORE WPF collapses the selection on mouse-down: pressing an
        // already-selected tile drags the whole selection (Explorer-style); otherwise just this one.
        var selected = ItemsList.SelectedItems.OfType<TileVm>().ToList();
        var basis = container.IsSelected && selected.Count > 1 ? selected : [pressed];
        _pendingDragPaths = basis
            .Select(t => t.FullPath)
            .Where(System.IO.File.Exists)
            .Distinct(System.StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _tileDragStart = e.GetPosition(null);
        _tileDragArmed = _pendingDragPaths.Length > 0;
    }

    private void OnGridPreviewMove(object sender, MouseEventArgs e)
    {
        if (!_tileDragArmed || e.LeftButton != MouseButtonState.Pressed)
            return;

        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _tileDragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(pos.Y - _tileDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _tileDragArmed = false; // one drag per press
        var data = new DataObject(DataFormats.FileDrop, _pendingDragPaths);
        try { DragDrop.DoDragDrop(ItemsList, data, DragDropEffects.Copy); }
        catch { /* the drag loop can throw if the window is torn down mid-drag */ }
    }

    private static bool IsWithinButton(DependencyObject? source)
    {
        while (source is not null and not System.Windows.Controls.ListBoxItem)
        {
            if (source is System.Windows.Controls.Primitives.ButtonBase)
                return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
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

    /// <summary>Last visual column walked by Up/Down / Page — used when leaving a section header.</summary>
    private int _lastNavCol;

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
                JumpCursor(toEnd: false);
                e.Handled = true;
            }
            else if (e.Key == Key.End)
            {
                JumpCursor(toEnd: true);
                e.Handled = true;
            }
            return; // leave Ctrl+arrows / Ctrl+wheel alone
        }

        if (e.Key is Key.Home or Key.End)
        {
            JumpCursor(e.Key == Key.End);
            e.Handled = true;
            return;
        }

        if (e.Key is Key.PageUp or Key.PageDown)
        {
            MoveGridPage(e.Key == Key.PageDown ? +1 : -1);
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

        if (e.Key is Key.Up or Key.Down)
        {
            // Keyboard focus follows the scroll: if the cursor tile was scrolled out of
            // view (wheel, scrollbar, Ctrl+End…), re-anchor to a visible tile first so the
            // arrow doesn't jump back to wherever the cursor used to be.
            if (!ViewModel.CursorIsHeader && !IsSelectedVisible() && TopVisibleItem() is { } top)
                ViewModel.SetCursorToItem(top);

            MoveGridVertical(e.Key == Key.Down ? +1 : -1);
            ItemsList.Focus();
            e.Handled = true;
            return;
        }

        var direction = e.Key switch
        {
            Key.Left => "left",
            Key.Right => "right",
            _ => null,
        };

        if (direction is not null)
        {
            if (!ViewModel.CursorIsHeader && !IsSelectedVisible() && TopVisibleItem() is { } top)
                ViewModel.SetCursorToItem(top);

            var target = ViewModel.MoveGrid(direction, ColumnsPerRow());
            if (target is not null)
                try { ItemsList.ScrollIntoView(target); } catch { }
            if (ViewModel.CursorIsHeader)
                BringHeaderIntoView(); // a collapsed section title still scrolls into view
            ItemsList.Focus(); // keep focus on the grid so arrows keep firing (even on a header)
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            // Enter opens the quick-look preview (like Space) rather than collapsing the cursor's section.
            ViewModel.OpenQuickLookCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Same-column Up/Down. Headers are not stops — the next/prev section's tile
    /// in this column is. Empty cells (short last row) fall back to the nearest
    /// tile in the column, then the nearest in the row.
    /// </summary>
    private void MoveGridVertical(int sign)
    {
        if (ViewModel is null || !TryBuildCells(out var cells, out var items))
            return;

        int? pick;
        if (ViewModel.CursorIsHeader)
            pick = PickFromHeader(cells, sign);
        else if (CurrentCellIndex(items) is { } cur)
            pick = TileSpatialNavigation.PickVertical(cells, cur, sign);
        else
            pick = items.Count > 0 ? 0 : null;

        ApplyTilePick(cells, items, pick, scrollIntoView: true);
    }

    /// <summary>
    /// Scroll roughly one viewport, then select the tile in the same column at
    /// the same screen Y. Not skip-N-items and not a jump onto a section header.
    /// </summary>
    private void MoveGridPage(int sign)
    {
        if (ViewModel is null)
            return;

        var sv = GridScroll();
        var viewH = sv?.ViewportHeight ?? 0;
        if (viewH <= 0)
            viewH = Math.Max(ItemsList.ActualHeight, 1);

        if (!TryBuildCells(out var cells, out var items))
            return;

        var cur = CurrentCellIndex(items);
        if (cur is null && ViewModel.CursorIsHeader)
            cur = PickFromHeader(cells, sign);
        if (cur is null)
            return;

        var offset = sv?.VerticalOffset ?? 0;
        var pick = TileSpatialNavigation.PickPage(cells, cur.Value, offset, viewH, sign);

        // Prefer the realized tile's viewport slot so refine survives header-height drift.
        var (anchorX, anchorViewportY) = ViewportAnchor(items[cur.Value], cells[cur.Value], offset);

        var max = Math.Max(0, (sv?.ExtentHeight ?? 0) - viewH);
        sv?.ScrollToVerticalOffset(Math.Clamp(offset + sign * viewH, 0, max));

        ApplyTilePick(cells, items, pick, scrollIntoView: false);
        ItemsList.Focus();

        // After the panel lays out the new page, land on the realized tile in
        // the same column at the same screen Y (layout Y can drift vs. scroll).
        Dispatcher.BeginInvoke(
            new Action(() => RefinePageSelection(anchorX, anchorViewportY)),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private (double X, double Y) ViewportAnchor(
        IGridItem current, TileSpatialNavigation.Cell estimated, double scrollOffset)
    {
        var realized = CollectRealizedCells();
        for (var i = 0; i < realized.Items.Count; i++)
        {
            if (ReferenceEquals(realized.Items[i], current))
                return (realized.Cells[i].CenterX, realized.Cells[i].CenterY);
        }
        return (estimated.CenterX, estimated.CenterY - scrollOffset);
    }

    private void RefinePageSelection(double columnX, double viewportY)
    {
        if (ViewModel is null)
            return;
        var realized = CollectRealizedCells();
        if (realized.Cells.Count == 0)
            return;
        var pick = TileSpatialNavigation.PickAtColumn(realized.Cells, columnX, viewportY);
        if (pick is int i)
            ApplyTilePick(realized.Cells, realized.Items, i, scrollIntoView: false);
    }

    private int? PickFromHeader(IReadOnlyList<TileSpatialNavigation.Cell> cells, int sign)
    {
        if (ViewModel is null || cells.Count == 0)
            return null;

        var first = ViewModel.FirstVisibleTileIndex(ViewModel.CursorSection);
        if (sign > 0)
        {
            if (first is not int f || f >= cells.Count)
                return null;
            return CellInRowNearestColumn(cells, cells[f].Row, _lastNavCol) ?? f;
        }

        // Up: last row of the previous section, same column, else nearest in that row.
        if (first is not int start || start <= 0)
            return null;
        return CellInRowNearestColumn(cells, cells[start - 1].Row, _lastNavCol) ?? start - 1;
    }

    private static int? CellInRowNearestColumn(
        IReadOnlyList<TileSpatialNavigation.Cell> cells, int row, int col)
    {
        var rowCells = cells.Where(c => c.Row == row).ToList();
        if (rowCells.Count == 0)
            return null;
        var same = rowCells.FirstOrDefault(c => c.Col == col);
        if (same.Width > 0)
            return same.Index;
        return rowCells.MinBy(c => (Math.Abs(c.Col - col), c.Index)).Index;
    }

    private void ApplyTilePick(
        IReadOnlyList<TileSpatialNavigation.Cell> cells,
        IReadOnlyList<IGridItem> items,
        int? pick,
        bool scrollIntoView)
    {
        if (ViewModel is null || pick is not int i || i < 0 || i >= items.Count)
            return;
        _lastNavCol = cells[i].Col;
        ViewModel.SetCursorToItem(items[i]);
        if (scrollIntoView)
        {
            try { ItemsList.ScrollIntoView(items[i]); } catch { /* virtualized generator can throw mid-refresh */ }
        }
    }

    private bool TryBuildCells(
        out IReadOnlyList<TileSpatialNavigation.Cell> cells,
        out IReadOnlyList<IGridItem> items)
    {
        cells = [];
        items = [];
        if (ViewModel is null)
            return false;

        var layout = ViewModel.GetVisibleTileLayout();
        items = layout.Tiles;
        if (items.Count == 0)
            return false;

        var columns = VisualColumnCount();
        var (tileW, tileH) = MeasureTileSize();
        var headerH = layout.HasHeaders ? MeasureSectionHeaderHeight() : 0;
        cells = TileSpatialNavigation.LayoutWrap(layout.SectionCounts, columns, tileW, tileH, headerH);
        return cells.Count > 0;
    }

    private int? CurrentCellIndex(IReadOnlyList<IGridItem> items)
    {
        if (ViewModel?.SelectedItem is not IGridItem sel)
            return null;
        for (var i = 0; i < items.Count; i++)
        {
            if (ReferenceEquals(items[i], sel))
                return i;
        }
        return null;
    }

    private (IReadOnlyList<TileSpatialNavigation.Cell> Cells, IReadOnlyList<IGridItem> Items) CollectRealizedCells()
    {
        var cells = new List<TileSpatialNavigation.Cell>();
        var items = new List<IGridItem>();
        CollectRealizedTileRects(ItemsList, ItemsList, cells, items);
        return (cells, items);
    }

    private static void CollectRealizedTileRects(
        DependencyObject node,
        UIElement origin,
        List<TileSpatialNavigation.Cell> cells,
        List<IGridItem> items)
    {
        if (node is System.Windows.Controls.ListBoxItem { IsVisible: true, DataContext: IGridItem item } li)
        {
            // Origin is the ListBox: coordinates are viewport-relative (scroll already applied).
            var tl = li.TranslatePoint(new Point(0, 0), origin);
            cells.Add(new TileSpatialNavigation.Cell(
                cells.Count, Row: 0, Col: 0, tl.X, tl.Y, li.ActualWidth, li.ActualHeight));
            items.Add(item);
            return;
        }

        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
            CollectRealizedTileRects(VisualTreeHelper.GetChild(node, i), origin, cells, items);
    }

    private int VisualColumnCount()
    {
        var realized = new List<Rect>();
        CollectRealizedItemRects(ItemsList, ItemsList, realized);
        if (realized.Count > 0)
        {
            var itemW = realized.Max(r => r.Width);
            var fitted = (int)((ItemsList.ActualWidth - 12) / Math.Max(1, itemW));
            var fullestRow = realized
                .GroupBy(r => Math.Round(r.Y / 8.0) * 8.0)
                .Select(g => g.Count())
                .DefaultIfEmpty(1)
                .Max();
            return Math.Max(1, Math.Max(fullestRow, fitted));
        }
        return ColumnsPerRow();
    }

    private (double Width, double Height) MeasureTileSize()
    {
        var realized = new List<Rect>();
        CollectRealizedItemRects(ItemsList, ItemsList, realized);
        if (realized.Count > 0)
            return (realized[0].Width, realized[0].Height);
        var outer = (ViewModel?.TileSize ?? 200) + 20;
        return (outer, outer + 18);
    }

    private double MeasureSectionHeaderHeight()
    {
        var header = FindFirstSectionHeader(ItemsList);
        if (header is { ActualHeight: > 0 } h)
            return h.ActualHeight + h.Margin.Top + h.Margin.Bottom;
        return 44;
    }

    private static System.Windows.Controls.Primitives.ToggleButton? FindFirstSectionHeader(DependencyObject node)
    {
        if (node is System.Windows.Controls.Primitives.ToggleButton header
            && header.TemplatedParent is System.Windows.Controls.GroupItem)
            return header;

        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            if (FindFirstSectionHeader(VisualTreeHelper.GetChild(node, i)) is { } found)
                return found;
        }
        return null;
    }

    private static void CollectRealizedItemRects(DependencyObject node, UIElement origin, List<Rect> rects)
    {
        if (node is System.Windows.Controls.ListBoxItem { IsVisible: true, DataContext: IGridItem } li)
        {
            rects.Add(new Rect(li.TranslatePoint(new Point(0, 0), origin), li.RenderSize));
            return;
        }

        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
            CollectRealizedItemRects(VisualTreeHelper.GetChild(node, i), origin, rects);
    }

    /// <summary>Home/End: move the cursor to the first/last stop and scroll the grid to that extreme.</summary>
    private void JumpCursor(bool toEnd)
    {
        _ = toEnd ? ViewModel!.MoveCursorToEnd() : ViewModel!.MoveCursorToStart();
        var sv = GridScroll();
        if (toEnd)
            sv?.ScrollToBottom();
        else
            sv?.ScrollToTop();
        if (ViewModel.CursorIsHeader)
            BringHeaderIntoView();
        ItemsList.Focus();
    }

    /// <summary>Scrolls the cursor's section header (which may be a collapsed, item-less section) into view.</summary>
    private void BringHeaderIntoView()
    {
        if (ViewModel?.CursorSection is not { } section)
            return;
        Dispatcher.BeginInvoke(
            new Action(() => FindGroupItem(ItemsList, section)?.BringIntoView()),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static System.Windows.Controls.GroupItem? FindGroupItem(DependencyObject root, object section)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is System.Windows.Controls.GroupItem gi
                && ReferenceEquals((gi.Content as System.Windows.Data.CollectionViewGroup)?.Name, section))
                return gi;
            if (FindGroupItem(child, section) is { } found)
                return found;
        }
        return null;
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
