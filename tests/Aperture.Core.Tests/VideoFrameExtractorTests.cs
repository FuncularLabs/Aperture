using Aperture.Core.Media;
using SkiaSharp;

namespace Aperture.Core.Tests;

/// <summary>
/// Unit tests for the brightness helper that drives blackness-aware video frame selection.
/// (The ffmpeg extraction itself is integration-only and verified against real clips.)
/// </summary>
public class VideoFrameExtractorTests
{
    [Fact]
    public void MeanLuma_Black_IsZero()
    {
        using var b = Filled(32, 32, SKColors.Black);
        Assert.Equal(0, VideoFrameExtractor.MeanLuma(b));
    }

    [Fact]
    public void MeanLuma_White_IsNear255()
    {
        using var b = Filled(32, 32, SKColors.White);
        Assert.True(VideoFrameExtractor.MeanLuma(b) >= 250);
    }

    [Fact]
    public void MeanLuma_MidGray_IsNearHalf()
    {
        using var b = Filled(32, 32, new SKColor(128, 128, 128));
        Assert.InRange(VideoFrameExtractor.MeanLuma(b), 120, 136);
    }

    [Fact]
    public void MeanLuma_MostlyBlackWithOneBrightCorner_StaysLow()
    {
        // A black-intro frame with a sliver of brightness must still read as dark (mean, not max),
        // so selection keeps seeking for a genuinely brighter frame.
        using var b = Filled(32, 32, SKColors.Black);
        using (var canvas = new SKCanvas(b))
        using (var paint = new SKPaint { Color = SKColors.White })
            canvas.DrawRect(0, 0, 3, 3, paint); // ~1% white
        Assert.True(VideoFrameExtractor.MeanLuma(b) < 16);
    }

    private static SKBitmap Filled(int w, int h, SKColor color)
    {
        var b = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(b);
        canvas.Clear(color);
        return b;
    }
}
