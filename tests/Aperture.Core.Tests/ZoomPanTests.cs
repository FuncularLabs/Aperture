using Aperture.App;

namespace Aperture.Core.Tests;

/// <summary>
/// Quick view's zoom and pan, as image viewers do (Paul, 2026-11-09): the photo opens fitted; zooming keeps the point
/// under the pointer where it is, from the fit to 8 × it; the photo never moves past its edges. A 1000 × 600 view and a
/// 4:3 photo throughout unless said: fitted, it's 800 × 600 at (100, 0).
/// </summary>
public class ZoomPanTests
{
    private const int Precision = 9;

    private static ZoomPan Fitted(double photoWidth = 400, double photoHeight = 300)
    {
        var zoom = new ZoomPan();
        zoom.SetView(1000, 600);
        zoom.SetImage(photoWidth, photoHeight);
        return zoom;
    }

    private static void AssertShown(ZoomPan zoom, double left, double top, double width, double height)
    {
        Assert.Equal(left, zoom.Left, Precision);
        Assert.Equal(top, zoom.Top, Precision);
        Assert.Equal(width, zoom.Width, Precision);
        Assert.Equal(height, zoom.Height, Precision);
    }

    [Fact]
    public void APhoto_OpensFitted_AndCentred()
    {
        var zoom = Fitted();

        Assert.Equal(1, zoom.Zoom);
        Assert.False(zoom.IsZoomed);
        AssertShown(zoom, 100, 0, 800, 600);
        Assert.Equal(100, zoom.FitLeft, Precision);
        Assert.Equal(0, zoom.FitTop, Precision);
        Assert.Equal(800, zoom.FitWidth, Precision);
        Assert.Equal(600, zoom.FitHeight, Precision);
    }

    [Fact]
    public void ZoomAt_KeepsThePointUnderThePointer()
    {
        var zoom = Fitted();

        zoom.ZoomAt(2, 300, 200); // a quarter of the way across the photo, a third of the way down

        Assert.Equal(2, zoom.Zoom);
        Assert.True(zoom.IsZoomed);
        AssertShown(zoom, 300 - 0.25 * 1600, 200 - 1200 / 3.0, 1600, 1200);
    }

    [Fact]
    public void ZoomIn_And_ZoomOut_StepByAQuarter_AboutTheCentre()
    {
        var zoom = Fitted();

        zoom.ZoomIn();

        Assert.Equal(1.25, zoom.Zoom, Precision);
        AssertShown(zoom, 0, 300 - 375, 1000, 750); // the photo's centre stays at the view's (500, 300)

        zoom.ZoomIn();
        zoom.ZoomOut();

        Assert.Equal(1.25, zoom.Zoom, Precision);
        AssertShown(zoom, 0, -75, 1000, 750);
    }

    [Fact]
    public void ZoomingOut_StopsAtTheFit()
    {
        var zoom = Fitted();
        zoom.ZoomAt(2, 300, 200);

        zoom.ZoomAt(0.1, 900, 100);

        Assert.Equal(1, zoom.Zoom);
        AssertShown(zoom, 100, 0, 800, 600); // centred again, wherever the pointer was
        zoom.ZoomOut();
        Assert.Equal(1, zoom.Zoom);
    }

    [Fact]
    public void Zooming_StopsAtEightTimesTheFit()
    {
        var zoom = Fitted();

        zoom.ZoomAt(100, 500, 300);

        Assert.Equal(ZoomPan.MaxZoom, zoom.Zoom);
        Assert.Equal(8, ZoomPan.MaxZoom);
        Assert.Equal(6400, zoom.Width, Precision);
    }

    [Fact]
    public void TheWheel_ZoomsInProportionToItsStep()
    {
        var zoom = Fitted();

        zoom.ZoomByWheel(120, 500, 300); // one notch
        Assert.Equal(1.25, zoom.Zoom, Precision);
        zoom.ZoomByWheel(240, 500, 300); // two
        Assert.Equal(1.25 * 1.25 * 1.25, zoom.Zoom, Precision);
        zoom.ZoomByWheel(-120, 500, 300);
        Assert.Equal(1.25 * 1.25, zoom.Zoom, Precision);
        zoom.ZoomByWheel(60, 500, 300); // a touchpad's half notch
        Assert.Equal(1.25 * 1.25 * Math.Sqrt(1.25), zoom.Zoom, Precision);
    }

    [Theory]
    [InlineData(1, 35)]
    [InlineData(2, 11)]
    [InlineData(3, 2)]
    [InlineData(2, 72)]
    public void ATouchpadsStepsInAndBackOut_EndAtTheFit(int first, int second)
    {
        // Round Z1 (the reviewer's simulation): in floating point these come back to 1.0000000000000002 — zoomed, by a
        // hair, while showing the fit (the drag cursor, a drag that moves nothing, even a full load).
        var zoom = Fitted();

        zoom.ZoomByWheel(first, 500, 300);
        zoom.ZoomByWheel(second, 500, 300);
        zoom.ZoomByWheel(-first, 500, 300);
        zoom.ZoomByWheel(-second, 500, 300);

        Assert.Equal(1.0, zoom.Zoom);
        Assert.False(zoom.IsZoomed);
    }

    [Fact]
    public void ASmallStepFromTheFit_Zooms()
    {
        // Round Z2: the snap to the fit is a rounding error wide, no more — a touchpad's smallest step (1 of a notch's 120)
        // still zooms in.
        var zoom = Fitted();

        zoom.ZoomByWheel(1, 500, 300);

        Assert.Equal(Math.Pow(1.25, 1 / 120.0), zoom.Zoom, 12);
        Assert.True(zoom.IsZoomed);
    }

