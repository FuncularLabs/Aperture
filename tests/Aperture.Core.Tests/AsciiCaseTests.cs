using Aperture.Core.Library;

namespace Aperture.Core.Tests;

/// <summary>
/// Paths compared as the library's columns compare them: only ASCII letters' case folded (0.9.2 review round 22), never
/// letters NTFS keeps apart (σ/ς, µ/μ), which .NET's OrdinalIgnoreCase takes for one.
/// </summary>
public class AsciiCaseTests
{
    [Theory]
    [InlineData(@"C:\Photos\a.jpg", @"c:\PHOTOS\A.JPG", true)] // ASCII letters' case
    [InlineData(@"C:\Lib\σ", @"C:\Lib\ς", false)]
    [InlineData(@"C:\Lib\10µm", @"C:\Lib\10μm", false)]
    [InlineData(@"C:\Lib\Été", @"C:\Lib\été", false)] // folded by Windows, not here: the safe side
    [InlineData(@"C:\Lib\[a]", @"C:\Lib\{A}", false)] // not letters, though 32 apart
    [InlineData(@"C:\a", @"C:\ab", false)]
    public void Same_FoldsOnlyAsciiLetters(string a, string b, bool same)
    {
        Assert.Equal(same, AsciiCase.Same(a, b));
        Assert.Equal(same, AsciiCase.Comparer.Equals(a, b));
        Assert.Equal(same, AsciiCase.Comparer.Compare(a, b) == 0);
        if (same)
            Assert.Equal(AsciiCase.Comparer.GetHashCode(a), AsciiCase.Comparer.GetHashCode(b));
    }

    [Fact]
    public void StartsWith_FoldsOnlyAsciiLetters()
    {
        Assert.True(AsciiCase.StartsWith(@"c:\photos\trips", @"C:\Photos\"));
        Assert.False(AsciiCase.StartsWith(@"C:\Lib\ς\a", @"C:\Lib\σ\"));
        Assert.False(AsciiCase.StartsWith(@"C:\", @"C:\Photos"));
    }

    [Fact]
    public void Nulls_AndOrder()
    {
        Assert.True(AsciiCase.Same(null, null));
        Assert.False(AsciiCase.Same("a", null));
        Assert.False(AsciiCase.Same(null, "a"));
        Assert.Equal(0, AsciiCase.Comparer.Compare(null, null));
        Assert.True(AsciiCase.Comparer.Compare(null, "a") < 0);
        Assert.True(AsciiCase.Comparer.Compare("a", null) > 0);
        Assert.True(AsciiCase.Comparer.Compare("a", "B") < 0);
        Assert.True(AsciiCase.Comparer.Compare("B", "a") > 0);
        Assert.True(AsciiCase.Comparer.Compare("ab", "A") > 0);
        Assert.Equal(2, new[] { "σ.jpg", "ς.jpg", "Σ.jpg".ToLowerInvariant() }.Distinct(AsciiCase.Comparer).Count());
    }
}
