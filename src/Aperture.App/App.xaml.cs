using System.IO;
using System.Windows;
using System.Windows.Threading;
using Aperture.App.Services;
using Aperture.App.Updates;
using Aperture.App.ViewModels;
using Aperture.Core.Library;
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

    /// <summary>True when the user may switch catalogs (not pinned by APERTURE_DATA_DIR). Drives the switcher UI.</summary>
    public bool CatalogSwitchingEnabled => _catalog is not null;

    /// <summary>The catalog registry + active catalog, when switching is enabled; otherwise null.</summary>
    public CatalogService? Catalogs => _catalog;

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
            _catalog.PreparingToSwitch += (_, _) => _viewModel?.PrepareForClose();
            _catalog.ActiveLibraryChanged += OnActiveLibraryChanged;
            _catalogBar = new CatalogBarViewModel(_catalog, SwitchCatalogAsync, CreateCatalogAsync);
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
        _window.Show();

        // Cached rows are already visible; reconcile with disk in the background.
        _viewModel.StartBackgroundRefresh();
    }

    private MainViewModel BuildViewModel(LibraryService library) =>
        new(library, new ThumbnailService(library), _catalogBar);

    /// <summary>
    /// After <see cref="CatalogService"/> has swapped in the new catalog's library, rebuild the view model
    /// over it and re-point the window. The old view model was already quiesced via
    /// <see cref="MainViewModel.PrepareForClose"/> (raised as PreparingToSwitch) and is discarded.
    /// </summary>
    private void OnActiveLibraryChanged(object? sender, EventArgs e)
    {
        _catalogBar?.Refresh(); // reflect the new active catalog before the new view model binds to it
        _viewModel = BuildViewModel(_catalog!.Current);
        if (_window is not null)
            _window.DataContext = _viewModel;
        _viewModel.StartBackgroundRefresh();
    }

    /// <summary>Switches to a registered catalog. Entry point for the switcher UI (increment 4).</summary>
    public Task SwitchCatalogAsync(string catalogId) =>
        _catalog?.SwitchToAsync(catalogId) ?? Task.CompletedTask;

    /// <summary>Creates a new catalog at <paramref name="dir"/> and switches to it.</summary>
    public Task CreateCatalogAsync(string name, string dir) =>
        _catalog?.CreateAndSwitchAsync(name, dir) ?? Task.CompletedTask;

    protected override void OnExit(ExitEventArgs e)
    {
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
