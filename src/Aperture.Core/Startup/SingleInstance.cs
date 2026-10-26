using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Aperture.Core.Startup;

/// <summary>What <see cref="SingleInstance.TryClaim"/> found.</summary>
public enum SingleInstanceClaim
{
    /// <summary>
    /// No Aperture is running here: this launch is the one. Normally it gets an instance to keep until it gives the
    /// window up; if the name is taken by something that isn't an event at all, it gets none and runs without the guard.
    /// </summary>
    Claimed,

    /// <summary>Aperture is already running here, and has been asked to bring its window to the front.</summary>
    AlreadyRunning,

    /// <summary>
    /// Aperture is already running here, but this launch can't ask it anything (it runs elevated and this launch
    /// doesn't).
    /// </summary>
    RunningButUnreachable,
}

/// <summary>
/// One Aperture window per library list (or per pinned data folder). The first launch claims a named event and keeps
/// it until it gives the window up (Aperture does as its window starts closing); a later launch finds it, asks the
/// running one to come to the front, and exits. Windows removes the event when the process that made it exits or
/// crashes, so nothing stale can block a launch.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const int AnyProcess = -1; // ASFW_ANY

    private readonly EventWaitHandle _asked;
    private readonly ManualResetEvent _closing = new(false);
    private readonly Action _activate;
    private readonly Thread _listener;
    private int _disposed;

    private SingleInstance(EventWaitHandle asked, Action activate)
    {
        _asked = asked;
        _activate = activate;
        _listener = new Thread(Listen) { IsBackground = true, Name = "Aperture single window" };
        _listener.Start();
    }

    /// <summary>
    /// The event's name for one place — the library list's path, or a pinned data folder — in this Windows session.
    /// </summary>
    public static string NameFor(string place)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(place)).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];
        return @"Local\Aperture.Window." + hash;
    }

    /// <summary>
    /// The launch's claim: if this launch was started to replace another copy (<c>--after</c> in
    /// <paramref name="args"/>, see <see cref="Relaunch"/>), it first waits — up to <see cref="Relaunch.Wait"/> — for
    /// that copy to exit; then <see cref="TryClaim"/>.
    /// </summary>
    public static SingleInstanceClaim ClaimAtLaunch(IReadOnlyList<string> args, string name, Action activate, out SingleInstance? instance)
    {
        if (Relaunch.PreviousProcessId(args) is { } previous)
            Relaunch.WaitForExit(previous, Relaunch.Wait);
        return TryClaim(name, activate, out instance);
    }

    /// <summary>
    /// Claims <paramref name="name"/>, or — if a running Aperture holds it — asks that one to come to the front.
    /// <paramref name="activate"/> runs on a background thread each time a later launch asks, so it must get onto the
    /// UI thread itself. Dispose the instance to give the window up.
    /// </summary>
    public static SingleInstanceClaim TryClaim(string name, Action activate, out SingleInstance? instance)
    {
        instance = null;
        EventWaitHandle handle;
        bool createdNew;
        try
        {
            handle = new EventWaitHandle(false, EventResetMode.AutoReset, name, out createdNew);
        }
        catch (UnauthorizedAccessException)
        {
            return SingleInstanceClaim.RunningButUnreachable;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return SingleInstanceClaim.Claimed; // the name belongs to something that isn't an event: start unguarded
        }

        if (createdNew)
        {
            instance = new SingleInstance(handle, activate);
            return SingleInstanceClaim.Claimed;
        }

        using (handle)
        {
            // Windows lets only the foreground process put a window in front; this launch is it, so pass that on.
            AllowSetForegroundWindow(AnyProcess);
            handle.Set();
        }
        return SingleInstanceClaim.AlreadyRunning;
    }

    private void Listen()
    {
        WaitHandle[] handles = [_asked, _closing];
        while (Volatile.Read(ref _disposed) == 0 && WaitHandle.WaitAny(handles) == 0)
        {
            try
            {
                _activate();
            }
            catch
            {
                // The window may be closing; a later launch's ask must still be heard.
            }
        }
    }

    /// <summary>
    /// Gives up the claim: the next launch becomes the instance. Safe to call more than once, and from inside the
    /// activation; the activation must not block on the thread that disposes (it would wait for itself).
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        if (Thread.CurrentThread != _listener)
        {
            _closing.Set();
            _listener.Join();
        }
        // Called from inside the activation, the listener isn't waiting now and checks _disposed before it waits again.
        _asked.Dispose();
        _closing.Dispose();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}