    [Fact]
    public void Panning_StopsAtThePhotosEdges()
    {
        var zoom = Fitted();
        zoom.ZoomAt(2, 500, 300); // 1600 × 1200 at (-300, -300)
        AssertShown(zoom, -300, -300, 1600, 1200);

        zoom.PanBy(120, -50);
        AssertShown(zoom, -180, -350, 1600, 1200);

        zoom.PanBy(1000, 1000); // past the top-left: its edges stop at the view's
        AssertShown(zoom, 0, 0, 1600, 1200);

        zoom.PanBy(-5000, -5000);
        AssertShown(zoom, 1000 - 1600, 600 - 1200, 1600, 1200);
    }

    [Fact]
    public void APanorama_FillsTheWidth_CentredDown()
    {
        // Round Z1: a photo wider than the view's shape fills its width (the other way of fitting) — 3:1 in 1000 × 600
        // is 1000 × 333⅓ at (0, 133⅓).
        var zoom = Fitted(300, 100);

        AssertShown(zoom, 0, 600 / 2.0 - 1000 / 6.0, 1000, 1000 / 3.0);
        Assert.Equal(1000 / 3.0, zoom.FitHeight, Precision);
        Assert.Equal(600 / 2.0 - 1000 / 6.0, zoom.FitTop, Precision);

        zoom.ZoomAt(2, 250, 300); // a quarter of the way across, half way down

        AssertShown(zoom, 250 - 0.25 * 2000, 300 - 0.5 * 2000 / 3.0, 2000, 2000 / 3.0);
    }

    [Fact]
    public void ASideSmallerThanTheView_StaysCentred()
    {
        var zoom = Fitted(300, 400); // portrait: fitted 450 × 600 at (275, 0)
        AssertShown(zoom, 275, 0, 450, 600);

        zoom.ZoomAt(1.25, 0, 300); // the pointer at the view's left edge; still narrower than the view

        AssertShown(zoom, (1000 - 562.5) / 2, 300 - 375, 562.5, 750);
        zoom.PanBy(100, 10);
        AssertShown(zoom, (1000 - 562.5) / 2, -65, 562.5, 750); // across, it stays put; down, it moves
    }

    [Fact]
    public void Contains_IsThePhotoAsShown()
    {
        var zoom = Fitted();

        Assert.True(zoom.Contains(100, 0));
        Assert.True(zoom.Contains(900, 600));
        Assert.False(zoom.Contains(99, 300)); // in the letterbox beside it
        Assert.False(zoom.Contains(901, 300));

        zoom.ZoomAt(2, 500, 300); // it covers the view now

        Assert.True(zoom.Contains(10, 300));
        Assert.True(zoom.Contains(990, 300));
    }

    [Fact]
    public void ANewView_KeepsTheZoomAndTheCentre()
    {
        var zoom = Fitted();
        zoom.ZoomAt(2, 300, 200); // 1600 × 1200 at (-100, -200); the view's centre (500, 300) is 3/8 across, 5/12 down

        zoom.SetView(500, 300); // half the size: fitted 400 × 300 at (50, 0)

        Assert.Equal(2, zoom.Zoom);
        Assert.Equal(50, zoom.FitLeft, Precision);
        AssertShown(zoom, 250 - 0.375 * 800, 150 - 600 * 5 / 12.0, 800, 600);
    }

    [Fact]
    public void ASharperCopy_ChangesNothing()
    {
        var zoom = Fitted();
        zoom.ZoomAt(2, 300, 200);

        zoom.SetImage(1600, 1200); // the same photo, decoded in full

        Assert.Equal(2, zoom.Zoom);
        AssertShown(zoom, -100, -200, 1600, 1200);
    }

    [Fact]
    public void Reset_FitsThePhotoAgain()
    {
        var zoom = Fitted();
        zoom.ZoomAt(3, 700, 100);

        zoom.Reset();

        Assert.Equal(1, zoom.Zoom);
        AssertShown(zoom, 100, 0, 800, 600);
    }

    [Fact]
    public void Clear_LeavesNothingToZoom_UntilAPhotoArrives()
    {
        var zoom = Fitted();
        zoom.ZoomAt(3, 700, 100);

        zoom.Clear(); // moving on: the next photo isn't here yet

        Assert.Equal(1, zoom.Zoom);
        AssertShown(zoom, 0, 0, 0, 0);
        Assert.False(zoom.Contains(500, 300));
        zoom.ZoomIn();
        zoom.ZoomByWheel(120, 500, 300);
        zoom.PanBy(50, 50);
        Assert.Equal(1, zoom.Zoom);

        zoom.SetImage(400, 300);

        Assert.Equal(1, zoom.Zoom); // the next photo opens fitted
        AssertShown(zoom, 100, 0, 800, 600);
    }

    [Fact]
    public void APhotoBeforeItsView_IsFittedOnceTheViewIsKnown()
    {
        var zoom = new ZoomPan();
        zoom.SetImage(400, 300);
        zoom.ZoomIn(); // no view yet: nothing to zoom

        zoom.SetView(1000, 600);

        Assert.Equal(1, zoom.Zoom);
        AssertShown(zoom, 100, 0, 800, 600);
    }

    [Fact]
    public void ShowsMoreThan_OnlyZoomedAndWiderThanItsPixels()
    {
        var zoom = Fitted();
        Assert.False(zoom.ShowsMoreThan(100, 1)); // fitted: as it opens, however few its pixels

        zoom.ZoomAt(2, 500, 300); // 1600 wide

        Assert.True(zoom.ShowsMoreThan(1599, 1));
        Assert.False(zoom.ShowsMoreThan(1600, 1));
        Assert.True(zoom.ShowsMoreThan(2000, 1.5)); // 2400 device pixels on a 150 % screen
        Assert.False(zoom.ShowsMoreThan(2400, 1.5));
    }
}
