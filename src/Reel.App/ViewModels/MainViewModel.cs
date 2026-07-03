using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using Microsoft.Win32;
using Reel.App.Mvvm;
using Reel.App.Services;
using Reel.Core.Library;
using Reel.Core.Models;

namespace Reel.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    // Five zoom stops: tile longest-edge in device-independent pixels.
    private static readonly double[] ZoomSizes = [96, 140, 200, 280, 400];
    private const int DefaultZoom = 2;

    private readonly LibraryService _library;
    private readonly SynchronizationContext _ui;
    private readonly Lock _indexLock = new();
    private int _activeIndexers;
    private long _lastStreamRefreshTick;

    private IReadOnlyList<TileVm> _items = [];
    private TileVm? _selectedTile;
    private int _zoom = DefaultZoom;
    private string _statusText = "No folders yet — add one to begin.";
    private string _indexingText = "";

    public MainViewModel(LibraryService library, ThumbnailService thumbnails)
    {
        _library = library;
        Thumbnails = thumbnails;
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();

        AddRootCommand = new RelayCommand(AddRoot);
        RemoveRootCommand = new RelayCommand<RootVm>(RemoveRoot);
        ZoomInCommand = new RelayCommand(ZoomIn, () => _zoom < ZoomSizes.Length - 1);
        ZoomOutCommand = new RelayCommand(ZoomOut, () => _zoom > 0);
        OpenSelectedCommand = new RelayCommand(OpenSelected, () => _selectedTile is not null);

        _library.RootChanged += OnRootChanged;

        LoadRoots();
        RefreshUnion();
    }

    public ThumbnailService Thumbnails { get; }

    public ObservableCollection<RootVm> Roots { get; } = [];

    public IReadOnlyList<TileVm> Items
    {
        get => _items;
        private set
        {
            _items = value;
            OnPropertyChanged();
        }
    }

    public TileVm? SelectedTile
    {
        get => _selectedTile;
        set => SetProperty(ref _selectedTile, value);
    }

    public double TileSize => ZoomSizes[_zoom];

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
        }
    }

    public void ZoomOut()
    {
        if (_zoom > 0)
        {
            _zoom--;
            OnPropertyChanged(nameof(TileSize));
        }
    }

    // --- Roots --------------------------------------------------------------

    private void LoadRoots()
    {
        Roots.Clear();
        foreach (var root in _library.GetRoots())
        {
            var vm = new RootVm(root, OnRootIncludedChanged) { Count = _library.CountForRoot(root.Id) };
            Roots.Add(vm);
        }
    }

    private void AddRoot()
    {
        var dialog = new OpenFolderDialog { Title = "Add a folder to Reel" };
        if (dialog.ShowDialog() != true)
            return;

        var path = dialog.FolderName;
        if (Roots.Any(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase)))
            return; // already added

        var root = _library.AddRoot(path, DeriveAlias(path));
        var vm = new RootVm(root, OnRootIncludedChanged);
        Roots.Add(vm);
        _ = IndexRootAsync(root, watchAfter: true);
    }

    private void RemoveRoot(RootVm? rootVm)
    {
        if (rootVm is null)
            return;
        _library.RemoveRoot(rootVm.Id);
        Roots.Remove(rootVm);
        RefreshUnion();
    }

    private void OnRootIncludedChanged(RootVm rootVm, bool included)
    {
        _library.SetIncluded(rootVm.Id, included);
        RefreshUnion();
    }

    private static string DeriveAlias(string path)
    {
        var name = new DirectoryInfo(path).Name;
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    // --- Union / status -----------------------------------------------------

    private void RefreshUnion()
    {
        var rows = _library.GetUnion();
        Items = rows.Select(r => new TileVm(r, Thumbnails)).ToList();
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        StatusText = Items.Count == 0
            ? (Roots.Count == 0 ? "No folders yet — add one to begin." : "No items in the included folders.")
            : $"{Items.Count:n0} items";
    }

    // --- Indexing -----------------------------------------------------------

    private async Task IndexRootAsync(Root root, bool watchAfter)
    {
        // Stream partial results into the grid only on the initial population, so a
        // watcher-triggered re-index never yanks the scroll position out from under
        // someone who is browsing.
        var streaming = Items.Count == 0;

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
                RefreshUnion();
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
                RefreshUnion();
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
