using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;
using Reel.App.Mvvm;
using Reel.App.Services;
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
    private SectionVm? _folderSection;

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
        NavigateHomeCommand = new RelayCommand(() => NavigateTo(null, ""));
        NavigateUpCommand = new RelayCommand(NavigateUp, () => !IsHome);
        NavigateBackCommand = new RelayCommand(NavigateBack, () => _back.Count > 0);
        NavigateCrumbCommand = new RelayCommand<BreadcrumbVm>(c => { if (c is not null) NavigateTo(c.RootId, c.RelDir); });
        ActivateItemCommand = new RelayCommand<object>(ActivateItem);

        _library.RootChanged += OnRootChanged;

        LoadRoots();
        BuildView();
        MaybeStartFirstRun();
    }

    public ThumbnailService Thumbnails { get; }

    public ObservableCollection<RootVm> Roots { get; } = [];

    /// <summary>The grouped, sorted, filtered view the grid binds to.</summary>
    public ICollectionView? ItemsView => _view.View;

    /// <summary>The selected grid item (media tile or folder tile).</summary>
    public object? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (SetProperty(ref _selectedItem, value))
                OnPropertyChanged(nameof(SelectedTile));
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
                BuildView();
        }
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
            BuildView();
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
            BuildView();
        }
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
            BuildView();
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
    public ICommand NavigateHomeCommand { get; }
    public ICommand NavigateUpCommand { get; }
    public ICommand NavigateBackCommand { get; }
    public ICommand NavigateCrumbCommand { get; }
    public ICommand ActivateItemCommand { get; }

    // --- Folder navigation --------------------------------------------------

    /// <summary>The location breadcrumb (Home / root / subfolder …).</summary>
    public ObservableCollection<BreadcrumbVm> Breadcrumb { get; } = [];

    public bool IsHome => _location.RootId is null && _location.RelDir.Length == 0;

    private void NavigateTo(long? rootId, string relDir, bool pushBack = true)
    {
        if (pushBack && (_location.RootId != rootId || !string.Equals(_location.RelDir, relDir, StringComparison.OrdinalIgnoreCase)))
            _back.Push(_location);
        _location = (rootId, relDir);
        _searchText = "";          // clear the filter without a redundant rebuild
        OnPropertyChanged(nameof(SearchText));
        SelectedItem = null;
        BuildView();
        OnPropertyChanged(nameof(IsHome));
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

        // Pics: the full-resolution image. Videos: the cached frame thumbnail.
        var image = tile.IsVideo
            ? DecodeThumbnail(tile.ItemId)
            : ImageLoading.LoadFullImage(tile.FullPath, decodePixelWidth: 0);
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
        if (_folderSection is not null)
            _folderSection.IsCursor = false;
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
            : ImageLoading.LoadFullImage(tile.FullPath));

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
        BuildView(); // captions reference {alias}
    }

    private void AddRoot()
    {
        var dialog = new OpenFolderDialog { Title = "Add a folder to Reel" };
        if (dialog.ShowDialog() != true)
            return;

        var path = dialog.FolderName;
        if (Roots.Any(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase)))
            return; // already added

        AddRootPath(path, DeriveAlias(path));
    }

    private void AddRootPath(string path, string alias)
    {
        var root = _library.AddRoot(path, alias);
        var vm = new RootVm(root, OnRootIncludedChanged, OnRootAliasChanged);
        Roots.Add(vm);
        _ = IndexRootAsync(root, watchAfter: true);
    }

    private void RemoveRoot(RootVm? rootVm)
    {
        if (rootVm is null)
            return;
        _library.RemoveRoot(rootVm.Id);
        Roots.Remove(rootVm);
        BuildView();
    }

    private void OnRootIncludedChanged(RootVm rootVm, bool included)
    {
        _library.SetIncluded(rootVm.Id, included);
        BuildView();
    }

    private static string DeriveAlias(string path)
    {
        var name = new DirectoryInfo(path).Name;
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    // --- Union / sections / status ------------------------------------------

    private void BuildView()
    {
        ClearCursorHighlight();
        _cursorSection = null;

        var rows = _library.GetUnion();

        var format = _library.Settings.Current.CaptionFormat;
        var sort = CurrentSort;
        var levels = sort.Levels;
        var query = _searchText.Trim();

        // Searching = a flat, recursive result under the current location (Explorer-style);
        // otherwise show this folder's subfolders as tiles plus its direct media.
        List<FolderTileVm> folders = [];
        List<LibraryRow> media;
        if (query.Length > 0)
            media = ScopeRecursive(rows).Where(r => Matches(r, query)).ToList();
        else
            (folders, media) = ComputeFolderView(rows);

        // Only date sorts get collapsible date sections; other sorts flatten to a global list.
        var useSections = GroupByDate && sort.Grouped && (media.Count > 0 || folders.Count > 0);

        List<TileVm> mediaTiles;
        if (useSections && media.Count > 0)
        {
            mediaTiles = BuildSectioned(media, format, levels);
        }
        else
        {
            LibrarySorter.Sort(media, levels);
            mediaTiles = media.Select(r => new TileVm(r, Thumbnails, format)).ToList();
            if (!useSections)
                _sections.Clear();
        }

        var items = new List<IGridItem>(folders.Count + mediaTiles.Count);
        if (folders.Count > 0)
        {
            var folderSection = useSections ? GetFolderSection(folders.Count) : null;
            foreach (var folder in folders.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                folder.Section = folderSection;
                items.Add(folder);
            }
        }
        items.AddRange(mediaTiles);

        _tiles = items;
        _mediaTiles = mediaTiles;
        _view.Source = items;
        _view.GroupDescriptions.Clear();
        if (useSections)
            _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(IGridItem.Section)));

        BuildBreadcrumb();
        OnPropertyChanged(nameof(ItemsView));
        UpdateStatus();
    }

    private SectionVm GetFolderSection(int count)
    {
        _folderSection ??= new SectionVm { Key = long.MaxValue, Label = "Folders", IsExpanded = true };
        _folderSection.Count = count;
        return _folderSection;
    }

    // --- Folder view derivation --------------------------------------------

    /// <summary>Resolves single-root "home" to that root's top level.</summary>
    private (long? RootId, string RelDir) EffectiveLocation()
    {
        if (_location.RootId is null)
        {
            var included = Roots.Where(r => r.IsIncluded).ToList();
            if (included.Count == 1)
                return (included[0].Id, "");
        }
        return _location;
    }

    private (List<FolderTileVm> Folders, List<LibraryRow> Media) ComputeFolderView(List<LibraryRow> rows)
    {
        var loc = EffectiveLocation();

        // Multi-root home: one tile per included root.
        if (loc.RootId is null)
        {
            var counts = rows.GroupBy(r => r.Item.RootId).ToDictionary(g => g.Key, g => g.Count());
            var rootTiles = Roots.Where(r => r.IsIncluded).Select(r => new FolderTileVm
            {
                Name = r.Alias,
                RootId = r.Id,
                RelDir = "",
                FullPath = r.Path,
                Count = counts.GetValueOrDefault(r.Id),
            }).ToList();
            return (rootTiles, []);
        }

        var rootId = loc.RootId.Value;
        var relDir = loc.RelDir;
        var rootPath = Roots.FirstOrDefault(r => r.Id == rootId)?.Path ?? "";

        var media = new List<LibraryRow>();
        var childCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var r in rows)
        {
            if (r.Item.RootId != rootId)
                continue;
            var dir = GetDir(r.Item.RelPath);
            if (!IsUnder(dir, relDir))
                continue;

            if (string.Equals(dir, relDir, StringComparison.OrdinalIgnoreCase))
                media.Add(r); // file directly in this folder
            else if (FirstSegmentUnder(r.Item.RelPath, relDir) is { } child)
                childCounts[child] = childCounts.GetValueOrDefault(child) + 1;
        }

        var folders = childCounts.Select(kv => new FolderTileVm
        {
            Name = kv.Key,
            RootId = rootId,
            RelDir = relDir.Length == 0 ? kv.Key : Path.Combine(relDir, kv.Key),
            FullPath = Path.Combine(rootPath, relDir, kv.Key),
            Count = kv.Value,
        }).ToList();

        return (folders, media);
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

    private List<TileVm> BuildSectioned(List<LibraryRow> rows, string format, IReadOnlyList<Reel.Core.Settings.SortLevel> levels)
    {
        var mode = DateBuckets.ChooseMode(rows.Select(r => r.Item.BestDate).ToList());

        var bucketByItem = new Dictionary<long, DateBucket>(rows.Count);
        foreach (var r in rows)
            bucketByItem[r.Item.Id] = DateBuckets.Bucket(r.Item.BestDate, mode);

        LibrarySorter.SortSectioned(rows, r => bucketByItem[r.Item.Id].Key, levels);

        var tiles = new List<TileVm>(rows.Count);
        var counts = new Dictionary<long, int>();
        var order = new List<SectionVm>();
        var seen = new HashSet<long>();
        var newlyCreated = new HashSet<long>();

        foreach (var r in rows)
        {
            var bucket = bucketByItem[r.Item.Id];
            counts[bucket.Key] = counts.GetValueOrDefault(bucket.Key) + 1;

            if (!_sections.TryGetValue(bucket.Key, out var section))
            {
                section = new SectionVm { Key = bucket.Key, Label = bucket.Label };
                _sections[bucket.Key] = section;
                newlyCreated.Add(bucket.Key);
            }

            if (seen.Add(bucket.Key))
                order.Add(section);

            tiles.Add(new TileVm(r, Thumbnails, format) { Section = section });
        }

        foreach (var section in order)
            section.Count = counts[section.Key];

        // Only newly-created sections get a default expansion; user toggles on
        // existing sections survive a rebuild.
        long cumulative = 0;
        foreach (var section in order)
        {
            if (newlyCreated.Contains(section.Key))
                section.IsExpanded = cumulative < ExpandTargetItems;
            cumulative += section.Count;
        }

        // Drop sections that no longer have any items.
        foreach (var key in _sections.Keys.Where(k => !seen.Contains(k)).ToList())
            _sections.Remove(key);

        return tiles;
    }

    private static bool Matches(LibraryRow r, string query) =>
        r.Item.FileName.Contains(query, StringComparison.OrdinalIgnoreCase)
        || r.RootAlias.Contains(query, StringComparison.OrdinalIgnoreCase)
        || (r.Item.Camera?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);

    private void UpdateStatus()
    {
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
                BuildView();
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
                BuildView();
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
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
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
