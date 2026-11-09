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
    private const double Near = 1e-6; // the photo isn't rounded to whole pixels: drawn where the zoom says, to rounding error

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
            w.QuickLookMove(new Point(c.X + 40, c.Y + 30));

            Assert.True(v.Vm.QuickLookOpen);
            Assert.Equal(left + 40, v.Zoom.Left, 9);
            Assert.Equal(top + 30, v.Zoom.Top, 9);
            AssertDrawnAsTheModelSays(v);

            w.QuickLookMove(new Point(c.X + 50, c.Y + 30));
            Assert.Equal(left + 50, v.Zoom.Left, 9); // from where the last move left it

            w.QuickLookRelease();
            w.QuickLookMove(new Point(c.X + 200, c.Y + 200));
            Assert.Equal(left + 50, v.Zoom.Left, 9); // released: a move no longer drags it
        });
    }

    [Fact]
    public void APressOnAFittedPhoto_DoesntClose()
    {
        WithQuickView(1, v =>
        {
            v.Window.QuickLookPress(Centre(v));
            v.Window.QuickLookRelease();

            Assert.True(v.Vm.QuickLookOpen);
            Assert.Equal(1, v.Zoom.Zoom);
        });
    }

    // --- Round Z1: the window's own handlers, as the mouse and keyboard send their events. The modifier keys and the
    // pointer come from the window's seams, since a test can't hold Ctrl down or move the mouse. ---

    private static void PointerAt(View v, Func<Point> inTheArea) =>
        v.Window.PointerIn = element => v.Area.TranslatePoint(inTheArea(), (UIElement)element);

    private static void Raise(UIElement target, RoutedEvent routed, MouseButton? button = null, int wheel = 0) =>
        target.RaiseEvent(routed == UIElement.PreviewMouseWheelEvent
            ? new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, wheel) { RoutedEvent = routed }
            : button is { } b
                ? new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, b) { RoutedEvent = routed }
                : new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = routed });

    private static void PressKey(Window window, Key key) =>
        window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        });

    [Fact]
    public void TheWindowsOwnHandlers_ZoomAndDrag_AsTheMouseAndKeyboardSendThem()
    {
        WithQuickView(1, v =>
        {
            var pointer = new Point(v.Zoom.Left + v.Zoom.Width * 0.3, v.Zoom.Top + v.Zoom.Height * 0.6);
            PointerAt(v, () => pointer);
            v.Window.ModifiersNow = () => ModifierKeys.Control;
            var under = PhotoPointAt(v, pointer);

            Raise(v.Area, UIElement.PreviewMouseWheelEvent, wheel: 240);

            Assert.Equal(1.5625, v.Zoom.Zoom, 9);
            var after = PhotoPointAt(v, pointer);
            Assert.Equal(under.X, after.X, Near); // at the pointer, in the image area's frame
            Assert.Equal(under.Y, after.Y, Near);

            PressKey(v.Window, Key.OemPlus);
            Assert.Equal(1.5625 * 1.25, v.Zoom.Zoom, 9);

            v.Window.ModifiersNow = () => ModifierKeys.None;
            var (left, top) = (v.Zoom.Left, v.Zoom.Top);
            Raise(v.Area, UIElement.MouseLeftButtonDownEvent, MouseButton.Left); // (capture: an inactive window gets none)
            pointer = new Point(pointer.X + 30, pointer.Y + 20);
            Raise(v.Area, UIElement.MouseMoveEvent);

            Assert.True(v.Vm.QuickLookOpen);
            Assert.Equal(left + 30, v.Zoom.Left, 9);
            Assert.Equal(top + 20, v.Zoom.Top, 9);

            Raise(v.Area, UIElement.MouseLeftButtonUpEvent, MouseButton.Left);
            pointer = new Point(pointer.X + 50, pointer.Y);
            Raise(v.Area, UIElement.MouseMoveEvent);
            Assert.Equal(left + 30, v.Zoom.Left, 9); // released: a move no longer drags it
        });
    }

    [Fact]
    public void APressBesideThePhoto_ThroughTheWindowsHandler_Closes()
    {
        WithQuickView(1, v =>
        {
            // 30 px left of the fitted photo, in the image area — a point on the photo in the overlay's frame (72 px wider)
            var beside = new Point(v.Zoom.FitLeft - 30, v.Area.ActualHeight / 2);
            PointerAt(v, () => beside);

            Raise(v.Area, UIElement.MouseLeftButtonDownEvent, MouseButton.Left);

            Assert.False(v.Vm.QuickLookOpen);
        });
    }

    [Fact]
    public void TheGridsKeys_WithQuickViewClosed_AreTheGridsAsBefore()
    {
        // Quick view's keys are its own only while it's open: closed, Ctrl+ zooms the grid's tiles again (round Z1).
        WithQuickView(1, v =>
        {
            v.Window.QuickLookKey(Key.Escape, ModifierKeys.None);
            Assert.False(v.Vm.QuickLookOpen);
            var tiles = v.Vm.TileSize;
            v.Window.ModifiersNow = () => ModifierKeys.Control;

            PressKey(v.Window, Key.OemPlus);

            Assert.True(v.Vm.TileSize > tiles, "Ctrl+ didn't zoom the grid's tiles");
            Assert.Equal(1, v.Zoom.Zoom);
        });
    }

    [Fact]
    public void ANewViewModel_StartsWithNothingZoomed()
    {
        // A library switch gives the window a new view model: quick view starts closed, with nothing zoomed.
        WithQuickView(1, v =>
        {
            v.Window.QuickLookKey(Key.OemPlus, ModifierKeys.Control);
            Assert.True(v.Zoom.IsZoomed);
            using var data = new TempDir();
            var other = new LibraryService(data.Path);
            var vm = new MainViewModel(other, new ThumbnailService(other));
            try
            {
                v.Window.DataContext = vm;

                Assert.Equal(1, v.Zoom.Zoom);
                Assert.Equal(0, v.Zoom.Width);
            }
            finally
            {
                v.Window.DataContext = v.Vm;
                vm.PrepareForClose();
                other.ShutdownAsync().GetAwaiter().GetResult();
            }
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
    public void TheDragCursor_ShowsOnlyOverAZoomedPhoto()
    {
        // Round Z1 (the reviewer's probe): a zoomed portrait still has room beside it, where a press closes quick view —
        // the cursor there mustn't invite a drag.
        WithQuickView(1, v =>
        {
            var on = Centre(v);
            var beside = new Point(v.Zoom.FitLeft / 2, v.Area.ActualHeight / 2);
            v.Window.QuickLookMove(on);
            Assert.Equal(Cursors.Hand, v.Area.Cursor); // fitted: nothing to drag

            v.Window.QuickLookKey(Key.OemPlus, ModifierKeys.Control);
            Assert.True(v.Zoom.IsZoomed);
            Assert.False(v.Zoom.Contains(beside.X, beside.Y)); // still room beside it

            Assert.Equal(Cursors.SizeAll, v.Area.Cursor); // over the photo, zoomed by the key
            v.Window.QuickLookMove(beside);
            Assert.Equal(Cursors.Hand, v.Area.Cursor);
            v.Window.QuickLookWheel(120, beside, ModifierKeys.Control);
            Assert.Equal(Cursors.Hand, v.Area.Cursor); // zoomed at a point beside it: still beside it
            v.Window.QuickLookWheel(120, on, ModifierKeys.Control); // the wheel turned over the photo, no move first
            Assert.Equal(Cursors.SizeAll, v.Area.Cursor);
            var edge = new Point(4, v.Area.ActualHeight / 2); // the photo has grown over `beside`; the edge is still clear
            Assert.False(v.Zoom.Contains(edge.X, edge.Y));
            v.Window.QuickLookMove(edge);
            Assert.Equal(Cursors.Hand, v.Area.Cursor);
            v.Window.QuickLookMove(on);
            Assert.Equal(Cursors.SizeAll, v.Area.Cursor);

            v.Window.QuickLookKey(Key.D0, ModifierKeys.Control);
            Assert.Equal(Cursors.Hand, v.Area.Cursor);
        }, size: (48, 64));
    }

    [Fact]
    public void TheHintLine_NamesTheZoomKeys()
    {
        WithQuickView(1, v =>
        {
            var overlay = (DependencyObject)v.Window.FindName("QuickLookOverlay");
            var hints = Descendants(overlay).OfType<TextBlock>().Select(t => t.Text).ToList();

            Assert.Contains("Space or Esc to close  ·  ← → to move  ·  Ctrl+scroll or Ctrl +/− to zoom, Ctrl+0 to fit", hints);
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
