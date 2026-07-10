using System.IO;
using System.Windows;
using System.Windows.Threading;
using Aperture.App.Services;
using Aperture.App.ViewModels;
using Aperture.Core.Library;
using Aperture.Core.Storage;

namespace Aperture.App;

public partial class App : Application
{
    private LibraryService? _library;
    private MainViewModel? _viewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogCrash(args.ExceptionObject as Exception);

        // APERTURE_DATA_DIR relocates the index/thumbnail store (handy for testing or
        // pointing at a different drive); otherwise the default under LOCALAPPDATA.
        var overrideDir = Environment.GetEnvironmentVariable("APERTURE_DATA_DIR");
        var dataDir = string.IsNullOrWhiteSpace(overrideDir) ? AperturePaths.DefaultDataDir : overrideDir;

        // One-time move of a pre-rename %LOCALAPPDATA%\Reel store into the Aperture location.
        // Only for the default location — a custom APERTURE_DATA_DIR is the user's to manage.
        if (string.IsNullOrWhiteSpace(overrideDir))
            LegacyDataMigration.Run(AperturePaths.DefaultDataDir, AperturePaths.LegacyReelDataDir);

        _library = new LibraryService(dataDir);
        var thumbnails = new ThumbnailService(_library);
        _viewModel = new MainViewModel(_library, thumbnails);

        var window = new MainWindow { DataContext = _viewModel };
        window.Show();

        // Cached rows are already visible; reconcile with disk in the background.
        _viewModel.StartBackgroundRefresh();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _viewModel?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogCrash(e.Exception);
        // Let it terminate — an unhandled UI exception means state is suspect.
    }

    private static void LogCrash(Exception? ex)
    {
        if (ex is null)
            return;
        try
        {
            var path = Path.Combine(AperturePaths.DefaultDataDir, "crash.log");
            Directory.CreateDirectory(AperturePaths.DefaultDataDir);
            File.AppendAllText(path, $"[{DateTime.Now:O}] {ex}\n\n");
        }
        catch { /* logging must never throw */ }
    }
}
