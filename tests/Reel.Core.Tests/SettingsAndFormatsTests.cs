using Reel.Core.Media;
using Reel.Core.Models;
using Reel.Core.Settings;
using Reel.Core.Tests.Support;

namespace Reel.Core.Tests;

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
}
