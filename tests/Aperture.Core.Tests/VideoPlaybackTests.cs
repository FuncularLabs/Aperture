using Aperture.App;
using Aperture.Core.Settings;
using Aperture.Core.Tests.Support;

namespace Aperture.Core.Tests;

/// <summary>
/// Quick view's video playback, as a model over a player (0.9.1 batch, items 1–2): it plays a video as it's shown,
/// muted the first time ever and as the last one was left after that; Space or a click plays and pauses it; one player
/// per visit, and what a player left behind says changes nothing.
/// </summary>
public class VideoPlaybackTests
{
    private readonly List<FakeVideoPlayer> _players = [];
    private readonly PlaybackPrefsStore _store = PlaybackPrefsStore.InMemory();

    private VideoPlayback Playback() => new(_store, () =>
    {
        var player = new FakeVideoPlayer();
        _players.Add(player);
        return player;
    });

    [Fact]
    public void TheFirstVideoEver_StartsMuted_AndPlays()
    {
        var playback = Playback();

        playback.Show(@"C:\v\a.mp4");

        var player = Assert.Single(_players);
        Assert.Equal(new Uri(@"C:\v\a.mp4"), player.Source);
        Assert.Equal((true, 0.5), (player.IsMuted, player.Volume));
        Assert.Equal("play", player.LastPlayOrPause);
        Assert.True(playback.IsVideoShown);
        Assert.True(playback.IsPlaying);
        Assert.True(playback.IsMuted);
        Assert.Equal(0.5, playback.Volume);
        Assert.False(playback.IsReady);
    }

    [Fact]
    public void TheNextVideo_StartsAsTheLastWasLeft()
    {
        var playback = Playback();
        playback.Show(@"C:\v\a.mp4");
        playback.SetMuted(false);
        playback.SetVolume(0.8);

        playback.Show(@"C:\v\b.mp4");

        Assert.Equal(2, _players.Count);
        Assert.True(_players[0].IsClosed); // one player per visit: the last one let go of its file
        Assert.Equal((false, 0.8), (_players[1].IsMuted, _players[1].Volume));
        Assert.False(playback.IsMuted);
    }

    [Fact]
    public void AMute_IsSavedAtOnce_AndAVolume_AsTheVideoIsLeft()
    {
        var playback = Playback();
        playback.Show(@"C:\v\a.mp4");

        playback.SetMuted(false);
        Assert.Equal(new PlaybackPrefs(false, 0.5), _store.Load());
        playback.SetVolume(0.4);
        playback.SetVolume(0.3);                                   // a slider dragged: no file written for each step
        Assert.Equal(new PlaybackPrefs(false, 0.5), _store.Load());
        playback.Leave();
        Assert.Equal(new PlaybackPrefs(false, 0.3), _store.Load()); // saved as the video is left

        // A new playback (a restart) starts as it was left.
        var next = new VideoPlayback(_store, () => { var p = new FakeVideoPlayer(); _players.Add(p); return p; });
        next.Show(@"C:\v\b.mp4");
        Assert.Equal((false, 0.3), (_players[^1].IsMuted, _players[^1].Volume));
    }

    [Fact]
    public void AVolumeSet_IsSavedWhenTheNextVideoIsShown()
    {
        var playback = Playback();
        playback.Show(@"C:\v\a.mp4");
        playback.SetVolume(0.2);

        playback.Show(@"C:\v\b.mp4");

        Assert.Equal(new PlaybackPrefs(true, 0.2), _store.Load());
    }

    [Fact]
    public void AVisit_DoesNotInheritTheLastVideosEnd()
    {
        // Review R1-B7: after a video played to its end, the next one's first play after a pause went back to its start.
        var playback = Playback();
        playback.Show(@"C:\v\a.mp4");
        _players[0].RaiseOpened();
        _players[0].RaiseEnded();

        playback.Show(@"C:\v\b.mp4");
        playback.Toggle(); // pause
        playback.Toggle(); // and play on

        Assert.DoesNotContain("rewind", _players[1].Calls);
    }

