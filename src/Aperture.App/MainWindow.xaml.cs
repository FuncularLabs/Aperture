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
        DataContextChanged += OnDataContextChanged;
        // The date grouping depends on how many tiles fit across (DateBuckets.ChooseMode): report the width when the grid is
        // resized, its first layout included. GroupingWidth reads the grid's scroll viewer, whose width changes only with
        // the grid's (and is laid out by then), never its viewport: the scrollbar coming or going leaves it as it was.
        ItemsList.SizeChanged += (_, _) => ViewModel?.SetGridWidth(GroupingWidth());
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    /// <summary>Raised first when the window starts closing — before its own Closing handler saves anything.</summary>
    public event EventHandler? ClosingStarted;

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        ClosingStarted?.Invoke(this, EventArgs.Empty);
        base.OnClosing(e); // raises Closing: OnClosing(sender, e) below saves the placement and the pane width
    }

    /// <summary>
    /// A catalog switch gives the window a new view model. Move the handlers <see cref="OnLoaded"/> attached
    /// from the old view model to the new one, and forget remembered scroll positions — they're keyed by
    /// per-library root ids, so one library's offsets would land on another library's folders.
    /// </summary>
    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainViewModel old)
        {
            old.ViewApplying -= OnViewApplying;
            old.ViewApplied -= OnViewApplied;
            old.PropertyChanged -= OnViewModelPropertyChanged;
        }
        OnQuickLookImageChanged(null); // another library: quick view starts closed, with nothing zoomed
        if (!IsLoaded || e.NewValue is not MainViewModel vm)
            return; // the first view model is wired up in OnLoaded

        _scrollByLocation.Clear();
        _lastLocationKey = vm.LocationKey;
        vm.SetGridWidth(GroupingWidth()); // the grid keeps its size across a switch: no SizeChanged to tell it
        vm.ViewApplying += OnViewApplying;
        vm.ViewApplied += OnViewApplied;
        vm.PropertyChanged += OnViewModelPropertyChanged;
        ConfigurePreviewLayout(vm.PreviewMode);
        Dispatcher.BeginInvoke(new Action(FocusTreeWithoutChangingSelection),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Restore window geometry before the window is shown, so there's no reposition flash.</summary>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (ViewModel?.GetWindowPlacement() is { Length: 5 } p)
            ApplyWindowPlacement(p);
        if (ViewModel?.GetNavWidth() is { } w && w >= NavColumn.MinWidth)
            NavColumn.Width = new GridLength(w);

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero && HwndSource.FromHwnd(hwnd) is { } source)
            source.AddHook(WndProc);
    }

    /// <summary>
    /// Explorer-style: the click that activates Aperture is also delivered to
    /// the folder / tile / canvas under the cursor. Default Win32/WPF can
    /// return MA_ACTIVATEANDEAT and eat that first click.
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (!LibraryPaneFocus.TryPassActivatingClick(msg, out var result))
            return IntPtr.Zero;
        handled = true;
        return result;
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

        // Land keyboard focus on the folder tree so Tab cycles tree ↔ tile list
        // without changing either pane's selection (ribbon is not a Tab stop).
        Dispatcher.BeginInvoke(new Action(FocusTreeWithoutChangingSelection),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.PreviewMode) && ViewModel is not null)
            ConfigurePreviewLayout(ViewModel.PreviewMode);
        if (e.PropertyName == nameof(MainViewModel.QuickLookImage) && ViewModel is not null)
            OnQuickLookImageChanged(ViewModel.QuickLookImage);
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
            if (QuickLookKey(e.Key, Keyboard.Modifiers))
                e.Handled = true;
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

        // Tab / Shift+Tab: only the folder tree and the tile list. Ribbon stays out.
        // Two stops — either direction toggles. Do not change the destination selection.
        if (e.Key == Key.Tab && (Keyboard.Modifiers & ~ModifierKeys.Shift) == 0)
        {
            if (ViewModel.ShowFirstRun)
                return; // first-run overlay owns Tab
            if (SettingsToggle.IsChecked == true)
                return; // hamburger menu owns Tab
            if (focused is System.Windows.Controls.TextBox tb && !ReferenceEquals(tb, SearchBox))
                return; // alias rename / caption field — leave caret Tab alone
            if (focused is DependencyObject d && System.Windows.Window.GetWindow(d) is not MainWindow)
                return;

            e.Handled = true;
            if (IsInTree(focused as DependencyObject))
                FocusTileListWithoutChangingSelection();
            else
                FocusTreeWithoutChangingSelection();
            return;
        }

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
    /// Same-click focus onto a folder row so Up/Down move folders immediately
    /// when the tree was not the keyboard pane. Do not handle the event —
    /// selection still happens. Focusing a TreeViewItem selects the node —
    /// skip that for the expand chevron and the include checkbox so those
    /// hits stay off selection (Explorer).
    /// </summary>
    private void OnTreePreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var action = LibraryPaneFocus.OnTreePreviewClick(e.OriginalSource as DependencyObject);
        if (action == LibraryPaneFocus.TreeClickAction.Leave)
            return;
        if (action == LibraryPaneFocus.TreeClickAction.FocusItem)
        {
            var src = e.OriginalSource as DependencyObject;
            while (src is not null && !ReferenceEquals(src, FolderTreeView))
            {
                if (src is System.Windows.Controls.TreeViewItem item)
                {
                    item.Focus();
                    return;
                }
                src = src is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(src)
                    : LogicalTreeHelper.GetParent(src);
            }
        }
        FolderTreeView.Focus();
    }

    /// <summary>Everything is click-only (not a Tab stop). After Home, park keyboard on the tree.</summary>
    private void OnEverythingClick(object sender, RoutedEventArgs e) =>
        FolderTreeView.Focus();

    /// <summary>
    /// Tabbing into the tree must not change its selection. WPF TreeView focuses
    /// (and selects) the first node when nothing is selected — keep Everything /
    /// the current folder. A row click already focused the item in PreviewMouseDown.
    /// </summary>
    private void OnTreePreviewGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // WPF TreeView.Focus() hops onto the first item with OldFocus=TreeView
        // (which IsInTree). Treat that hop as outside so Tab onto Everything does
        // not select the first folder.
        var action = LibraryPaneFocus.OnTreeKeyboardArrival(
            fromOutside: !IsInTree(e.OldFocus as DependencyObject)
                || ReferenceEquals(e.OldFocus, FolderTreeView),
            mousePressed: Mouse.LeftButton == MouseButtonState.Pressed,
            newFocusIsItem: e.NewFocus is System.Windows.Controls.TreeViewItem,
            newFocusIsSelected: e.NewFocus is System.Windows.Controls.TreeViewItem { IsSelected: true },
            hasSelectedItem: FolderTreeView.SelectedItem is FolderNodeVm);
        if (action == LibraryPaneFocus.TreeFocusAction.Leave)
            return;

        e.Handled = true;
        if (action == LibraryPaneFocus.TreeFocusAction.FocusSelectedItem)
            FocusTreeWithoutChangingSelection();
        // FocusTree: swallow the hop onto the first folder; the TreeView keeps focus.
    }

    private void FocusTreeWithoutChangingSelection()
    {
        if (FolderTreeView.SelectedItem is FolderNodeVm selected
            && FindTreeViewItem(FolderTreeView, selected) is { } keep)
            keep.Focus();
        else
            FolderTreeView.Focus();
    }

    private void FocusTileListWithoutChangingSelection()
    {
        if (ItemsList.SelectedItem is { } selected
            && ItemsList.ItemContainerGenerator.ContainerFromItem(selected) is System.Windows.Controls.ListBoxItem keep)
            keep.Focus();
        else
            ItemsList.Focus();
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

    private static T? FindDescendant<T>(DependencyObject root, Func<T, bool>? where = null) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match && (where is null || where(match)))
                return match;
            if (FindDescendant(child, where) is { } found)
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
        // Once the drop has returned: adding asks about subfolders, and a dialog shown inside the drop would keep the
        // window the folders were dragged from (Explorer) waiting until it was answered.
        if (folders.Length > 0)
            Dispatcher.BeginInvoke(new Action(() => ViewModel?.AddFolders(folders)));
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
    /// itself is swallowed (so the double-click can land), and on a zoomed image starts a drag;
    /// a single click in the letterbox around the image dismisses the overlay, just like
    /// clicking the outer margin.
    /// </summary>
    private void OnQuickLookImageClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (e.ClickCount == 2)
        {
            ViewModel?.OpenSelectedCommand.Execute(null);
            return;
        }
        QuickLookPress(e.GetPosition(QuickLookImageArea));
        if (_quickPanning)
            QuickLookImageArea.CaptureMouse(); // the drag goes on wherever the pointer goes
    }

    private void OnQuickLookImageMove(object sender, MouseEventArgs e) => QuickLookDrag(e.GetPosition(QuickLookImageArea));

    private void OnQuickLookImageUp(object sender, MouseButtonEventArgs e) => QuickLookRelease();

    private void OnQuickLookImageLostCapture(object sender, MouseEventArgs e) => QuickLookRelease();

    // --- Quick look: zoom + pan (Ctrl+wheel at the pointer, Ctrl +/− about the centre, Ctrl+0 to fit; drag a zoomed
    // photo), as Photos and image editors do (Paul, 2026-11-09). The entry points take the modifier keys and the pointer
    // as arguments, so tests can call them as the handlers do. ---

    private readonly ZoomPan _quickZoom = new();
    private bool _quickPanning;
    private Point _quickPanLast;

    /// <summary>Quick view's zoom and pan (for tests).</summary>
    internal ZoomPan QuickLookZoom => _quickZoom;

    /// <summary>Quick view's keys — it owns the keyboard while it's open. True if <paramref name="key"/> was one of them.</summary>
    internal bool QuickLookKey(Key key, ModifierKeys modifiers)
    {
        if (ViewModel is not { } vm)
            return false;
        if ((modifiers & ModifierKeys.Control) != 0)
        {
            Action? zoom = key switch
            {
                Key.OemPlus or Key.Add => _quickZoom.ZoomIn,
                Key.OemMinus or Key.Subtract => _quickZoom.ZoomOut,
                Key.D0 or Key.NumPad0 => _quickZoom.Reset,
                _ => null,
            };
            if (zoom is not null)
            {
                zoom();
                ApplyQuickLookZoom();
                return true;
            }
        }
        switch (key) // with or without Ctrl, as before zoom
        {
            case Key.Escape or Key.Space:
                vm.CloseQuickLookCommand.Execute(null);
                return true;
            case Key.Enter:
                vm.OpenSelectedCommand.Execute(null); // launch the viewed item, like a tile
                return true;
            case Key.Left:
                vm.QuickLookPrevCommand.Execute(null);
                return true;
            case Key.Right:
                vm.QuickLookNextCommand.Execute(null);
                return true;
            default:
                return false;
        }
    }

    private void OnQuickLookWheel(object sender, MouseWheelEventArgs e)
    {
        if (QuickLookWheel(e.Delta, e.GetPosition(QuickLookImageArea), Keyboard.Modifiers))
            e.Handled = true;
    }

    /// <summary>Ctrl+wheel zooms quick view at the pointer (<paramref name="at"/>, in its image area); a plain wheel doesn't.</summary>
    internal bool QuickLookWheel(int delta, Point at, ModifierKeys modifiers)
    {
        if ((modifiers & ModifierKeys.Control) == 0)
            return false;
        _quickZoom.ZoomByWheel(delta, at.X, at.Y);
        ApplyQuickLookZoom();
        return true;
    }

    /// <summary>
    /// A press in quick view's image area at <paramref name="at"/>: beside the photo as it's shown, it closes quick view;
    /// on a zoomed photo, it starts a drag.
    /// </summary>
    internal void QuickLookPress(Point at)
    {
        if (!_quickZoom.Contains(at.X, at.Y))
        {
            ViewModel?.CloseQuickLookCommand.Execute(null);
            return;
        }
        if (!_quickZoom.IsZoomed)
            return;
        _quickPanning = true;
        _quickPanLast = at;
    }

    /// <summary>The pointer at <paramref name="at"/>: a drag that's on moves the photo as far.</summary>
    internal void QuickLookDrag(Point at)
    {
        if (!_quickPanning)
            return;
        _quickZoom.PanBy(at.X - _quickPanLast.X, at.Y - _quickPanLast.Y);
        _quickPanLast = at;
        ApplyQuickLookZoom();
    }

    /// <summary>The button released (or the pointer lost): a drag ends.</summary>
    internal void QuickLookRelease()
    {
        if (!_quickPanning)
            return;
        _quickPanning = false;
        QuickLookImageArea.ReleaseMouseCapture();
    }

    private void OnQuickLookAreaSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _quickZoom.SetView(e.NewSize.Width, e.NewSize.Height);
        ApplyQuickLookZoom();
    }

    // Another photo, or none (moving on, closing): it opens fitted. A sharper copy of the photo shown changes nothing.
    private void OnQuickLookImageChanged(System.Windows.Media.Imaging.BitmapSource? image)
    {
        if (image is null)
        {
            QuickLookRelease();
            _quickZoom.Clear();
        }
        else
        {
            _quickZoom.SetImage(image.PixelWidth, image.PixelHeight);
        }
        ApplyQuickLookZoom();
    }

    // Draws the photo as the zoom says: the Image lays the photo out fitted, and its transforms scale it and move it from
    // there. Zoomed past what the image shows holds, it asks for the photo in full.
    private void ApplyQuickLookZoom()
    {
        QuickLookScale.ScaleX = QuickLookScale.ScaleY = _quickZoom.Zoom;
        QuickLookShift.X = _quickZoom.Left - _quickZoom.FitLeft;
        QuickLookShift.Y = _quickZoom.Top - _quickZoom.FitTop;
        QuickLookImageArea.Cursor = _quickZoom.IsZoomed ? Cursors.SizeAll : Cursors.Hand;
        if (ViewModel?.QuickLookImage is { } shown
            && _quickZoom.ShowsMoreThan(shown.PixelWidth, VisualTreeHelper.GetDpi(QuickLookImg).DpiScaleX))
            ViewModel.LoadQuickLookDetail();
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

    /// <summary>
    /// The grid has one tile menu. It opens on a tile — the one under the pointer, or the focused one for the menu
    /// key or Shift+F10 — and acts on it: the tile becomes the menu's DataContext. Not on empty canvas or a section
    /// header.
    /// </summary>
    private void OnGridContextMenuOpening(object sender, System.Windows.Controls.ContextMenuEventArgs e)
    {
        if (ContainerFrom(e.OriginalSource as DependencyObject)?.DataContext is not TileVm tile)
        {
            e.Handled = true;
            return;
        }
        ItemsList.ContextMenu.DataContext = tile;
    }

    /// <summary>Lets go of the tile once the menu has closed, so it doesn't outlive a library switch.</summary>
    private void OnGridContextMenuClosed(object sender, RoutedEventArgs e) =>
        ItemsList.ContextMenu.DataContext = null;

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

        // Same-click focus when the grid was not the keyboard pane (tree had focus,
        // or the window just activated). Do not handle — selection and drag still run.
        container.Focus();

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

        var realized = CollectRealizedByItem();
        var (tileW, tileH, slotW, _) = MeasureTileSlot(realized.Values);
        var viewportW = WrapViewportWidth();
        var observedXs = TileSpatialNavigation.DistinctColumnXs(
            realized.Values.Select(c => c.X).ToList(), slop: Math.Max(tileW * 0.5, 1));
        var fitted = TileSpatialNavigation.FitColumns(viewportW, slotW);
        var columns = TileSpatialNavigation.ResolveColumnCount(observedXs.Count, fitted);

        IReadOnlyList<double>? columnXs = observedXs.Count == columns
            ? observedXs
            : TileSpatialNavigation.UniformColumnXs(viewportW, slotW, tileW);

        var headerH = layout.HasHeaders ? MeasureSectionHeaderHeight() : 0;
        cells = TileSpatialNavigation.LayoutWrap(
            layout.SectionCounts, columns, tileW, tileH, headerH, columnXs);

        if (realized.Count > 0)
        {
            var byIndex = new Dictionary<int, TileSpatialNavigation.Cell>();
            foreach (var (item, measured) in realized)
            {
                var idx = IndexOfItem(items, item);
                if (idx < 0)
                    continue;
                // Overlay X/size only. Realized Y is viewport-relative; mixing it
                // with the document-space wrap model skips rows. Column is X.
                byIndex[idx] = cells[idx] with { X = measured.X, Width = measured.Width, Height = measured.Height };
            }
            cells = TileSpatialNavigation.OverlayMeasured(cells, byIndex);
        }

        return cells.Count > 0;
    }

    private static int IndexOfItem(IReadOnlyList<IGridItem> items, IGridItem item)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (ReferenceEquals(items[i], item))
                return i;
        }
        return -1;
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
            // Use the wrap slot (margin included) so X matches TryBuildCells / Uniform columns.
            var tl = li.TranslatePoint(new Point(0, 0), origin);
            var slotW = li.ActualWidth + li.Margin.Left + li.Margin.Right;
            var slotH = li.ActualHeight + li.Margin.Top + li.Margin.Bottom;
            cells.Add(new TileSpatialNavigation.Cell(
                cells.Count, Row: 0, Col: 0,
                tl.X - li.Margin.Left, tl.Y - li.Margin.Top, slotW, slotH));
            items.Add(item);
            return;
        }

        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
            CollectRealizedTileRects(VisualTreeHelper.GetChild(node, i), origin, cells, items);
    }

    /// <summary>
    /// The width the date grouping is chosen for: the grid's viewport as it is with its scrollbar showing, whether or not
    /// it is — the scroll viewer's own width less its padding and a scrollbar, never the viewport itself. A regroup can
    /// bring the scrollbar in or take it away; were this width to follow it, the grouping could change back, and again.
    /// Nor a section's wrap panel: a collapsed section's keeps its old width.
    /// </summary>
    private double GroupingWidth()
    {
        var width = GridScroll() is { ActualWidth: > 1 } scroll
            ? scroll.ActualWidth - scroll.Padding.Left - scroll.Padding.Right
            : ItemsList.ActualWidth - ItemsList.Padding.Left - ItemsList.Padding.Right;
        return Math.Max(1, width - SystemParameters.VerticalScrollBarWidth);
    }

    /// <summary>
    /// Width the wrap panel actually laid out against. ScrollViewer.ViewportWidth
    /// already excludes the vertical scrollbar — do not subtract padding/12 again
    /// (that was the 0.8.4 off-by-one: 4 visual columns, nav used 3). The first
    /// shown section's panel: a collapsed section's keeps the width it last had.
    /// </summary>
    private double WrapViewportWidth()
    {
        if (FindDescendant<WpfToolkit.Controls.VirtualizingWrapPanel>(ItemsList, p => p.IsVisible && p.ActualWidth > 1)
            is { } panel)
            return panel.ActualWidth;

        var sv = GridScroll();
        if (sv is { ViewportWidth: > 1 })
            return sv.ViewportWidth;

        var w = ItemsList.ActualWidth - ItemsList.Padding.Left - ItemsList.Padding.Right;
        if (sv?.ComputedVerticalScrollBarVisibility == System.Windows.Visibility.Visible)
            w -= SystemParameters.VerticalScrollBarWidth;
        return Math.Max(1, w);
    }

    private (double Width, double Height, double SlotWidth, double SlotHeight) MeasureTileSlot(
        IEnumerable<TileSpatialNavigation.Cell>? realized = null)
    {
        if (realized is not null)
        {
            foreach (var c in realized)
                return (c.Width, c.Height, c.Width, c.Height);
        }

        if (FindFirstTileItem(ItemsList) is { } li && li.ActualWidth > 0)
        {
            var w = li.ActualWidth;
            var h = li.ActualHeight;
            return (w, h, w + li.Margin.Left + li.Margin.Right, h + li.Margin.Top + li.Margin.Bottom);
        }

        var outer = (ViewModel?.TileSize ?? 200) + 20;
        return (outer, outer + 18, outer, outer + 18);
    }

    private static System.Windows.Controls.ListBoxItem? FindFirstTileItem(DependencyObject node)
    {
        if (node is System.Windows.Controls.ListBoxItem { IsVisible: true, DataContext: IGridItem } li)
            return li;

        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            if (FindFirstTileItem(VisualTreeHelper.GetChild(node, i)) is { } found)
                return found;
        }
        return null;
    }

    private Dictionary<IGridItem, TileSpatialNavigation.Cell> CollectRealizedByItem()
    {
        var map = new Dictionary<IGridItem, TileSpatialNavigation.Cell>();
        CollectRealizedByItem(ItemsList, ItemsList, map);
        return map;
    }

    private static void CollectRealizedByItem(
        DependencyObject node,
        UIElement origin,
        Dictionary<IGridItem, TileSpatialNavigation.Cell> map)
    {
        if (node is System.Windows.Controls.ListBoxItem { IsVisible: true, DataContext: IGridItem item } li)
        {
            var tl = li.TranslatePoint(new Point(0, 0), origin);
            // Slot the wrap used: arranged width includes margin.
            var slotW = li.ActualWidth + li.Margin.Left + li.Margin.Right;
            var slotH = li.ActualHeight + li.Margin.Top + li.Margin.Bottom;
            map[item] = new TileSpatialNavigation.Cell(
                map.Count, Row: 0, Col: 0, tl.X - li.Margin.Left, tl.Y - li.Margin.Top, slotW, slotH);
            return;
        }

        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
            CollectRealizedByItem(VisualTreeHelper.GetChild(node, i), origin, map);
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

    /// <summary>How many tiles the grid lays out across, for keyboard navigation. Internal for tests.</summary>
    internal int ColumnsPerRow()
    {
        var (_, _, slotW, _) = MeasureTileSlot();
        return TileSpatialNavigation.FitColumns(WrapViewportWidth(), slotW);
    }
}
