using SkiaSharp;

namespace Reel.Core.Tests.Support;

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
}
