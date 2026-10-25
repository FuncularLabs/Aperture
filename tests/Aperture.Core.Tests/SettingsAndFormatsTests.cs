using Aperture.Core.Media;
using Aperture.Core.Models;
using Aperture.Core.Settings;
using Aperture.Core.Tests.Support;

namespace Aperture.Core.Tests;

public class SettingsAndFormatsTests
{
    [Theory]
    [InlineData(".jpg", MediaKind.Image)]
    [InlineData(".PNG", MediaKind.Image)]
    [InlineData(".mp4", MediaKind.Video)]
    [InlineData(".MOV", MediaKind.Video)]
    public void SupportedFormats_ClassifiesKind(string ext, MediaKind expected)
    {
        Assert.Equal(expected, SupportedFormats.KindOf(ext));
    }

    [Fact]
    public void SupportedFormats_VideoGatedByFlag()
    {
        Assert.True(SupportedFormats.IsSupported(".mp4", includeVideos: true));
        Assert.False(SupportedFormats.IsSupported(".mp4", includeVideos: false));
        Assert.True(SupportedFormats.IsSupported(".jpg", includeVideos: false));
    }

    [Fact]
    public void Settings_DefaultsAreUsableOutOfTheBox()
    {
        using var dir = new TempDir();
        var settings = new SettingsService(dir.Path);

        Assert.True(settings.Current.IncludeVideos);
        Assert.Equal(2, settings.Current.DefaultZoom);
        Assert.Equal("Newest first", settings.Current.SortPreset);
        Assert.True(settings.Current.GroupByDate);
    }

    [Fact]
    public void Settings_PersistAcrossInstances()
    {
        using var dir = new TempDir();

        var first = new SettingsService(dir.Path);
        first.Update(s =>
        {
            s.IncludeVideos = false;
            s.CaptionFormat = "{name}";
        });

        var second = new SettingsService(dir.Path);
        Assert.False(second.Current.IncludeVideos);
        Assert.Equal("{name}", second.Current.CaptionFormat);
    }

    [Fact]
    public void Settings_CorruptFile_FallsBackToDefaults()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.Combine(SettingsService.FileName), "{ not json");

        var settings = new SettingsService(dir.Path);

        Assert.Equal(2, settings.Current.DefaultZoom);
    }

    [Fact]
    public void Settings_UnwritableFile_UpdateKeepsTheChange_RaisesChanged_AndDoesNotThrow()
    {
        using var dir = new TempDir();
        var path = dir.Combine(SettingsService.FileName);
        var settings = new SettingsService(dir.Path);
        settings.Update(s => s.DefaultZoom = 1);
        var raised = false;
        settings.Changed += (_, _) => raised = true;

        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            settings.Update(s => s.DefaultZoom = 4);
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }

        Assert.True(raised);
        Assert.Equal(4, settings.Current.DefaultZoom);                   // this session keeps it
        Assert.Equal(1, new SettingsService(dir.Path).Current.DefaultZoom); // but it couldn't be saved
    }
}
