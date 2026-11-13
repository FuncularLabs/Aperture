using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Aperture.App;
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
