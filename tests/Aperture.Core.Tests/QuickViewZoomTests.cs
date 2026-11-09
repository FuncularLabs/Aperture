using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Aperture.App;
using Aperture.App.Services;
using Aperture.App.ViewModels;
using Aperture.Core.Library;
using Aperture.Core.Tests.Support;
using Path = System.IO.Path;

namespace Aperture.Core.Tests;

/// <summary>
/// Zoom in quick view, in the real window shown off screen (Paul, 2026-11-09: "the previewer should follow a similar zoom
/// in/out with Ctrl+Scroll and Ctrl +/- as Photos and most image editors"). The window's entry points take the modifier
/// keys and the pointer as arguments, as its handlers pass them, since a test can't hold Ctrl down.
/// </summary>
public class QuickViewZoomTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private const double Near = 0.5; // layout rounds to the pixel

    private sealed record View(MainViewModel Vm, MainWindow Window, ZoomPan Zoom, Image Img, FrameworkElement Area);

    // A library of 4:3 photos (newest first), a real window over it shown off screen, quick view open on the first photo.
    private static void WithQuickView(int photos, Action<View> body, int decodeEdge = 1600, (int W, int H)? size = null)
    {
        Sta.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            using var data = new TempDir();
            using var folder = new TempDir();
            for (var i = 0; i < photos; i++)
            {
                var path = Path.Combine(folder.Path, $"p{i}.jpg");
                TestImages.Write(path, size?.W ?? 64, size?.H ?? 48);
                File.SetLastWriteTimeUtc(path, new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc).AddDays(-i));
            }
            var library = new LibraryService(data.Path);
            library.IndexRoot(library.AddRoot(folder.Path, "Photos"));
            var vm = new MainViewModel(library, new ThumbnailService(library)) { QuickLookDecodeEdge = decodeEdge };
            var window = new MainWindow
            {
                Width = 1200, Height = 800, DataContext = vm,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000,
                ShowActivated = false, ShowInTaskbar = false,
            };
            try
            {
                window.Show();
                Assert.True(Sta.PumpUntil(() => vm.TotalItemCount == photos && !vm.IsLoadingView, Wait));
                vm.OpenQuickLookCommand.Execute(null);
                Assert.True(Sta.PumpUntil(() => vm.QuickLookImage is not null, Wait), "quick view showed nothing");
                Settle();
                body(new View(vm, window, window.QuickLookZoom, (Image)window.FindName("QuickLookImg"),
                    (FrameworkElement)window.FindName("QuickLookImageArea")));
            }
            finally
            {
                window.Close();
                vm.PrepareForClose();
                library.ShutdownAsync().GetAwaiter().GetResult();
            }
        });
    }

    private static void Settle() => Sta.PumpUntil(() => false, TimeSpan.FromMilliseconds(300));

    // Where the photo is drawn in quick view's area, from the window's own layout and transforms.
    private static Rect Drawn(View v)
    {
        v.Window.UpdateLayout();
        var topLeft = v.Img.TranslatePoint(new Point(0, 0), v.Area);
        var bottomRight = v.Img.TranslatePoint(new Point(v.Img.ActualWidth, v.Img.ActualHeight), v.Area);
        return new Rect(topLeft, bottomRight);
    }

    private static void AssertDrawnAsTheModelSays(View v)
    {
        var drawn = Drawn(v);
        Assert.Equal(v.Zoom.Left, drawn.Left, Near);
        Assert.Equal(v.Zoom.Top, drawn.Top, Near);
        Assert.Equal(v.Zoom.Width, drawn.Width, Near);
        Assert.Equal(v.Zoom.Height, drawn.Height, Near);
    }

    // The point of the photo (in its own layout units) drawn at `at` in the area.
    private static Point PhotoPointAt(View v, Point at)
    {
        v.Window.UpdateLayout();
        return v.Area.TranslatePoint(at, v.Img);
    }

    private static Point Centre(View v) => new(v.Area.ActualWidth / 2, v.Area.ActualHeight / 2);

    [Fact]
    public void ThePhoto_IsDrawnWhereTheZoomSays_FittedAndZoomed()
    {
        WithQuickView(1, v =>
        {
            Assert.True(v.Area.ActualWidth > 0 && v.Area.ActualHeight > 0, "quick view's area wasn't laid out");
            Assert.True(v.Zoom.FitWidth > 0, "the zoom wasn't given the view and the photo");
            Assert.Equal(1, v.Zoom.Zoom);
            AssertDrawnAsTheModelSays(v);

            Assert.True(v.Window.QuickLookWheel(240, new Point(v.Zoom.Left + v.Zoom.Width / 3, v.Zoom.Top + 40), ModifierKeys.Control));

            Assert.True(v.Zoom.IsZoomed);
            AssertDrawnAsTheModelSays(v);
        });
    }

    [Fact]
    public void CtrlWheel_ZoomsAtThePointer_APlainWheelDoesnt()
    {
        WithQuickView(1, v =>
        {
            var at = new Point(v.Zoom.Left + v.Zoom.Width * 0.3, v.Zoom.Top + v.Zoom.Height * 0.6);
            var under = PhotoPointAt(v, at);

            Assert.False(v.Window.QuickLookWheel(240, at, ModifierKeys.None));
            Assert.Equal(1, v.Zoom.Zoom);

            Assert.True(v.Window.QuickLookWheel(240, at, ModifierKeys.Control)); // two notches: far enough from the edges

            Assert.Equal(1.5625, v.Zoom.Zoom, 9);
            var after = PhotoPointAt(v, at);
            Assert.Equal(under.X, after.X, Near); // the same point of the photo is under the pointer
            Assert.Equal(under.Y, after.Y, Near);

            Assert.True(v.Window.QuickLookWheel(-240, at, ModifierKeys.Control | ModifierKeys.Shift));
            Assert.Equal(1, v.Zoom.Zoom, 9);

            // The window's own handler, as the mouse sends it: no Ctrl held, so nothing happens.
            v.Area.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
            Assert.Equal(1, v.Zoom.Zoom);
        });
    }

    [Fact]
    public void CtrlPlusMinusAndZero_Zoom_AndQuickViewsOtherKeysStillWork()
    {
        WithQuickView(2, v =>
        {
            var w = v.Window;
            Assert.True(w.QuickLookKey(Key.OemPlus, ModifierKeys.Control));
            Assert.Equal(1.25, v.Zoom.Zoom, 9);
            Assert.True(w.QuickLookKey(Key.Add, ModifierKeys.Control));
            Assert.Equal(1.5625, v.Zoom.Zoom, 9);
            Assert.True(w.QuickLookKey(Key.OemMinus, ModifierKeys.Control));
            Assert.Equal(1.25, v.Zoom.Zoom, 9);
            Assert.True(w.QuickLookKey(Key.Subtract, ModifierKeys.Control));
            Assert.Equal(1, v.Zoom.Zoom, 9);
            Assert.True(w.QuickLookKey(Key.OemPlus, ModifierKeys.Control | ModifierKeys.Shift)); // "+" on the = key
            Assert.True(w.QuickLookKey(Key.D0, ModifierKeys.Control));
            Assert.Equal(1, v.Zoom.Zoom, 9);
            Assert.True(w.QuickLookKey(Key.Add, ModifierKeys.Control));
            Assert.True(w.QuickLookKey(Key.NumPad0, ModifierKeys.Control));
            Assert.Equal(1, v.Zoom.Zoom, 9);
            AssertDrawnAsTheModelSays(v);

            Assert.False(w.QuickLookKey(Key.OemPlus, ModifierKeys.None)); // without Ctrl, not a zoom
            Assert.False(w.QuickLookKey(Key.D0, ModifierKeys.None));
            Assert.Equal(1, v.Zoom.Zoom);

            Assert.True(w.QuickLookKey(Key.Right, ModifierKeys.Control)); // quick view's own keys, Ctrl or not
            Assert.True(Sta.PumpUntil(() => v.Vm.QuickLookCaption.Contains("2 / 2"), Wait));
            Assert.True(w.QuickLookKey(Key.Left, ModifierKeys.None));
            Assert.True(Sta.PumpUntil(() => v.Vm.QuickLookCaption.Contains("1 / 2"), Wait));

            // Through the window's key handler, as the keyboard sends it.
            var source = PresentationSource.FromVisual(w)!;
            w.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Right) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Assert.True(Sta.PumpUntil(() => v.Vm.QuickLookCaption.Contains("2 / 2"), Wait), "the window's keys didn't reach quick view");

            Assert.True(w.QuickLookKey(Key.Escape, ModifierKeys.Control));
            Assert.False(v.Vm.QuickLookOpen);
        });
    }

    [Fact]
    public void DraggingAZoomedPhoto_MovesIt_UntilReleased()
    {
        WithQuickView(1, v =>
        {
            var w = v.Window;
            for (var i = 0; i < 4; i++)
                w.QuickLookKey(Key.OemPlus, ModifierKeys.Control); // 1.25⁴ ≈ 2.44: bigger than the view both ways
            var (left, top) = (v.Zoom.Left, v.Zoom.Top);
            var c = Centre(v);

            w.QuickLookPress(c);
            w.QuickLookDrag(new Point(c.X + 40, c.Y + 30));

            Assert.True(v.Vm.QuickLookOpen);
            Assert.Equal(left + 40, v.Zoom.Left, 9);
            Assert.Equal(top + 30, v.Zoom.Top, 9);
            AssertDrawnAsTheModelSays(v);

            w.QuickLookDrag(new Point(c.X + 50, c.Y + 30));
            Assert.Equal(left + 50, v.Zoom.Left, 9); // from where the last move left it

            w.QuickLookRelease();
            w.QuickLookDrag(new Point(c.X + 200, c.Y + 200));
            Assert.Equal(left + 50, v.Zoom.Left, 9); // released: a move no longer drags it
        });
    }

    [Fact]
    public void APress_OnAFittedPhoto_NeitherClosesNorDrags()
    {
        WithQuickView(1, v =>
        {
            var c = Centre(v);
            v.Window.QuickLookPress(c);
            v.Window.QuickLookDrag(new Point(c.X + 40, c.Y));

            Assert.True(v.Vm.QuickLookOpen);
            Assert.Equal(1, v.Zoom.Zoom);
            Assert.Equal(v.Zoom.FitLeft, v.Zoom.Left, 9);
            v.Window.QuickLookRelease();
        });
    }

    [Fact]
    public void APressBesideThePhoto_Closes_BesideItAsShown()
    {
        WithQuickView(1, v =>
        {
            // Fitted, a 4:3 photo in a wider view leaves room beside it.
            Assert.True(v.Zoom.FitLeft > 20, $"no room beside the photo (it's at {v.Zoom.FitLeft})");
            var beside = new Point(v.Zoom.FitLeft / 2, v.Area.ActualHeight / 2);

            v.Window.QuickLookKey(Key.OemPlus, ModifierKeys.Control); // zoomed, it covers that room
            Assert.True(v.Zoom.Contains(beside.X, beside.Y));
            v.Window.QuickLookPress(beside);
            v.Window.QuickLookRelease();
            Assert.True(v.Vm.QuickLookOpen, "a press on the zoomed photo closed quick view");

            v.Window.QuickLookKey(Key.D0, ModifierKeys.Control);
            v.Window.QuickLookPress(beside);
            Assert.False(v.Vm.QuickLookOpen, "a press beside the fitted photo didn't close quick view");
        });
    }

    [Fact]
    public void AnotherPhoto_OpensAtTheFit_AndSoDoesQuickViewOpenedAgain()
    {
        WithQuickView(2, v =>
        {
            v.Window.QuickLookKey(Key.OemPlus, ModifierKeys.Control);
            v.Window.QuickLookKey(Key.OemPlus, ModifierKeys.Control);

            v.Window.QuickLookKey(Key.Right, ModifierKeys.None);
            Assert.True(Sta.PumpUntil(() => v.Vm.QuickLookCaption.Contains("2 / 2") && v.Vm.QuickLookImage is not null, Wait));
            Settle();

            Assert.Equal(1, v.Zoom.Zoom);
            AssertDrawnAsTheModelSays(v);
            Assert.Equal(v.Zoom.FitLeft, v.Zoom.Left, 9);

            v.Window.QuickLookKey(Key.OemPlus, ModifierKeys.Control);
            v.Window.QuickLookKey(Key.Escape, ModifierKeys.None);
            v.Vm.OpenQuickLookCommand.Execute(null);
            Assert.True(Sta.PumpUntil(() => v.Vm.QuickLookImage is not null, Wait));
            Settle();

            Assert.Equal(1, v.Zoom.Zoom);
            AssertDrawnAsTheModelSays(v);
        });
    }

    [Fact]
    public void AtTheLastPhoto_RightLeavesItsZoomAlone()
    {
        WithQuickView(1, v =>
        {
            v.Window.QuickLookKey(Key.OemPlus, ModifierKeys.Control);

            v.Window.QuickLookKey(Key.Right, ModifierKeys.None);
            Settle();

            Assert.Equal(1.25, v.Zoom.Zoom, 9);
            Assert.NotNull(v.Vm.QuickLookImage);
        });
    }

    [Fact]
    public void ZoomingPastTheImage_LoadsThePhotoInFull_WithoutMovingTheView()
    {
        WithQuickView(1, v =>
        {
            Assert.Equal(32, v.Vm.QuickLookImage!.PixelWidth); // cut down to the decode size
            Settle();
            Assert.Equal(32, v.Vm.QuickLookImage!.PixelWidth); // fitted: as it opens, however few its pixels

            v.Window.QuickLookWheel(120, Centre(v), ModifierKeys.Control);
            var (zoom, left, top) = (v.Zoom.Zoom, v.Zoom.Left, v.Zoom.Top);

            Assert.True(Sta.PumpUntil(() => v.Vm.QuickLookImage?.PixelWidth == 640, Wait), "zooming in didn't load the photo in full");
            Settle();
            Assert.Equal(zoom, v.Zoom.Zoom);
            Assert.Equal(left, v.Zoom.Left, 9);
            Assert.Equal(top, v.Zoom.Top, 9);
            AssertDrawnAsTheModelSays(v);
        }, decodeEdge: 32, size: (640, 480));
    }

    [Fact]
    public void ResizingTheWindow_KeepsTheZoom_AndTheDrawingFollows()
    {
        WithQuickView(1, v =>
        {
            v.Window.QuickLookKey(Key.OemPlus, ModifierKeys.Control);
            v.Window.QuickLookKey(Key.OemPlus, ModifierKeys.Control);
            var fit = v.Zoom.FitWidth;

            v.Window.Width -= 300;
            Settle();

            Assert.Equal(1.5625, v.Zoom.Zoom, 9);
            Assert.NotEqual(fit, v.Zoom.FitWidth);
            AssertDrawnAsTheModelSays(v);
        });
    }

    [Fact]
    public void TheHintLine_NamesTheZoomKeys()
    {
        WithQuickView(1, v =>
        {
            var overlay = (DependencyObject)v.Window.FindName("QuickLookOverlay");
            var hints = Descendants(overlay).OfType<TextBlock>().Select(t => t.Text).ToList();

            Assert.Contains(hints, text => text.Contains("Ctrl") && text.Contains("zoom") && text.Contains("Ctrl+0"));
        });
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
}
