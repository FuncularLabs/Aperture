using Aperture.App;
using Aperture.Core.Settings;
using Aperture.Core.Tests.Support;

namespace Aperture.Core.Tests;

/// <summary>
/// Quick view's video playback, as a model over a player (0.9.1 batch, items 1–2; 0.9.2 V9–V10): it opens a video paused
/// as it's shown, muted the first time ever and as the last one was left after that; Space or a click plays and pauses
/// it; a seek bar and a time follow it; one player per visit, and what a player left behind says changes nothing.
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
    public void TheFirstVideoEver_StartsMuted_AndOpensPaused()
    {
        var playback = Playback();

        playback.Show(@"C:\v\a.mp4");

        var player = Assert.Single(_players);
        Assert.Equal(new Uri(@"C:\v\a.mp4"), player.Source);
        Assert.Equal((true, 0.5), (player.IsMuted, player.Volume));
        Assert.Equal("pause", player.LastPlayOrPause);
        Assert.True(playback.IsVideoShown);
        Assert.False(playback.IsPlaying);
        Assert.True(playback.IsMuted);
        Assert.Equal(0.5, playback.Volume);
        Assert.False(playback.IsReady);
    }

    [Fact]
    public void Show_OpensPaused_AndDoesNotPlay()
    {
        // 0.9.2 V9 (Paul: "only on click on the play button or spacebar"): opened, and paused so it opens, never played.
        var playback = Playback();

        playback.Show(@"C:\v\a.mp4");
        _players[0].RaiseOpened();

        Assert.Equal(["open", "pause"], _players[0].Calls);
        Assert.False(playback.IsPlaying);
        Assert.True(playback.IsReady);
        Assert.False(playback.ShowsFrame); // the still stays until it's played or moved along
        Assert.Equal("", playback.StatusLine);
        Assert.True(playback.SpaceToggles);
    }

    [Fact]
    public void Playing_ShowsItsOwnFrame()
    {
        var playback = Playback();
        playback.Show(@"C:\v\a.mp4");
        _players[0].RaiseOpened();

        playback.Toggle();

        Assert.True(playback.IsPlaying);
        Assert.Equal("play", _players[0].LastPlayOrPause);
        Assert.True(playback.ShowsFrame);
        playback.Toggle();                // paused again: its frame stays (it's where it was stopped)
        Assert.True(playback.ShowsFrame);
    }

    [Fact]
    public void PlayedBeforeItOpens_ItsFrameShows_OnceItHas()
    {
        var playback = Playback();
        playback.Show(@"C:\v\a.mp4");

        playback.Toggle();
        Assert.True(playback.IsPlaying);
        Assert.False(playback.ShowsFrame); // nothing to show yet

        _players[0].RaiseOpened();
        Assert.True(playback.ShowsFrame);
        Assert.True(playback.IsPlaying);
    }

    [Fact]
    public void BeforeItOpens_TheBarIsOff_AndTheTimeUnknown()
    {
        var playback = Playback();

        playback.Show(@"C:\v\a.mp4");

        Assert.False(playback.CanSeek);
        Assert.Equal("0:00 / –:––", playback.TimeText);
        playback.PositionSeconds = 10; // no seek before it has opened
        Assert.DoesNotContain(_players[0].Calls, c => c.StartsWith("seek:"));
        Assert.Equal(0, playback.PositionSeconds);
    }

    [Fact]
    public void Opened_SetsTheLength_AndTheTime()
    {
        var playback = Playback();
        playback.Show(@"C:\v\a.mp4");

        _players[0].RaiseOpened(seconds: 83);

        Assert.True(playback.CanSeek);
        Assert.Equal(83, playback.DurationSeconds);
        Assert.Equal(0, playback.PositionSeconds);
        Assert.Equal("0:00 / 1:23", playback.TimeText);
    }

    [Fact]
    public void Seek_MovesThePlayer_AndTheTime_AndShowsTheFrameThere()
    {
        var playback = Playback();
        playback.Show(@"C:\v\a.mp4");
        _players[0].RaiseOpened(seconds: 83);

        playback.PositionSeconds = 30; // as the seek bar sets it

        Assert.Equal(["seek:30", "pause"], _players[0].Calls.TakeLast(2)); // paused, it stays paused there
        Assert.Equal(TimeSpan.FromSeconds(30), _players[0].Position);
        Assert.Equal(30, playback.PositionSeconds);
        Assert.Equal("0:30 / 1:23", playback.TimeText);
        Assert.False(playback.IsPlaying); // a seek doesn't play
        Assert.True(playback.ShowsFrame); // a paused video shows the frame there
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(999, 83)]
    [InlineData(41.5, 41.5)]
    public void Seek_IsClamped(double asked, double at)
    {
        var playback = Playback();
        playback.Show(@"C:\v\a.mp4");
        _players[0].RaiseOpened(seconds: 83);

        playback.Seek(asked);

        Assert.Equal(at, playback.PositionSeconds);
        Assert.Equal(TimeSpan.FromSeconds(at), _players[0].Position);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Seek_ToNotANumber_DoesNothing(double asked)
    {
        var playback = Playback();
        playback.Show(@"C:\v\a.mp4");
        _players[0].RaiseOpened(seconds: 83);

        playback.Seek(asked);

        Assert.DoesNotContain(_players[0].Calls, c => c.StartsWith("seek:"));
        Assert.Equal(0, playback.PositionSeconds);
    }

    [Fact]
    public void Seek_AfterTheEnd_PlaysFromThere()
    {
        var playback = Playback();
        playback.Show(@"C:\v\a.mp4");
        _players[0].RaiseOpened(seconds: 83);
        playback.Toggle();
        _players[0].PlayTo(83);
        _players[0].RaiseEnded();
        Assert.Equal(83, playback.PositionSeconds); // the bar at the end

        playback.Seek(10);
        playback.Toggle();

        Assert.DoesNotContain("rewind", _players[0].Calls);
        Assert.Equal(["seek:10", "pause", "play"], _players[0].Calls.TakeLast(3));
    }

    [Fact]
    public void AfterTheEnd_ASeek_PausesThePlayerThere()
    {
        // 0.9.2 V12: a Manual MediaElement stays "playing" after its end, so a seek back would set it going; paused as
        // it ends, a seek back lands at 0:00 (review round 1, F1). So: a seek while not playing pauses it after the seek.
        var playback = Playback();
        playback.Show(@"C:\v\a.mp4");
        _players[0].RaiseOpened(seconds: 83);
        playback.Toggle();

        _players[0].RaiseEnded();
        Assert.Equal("play", _players[0].LastPlayOrPause); // not paused as it ends
        playback.Seek(20);

        Assert.False(playback.IsPlaying);
        Assert.Equal(["seek:20", "pause"], _players[0].Calls.TakeLast(2));
    }

    [Fact]
    public void ASeekWhilePlaying_DoesNotPause()
    {
        var playback = Playback();
        playback.Show(@"C:\v\a.mp4");
        _players[0].RaiseOpened(seconds: 83);
        playback.Toggle();

        playback.Seek(20);

        Assert.True(playback.IsPlaying);
        Assert.Equal(["play", "seek:20"], _players[0].Calls.TakeLast(2));
    }

    [Fact]
    public void Seek_ToTheEnd_ThenPlay_StartsAgain()
    {
        var playback = Playback();
        playback.Show(@"C:\v\a.mp4");
        _players[0].RaiseOpened(seconds: 83);

        playback.Seek(83);
        playback.Toggle();

        Assert.Equal(["rewind", "play"], _players[0].Calls.TakeLast(2));
        Assert.Equal(0, playback.PositionSeconds);
    }

    [Fact]
    public void Tick_FollowsThePlayer()
    {
        var playback = Playback();
        playback.Show(@"C:\v\a.mp4");
        _players[0].RaiseOpened(seconds: 83);
        playback.Toggle();
        var changed = new List<string?>();
        playback.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        _players[0].PlayTo(12.9);
        playback.Tick();

        Assert.Equal(12.9, playback.PositionSeconds, 3);
        Assert.Equal("0:12 / 1:23", playback.TimeText);
        Assert.Contains(nameof(VideoPlayback.PositionSeconds), changed);
        Assert.Contains(nameof(VideoPlayback.TimeText), changed);
        Assert.DoesNotContain(_players[0].Calls, c => c.StartsWith("seek:")); // following it isn't seeking it
    }

    [Fact]
    public void WhileTheBarIsDragged_ItFollowsTheDrag_NotThePlayer()
    {
        var playback = Playback();
        playback.Show(@"C:\v\a.mp4");
        _players[0].RaiseOpened(seconds: 83);
        playback.Toggle();

        playback.BeginScrub();
        playback.PositionSeconds = 40;   // the drag moves the video…
        _players[0].PlayTo(41);          // …which plays on a little
        playback.Tick();
        Assert.Equal(40, playback.PositionSeconds); // the bar stays under the pointer
        Assert.Equal("seek:40", _players[0].Calls.Last(c => c.StartsWith("seek:")));

        playback.EndScrub();
        playback.Tick();
        Assert.Equal(41, playback.PositionSeconds); // and follows the player again
    }

    [Fact]
    public void AnEndWhileTheBarIsDragged_LeavesItUnderThePointer()
    {
        var playback = Playback();
        playback.Show(@"C:\v\a.mp4");
        _players[0].RaiseOpened(seconds: 83);
        playback.Toggle();
        playback.BeginScrub();
        playback.PositionSeconds = 40;

        _players[0].RaiseEnded();

        Assert.Equal(40, playback.PositionSeconds);
        Assert.False(playback.IsPlaying);
    }

    [Fact]
    public void Tick_IsClampedToTheLength()
    {
        var playback = Playback();
        playback.Show(@"C:\v\a.mp4");
        _players[0].RaiseOpened(seconds: 83);
        playback.Toggle();

        _players[0].PlayTo(90); // a player that reports a little past its length
        playback.Tick();

        Assert.Equal(83, playback.PositionSeconds);
        Assert.Equal("1:23 / 1:23", playback.TimeText);
    }

    [Fact]
    public void ALengthItCantTell_LeavesTheBarOff_AndTheTimeAlone()
    {
        var playback = Playback();
        playback.Show(@"C:\v\a.mp4");
        _players[0].RaiseOpened(seconds: null);
        playback.Toggle();

        _players[0].PlayTo(5);
        playback.Tick();

        Assert.False(playback.CanSeek);
        Assert.Equal("0:05", playback.TimeText);
        playback.Seek(1);
        Assert.DoesNotContain(_players[0].Calls, c => c.StartsWith("seek:"));
    }

    [Fact]
    public void ALengthToldLate_IsTakenOnTheNextTick()
    {
        var playback = Playback();
        playback.Show(@"C:\v\a.mp4");
        _players[0].RaiseOpened(seconds: null);

        _players[0].Duration = TimeSpan.FromSeconds(60);
        playback.Tick();

        Assert.True(playback.CanSeek);
        Assert.Equal(60, playback.DurationSeconds);
        Assert.Equal("0:00 / 1:00", playback.TimeText);
    }

    [Fact]
    public void Leaving_ForgetsWhereItWas()
    {
        var playback = Playback();
        playback.Show(@"C:\v\a.mp4");
        _players[0].RaiseOpened(seconds: 83);
        playback.Seek(30);

        playback.Leave();

        Assert.False(playback.CanSeek);
        Assert.False(playback.ShowsFrame);
        Assert.Equal((0.0, 0.0), (playback.PositionSeconds, playback.DurationSeconds));
        Assert.Equal("", playback.TimeText);
        playback.Tick();       // nothing shown: harmless
        playback.Seek(5);
        playback.BeginScrub();
        playback.EndScrub();
        Assert.Equal(0, playback.PositionSeconds);
    }

    [Fact]
    public void AVideoThatFails_HasNoBar()
    {
        var playback = Playback();
        playback.Show(@"C:\v\a.mp4");
        _players[0].RaiseOpened(seconds: 83);

        _players[0].RaiseFailed();

        Assert.False(playback.CanSeek);
        Assert.False(playback.ShowsFrame);
        playback.Tick();
        playback.Seek(5);
        Assert.Equal(0, playback.PositionSeconds);
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
    public void Toggle_PlaysAndPauses()
    {
        var playback = Playback();
        playback.Show(@"C:\v\a.mp4");
        _players[0].RaiseOpened();

        playback.Toggle();
        Assert.True(playback.IsPlaying);
        Assert.Equal("play", _players[0].LastPlayOrPause);

        playback.ToggleCommand.Execute(null);
        Assert.False(playback.IsPlaying);
        Assert.Equal("pause", _players[0].LastPlayOrPause);
    }

    [Fact]
    public void BeforeItOpens_SpaceSetsWhetherItPlays()
    {
        var playback = Playback();
        playback.Show(@"C:\v\a.mp4");

        playback.Toggle();                // played before it has opened…
        playback.Toggle();                // …and paused again
        playback.Toggle();                // …and played
        _players[0].RaiseOpened(640, 360);

        Assert.True(playback.IsReady);
        Assert.True(playback.IsPlaying);  // …so it plays once it has
        Assert.Equal("play", _players[0].LastPlayOrPause);
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
        playback.Toggle(); // b plays

        _players[0].RaiseOpened();
        _players[0].RaiseEnded();
        _players[0].RaiseFailed();

        Assert.False(playback.IsReady);
        Assert.False(playback.CanSeek);
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
        playback.Toggle(); // playing

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
        playback.Seek(3);

        foreach (var name in new[]
                 {
                     nameof(VideoPlayback.IsVideoShown), nameof(VideoPlayback.IsReady), nameof(VideoPlayback.IsPlaying),
                     nameof(VideoPlayback.IsMuted), nameof(VideoPlayback.Volume), nameof(VideoPlayback.StatusLine),
                     nameof(VideoPlayback.ShowsFrame), nameof(VideoPlayback.CanSeek), nameof(VideoPlayback.DurationSeconds),
                     nameof(VideoPlayback.PositionSeconds), nameof(VideoPlayback.TimeText),
                 })
            Assert.Contains(name, changed);
    }
}
