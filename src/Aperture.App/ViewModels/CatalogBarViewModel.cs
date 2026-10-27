using System.Windows.Input;
using Aperture.App.Mvvm;
using Aperture.App.Services;
using Aperture.Core.Library;
using Aperture.Core.Storage;

namespace Aperture.App.ViewModels;

/// <summary>
/// A library as the nav lists it. <see cref="ItemCount"/> is how many items it showed when last open (the open library
/// shows its live count instead); <c>null</c> if not recorded yet.
/// </summary>
public sealed record LibraryListItem(string Id, string Name, string Dir, bool IsActive, int? ItemCount);

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

    public CatalogBarViewModel(CatalogService catalog, ICatalogDialogs dialogs, string librariesDir, string? picturesDir = null)
    {
        _catalog = catalog;
        _dialogs = dialogs;
        _librariesDir = librariesDir;
        _picturesDir = picturesDir ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

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

    /// <summary>Every library, by name; the open one has <see cref="LibraryListItem.IsActive"/>.</summary>
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

    /// <summary>Re-reads the library list, so the nav shows every library and which one is open.</summary>
    public void Refresh()
    {
        var activeId = _catalog.ActiveCatalog?.Id;
        Libraries = _catalog.Registry.List()
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => new LibraryListItem(c.Id, c.Name, c.Dir, c.Id == activeId, c.ItemCount))
            .ToList();
        OnPropertyChanged(nameof(ActiveName));
        OnPropertyChanged(nameof(Libraries));
        CommandManager.InvalidateRequerySuggested(); // Remove/Delete follow which library is open
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

        var result = await _catalog.DeleteFilesAsync(id);
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
