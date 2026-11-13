using Aperture.Core.Formatting;

namespace Aperture.Core.Tests;

/// <summary>Quick view's video time, "elapsed / length" (0.9.2 V10).</summary>
public class MediaTimeTests
{
    [Theory]
    [InlineData(0, 83, "0:00 / 1:23")]
    [InlineData(12.9, 83, "0:12 / 1:23")]    // whole seconds gone by, not rounded up
    [InlineData(599, 600, "9:59 / 10:00")]
    [InlineData(83, 83, "1:23 / 1:23")]
    [InlineData(3599, 3600, "0:59:59 / 1:00:00")] // an hour or more: hours on both
    [InlineData(4000, 7322, "1:06:40 / 2:02:02")]
    [InlineData(-3, 83, "0:00 / 1:23")]      // never before the start
    [InlineData(90, 83, "1:23 / 1:23")]      // nor past the end
    public void Format_ElapsedAndLength(double elapsed, double length, string text) =>
        Assert.Equal(text, MediaTime.Format(TimeSpan.FromSeconds(elapsed), TimeSpan.FromSeconds(length)));

    [Theory]
    [InlineData(5, "0:05")]
    [InlineData(65, "1:05")]
    [InlineData(3661, "1:01:01")]
    [InlineData(-1, "0:00")]
    public void Format_WithoutALength_IsTheElapsedTimeAlone(double elapsed, string text) =>
        Assert.Equal(text, MediaTime.Format(TimeSpan.FromSeconds(elapsed), null));

    [Fact]
    public void BeforeTheLengthIsKnown() => Assert.Equal("0:00 / –:––", MediaTime.Pending);
}
