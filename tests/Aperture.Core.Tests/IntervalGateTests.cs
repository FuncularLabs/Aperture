using Aperture.Core.Indexing;

namespace Aperture.Core.Tests;

/// <summary>
/// How often a folder's count is read while it's indexed: at most once an interval, on a clock the tests set (the last
/// test, the system's). Rounds 36–38 of the multiple-libraries review each found the interval's tests, run through the
/// whole view model, short in one more direction; here each boundary is a line.
/// </summary>
public class IntervalGateTests
{
    private long _now;

    private IntervalGate Gate(TimeSpan interval) => new(interval, () => _now);

    private bool At(IntervalGate gate, long ms, long key = 7)
    {
        _now = ms;
        return gate.IsDue(key);
    }

    [Fact]
    public void AKey_IsLetThroughFirst_ThenOnceTheIntervalHasPassed()
    {
        // A second is a thousand milliseconds: not one (seconds), nor a million (microseconds) or ten million (ticks).
        var gate = Gate(TimeSpan.FromSeconds(1));

        Assert.True(At(gate, 0));     // the first time
        Assert.False(At(gate, 999));  // within the interval
        Assert.True(At(gate, 1000));  // it has passed: read again (not once a run)
        Assert.False(At(gate, 1001)); // measured from the last time through, not the first
        Assert.True(At(gate, 2000));
    }

    [Fact]
    public void AnAskThatIsntLetThrough_DoesntRestartTheInterval()
    {
        var gate = Gate(TimeSpan.FromSeconds(1));

        Assert.True(At(gate, 0));
        Assert.False(At(gate, 600));
        Assert.True(At(gate, 1000));
    }

    [Fact]
    public void EachKey_HasAnIntervalOfItsOwn()
    {
        // A library's folders are indexed side by side: one folder's reads mustn't hold back another's (round 23).
        var gate = Gate(TimeSpan.FromSeconds(1));

        Assert.True(At(gate, 0, key: 1));
        Assert.True(At(gate, 1, key: 2)); // within the first folder's interval
        Assert.False(At(gate, 2, key: 1));
        Assert.False(At(gate, 2, key: 2));
    }

    [Fact]
    public void AChangedInterval_AppliesFromTheNextAsk()
    {
        // The tests set the view model's interval after it's made.
        var gate = Gate(TimeSpan.FromSeconds(1));
        Assert.True(At(gate, 0));

        gate.Interval = TimeSpan.FromSeconds(10);

        Assert.False(At(gate, 1000));
        Assert.True(At(gate, 10_000));
    }

    [Fact]
    public void Now_IsTheClockItReads()
    {
        _now = 42;

        Assert.Equal(42, Gate(TimeSpan.FromSeconds(1)).Now);
    }

    [Fact]
    public void ByDefault_ItRunsOnTheSystemsMillisecondClock()
    {
        // Its reading, between two of the system's: two samples 50–100 ms apart passed a clock at a tenth of the speed,
        // or one that wraps each second (round 40).
        var gate = new IntervalGate(TimeSpan.FromSeconds(1));

        var before = Environment.TickCount64;
        var now = gate.Now;

        Assert.InRange(now, before, Environment.TickCount64);
    }
}
