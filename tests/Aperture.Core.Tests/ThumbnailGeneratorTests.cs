using Aperture.Core.Media;
using Aperture.Core.Models;
using Aperture.Core.Tests.Support;
using SkiaSharp;

namespace Aperture.Core.Tests;

public class ThumbnailGeneratorTests
{
    [Fact]
    public void Generate_ProducesAllRequestedSizes()
    {
        using var lib = new TempDir();
        var path = lib.Combine("photo.jpg");
        TestImages.Write(path, 1200, 900);

        var set = new ThumbnailGenerator().Generate(path, ThumbSizes.All);

        Assert.NotNull(set);
        Assert.Equal(3, set!.Thumbs.Count);
        Assert.Equal(1200, set.SourceWidth);
        Assert.Equal(900, set.SourceHeight);
    }

    [Fact]
    public void Generate_LandscapeThumb_FitsLongestEdge_PreservingAspect()
    {
        using var lib = new TempDir();
        var path = lib.Combine("wide.jpg");
        TestImages.Write(path, 400, 200);

        var set = new ThumbnailGenerator().Generate(path, [ThumbSize.Small]);

        var small = set!.Thumbs[ThumbSize.Small];
        Assert.Equal(128, small.Width);   // longest edge clamped to 128
        Assert.Equal(64, small.Height);   // aspect preserved (2:1)
    }

    [Fact]
    public void Generate_DoesNotUpscaleSmallSource()
    {
        using var lib = new TempDir();
        var path = lib.Combine("tiny.png");
        TestImages.Write(path, 64, 48);

        var set = new ThumbnailGenerator().Generate(path, [ThumbSize.Large]);

        var large = set!.Thumbs[ThumbSize.Large];
        Assert.Equal(64, large.Width);
        Assert.Equal(48, large.Height);
    }

    [Fact]
    public void Generate_EncodedThumb_IsDecodableToStatedSize()
    {
        using var lib = new TempDir();
        var path = lib.Combine("photo.jpg");
        TestImages.Write(path, 800, 600);

        var set = new ThumbnailGenerator().Generate(path, [ThumbSize.Medium]);
        var medium = set!.Thumbs[ThumbSize.Medium];

        using var decoded = SKBitmap.Decode(medium.Bytes);
        Assert.Equal(256, decoded.Width);
        Assert.Equal(192, decoded.Height);
    }

    [Fact]
    public void DecodeUpright_AppliesExifOrientation_ForRotatedPhoto()
    {
        using var lib = new TempDir();
        var path = lib.Combine("rotated.jpg");
        // Raw 400x200 landscape, EXIF orientation 6 = "rotate 90 CW" → upright is 200x400.
        TestImages.WriteJpegWithOrientation(path, 400, 200, orientation: 6);

        var img = ThumbnailGenerator.DecodeUpright(path);

        Assert.NotNull(img);
        Assert.Equal(200, img!.Width);   // axes swapped
        Assert.Equal(400, img.Height);
        Assert.Equal(img.Width * 4, img.Stride);          // BGRA8888
        Assert.Equal(img.Stride * img.Height, img.Bgra.Length);
    }

    [Fact]
    public void DecodeUpright_NoOrientation_KeepsDimensions()
    {
        using var lib = new TempDir();
        var path = lib.Combine("plain.jpg");
        TestImages.Write(path, 400, 200);

        var img = ThumbnailGenerator.DecodeUpright(path);

        Assert.NotNull(img);
        Assert.Equal(400, img!.Width);
        Assert.Equal(200, img.Height);
    }

    [Fact]
    public void DecodeUpright_CapsLongestEdge()
    {
        using var lib = new TempDir();
        var path = lib.Combine("big.jpg");
        TestImages.Write(path, 2000, 1000);

        var img = ThumbnailGenerator.DecodeUpright(path, maxLongestEdge: 500);

        Assert.NotNull(img);
        Assert.Equal(500, img!.Width);   // longest edge clamped
        Assert.Equal(250, img.Height);   // aspect preserved
    }

    [Fact]
    public void Generate_ReturnsNull_ForNonImage()
    {
        using var lib = new TempDir();
        var path = lib.Combine("notreally.jpg");
        File.WriteAllText(path, "this is not an image");

        var set = new ThumbnailGenerator().Generate(path, ThumbSizes.All);

        Assert.Null(set);
    }

    // --- Transparency: JPEG thumbnails have no alpha, so transparent pixels must be composited
    //     onto a light background rather than flattened to black (a transparent icon/logo was black). ---

    [Fact]
    public void Generate_FullyTransparentSource_ThumbnailIsLight_NotBlack()
    {
        using var lib = new TempDir();
        var path = lib.Combine("empty.png");
        WriteTransparentPng(path, 256, 256, fg: null); // nothing drawn → fully transparent

        var set = new ThumbnailGenerator().Generate(path, [ThumbSize.Large]);

        using var thumb = SKBitmap.Decode(set!.Thumbs[ThumbSize.Large].Bytes);
        Assert.True(MaxLuma(thumb) > 240, $"fully-transparent source should flatten to near-white, got maxLuma {MaxLuma(thumb)}");
    }

    [Fact]
    public void Generate_PartlyTransparentSource_TransparentAreaIsLight()
    {
        using var lib = new TempDir();
        var path = lib.Combine("icon.png");
        WriteTransparentPng(path, 256, 256, fg: SKColors.Red); // red square centered, corners transparent

        var set = new ThumbnailGenerator().Generate(path, [ThumbSize.Large]);

        using var thumb = SKBitmap.Decode(set!.Thumbs[ThumbSize.Large].Bytes);
        var corner = thumb.GetPixel(2, 2);           // transparent corner → background
        int cornerLuma = (corner.Red * 299 + corner.Green * 587 + corner.Blue * 114) / 1000;
        Assert.True(cornerLuma > 220, $"transparent corner should be light, got luma {cornerLuma}");
    }

    [Fact]
    public void Generate_RotatedOpaquePhoto_KeepsColor()
    {
        // An EXIF-rotated opaque photo must thumbnail to its real color — the opacity-preserving
        // ApplyOrigin path must not accidentally flatten it to white or black.
        using var lib = new TempDir();
        var path = lib.Combine("rot.jpg");
        TestImages.WriteJpegWithOrientation(path, 400, 200, orientation: 6); // OrangeRed, rotate 90 CW

        var set = new ThumbnailGenerator().Generate(path, [ThumbSize.Large]);

        using var thumb = SKBitmap.Decode(set!.Thumbs[ThumbSize.Large].Bytes);
        var c = thumb.GetPixel(thumb.Width / 2, thumb.Height / 2);
        Assert.True(c.Red > 180 && c.Green < 150 && c.Blue < 90, $"expected orange-red center, got {c}");
    }

    /// <summary>A PNG with a transparent background and (optionally) a solid square centered in it.</summary>
    private static void WriteTransparentPng(string path, int w, int h, SKColor? fg)
    {
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.Transparent);
            if (fg is { } color)
            {
                using var paint = new SKPaint { Color = color };
                canvas.DrawRect(w / 4, h / 4, w / 2, h / 2, paint);
            }
        }
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path);
        data.SaveTo(stream);
    }

    private static int MaxLuma(SKBitmap b)
    {
        int max = 0;
        for (int y = 0; y < b.Height; y += Math.Max(1, b.Height / 16))
            for (int x = 0; x < b.Width; x += Math.Max(1, b.Width / 16))
            {
                var p = b.GetPixel(x, y);
                int l = (p.Red * 299 + p.Green * 587 + p.Blue * 114) / 1000;
                if (l > max) max = l;
            }
        return max;
    }
}
