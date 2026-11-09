namespace Aperture.App;

/// <summary>
/// Quick view's zoom and pan, as image viewers do: at a zoom of 1 the photo fits the view, letterboxed and centred
/// (zooming out stops there); zooming keeps the point under the pointer — or at the view's centre — where it is, up to
/// <see cref="MaxZoom"/>; the photo never moves past its edges, and stays centred along a side on which it's smaller than
/// the view. Positions and sizes are in the view's units (device-independent pixels); nothing here touches WPF.
/// </summary>
public sealed class ZoomPan
{
    /// <summary>The most it zooms in: 8 × the fit.</summary>
    public const double MaxZoom = 8;

    /// <summary>The zoom of one wheel notch or key press.</summary>
    public const double Step = 1.25;

    private double _viewWidth, _viewHeight;
    private double _aspect; // the photo's width over its height; 0 while there's no photo

    /// <summary>1 when the photo is fitted, up to <see cref="MaxZoom"/>.</summary>
    public double Zoom { get; private set; } = 1;

    public bool IsZoomed => Zoom > 1;

    /// <summary>The photo's left edge in the view, as shown.</summary>
    public double Left { get; private set; }

    /// <summary>The photo's top edge in the view, as shown.</summary>
    public double Top { get; private set; }

    /// <summary>The photo's width as shown.</summary>
    public double Width { get; private set; }

    /// <summary>The photo's height as shown.</summary>
    public double Height { get; private set; }

    /// <summary>The photo's width fitted to the view (at a zoom of 1).</summary>
    public double FitWidth { get; private set; }

    /// <summary>The photo's height fitted to the view.</summary>
    public double FitHeight { get; private set; }

    /// <summary>The fitted photo's left edge: the photo is centred.</summary>
    public double FitLeft => (_viewWidth - FitWidth) / 2;

    /// <summary>The fitted photo's top edge.</summary>
    public double FitTop => (_viewHeight - FitHeight) / 2;

    private bool CanZoom => FitWidth > 0 && FitHeight > 0;

    /// <summary>The view's size. The photo is fitted to it again, keeping the zoom and the point at the view's centre.</summary>
    public void SetView(double width, double height) => Refit(() =>
    {
        _viewWidth = Math.Max(0, width);
        _viewHeight = Math.Max(0, height);
    });

    /// <summary>The photo's size in pixels. Only its shape matters: a sharper copy of the photo shown changes nothing.</summary>
    public void SetImage(double pixelWidth, double pixelHeight) =>
        Refit(() => _aspect = pixelWidth > 0 && pixelHeight > 0 ? pixelWidth / pixelHeight : 0);

    /// <summary>No photo (quick view moved on, or closed): nothing to zoom until the next is set, which opens fitted.</summary>
    public void Clear()
    {
        _aspect = 0;
        Zoom = 1;
        Fit();
        Show(0.5, 0.5, 0, 0);
    }

    /// <summary>Fits the photo again.</summary>
    public void Reset()
    {
        Zoom = 1;
        Show(0.5, 0.5, _viewWidth / 2, _viewHeight / 2);
    }

    /// <summary>Zooms by <paramref name="factor"/>, keeping the point of the photo at (x, y) in the view where it is.</summary>
    public void ZoomAt(double factor, double x, double y)
    {
        if (!CanZoom || !(factor > 0))
            return;
        var (u, v) = ((x - Left) / Width, (y - Top) / Height);
        var zoom = Math.Min(Zoom * factor, MaxZoom);
        Zoom = zoom < 1 + 1e-9 ? 1 : zoom; // never below the fit; steps in and back out end at it, not a rounding error past
        Show(u, v, x, y);
    }

    /// <summary>A wheel's turn at (x, y): a notch (120) is a <see cref="Step"/>; a touchpad's smaller steps, as much less.</summary>
    public void ZoomByWheel(int delta, double x, double y) => ZoomAt(Math.Pow(Step, delta / 120.0), x, y);

    /// <summary>A step in, about the view's centre.</summary>
    public void ZoomIn() => ZoomAt(Step, _viewWidth / 2, _viewHeight / 2);

    /// <summary>A step out, about the view's centre.</summary>
    public void ZoomOut() => ZoomAt(1 / Step, _viewWidth / 2, _viewHeight / 2);

    /// <summary>Moves the photo by (dx, dy), as far as its edges allow.</summary>
    public void PanBy(double dx, double dy)
    {
        if (!CanZoom)
            return;
        Left = Clamp(Left + dx, Width, _viewWidth);
        Top = Clamp(Top + dy, Height, _viewHeight);
    }

    /// <summary>Whether (x, y) in the view is on the photo as shown.</summary>
    public bool Contains(double x, double y) =>
        CanZoom && x >= Left && x <= Left + Width && y >= Top && y <= Top + Height;

    /// <summary>
    /// Whether the photo is zoomed and shown wider on screen than an image of <paramref name="pixelWidth"/> pixels holds
    /// (at <paramref name="dpiScale"/> device pixels to the view's unit): a sharper copy would show more.
    /// </summary>
    public bool ShowsMoreThan(int pixelWidth, double dpiScale) => IsZoomed && Width * dpiScale > pixelWidth;

    private void Refit(Action change)
    {
        // The point of the photo at the view's centre, as a fraction across and down it, stays there.
        var (u, v) = Width > 0 && Height > 0
            ? ((_viewWidth / 2 - Left) / Width, (_viewHeight / 2 - Top) / Height)
            : (0.5, 0.5);
        change();
        Fit();
        Show(u, v, _viewWidth / 2, _viewHeight / 2);
    }

    // Fills the view's width or height, whichever the photo's shape allows (WPF's Stretch="Uniform").
    private void Fit()
    {
        if (_aspect <= 0 || _viewWidth <= 0 || _viewHeight <= 0)
        {
            FitWidth = FitHeight = 0;
            return;
        }
        if (_viewWidth / _viewHeight > _aspect)
        {
            FitHeight = _viewHeight;
            FitWidth = _viewHeight * _aspect;
        }
        else
        {
            FitWidth = _viewWidth;
            FitHeight = _viewWidth / _aspect;
        }
    }

    // Shows the photo at the zoom with the point (u, v) of it — fractions across and down — at (x, y) in the view.
    private void Show(double u, double v, double x, double y)
    {
        if (!CanZoom)
        {
            Left = Top = Width = Height = 0;
            return;
        }
        Width = FitWidth * Zoom;
        Height = FitHeight * Zoom;
        Left = Clamp(x - u * Width, Width, _viewWidth);
        Top = Clamp(y - v * Height, Height, _viewHeight);
    }

    // Along one side: centred if the photo is no bigger than the view; otherwise its edges stay outside the view's.
    private static double Clamp(double start, double size, double view) =>
        size <= view ? (view - size) / 2 : Math.Clamp(start, view - size, 0);
}
