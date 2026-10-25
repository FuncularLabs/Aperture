using System.Collections.Concurrent;
using Aperture.Core.Models;

namespace Aperture.Core.Tests.Support;

/// <summary>
/// Holds an index mid-run: the indexer's first progress report (the Scanning phase, before any file is
/// processed) blocks until <see cref="Release"/>. Lets a test observe shutdown/switch behaviour while an
/// index is genuinely still running, instead of racing a tiny index that may already have finished.
/// </summary>
public sealed class BlockingProgress : IProgress<IndexProgress>
{
    private readonly ManualResetEventSlim _release = new();
    private int _first;

    /// <summary>Set once the index has reported and is now blocked.</summary>
    public ManualResetEventSlim Entered { get; } = new();

    public void Report(IndexProgress value)
    {
        if (Interlocked.Exchange(ref _first, 1) != 0)
            return;
        Entered.Set();
        _release.Wait(TimeSpan.FromSeconds(30));
    }

    public void Release() => _release.Set();
}

/// <summary>
/// A UI-thread stand-in: one dedicated thread pumping posted callbacks, installed as its
/// SynchronizationContext. Code that awaits without ConfigureAwait(false) resumes on this thread.
/// </summary>
public sealed class SingleThreadContext : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
    private readonly Thread _thread;

    public SingleThreadContext()
    {
        _thread = new Thread(() =>
        {
            SetSynchronizationContext(this);
            foreach (var (callback, state) in _queue.GetConsumingEnumerable())
                callback(state);
        }) { IsBackground = true };
        _thread.Start();
    }

    public int ThreadId => _thread.ManagedThreadId;

    public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

    public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

    /// <summary>Runs <paramref name="action"/> on this context's thread and completes when it does.</summary>
    public Task Run(Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(async _ =>
        {
            try { await action(); done.SetResult(); }
            catch (Exception ex) { done.SetException(ex); }
        }, null);
        return done.Task;
    }

    public void Dispose() => _queue.CompleteAdding();
}
