using Reel.Core.Models;
using SkiaSharp;

namespace Reel.Core.Media;

/// <summary>A decoded, orientation-corrected image as raw BGRA8888 pixels.</summary>
public sealed class UprightImage
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int Stride { get; init; }
    public required byte[] Bgra { get; init; }
}

/// <summary>One encoded thumbnail plus its dimensions.</summary>
public sealed class ThumbnailData
{
    public required byte[] Bytes { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
}

/// <summary>The full set produced from a single decode of one source image.</summary>
public sealed class ThumbnailSet
{
    /// <summary>Upright (orientation-applied) source dimensions.</summary>
    public int SourceWidth { get; init; }
    public int SourceHeight { get; init; }

    /// <summary>Raw EXIF orientation the source declared, or null.</summary>
    public int? Orientation { get; init; }

    public required Dictionary<ThumbSize, ThumbnailData> Thumbs { get; init; }
}

/// <summary>
/// Decodes an image once and emits multiple downscaled JPEG thumbnails. Applies
/// EXIF/codec orientation so portrait phone photos are not stored sideways.
/// </summary>
public sealed class ThumbnailGenerator
{
    private const int JpegQuality = 82;

    /// <summary>
    /// Produces thumbnails for the requested sizes. Returns null if the file
    /// cannot be decoded (unsupported/corrupt), leaving the item indexed sans
    /// dimensions and thumbnails.
    /// </summary>
    public ThumbnailSet? Generate(string path, IEnumerable<ThumbSize> sizes)
    {
        using var stream = new SKManagedStream(File.OpenRead(path), disposeManagedStream: true);
        using var codec = SKCodec.Create(stream);
        if (codec is null)
            return null;

        var origin = codec.EncodedOrigin;
        using var decoded = SKBitmap.Decode(codec);
        if (decoded is null)
            return null;

        var upright = ApplyOrigin(decoded, origin);
        try
        {
            var thumbs = EncodeSizes(upright, sizes);
            if (thumbs.Count == 0)
                return null;

            return new ThumbnailSet
            {
                SourceWidth = upright.Width,
                SourceHeight = upright.Height,
                Orientation = OriginToExifOrientation(origin),
                Thumbs = thumbs,
            };
        }
        finally
        {
            if (!ReferenceEquals(upright, decoded))
                upright.Dispose();
        }
    }

    /// <summary>
    /// Produces thumbnails for a video: a real decoded frame via ffmpeg when
    /// available, otherwise the Windows shell's thumbnail/icon. Returns null only
    /// if neither can produce anything.
    /// </summary>
    public ThumbnailSet? GenerateVideo(string path, IEnumerable<ThumbSize> sizes)
    {
        var largest = ThumbSizes.All.Max(ThumbSizes.LongestEdge);
        using var frame = VideoFrameExtractor.Extract(path, largest);
        if (frame is not null)
        {
            var thumbs = EncodeSizes(frame, sizes);
            if (thumbs.Count > 0)
            {
                return new ThumbnailSet
                {
                    SourceWidth = frame.Width,
                    SourceHeight = frame.Height,
                    Orientation = null,
                    Thumbs = thumbs,
                };
            }
        }

        // No real frame — fall back to the shell (icon).
        return GenerateFromShell(path, sizes);
    }

    /// <summary>
    /// Produces thumbnails for any file the OS shell can render by asking the shell
    /// for an image, then downscaling. Returns null if the shell produces nothing.
    /// </summary>
    public ThumbnailSet? GenerateFromShell(string path, IEnumerable<ThumbSize> sizes)
    {
        var largest = ThumbSizes.All.Max(ThumbSizes.LongestEdge);
        using var frame = ShellThumbnailProvider.GetBitmap(path, largest);
        if (frame is null)
            return null;

        var thumbs = EncodeSizes(frame, sizes);
        if (thumbs.Count == 0)
            return null;

        return new ThumbnailSet
        {
            SourceWidth = frame.Width,
            SourceHeight = frame.Height,
            Orientation = null, // shell frames are already upright
            Thumbs = thumbs,
        };
    }

