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
}
