using Aperture.Core.Tests.Support;
using Aperture.Core.Watching;

namespace Aperture.Core.Tests;

public class RootWatcherTests
{
    [Fact]
    public void Watcher_CoalescesBurstOfEvents_IntoASingleFire()
    {
        using var lib = new TempDir();
        var fired = 0;
        var settled = new ManualResetEventSlim(false);

        using var watcher = new RootWatcher(lib.Path, debounceMs: 300);
        watcher.Changed += (_, _) =>
        {
            Interlocked.Increment(ref fired);
            settled.Set();
        };
        watcher.Start();

        // Drop a burst well inside the debounce window.
        for (var i = 0; i < 8; i++)
            TestImages.Write(lib.Combine($"burst{i}.jpg"), 64, 64);

        // Wait for the coalesced fire, then a bit longer to catch any stragglers.
        Assert.True(settled.Wait(TimeSpan.FromSeconds(5)), "watcher never fired");
        Thread.Sleep(500);

        Assert.Equal(1, fired);
    }

    [Fact]
    public void Watcher_DoesNotFire_WithoutActivity()
    {
        using var lib = new TempDir();
        var fired = 0;

        using var watcher = new RootWatcher(lib.Path, debounceMs: 200);
        watcher.Changed += (_, _) => Interlocked.Increment(ref fired);
        watcher.Start();

        Thread.Sleep(600);

        Assert.Equal(0, fired);
    }
}