    /// <summary>
    /// Decodes a full image with EXIF/codec orientation applied — the exact same
    /// decode + <see cref="ApplyOrigin"/> path thumbnails use, reading orientation
    /// from <see cref="SKCodec.EncodedOrigin"/> — and returns BGRA8888 pixels.
    /// This keeps "copy image" / quick-look orientation identical to the tiles
    /// (WPF's <c>BitmapImage</c> ignores EXIF orientation, so it must not be used).
    /// <paramref name="maxLongestEdge"/> caps the longest edge (0 = full resolution).
    /// Null if the file cannot be decoded.
    /// </summary>
    public static UprightImage? DecodeUpright(string path, int maxLongestEdge = 0)
    {
        using var stream = new SKManagedStream(File.OpenRead(path), disposeManagedStream: true);
        using var codec = SKCodec.Create(stream);
        if (codec is null)
            return null;

        var origin = codec.EncodedOrigin;
        using var decoded = SKBitmap.Decode(codec);
        if (decoded is null)
            return null;

        var upright = ApplyOrigin(decoded, origin);
        SKBitmap? resized = null;
        try
        {
            var source = upright;
            if (maxLongestEdge > 0)
            {
                var (w, h) = FitTo(upright.Width, upright.Height, maxLongestEdge);
                if (w != upright.Width || h != upright.Height)
                {
                    var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
                    resized = upright.Resize(new SKImageInfo(w, h), sampling);
                    if (resized is not null)
                        source = resized;
                }
            }

            // Normalize to BGRA8888 (WPF Bgra32), converting from whatever the decode
            // produced. Bytes returns a copy, so it survives disposing the bitmaps.
            using var bgra = source.ColorType == SKColorType.Bgra8888 ? source : source.Copy(SKColorType.Bgra8888);
            if (bgra is null)
                return null;

            return new UprightImage
            {
                Width = bgra.Width,
                Height = bgra.Height,
                Stride = bgra.RowBytes,
                Bgra = bgra.Bytes,
            };
        }
        finally
        {
            resized?.Dispose();
            if (!ReferenceEquals(upright, decoded))
                upright.Dispose();
        }
    }

    private static Dictionary<ThumbSize, ThumbnailData> EncodeSizes(SKBitmap upright, IEnumerable<ThumbSize> sizes)
    {
        var thumbs = new Dictionary<ThumbSize, ThumbnailData>();
        foreach (var size in sizes)
        {
            var data = Encode(upright, ThumbSizes.LongestEdge(size));
            if (data is not null)
                thumbs[size] = data;
        }
        return thumbs;
    }

    private static ThumbnailData? Encode(SKBitmap upright, int longestEdge)
    {
        var (w, h) = FitTo(upright.Width, upright.Height, longestEdge);

        SKBitmap? resized = null;
        try
        {
            SKBitmap toEncode;
            if (w == upright.Width && h == upright.Height)
            {
                // Source already within target — encode as-is, no upscaling.
                toEncode = upright;
            }
            else
            {
                var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
                resized = upright.Resize(new SKImageInfo(w, h), sampling);
                if (resized is null)
                    return null;
                toEncode = resized;
            }

            using var image = SKImage.FromBitmap(toEncode);
            using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
            if (encoded is null)
                return null;

            return new ThumbnailData
            {
                Bytes = encoded.ToArray(),
                Width = toEncode.Width,
                Height = toEncode.Height,
            };
        }
        finally
        {
            resized?.Dispose();
        }
    }

    /// <summary>Scales (w,h) down to fit within a square of <paramref name="longestEdge"/>. Never upscales.</summary>
    private static (int Width, int Height) FitTo(int w, int h, int longestEdge)
    {
        var longest = Math.Max(w, h);
        if (longest <= longestEdge)
            return (w, h);

        var scale = (double)longestEdge / longest;
        return (Math.Max(1, (int)Math.Round(w * scale)), Math.Max(1, (int)Math.Round(h * scale)));
    }

    /// <summary>
    /// Returns an upright bitmap. For the no-op orientation the source is returned
    /// directly (caller must not dispose it twice — see the ReferenceEquals guard).
    /// </summary>
    private static SKBitmap ApplyOrigin(SKBitmap src, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default)
            return src;

        var swapsAxes = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

        var dstW = swapsAxes ? src.Height : src.Width;
        var dstH = swapsAxes ? src.Width : src.Height;

        var dst = new SKBitmap(dstW, dstH);
        using (var canvas = new SKCanvas(dst))
        {
            canvas.SetMatrix(OriginMatrix(origin, src.Width, src.Height));
            canvas.DrawBitmap(src, new SKPoint(0, 0), new SKSamplingOptions());
        }
        return dst;
    }

    // Maps a source pixel (x,y) to its upright destination. Matrix fields are in
    // (scaleX, skewX, transX, skewY, scaleY, transY, persp0, persp1, persp2) order.
    private static SKMatrix OriginMatrix(SKEncodedOrigin origin, int w, int h) => origin switch
    {
        SKEncodedOrigin.TopRight    => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),   // mirror horizontal
        SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),  // rotate 180
        SKEncodedOrigin.BottomLeft  => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),   // mirror vertical
        SKEncodedOrigin.LeftTop     => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),    // transpose
        SKEncodedOrigin.RightTop    => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),   // rotate 90 CW
        SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),  // transverse
        SKEncodedOrigin.LeftBottom  => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),   // rotate 90 CCW
        _ => SKMatrix.CreateIdentity(),
    };

    private static int? OriginToExifOrientation(SKEncodedOrigin origin) => origin switch
    {
        SKEncodedOrigin.TopLeft => 1,
        SKEncodedOrigin.TopRight => 2,
        SKEncodedOrigin.BottomRight => 3,
        SKEncodedOrigin.BottomLeft => 4,
        SKEncodedOrigin.LeftTop => 5,
        SKEncodedOrigin.RightTop => 6,
        SKEncodedOrigin.RightBottom => 7,
        SKEncodedOrigin.LeftBottom => 8,
        _ => null,
    };
}
