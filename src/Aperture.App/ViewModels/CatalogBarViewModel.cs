using System.IO;
using System.Windows;
using System.Windows.Input;
using Aperture.App.Mvvm;
using Aperture.Core.Library;
using Aperture.Core.Storage;
using Microsoft.Win32;

namespace Aperture.App.ViewModels;

/// <summary>
/// The catalog switcher shown in the nav (Option B increment 4): the active catalog's name plus a menu to
/// switch to another library, create or open one, rename, and remove it (optionally deleting its files).
///
/// Persists across view-model rebuilds — App injects the single instance into each <see cref="MainViewModel"/>
/// and calls <see cref="Refresh"/> after a switch so the bar reflects the new active catalog. It is
/// <c>null</c> on the view model when <c>APERTURE_DATA_DIR</c> pins a single catalog, and the switcher hides.
/// </summary>
public sealed class CatalogBarViewModel : ObservableObject
{
    private readonly CatalogService _catalog;
    private readonly Func<string, Task> _switchTo;        // App.SwitchCatalogAsync — runs the full orchestration
    private readonly Func<string, string, Task> _create;  // App.CreateCatalogAsync

    public CatalogBarViewModel(CatalogService catalog, Func<string, Task> switchTo, Func<string, string, Task> create)
    {
        _catalog = catalog;
        _switchTo = switchTo;
        _create = create;

        SwitchCommand = new RelayCommand<string>(id => Run(() => _switchTo(id)));
        NewLibraryCommand = new RelayCommand(() => PickAndOpen("Choose a folder for the new library"));
        OpenLibraryCommand = new RelayCommand(() => PickAndOpen("Open an existing library folder"));
        RenameCommand = new RelayCommand(Rename);
        RemoveCommand = new RelayCommand(() => RemoveActive(deleteFiles: false), () => HasOthers);
        DeleteFilesCommand = new RelayCommand(() => RemoveActive(deleteFiles: true), () => HasOthers);

        Refresh();
    }

    /// <summary>The active catalog's display name, shown on the switcher.</summary>
    public string ActiveName => _catalog.ActiveCatalog?.Name ?? "Library";

    /// <summary>The catalogs other than the active one — the switch targets.</summary>
    public IReadOnlyList<CatalogEntry> OtherCatalogs { get; private set; } = [];

    /// <summary>True when there is at least one other catalog to switch to (and thus one to fall back on).</summary>
    public bool HasOthers => OtherCatalogs.Count > 0;

    public ICommand SwitchCommand { get; }
    public ICommand NewLibraryCommand { get; }
    public ICommand OpenLibraryCommand { get; }
    public ICommand RenameCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand DeleteFilesCommand { get; }

    /// <summary>Re-reads the registry so the bar reflects the current active catalog and the others.</summary>
    public void Refresh()
    {
        var active = _catalog.ActiveCatalog;
        OtherCatalogs = _catalog.Registry.List().Where(c => c.Id != active?.Id).ToList();
        OnPropertyChanged(nameof(ActiveName));
        OnPropertyChanged(nameof(OtherCatalogs));
        OnPropertyChanged(nameof(HasOthers));
        CommandManager.InvalidateRequerySuggested();
    }

    private void PickAndOpen(string title)
    {
        var dialog = new OpenFolderDialog { Title = title };
        if (dialog.ShowDialog() != true)
            return;
        var dir = dialog.FolderName;
        Run(() => _create(DeriveName(dir), dir));
    }

    private void Rename()
    {
        var active = _catalog.ActiveCatalog;
        if (active is null)
            return;

        var name = TextPromptDialog.Prompt("Rename library", "Library name:", active.Name);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == active.Name)
            return;

        _catalog.Registry.Rename(active.Id, name.Trim());
        Refresh();
    }

    // Removing the library you're in means first moving to another one, then forgetting (or deleting) the
    // one you left — you can't tear down the catalog that's currently open. Disabled when it's the only one.
    private async void RemoveActive(bool deleteFiles)
    {
        var active = _catalog.ActiveCatalog;
        var fallback = OtherCatalogs.FirstOrDefault();
        if (active is null || fallback is null)
            return;

        if (deleteFiles && !ConfirmDelete(active))
            return;

        var removedId = active.Id;
        try
        {
            await _switchTo(fallback.Id); // leave the catalog (shuts its library down) before touching its files
            _catalog.Registry.Remove(removedId, deleteFiles);
            Refresh();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not remove the library: {ex.Message}", "Aperture",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static bool ConfirmDelete(CatalogEntry catalog) =>
        MessageBox.Show(
            $"Delete the library \"{catalog.Name}\" and all of its files in:\n\n{catalog.Dir}\n\n" +
            "Your original photos are not touched — only Aperture's index and thumbnail cache for this " +
            "library are deleted. This cannot be undone.",
            "Delete library files", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;

    private static string DeriveName(string dir)
    {
        var name = new DirectoryInfo(dir).Name;
        return string.IsNullOrWhiteSpace(name) ? "Library" : name;
    }

    private static async void Run(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Catalog operation failed: {ex.Message}", "Aperture",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
