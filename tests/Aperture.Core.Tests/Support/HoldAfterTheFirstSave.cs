using Aperture.Core.Models;

namespace Aperture.Core.Tests.Support;

/// <summary>
/// Hands an index run's reports on and, after its first saved batch, holds the run (on its own thread) until
/// <c>reads</c> has risen past what it was before that report was handed on, or 20 s pass. What the window asked for at
/// that batch is then read before the run goes on. Without the hold, a read asked for at the first batch and one asked for
/// at the run's end can be merged into one (one refresh at a time, merging what's asked for while it waits: by design),
/// or the count read after the next batch is saved, whenever the thread pool is slow to start the first: a test that
/// counts reads, or looks for the first batch's count, depended on the machine's speed (release review REL-1).
/// </summary>
internal sealed class HoldAfterTheFirstSave(IProgress<IndexProgress> inner, Func<int> reads) : IProgress<IndexProgress>
{
    private int _held;

    /// <summary>False if the hold gave up waiting (the read never came); the run then went on regardless.</summary>
    public bool ReadCame { get; private set; } = true;

    public void Report(IndexProgress value)
    {
        var before = reads(); // the reads this report leads to all come after it's handed on
        inner.Report(value);
        if (value.Phase != IndexPhase.Indexing || !value.Saved || Interlocked.Exchange(ref _held, 1) == 1)
            return;
        ReadCame = SpinWait.SpinUntil(() => reads() > before, TimeSpan.FromSeconds(20));
    }
}
