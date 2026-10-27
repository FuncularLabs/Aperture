using System.IO;
using System.Windows;
using System.Windows.Threading;
using Aperture.App.Services;
using Aperture.App.Updates;
using Aperture.App.ViewModels;
using Aperture.Core.Library;
using Aperture.Core.Startup;
using Aperture.Core.Storage;

namespace Aperture.App;

public partial class App : Application
{
    // In the normal (multi-catalog) path, _catalog owns the live library and swaps it on a switch.
    // When APERTURE_DATA_DIR pins a single data dir, _catalog is null and _pinnedLibrary is owned here.
    private CatalogService? _catalog;
    private CatalogBarViewModel? _catalogBar;
    private LibraryService? _pinnedLibrary;
    private MainViewModel? _viewModel;
    private MainWindow? _window;
    private SingleInstance? _instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogCrash(args.ExceptionObject as Exception);

        // Anything that throws before the window is up (a missing native DLL surfaces
        // here as a TypeInitializationException from the first SqliteConnection) used
        // to end the process with only a crash.log entry. Say so on screen instead.
        try
        {
            StartUp(e);
        }
        catch (Exception ex)
        {
            LogCrash(ex);
            ReleaseWindow(); // a launch while this box is up should open, not be swallowed by a copy that's failing
            try
            {
                MessageBox.Show(StartupGuard.Describe(ex, CrashLogPath), StartupGuard.Title,
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { /* the report must not become a second crash */ }
            Shutdown(1);
        }
    }

    private void StartUp(StartupEventArgs e)
    {
        // APERTURE_DATA_DIR relocates the index/thumbnail store (handy for testing or
        // pointing at a different drive); otherwise the default under LOCALAPPDATA.
        var overrideDir = Environment.GetEnvironmentVariable("APERTURE_DATA_DIR");
        var dataDir = string.IsNullOrWhiteSpace(overrideDir) ? AperturePaths.DefaultDataDir : overrideDir;

        // One window. A relaunch (an update, the installer's move) first waits for the copy it replaces; then a launch
        // while Aperture is already running brings that window to the front instead of opening a second one. A pinned
        // data folder has a window of its own.
        var place = string.IsNullOrWhiteSpace(overrideDir) ? CatalogRegistry.DefaultPath : overrideDir;
        switch (SingleInstance.ClaimAtLaunch(e.Args, SingleInstance.NameFor(place), BringWindowForward, out _instance))
        {
            case SingleInstanceClaim.AlreadyRunning:
                Shutdown(0);
                return;
            case SingleInstanceClaim.RunningButUnreachable:
                MessageBox.Show(
                    "Aperture is already running with different permissions (for example, as administrator), so this " +
                    "copy can't bring it to the front. Switch to it, or close it and start Aperture again.", "Aperture",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown(0);
                return;
        }

        // One-time move of a pre-rename %LOCALAPPDATA%\Reel store into the Aperture location.
        // Only for the default location — a custom APERTURE_DATA_DIR is the user's to manage.
        if (string.IsNullOrWhiteSpace(overrideDir))
            LegacyDataMigration.Run(AperturePaths.DefaultDataDir, AperturePaths.LegacyReelDataDir);

        // A "move" install launches the AppData copy with --finish-move <download>
        // so it can delete the original once that process has exited.
        var args = e.Args;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--finish-move" && i + 1 < args.Length)
                RegistrationService.FinishMove(args[++i]);
        }

        UpdateService.CleanupOldBinaries();
        RegistrationService.RefreshAutostartToInstalledCopy();

        LibraryService library;
        if (string.IsNullOrWhiteSpace(overrideDir))
        {
            // Multi-catalog: the existing default dir auto-registers as the "Default" catalog, and the
            // user can switch between catalogs at runtime. The registry lives outside any catalog.
            _catalog = CatalogService.Open(CatalogRegistry.DefaultPath, AperturePaths.DefaultDataDir);
            _catalog.PreparingToSwitch += OnPreparingToSwitch;
            _catalog.ActiveLibraryChanged += OnActiveLibraryChanged;
            _catalogBar = new CatalogBarViewModel(_catalog, new WpfCatalogDialogs(), AperturePaths.LibrariesDir);
            // New library has switched to the new library (so _viewModel is its view model): add and index the folder.
            _catalogBar.NewLibraryFolderChosen += (_, folder) => _viewModel?.AddFolder(folder.Path, folder.IncludeSubfolders);
            library = _catalog.Current;
        }
        else
        {
            // Pinned single catalog (test/CI or a user-chosen drive): no switching, no registry.
            _pinnedLibrary = new LibraryService(dataDir);
            library = _pinnedLibrary;
        }

        _viewModel = BuildViewModel(library);
        _window = new MainWindow { DataContext = _viewModel };
        _window.ClosingStarted += (_, _) => ReleaseWindow(); // nothing in Aperture cancels a close
        _window.Show();

        // The last-used library couldn't be opened (an unplugged drive, a deleted folder), so another was
        // opened instead. Say which and why, once the window is up.
        if (_catalog?.OpenWarning is { } warning)
            _window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
                MessageBox.Show(_window, warning, "Aperture", MessageBoxButton.OK, MessageBoxImage.Warning));

        // Cached rows are already visible; reconcile with disk in the background.
        _viewModel.StartBackgroundRefresh();
    }

