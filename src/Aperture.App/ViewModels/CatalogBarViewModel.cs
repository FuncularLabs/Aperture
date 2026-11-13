using System.Windows.Input;
using Aperture.App.Mvvm;
using Aperture.App.Services;
using Aperture.Core.Formatting;
using Aperture.Core.Library;
using Aperture.Core.Storage;

namespace Aperture.App.ViewModels;

/// <summary>
/// A library as the nav lists it. <see cref="ItemCount"/> is how many items it shows when open: the count recorded for
/// it, until its own files have been read (the open library shows its live count instead); <c>null</c> if unknown.
/// </summary>
public sealed class LibraryListItem(string id, string name, string dir, bool isActive, int? itemCount) : ObservableObject
{
    private int? _itemCount = itemCount;

    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Dir { get; } = dir;
    public bool IsActive { get; } = isActive;

    public int? ItemCount
    {
        get => _itemCount;
        internal set => SetProperty(ref _itemCount, value);
    }
}

/// <summary>The folder New library was asked to put in the new library, and whether with its subfolders.</summary>
public sealed record NewLibraryFolder(string Path, bool IncludeSubfolders);

/// <summary>
/// The libraries in the nav: every library, the open one marked, with commands to switch, create one (with a
/// folder of photos to put in it), open an existing one, rename one, and remove or delete libraries you aren't in.
/// Deleting removes only Aperture's own files for that library.
///
/// One instance lives for the whole session: App injects it into each <see cref="MainViewModel"/> and calls
/// <see cref="Refresh"/> after a switch. It is <c>null</c> on the view model when <c>APERTURE_DATA_DIR</c> pins a
/// single library, and the list isn't shown. Commands are disabled while one of them is running.
/// </summary>
public sealed class CatalogBarViewModel : ObservableObject
{
    private readonly CatalogService _catalog;
    private readonly ICatalogDialogs _dialogs;
    private readonly string _librariesDir; // where a new library's own files go
    private readonly string _picturesDir;  // where New library starts looking for photos
    private bool _isBusy;
    private bool _isExpanded = true;

    // Counting the libraries that aren't open (0.9.1 batch, item 6): a read of one library at a time, under _countGate,
    // which deleting a library's files waits for (a read holds its database open); a refresh starts a new generation,
    // and a read from an older one lands nowhere.
    private readonly Func<string, int?> _countLibrary;
    private readonly SemaphoreSlim _countGate = new(1, 1);
    private readonly SynchronizationContext? _ui;
    private int _countGeneration;

    /// <param name="countLibrary">
    /// Reads a library's count from its own folder (<see cref="LibraryCount.Read"/> unless a test gives another).
    /// </param>
    public CatalogBarViewModel(
        CatalogService catalog, ICatalogDialogs dialogs, string librariesDir, string? picturesDir = null,
        Func<string, int?>? countLibrary = null)
    {
        _catalog = catalog;
        _dialogs = dialogs;
        _librariesDir = librariesDir;
        _picturesDir = picturesDir ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        _countLibrary = countLibrary ?? LibraryCount.Read;
        _ui = SynchronizationContext.Current;

        SwitchCommand = new RelayCommand<string>(id => Start(() => _catalog.SwitchToAsync(id)), _ => !IsBusy);
        NewLibraryCommand = new RelayCommand(() => Start(NewLibraryAsync), () => !IsBusy);
        OpenLibraryCommand = new RelayCommand(() => Start(OpenLibraryAsync), () => !IsBusy);
        RenameCommand = new RelayCommand<string>(id => Start(() => RenameAsync(id)), _ => !IsBusy);
        RemoveCommand = new RelayCommand<string>(id => Start(() => _catalog.RemoveAsync(id)), id => !IsBusy && !IsOpen(id));
        DeleteFilesCommand = new RelayCommand<string>(id => Start(() => DeleteFilesAsync(id)), id => !IsBusy && !IsOpen(id));
        ToggleExpandedCommand = new RelayCommand(() => IsExpanded = !IsExpanded);

        Refresh();
    }

