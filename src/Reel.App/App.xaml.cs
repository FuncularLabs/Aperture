using System.IO;
using System.Windows;
using System.Windows.Threading;
using Reel.App.Services;
using Reel.App.ViewModels;
using Reel.Core.Library;
using Reel.Core.Storage;

namespace Reel.App;

public partial class App : Application
{
    private LibraryService? _library;
    private MainViewModel? _viewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogCrash(args.ExceptionObject as Exception);

        // REEL_DATA_DIR relocates the index/thumbnail store (handy for testing or
        // pointing at a different drive); otherwise the default under LOCALAPPDATA.
        var dataDir = Environment.GetEnvironmentVariable("REEL_DATA_DIR");
        _library = new LibraryService(string.IsNullOrWhiteSpace(dataDir) ? ReelPaths.DefaultDataDir : dataDir);
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
            var path = Path.Combine(ReelPaths.DefaultDataDir, "crash.log");
            Directory.CreateDirectory(ReelPaths.DefaultDataDir);
            File.AppendAllText(path, $"[{DateTime.Now:O}] {ex}\n\n");
        }
        catch { /* logging must never throw */ }
    }
}
