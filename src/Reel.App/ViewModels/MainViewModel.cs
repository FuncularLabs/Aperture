using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;
using Reel.App.Mvvm;
using Reel.App.Services;
using Reel.Core.Annotations;
using Reel.Core.Formatting;
using Reel.Core.Library;
using Reel.Core.Models;

namespace Reel.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    // Five zoom stops: tile longest-edge in device-independent pixels.
    private static readonly double[] ZoomSizes = [96, 140, 200, 280, 400];

    // Roughly two screens of tiles — how many newest-section items to auto-expand.
    private const int ExpandTargetItems = 60;

    private readonly LibraryService _library;
    private readonly SynchronizationContext _ui;
    private readonly Lock _indexLock = new();
    private readonly CollectionViewSource _view = new();
    private readonly Dictionary<long, SectionVm> _sections = [];
    private int _activeIndexers;
    private long _lastStreamRefreshTick;

    private bool _isLoadingView;
    private CancellationTokenSource? _buildDebounce;
    private int _buildGeneration;

    private List<IGridItem> _tiles = [];
    private object? _selectedItem;
    private int _zoom;
    private string _searchText = "";
    private string _statusText = "No folders yet — add one to begin.";
    private string _indexingText = "";

    // Folder navigation: null root = home (all included roots).
    private (long? RootId, string RelDir) _location = (null, "");
    private readonly Stack<(long? RootId, string RelDir)> _back = new();
    private List<TileVm> _mediaTiles = [];
    private IReadOnlyList<TileVm> _selectedTiles = [];
    private List<LibraryRow> _allRows = [];
    private Dictionary<string, Annotation> _annotations = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _availableTags = [];

    public MainViewModel(LibraryService library, ThumbnailService thumbnails)
    {
        _library = library;
        Thumbnails = thumbnails;
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();
        _zoom = Math.Clamp(_library.Settings.Current.DefaultZoom, 0, ZoomSizes.Length - 1);

        AddRootCommand = new RelayCommand(AddRoot);
        RemoveRootCommand = new RelayCommand<RootVm>(RemoveRoot);
        ZoomInCommand = new RelayCommand(ZoomIn, () => _zoom < ZoomSizes.Length - 1);
        ZoomOutCommand = new RelayCommand(ZoomOut, () => _zoom > 0);
        OpenSelectedCommand = new RelayCommand(OpenSelected, () => SelectedTile is not null);
        ClearSearchCommand = new RelayCommand(() => SearchText = "");
        QuickPickCommand = new RelayCommand<string>(QuickPickTag);
        OpenQuickLookCommand = new RelayCommand(OpenQuickLook, () => _tiles.Count > 0);
        CloseQuickLookCommand = new RelayCommand(CloseQuickLook);
        QuickLookNextCommand = new RelayCommand(() => MoveQuickLook(1));
        QuickLookPrevCommand = new RelayCommand(() => MoveQuickLook(-1));

        AddChosenFoldersCommand = new RelayCommand(AddChosenFolders);
        SkipFirstRunCommand = new RelayCommand(SkipFirstRun);
        CopyFullPathCommand = new RelayCommand<TileVm>(t => CopyText(t?.FullPath));
        CopyFileNameCommand = new RelayCommand<TileVm>(t => CopyText(t?.FileName));
        CopyImageCommand = new RelayCommand<TileVm>(CopyImage);
        CopyFileCommand = new RelayCommand<TileVm>(t => PutFileOnClipboard(t, cut: false));
        CutFileCommand = new RelayCommand<TileVm>(t => PutFileOnClipboard(t, cut: true));
        OpenContainingFolderCommand = new RelayCommand<TileVm>(OpenContainingFolder);
        CyclePreviewCommand = new RelayCommand(CyclePreview);
        ClosePreviewCommand = new RelayCommand(() => PreviewMode = PreviewMode.Off);
        RemovePreviewTagCommand = new RelayCommand<string>(RemovePreviewTag);
        EditAnnotationCommand = new RelayCommand<TileVm>(EditAnnotation);
        ManageTagsCommand = new RelayCommand(ManageTags);
        OpenReadmeCommand = new RelayCommand(OpenReadme);
        NavigateHomeCommand = new RelayCommand(() => NavigateTo(null, ""));
        NavigateUpCommand = new RelayCommand(NavigateUp, () => !IsHome);
        NavigateBackCommand = new RelayCommand(NavigateBack, () => _back.Count > 0);
        NavigateCrumbCommand = new RelayCommand<BreadcrumbVm>(c => { if (c is not null) NavigateTo(c.RootId, c.RelDir); });
        ActivateItemCommand = new RelayCommand<object>(ActivateItem);

        _library.RootChanged += OnRootChanged;

        LoadRoots();
        LoadAnnotations();
        LoadRows();
        BuildViewImmediate();
        BuildTree();
        SelectDefaultNode();
        MaybeStartFirstRun();
    }

    private void LoadAnnotations()
    {
        _annotations = _library.GetAllAnnotations();
        _availableTags = _library.GetTagsByRecency();
    }

    private Annotation AnnotationFor(LibraryRow row) =>
        _annotations.GetValueOrDefault(row.FullPath) ?? Annotation.Empty;

    private void EditAnnotation(TileVm? tile)
    {
        // Operate on the whole media selection; fall back to the passed/clicked tile.
        var targets = _selectedTiles.Count > 0 ? _selectedTiles.ToList() : [];
        if (tile is not null && !targets.Contains(tile))
            targets.Add(tile);
        if (targets.Count == 0)
            return;

        var header = targets.Count == 1 ? targets[0].FileName : $"{targets.Count} items selected";
        var inputs = targets
            .Select(t => { var a = AnnotationFor(t.Row); return new AnnotationTarget(t.FullPath, a.Tags, a.Note); })
            .ToList();

        var vm = new AnnotationDialogVm(header, inputs, _availableTags);
        var dialog = new AnnotationDialog { DataContext = vm, Owner = System.Windows.Application.Current.MainWindow };
        if (dialog.ShowDialog() != true)
            return;

        foreach (var input in inputs)
        {
            var (tags, note) = vm.ResolveFor(input);
            _library.SaveAnnotation(input.Path, tags, note);
        }
        RefreshAnnotationsInPlace(targets);
    }

    /// <summary>Reloads the annotation cache and refreshes the given tiles' badges/tooltips in place.</summary>
    private void RefreshAnnotationsInPlace(IEnumerable<TileVm> tiles)
    {
        LoadAnnotations();
        foreach (var tile in tiles)
            tile.UpdateAnnotation(AnnotationFor(tile.Row));
    }

    /// <summary>Refreshes every media tile's annotation in place (used after bulk tag-manager edits).</summary>
    private void RefreshAllAnnotationsInPlace()
    {
        LoadAnnotations();
        foreach (var tile in _mediaTiles)
            tile.UpdateAnnotation(AnnotationFor(tile.Row));
    }

    /// <summary>Pushed from the grid on selection change; media tiles only.</summary>
    public void SetSelectedItems(System.Collections.IEnumerable items) =>
        _selectedTiles = items.OfType<TileVm>().ToList();

    private void SelectDefaultNode()
    {
        if (_location.RootId is null && FolderTree.Count > 0)
            NavigateTo(FolderTree[0].RootId, "", pushBack: false);
        else
            SelectPath(_location.RootId, _location.RelDir);
    }

    public ThumbnailService Thumbnails { get; }

    /// <summary>"Aperture vX.Y.Z" from the assembly version, for the About row.</summary>
    public string AppVersion =>
        "Aperture v" + (System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.6.0");

    private string _windowTitle = "Aperture";

    /// <summary>Window title — shows the current folder's full path.</summary>
    public string WindowTitle
    {
        get => _windowTitle;
        private set => SetProperty(ref _windowTitle, value);
    }

    private void UpdateWindowTitle()
    {
        if (_location.RootId is { } rootId && Roots.FirstOrDefault(r => r.Id == rootId) is { } root)
        {
            var full = _location.RelDir.Length == 0 ? root.Path : Path.Combine(root.Path, _location.RelDir);
            WindowTitle = $"Aperture  —  {full}";
        }
        else
        {
            WindowTitle = "Aperture";
        }
    }

    public ObservableCollection<RootVm> Roots { get; } = [];

    /// <summary>Root nodes of the left folder tree (roots → lazy subfolders).</summary>
    public ObservableCollection<FolderNodeVm> FolderTree { get; } = [];

    /// <summary>The grouped, sorted, filtered view the grid binds to.</summary>
    public ICollectionView? ItemsView => _view.View;

    /// <summary>The selected grid item (media tile or folder tile).</summary>
    public object? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (SetProperty(ref _selectedItem, value))
            {
                OnPropertyChanged(nameof(SelectedTile));
                LoadPreview(); // no-op when the preview pane is closed
            }
        }
    }

    /// <summary>The selection when it is a media tile (null when a folder is selected).</summary>
    public TileVm? SelectedTile => _selectedItem as TileVm;

    public double TileSize => ZoomSizes[_zoom];

    /// <summary>Live text filter over file name, folder alias and camera.</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
                RequestBuildView();
        }
    }

    // --- Search quick-picks (recent / most-used tags, shown when the box is focused) ---

    private bool _quickPicksOpen;
    private string _quickPickHeader = "";

    public ObservableCollection<string> QuickPickTags { get; } = [];

    public bool QuickPicksOpen
    {
        get => _quickPicksOpen;
        private set => SetProperty(ref _quickPicksOpen, value);
    }

    public string QuickPickHeader
    {
        get => _quickPickHeader;
        private set => SetProperty(ref _quickPickHeader, value);
    }

    /// <summary>Called when the search box gains focus: recompute and show the tag quick-picks.</summary>
    public void OpenQuickPicks()
    {
        var (tags, byUsage) = TagQuickPicks.Select(_library.GetTagUsage(), 12);
        QuickPickTags.Clear();
        foreach (var tag in tags)
            QuickPickTags.Add(tag);
        QuickPickHeader = byUsage ? "Most-used tags" : "Recent tags";
        QuickPicksOpen = QuickPickTags.Count > 0;
    }

    public void CloseQuickPicks() => QuickPicksOpen = false;

    /// <summary>Adds a <c>tag:</c> clause for the picked tag (tag clauses OR together).</summary>
    private void QuickPickTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return;
        var clause = tag.Contains(' ') ? $"tag:\"{tag}\"" : $"tag:{tag}";
        var current = _searchText.TrimEnd();
        SearchText = current.Length == 0 ? clause : $"{current} {clause}";
    }

    /// <summary>Tokenized caption format string. Editing rebuilds tile captions.</summary>
    public string CaptionFormat
    {
        get => _library.Settings.Current.CaptionFormat;
        set
        {
            if (value == _library.Settings.Current.CaptionFormat)
                return;
            _library.Settings.Update(s => s.CaptionFormat = value);
            OnPropertyChanged();
            RequestBuildView();
        }
    }

    /// <summary>Group the grid into collapsible date sections (date sorts only).</summary>
    public bool GroupByDate
    {
        get => _library.Settings.Current.GroupByDate;
        set
        {
            if (value == _library.Settings.Current.GroupByDate)
                return;
            _library.Settings.Update(s => s.GroupByDate = value);
            OnPropertyChanged();
            RequestBuildView();
        }
    }

    private bool _showImages = true;
    private bool _showVideos = true;

    /// <summary>Top-bar "Show: Pictures" filter over the current view.</summary>
    public bool ShowImages
    {
        get => _showImages;
        set { if (SetProperty(ref _showImages, value)) RequestBuildView(); }
    }

    /// <summary>Top-bar "Show: Videos" filter over the current view.</summary>
    public bool ShowVideos
    {
        get => _showVideos;
        set { if (SetProperty(ref _showVideos, value)) RequestBuildView(); }
    }

    // --- Sort ---------------------------------------------------------------

    /// <summary>A named sort. Date sorts keep collapsible sections; others flatten to a global list.</summary>
    public sealed record SortOption(string Label, List<Reel.Core.Settings.SortLevel> Levels, bool Grouped);

    private static readonly SortOption[] SortOptions =
    [
        new("Newest first", [new("date", true)], true),
        new("Oldest first", [new("date", false)], true),
        new("Name (A–Z)", [new("name", false)], false),
        new("Name (Z–A)", [new("name", true)], false),
        new("Largest", [new("size", true)], false),
        new("Smallest", [new("size", false)], false),
        new("By camera", [new("camera", false), new("date", true)], false),
    ];

    public IReadOnlyList<string> SortLabels { get; } = SortOptions.Select(o => o.Label).ToList();

    public string SortPreset
    {
        get => _library.Settings.Current.SortPreset;
        set
        {
            if (string.IsNullOrEmpty(value) || value == _library.Settings.Current.SortPreset)
                return;
            _library.Settings.Update(s => s.SortPreset = value);
            OnPropertyChanged();
            RequestBuildView();
        }
    }

    private SortOption CurrentSort => SortOptions.FirstOrDefault(o => o.Label == SortPreset) ?? SortOptions[0];

    /// <summary>Index and show video files. Toggling re-indexes every root to add or drop videos.</summary>
    public bool IncludeVideos
    {
        get => _library.Settings.Current.IncludeVideos;
        set
        {
            if (value == _library.Settings.Current.IncludeVideos)
                return;
            _library.Settings.Update(s => s.IncludeVideos = value);
            OnPropertyChanged();
            foreach (var rootVm in Roots.ToList())
                _ = IndexRootAsync(rootVm.Model, watchAfter: false);
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string IndexingText
    {
        get => _indexingText;
        private set => SetProperty(ref _indexingText, value);
    }

    public ICommand AddRootCommand { get; }
    public ICommand RemoveRootCommand { get; }
    public ICommand ZoomInCommand { get; }
    public ICommand ZoomOutCommand { get; }
    public ICommand OpenSelectedCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand QuickPickCommand { get; }
    public ICommand OpenQuickLookCommand { get; }
    public ICommand CloseQuickLookCommand { get; }
    public ICommand QuickLookNextCommand { get; }
    public ICommand QuickLookPrevCommand { get; }
    public ICommand AddChosenFoldersCommand { get; }
    public ICommand SkipFirstRunCommand { get; }
    public ICommand CopyFullPathCommand { get; }
    public ICommand CopyFileNameCommand { get; }
    public ICommand CopyImageCommand { get; }
    public ICommand CopyFileCommand { get; }
    public ICommand CutFileCommand { get; }
    public ICommand OpenContainingFolderCommand { get; }
    public ICommand CyclePreviewCommand { get; }
    public ICommand ClosePreviewCommand { get; }
    public ICommand RemovePreviewTagCommand { get; }

    /// <summary>Removes a tag from the previewed item (the ✕ on a preview-pane chip).</summary>
    private void RemovePreviewTag(string? tag)
    {
        var tile = SelectedTile;
        if (tile is null || string.IsNullOrEmpty(tag))
            return;
        var current = AnnotationFor(tile.Row);
        var tags = current.Tags.Where(t => !string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)).ToList();
        _library.SaveAnnotation(tile.FullPath, tags, current.Note);
        RefreshAnnotationsInPlace([tile]);
    }

    private void OpenContainingFolder(TileVm? tile)
    {
        var path = tile?.FullPath ?? SelectedTile?.FullPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch { }
    }

    // --- Sync status pill ----------------------------------------------------

    private string _syncStatus = "";

    /// <summary>Top-bar pill: total item count, or the current indexing progress.</summary>
    public string SyncStatus
    {
        get => _syncStatus;
        private set => SetProperty(ref _syncStatus, value);
    }

    private void UpdateSyncStatus()
    {
        bool indexing;
        lock (_indexLock)
            indexing = _activeIndexers > 0;

        if (indexing)
            SyncStatus = string.IsNullOrEmpty(IndexingText) ? "Indexing…" : IndexingText;
        else
            SyncStatus = _allRows.Count == 0 ? "" : $"{_allRows.Count:n0} items  ·  up to date";
    }

    // --- Preview / inspector pane -------------------------------------------

    private PreviewMode _previewMode = PreviewMode.Off;
    private System.Windows.Media.Imaging.BitmapSource? _previewImage;
    private IReadOnlyList<MetaRow> _previewExif = [];
    private int _previewGen;

    /// <summary>One EXIF/metadata row in the preview pane.</summary>
    public sealed record MetaRow(string Label, string Value);

    /// <summary>Where the preview/inspector pane is docked (or Off).</summary>
    public PreviewMode PreviewMode
    {
        get => _previewMode;
        set
        {
            if (!SetProperty(ref _previewMode, value))
                return;
            OnPropertyChanged(nameof(PreviewOpen));
            OnPropertyChanged(nameof(PreviewLabel));
            if (value != PreviewMode.Off)
                LoadPreview();
        }
    }

    public bool PreviewOpen => _previewMode != PreviewMode.Off;

    /// <summary>Toolbar button label reflecting the current dock.</summary>
    public string PreviewLabel => _previewMode switch
    {
        PreviewMode.Right => "◧  Preview",
        PreviewMode.Bottom => "⬓  Preview",
        _ => "◫  Preview",
    };

    private void CyclePreview() => PreviewMode = _previewMode switch
    {
        PreviewMode.Off => PreviewMode.Right,
        PreviewMode.Right => PreviewMode.Bottom,
        _ => PreviewMode.Off,
    };

    /// <summary>The full-resolution, orientation-corrected image shown in the preview (async loaded).</summary>
    public System.Windows.Media.Imaging.BitmapSource? PreviewImage
    {
        get => _previewImage;
        private set => SetProperty(ref _previewImage, value);
    }

    public IReadOnlyList<MetaRow> PreviewExif
    {
        get => _previewExif;
        private set => SetProperty(ref _previewExif, value);
    }

    /// <summary>The tile the preview is inspecting (drives its tags/notes + basic fields).</summary>
    public TileVm? PreviewTile => SelectedTile;

    private async void LoadPreview()
    {
        if (_previewMode == PreviewMode.Off)
            return;
        OnPropertyChanged(nameof(PreviewTile));
        var tile = SelectedTile;
        PreviewExif = BuildExif(tile);

        var gen = ++_previewGen;
        PreviewImage = null;
        if (tile is null)
            return;

        var path = tile.FullPath;
        var isVideo = tile.IsVideo;
        var itemId = tile.ItemId;
        var image = await Task.Run(() => isVideo ? DecodeThumbnail(itemId) : ImageLoading.LoadFullImageUpright(path, 2400));
        if (gen == _previewGen && _previewMode != PreviewMode.Off)
            PreviewImage = image;
    }

    private static List<MetaRow> BuildExif(TileVm? tile)
    {
        if (tile is null)
            return [];
        var item = tile.Item;
        var date = item.TakenUtc ?? item.MTimeUtc.ToLocalTime();

        var rows = new List<MetaRow>
        {
            new("Name", tile.FileName),
            new("Folder", tile.Location),
            new("Type", tile.IsVideo ? "Video" : "Image"),
        };
        if (tile.Dimensions.Length > 0)
            rows.Add(new("Dimensions", tile.Dimensions));
        rows.Add(new("Size", tile.SizeText));
        rows.Add(new("Taken", date.ToString("yyyy-MM-dd HH:mm")));

        foreach (var (label, value) in Reel.Core.Media.MetadataReader.ReadExifSummary(tile.FullPath))
            if (!rows.Any(r => r.Label == label)) // don't duplicate Camera etc.
                rows.Add(new(label, value));

        rows.Add(new("Path", tile.FullPath));
        return rows;
    }
    public ICommand EditAnnotationCommand { get; }
    public ICommand ManageTagsCommand { get; }
    public ICommand OpenReadmeCommand { get; }
    public ICommand NavigateHomeCommand { get; }

    private void OpenReadme()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "README.md");
        if (!File.Exists(path))
            return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { }
    }

    private void ManageTags()
    {
        var vm = new TagManagerVm(_library);
        var dialog = new TagManagerDialog { DataContext = vm, Owner = System.Windows.Application.Current.MainWindow };
        dialog.ShowDialog();
        if (vm.Changed)
            RefreshAllAnnotationsInPlace();
    }
    public ICommand NavigateUpCommand { get; }
    public ICommand NavigateBackCommand { get; }
    public ICommand NavigateCrumbCommand { get; }
    public ICommand ActivateItemCommand { get; }

    // --- Folder navigation --------------------------------------------------

    /// <summary>The location breadcrumb (Home / root / subfolder …).</summary>
    public ObservableCollection<BreadcrumbVm> Breadcrumb { get; } = [];

    public bool IsHome => _location.RootId is null && _location.RelDir.Length == 0;

    private bool _navigating;

    private void NavigateTo(long? rootId, string relDir, bool pushBack = true)
    {
        if (_navigating)
            return;
        if (_location.RootId == rootId && string.Equals(_location.RelDir, relDir, StringComparison.OrdinalIgnoreCase))
            return;

        _navigating = true;
        try
        {
            if (pushBack)
                _back.Push(_location);
            _location = (rootId, relDir);
            _searchText = "";
            OnPropertyChanged(nameof(SearchText));
            SelectedItem = null;
            RequestBuildView(); // debounced + async so rapid tree navigation doesn't queue
            OnPropertyChanged(nameof(IsHome));
            SelectPath(rootId, relDir); // keep the tree's selection in sync
        }
        finally
        {
            _navigating = false;
        }
    }

    private void NavigateUp()
    {
        if (_location.RootId is null)
            return;
        var relDir = _location.RelDir;
        if (relDir.Length == 0)
        {
            NavigateTo(null, ""); // root top → home
            return;
        }
        var parent = Path.GetDirectoryName(relDir) ?? "";
        NavigateTo(_location.RootId, parent);
    }

    private void NavigateBack()
    {
        if (_back.Count == 0)
            return;
        var target = _back.Pop();
        NavigateTo(target.RootId, target.RelDir, pushBack: false);
    }

    /// <summary>Enter a folder tile, or open a media tile.</summary>
    private void ActivateItem(object? item)
    {
        switch (item)
        {
            case FolderTileVm folder:
                NavigateTo(folder.RootId, folder.RelDir);
                break;
            case TileVm:
                OpenSelected();
                break;
        }
    }

    // --- Clipboard / context menu ------------------------------------------

    private static void CopyText(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        try { System.Windows.Clipboard.SetText(text); }
        catch { /* clipboard can be transiently locked by another app */ }
    }

    private void CopyImage(TileVm? tile)
    {
        if (tile is null)
            return;

        // Pics: the full-resolution image, EXIF-oriented like the tile/Photos.
        // Videos: the cached frame thumbnail.
        var image = tile.IsVideo
            ? DecodeThumbnail(tile.ItemId)
            : ImageLoading.LoadFullImageUpright(tile.FullPath);
        if (image is null)
            return;
        try { System.Windows.Clipboard.SetImage(image); }
        catch { }
    }

    private static void PutFileOnClipboard(TileVm? tile, bool cut)
    {
        if (tile is null || !File.Exists(tile.FullPath))
            return;
        try
        {
            var files = new System.Collections.Specialized.StringCollection { tile.FullPath };
            var data = new System.Windows.DataObject();
            data.SetFileDropList(files);
            // "Preferred DropEffect" DWORD: 2 = Move (cut), 5 = Copy — what Explorer reads.
            var effect = new byte[] { (byte)(cut ? 2 : 5), 0, 0, 0 };
            data.SetData("Preferred DropEffect", new MemoryStream(effect));
            System.Windows.Clipboard.SetDataObject(data, copy: true);
        }
        catch { }
    }

    // --- Keyboard navigation (headers + visible tiles) ----------------------

    private readonly record struct NavStop(bool IsHeader, SectionVm? Section, IGridItem? Item);

    private SectionVm? _cursorSection;

    /// <summary>True when the keyboard cursor is on a section header rather than a tile.</summary>
    public bool CursorIsHeader => _cursorSection is not null;

    /// <summary>
    /// The linear sequence the keyboard walks: each section's header, then that
    /// section's tiles when it's expanded (collapsed sections contribute only a header).
    /// </summary>
    private List<NavStop> BuildNav()
    {
        var nav = new List<NavStop>();
        var i = 0;
        while (i < _tiles.Count)
        {
            var section = _tiles[i].Section;
            if (section is not null)
                nav.Add(new NavStop(true, section, null));

            var expanded = section is null || section.IsExpanded;
            while (i < _tiles.Count && ReferenceEquals(_tiles[i].Section, section))
            {
                if (expanded)
                    nav.Add(new NavStop(false, section, _tiles[i]));
                i++;
            }
        }
        return nav;
    }

    private int FindCursor(List<NavStop> nav)
    {
        if (_cursorSection is not null)
        {
            var h = nav.FindIndex(s => s.IsHeader && ReferenceEquals(s.Section, _cursorSection));
            if (h >= 0)
                return h;
        }
        if (_selectedItem is IGridItem item)
        {
            var t = nav.FindIndex(s => !s.IsHeader && ReferenceEquals(s.Item, item));
            if (t >= 0)
                return t;
        }
        return 0;
    }

    /// <summary>Moves the keyboard cursor. Returns the item/section to scroll into view.</summary>
    public object? MoveGrid(string direction, int columns)
    {
        var nav = BuildNav();
        if (nav.Count == 0)
            return null;

        var cur = Math.Clamp(FindCursor(nav), 0, nav.Count - 1);
        var next = direction switch
        {
            "left" => cur - 1,
            "right" => cur + 1,
            "down" => StepVertical(nav, cur, columns),
            "up" => StepVertical(nav, cur, -columns),
            _ => cur,
        };
        return ApplyCursor(nav, Math.Clamp(next, 0, nav.Count - 1));
    }

    private static int StepVertical(List<NavStop> nav, int cur, int delta)
    {
        // Headers are full-width single stops — step by one.
        if (nav[cur].IsHeader)
            return cur + Math.Sign(delta);

        var target = cur + delta;
        if (delta > 0)
        {
            for (var k = cur + 1; k <= Math.Min(target, nav.Count - 1); k++)
                if (nav[k].IsHeader)
                    return k; // stop at the next header rather than jumping into the next section
            return Math.Min(target, nav.Count - 1);
        }
        for (var k = cur - 1; k >= Math.Max(target, 0); k--)
            if (nav[k].IsHeader)
                return k;
        return Math.Max(target, 0);
    }

    private object? ApplyCursor(List<NavStop> nav, int index)
    {
        var stop = nav[index];
        ClearCursorHighlight();

        if (stop.IsHeader)
        {
            _cursorSection = stop.Section;
            stop.Section!.IsCursor = true;
            SelectedItem = null;
            return FirstItemOfSection(stop.Section);
        }

        _cursorSection = null;
        SelectedItem = stop.Item;
        return stop.Item;
    }

    private void ClearCursorHighlight()
    {
        foreach (var s in _sections.Values)
            s.IsCursor = false;
    }

    private IGridItem? FirstItemOfSection(SectionVm section) =>
        _tiles.FirstOrDefault(t => ReferenceEquals(t.Section, section));

    /// <summary>Enter: toggle the section on a header, or open/enter the tile.</summary>
    public object? ActivateCursor()
    {
        if (_cursorSection is not null)
        {
            _cursorSection.IsExpanded = !_cursorSection.IsExpanded;
            return FirstItemOfSection(_cursorSection);
        }
        if (_selectedItem is not null)
            ActivateItem(_selectedItem);
        return null;
    }

    /// <summary>Space on a header toggles it (Space on a tile is handled as quick-look).</summary>
    public void ToggleCursorSection()
    {
        if (_cursorSection is not null)
            _cursorSection.IsExpanded = !_cursorSection.IsExpanded;
    }

    /// <summary>Right/Left on a header expands/collapses it. Returns false when the cursor isn't on a header.</summary>
    public bool SetCursorSectionExpanded(bool expanded)
    {
        if (_cursorSection is null)
            return false;
        _cursorSection.IsExpanded = expanded;
        return true;
    }

    /// <summary>Re-anchors the keyboard cursor onto a visible tile (so arrow keys follow the scroll).</summary>
    public void SetCursorToItem(object? item)
    {
        if (item is not IGridItem)
            return;
        var nav = BuildNav();
        var idx = nav.FindIndex(s => !s.IsHeader && ReferenceEquals(s.Item, item));
        if (idx >= 0)
            ApplyCursor(nav, idx);
    }

    /// <summary>Home / End: move the cursor to the first / last visible stop.</summary>
    public object? MoveCursorToStart()
    {
        var nav = BuildNav();
        return nav.Count == 0 ? null : ApplyCursor(nav, 0);
    }

    public object? MoveCursorToEnd()
    {
        var nav = BuildNav();
        return nav.Count == 0 ? null : ApplyCursor(nav, nav.Count - 1);
    }

    // --- First run ----------------------------------------------------------

    private bool _showFirstRun;

    public ObservableCollection<FirstRunCandidateVm> FirstRunCandidates { get; } = [];

    public bool ShowFirstRun
    {
        get => _showFirstRun;
        private set => SetProperty(ref _showFirstRun, value);
    }

    private void MaybeStartFirstRun()
    {
        if (Roots.Count > 0 || _library.Settings.Current.FirstRunDone)
            return;

        var candidates = Reel.Core.Library.FolderDetection.DetectCandidates();
        if (candidates.Count == 0)
        {
            _library.Settings.Update(s => s.FirstRunDone = true);
            return;
        }

        foreach (var c in candidates)
            FirstRunCandidates.Add(new FirstRunCandidateVm(c.Path, c.Alias));
        ShowFirstRun = true;
    }

    private void AddChosenFolders()
    {
        foreach (var candidate in FirstRunCandidates.Where(c => c.IsChosen))
            AddRootPath(candidate.Path, candidate.Alias);
        FinishFirstRun();
    }

    private void SkipFirstRun() => FinishFirstRun();

    private void FinishFirstRun()
    {
        _library.Settings.Update(s => s.FirstRunDone = true);
        FirstRunCandidates.Clear();
        ShowFirstRun = false;
    }

    // --- Quick look ---------------------------------------------------------

    private int _quickLookIndex = -1;
    private bool _quickLookOpen;
    private System.Windows.Media.Imaging.BitmapSource? _quickLookImage;
    private string _quickLookCaption = "";

    public bool QuickLookOpen
    {
        get => _quickLookOpen;
        private set => SetProperty(ref _quickLookOpen, value);
    }

    public System.Windows.Media.Imaging.BitmapSource? QuickLookImage
    {
        get => _quickLookImage;
        private set => SetProperty(ref _quickLookImage, value);
    }

    public string QuickLookCaption
    {
        get => _quickLookCaption;
        private set => SetProperty(ref _quickLookCaption, value);
    }

    private void OpenQuickLook()
    {
        if (_mediaTiles.Count == 0)
            return;
        var start = SelectedTile is not null ? _mediaTiles.IndexOf(SelectedTile) : 0;
        QuickLookOpen = true;
        ShowQuickLookAt(start < 0 ? 0 : start);
    }

    private void CloseQuickLook()
    {
        QuickLookOpen = false;
        QuickLookImage = null;
        _quickLookIndex = -1;
    }

    private void MoveQuickLook(int delta)
    {
        if (!_quickLookOpen || _mediaTiles.Count == 0)
            return;
        ShowQuickLookAt(Math.Clamp(_quickLookIndex + delta, 0, _mediaTiles.Count - 1));
    }

    private void ShowQuickLookAt(int index)
    {
        if (index < 0 || index >= _mediaTiles.Count)
            return;
        _quickLookIndex = index;
        var tile = _mediaTiles[index];
        SelectedItem = tile;
        QuickLookCaption = $"{tile.FileName}   ·   {index + 1} / {_mediaTiles.Count:n0}";
        QuickLookImage = null;
        LoadQuickLook(index, tile);
    }

    private async void LoadQuickLook(int index, TileVm tile)
    {
        var image = await Task.Run(() => tile.IsVideo
            ? DecodeThumbnail(tile.ItemId)
            : ImageLoading.LoadFullImageUpright(tile.FullPath, maxLongestEdge: 1600));

        // Ignore if the user moved on while we decoded.
        if (_quickLookOpen && _quickLookIndex == index)
            QuickLookImage = image;
    }

    private System.Windows.Media.Imaging.BitmapSource? DecodeThumbnail(long itemId)
    {
        var bytes = _library.GetThumbnail(itemId, ThumbSize.Large);
        return bytes is null ? null : ImageLoading.Decode(bytes, 0);
    }

    /// <summary>Kick a background reconcile of every root, then watch it. Cached rows are already on screen.</summary>
    public void StartBackgroundRefresh()
    {
        foreach (var rootVm in Roots.ToList())
            _ = IndexRootAsync(rootVm.Model, watchAfter: true);
    }

    // --- Zoom ---------------------------------------------------------------

    public void ZoomIn()
    {
        if (_zoom < ZoomSizes.Length - 1)
        {
            _zoom++;
            OnPropertyChanged(nameof(TileSize));
            _library.Settings.Update(s => s.DefaultZoom = _zoom);
        }
    }

    public void ZoomOut()
    {
        if (_zoom > 0)
        {
            _zoom--;
            OnPropertyChanged(nameof(TileSize));
            _library.Settings.Update(s => s.DefaultZoom = _zoom);
        }
    }

    // --- Window layout persistence ------------------------------------------

    public (double? Width, double? Height, double? Left, double? Top, bool Maximized) RestoreWindow()
    {
        var s = _library.Settings.Current;
        return (s.WindowWidth, s.WindowHeight, s.WindowLeft, s.WindowTop, s.WindowMaximized);
    }

    public void SaveWindow(double width, double height, double left, double top, bool maximized)
    {
        _library.Settings.Update(s =>
        {
            s.WindowWidth = width;
            s.WindowHeight = height;
            s.WindowLeft = left;
            s.WindowTop = top;
            s.WindowMaximized = maximized;
        });
    }

    // --- Roots --------------------------------------------------------------

    private void LoadRoots()
    {
        Roots.Clear();
        foreach (var root in _library.GetRoots())
        {
            var vm = new RootVm(root, OnRootIncludedChanged, OnRootAliasChanged) { Count = _library.CountForRoot(root.Id) };
            Roots.Add(vm);
        }
    }

    private void OnRootAliasChanged(RootVm rootVm, string alias)
    {
        _library.SetAlias(rootVm.Id, alias);
        BuildTree();
        RequestBuildView(); // captions reference {alias}
    }

    private void AddRoot()
    {
        var dialog = new OpenFolderDialog { Title = "Add a folder to Aperture" };
        if (dialog.ShowDialog() != true)
            return;

        var path = dialog.FolderName;
        if (Roots.Any(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase)))
            return; // already added

        AddRootPath(path, DeriveAlias(path));
    }

    /// <summary>Adds dropped folders as watched roots (skips duplicates). Used by drag-and-drop.</summary>
    public void AddFolders(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            var full = Path.GetFullPath(path);
            if (!System.IO.Directory.Exists(full)
                || Roots.Any(r => string.Equals(r.Path, full, StringComparison.OrdinalIgnoreCase)))
                continue;
            AddRootPath(full, DeriveAlias(full));
        }
    }

    private void AddRootPath(string path, string alias)
    {
        var root = _library.AddRoot(path, alias);
        var vm = new RootVm(root, OnRootIncludedChanged, OnRootAliasChanged);
        Roots.Add(vm);
        BuildTree();
        _ = IndexRootAsync(root, watchAfter: true);
    }

    private void RemoveRoot(RootVm? rootVm)
    {
        if (rootVm is null)
            return;
        var wasCurrent = _location.RootId == rootVm.Id;
        _library.RemoveRoot(rootVm.Id);
        Roots.Remove(rootVm);
        if (wasCurrent)
            _location = (null, "");
        LoadRows();
        BuildTree();
        SelectDefaultNode();
        RequestBuildView();
    }

    private void OnRootIncludedChanged(RootVm rootVm, bool included)
    {
        _library.SetIncluded(rootVm.Id, included);
        LoadRows();
        RequestBuildView();
    }

    private static string DeriveAlias(string path)
    {
        var name = new DirectoryInfo(path).Name;
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    // --- Union / sections / status ------------------------------------------

    private const int BuildDebounceMs = 140;

    /// <summary>True while an async rebuild is running — drives the right-pane spinner.</summary>
    public bool IsLoadingView
    {
        get => _isLoadingView;
        private set => SetProperty(ref _isLoadingView, value);
    }

    /// <summary>Re-reads the union of included items. Call only when the underlying data changed.</summary>
    private void LoadRows() => _allRows = _library.GetUnion();

    /// <summary>Rebuilds the grid synchronously from the cached rows (startup / one-off data changes).</summary>
    private void BuildViewImmediate() => ApplyView(ComputeView(CaptureBuildInputs()));

    /// <summary>
    /// Debounced, last-one-wins async rebuild. Rapid triggers — arrowing the folder
    /// tree, typing in search — coalesce, and the heavy filter/sort/tile work runs off
    /// the UI thread so keystrokes never queue behind it. A brief spinner covers the gap.
    /// </summary>
    private async void RequestBuildView()
    {
        _buildDebounce?.Cancel();
        var cts = new CancellationTokenSource();
        _buildDebounce = cts;
        var token = cts.Token;
        try { await Task.Delay(BuildDebounceMs, token); }
        catch (OperationCanceledException) { return; }

        var generation = ++_buildGeneration;
        IsLoadingView = true;
        var inputs = CaptureBuildInputs();

        ViewBuild build;
        try { build = await Task.Run(() => ComputeView(inputs), token); }
        catch (OperationCanceledException) { return; }

        if (generation != _buildGeneration)
            return; // a newer request superseded this one
        ApplyView(build);
        IsLoadingView = false;
    }

    private sealed record BuildInputs(
        List<LibraryRow> Rows, string Format, SortOption Sort, string Query,
        (long? RootId, string RelDir) Location, bool GroupByDate,
        Dictionary<string, Annotation> Annotations, Dictionary<long, bool> Expansion,
        bool ShowImages, bool ShowVideos);

    private sealed record ViewBuild(
        List<TileVm> MediaTiles, List<IGridItem> Tiles, bool UseSections, Dictionary<long, SectionVm> Sections);

    /// <summary>Snapshots everything the compute needs — must run on the UI thread.</summary>
    private BuildInputs CaptureBuildInputs() => new(
        _allRows,
        _library.Settings.Current.CaptionFormat,
        CurrentSort,
        _searchText.Trim(),
        EffectiveLocation(),
        GroupByDate,
        _annotations,
        _sections.ToDictionary(kv => kv.Key, kv => kv.Value.IsExpanded),
        _showImages,
        _showVideos);

    /// <summary>Pure: builds tiles + fresh sections from the snapshot. Touches no shared/UI state, so it is thread-safe.</summary>
    private ViewBuild ComputeView(BuildInputs input)
    {
        Annotation Ann(LibraryRow r) => input.Annotations.GetValueOrDefault(r.FullPath) ?? Annotation.Empty;

        // Searching = a flat, global Gmail-style query over the whole library;
        // otherwise the current folder's direct media (subfolders live in the tree).
        List<LibraryRow> media;
        if (input.Query.Length > 0)
        {
            var search = SearchQuery.Parse(input.Query);
            media = input.Rows.Where(r => search.Matches(r, Ann(r))).ToList();
        }
        else
        {
            media = ComputeMediaFor(input.Rows, input.Location);
        }

        // Top-bar "Show: Pictures / Videos" filter.
        if (!input.ShowImages || !input.ShowVideos)
            media = media.Where(r => r.Item.IsVideo ? input.ShowVideos : input.ShowImages).ToList();

        // Only date sorts get collapsible date sections; other sorts flatten to a global list.
        var useSections = input.GroupByDate && input.Sort.Grouped && media.Count > 0;
        var sections = new Dictionary<long, SectionVm>();
        List<TileVm> tiles;
        if (useSections)
        {
            tiles = BuildSectionedPure(media, input.Format, input.Sort.Levels, Ann, input.Expansion, sections);
        }
        else
        {
            LibrarySorter.Sort(media, input.Sort.Levels);
            tiles = media.Select(r => new TileVm(r, Thumbnails, input.Format, Ann(r))).ToList();
        }

        return new ViewBuild(tiles, [.. tiles.Cast<IGridItem>()], useSections, sections);
    }

    /// <summary>Identifies the current view (folder or search) for per-location scroll memory.</summary>
    public string LocationKey =>
        _searchText.Trim().Length > 0 ? "search" : $"{_location.RootId}|{_location.RelDir}";

    /// <summary>Raised just before the grid's items change (so the view can save its scroll position).</summary>
    public event Action? ViewApplying;

    /// <summary>Raised just after the grid's items change (so the view can restore/reset scroll).</summary>
    public event Action? ViewApplied;

    /// <summary>Applies a computed build to the live view — UI thread only.</summary>
    private void ApplyView(ViewBuild build)
    {
        ViewApplying?.Invoke();

        ClearCursorHighlight();
        _cursorSection = null;
        _selectedTiles = []; // stale references across a view rebuild would act on the wrong tiles

        _sections.Clear();
        foreach (var (key, section) in build.Sections)
            _sections[key] = section;

        _mediaTiles = build.MediaTiles;
        _tiles = build.Tiles;
        _view.Source = _tiles;
        _view.GroupDescriptions.Clear();
        if (build.UseSections)
            _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(IGridItem.Section)));

        OnPropertyChanged(nameof(ItemsView));
        UpdateStatus();
        UpdateWindowTitle();

        ViewApplied?.Invoke();
    }

    /// <summary>Media files directly inside the given folder (non-recursive).</summary>
    private static List<LibraryRow> ComputeMediaFor(List<LibraryRow> rows, (long? RootId, string RelDir) loc)
    {
        if (loc.RootId is null)
            return [];

        var media = new List<LibraryRow>();
        foreach (var r in rows)
            if (r.Item.RootId == loc.RootId.Value
                && string.Equals(GetDir(r.Item.RelPath), loc.RelDir, StringComparison.OrdinalIgnoreCase))
                media.Add(r);
        return media;
    }

    // --- Folder tree --------------------------------------------------------

    private void BuildTree()
    {
        FolderTree.Clear();
        foreach (var rootVm in Roots)
        {
            var subs = GetSubfolders(rootVm.Id, "");
            FolderTree.Add(new FolderNodeVm(
                rootVm.Id, "", rootVm.Alias, isRoot: true, hasChildren: subs.Count > 0,
                rootVm, rootVm.Count, LoadChildNodes));
        }
        ExpandTree(); // expand everything by default (also after background rebuilds)
    }

    /// <summary>Expands the whole folder tree by default (eager-loads every node's children).</summary>
    private void ExpandTree()
    {
        foreach (var node in FolderTree)
            node.ExpandAll();
    }

    private List<FolderNodeVm> LoadChildNodes(long rootId, string relDir) =>
        GetSubfolders(rootId, relDir)
            .Select(s => new FolderNodeVm(rootId, s.RelDir, s.Name, isRoot: false, s.HasChildren, null, s.Count, LoadChildNodes))
            .ToList();

    /// <summary>Immediate subfolders of (rootId, relDir): name, path, recursive count, and whether it nests further.</summary>
    private List<(string Name, string RelDir, int Count, bool HasChildren)> GetSubfolders(long rootId, string relDir)
    {
        var acc = new Dictionary<string, (string RelDir, int Count, bool HasChild)>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in _allRows)
        {
            if (r.Item.RootId != rootId)
                continue;
            var dir = GetDir(r.Item.RelPath);
            if (!IsUnder(dir, relDir) || string.Equals(dir, relDir, StringComparison.OrdinalIgnoreCase))
                continue;
            if (FirstSegmentUnder(r.Item.RelPath, relDir) is not { } child)
                continue;

            var childRel = relDir.Length == 0 ? child : Path.Combine(relDir, child);
            var deeper = !string.Equals(dir, childRel, StringComparison.OrdinalIgnoreCase);
            if (acc.TryGetValue(child, out var v))
                acc[child] = (childRel, v.Count + 1, v.HasChild || deeper);
            else
                acc[child] = (childRel, 1, deeper);
        }

        return acc.OrderByDescending(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => (kv.Key, kv.Value.RelDir, kv.Value.Count, kv.Value.HasChild))
            .ToList();
    }

    /// <summary>Called from the tree when the user selects a node.</summary>
    public void NavigateToNode(FolderNodeVm? node)
    {
        if (node is not null)
            NavigateTo(node.RootId, node.RelDir);
    }

    /// <summary>Expands the tree to and selects the node for a location (keeps the tree in sync).</summary>
    private void SelectPath(long? rootId, string relDir)
    {
        if (rootId is null)
            return;
        var node = FolderTree.FirstOrDefault(n => n.RootId == rootId);
        if (node is null)
            return;

        if (relDir.Length > 0)
        {
            var accumulated = "";
            foreach (var segment in relDir.Split(Path.DirectorySeparatorChar))
            {
                node.IsExpanded = true; // lazy-loads children
                accumulated = accumulated.Length == 0 ? segment : Path.Combine(accumulated, segment);
                var next = node.Children.FirstOrDefault(c => string.Equals(c.RelDir, accumulated, StringComparison.OrdinalIgnoreCase));
                if (next is null)
                    break;
                node = next;
            }
        }
        node.IsSelected = true;
    }

    /// <summary>Resolves single-root "home" to that root's top level.</summary>
    private (long? RootId, string RelDir) EffectiveLocation()
    {
        if (_location.RootId is null && Roots.Count == 1)
            return (Roots[0].Id, "");
        return _location;
    }

    /// <summary>All rows under the current location, recursively (for search).</summary>
    private List<LibraryRow> ScopeRecursive(List<LibraryRow> rows)
    {
        var loc = EffectiveLocation();
        if (loc.RootId is null)
            return rows;
        var rootId = loc.RootId.Value;
        var relDir = loc.RelDir;
        return rows.Where(r => r.Item.RootId == rootId && IsUnder(GetDir(r.Item.RelPath), relDir)).ToList();
    }

    private static string GetDir(string relPath) => Path.GetDirectoryName(relPath) ?? "";

    private static bool IsUnder(string dir, string relDir) =>
        relDir.Length == 0
        || dir.Equals(relDir, StringComparison.OrdinalIgnoreCase)
        || dir.StartsWith(relDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string? FirstSegmentUnder(string relPath, string relDir)
    {
        var start = relDir.Length == 0 ? 0 : relDir.Length + 1;
        if (start >= relPath.Length)
            return null;
        var rest = relPath[start..];
        var sep = rest.IndexOf(Path.DirectorySeparatorChar);
        return sep < 0 ? null : rest[..sep];
    }

    private void BuildBreadcrumb()
    {
        Breadcrumb.Clear();
        Breadcrumb.Add(new BreadcrumbVm { Label = "Home", RootId = null, RelDir = "" });

        if (_location.RootId is { } rootId)
        {
            var alias = Roots.FirstOrDefault(r => r.Id == rootId)?.Alias ?? "…";
            Breadcrumb.Add(new BreadcrumbVm { Label = alias, RootId = rootId, RelDir = "" });

            if (_location.RelDir.Length > 0)
            {
                var accumulated = "";
                foreach (var part in _location.RelDir.Split(Path.DirectorySeparatorChar))
                {
                    accumulated = accumulated.Length == 0 ? part : Path.Combine(accumulated, part);
                    Breadcrumb.Add(new BreadcrumbVm { Label = part, RootId = rootId, RelDir = accumulated });
                }
            }
        }

        for (var i = 0; i < Breadcrumb.Count; i++)
            Breadcrumb[i].IsLast = i == Breadcrumb.Count - 1;
    }

    /// <summary>
    /// Pure sectioned build: creates fresh <see cref="SectionVm"/>s (safe to build off
    /// the UI thread since they aren't bound yet), restoring prior expansion from
    /// <paramref name="expansion"/> and defaulting genuinely-new sections to expanded
    /// for roughly the newest two screens. Populates <paramref name="sections"/>.
    /// </summary>
    private List<TileVm> BuildSectionedPure(
        List<LibraryRow> rows, string format, IReadOnlyList<Reel.Core.Settings.SortLevel> levels,
        Func<LibraryRow, Annotation> ann, Dictionary<long, bool> expansion, Dictionary<long, SectionVm> sections)
    {
        var mode = DateBuckets.ChooseMode(rows.Select(r => r.Item.BestDate).ToList());

        var bucketByItem = new Dictionary<long, DateBucket>(rows.Count);
        foreach (var r in rows)
            bucketByItem[r.Item.Id] = DateBuckets.Bucket(r.Item.BestDate, mode);

        LibrarySorter.SortSectioned(rows, r => bucketByItem[r.Item.Id].Key, levels);

        var tiles = new List<TileVm>(rows.Count);
        var counts = new Dictionary<long, int>();
        var order = new List<SectionVm>();

        foreach (var r in rows)
        {
            var bucket = bucketByItem[r.Item.Id];
            counts[bucket.Key] = counts.GetValueOrDefault(bucket.Key) + 1;

            if (!sections.TryGetValue(bucket.Key, out var section))
            {
                section = new SectionVm { Key = bucket.Key, Label = bucket.Label };
                sections[bucket.Key] = section;
                order.Add(section);
            }

            tiles.Add(new TileVm(r, Thumbnails, format, ann(r)) { Section = section });
        }

        long cumulative = 0;
        foreach (var section in order)
        {
            section.Count = counts[section.Key];
            section.IsExpanded = expansion.TryGetValue(section.Key, out var wasExpanded)
                ? wasExpanded                       // preserve the user's toggle across rebuilds
                : cumulative < ExpandTargetItems;   // default: expand the newest ~2 screens
            cumulative += section.Count;
        }

        return tiles;
    }


    private void UpdateStatus()
    {
        UpdateSyncStatus();
        var mediaCount = _mediaTiles.Count;
        var folderCount = _tiles.Count - mediaCount;

        if (_tiles.Count == 0)
        {
            StatusText = Roots.Count == 0
                ? "No folders yet — add one to begin."
                : _searchText.Length > 0 ? "No matches." : "This folder is empty.";
            return;
        }

        if (_searchText.Length > 0)
        {
            StatusText = $"{mediaCount:n0} matches";
            return;
        }

        StatusText = folderCount > 0
            ? $"{mediaCount:n0} items · {folderCount:n0} folder{(folderCount == 1 ? "" : "s")}"
            : $"{mediaCount:n0} items";
    }

    // --- Indexing -----------------------------------------------------------

    private async Task IndexRootAsync(Root root, bool watchAfter)
    {
        // Stream partial results into the grid only on the initial population, so a
        // watcher-triggered re-index never yanks the scroll position out from under
        // someone who is browsing.
        var streaming = _tiles.Count == 0;

        lock (_indexLock)
        {
            _activeIndexers++;
        }
        RunOnUi(UpdateIndexingText);

        var progress = new Progress<IndexProgress>(p => RunOnUi(() => ApplyProgress(root.Id, p, streaming)));

        try
        {
            await Task.Run(() => _library.IndexRoot(root, progress)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Index failed for {root.Alias}: {ex}");
        }
        finally
        {
            lock (_indexLock)
            {
                _activeIndexers--;
            }
            RunOnUi(() =>
            {
                var rootVm = Roots.FirstOrDefault(r => r.Id == root.Id);
                if (rootVm is not null)
                {
                    rootVm.Count = _library.CountForRoot(root.Id);
                    rootVm.Status = "";
                }
                UpdateIndexingText();
                LoadRows();
                BuildViewImmediate();
                BuildTree();
                SelectPath(_location.RootId, _location.RelDir); // restore tree selection/expansion
            });

            if (watchAfter)
                _library.Watch(root);
        }
    }

    private void ApplyProgress(long rootId, IndexProgress p, bool streaming)
    {
        var rootVm = Roots.FirstOrDefault(r => r.Id == rootId);
        if (rootVm is null)
            return;

        rootVm.Status = p.Phase switch
        {
            IndexPhase.Scanning => "scanning…",
            IndexPhase.Indexing => $"indexing {p.Processed:n0} / {p.Total:n0}",
            IndexPhase.Pruning => "cleaning up…",
            _ => "",
        };
        UpdateIndexingText();

        // Throttled live refresh so thumbnails appear as they're indexed.
        if (streaming && p.Phase == IndexPhase.Indexing)
        {
            var now = Environment.TickCount64;
            if (now - _lastStreamRefreshTick > 1200)
            {
                _lastStreamRefreshTick = now;
                LoadRows();
                BuildViewImmediate();
            }
        }
    }

    private void UpdateIndexingText()
    {
        int active;
        lock (_indexLock)
        {
            active = _activeIndexers;
        }

        if (active == 0)
        {
            IndexingText = "";
            return;
        }

        var busy = Roots.Where(r => !string.IsNullOrEmpty(r.Status)).ToList();
        IndexingText = busy.Count == 1
            ? $"{busy[0].Alias}: {busy[0].Status}"
            : $"Indexing {active} folders…";
        UpdateSyncStatus();
    }

    private void OnRootChanged(object? sender, long rootId)
    {
        // Fired on a background thread by the watcher (already debounced).
        var root = _library.GetRoots().FirstOrDefault(r => r.Id == rootId);
        if (root is not null)
            RunOnUi(() => _ = IndexRootAsync(root, watchAfter: false));
    }

    // --- Open ---------------------------------------------------------------

    private void OpenSelected()
    {
        var path = SelectedTile?.FullPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return;

        try
        {
            // Replicate an Explorer double-click as faithfully as possible: shell-execute
            // the file's default "open" verb with the containing folder as the working
            // directory. This is what lets folder-aware viewers (Windows Photos) page
            // through siblings with Back/Forward. (Launching via explorer.exe spawns an
            // extra shell process and doesn't improve that folder context.)
            Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                Verb = "open",
                WorkingDirectory = Path.GetDirectoryName(path) ?? "",
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Open failed: {ex}");
        }
    }

    private void RunOnUi(Action action)
    {
        if (SynchronizationContext.Current == _ui)
            action();
        else
            _ui.Post(_ => action(), null);
    }

    public void Dispose()
    {
        _library.RootChanged -= OnRootChanged;
        _library.Dispose();
    }
}
