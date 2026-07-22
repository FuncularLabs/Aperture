using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;
using Aperture.App.Mvvm;
using Aperture.App.Services;
using Aperture.Core.Annotations;
using Aperture.Core.Formatting;
using Aperture.Core.Library;
using Aperture.Core.Models;

namespace Aperture.App.ViewModels;

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

    // In-flight indexers by root id, so a folder (or all of them) can be paused/cancelled mid-run.
    // Guarded by _indexLock, as is _pausedRoots: roots stopped by an explicit Pause rather than a
    // Cancel, which are the ones that offer Resume.
    private readonly Dictionary<long, CancellationTokenSource> _indexCts = [];
    private readonly HashSet<long> _pausedRoots = [];

    private bool _isLoadingView;
    private CancellationTokenSource? _buildDebounce;
    private int _buildGeneration;

    private List<IGridItem> _tiles = [];
    private object? _selectedItem;
    private int _zoom;
    private string _searchText = "";
    private string _statusText = "No folders yet — add one to begin.";
    private string _indexingText = "";
    private bool _isIndexing;
    private bool _indexIndeterminate = true;
    private double _indexValue;
    private double _indexMax;

    // Folder-tree nodes the user left expanded ("rootId|relDir"). Null means nothing has ever been
    // saved, which is what makes a fresh install differ from "the user collapsed everything".
    private HashSet<string>? _expandedFolders;
    private bool _restoringExpansion;

    // Folder navigation: null root = home (all included roots).
    private (long? RootId, string RelDir) _location = (null, "");
    private readonly Stack<(long? RootId, string RelDir)> _back = new();
    private List<TileVm> _mediaTiles = [];
    private IReadOnlyList<TileVm> _selectedTiles = [];
    private List<LibraryRow> _allRows = [];

    // The current view's tiles, keyed by item id. A rebuild that keeps the same items (streaming index,
    // background reconcile, watcher, filter/sort) reuses these instances instead of allocating new ones —
    // preserving each tile's already-decoded thumbnail so the grid doesn't blank-then-repaint every cell.
    // Owned by the UI thread: ApplyView reassigns a fresh dict (scoped to the new view, so retained
    // thumbnails stay bounded), which a compute still running off-thread never sees mutated. Invalidated on
    // annotation/caption changes; a per-item mtime check refreshes tiles whose underlying file was re-indexed.
    private Dictionary<long, TileVm> _tilePool = [];
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
        PauseRootIndexCommand = new RelayCommand<RootVm>(PauseRootIndex);
        ResumeRootIndexCommand = new RelayCommand<RootVm>(ResumeRootIndex);
        CancelRootIndexCommand = new RelayCommand<RootVm>(CancelRootIndex);
        PauseAllIndexingCommand = new RelayCommand(PauseAllIndexing);
        ResumeAllIndexingCommand = new RelayCommand(ResumeAllIndexing);
        CancelAllIndexingCommand = new RelayCommand(CancelAllIndexing);
        ZoomInCommand = new RelayCommand(ZoomIn, () => _zoom < ZoomSizes.Length - 1);
        ZoomOutCommand = new RelayCommand(ZoomOut, () => _zoom > 0);
        OpenSelectedCommand = new RelayCommand(OpenSelected, () => SelectedTile is not null);
        ClearSearchCommand = new RelayCommand(() => { SearchInput = ""; CommitSearch(); });
        QuickPickCommand = new RelayCommand<string>(QuickPickTag);
        OpenQuickLookCommand = new RelayCommand(OpenQuickLook, () => _tiles.Count > 0);
        CloseQuickLookCommand = new RelayCommand(CloseQuickLook);
        QuickLookNextCommand = new RelayCommand(() => MoveQuickLook(1));
        QuickLookPrevCommand = new RelayCommand(() => MoveQuickLook(-1));

        AddChosenFoldersCommand = new RelayCommand(AddChosenFolders);
        SkipFirstRunCommand = new RelayCommand(SkipFirstRun);
        RefreshCommand = new RelayCommand(RefreshView);
        CopyFullPathCommand = new RelayCommand<TileVm>(t => CopyText(QuotePathIfNeeded(t?.FullPath)));
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
        ImportAnnotationsCommand = new RelayCommand(ImportAnnotations);
        ExportAnnotationsCommand = new RelayCommand(ExportAnnotations);
        OpenReadmeCommand = new RelayCommand(OpenReadme);
        NavigateHomeCommand = new RelayCommand(() => NavigateTo(null, ""));
        NavigateUpCommand = new RelayCommand(NavigateUp, () => !IsHome);
        NavigateBackCommand = new RelayCommand(NavigateBack, () => _back.Count > 0);
        NavigateCrumbCommand = new RelayCommand<BreadcrumbVm>(c => { if (c is not null) NavigateTo(c.RootId, c.RelDir); });
        ActivateItemCommand = new RelayCommand<object>(ActivateItem);

        _library.RootChanged += OnRootChanged;

        // Null (never saved) leaves RestoreExpansion to fall back to "roots expanded".
        _expandedFolders = _library.Settings.Current.ExpandedFolders is { } saved ? [.. saved] : null;

        LoadRoots();
        LoadAnnotations();
        InitializeViewAsync(); // reads the union + builds the first view off the UI thread (spinner meanwhile)
        MaybeStartFirstRun();
    }

    /// <summary>
    /// Startup view build. The union read and the tile/section compute — the heavy part on a large
    /// library — run off the UI thread behind the loading spinner, so the window paints immediately
    /// and the keyboard is responsive instead of frozen while the first view is prepared.
    /// </summary>
    private async void InitializeViewAsync()
    {
        var generation = ++_buildGeneration;
        IsLoadingView = true;

        _allRows = await Task.Run(_library.GetUnion);
        OnPropertyChanged(nameof(TotalItemCount));
        if (generation != _buildGeneration)
            return; // a data-change rebuild already superseded startup

        // Build the grid FIRST — it's the content the user came to see. The union read and the
        // tile/section compute run off the UI thread; only ApplyView touches the UI, so the spinner
        // animates for most of the wait and the tiles appear as soon as possible.
        var inputs = CaptureBuildInputs();
        ViewBuild build;
        try { build = await Task.Run(() => ComputeView(inputs)); }
        catch (OperationCanceledException) { return; }
        if (generation != _buildGeneration)
            return;

        ApplyView(build);
        IsLoadingView = false; // tiles are up — drop the spinner even though the tree is still to come

        // The folder tree fills in afterward; it doesn't gate seeing the photos.
        BuildTree();
        SelectDefaultNode();
    }

    private void LoadAnnotations()
    {
        _annotations = _library.GetAllAnnotations();
        // Dialog suggestions use the blended popularity-vs-recency order (most relevant first).
        _availableTags = TagQuickPicks.Order(_library.GetTagUsage());
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
        _tilePool = []; // a bulk edit can touch items outside the current view; drop pooled (possibly stale) tiles
        LoadAnnotations();
        foreach (var tile in _mediaTiles)
            tile.UpdateAnnotation(AnnotationFor(tile.Row));
    }

    /// <summary>Pushed from the grid on selection change; media tiles only.</summary>
    public void SetSelectedItems(System.Collections.IEnumerable items) =>
        _selectedTiles = items.OfType<TileVm>().ToList();

    private void SelectDefaultNode()
    {
        // Default landing view is "Everything" (the union). The Home view was already built
        // during construction, so just make sure no folder node is left highlighted.
        if (_location.RootId is null)
            ClearTreeSelection(FolderTree);
        else
            SelectPath(_location.RootId, _location.RelDir);
    }

    public ThumbnailService Thumbnails { get; }

    /// <summary>"Aperture Image Viewer vX.Y.Z[-suffix]" — from InformationalVersion so a prerelease tag shows.</summary>
    public string AppVersion
    {
        get
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var info = asm.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var version = info?.Split('+')[0]                      // drop any +<commit> build metadata
                          ?? asm.GetName().Version?.ToString(3) ?? "0.7.0";
            return "Aperture Image Viewer v" + version;
        }
    }

    private string _windowTitle = "Aperture Image Viewer";

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
            WindowTitle = $"{AppVersion}  —  {full}";
        }
        else
        {
            WindowTitle = AppVersion;
        }
    }

    public ObservableCollection<RootVm> Roots { get; } = [];

    /// <summary>Root nodes of the left folder tree (roots → lazy subfolders).</summary>
    public ObservableCollection<FolderNodeVm> FolderTree { get; } = [];

    /// <summary>Item count across all included folders — the badge on the "Everything" library node.</summary>
    public int TotalItemCount => _allRows.Count;

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
                // Selecting a media tile (e.g. a mouse click) moves the keyboard cursor off any date-section
                // header, so Space/Enter act on the tile (quick-look / open) instead of collapsing the section.
                if (value is TileVm)
                {
                    ClearCursorHighlight();
                    _cursorSection = null;
                }
                OnPropertyChanged(nameof(SelectedTile));
                OnPropertyChanged(nameof(PreviewTile)); // keep current even when the inspector pane is closed (quick-look menu depends on it)
                LoadPreview(); // no-op when the preview pane is closed
            }
        }
    }

    /// <summary>The selection when it is a media tile (null when a folder is selected).</summary>
    public TileVm? SelectedTile => _selectedItem as TileVm;

    public double TileSize => ZoomSizes[_zoom];

    private string _searchInput = "";
    private CancellationTokenSource? _searchDebounce;
    private const int SearchDebounceMs = 700;

    /// <summary>
    /// The live text in the search box. Applying it to the grid filter is debounced
    /// (~0.7s idle) so we don't re-filter the whole library on every keystroke; Enter
    /// (or a quick-pick) applies it immediately via <see cref="CommitSearch"/>.
    /// </summary>
    public string SearchInput
    {
        get => _searchInput;
        set
        {
            if (!SetProperty(ref _searchInput, value))
                return;
            _searchDebounce?.Cancel();
            var cts = new CancellationTokenSource();
            _searchDebounce = cts;
            _ = DebouncedCommitSearch(cts.Token);
        }
    }

    private async Task DebouncedCommitSearch(CancellationToken token)
    {
        try { await Task.Delay(SearchDebounceMs, token); }
        catch (OperationCanceledException) { return; }
        CommitSearch();
    }

    /// <summary>Applies the current search input to the filter now (Enter key, quick-pick, clear).</summary>
    public void CommitSearch()
    {
        _searchDebounce?.Cancel();
        var applied = _searchInput.Trim();
        if (applied == _searchText.Trim())
            return;
        _searchText = applied;
        RequestBuildView();
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
        var current = _searchInput.TrimEnd();
        SearchInput = current.Length == 0 ? clause : $"{current} {clause}";
        CommitSearch(); // apply the picked tag immediately
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
            _tilePool = []; // captions are baked into each tile at construction; rebuild them with the new format
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
    public sealed record SortOption(string Label, List<Aperture.Core.Settings.SortLevel> Levels, bool Grouped);

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

    /// <summary>True while any folder is being indexed — drives the status-bar progress bar.</summary>
    public bool IsIndexing
    {
        get => _isIndexing;
        private set => SetProperty(ref _isIndexing, value);
    }

    /// <summary>Indexing progress bar: indeterminate while scanning/pruning, determinate while indexing files.</summary>
    public bool IndexIndeterminate
    {
        get => _indexIndeterminate;
        private set => SetProperty(ref _indexIndeterminate, value);
    }

    public double IndexValue
    {
        get => _indexValue;
        private set => SetProperty(ref _indexValue, value);
    }

    public double IndexMax
    {
        get => _indexMax;
        private set => SetProperty(ref _indexMax, value);
    }

    public ICommand AddRootCommand { get; }
    public ICommand RemoveRootCommand { get; }

    // Indexing control — per folder (from its nav menu) and across the board (status bar).
    public ICommand PauseRootIndexCommand { get; }
    public ICommand ResumeRootIndexCommand { get; }
    public ICommand CancelRootIndexCommand { get; }
    public ICommand PauseAllIndexingCommand { get; }
    public ICommand ResumeAllIndexingCommand { get; }
    public ICommand CancelAllIndexingCommand { get; }
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
    public ICommand RefreshCommand { get; }

    /// <summary>
    /// F5 / View→Refresh: re-read the library and rebuild the grid from scratch. This is also the graceful
    /// recovery when the tile display gets wonky — stale/garbled tiles after alt-tabbing, or a file deleted
    /// out from under the preview — since it discards the current tiles and rebuilds fresh ones.
    /// </summary>
    public void RefreshView()
    {
        _tilePool = []; // recovery path: rebuild genuinely fresh tiles (also picks up external annotation edits)
        LoadAnnotations(); // also picks up any external tag/note edits
        LoadRows();
        RequestBuildView();
    }

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
        var tile = SelectedTile;
        PreviewExif = BuildExif(tile);

        var gen = ++_previewGen;
        PreviewImage = null;
        if (tile is null)
            return;

        var path = tile.FullPath;
        var isVideo = tile.IsVideo;
        var itemId = tile.ItemId;
        var mtime = tile.Item.MTimeUtc.Ticks;
        var image = await Task.Run(() => isVideo ? DecodeThumbnail(itemId, path, mtime) : ImageLoading.LoadFullImageUpright(path, 2400));
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

        foreach (var (label, value) in Aperture.Core.Media.MetadataReader.ReadExifSummary(tile.FullPath))
            if (!rows.Any(r => r.Label == label)) // don't duplicate Camera etc.
                rows.Add(new(label, value));

        rows.Add(new("Path", tile.FullPath));
        return rows;
    }
    public ICommand EditAnnotationCommand { get; }
    public ICommand ManageTagsCommand { get; }
    public ICommand ImportAnnotationsCommand { get; }
    public ICommand ExportAnnotationsCommand { get; }
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

    private void ExportAnnotations()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export tags & notes",
            Filter = "Aperture tags (*.json)|*.json|All files (*.*)|*.*",
            FileName = $"aperture-tags-{DateTime.Now:yyyy-MM-dd}.json",
            DefaultExt = ".json",
            AddExtension = true,
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var count = _library.ExportAnnotations(dialog.FileName);
            Notify($"Exported tags & notes for {count:n0} item(s).");
        }
        catch (Exception ex)
        {
            Notify($"Export failed:\n{ex.Message}", warn: true);
        }
    }

    private void ImportAnnotations()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import tags & notes",
            Filter = "Aperture tags (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var summary = _library.ImportAnnotations(dialog.FileName);
            RefreshAllAnnotationsInPlace(); // badges + quick-pick tags reflect the merged set

            var message = $"Applied tags & notes to {summary.Applied:n0} item(s).";
            if (summary.Unresolved > 0)
                message += $"\n{summary.Unresolved:n0} could not be matched to a local folder.";
            if (summary.Skipped > 0)
                message += $"\n{summary.Skipped:n0} empty entr(ies) skipped.";
            Notify(message);
        }
        catch (Exception ex)
        {
            Notify($"Import failed:\n{ex.Message}", warn: true);
        }
    }

    private static void Notify(string message, bool warn = false) =>
        System.Windows.MessageBox.Show(
            message, "Aperture",
            System.Windows.MessageBoxButton.OK,
            warn ? System.Windows.MessageBoxImage.Warning : System.Windows.MessageBoxImage.Information);
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
            _searchDebounce?.Cancel();
            _searchInput = "";
            _searchText = "";
            OnPropertyChanged(nameof(SearchInput));
            SelectedItem = null;
            RequestBuildView(); // debounced + async so rapid tree navigation doesn't queue
            OnPropertyChanged(nameof(IsHome));
            if (rootId is null)
                ClearTreeSelection(FolderTree); // "Everything" — no folder node should stay highlighted
            else
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

    /// <summary>
    /// Wraps a path in double quotes only when it contains whitespace (and isn't already quoted), so it
    /// pastes cleanly as a single shell argument — but plain paths stay unquoted for easy reuse.
    /// </summary>
    private static string? QuotePathIfNeeded(string? path) =>
        string.IsNullOrEmpty(path) || !path.Any(char.IsWhiteSpace)
            ? path
            : path.StartsWith('"') && path.EndsWith('"') ? path : $"\"{path}\"";

    private void CopyImage(TileVm? tile)
    {
        if (tile is null)
            return;

        // Pics: the full-resolution image, EXIF-oriented like the tile/Photos.
        // Videos: the cached frame thumbnail.
        var image = tile.IsVideo
            ? DecodeThumbnail(tile.ItemId, tile.FullPath, tile.Item.MTimeUtc.Ticks)
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

    /// <summary>The section the cursor is on (a header), for scrolling a collapsed title into view.</summary>
    public SectionVm? CursorSection => _cursorSection;

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

    /// <summary>Selects the first media tile (skipping a leading date-section header) — used when Tabbing into the grid.</summary>
    public object? MoveCursorToFirstItem()
    {
        var nav = BuildNav();
        var idx = nav.FindIndex(s => !s.IsHeader);
        return idx < 0 ? null : ApplyCursor(nav, idx);
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

        var candidates = Aperture.Core.Library.FolderDetection.DetectCandidates();
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
        // The selected tile; or, when the cursor sits on a date-section header, that section's first tile.
        var tile = SelectedTile
                   ?? (_cursorSection is { } section ? FirstItemOfSection(section) as TileVm : null);
        var start = tile is not null ? _mediaTiles.IndexOf(tile) : 0;
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
            ? DecodeThumbnail(tile.ItemId, tile.FullPath, tile.Item.MTimeUtc.Ticks)
            : ImageLoading.LoadFullImageUpright(tile.FullPath, maxLongestEdge: 1600));

        // Ignore if the user moved on while we decoded.
        if (_quickLookOpen && _quickLookIndex == index)
            QuickLookImage = image;
    }

    private System.Windows.Media.Imaging.BitmapSource? DecodeThumbnail(long itemId, string path, long srcMtimeTicks)
    {
        var bytes = _library.GetOrCreateThumbnail(itemId, path, ThumbSize.Large, srcMtimeTicks, isVideo: true);
        return bytes is null ? null : ImageLoading.Decode(bytes, 0);
    }

    /// <summary>Kick a background reconcile of every root, then watch it. Cached rows are already on screen.</summary>
    public async void StartBackgroundRefresh()
    {
        // Give the window a moment to paint and the on-screen tiles to load (or regenerate) their
        // thumbnails before a full reconcile starts contending for disk + CPU. Without this, indexing
        // tens of thousands of files immediately starves the visible tiles and they sit blank.
        await Task.Delay(StartupReconcileDelayMs);
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

    /// <summary>Saved native window placement (size/position/monitor/maximized), or null on first run.</summary>
    public int[]? GetWindowPlacement() => _library.Settings.Current.WindowPlacement;

    public void SaveWindowPlacement(int[] placement) =>
        _library.Settings.Update(s => s.WindowPlacement = placement);

    /// <summary>Saved width of the left folder pane, or null on first run.</summary>
    public double? GetNavWidth() => _library.Settings.Current.NavWidth;

    public void SaveNavWidth(double width) =>
        _library.Settings.Update(s => s.NavWidth = width);

    // --- Roots --------------------------------------------------------------

    private void LoadRoots()
    {
        Roots.Clear();
        foreach (var root in _library.GetRoots())
        {
            var vm = new RootVm(root, OnRootIncludedChanged, OnRootAliasChanged, OnRootRecursiveChanged)
            {
                Count = _library.CountForRoot(root.Id),
            };
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

        // The OS folder picker can't host extra controls, so the subfolder choice is a second step.
        var confirm = new AddFolderDialog(path) { Owner = System.Windows.Application.Current.MainWindow };
        if (confirm.ShowDialog() != true)
            return;

        AddRootPath(path, DeriveAlias(path), confirm.IncludeSubfolders);
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

    private void AddRootPath(string path, string alias, bool recursive = true)
    {
        var root = _library.AddRoot(path, alias, recursive);
        var vm = new RootVm(root, OnRootIncludedChanged, OnRootAliasChanged, OnRootRecursiveChanged);
        Roots.Add(vm);
        BuildTree();
        NavigateTo(root.Id, ""); // jump to the new folder so its thumbnails stream in as they're indexed
        _ = IndexRootAsync(root, watchAfter: true, forceStream: true);
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

    /// <summary>
    /// Subfolder indexing toggled on a root: persist it, drop the watcher (it's recreated at the new
    /// depth by the re-index), then re-index. Turning it off prunes the now-out-of-scope items;
    /// turning it back on picks them up again.
    /// </summary>
    private void OnRootRecursiveChanged(RootVm rootVm, bool recursive)
    {
        _library.SetRecursive(rootVm.Id, recursive);
        _library.StopWatching(rootVm.Id);
        _ = IndexRootAsync(rootVm.Model, watchAfter: true);
    }

    private static string DeriveAlias(string path)
    {
        var name = new DirectoryInfo(path).Name;
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    // --- Union / sections / status ------------------------------------------

    private const int BuildDebounceMs = 140;

    /// <summary>How long startup waits before the first full reconcile, so visible thumbnails load uncontended.</summary>
    private const int StartupReconcileDelayMs = 2500;

    /// <summary>True while an async rebuild is running — drives the right-pane spinner.</summary>
    public bool IsLoadingView
    {
        get => _isLoadingView;
        private set => SetProperty(ref _isLoadingView, value);
    }

    /// <summary>Re-reads the union of included items. Call only when the underlying data changed.</summary>
    private void LoadRows()
    {
        _allRows = _library.GetUnion();
        OnPropertyChanged(nameof(TotalItemCount));
    }

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

        try
        {
            var build = await Task.Run(() => ComputeView(inputs), token);
            if (generation == _buildGeneration) // a newer request may have superseded this one
                ApplyView(build);
        }
        catch (OperationCanceledException)
        {
            // Superseded mid-compute; the newer request owns the spinner from here.
        }
        finally
        {
            // Only the newest request clears the spinner: a superseded build must not hide it while
            // the current one is still working, and *some* path must always clear it or the grid
            // stays stuck behind "Loading…" (which a cancelled/superseded build used to do).
            if (generation == _buildGeneration)
                IsLoadingView = false;
        }
    }

    private sealed record BuildInputs(
        List<LibraryRow> Rows, string Format, SortOption Sort, string Query,
        (long? RootId, string RelDir) Location, bool GroupByDate,
        Dictionary<string, Annotation> Annotations, Dictionary<long, bool> Expansion,
        bool ShowImages, bool ShowVideos, Dictionary<long, TileVm> Pool);

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
        _showVideos,
        _tilePool);

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
            tiles = BuildSectionedPure(media, input.Format, input.Sort.Levels, Ann, input.Expansion, sections, input.Pool);
        }
        else
        {
            LibrarySorter.Sort(media, input.Sort.Levels);
            tiles = media.Select(r => GetOrReuseTile(input.Pool, r, input.Format, Ann(r))).ToList();
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

        // Bind each committed tile to its committed section here, on the UI thread — never off-thread,
        // where a reused tile could be shared with a superseded concurrent build and race.
        if (build.UseSections)
            foreach (var section in build.Sections.Values)
                foreach (var t in section.Members)
                    t.Section = section;

        _mediaTiles = build.MediaTiles;
        _tiles = build.Tiles;

        // Reuse pool = exactly this view's tiles (a fresh dict, so a compute still running off-thread keeps
        // reading the snapshot it captured). Scoped to the current view — not accumulated across views —
        // so retained decoded thumbnails stay bounded by the live view, never growing unbounded per session.
        var pool = new Dictionary<long, TileVm>(build.MediaTiles.Count);
        foreach (var t in build.MediaTiles)
            pool[t.ItemId] = t;
        _tilePool = pool;

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
        // "Everything" (no root) shows the whole union; a folder shows its direct media
        // (subfolders live in the tree, so they aren't duplicated as tiles here).
        if (loc.RootId is null)
            return [.. rows];

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
                rootVm, rootVm.Count, LoadChildNodes, OnNodeExpandedChanged));
        }
        RestoreExpansion();
    }

    /// <summary>
    /// Re-opens the folders the user left expanded — on launch and after every background rebuild —
    /// instead of expanding the whole tree. A library that has never saved any state (fresh install)
    /// opens with just the root folders expanded; a saved-but-empty set means the user collapsed
    /// everything, and is honoured.
    /// </summary>
    private void RestoreExpansion()
    {
        _restoringExpansion = true; // applying saved state must not write it back
        try
        {
            if (_expandedFolders is null)
            {
                foreach (var node in FolderTree)
                    node.IsExpanded = true;
                return;
            }
            foreach (var node in FolderTree)
                RestoreExpansion(node);
        }
        finally
        {
            _restoringExpansion = false;
        }
    }

    private void RestoreExpansion(FolderNodeVm node)
    {
        if (!node.HasChildren || _expandedFolders?.Contains(node.Key) != true)
            return;
        node.IsExpanded = true; // lazily loads this node's children
        foreach (var child in node.Children)
            RestoreExpansion(child);
    }

    /// <summary>Records an expand/collapse so the tree comes back this way next launch.</summary>
    private void OnNodeExpandedChanged(FolderNodeVm node, bool expanded)
    {
        if (_restoringExpansion)
            return;
        _expandedFolders ??= [];
        if (expanded)
            _expandedFolders.Add(node.Key);
        else
            _expandedFolders.Remove(node.Key);
        _library.Settings.Update(s => s.ExpandedFolders = [.. _expandedFolders]);
    }

    private List<FolderNodeVm> LoadChildNodes(long rootId, string relDir) =>
        GetSubfolders(rootId, relDir)
            .Select(s => new FolderNodeVm(
                rootId, s.RelDir, s.Name, isRoot: false, s.HasChildren, null, s.Count,
                LoadChildNodes, OnNodeExpandedChanged))
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

    /// <summary>Clears selection on every loaded tree node — used when navigating to "Everything".</summary>
    private static void ClearTreeSelection(IEnumerable<FolderNodeVm> nodes)
    {
        foreach (var node in nodes)
        {
            node.IsSelected = false;
            if (node.Children.Count > 0)
                ClearTreeSelection(node.Children);
        }
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
    /// The pooled tile for this row when it is still current (same item, unchanged file mtime), else a
    /// fresh one. Reuse preserves the tile's decoded thumbnail so a rebuild doesn't repaint every cell
    /// blank; the mtime guard means a re-indexed (edited) file still gets a fresh tile. Safe off-thread:
    /// only reads the captured pool snapshot and constructs new tiles (which decode lazily on the UI thread).
    /// </summary>
    private TileVm GetOrReuseTile(IReadOnlyDictionary<long, TileVm> pool, LibraryRow r, string format, Annotation ann)
        => pool.TryGetValue(r.Item.Id, out var existing) && existing.Row.Item.MTimeUtc == r.Item.MTimeUtc
            ? existing
            : new TileVm(r, Thumbnails, format, ann);

    /// <summary>
    /// Pure sectioned build: creates fresh <see cref="SectionVm"/>s (safe to build off
    /// the UI thread since they aren't bound yet), restoring prior expansion from
    /// <paramref name="expansion"/> and defaulting genuinely-new sections to expanded
    /// for roughly the newest two screens. Populates <paramref name="sections"/>.
    /// </summary>
    private List<TileVm> BuildSectionedPure(
        List<LibraryRow> rows, string format, IReadOnlyList<Aperture.Core.Settings.SortLevel> levels,
        Func<LibraryRow, Annotation> ann, Dictionary<long, bool> expansion, Dictionary<long, SectionVm> sections,
        Dictionary<long, TileVm> pool)
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

            // Record membership; tile.Section is assigned on the UI thread in ApplyView so a
            // tile shared with a concurrent build is never written off-thread (see _tilePool).
            var tile = GetOrReuseTile(pool, r, format, ann(r));
            section.Members.Add(tile);
            tiles.Add(tile);
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

    private async Task IndexRootAsync(Root root, bool watchAfter, bool forceStream = false)
    {
        // Stream partial results into the grid on the initial population, or when the caller is showing
        // the folder being indexed (a freshly added root) — so its thumbnails appear as they're found.
        // Otherwise a watcher/reconcile re-index never yanks the scroll out from under someone browsing.
        var streaming = _tiles.Count == 0 || forceStream;

        var cts = new CancellationTokenSource();
        lock (_indexLock)
        {
            _activeIndexers++;
            _indexCts[root.Id] = cts;
            _pausedRoots.Remove(root.Id); // starting/resuming clears any previous pause
        }
        RunOnUi(() =>
        {
            if (Roots.FirstOrDefault(r => r.Id == root.Id) is { } vm)
            {
                vm.IsIndexing = true;
                vm.IsPaused = false;
            }
            UpdateIndexingText();
        });

        var progress = new Progress<IndexProgress>(p => RunOnUi(() => ApplyProgress(root.Id, p, streaming)));

        IndexResult? result = null;
        var canceled = false;
        try
        {
            result = await Task.Run(() => _library.IndexRoot(root, progress, cts.Token)).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Paused or cancelled by the user. Whatever was committed before the stop stays — the
            // indexer skips its prune step when it's cancelled, so nothing is wrongly deleted.
            canceled = true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Index failed for {root.Alias}: {ex}");
        }
        finally
        {
            bool paused;
            lock (_indexLock)
            {
                _activeIndexers--;
                _indexCts.Remove(root.Id);
                paused = canceled && _pausedRoots.Contains(root.Id);
                cts.Dispose(); // inside the lock — StopIndex cancels under it (see StopIndex)
            }
            RunOnUi(() =>
            {
                var rootVm = Roots.FirstOrDefault(r => r.Id == root.Id);
                if (rootVm is not null)
                {
                    rootVm.Count = _library.CountForRoot(root.Id);
                    rootVm.Status = paused ? "paused" : "";
                    rootVm.IsIndexing = false;
                    rootVm.IsPaused = paused;
                }
                OnPropertyChanged(nameof(HasPausedRoots));
                UpdateIndexingText();

                // Only rebuild when the index actually changed the library (or we streamed partials
                // and need a final complete pass). A no-op reconcile — the common case a couple of
                // seconds after startup — must not rebuild the grid + tree and repaint everything.
                // A cancelled run may have committed partial work, so show that too.
                var added = result?.Added ?? 0;
                var removed = result?.Removed ?? 0;
                var dataChanged = streaming || canceled || added + (result?.Updated ?? 0) + removed > 0;
                if (dataChanged)
                {
                    LoadRows();
                    RequestBuildView(); // debounced + off-thread + tile reuse, so the reconcile stays smooth
                }
                if (streaming || canceled || added + removed > 0) // folders shift when items are added/removed
                {
                    BuildTree();
                    SelectPath(_location.RootId, _location.RelDir); // restore tree selection/expansion
                }
            });

            // Don't re-arm the watcher on a stopped run — a file event would restart the very
            // indexing the user just paused.
            if (watchAfter && !canceled)
                _library.Watch(root);
        }
    }

    /// <summary>True when at least one folder's indexing is paused and can be resumed.</summary>
    public bool HasPausedRoots
    {
        get { lock (_indexLock) { return _pausedRoots.Count > 0; } }
    }

    /// <summary>
    /// Stops an in-flight index. Cancelling the token unwinds the indexer, which keeps everything it
    /// already committed and skips its prune step, so a stop never loses or wrongly deletes items.
    /// A <paramref name="pause"/> is remembered so the folder offers Resume; a cancel just stops.
    /// </summary>
    private void StopIndex(long rootId, bool pause)
    {
        lock (_indexLock)
        {
            if (!_indexCts.TryGetValue(rootId, out var cts))
                return; // nothing running for this root
            if (pause)
                _pausedRoots.Add(rootId);
            else
                _pausedRoots.Remove(rootId);
            // Cancel under the same lock the runner disposes under, so we can never cancel a
            // token source it just disposed (which would throw out of the command).
            cts.Cancel();
        }

        // Stop watching too: otherwise a file change would immediately restart the very indexing
        // the user just stopped. Resuming re-arms the watcher.
        _library.StopWatching(rootId);
    }

    private void PauseRootIndex(RootVm? rootVm)
    {
        if (rootVm is not null)
            StopIndex(rootVm.Id, pause: true);
    }

    private void CancelRootIndex(RootVm? rootVm)
    {
        if (rootVm is not null)
            StopIndex(rootVm.Id, pause: false);
    }

    /// <summary>Re-runs the index for a paused folder. Already-indexed files are skipped, not re-read.</summary>
    private void ResumeRootIndex(RootVm? rootVm)
    {
        if (rootVm is null)
            return;
        lock (_indexLock)
        {
            _pausedRoots.Remove(rootVm.Id);
        }
        rootVm.IsPaused = false;
        rootVm.Status = "";
        OnPropertyChanged(nameof(HasPausedRoots));
        _ = IndexRootAsync(rootVm.Model, watchAfter: true);
    }

    private void PauseAllIndexing()
    {
        foreach (var id in RunningIndexRootIds())
            StopIndex(id, pause: true);
    }

    private void CancelAllIndexing()
    {
        foreach (var id in RunningIndexRootIds())
            StopIndex(id, pause: false);
    }

    private void ResumeAllIndexing()
    {
        List<long> ids;
        lock (_indexLock)
        {
            ids = [.. _pausedRoots];
        }
        foreach (var id in ids)
            if (Roots.FirstOrDefault(r => r.Id == id) is { } vm)
                ResumeRootIndex(vm);
    }

    private List<long> RunningIndexRootIds()
    {
        lock (_indexLock)
        {
            return [.. _indexCts.Keys];
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

        // Drive the status-bar progress bar: a real fraction while indexing files, otherwise indeterminate.
        if (p.Phase == IndexPhase.Indexing && p.Total > 0)
        {
            IndexIndeterminate = false;
            IndexMax = p.Total;
            IndexValue = p.Processed;
        }
        else
        {
            IndexIndeterminate = true;
        }
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

        IsIndexing = active > 0;

        if (active == 0)
        {
            IndexingText = "";
            IndexIndeterminate = true; // reset for the next run
            // Refresh the pill here too: a reconcile that changed nothing skips the view rebuild that
            // would otherwise have done it, which would leave the pill reading "indexing…" forever.
            UpdateSyncStatus();
            return;
        }

        var busy = Roots.Where(r => !string.IsNullOrEmpty(r.Status)).ToList();
        IndexingText = busy.Count == 1
            ? $"{busy[0].Alias}: {busy[0].Status}"
            : $"Indexing {active} folder{(active == 1 ? "" : "s")}…";
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
