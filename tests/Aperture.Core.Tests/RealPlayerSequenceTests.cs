using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Aperture.App;
using Aperture.Core.Settings;
using Aperture.Core.Tests.Support;

namespace Aperture.Core.Tests;

/// <summary>
/// Quick view's playback on the real player (opt-in, <see cref="MediaFactAttribute"/>), driven through
/// <see cref="VideoPlayback"/> as the window drives it — seeks queued at Input priority, as a drag's mouse moves come,
/// with nothing pumped between them (0.9.2 review round 3: three rounds of fixes each broke a sequence the tests before
/// them didn't play). Besides the named sequences, seeded random ones check one rule after every sequence: what the
/// window shows is what the player does — shown paused, it holds still where the bar says; shown playing, it moves —
/// and a seek then holds, and play goes on from it.
/// </summary>
public class RealPlayerSequenceTests
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

    private static void Pump(int ms) => Sta.PumpUntil(() => false, TimeSpan.FromMilliseconds(ms));

    // The player in a collapsed host (as the window's is until the video's frame shows), opened by VideoPlayback.Show.
    private static void WithPlayback(Action<VideoPlayback, MediaElementPlayer> body)
    {
        Sta.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            using var dir = new TempDir();
            var copy = dir.Combine("clip.mp4");
            File.Copy(SampleClip(), copy);
            var host = new Border { Visibility = Visibility.Collapsed };
            var window = new Window
            {
                Width = 400, Height = 300, Content = host, WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false,
            };
            window.Show();
            var player = new MediaElementPlayer(host) { IsMuted = true };
            var playback = new VideoPlayback(PlaybackPrefsStore.InMemory(), () => player);
            try
            {
                playback.Show(copy);
                Assert.True(Sta.PumpUntil(() => playback.IsReady, Wait), "the sample clip didn't open");
                body(playback, player);
            }
            finally
            {
                playback.Leave();
                window.Close();
            }
        });
    }

    private static void ToTheEnd(VideoPlayback playback)
    {
        if (!playback.IsPlaying)
            playback.Toggle();
        playback.Seek(playback.DurationSeconds - 0.3);
        Assert.True(Sta.PumpUntil(() => !playback.IsPlaying, Wait), "no end");
    }

    // A drag on the bar as the window gets it: the thumb's drag start, a mouse move per step, the drag's end, all queued
    // at Input priority — nothing at Background priority runs between them.
    private static void DragAsInput(VideoPlayback playback, double from, double to, double step, Action? midway = null)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        dispatcher.BeginInvoke(playback.BeginScrub, DispatcherPriority.Input);
        var steps = 0;
        for (var at = from; step > 0 ? at <= to + 1e-9 : at >= to - 1e-9; at += step)
        {
            var x = at;
            dispatcher.BeginInvoke(() => playback.PositionSeconds = x, DispatcherPriority.Input);
            if (++steps == 3 && midway is not null)
                dispatcher.BeginInvoke(midway, DispatcherPriority.Input);
        }
        dispatcher.BeginInvoke(playback.EndScrub, DispatcherPriority.Input);
    }

    // What's shown is what the player does, and a seek then holds and plays on from there.
    private static void AssertTheShownStateIsTrue(VideoPlayback playback, MediaElementPlayer player, string what)
    {
        Pump(900);
        playback.Tick();
        if (!playback.IsPlaying)
        {
            var at = player.Position;
            Pump(600);
            Assert.True(Math.Abs((player.Position - at).TotalSeconds) < 0.1,
                $"{what}: shown paused, it moved {at} → {player.Position}");
            Assert.True(Math.Abs(player.Position.TotalSeconds - playback.PositionSeconds) < 0.25,
                $"{what}: the bar says {playback.PositionSeconds:F2}, the player is at {player.Position}");
        }
        else
        {
            var at = player.Position;
            Pump(600);
            Assert.True(!playback.IsPlaying || player.Position - at > TimeSpan.FromSeconds(0.3),
                $"{what}: shown playing, it stayed at {at}");
        }

        playback.Seek(2);
        Pump(700);
        playback.Tick();
        if (!playback.IsPlaying)
        {
            Assert.InRange(player.Position.TotalSeconds, 1.85, 2.15);
            playback.Toggle();
        }
        Pump(700);
        Assert.True(player.Position > TimeSpan.FromSeconds(2.3) || !playback.IsPlaying,
            $"{what}: it didn't play on from a seek to 2 s ({player.Position})");
    }

    [MediaFact]
    public void OpenedPaused_ASeekToTheVeryEnd_ThenPlay_PlaysFromTheStart()
    {
        // Round 3, M-1: the seek made before the first play was set again by that play — the end, so nothing played.
        WithPlayback((playback, player) =>
        {
            playback.Seek(playback.DurationSeconds);
            Pump(500);

            playback.Toggle();
            Pump(800);

            Assert.True(playback.IsPlaying, "a play at the end didn't play again from the start");
            Assert.InRange(player.Position.TotalSeconds, 0.2, 1.6);
        });
    }

    [MediaFact]
    public void AfterTheEnd_TwoSeeksInOneTurn_HoldTheSecond()
    {
        // Round 3, H-1: the second seek cancelled the first's deferred pause and was then skipped as already paused.
        WithPlayback((playback, player) =>
        {
            ToTheEnd(playback);

            playback.Seek(1);
            playback.Seek(2);
            Pump(1200);
            playback.Tick();

            Assert.False(playback.IsPlaying);
            Assert.InRange(player.Position.TotalSeconds, 1.9, 2.1);
        });
    }

    [MediaFact]
    public void Playing_ASeek_APause_ASeek_InOneTurn_StaysPaused()
    {
        WithPlayback((playback, player) =>
        {
            playback.Toggle();
            Pump(500);

            playback.Seek(3);
            playback.Toggle();
            playback.Seek(1);
            Pump(1200);
            playback.Tick();

            Assert.False(playback.IsPlaying);
            Assert.InRange(player.Position.TotalSeconds, 0.9, 1.1);
        });
    }

    [MediaFact]
    public void AfterTheEnd_ADragAsTheWindowGetsIt_HoldsWhereItEnds()
    {
        WithPlayback((playback, player) =>
        {
            ToTheEnd(playback);

            DragAsInput(playback, 5.5, 2.0, -0.25);
            Pump(1200);
            playback.Tick();

            Assert.False(playback.IsPlaying);
            Assert.InRange(player.Position.TotalSeconds, 1.9, 2.1);
        });
    }

    [MediaFact]
    public void ADragWhilePlaying_WithSpaceMidway_EndsPausedWhereItEnds()
    {
        WithPlayback((playback, player) =>
        {
            playback.Toggle();
            Pump(400);

            DragAsInput(playback, 1.0, 2.5, 0.5, midway: playback.Toggle);
            Pump(1200);
            playback.Tick();

            Assert.False(playback.IsPlaying);
            Assert.InRange(player.Position.TotalSeconds, 2.4, 2.6);
        });
    }

    [MediaFact]
    public void OpenedPaused_ADragAsTheWindowGetsIt_HoldsWhereItEnds()
    {
        WithPlayback((playback, player) =>
        {
            DragAsInput(playback, 0.5, 3.0, 0.25);
            Pump(1200);
            playback.Tick();

            Assert.False(playback.IsPlaying);
            Assert.InRange(player.Position.TotalSeconds, 2.9, 3.1);
            playback.Toggle();
            Pump(700);
            Assert.True(player.Position > TimeSpan.FromSeconds(3.3), $"it didn't play on from the drag: {player.Position}");
        });
    }

    [MediaTheory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(25)] // 25 and 39 find round 3's H-1 (a pause counted as it's posted) every time, 36 now and then: round 4, N-3
    [InlineData(36)]
    [InlineData(39)]
    public void ASequenceOfPlaysPausesSeeksAndDrags_LeavesTheShownStateTrue(int seed)
    {
        WithPlayback((playback, player) =>
        {
            var random = new Random(seed);
            var steps = new List<string>();
            for (var i = 0; i < 10; i++)
            {
                var length = playback.DurationSeconds;
                switch (random.Next(7))
                {
                    case 0:
                    case 1:
                        steps.Add("space");
                        playback.Toggle();
                        break;
                    case 2:
                        var at = Math.Round(random.NextDouble() * length, 2);
                        steps.Add($"seek {at}");
                        playback.Seek(at);
                        break;
                    case 3:
                        var from = Math.Round(random.NextDouble() * length, 2);
                        var to = Math.Round(random.NextDouble() * length, 2);
                        steps.Add($"drag {from}→{to}");
                        DragAsInput(playback, from, to, from <= to ? 0.3 : -0.3);
                        break;
                    case 4:
                        steps.Add("seek to the end");
                        playback.Seek(length);
                        break;
                    case 5:
                        steps.Add("play to the end");
                        ToTheEnd(playback);
                        break;
                    default:
                        steps.Add("seek, seek");
                        playback.Seek(Math.Round(random.NextDouble() * length, 2));
                        playback.Seek(Math.Round(random.NextDouble() * length, 2));
                        break;
                }
                Pump(new[] { 0, 0, 60, 300 }[random.Next(4)]);
            }

            AssertTheShownStateIsTrue(playback, player, $"seed {seed}: {string.Join(", ", steps)}");
        });
    }
}

/// <summary>A theory that runs only with APERTURE_MEDIA_TESTS=1 (see <see cref="MediaFactAttribute"/>).</summary>
public sealed class MediaTheoryAttribute : TheoryAttribute
{
    public MediaTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("APERTURE_MEDIA_TESTS") != "1")
            Skip = "Needs Windows' media playback: set APERTURE_MEDIA_TESTS=1 to run it (CI's image has none).";
    }
}
