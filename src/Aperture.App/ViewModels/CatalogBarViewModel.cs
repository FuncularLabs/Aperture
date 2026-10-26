using System.IO;
using System.Windows.Input;
using Aperture.App.Mvvm;
using Aperture.App.Services;
using Aperture.Core.Library;
using Aperture.Core.Storage;

namespace Aperture.App.ViewModels;

/// <summary>
/// The library switcher in the nav: the open library's name, plus commands to switch to another library,
/// create one (in a folder of its own), open an existing one, rename the open one, and remove or delete
/// libraries you aren't in. Deleting removes only Aperture's own files for that library.
///
/// One instance lives for the whole session: App injects it into each <see cref="MainViewModel"/> and calls
/// <see cref="Refresh"/> after a switch. It is <c>null</c> on the view model when <c>APERTURE_DATA_DIR</c> pins a
/// single library, and the switcher is hidden. Commands are disabled while one of them is running.
/// </summary>
public sealed class CatalogBarViewModel : ObservableObject
{
    private readonly CatalogService _catalog;
    private readonly ICatalogDialogs _dialogs;
    private readonly string _librariesDir; // where New library suggests creating the folder
    private bool _isBusy;

    public CatalogBarViewModel(CatalogService catalog, ICatalogDialogs dialogs, string librariesDir)
    {
        _catalog = catalog;
        _dialogs = dialogs;
        _librariesDir = librariesDir;

        SwitchCommand = new RelayCommand<string>(id => Start(() => _catalog.SwitchToAsync(id)), _ => !IsBusy);
        NewLibraryCommand = new RelayCommand(() => Start(NewLibraryAsync), () => !IsBusy);
        OpenLibraryCommand = new RelayCommand(() => Start(OpenLibraryAsync), () => !IsBusy);
        RenameCommand = new RelayCommand(() => Start(RenameAsync), () => !IsBusy);
        RemoveCommand = new RelayCommand<string>(id => Start(() => _catalog.RemoveAsync(id)), _ => !IsBusy);
        DeleteFilesCommand = new RelayCommand<string>(id => Start(() => DeleteFilesAsync(id)), _ => !IsBusy);

        Refresh();
    }

    /// <summary>The open library's name, shown on the switcher.</summary>
    public string ActiveName => _catalog.ActiveCatalog?.Name ?? "Library";

    /// <summary>Every library except the open one, most recently opened first: switch, remove and delete targets.</summary>
    public IReadOnlyList<CatalogEntry> OtherCatalogs { get; private set; } = [];

    /// <summary>True when there is at least one library besides the open one.</summary>
    public bool HasOthers => OtherCatalogs.Count > 0;

    /// <summary>True while a switcher command runs; every command is disabled meanwhile.</summary>
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
    public ICommand RenameCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand DeleteFilesCommand { get; }

    /// <summary>The command that is running (or last ran). Tests await it; the app doesn't need to.</summary>
    internal Task? PendingOperation { get; private set; }

    /// <summary>Re-reads the library list so the switcher shows the open library and the others.</summary>
    public void Refresh()
    {
        var activeId = _catalog.ActiveCatalog?.Id;
        OtherCatalogs = _catalog.Registry.List()
            .Where(c => c.Id != activeId)
            .OrderByDescending(c => c.LastOpenedTicks)
            .ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        OnPropertyChanged(nameof(ActiveName));
        OnPropertyChanged(nameof(OtherCatalogs));
        OnPropertyChanged(nameof(HasOthers));
    }

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

    private async Task NewLibraryAsync()
    {
        var name = _dialogs.PromptText("New library", "Name for the new library:", "");
        if (name is null)
            return;
        name = _catalog.CheckNewLibraryName(name); // say what's wrong with the name before asking where

        Directory.CreateDirectory(_librariesDir); // so the folder picker can start there
        var parent = _dialogs.PickFolder($"Choose where to create the \"{name}\" library folder", _librariesDir);
        if (parent is null)
            return;
        await _catalog.CreateLibraryAsync(name, parent);
    }

    private async Task OpenLibraryAsync()
    {
        var dir = _dialogs.PickFolder("Open an existing Aperture library folder", _librariesDir);
        if (dir is null)
            return;
        await _catalog.OpenLibraryAsync(dir);
    }

    private Task RenameAsync()
    {
        var active = _catalog.ActiveCatalog;
        if (active is null)
            return Task.CompletedTask;
        var name = _dialogs.PromptText("Rename library", "Library name:", active.Name);
        if (name is not null && name.Trim() != active.Name)
            _catalog.Rename(active.Id, name);
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
                $"\"{entry.Name}\" was removed from the list, but some of its files couldn't be deleted " +
                $"(another program may be using them):\n\n{string.Join("\n", result.FilesLeft)}",
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