    [Fact]
    public void Toggle_PausesAndPlays()
    {
        var playback = Playback();
        playback.Show(@"C:\v\a.mp4");
        _players[0].RaiseOpened();

        playback.Toggle();
        Assert.False(playback.IsPlaying);
        Assert.Equal("pause", _players[0].LastPlayOrPause);

        playback.ToggleCommand.Execute(null);
        Assert.True(playback.IsPlaying);
        Assert.Equal("play", _players[0].LastPlayOrPause);
    }

    [Fact]
    public void BeforeItOpens_SpaceSetsWhetherItPlays()
    {
        var playback = Playback();
        playback.Show(@"C:\v\a.mp4");

        playback.Toggle();                // paused before it has opened…
        _players[0].RaiseOpened(640, 360);

        Assert.True(playback.IsReady);
        Assert.False(playback.IsPlaying); // …and it stays paused once it has
        Assert.Equal("pause", _players[0].LastPlayOrPause);
        Assert.Equal((640, 360), playback.NaturalSize);
    }

    [Fact]
    public void AtTheEnd_ItPauses_AndPlaysAgainFromTheStart()
    {
        var playback = Playback();
        playback.Show(@"C:\v\a.mp4");
        _players[0].RaiseOpened();

        _players[0].RaiseEnded();
        Assert.False(playback.IsPlaying);

        playback.Toggle();
        Assert.True(playback.IsPlaying);
        Assert.Equal(["rewind", "play"], _players[0].Calls.TakeLast(2));
        playback.Toggle();                // and an ordinary pause after that, not another rewind
        Assert.Equal(["rewind", "play", "pause"], _players[0].Calls.TakeLast(3));
    }

    [Fact]
    public void Mute_TogglesSound()
    {
        var playback = Playback();
        playback.Show(@"C:\v\a.mp4");

        playback.ToggleMuteCommand.Execute(null);
        Assert.False(playback.IsMuted);
        Assert.False(_players[0].IsMuted);
        playback.ToggleMute();
        Assert.True(playback.IsMuted);
        Assert.True(_players[0].IsMuted);
    }

    [Theory]
    [InlineData(7, 1.0)]
    [InlineData(-1, 0.0)]
    [InlineData(0.25, 0.25)]
    [InlineData(double.NaN, 0.5)] // not a volume: it stays as it was
    public void Volume_IsClamped(double asked, double volume)
    {
        var playback = Playback();
        playback.Show(@"C:\v\a.mp4");

        playback.Volume = asked; // as the slider sets it

        Assert.Equal(volume, playback.Volume);
        Assert.Equal(volume, _players[0].Volume);
    }

    [Fact]
    public void RaisingTheVolume_WhileMuted_Unmutes_AndLoweringItDoesNot()
    {
        var playback = Playback();
        playback.Show(@"C:\v\a.mp4"); // muted, at 0.5

        playback.SetVolume(0.4);
        Assert.True(playback.IsMuted);

        playback.SetVolume(0.6);
        Assert.False(playback.IsMuted);
        Assert.False(_players[0].IsMuted);
        Assert.Equal(new PlaybackPrefs(false, 0.6), _store.Load());
    }

    [Fact]
    public void AnEventFromAVideoLeft_ChangesNothing()
    {
        var playback = Playback();
        playback.Show(@"C:\v\a.mp4");
        playback.Show(@"C:\v\b.mp4");

        _players[0].RaiseOpened();
        _players[0].RaiseEnded();
        _players[0].RaiseFailed();

        Assert.False(playback.IsReady);
        Assert.True(playback.IsPlaying);
        Assert.False(playback.HasFailed);
        Assert.False(_players[1].IsClosed);
    }

