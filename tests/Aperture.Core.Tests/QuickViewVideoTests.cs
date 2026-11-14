using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Aperture.App;
using Aperture.App.Services;
using Aperture.App.ViewModels;
using Aperture.Core.Library;
using Aperture.Core.Settings;
using Aperture.Core.Tests.Support;
using Path = System.IO.Path;

namespace Aperture.Core.Tests;

/// <summary>
/// Videos in quick view, in the real window shown off screen (0.9.1 batch, items 1–2: "a click on the video or a
/// spacebar press should toggle play and pause"; "Add mute/unmute and a volume control"; 0.9.2 V9–V11: a video opens
/// paused, "only on click on the play button or spacebar", with a seek bar and a time). The window's players are
/// fakes that play nothing (CI has no media player); each test drives the window's entry points as its handlers do.
/// </summary>
public class QuickViewVideoTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private sealed class View
    {
        public required MainViewModel Vm { get; init; }
        public required MainWindow Window { get; init; }
        public required List<FakeVideoPlayer> Players { get; init; }
        public required List<string> Opened { get; init; }
        public required LibraryService Library { get; init; }
        public required string Folder { get; init; }
        public FakeVideoPlayer Player => Players[^1];
        public FrameworkElement Area => (FrameworkElement)Window.FindName("QuickLookImageArea");
        public Image Still => (Image)Window.FindName("QuickLookImg");
        public FrameworkElement VideoHost => (FrameworkElement)Window.FindName("QuickLookVideoHost");
        public FrameworkElement Chrome => (FrameworkElement)Window.FindName("QuickLookChrome");
        public Slider Seek => (Slider)Window.FindName("QuickLookSeek");
        public Slider VolumeSlider => (Slider)Window.FindName("QuickLookVolume");
        public TextBlock Time => (TextBlock)Window.FindName("QuickLookTime");
        public Point Middle => new(Area.ActualWidth / 2, Area.ActualHeight / 2);
        public string Path(string name) => System.IO.Path.Combine(Folder, name);
    }

    // A library of the named items, newest first; a window over it off screen; quick view open on the first.
    // still: what a video's still frame decodes to (null: none, as on CI where the shell makes no video thumbnails).
    private static void WithQuickView(
        string[] items, Action<View> body, Func<BitmapSource?>? still = null, Func<IVideoPlayer>? factory = null,
        string? prefsPath = null)
    {
        Sta.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            using var data = new TempDir();
            using var folder = new TempDir();
            for (var i = 0; i < items.Length; i++)
            {
                var path = Path.Combine(folder.Path, items[i]);
                if (items[i].EndsWith(".jpg"))
                    TestImages.Write(path, 64, 48);
                else
                    File.WriteAllText(path, "not really a video"); // only its name says it's one
                File.SetLastWriteTimeUtc(path, new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc).AddDays(-i));
            }
            var library = new LibraryService(data.Path);
            library.IndexRoot(library.AddRoot(folder.Path, "Clips"));
            var vm = new MainViewModel(library, new ThumbnailService(library));
            vm.DecodeQuickLookVideo = (_, _, _) => still?.Invoke();
            var players = new List<FakeVideoPlayer>();
            var opened = new List<string>();
            var window = new MainWindow
            {
                Width = 1000, Height = 700, DataContext = vm,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000,
                ShowActivated = false, ShowInTaskbar = false,
                OpenViewed = v => opened.Add(v.QuickLookPath ?? "?"),
                VideoPlayerFactory = factory ?? (() =>
                {
                    var player = new FakeVideoPlayer();
                    players.Add(player);
                    return player;
                }),
            };
            window.PlaybackPrefs = prefsPath is null ? PlaybackPrefsStore.InMemory() : new PlaybackPrefsStore(prefsPath);
            var closed = false;
            window.Closed += (_, _) => closed = true;
            try
            {
                window.Show();
                Assert.Equal((1000.0, 700.0), (window.ActualWidth, window.ActualHeight));
                Assert.True(Sta.PumpUntil(() => vm.TotalItemCount == items.Length && !vm.IsLoadingView, Wait));
                vm.OpenQuickLookCommand.Execute(null);
                Settle();
                body(new View
                {
                    Vm = vm, Window = window, Players = players, Opened = opened, Library = library, Folder = folder.Path,
                });
            }
            finally
            {
                if (!closed)
                    window.Close();
                vm.PrepareForClose();
                library.ShutdownAsync().GetAwaiter().GetResult();
            }
        });
    }

    private static void Settle() => Sta.PumpUntil(() => false, TimeSpan.FromMilliseconds(300));

    private static BitmapSource Still16By9()
    {
        var bitmap = BitmapSource.Create(160, 90, 96, 96, PixelFormats.Bgra32, null, new byte[160 * 90 * 4], 160 * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            yield return child;
            foreach (var deeper in Descendants(child))
                yield return deeper;
        }
    }

    private static List<string> VisibleTexts(DependencyObject node) =>
        [.. Descendants(node).OfType<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text)];

    [Fact]
    public void OpeningAVideo_ShowsItPaused()
    {
        WithQuickView(["v0.mp4", "p1.jpg"], v =>
        {
            var player = Assert.Single(v.Players);
            Assert.Equal(new Uri(v.Path("v0.mp4")), player.Source);
            Assert.Equal("pause", player.LastPlayOrPause);
            Assert.DoesNotContain("play", player.Calls); // V9: only the play button, Space or a click plays it
            Assert.True(player.IsMuted); // the first video ever: muted
            Assert.False(v.Window.Playback.IsPlaying);
        });
    }

    [Fact]
    public void TheStill_ShowsUntilTheVideoIsPlayed()
    {
        WithQuickView(["v0.mp4"], v =>
        {
            Assert.True(Sta.PumpUntil(() => v.Vm.QuickLookImage is not null, Wait));
            Assert.Equal(Visibility.Visible, v.Still.Visibility);
            Assert.NotEqual(Visibility.Visible, v.VideoHost.Visibility);
            Assert.Contains("Loading video…", VisibleTexts(v.Chrome));

            v.Player.RaiseOpened(1280, 720);
            Settle();

            Assert.Equal(Visibility.Visible, v.Still.Visibility);           // opened, paused: still the still
            Assert.NotEqual(Visibility.Visible, v.VideoHost.Visibility);
            Assert.DoesNotContain("Loading video…", VisibleTexts(v.Chrome));

            Assert.True(v.Window.QuickLookKey(Key.Space, ModifierKeys.None));
            Settle();

            Assert.NotEqual(Visibility.Visible, v.Still.Visibility);        // played: its own frame
            Assert.Equal(Visibility.Visible, v.VideoHost.Visibility);
        }, still: Still16By9);
    }

    [Fact]
    public void TheSeekBar_AndTime_FollowThePlayback()
    {
        WithQuickView(["v0.mp4"], v =>
        {
            Assert.False(v.Seek.IsEnabled);                 // V10: until it has opened
            Assert.Equal("0:00 / –:––", v.Time.Text);
            Assert.False(v.Seek.Focusable);                 // V11: no keys of its own

            v.Player.RaiseOpened(seconds: 83);
            Settle();
            Assert.True(v.Seek.IsEnabled);
            Assert.True(v.Seek.IsVisible);
            Assert.Equal((0.0, 83.0), (v.Seek.Minimum, v.Seek.Maximum));
            Assert.Equal("0:00 / 1:23", v.Time.Text);

            v.Seek.Value = 30;                               // a click or a drag on the bar
            Assert.Equal(TimeSpan.FromSeconds(30), v.Player.Position);
            Assert.Equal("0:30 / 1:23", v.Time.Text);

            v.Window.QuickLookKey(Key.Space, ModifierKeys.None); // plays
            v.Player.PlayTo(45);
            Assert.True(Sta.PumpUntil(() => v.Seek.Value == 45, Wait)); // the window's timer follows it
            Assert.Equal("0:45 / 1:23", v.Time.Text);
        });
    }

    [Fact]
    public void DraggingTheSeekBar_HoldsItUnderThePointer_ThenItFollowsThePlayerAgain()
    {
        // Round 1 (lens 2, finding 6): the window wires the thumb's drag to the scrub.
        WithQuickView(["v0.mp4"], v =>
        {
            v.Player.RaiseOpened(seconds: 83);
            v.Window.QuickLookKey(Key.Space, ModifierKeys.None);
            Settle();
            v.Seek.Value = 20;

            v.Seek.RaiseEvent(new DragStartedEventArgs(0, 0));
            v.Player.PlayTo(50);
            Settle(); // the clock ticks meanwhile
            Assert.Equal(20, v.Seek.Value);

            v.Seek.RaiseEvent(new DragCompletedEventArgs(0, 0, canceled: false));
            Assert.True(Sta.PumpUntil(() => v.Seek.Value == 50, Wait));
        });
    }

    [Fact]
    public void AVideoWithoutAStill_ShowsItsOwnFrame_OnceOpened()
    {
        // Round 1 (lens 2, finding 16): with no still to show (one the shell couldn't make), the video's first frame
        // shows once it has opened, as in 0.9.1, rather than nothing until it's played.
        WithQuickView(["v0.mp4"], v =>
        {
            Assert.Null(v.Vm.QuickLookImage);
            Assert.NotEqual(Visibility.Visible, v.VideoHost.Visibility);

            v.Player.RaiseOpened(1280, 720);
            Settle();

            Assert.Equal(Visibility.Visible, v.VideoHost.Visibility);
            Assert.Equal("pause", v.Player.LastPlayOrPause); // still paused
        });
    }

    [Fact]
    public void AStillArrivingAfterTheFrameShows_TakesItsPlace_UntilPlayed()
    {
        // Round 2 (dispositions lens), L-2a: the frame shown for want of a still gives way once the still comes.
        var release = new ManualResetEventSlim();
        WithQuickView(["v0.mp4"], v =>
        {
            v.Player.RaiseOpened(1280, 720);
            Settle();
            Assert.Equal(Visibility.Visible, v.VideoHost.Visibility); // no still yet: its own frame

            release.Set();
            Assert.True(Sta.PumpUntil(() => v.Vm.QuickLookImage is not null, Wait));
            Settle();
            Assert.Equal(Visibility.Visible, v.Still.Visibility);
            Assert.NotEqual(Visibility.Visible, v.VideoHost.Visibility);

            v.Window.QuickLookKey(Key.Space, ModifierKeys.None);
            Settle();
            Assert.Equal(Visibility.Visible, v.VideoHost.Visibility);
        }, still: () =>
        {
            release.Wait(Wait);
            return Still16By9();
        });
    }

    [Fact]
    public void ArrowKeys_StillMove_WithTheSeekBarShown()
    {
        WithQuickView(["v0.mp4", "p1.jpg"], v =>
        {
            v.Player.RaiseOpened(seconds: 83);
            Settle();
            Assert.True(v.Seek.IsVisible);

            Assert.True(v.Window.QuickLookKey(Key.Right, ModifierKeys.None));

            Assert.EndsWith("p1.jpg", v.Vm.QuickLookPath);
            Assert.False(v.Seek.IsVisible);                 // a photo has no bar
        });
    }

    [Fact]
    public void AVideoThatCannotPlay_KeepsTheStillAndSaysSo()
    {
        WithQuickView(["v0.mp4"], v =>
        {
            Assert.True(Sta.PumpUntil(() => v.Vm.QuickLookImage is not null, Wait));

            v.Player.RaiseFailed();
            Settle();

            Assert.True(v.Player.IsClosed);
            Assert.Equal(Visibility.Visible, v.Still.Visibility);
            Assert.Contains(VisibleTexts(v.Chrome), t => t.Contains("can't be played here") && t.Contains("Enter"));
        }, still: Still16By9);
    }

    [Fact]
    public void Space_PlaysAndPauses_AVideo()
    {
        WithQuickView(["v0.mp4"], v =>
        {
            Assert.True(v.Window.QuickLookKey(Key.Space, ModifierKeys.None));
            Assert.Equal("play", v.Player.LastPlayOrPause);
            Assert.True(v.Vm.QuickLookOpen);

            Assert.True(v.Window.QuickLookKey(Key.Space, ModifierKeys.None));
            Assert.Equal("pause", v.Player.LastPlayOrPause);
            Assert.True(v.Vm.QuickLookOpen);
        });
    }

    [Fact]
    public void Space_StillClosesAPhoto()
    {
        WithQuickView(["v0.mp4", "p1.jpg"], v =>
        {
            v.Window.QuickLookKey(Key.Right, ModifierKeys.None); // to the photo

            Assert.True(v.Window.QuickLookKey(Key.Space, ModifierKeys.None));

            Assert.False(v.Vm.QuickLookOpen);
        });
    }

    [Fact]
    public void Space_ClosesAVideoThatCannotPlay()
    {
        WithQuickView(["v0.mp4"], v =>
        {
            v.Player.RaiseFailed();

            Assert.True(v.Window.QuickLookKey(Key.Space, ModifierKeys.None));

            Assert.False(v.Vm.QuickLookOpen);
        });
    }

    [Fact]
    public void AClick_PlaysAndPauses()
    {
        WithQuickView(["v0.mp4"], v =>
        {
            Assert.True(Sta.PumpUntil(() => v.Vm.QuickLookImage is not null, Wait));
            Settle();

            v.Window.QuickLookClick(v.Middle, clickCount: 1);
            Assert.Equal("play", v.Player.LastPlayOrPause);
            v.Window.QuickLookClick(v.Middle, clickCount: 1);
            Assert.Equal("pause", v.Player.LastPlayOrPause);
            Assert.True(v.Vm.QuickLookOpen);
        }, still: Still16By9);
    }

    [Fact]
    public void AClickOnAVideoWithoutAStill_PlaysAndPauses()
    {
        WithQuickView(["v0.mp4"], v =>
        {
            Assert.Null(v.Vm.QuickLookImage);

            v.Window.QuickLookClick(new Point(3, 3), clickCount: 1); // anywhere: there's nothing beside it yet

            Assert.True(v.Vm.QuickLookOpen);
            Assert.Equal("play", v.Player.LastPlayOrPause);
        });
    }

    [Fact]
    public void APressBesideTheVideo_Closes()
    {
        WithQuickView(["v0.mp4"], v =>
        {
            Assert.True(Sta.PumpUntil(() => v.Vm.QuickLookImage is not null, Wait));
            Settle();

            v.Window.QuickLookClick(new Point(v.Area.ActualWidth / 2, 3), clickCount: 1); // above a 16:9 picture

            Assert.False(v.Vm.QuickLookOpen);
            Assert.True(v.Player.IsClosed);
        }, still: Still16By9);
    }

    [Fact]
    public void TheVideosOwnFrame_IsTheVideo_OnceItShows()
    {
        // Review R1-B6: with no still, the whole area is the video until its own (16:9) frame shows; then that is. With
        // no still, its frame shows once it has opened (round 1, lens 2, finding 16).
        WithQuickView(["v0.mp4"], v =>
        {
            v.Window.QuickLookClick(new Point(v.Area.ActualWidth / 2, 3), clickCount: 1); // no frame yet: plays
            Assert.Equal("play", v.Player.LastPlayOrPause);
            Assert.True(v.Vm.QuickLookOpen);
            v.Player.RaiseOpened(1920, 1080);
            Settle();

            v.Window.QuickLookClick(v.Middle, clickCount: 1);
            Assert.Equal("pause", v.Player.LastPlayOrPause);
            v.Window.QuickLookClick(new Point(v.Area.ActualWidth / 2, 3), clickCount: 1); // above its frame
            Assert.False(v.Vm.QuickLookOpen);
        });
    }

    [Fact]
    public void ClosingTheWindow_ClosesTheVideo()
    {
        WithQuickView(["v0.mp4"], v =>
        {
            v.Window.Close();

            Assert.True(v.Player.IsClosed);
        });
    }

    [Fact]
    public void Enter_OpensTheVideoAndPausesIt()
    {
        WithQuickView(["v0.mp4"], v =>
        {
            v.Window.QuickLookKey(Key.Space, ModifierKeys.None); // playing

            Assert.True(v.Window.QuickLookKey(Key.Enter, ModifierKeys.None));

            Assert.Equal([v.Path("v0.mp4")], v.Opened);
            Assert.Equal("pause", v.Player.LastPlayOrPause);
        });
    }

    [Fact]
    public void ADoubleClick_OpensAndPauses()
    {
        WithQuickView(["v0.mp4"], v =>
        {
            // As the mouse reports it: a first click, then the second of a double-click.
            v.Window.QuickLookClick(v.Middle, clickCount: 1);
            v.Window.QuickLookClick(v.Middle, clickCount: 2);

            Assert.Equal([v.Path("v0.mp4")], v.Opened);
            Assert.Equal("pause", v.Player.LastPlayOrPause);
            Assert.True(v.Vm.QuickLookOpen);
        });
    }

    [Fact]
    public void ADoubleClick_OnAPausedVideo_LeavesItPaused()
    {
        WithQuickView(["v0.mp4"], v =>
        {
            // It opens paused (V9).
            v.Window.QuickLookClick(v.Middle, clickCount: 1);    // plays…
            v.Window.QuickLookClick(v.Middle, clickCount: 2);    // …and the double-click pauses it as it opens

            Assert.Equal("pause", v.Player.LastPlayOrPause);
            Assert.Single(v.Opened);
        });
    }

    [Fact]
    public void Esc_ClosesAVideo()
    {
        WithQuickView(["v0.mp4"], v =>
        {
            Assert.True(v.Window.QuickLookKey(Key.Escape, ModifierKeys.None));

            Assert.False(v.Vm.QuickLookOpen);
            Assert.True(v.Player.IsClosed);
        });
    }

    [Fact]
    public void TheControls_DriveThePlayer()
    {
        WithQuickView(["v0.mp4"], v =>
        {
            v.Player.RaiseOpened();
            Settle();
            var buttons = Descendants(v.Chrome).OfType<Button>().Where(b => b.IsVisible).ToList();
            var play = Assert.Single(buttons, b => b.Command == v.Window.Playback.ToggleCommand);
            var mute = Assert.Single(buttons, b => b.Command == v.Window.Playback.ToggleMuteCommand);
            var volume = v.VolumeSlider;
            Assert.True(volume.IsVisible);

            play.Command.Execute(null);
            Assert.Equal("play", v.Player.LastPlayOrPause);
            mute.Command.Execute(null);
            Assert.False(v.Player.IsMuted);
            volume.Value = 0.8;
            Assert.Equal(0.8, v.Player.Volume);
            Assert.Equal((0.0, 1.0), (volume.Minimum, volume.Maximum));
            Assert.False(play.Focusable); // Space stays quick view's, never a focused button's
            Assert.False(volume.Focusable);
        });
    }

    [Fact]
    public void TheNextVideo_AfterARestart_StartsAsTheLastWasLeft()
    {
        using var prefs = new TempDir();
        var path = prefs.Combine(PlaybackPrefsStore.FileName);

        WithQuickView(["v0.mp4"], v =>
        {
            v.Window.Playback.SetMuted(false);
            v.Window.Playback.SetVolume(0.7);
        }, prefsPath: path);
        WithQuickView(["v0.mp4"], v =>
        {
            Assert.Equal((false, 0.7), (v.Player.IsMuted, v.Player.Volume));
        }, prefsPath: path);
    }

    [Fact]
    public void MovingOn_ClosesTheVideo()
    {
        WithQuickView(["v0.mp4", "p1.jpg"], v =>
        {
            var first = v.Player;

            v.Window.QuickLookKey(Key.Right, ModifierKeys.None);

            Assert.True(first.IsClosed);
            Assert.Single(v.Players); // the photo has no player
            Assert.False(v.Window.Playback.IsVideoShown);
        });
    }

    [Fact]
    public void VideoToPhoto_IsSilentAtOnce()
    {
        WithQuickView(["v0.mp4", "p1.jpg"], v =>
        {
            var first = v.Player;
            v.Vm.DecodeQuickLook = (_, _) => throw new InvalidOperationException("never decoded"); // the photo never arrives

            v.Vm.QuickLookNextCommand.Execute(null);

            Assert.True(first.IsClosed); // before any photo has decoded
        });
    }

    [Fact]
    public void Closing_ClosesTheVideo()
    {
        WithQuickView(["v0.mp4"], v =>
        {
            v.Vm.CloseQuickLookCommand.Execute(null);

            Assert.True(v.Player.IsClosed);
            Assert.False(v.Window.Playback.IsVideoShown);
        });
    }

    [Fact]
    public void ALibrarySwitch_ClosesTheVideo()
    {
        WithQuickView(["v0.mp4"], v =>
        {
            using var otherData = new TempDir();
            var other = new LibraryService(otherData.Path);
            var otherVm = new MainViewModel(other, new ThumbnailService(other));
            try
            {
                v.Window.DataContext = otherVm;

                Assert.True(v.Player.IsClosed);
                Assert.False(v.Window.Playback.IsVideoShown);
            }
            finally
            {
                v.Window.DataContext = v.Vm;
                otherVm.PrepareForClose();
                other.ShutdownAsync().GetAwaiter().GetResult();
            }
        });
    }

    [Fact]
    public void ZoomInput_DoesNothingOnAVideo()
    {
        WithQuickView(["v0.mp4"], v =>
        {
            Assert.True(Sta.PumpUntil(() => v.Vm.QuickLookImage is not null, Wait));
            Settle();

            Assert.True(v.Window.QuickLookKey(Key.OemPlus, ModifierKeys.Control)); // taken, so the grid doesn't zoom
            Assert.True(v.Window.QuickLookWheel(120, v.Middle, ModifierKeys.Control));

            Assert.Equal(1, v.Window.QuickLookZoom.Zoom);
        }, still: Still16By9);
    }

    [Fact]
    public void TheStillArriving_DoesNotStopTheVideo()
    {
        var release = new ManualResetEventSlim();
        WithQuickView(["v0.mp4"], v =>
        {
            var player = v.Player;
            v.Window.QuickLookKey(Key.Space, ModifierKeys.None);
            Assert.Equal("play", player.LastPlayOrPause);

            release.Set();                                                   // the still decodes after it began to play
            Assert.True(Sta.PumpUntil(() => v.Vm.QuickLookImage is not null, Wait));
            Settle();

            Assert.False(player.IsClosed);
            Assert.Single(v.Players);
            Assert.True(v.Window.Playback.IsPlaying);
        }, still: () =>
        {
            release.Wait(Wait);
            return Still16By9();
        });
    }

    [Fact]
    public void VideoToVideo_WithoutStills_ShowsTheNewOnePaused()
    {
        WithQuickView(["v0.mp4", "v1.mp4"], v =>
        {
            var first = v.Player;
            v.Window.QuickLookKey(Key.Space, ModifierKeys.None); // the first plays

            v.Window.QuickLookKey(Key.Right, ModifierKeys.None);

            Assert.True(first.IsClosed);
            Assert.Equal(2, v.Players.Count);
            Assert.Equal(new Uri(v.Path("v1.mp4")), v.Player.Source);
            Assert.Equal("pause", v.Player.LastPlayOrPause); // V9: moved to, it waits to be played
            Assert.DoesNotContain("play", v.Player.Calls);
            Assert.False(v.Window.Playback.IsPlaying);
        });
    }

    [Fact]
    public void ReopeningOnTheSameVideo_ShowsItPausedAgain()
    {
        WithQuickView(["v0.mp4"], v =>
        {
            v.Window.QuickLookKey(Key.Space, ModifierKeys.None);
            v.Vm.CloseQuickLookCommand.Execute(null);
            v.Vm.OpenQuickLookCommand.Execute(null);

            Assert.Equal(2, v.Players.Count);
            Assert.True(v.Players[0].IsClosed);
            Assert.Equal(new Uri(v.Path("v0.mp4")), v.Player.Source);
            Assert.Equal("pause", v.Player.LastPlayOrPause);
            Assert.DoesNotContain("play", v.Player.Calls);
        });
    }

    [Fact]
    public void WithoutAMediaPlayer_TheWindowOpens_AndAVideoShowsItsStill()
    {
        WithQuickView(["v0.mp4"], v =>
        {
            Assert.True(Sta.PumpUntil(() => v.Vm.QuickLookImage is not null, Wait));
            Settle();

            Assert.True(v.Window.Playback.HasFailed);
            Assert.Equal(Visibility.Visible, v.Still.Visibility);
            Assert.Contains(VisibleTexts(v.Chrome), t => t.Contains("can't be played here"));
            Assert.True(v.Window.QuickLookKey(Key.Space, ModifierKeys.None)); // and Space closes, as for a photo
            Assert.False(v.Vm.QuickLookOpen);
        }, still: Still16By9, factory: () => throw new NotSupportedException("no media player"));
    }

    [Fact]
    public void TheHintLine_NamesTheVideoKeys()
    {
        WithQuickView(["v0.mp4", "p1.jpg"], v =>
        {
            Assert.Contains(VisibleTexts(v.Chrome), t => t.Contains("Space or click to play or pause") && t.Contains("Esc to close"));
            Assert.DoesNotContain(VisibleTexts(v.Window), t => t.Contains("Ctrl+0 to fit")); // no zoom on a video

            v.Window.QuickLookKey(Key.Right, ModifierKeys.None);
            Settle();

            Assert.Contains(VisibleTexts(v.Window), t => t.Contains("Ctrl+0 to fit")); // a photo's keys again
            Assert.DoesNotContain(VisibleTexts(v.Window), t => t.Contains("Space or click to play or pause"));
        });
    }
}
