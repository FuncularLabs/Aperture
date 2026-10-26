using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace Aperture.Core.Startup;

/// <summary>
/// The handoff when Aperture restarts itself (an update, the installer's move): the closing copy starts the new one
/// with <c>--after &lt;its process id&gt;</c>, and the new one waits for it to exit before claiming the window
/// (<see cref="SingleInstance"/>) — so the new copy doesn't open while the old one is still tearing its library
/// down, and the old exe is free for the new copy to clean up.
/// </summary>
public static class Relaunch
{
    public const string AfterFlag = "--after";

    /// <summary>How long a relaunched copy waits for the one it replaces before going on regardless.</summary>
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    /// <summary>The arguments for the copy this process starts: <c>--after</c> this process, then <paramref name="rest"/>.</summary>
    public static IReadOnlyList<string> ArgsAfter(int processId, IEnumerable<string>? rest = null) =>
        [AfterFlag, processId.ToString(CultureInfo.InvariantCulture), .. rest ?? []];

    /// <summary>The process this copy was started to replace, if its arguments name one.</summary>
    public static int? PreviousProcessId(IReadOnlyList<string> args)
    {
        for (var i = 0; i + 1 < args.Count; i++)
        {
            if (args[i] == AfterFlag
                && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var processId)
                && processId > 0)
                return processId;
        }
        return null;
    }

    /// <summary>
    /// Waits up to <paramref name="timeout"/> for the process to exit. True once it has, or if it isn't running; false
    /// if it's still running at the timeout, or can't be watched (then the caller goes on anyway).
    /// </summary>
    public static bool WaitForExit(int processId, TimeSpan timeout) => WaitForExit(processId, timeout, WaitOn);

    // `waitOn` is the test seam: whether this process may watch another depends on who runs the tests (an elevated
    // runner may watch even the System process), so tests raise what Windows raises instead.
    internal static bool WaitForExit(int processId, TimeSpan timeout, Func<int, TimeSpan, bool> waitOn)
    {
        try
        {
            return waitOn(processId, timeout);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return true; // not running (any more)
        }
        catch (Win32Exception)
        {
            return false; // running, but this process may not watch it (it's elevated)
        }
    }

    private static bool WaitOn(int processId, TimeSpan timeout)
    {
        using var process = Process.GetProcessById(processId);
        return process.WaitForExit(timeout);
    }
}
