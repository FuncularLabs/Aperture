using Reel.Core.Media;
using Reel.Core.Models;
using Reel.Core.Tests.Support;
using SkiaSharp;

namespace Reel.Core.Tests;

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
    public void Generate_ReturnsNull_ForNonImage()
    {
        using var lib = new TempDir();
        var path = lib.Combine("notreally.jpg");
        File.WriteAllText(path, "this is not an image");

        var set = new ThumbnailGenerator().Generate(path, ThumbSizes.All);

        Assert.Null(set);
    }
}
