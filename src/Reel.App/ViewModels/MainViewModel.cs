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

    private List<TileVm> _tiles = [];
    private TileVm? _selectedTile;
    private int _zoom;
    private string _searchText = "";
    private string _statusText = "No folders yet — add one to begin.";
    private string _indexingText = "";

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
        OpenSelectedCommand = new RelayCommand(OpenSelected, () => _selectedTile is not null);
        ClearSearchCommand = new RelayCommand(() => SearchText = "");
        SetSortCommand = new RelayCommand<string>(ApplySortPreset);
        HideFolderCommand = new RelayCommand<TileVm>(HideFolder);
        ClearExclusionsCommand = new RelayCommand(ClearExclusions);
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

        _library.RootChanged += OnRootChanged;

        LoadRoots();
        BuildView();
        MaybeStartFirstRun();
    }

    public ThumbnailService Thumbnails { get; }

    public ObservableCollection<RootVm> Roots { get; } = [];

    /// <summary>The grouped, sorted, filtered view the grid binds to.</summary>
    public ICollectionView? ItemsView => _view.View;

    public TileVm? SelectedTile
    {
        get => _selectedTile;
        set => SetProperty(ref _selectedTile, value);
    }

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

    /// <summary>Group the grid into collapsible date sections.</summary>
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
    public ICommand SetSortCommand { get; }
    public ICommand HideFolderCommand { get; }
    public ICommand ClearExclusionsCommand { get; }
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

    /// <summary>Moves the selection by <paramref name="delta"/> in view order, expanding the landing section.</summary>
    public TileVm? MoveSelection(int delta)
    {
        if (_tiles.Count == 0)
            return null;

        var index = _selectedTile is null ? -1 : _tiles.IndexOf(_selectedTile);
        var next = Math.Clamp(index < 0 ? 0 : index + delta, 0, _tiles.Count - 1);
        var tile = _tiles[next];
        if (tile.Section is { IsExpanded: false } section)
            section.IsExpanded = true;
        SelectedTile = tile;
        return tile;
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
        if (_tiles.Count == 0)
            return;
        var start = _selectedTile is not null ? _tiles.IndexOf(_selectedTile) : 0;
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
        if (!_quickLookOpen)
            return;
        ShowQuickLookAt(Math.Clamp(_quickLookIndex + delta, 0, _tiles.Count - 1));
    }

    private void ShowQuickLookAt(int index)
    {
        if (index < 0 || index >= _tiles.Count)
            return;
        _quickLookIndex = index;
        var tile = _tiles[index];
        SelectedTile = tile;
        QuickLookCaption = $"{tile.FileName}   ·   {index + 1} / {_tiles.Count:n0}";
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

    public string ExclusionsSummary
    {
        get
        {
            var n = _library.Settings.Current.ExcludedFolders.Count;
            return n == 0 ? "" : $"{n} folder{(n == 1 ? "" : "s")} hidden";
        }
    }

    public bool HasExclusions => _library.Settings.Current.ExcludedFolders.Count > 0;

    private void HideFolder(TileVm? tile)
    {
        var dir = tile is null ? null : Path.GetDirectoryName(tile.FullPath);
        if (string.IsNullOrEmpty(dir))
            return;

        _library.Settings.Update(s =>
        {
            if (!s.ExcludedFolders.Contains(dir, StringComparer.OrdinalIgnoreCase))
                s.ExcludedFolders.Add(dir);
        });
        OnPropertyChanged(nameof(ExclusionsSummary));
        OnPropertyChanged(nameof(HasExclusions));
        BuildView();
    }

    private void ClearExclusions()
    {
        _library.Settings.Update(s => s.ExcludedFolders.Clear());
        OnPropertyChanged(nameof(ExclusionsSummary));
        OnPropertyChanged(nameof(HasExclusions));
        BuildView();
    }

    private static bool IsExcluded(string fullPath, List<string> excluded)
    {
        var dir = Path.GetDirectoryName(fullPath) ?? "";
        foreach (var e in excluded)
        {
            if (dir.Equals(e, StringComparison.OrdinalIgnoreCase)
                || dir.StartsWith(e + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>Human-readable summary of the active sort, for the settings UI.</summary>
    public string SortSummary
    {
        get
        {
            var levels = _library.Settings.Current.Sort;
            return levels.Count == 0
                ? "unsorted"
                : string.Join(", ", levels.Select(l => $"{l.Token} {(l.Descending ? "↓" : "↑")}"));
        }
    }

    private void ApplySortPreset(string? preset)
    {
        List<Reel.Core.Settings.SortLevel> spec = preset switch
        {
            "newest" => [new("date", true)],
            "oldest" => [new("date", false)],
            "name" => [new("name", false)],
            "largest" => [new("size", true)],
            "camera" => [new("camera", false), new("date", true)],
            _ => _library.Settings.Current.Sort,
        };
        _library.Settings.Update(s => s.Sort = spec);
        OnPropertyChanged(nameof(SortSummary));
        BuildView();
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
        var rows = _library.GetUnion();

        var excluded = _library.Settings.Current.ExcludedFolders;
        if (excluded.Count > 0)
            rows = rows.Where(r => !IsExcluded(r.FullPath, excluded)).ToList();

        var query = _searchText.Trim();
        if (query.Length > 0)
            rows = rows.Where(r => Matches(r, query)).ToList();

        var format = _library.Settings.Current.CaptionFormat;
        var levels = _library.Settings.Current.Sort;

        List<TileVm> tiles;
        if (GroupByDate && rows.Count > 0)
            tiles = BuildSectioned(rows, format, levels);
        else
        {
            LibrarySorter.Sort(rows, levels);
            tiles = rows.Select(r => new TileVm(r, Thumbnails, format)).ToList();
            _sections.Clear();
        }

        _tiles = tiles;
        _view.Source = tiles;
        _view.GroupDescriptions.Clear();
        if (GroupByDate && rows.Count > 0)
            _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TileVm.Section)));

        OnPropertyChanged(nameof(ItemsView));
        UpdateStatus();
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
        var visible = _tiles.Count;
        if (visible == 0)
        {
            StatusText = Roots.Count == 0
                ? "No folders yet — add one to begin."
                : _searchText.Length > 0 ? "No matches." : "No items in the included folders.";
            return;
        }
        StatusText = _searchText.Length > 0 ? $"{visible:n0} matches" : $"{visible:n0} items";
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
        var path = _selectedTile?.FullPath;
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
