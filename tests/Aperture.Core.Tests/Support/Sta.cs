using System.Runtime.ExceptionServices;
using System.Windows.Threading;

namespace Aperture.Core.Tests.Support;

/// <summary>Runs a test body on its own STA thread, as WPF elements require, and rethrows what it throws.</summary>
public static class Sta
{
    public static void Run(Action body)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    /// <summary>
    /// Pumps this thread's dispatcher (so posted work and async continuations run) until <paramref name="done"/> is
    /// true or <paramref name="timeout"/> passes. Returns whether it came true. Call from inside <see cref="Run"/>,
    /// after installing a <see cref="DispatcherSynchronizationContext"/>.
    /// </summary>
    public static bool PumpUntil(Func<bool> done, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(10), DispatcherPriority.Background, (_, _) =>
        {
            if (done() || DateTime.UtcNow >= deadline)
                frame.Continue = false;
        }, Dispatcher.CurrentDispatcher);
        Dispatcher.PushFrame(frame);
        timer.Stop();
        return done();
    }
}
