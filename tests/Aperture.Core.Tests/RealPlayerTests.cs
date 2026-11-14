using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Aperture.App;
using Aperture.Core.Settings;
using Aperture.Core.Tests.Support;

namespace Aperture.Core.Tests;

/// <summary>Runs only with APERTURE_MEDIA_TESTS=1: it needs Windows' media playback, which CI's server image lacks.</summary>
public sealed class MediaFactAttribute : FactAttribute
{
    public MediaFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("APERTURE_MEDIA_TESTS") != "1")
            Skip = "Needs Windows' media playback: set APERTURE_MEDIA_TESTS=1 to run it (CI's image has none).";
    }
}

/// <summary>
/// Quick view's real player (<see cref="MediaElementPlayer"/>, a WPF MediaElement) on the repository's own sample clip,
/// one test per member of <see cref="IVideoPlayer"/> (0.9.1 plan, V1–V6). Opt-in: see <see cref="MediaFactAttribute"/>.
/// </summary>
public class RealPlayerTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private static string SampleClip()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Aperture.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return Path.Combine(dir!, "sample", "2024-06-12 17.12.00.mp4");
    }

    // A window off screen holding the player's host; the player on a copy of the sample (so the copy can be deleted).
    private static void WithPlayer(Action<MediaElementPlayer, Border, string, Func<Func<bool>, bool>> body, string? file = null)
    {
        Sta.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            using var dir = new TempDir();
            var copy = dir.Combine("clip.mp4");
            if (file is null)
                File.Copy(SampleClip(), copy);
            else
                File.WriteAllText(copy, file);
            var host = new Border();
            var window = new Window
            {
                Width = 400, Height = 300, Content = host, WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false,
            };
            window.Show();
            var player = new MediaElementPlayer(host) { IsMuted = true };
            try
            {
                body(player, host, copy, until => Sta.PumpUntil(until, Wait));
            }
            finally
            {
                player.Close();
                window.Close();
            }
        });
    }

    [MediaFact]
    public void Open_RaisesOpened_WithTheVideosSize()
    {
        WithPlayer((player, host, clip, pump) =>
        {
            var opened = false;
            player.Opened += (_, _) => opened = true;
            Assert.Null(player.NaturalSize);
            Assert.IsType<MediaElement>(host.Child); // it's in quick view's host

            player.Open(new Uri(clip));
            player.Play();

            Assert.True(pump(() => opened), "the sample clip didn't open");
            Assert.True(player.NaturalSize is { Width: > 0, Height: > 0 });
        });
    }

    [MediaFact]
    public void OpenThenPause_RaisesOpened_AndStaysPut()
    {
        // 0.9.2 V9: quick view opens a video paused (Open, then Pause): a Manual MediaElement opens on a Pause too.
        WithPlayer((player, host, clip, pump) =>
        {
            var opened = false;
            player.Opened += (_, _) => opened = true;

            player.Open(new Uri(clip));
            player.Pause();

            Assert.True(pump(() => opened), "the sample clip didn't open on a pause");
            Sta.PumpUntil(() => false, TimeSpan.FromMilliseconds(500));
            Assert.True(player.Position < TimeSpan.FromMilliseconds(100), $"it played on to {player.Position}"); // it didn't play
        });
    }

    [MediaFact]
    public void OpenThenPause_RaisesOpened_InACollapsedHost()
    {
        // As the window has it: quick view's video host stays collapsed until the video's frame shows.
        WithPlayer((player, host, clip, pump) =>
        {
            host.Visibility = Visibility.Collapsed;
            var opened = false;
            player.Opened += (_, _) => opened = true;

            player.Open(new Uri(clip));
            player.Pause();

            Assert.True(pump(() => opened), "the sample clip didn't open in a collapsed host");
            Assert.True(player.Duration is { } length && length > TimeSpan.Zero);
        });
    }

    [MediaFact]
    public void AfterTheEnd_ASeekHoldsThere_AndPlaysOnFromThere()
    {
        // 0.9.2 V10 + V12 through VideoPlayback, as quick view drives it (review round 1, F1: pausing the player as it
        // ended made a seek back land at 0:00). After the end: a seek holds where it was put, paused, and play goes on
        // from there — neither back at the start nor playing by itself.
        WithPlayer((player, host, clip, pump) =>
        {
            var playback = new VideoPlayback(PlaybackPrefsStore.InMemory(), () => player);
            playback.Show(clip);
            Assert.True(pump(() => playback.IsReady), "the sample clip didn't open");
            playback.Toggle();
            playback.Seek(playback.DurationSeconds - 0.3);
            Assert.True(pump(() => !playback.IsPlaying), "no end");

            playback.Seek(1);
            Sta.PumpUntil(() => false, TimeSpan.FromMilliseconds(700));
            playback.Tick();

            Assert.False(playback.IsPlaying);
            Assert.InRange(player.Position.TotalSeconds, 0.9, 1.1);
            Assert.InRange(playback.PositionSeconds, 0.9, 1.1); // the bar stays where it was put

            playback.Toggle();
            Sta.PumpUntil(() => false, TimeSpan.FromMilliseconds(700));
            Assert.True(player.Position > TimeSpan.FromSeconds(1.3), $"it didn't play on from the seek: {player.Position}");
            playback.Leave();
        });
    }

    [MediaFact]
    public void OpenedPaused_ASeek_HoldsThere_AndPlaysOnFromThere()
    {
        // Round 2, R2-1: quick view opens every video paused; a seek before it was ever played went back to 0:00 (a
        // second pause on a MediaElement opened by a pause rewinds it), and its first play started from 0:00.
        WithPlayer((player, host, clip, pump) =>
        {
            var playback = new VideoPlayback(PlaybackPrefsStore.InMemory(), () => player);
            playback.Show(clip);
            Assert.True(pump(() => playback.IsReady), "the sample clip didn't open");

            playback.Seek(2);
            Sta.PumpUntil(() => false, TimeSpan.FromMilliseconds(700));
            playback.Tick();

            Assert.InRange(player.Position.TotalSeconds, 1.9, 2.1);
            Assert.InRange(playback.PositionSeconds, 1.9, 2.1); // the bar stays where it was put
            playback.Toggle();
            Sta.PumpUntil(() => false, TimeSpan.FromMilliseconds(700));
            Assert.True(player.Position > TimeSpan.FromSeconds(2.3), $"it didn't play on from the seek: {player.Position}");
            playback.Leave();
        });
    }

    [MediaFact]
    public void OpenedPaused_TwoSeeks_HoldTheSecond()
    {
        WithPlayer((player, host, clip, pump) =>
        {
            var playback = new VideoPlayback(PlaybackPrefsStore.InMemory(), () => player);
            playback.Show(clip);
            Assert.True(pump(() => playback.IsReady), "the sample clip didn't open");

            playback.Seek(1);
            Sta.PumpUntil(() => false, TimeSpan.FromMilliseconds(300));
            playback.Seek(3);
            Sta.PumpUntil(() => false, TimeSpan.FromMilliseconds(700));

            Assert.InRange(player.Position.TotalSeconds, 2.9, 3.1);
            playback.Toggle();
            Sta.PumpUntil(() => false, TimeSpan.FromMilliseconds(700));
            Assert.True(player.Position > TimeSpan.FromSeconds(3.3), $"it didn't play on from the seek: {player.Position}");
            playback.Leave();
        });
    }

    [MediaFact]
    public void OpenedPaused_ADrag_HoldsWhereItEnds()
    {
        // A drag on the bar: many seeks in a row, the window's clock ticking, then let go.
        WithPlayer((player, host, clip, pump) =>
        {
            var playback = new VideoPlayback(PlaybackPrefsStore.InMemory(), () => player);
            playback.Show(clip);
            Assert.True(pump(() => playback.IsReady), "the sample clip didn't open");

            playback.BeginScrub();
            for (var at = 0.5; at <= 3.0; at += 0.25)
            {
                playback.PositionSeconds = at;
                Sta.PumpUntil(() => false, TimeSpan.FromMilliseconds(40));
                playback.Tick();
            }
            playback.EndScrub();
            Sta.PumpUntil(() => false, TimeSpan.FromMilliseconds(700));
            playback.Tick();

            Assert.InRange(player.Position.TotalSeconds, 2.9, 3.1);
            Assert.InRange(playback.PositionSeconds, 2.9, 3.1);
            playback.Leave();
        });
    }

    [MediaFact]
    public void OpenedPaused_ASeekToTheEnd_ThenBack_HoldsThere()
    {
        WithPlayer((player, host, clip, pump) =>
        {
            var playback = new VideoPlayback(PlaybackPrefsStore.InMemory(), () => player);
            playback.Show(clip);
            Assert.True(pump(() => playback.IsReady), "the sample clip didn't open");

            playback.Seek(playback.DurationSeconds);
            Sta.PumpUntil(() => false, TimeSpan.FromMilliseconds(300));
            playback.Seek(2);
            Sta.PumpUntil(() => false, TimeSpan.FromMilliseconds(700));
            playback.Tick();

            Assert.InRange(player.Position.TotalSeconds, 1.9, 2.1);
            Assert.False(playback.IsPlaying);
            playback.Toggle();
            Sta.PumpUntil(() => false, TimeSpan.FromMilliseconds(700));
            Assert.True(player.Position > TimeSpan.FromSeconds(2.3), $"it didn't play on from the seek: {player.Position}");
            playback.Leave();
        });
    }

    [MediaFact]
    public void AfterTheEnd_ASeekToTheEnd_ThenBack_HoldsThere()
    {
        // Round 2 (dispositions lens), P2: after the end, the bar pulled to its very end and back.
        WithPlayer((player, host, clip, pump) =>
        {
            var playback = new VideoPlayback(PlaybackPrefsStore.InMemory(), () => player);
            playback.Show(clip);
            Assert.True(pump(() => playback.IsReady), "the sample clip didn't open");
            playback.Toggle();
            playback.Seek(playback.DurationSeconds - 0.3);
            Assert.True(pump(() => !playback.IsPlaying), "no end");

            playback.Seek(playback.DurationSeconds);
            Sta.PumpUntil(() => false, TimeSpan.FromMilliseconds(300));
            playback.Seek(2);
            Sta.PumpUntil(() => false, TimeSpan.FromMilliseconds(700));
            playback.Tick();

            Assert.InRange(player.Position.TotalSeconds, 1.9, 2.1);
            Assert.InRange(playback.PositionSeconds, 1.9, 2.1);
            playback.Toggle();
            Sta.PumpUntil(() => false, TimeSpan.FromMilliseconds(700));
            Assert.True(player.Position > TimeSpan.FromSeconds(2.3), $"it didn't play on from the seek: {player.Position}");
            playback.Leave();
        });
    }

    [MediaFact]
    public void PlayStraightAfterASeek_PlaysOn()
    {
        // The pause a seek defers (MediaElementPlayer.Pause) is cancelled by a play straight after it.
        WithPlayer((player, host, clip, pump) =>
        {
            var playback = new VideoPlayback(PlaybackPrefsStore.InMemory(), () => player);
            playback.Show(clip);
            Assert.True(pump(() => playback.IsReady), "the sample clip didn't open");
            playback.Toggle();
            playback.Seek(playback.DurationSeconds - 0.3);
            Assert.True(pump(() => !playback.IsPlaying), "no end");

            playback.Seek(1);
            playback.Toggle(); // at once, before the seek has gone through
            Sta.PumpUntil(() => false, TimeSpan.FromMilliseconds(700));

            Assert.True(playback.IsPlaying);
            Assert.True(player.Position > TimeSpan.FromSeconds(1.3), $"a deferred pause stopped it: {player.Position}");
            playback.Leave();
        });
    }

    [MediaFact]
    public void Position_AndDuration_AreTheVideos()
    {
        // 0.9.2 V10: the seek bar's length and where the video is; a seek while paused moves it there.
        WithPlayer((player, host, clip, pump) =>
        {
            var opened = false;
            player.Opened += (_, _) => opened = true;
            Assert.Null(player.Duration);
            player.Open(new Uri(clip));
            player.Pause();
            Assert.True(pump(() => opened));

            Assert.True(player.Duration is { } length && length > TimeSpan.FromSeconds(1), $"length {player.Duration}");
            player.Position = TimeSpan.FromSeconds(1);
            Assert.True(pump(() => Math.Abs((player.Position - TimeSpan.FromSeconds(1)).TotalMilliseconds) < 50));
            Assert.True(((MediaElement)host.Child).ScrubbingEnabled); // a paused seek shows its frame
        });
    }

    [MediaFact]
    public void PlayPauseAndRewind_DriveIt()
    {
        WithPlayer((player, host, clip, pump) =>
        {
            var opened = false;
            player.Opened += (_, _) => opened = true;
            player.Open(new Uri(clip));
            player.Play();
            Assert.True(pump(() => opened));
            var element = (MediaElement)host.Child;

            Assert.True(pump(() => element.Position > TimeSpan.FromMilliseconds(200)), "it didn't play");
            player.Pause();
            Sta.PumpUntil(() => false, TimeSpan.FromMilliseconds(300));
            var paused = element.Position;
            Sta.PumpUntil(() => false, TimeSpan.FromMilliseconds(500));
            Assert.True(element.Position - paused < TimeSpan.FromMilliseconds(100), "it went on after a pause");
            player.Rewind();
            Assert.True(element.Position < TimeSpan.FromMilliseconds(100));
        });
    }

    [MediaFact]
    public void ItsSound_IsSetThrough()
    {
        WithPlayer((player, host, _, _) =>
        {
            var element = (MediaElement)host.Child;

            player.IsMuted = false;
            player.Volume = 0.3;
            Assert.Equal((false, 0.3), (element.IsMuted, element.Volume));
            player.IsMuted = true;
            Assert.True(player.IsMuted);
            Assert.Equal(0.3, player.Volume, 3);
        });
    }

    [MediaFact]
    public void AtTheEnd_ItRaisesEnded()
    {
        WithPlayer((player, host, clip, pump) =>
        {
            var opened = false;
            var ended = false;
            player.Opened += (_, _) => opened = true;
            player.Ended += (_, _) => ended = true;
            player.Open(new Uri(clip));
            player.Play();
            Assert.True(pump(() => opened));
            var element = (MediaElement)host.Child;
            if (element.NaturalDuration.HasTimeSpan)
                element.Position = element.NaturalDuration.TimeSpan - TimeSpan.FromMilliseconds(300); // near the end

            Assert.True(pump(() => ended), "no end");
        });
    }

    [MediaFact]
    public void AFileThatIsntAVideo_RaisesFailed()
    {
        WithPlayer((player, _, clip, pump) =>
        {
            var failed = false;
            player.Failed += (_, _) => failed = true;

            player.Open(new Uri(clip));
            player.Play();

            Assert.True(pump(() => failed), "a file that isn't a video didn't fail");
        }, file: "not really a video");
    }

    [MediaFact]
    public void AClosedVideo_CanBeDeleted_AndLeavesTheHost()
    {
        WithPlayer((player, host, clip, pump) =>
        {
            var opened = false;
            player.Opened += (_, _) => opened = true;
            player.Open(new Uri(clip));
            player.Play();
            Assert.True(pump(() => opened));

            player.Close();
            Sta.PumpUntil(() => false, TimeSpan.FromMilliseconds(300));

            Assert.Null(host.Child);
            File.Delete(clip); // V6: renamed or deleted at once
            Assert.False(File.Exists(clip));
        });
    }
}