    /// <summary>The open library's name (the title bar shows it).</summary>
    public string ActiveName => _catalog.ActiveCatalog?.Name ?? "Library";

    /// <summary>
    /// Every library, in the order the user dragged them into (alphabetical until then, and for libraries added since);
    /// the open one has <see cref="LibraryListItem.IsActive"/>.
    /// </summary>
    public IReadOnlyList<LibraryListItem> Libraries { get; private set; } = [];

    /// <summary>Whether the nav shows the list (the LIBRARIES header collapses it). For this session.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        private set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>True while a library command runs; every command is disabled meanwhile.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public ICommand SwitchCommand { get; }
    public ICommand NewLibraryCommand { get; }
    public ICommand OpenLibraryCommand { get; }

    /// <summary>Renames the library whose id is the parameter.</summary>
    public ICommand RenameCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand DeleteFilesCommand { get; }
    public ICommand ToggleExpandedCommand { get; }

    /// <summary>
    /// Raised once New library has created the library and switched to it, with the folder the user chose to put in
    /// it: App hands it to the new library's view model, which adds and indexes it.
    /// </summary>
    public event EventHandler<NewLibraryFolder>? NewLibraryFolderChosen;

    /// <summary>The command that is running (or last ran). Tests await it; the app doesn't need to.</summary>
    internal Task? PendingOperation { get; private set; }

    /// <summary>The latest refresh's reads of the other libraries' counts. Tests await it; the app doesn't need to.</summary>
    internal Task PendingCounts { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Re-reads the library list, so the nav shows every library and which one is open, then reads each other
    /// library's count from its own files in the background.
    /// </summary>
    public void Refresh()
    {
        var activeId = _catalog.ActiveCatalog?.Id;
        var registry = _catalog.Registry;
        Libraries = ListOrder.Apply(registry.List(), registry.Order, c => c.Id, c => c.Name)
            .Select(c => new LibraryListItem(c.Id, c.Name, c.Dir, c.Id == activeId, c.ItemCount))
            .ToList();
        OnPropertyChanged(nameof(ActiveName));
        OnPropertyChanged(nameof(Libraries));
        CommandManager.InvalidateRequerySuggested(); // Remove/Delete follow which library is open
        PendingCounts = ReadCountsAsync(++_countGeneration, [.. Libraries.Where(l => !l.IsActive)]);
    }

    /// <summary>
    /// Moves a library to <paramref name="toIndex"/> in the list (an index into the list without it) and keeps that
    /// order, as dropping it there after a drag does. If the list can't be saved, says so and the order stays as it was.
    /// </summary>
    public void MoveLibrary(string id, int toIndex)
    {
        var ids = Libraries.Select(l => l.Id).ToList();
        var moved = ListOrder.Move(ids, id, toIndex);
        if (moved.SequenceEqual(ids))
            return;
        if (!_catalog.Registry.SetOrder(moved))
            _dialogs.ShowMessage("Aperture",
                "Aperture couldn't save its list of libraries, so the new order wasn't kept.", isError: true);
        Refresh();
    }

    // Each library's count from its own files, one at a time; a changed count is remembered for the next launch, and
    // shown on the item (on the UI thread when there is one). A refresh since this one started makes it stop.
    private Task ReadCountsAsync(int generation, List<LibraryListItem> others)
    {
        if (others.Count == 0)
            return Task.CompletedTask;
        return Task.Run(async () =>
        {
            foreach (var item in others)
            {
                int? count;
                await _countGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (generation != Volatile.Read(ref _countGeneration))
                        return;
                    count = _countLibrary(item.Dir);
                }
                catch (Exception)
                {
                    count = null; // a label, best effort: the item keeps the count it had
                }
                finally
                {
                    _countGate.Release();
                }
                if (count is not { } known || generation != Volatile.Read(ref _countGeneration))
                    continue;
                if (known != item.ItemCount)
                    _catalog.Registry.SetItemCount(item.Id, known); // off the UI thread: it can wait on another window
                if (_ui is null)
                    item.ItemCount = known;
                else
                    await OnUi(() => item.ItemCount = known).ConfigureAwait(false); // so PendingCounts covers it
            }
        });
    }

