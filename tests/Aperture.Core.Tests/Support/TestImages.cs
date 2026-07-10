using SkiaSharp;

namespace Aperture.Core.Tests.Support;

/// <summary>Synthesizes small real image files so the indexer/thumbnailer run against genuine decodable bytes.</summary>
public static class TestImages
{
    /// <summary>Writes a gradient-filled image of the given size. Format inferred from the file extension.</summary>
    public static void Write(string path, int width, int height)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            using var shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0),
                new SKPoint(width, height),
                [SKColors.OrangeRed, SKColors.MidnightBlue],
                null,
                SKShaderTileMode.Clamp);
            using var paint = new SKPaint { Shader = shader };
            canvas.DrawRect(0, 0, width, height, paint);
        }

        var format = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => SKEncodedImageFormat.Png,
            ".webp" => SKEncodedImageFormat.Webp,
            ".bmp" => SKEncodedImageFormat.Bmp,
            _ => SKEncodedImageFormat.Jpeg,
        };

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        using var stream = File.Create(path);
        data.SaveTo(stream);
    }

    /// <summary>
    /// Writes a JPEG of the given raw (stored) size with an EXIF orientation tag
    /// (1-8), so decoders that honor orientation upright it. Used to test that the
    /// orientation path swaps axes for rotated photos.
    /// </summary>
    public static void WriteJpegWithOrientation(string path, int width, int height, int orientation)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        byte[] jpeg;
        using (var bitmap = new SKBitmap(width, height))
        {
            using (var canvas = new SKCanvas(bitmap))
            {
                using var paint = new SKPaint { Color = SKColors.OrangeRed };
                canvas.DrawRect(0, 0, width, height, paint);
            }
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
            jpeg = data.ToArray();
        }

        // Splice a minimal EXIF APP1 segment (just the Orientation tag) right after
        // the SOI marker (FF D8), which is where readers expect the header block.
        var app1 = ExifApp1Orientation(orientation);
        using var stream = File.Create(path);
        stream.Write(jpeg, 0, 2);                // SOI
        stream.Write(app1, 0, app1.Length);      // APP1 / Exif
        stream.Write(jpeg, 2, jpeg.Length - 2);  // remainder
    }

    // A 34-byte APP1 segment: "Exif\0\0" + big-endian TIFF with one IFD0 entry
    // (tag 0x0112 Orientation, SHORT, count 1, value = orientation).
    private static byte[] ExifApp1Orientation(int orientation) =>
    [
        0xFF, 0xE1, 0x00, 0x22,                          // APP1 marker, length 34
        0x45, 0x78, 0x69, 0x66, 0x00, 0x00,              // "Exif\0\0"
        0x4D, 0x4D, 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08,  // TIFF (big-endian), IFD0 @ offset 8
        0x00, 0x01,                                      // 1 directory entry
        0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01,  // Orientation, SHORT, count 1
        0x00, (byte)orientation, 0x00, 0x00,             // value (big-endian SHORT, left-justified)
        0x00, 0x00, 0x00, 0x00,                          // next-IFD offset = 0
    ];
}
