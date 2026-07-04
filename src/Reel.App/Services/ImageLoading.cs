using System.IO;
using System.Windows.Media.Imaging;

namespace Reel.App.Services;

/// <summary>Decodes full-size images for the quick-look overlay, off the UI thread.</summary>
public static class ImageLoading
{
    /// <summary>Loads an image file, capped to <paramref name="decodePixelWidth"/> px wide. Null on failure.</summary>
    public static BitmapSource? LoadFullImage(string path, int decodePixelWidth = 1600)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            using var stream = File.OpenRead(path);
            return Decode(stream, decodePixelWidth);
        }
        catch
        {
            return null;
        }
    }

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
