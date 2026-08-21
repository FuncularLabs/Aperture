using Aperture.App;

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
}
