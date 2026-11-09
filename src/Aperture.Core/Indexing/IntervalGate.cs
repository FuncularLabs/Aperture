namespace Aperture.Core.Indexing;

/// <summary>
/// Lets each key through at most once an interval: the first time it's asked about, then once the interval has passed
/// since it was last let through (an ask it isn't let through on doesn't restart the interval). Runs on the system's
/// millisecond clock unless it's given one.
/// </summary>
public sealed class IntervalGate(TimeSpan interval, Func<long>? clockMs = null)
{
    private readonly Func<long> _clockMs = clockMs ?? (() => Environment.TickCount64);
    private readonly Dictionary<long, long> _lastAt = []; // key → when it was last let through, in ms

    /// <summary>How long a key waits after it's let through. A change applies from the next ask.</summary>
    public TimeSpan Interval { get; set; } = interval;

    /// <summary>The clock's reading now, in ms (for tests).</summary>
    internal long Now => _clockMs();

    /// <summary>Whether <paramref name="key"/> is let through now; if it is, its interval starts again.</summary>
    public bool IsDue(long key)
    {
        var now = Now; // the reading the tests check is the one it uses
        if (_lastAt.TryGetValue(key, out var last) && now - last < Interval.TotalMilliseconds)
            return false;
        _lastAt[key] = now;
        return true;
    }
}
