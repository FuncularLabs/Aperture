using Aperture.Core.Formatting;
using Aperture.Core.Models;

namespace Aperture.Core.Indexing;

/// <summary>
/// The index runs in progress and each one's latest report, for the window's indexing lines (0.9.1 batch, item 4). Kept
/// by run: a folder can be indexed twice at once (its watcher fires during a run), and each run's end removes only its
/// own. Folders are counted once, each by its newest run. Not thread-safe: the window's UI thread uses it.
/// </summary>
public sealed class IndexRuns
{
    private readonly SortedDictionary<int, (long RootId, IndexProgress? Progress)> _runs = [];
    private int _last;

    /// <summary>How many folders are being indexed.</summary>
    public int Count => _runs.Values.Select(r => r.RootId).Distinct().Count();

    /// <summary>A run of <paramref name="rootId"/> begins; returns its id.</summary>
    public int Start(long rootId)
    {
        var run = ++_last;
        _runs[run] = (rootId, null);
        return run;
    }

    /// <summary>The run's latest report; one for a run that has ended is dropped.</summary>
    public void Report(int run, IndexProgress progress)
    {
        if (_runs.TryGetValue(run, out var entry))
            _runs[run] = (entry.RootId, progress);
    }

    /// <summary>The run has ended.</summary>
    public void End(int run) => _runs.Remove(run);

    /// <summary>
    /// The progress bar for the folders being indexed: indeterminate (moving) while any is counting, cleaning up or yet
    /// to report; otherwise the files done in all of them over the files to do in all of them.
    /// </summary>
    public (bool Indeterminate, double Value, double Max) Bar()
    {
        var latest = Newest().Select(f => f.Progress).ToList();
        if (latest.Count == 0 || latest.Any(p => p is not { Phase: IndexPhase.Indexing, Total: > 0 }))
            return (true, 0, 0);
        return (false, latest.Sum(p => p!.Processed), latest.Sum(p => p!.Total));
    }

    /// <summary>The headline and the friendly line for the folders being indexed (see <see cref="IndexingMessage.For"/>).</summary>
    public (string Headline, string Note) Lines(Func<long, string> aliasOf) =>
        IndexingMessage.For([.. Newest().Select(f => (aliasOf(f.RootId), f.Progress))]);

    // Each folder being indexed, with its newest run's latest report (runs are kept in start order: Last is the newest).
    private IEnumerable<(long RootId, IndexProgress? Progress)> Newest() =>
        _runs.Values.GroupBy(r => r.RootId).Select(folder => (folder.Key, folder.Last().Progress));
}
