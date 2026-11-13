using Aperture.Core.Settings;
using Aperture.Core.Tests.Support;

namespace Aperture.Core.Tests;

/// <summary>
/// The order of a library's top-level folders, kept in its settings (0.9.1 batch, item 5). A bad value must never cost
/// the user their other settings: the settings file is read in one go, and an unreadable one falls back to defaults.
/// </summary>
public class SettingsRootOrderTests
{
    [Fact]
    public void TheOrder_IsSavedAndReadBack()
    {
        using var dir = new TempDir();
        new SettingsService(dir.Path).Update(s => s.RootOrder = [3, 1, 2]);

        Assert.Equal([3L, 1L, 2L], new SettingsService(dir.Path).Current.RootOrder!);
    }

    [Fact]
    public void ALibraryWithoutOne_HasNone() =>
        Assert.Null(new ApertureSettings().RootOrder);

    [Theory]
    [InlineData("\"x\"", null)]
    [InlineData("{}", null)]
    [InlineData("true", null)]
    [InlineData("null", null)]
    [InlineData("[3, \"x\", null, 1.5, {}, 1, 3, 2]", "3,1,2")] // numbers kept (each once), anything else dropped
    [InlineData("[\"4\", 9007199254740993]", "9007199254740993")]
    public void AMalformedRootOrder_KeepsTheOtherSettings(string order, string? kept)
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, SettingsService.FileName),
            "{ \"DefaultZoom\": 4, \"CaptionFormat\": \"{name}\", \"RootOrder\": " + order + ", \"GroupByDate\": false }");

        var settings = new SettingsService(dir.Path).Current;

        Assert.Equal((4, "{name}", false), (settings.DefaultZoom, settings.CaptionFormat, settings.GroupByDate));
        Assert.Equal(kept, settings.RootOrder is { } o ? string.Join(",", o) : null);
    }
}
