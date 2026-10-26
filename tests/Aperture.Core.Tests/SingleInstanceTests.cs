using System.Security.AccessControl;
using System.Security.Principal;
using Aperture.Core.Startup;

namespace Aperture.Core.Tests;

/// <summary>
/// One Aperture window (Paul, 2026-10-26): the first launch claims a named event for as long as it runs; a later
/// launch asks it to come to the front and exits. Real kernel objects, each test under a name of its own.
/// </summary>
public class SingleInstanceTests
{
    private static string AName() =>
        SingleInstance.NameFor(Path.Combine(Path.GetTempPath(), "aperture-window-" + Guid.NewGuid().ToString("N")));

    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(5);

    [Fact]
    public void TheFirstLaunch_IsTheInstance_ALaterOneIsNot()
    {
        var name = AName();

        Assert.Equal(SingleInstanceClaim.Claimed, SingleInstance.TryClaim(name, () => { }, out var first));
        using (first)
        {
            Assert.NotNull(first);
            Assert.Equal(SingleInstanceClaim.AlreadyRunning, SingleInstance.TryClaim(name, () => { }, out var second));
            Assert.Null(second);
        }
    }

    [Fact]
    public void ALaterLaunch_AsksTheRunningOneToComeForward()
    {
        var name = AName();
        using var asked = new SemaphoreSlim(0);
        SingleInstance.TryClaim(name, () => asked.Release(), out var first);
        using (first)
        {
            SingleInstance.TryClaim(name, () => { }, out _);

            Assert.True(asked.Wait(Soon));
        }
    }

    [Fact]
    public void EveryLaterLaunch_AsksAgain()
    {
        var name = AName();
        using var asked = new SemaphoreSlim(0);
        SingleInstance.TryClaim(name, () => asked.Release(), out var first);
        using (first)
        {
            for (var launch = 0; launch < 3; launch++)
            {
                SingleInstance.TryClaim(name, () => { }, out _);
                Assert.True(asked.Wait(Soon), $"launch {launch + 2} wasn't passed on");
            }
        }
    }

    [Fact]
    public void OnceTheRunningOneHasClosed_TheNextLaunchIsTheInstance()
    {
        var name = AName();
        SingleInstance.TryClaim(name, () => { }, out var first);
        first!.Dispose(); // the running Aperture exits (or crashes: Windows drops its event either way)

        Assert.Equal(SingleInstanceClaim.Claimed, SingleInstance.TryClaim(name, () => { }, out var next));
        next!.Dispose();
    }

    [Fact]
    public void ARunningOneThisLaunchMayNotSignal_IsReported()
    {
        var name = AName();
        var systemOnly = new EventWaitHandleSecurity(); // as an elevated Aperture's event looks to an unelevated launch
        systemOnly.AddAccessRule(new EventWaitHandleAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), EventWaitHandleRights.FullControl, AccessControlType.Allow));
        using var elevated = EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, name, out var created, systemOnly);
        Assert.True(created);
        Assert.ThrowsAny<UnauthorizedAccessException>(() => EventWaitHandle.OpenExisting(name).Dispose()); // the premise

        Assert.Equal(SingleInstanceClaim.RunningButUnreachable, SingleInstance.TryClaim(name, () => { }, out var instance));
        Assert.Null(instance);
    }

    [Fact]
    public void ANameTakenBySomethingElse_LetsThisLaunchRunWithoutTheGuard()
    {
        var name = AName();
        using var squatter = new Mutex(false, name); // not an event: whatever it is, it isn't an Aperture

        Assert.Equal(SingleInstanceClaim.Claimed, SingleInstance.TryClaim(name, () => { }, out var instance));
        Assert.Null(instance); // nothing to listen on — but Aperture still starts
    }

    [Fact]
    public void ManyLaunchesAtOnce_OnlyOneIsTheInstance()
    {
        var name = AName();
        const int launches = 8;
        using var start = new Barrier(launches);
        var claims = new SingleInstanceClaim[launches];
        var instances = new SingleInstance?[launches];
        var threads = Enumerable.Range(0, launches).Select(i => new Thread(() =>
        {
            start.SignalAndWait();
            claims[i] = SingleInstance.TryClaim(name, () => { }, out instances[i]);
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        try
        {
            Assert.Single(claims, c => c == SingleInstanceClaim.Claimed);
            Assert.Equal(launches - 1, claims.Count(c => c == SingleInstanceClaim.AlreadyRunning));
        }
        finally
        {
            foreach (var instance in instances)
                instance?.Dispose();
        }
    }

    [Fact]
    public void NameFor_IsOnePerPlace()
    {
        var list = @"C:\Users\someone\AppData\Local\Aperture Libraries\catalogs.json";
        var pinned = @"D:\pinned\library";

        Assert.Equal(SingleInstance.NameFor(list), SingleInstance.NameFor(list.ToUpperInvariant()));
        Assert.Equal(SingleInstance.NameFor(pinned), SingleInstance.NameFor(pinned + @"\"));
        Assert.Equal(SingleInstance.NameFor("relative"), SingleInstance.NameFor(Path.GetFullPath("relative")));
        Assert.NotEqual(SingleInstance.NameFor(list), SingleInstance.NameFor(pinned));
        Assert.StartsWith(@"Local\Aperture.Window.", SingleInstance.NameFor(list)); // this Windows session only
    }

    [Fact]
    public void AnActivationThatThrows_DoesntStopTheNext()
    {
        var name = AName();
        var calls = 0;
        using var asked = new SemaphoreSlim(0);
        SingleInstance.TryClaim(name, () =>
        {
            asked.Release();
            if (Interlocked.Increment(ref calls) == 1)
                throw new InvalidOperationException("the window was closing");
        }, out var first);
        using (first)
        {
            SingleInstance.TryClaim(name, () => { }, out _);
            Assert.True(asked.Wait(Soon));
            SingleInstance.TryClaim(name, () => { }, out _);
            Assert.True(asked.Wait(Soon)); // still listening
        }
    }

    [Fact]
    public void Dispose_StopsListening_AndCanBeRepeated()
    {
        var name = AName();
        var askedTheClosedOne = 0;
        SingleInstance.TryClaim(name, () => Interlocked.Increment(ref askedTheClosedOne), out var closed);
        closed!.Dispose();
        closed.Dispose();

        using var asked = new SemaphoreSlim(0);
        Assert.Equal(SingleInstanceClaim.Claimed, SingleInstance.TryClaim(name, () => asked.Release(), out var next));
        using (next)
        {
            SingleInstance.TryClaim(name, () => { }, out _);
            Assert.True(asked.Wait(Soon)); // the new instance was asked …
        }
        Assert.Equal(0, askedTheClosedOne); // … and the closed one never was
    }

    [Fact]
    public void Dispose_FromInsideTheActivation_DoesntHang()
    {
        var name = AName();
        SingleInstance? first = null;
        using var done = new SemaphoreSlim(0);
        SingleInstance.TryClaim(name, () => { first!.Dispose(); done.Release(); }, out first);

        SingleInstance.TryClaim(name, () => { }, out _);

        Assert.True(done.Wait(Soon));
        Assert.Equal(SingleInstanceClaim.Claimed, SingleInstance.TryClaim(name, () => { }, out var next));
        next!.Dispose();
    }
}
