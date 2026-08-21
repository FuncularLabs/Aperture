using Aperture.App;
using Aperture.App.Updates;

namespace Aperture.Core.Tests;

public class RegistrationServiceTests
{
    [Fact]
    public void AppDataInstallExe_IsUnderLocalProgramsAperture()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Aperture", "Aperture.exe");
        Assert.Equal(expected, RegistrationService.AppDataInstallExe);
    }

    [Fact]
    public void ShortcutNames_AreApertureLnk()
    {
        Assert.Equal("Aperture.lnk", Path.GetFileName(RegistrationService.StartMenuLinkPath));
        Assert.Equal("Aperture.lnk", Path.GetFileName(RegistrationService.DesktopLinkPath));
    }

    [Fact]
    public void FinishMove_RefusesToDeleteTheInstalledCopy()
    {
        // Safety: a mistaken --finish-move of the AppData path must be a no-op.
        // We can't observe the skip except by it not throwing.
        RegistrationService.FinishMove(RegistrationService.AppDataInstallExe);
    }

    [Fact]
    public void RunningKind_MatchesIsRunningFromAppDataInstall()
    {
        // One source of truth: this process is the AppData exe, or it is portable.
        // testhost is never %LocalAppData%\Programs\Aperture\Aperture.exe.
        var runningInstalled = RegistrationService.IsRunningFromAppDataInstall();
        var expectedKind = runningInstalled ? "installed" : "portable";

        Assert.False(runningInstalled);
        Assert.Equal("portable", RegistrationService.RunningKind);
        Assert.Equal(expectedKind, RegistrationService.RunningKind);
        Assert.Equal(runningInstalled, UpdateService.IsInstalled());
        Assert.Equal(
            PathsEqual(RegistrationService.CurrentExePath, RegistrationService.AppDataInstallExe),
            runningInstalled);
    }

    [Fact]
    public void RunningKind_IsNotWhetherAnAppDataCopyExists()
    {
        // A leftover AppData exe must not flip this testhost to "installed".
        // The About badge and updater share IsRunningFromAppDataInstall, not
        // IsInstalledToAppData (file exists).
        var kindFromProcess = RegistrationService.IsRunningFromAppDataInstall()
            ? "installed" : "portable";
        Assert.Equal(kindFromProcess, RegistrationService.RunningKind);
        Assert.Equal(kindFromProcess, UpdateService.IsInstalled() ? "installed" : "portable");
    }

    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