    private MainViewModel BuildViewModel(LibraryService library) =>
        new(library, new ThumbnailService(library), _catalogBar);

    // Gives up the window as soon as this copy starts closing (or has failed to start), before the settings save and
    // the library's teardown: a launch in the meantime then opens, instead of asking a window that is going away.
    // Safe to call more than once.
    private void ReleaseWindow() => _instance?.Dispose();

    // A later launch asked for this window. It runs on the single-window listener's thread, so it only queues the work
    // on the UI thread — never blocking, since Dispose waits for the listener.
    private void BringWindowForward() => Dispatcher.InvokeAsync(() =>
    {
        if (_window is null)
            return; // still starting: the window comes up in front anyway
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
        _window.Topmost = true; // in front of the window the later launch was started from
        _window.Topmost = false;
    });

    /// <summary>
    /// The target library has opened and the current one is about to close: stop the view model from
    /// starting work against it, and disable the window so nothing can be clicked against a closing
    /// library until <see cref="OnActiveLibraryChanged"/> rebinds it.
    /// </summary>
    private void OnPreparingToSwitch(object? sender, EventArgs e) => OnUiThread(() =>
    {
        _viewModel?.PrepareForClose();
        if (_window is not null)
            _window.IsEnabled = false;
    });

    /// <summary>
    /// After <see cref="CatalogService"/> has swapped in the new catalog's library, rebuild the view model
    /// over it and re-point the window. The old view model was already quiesced via
    /// <see cref="MainViewModel.PrepareForClose"/> (raised as PreparingToSwitch) and is discarded.
    /// </summary>
    private void OnActiveLibraryChanged(object? sender, EventArgs e) => OnUiThread(() =>
    {
        try
        {
            _catalogBar?.Refresh(); // reflect the new active catalog before the new view model binds to it
            _viewModel = BuildViewModel(_catalog!.Current);
            if (_window is not null)
                _window.DataContext = _viewModel;
            _viewModel.StartBackgroundRefresh();
        }
        finally
        {
            if (_window is not null)
                _window.IsEnabled = true;
        }
    });

    // CatalogService raises its events on the caller's context, and the libraries in the nav call it from the UI
    // thread — so this normally runs inline. The marshal is a guard: a window can only be touched on its own thread.
    private void OnUiThread(Action action)
    {
        if (Dispatcher.CheckAccess())
            action();
        else
            Dispatcher.Invoke(action);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ReleaseWindow(); // a backstop: closing the window (Shutdown closes it too) has already given it up
        _viewModel?.PrepareForClose();

        // Release the live library's DB handles. The catalog service owns it in the normal path; a pinned
        // library is owned here. A bounded wait keeps exit from hanging on a stuck indexer.
        if (_catalog is not null)
            _catalog.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        else
            _pinnedLibrary?.Dispose();

        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogCrash(e.Exception);
        // Let it terminate — an unhandled UI exception means state is suspect.
    }

    private static string CrashLogPath => Path.Combine(AperturePaths.DefaultDataDir, "crash.log");

    private static void LogCrash(Exception? ex)
    {
        if (ex is null)
            return;
        try
        {
            Directory.CreateDirectory(AperturePaths.DefaultDataDir);
            File.AppendAllText(CrashLogPath, $"[{DateTime.Now:O}] {ex}\n\n");
        }
        catch { /* logging must never throw */ }
    }
}