    [Fact]
    public void AVideoThatFails_SaysSo_AndLetsGoOfItsFile()
    {
        var playback = Playback();
        playback.Show(@"C:\v\a.mp4");

        _players[0].RaiseFailed();

        Assert.True(playback.HasFailed);
        Assert.False(playback.IsPlaying);
        Assert.True(_players[0].IsClosed);
        Assert.False(playback.SpaceToggles); // Space closes quick view, as for a photo
        Assert.Contains("can't be played here", playback.StatusLine);
        Assert.Contains("Enter", playback.StatusLine);
        playback.Toggle();                   // nothing to play
        Assert.False(playback.IsPlaying);
    }

    [Fact]
    public void AFactoryThatThrows_CountsAsCannotPlay()
    {
        // A PC without a media player: making one fails, and the video keeps its still frame.
        var playback = new VideoPlayback(_store, () => throw new NotSupportedException("no media player"));

        playback.Show(@"C:\v\a.mp4");

        Assert.True(playback.IsVideoShown);
        Assert.True(playback.HasFailed);
        Assert.False(playback.IsPlaying);
        Assert.Null(playback.Player);
    }

    [Fact]
    public void TheStatusLine_SaysLoadingUntilItOpens()
    {
        var playback = Playback();
        Assert.Equal("", playback.StatusLine);

        playback.Show(@"C:\v\a.mp4");
        Assert.Equal("Loading video…", playback.StatusLine);

        _players[0].RaiseOpened();
        Assert.Equal("", playback.StatusLine);
    }

    [Fact]
    public void Leave_StopsAndLetsGo_AndAgainIsHarmless()
    {
        var playback = Playback();
        playback.Show(@"C:\v\a.mp4");
        _players[0].RaiseOpened();

        playback.Leave();
        playback.Leave();

        Assert.True(_players[0].IsClosed);
        Assert.Equal(1, _players[0].Calls.Count(c => c == "close"));
        Assert.False(playback.IsVideoShown);
        Assert.False(playback.IsReady);
        Assert.False(playback.IsPlaying);
        Assert.Null(playback.Player);
        Assert.False(playback.SpaceToggles);
    }

    [Fact]
    public void Pause_ForOpeningItElsewhere()
    {
        var playback = Playback();
        playback.Show(@"C:\v\a.mp4");

        playback.Pause();
        playback.Pause();

        Assert.False(playback.IsPlaying);
        Assert.Equal("pause", _players[0].LastPlayOrPause);
    }

    [Fact]
    public void WithNothingShown_TheControlsDoNothing()
    {
        var playback = Playback();

        playback.Toggle();
        playback.Pause();
        playback.ToggleMute();
        playback.SetVolume(0.9);

        Assert.Empty(_players);
        Assert.False(playback.IsPlaying);
        Assert.Equal(new PlaybackPrefs(false, 0.9), _store.Load()); // the sound is the user's choice even so
    }

    [Theory]
    [InlineData(false, false, true)] // no still and not open yet: the whole area is the video
    [InlineData(true, true, true)]
    [InlineData(true, false, false)] // beside the picture
    public void IsOnVideo(bool hasPicture, bool onPicture, bool onVideo) =>
        Assert.Equal(onVideo, VideoPlayback.IsOnVideo(hasPicture, onPicture));

    [Fact]
    public void ItsStateChanges_AreAnnounced_ForTheControls()
    {
        var playback = Playback();
        var changed = new List<string?>();
        playback.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        playback.Show(@"C:\v\a.mp4");
        _players[0].RaiseOpened();
        playback.Toggle();
        playback.ToggleMute();
        playback.SetVolume(0.7);

        foreach (var name in new[]
                 {
                     nameof(VideoPlayback.IsVideoShown), nameof(VideoPlayback.IsReady), nameof(VideoPlayback.IsPlaying),
                     nameof(VideoPlayback.IsMuted), nameof(VideoPlayback.Volume), nameof(VideoPlayback.StatusLine),
                 })
            Assert.Contains(name, changed);
    }
}