    // Runs an action on the UI thread, done when it has run there.
    private Task OnUi(Action action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui!.Post(_ =>
        {
            try
            {
                action();
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        }, null);
        return done.Task;
    }

    private bool IsOpen(string? id) => id is not null && id == _catalog.ActiveCatalog?.Id;

    private void Start(Func<Task> operation)
    {
        if (IsBusy)
            return; // one at a time: a double-click must not start a second switch
        PendingOperation = RunAsync(operation);
    }

    private async Task RunAsync(Func<Task> operation)
    {
        IsBusy = true;
        try
        {
            await operation();
        }
        catch (CatalogException ex)
        {
            _dialogs.ShowMessage("Aperture", ex.Message, isError: true);
        }
        catch (Exception ex)
        {
            _dialogs.ShowMessage("Aperture", $"The library operation failed: {ex.Message}", isError: true);
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    // Asks what goes in the library — never where Aperture keeps its own files, which is the libraries folder.
    private async Task NewLibraryAsync()
    {
        var name = _dialogs.PromptText("New library", "Name for the new library:", "");
        if (name is null)
            return;
        name = _catalog.CheckNewLibraryName(name); // say what's wrong with the name before asking anything else

        var folder = _dialogs.PickFolder($"Choose a folder of photos and videos for \"{name}\"", _picturesDir);
        if (folder is null)
            return;
        if (_dialogs.AskIncludeSubfolders(folder) is not { } includeSubfolders)
            return;

        await _catalog.CreateLibraryAsync(name, _librariesDir);
        NewLibraryFolderChosen?.Invoke(this, new NewLibraryFolder(folder, includeSubfolders));
    }

    private async Task OpenLibraryAsync()
    {
        var dir = _dialogs.PickFolder("Open an existing Aperture library folder", _librariesDir);
        if (dir is null)
            return;
        await _catalog.OpenLibraryAsync(dir);
    }

    private Task RenameAsync(string id)
    {
        var entry = _catalog.Registry.List().FirstOrDefault(c => c.Id == id);
        if (entry is null)
            return Task.CompletedTask;
        var name = _dialogs.PromptText("Rename library", "Library name:", entry.Name);
        if (name is not null && name.Trim() != entry.Name)
            _catalog.Rename(entry.Id, name);
        return Task.CompletedTask;
    }

    private async Task DeleteFilesAsync(string id)
    {
        var entry = _catalog.Registry.List().FirstOrDefault(c => c.Id == id);
        if (entry is null)
            return;
        if (!_dialogs.Confirm("Delete library files", DeleteWarning(entry)))
            return;

        CatalogDeletion result;
        await _countGate.WaitAsync(); // a count read holds the library's database open: let it finish first
        try
        {
            result = await _catalog.DeleteFilesAsync(id);
        }
        finally
        {
            _countGate.Release();
        }
        if (result.Outcome == CatalogDeletionOutcome.PartiallyDeleted)
            _dialogs.ShowMessage("Aperture",
                $"\"{entry.Name}\" was removed from the list, but the files below couldn't be deleted " +
                $"(another program may be using them, or Windows didn't allow it):\n\n{string.Join("\n", result.FilesLeft)}",
                isError: true);
        else if (result.Outcome == CatalogDeletionOutcome.FolderLostDuringDelete)
            _dialogs.ShowMessage("Aperture",
                $"\"{entry.Name}\" was removed from the list, but Aperture lost access to {entry.Dir} during the delete, " +
                "so not all of its files could be deleted. To delete them, reconnect the drive, add the library again with " +
                "Open library…, then switch to another library and delete it.",
                isError: true);
    }

    private static string DeleteWarning(CatalogEntry entry) =>
        $"Delete the library \"{entry.Name}\"?\n\n" +
        $"This permanently deletes Aperture's index, thumbnail cache and settings for it from:\n\n{entry.Dir}\n\n" +
        "The tags and notes you added in this library are deleted with it. Your photos and videos, and any " +
        "other files in that folder, are not touched. This can't be undone.";
}
