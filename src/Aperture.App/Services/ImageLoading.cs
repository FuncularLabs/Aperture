using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Aperture.Core.Media;

namespace Aperture.App.Services;

/// <summary>Decodes full-size images for the quick-look overlay, off the UI thread.</summary>
public static class ImageLoading
{
    /// <summary>
    /// Loads a full image with EXIF orientation applied (via SkiaSharp — WPF's
    /// <see cref="BitmapImage"/> ignores EXIF orientation, which left "copy image"
    /// and quick-look sideways for rotated phone photos). <paramref name="maxLongestEdge"/>
    /// caps the longest edge (0 = full resolution). Null on failure.
    /// </summary>
    public static BitmapSource? LoadFullImageUpright(string path, int maxLongestEdge = 0)
    {
        try
        {
            var img = ThumbnailGenerator.DecodeUpright(path, maxLongestEdge);
            if (img is null)
                return null;
            var bitmap = BitmapSource.Create(
                img.Width, img.Height, 96, 96, PixelFormats.Bgra32, null, img.Bgra, img.Stride);
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Decodes cached thumbnail bytes (already upright JPEGs) for videos. Null on failure.</summary>
    public static BitmapSource? Decode(byte[] bytes, int decodePixelWidth)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            return Decode(stream, decodePixelWidth);
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource Decode(Stream stream, int decodePixelWidth)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        if (decodePixelWidth > 0)
            bitmap.DecodePixelWidth = decodePixelWidth;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
