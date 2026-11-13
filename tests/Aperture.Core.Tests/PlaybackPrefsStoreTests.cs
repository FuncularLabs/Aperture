using Aperture.Core.Settings;
using Aperture.Core.Tests.Support;

namespace Aperture.Core.Tests;

/// <summary>
/// Quick view's mute and volume, kept for the next video and the next launch (0.9.1 batch, item 2: "Start muted the very
/// first time a video is played. After that, each video should start with the mute state and volume from the last video
/// the user Quick Viewed, saved across sessions"). A file that can't be read is "never played": muted.
/// </summary>
public class PlaybackPrefsStoreTests
{
    [Fact]
    public void TheDefault_IsMuted_AtHalfVolume() =>
        Assert.Equal(new PlaybackPrefs(Muted: true, Volume: 0.5), PlaybackPrefs.Default);

    [Fact]
    public void RoundTrips()
    {
        using var dir = new TempDir();
        var path = dir.Combine(PlaybackPrefsStore.FileName);

        new PlaybackPrefsStore(path).Save(new PlaybackPrefs(Muted: false, Volume: 0.8));

        Assert.Equal(new PlaybackPrefs(false, 0.8), new PlaybackPrefsStore(path).Load());
    }

    [Fact]
    public void Missing_IsNeverPlayed()
    {
        using var dir = new TempDir();

        Assert.Equal(PlaybackPrefs.Default, new PlaybackPrefsStore(dir.Combine("none", PlaybackPrefsStore.FileName)).Load());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("")]
    public void Corrupt_IsNeverPlayed(string text)
    {
        using var dir = new TempDir();
        var path = dir.Combine(PlaybackPrefsStore.FileName);
        File.WriteAllText(path, text);

        Assert.Equal(PlaybackPrefs.Default, new PlaybackPrefsStore(path).Load());
    }

    [Theory]
    [InlineData("{\"muted\": false}", false, 0.5)]          // what it has is kept; what it lacks is the default
    [InlineData("{\"volume\": 0.25}", true, 0.25)]
    [InlineData("{\"muted\": \"no\", \"volume\": \"loud\"}", true, 0.5)] // values of the wrong kind count as missing
    [InlineData("{\"Muted\": false, \"Volume\": 1}", false, 1.0)]        // any case
    [InlineData("{\"muted\": false, \"volume\": 7}", false, 1.0)]        // out of range: clamped
    [InlineData("{\"muted\": false, \"volume\": -2}", false, 0.0)]
    public void ItsValues_AreReadOneByOne(string json, bool muted, double volume)
    {
        using var dir = new TempDir();
        var path = dir.Combine(PlaybackPrefsStore.FileName);
        File.WriteAllText(path, json);

        Assert.Equal(new PlaybackPrefs(muted, volume), new PlaybackPrefsStore(path).Load());
    }

    [Fact]
    public void AFailedSave_KeepsTheOldFile_AndDoesNotThrow()
    {
        using var dir = new TempDir();
        var path = dir.Combine(PlaybackPrefsStore.FileName);
        var store = new PlaybackPrefsStore(path);
        store.Save(new PlaybackPrefs(false, 0.3));

        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            store.Save(new PlaybackPrefs(true, 0.9));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }

        Assert.Equal(new PlaybackPrefs(false, 0.3), store.Load());
        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp")); // nothing left behind
    }

    [Fact]
    public void ASaveMakesItsFolder()
    {
        using var dir = new TempDir();
        var path = dir.Combine("Aperture Libraries", PlaybackPrefsStore.FileName);

        new PlaybackPrefsStore(path).Save(new PlaybackPrefs(false, 0.6));

        Assert.Equal(new PlaybackPrefs(false, 0.6), new PlaybackPrefsStore(path).Load());
    }

    [Fact]
    public void PathFor_IsBesideTheLibraryList_OrInThePinnedLibrary()
    {
        Assert.Equal(Path.Combine(@"C:\Libs", "playback.json"), PlaybackPrefsStore.PathFor(null, @"C:\Libs"));
        Assert.Equal(Path.Combine(@"D:\Pinned", "playback.json"), PlaybackPrefsStore.PathFor(@"D:\Pinned", @"C:\Libs"));
    }

    [Fact]
    public void InMemory_KeepsWhatItIsGiven_AndWritesNothing()
    {
        var store = PlaybackPrefsStore.InMemory();

        Assert.Equal(PlaybackPrefs.Default, store.Load());
        store.Save(new PlaybackPrefs(false, 0.7));
        Assert.Equal(new PlaybackPrefs(false, 0.7), store.Load());
        Assert.Null(store.FilePath);
    }
}
