namespace Aperture.Core.Watching;

/// <summary>
/// Watches a root subtree and raises a single coalesced <see cref="Changed"/>
/// event after activity settles for the debounce interval. A burst of file
/// events (a sync client dropping in 200 photos) collapses into one re-index
/// trigger rather than 200.
/// </summary>
public sealed class RootWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _debounce;
    private readonly int _debounceMs;
    private readonly object _gate = new();
    private bool _disposed;

    /// <summary>Raised on a background thread once file activity has been quiet for the debounce window.</summary>
    public event EventHandler? Changed;

    public RootWatcher(string path, int debounceMs = 500)
    {
        _debounceMs = debounceMs;
        _debounce = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);

        _watcher = new FileSystemWatcher(path)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName
                | NotifyFilters.DirectoryName
                | NotifyFilters.LastWrite
                | NotifyFilters.Size,
        };
        _watcher.Created += OnFileSystemEvent;
        _watcher.Deleted += OnFileSystemEvent;
        _watcher.Changed += OnFileSystemEvent;
        _watcher.Renamed += OnFileSystemEvent;
        // Buffer overflow / dropped events: treat as "something changed".
        _watcher.Error += (_, _) => ScheduleFire();
    }

    public void Start() => _watcher.EnableRaisingEvents = true;

    public void Stop() => _watcher.EnableRaisingEvents = false;

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e) => ScheduleFire();

    private void ScheduleFire()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            // Restart the countdown; only the last event in a burst fires.
            _debounce.Change(_debounceMs, Timeout.Infinite);
        }
    }

    private void Fire()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        _watcher.Dispose();
        _debounce.Dispose();
    }
}
