using System.ComponentModel;
using System.Diagnostics;
using Aperture.Core.Startup;

namespace Aperture.Core.Tests;

/// <summary>
/// When Aperture restarts itself (an update, the installer's move), the closing copy starts the new one with
/// <c>--after &lt;pid&gt;</c> and the new one waits for it — otherwise the one-window rule would hand the window back to
/// the copy that is closing.
/// </summary>
public class RelaunchTests
{
    private static Process Start(string command) =>
        Process.Start(new ProcessStartInfo("cmd.exe", "/c " + command) { CreateNoWindow = true, UseShellExecute = false })!;

    [Fact]
    public void ArgsAfter_PutsThisProcessFirst_ThenTheRest()
    {
        Assert.Equal(["--after", "1234", "--finish-move", @"C:\Downloads\Aperture.exe"],
            Relaunch.ArgsAfter(1234, ["--finish-move", @"C:\Downloads\Aperture.exe"]));
        Assert.Equal(["--after", "7"], Relaunch.ArgsAfter(7));
    }

    // Arguments as the command line splits them, written here with "|" between them.
    private static string[] Args(string joined) => joined.Length == 0 ? [] : joined.Split('|');

    [Theory]
    [InlineData("--after|1234", 1234)]
    [InlineData(@"--finish-move|C:\x.exe|--after|42", 42)]
    public void PreviousProcessId_ReadsIt(string args, int processId) =>
        Assert.Equal(processId, Relaunch.PreviousProcessId(Args(args)));

    [Theory]
    [InlineData("")]
    [InlineData("--after")]
    [InlineData("--after|soon")]
    [InlineData("--after|-5")]
    [InlineData("--after|0")]
    [InlineData("--after| 12")]
    [InlineData("--finish-move|--after")]
    public void PreviousProcessId_IgnoresWhatIsntOne(string args) =>
        Assert.Null(Relaunch.PreviousProcessId(Args(args)));

    [Fact]
    public void RestartStartInfo_StartsTheNewCopyAfterThisOne()
    {
        // The in-place update, the portable update and the installer's move start the new copy with this — checked by
        // reading: the methods that call it replace and start the running exe, which a test can't let them do.
        var psi = Aperture.App.Updates.UpdateService.RestartStartInfo(@"C:\Apps\Aperture.exe", ["--finish-move", @"C:\Downloads\Aperture.exe"]);

        Assert.Equal(@"C:\Apps\Aperture.exe", psi.FileName);
        Assert.True(psi.UseShellExecute);
        Assert.Equal(["--after", Environment.ProcessId.ToString(), "--finish-move", @"C:\Downloads\Aperture.exe"], psi.ArgumentList);
        Assert.Equal(["--after", Environment.ProcessId.ToString()],
            Aperture.App.Updates.UpdateService.RestartStartInfo(@"C:\Apps\Aperture.exe", null).ArgumentList);
    }

    [Fact]
    public void WaitForExit_OfAProcessThisOneMayNotWatch_SaysItCouldnt()
    {
        // What Process.WaitForExit throws for an elevated process watched from an unelevated one (ERROR_ACCESS_DENIED).
        // Raised through the seam: whether a real process is watchable depends on who runs the tests.
        Assert.False(Relaunch.WaitForExit(1234, TimeSpan.FromSeconds(10), (_, _) => throw new Win32Exception(5)));
    }

    [Fact]
    public void ARelaunch_WaitsHalfAMinuteAtMost() => Assert.Equal(TimeSpan.FromSeconds(30), Relaunch.Wait);

    [Fact]
    public void WaitForExit_ReturnsAtOnce_ForAProcessThatIsntRunning()
    {
        var clock = Stopwatch.StartNew();

        Assert.True(Relaunch.WaitForExit(int.MaxValue, TimeSpan.FromSeconds(10)));
        Assert.True(clock.ElapsedMilliseconds < 2000);
    }

    [Fact]
    public void WaitForExit_WaitsForTheProcessToExit()
    {
        using var closing = Start("ping -n 2 127.0.0.1 > nul"); // about a second

        Assert.True(Relaunch.WaitForExit(closing.Id, TimeSpan.FromSeconds(30)));
        Assert.True(closing.HasExited);
    }

    [Fact]
    public void WaitForExit_GivesUpAfterTheTimeout()
    {
        using var hung = Start("ping -n 30 127.0.0.1 > nul");
        try
        {
            var clock = Stopwatch.StartNew();

            Assert.False(Relaunch.WaitForExit(hung.Id, TimeSpan.FromMilliseconds(200)));
            Assert.True(clock.ElapsedMilliseconds < 5000);
            Assert.False(hung.HasExited);
        }
        finally
        {
            hung.Kill(entireProcessTree: true);
        }
    }
}
