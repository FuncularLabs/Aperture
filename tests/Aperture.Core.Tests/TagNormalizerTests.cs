using Aperture.Core.Annotations;

namespace Aperture.Core.Tests;

public class TagNormalizerTests
{
    [Theory]
    [InlineData("date night", "date-night")]
    [InlineData("  multi   word  tag ", "multi-word-tag")]
    [InlineData("already-hyphen", "already-hyphen")]
    [InlineData("trump", "trump")]
    [InlineData("a -- b", "a-b")]
    [InlineData("  ", "")]
    public void Normalize_HyphenatesWords(string input, string expected) =>
        Assert.Equal(expected, TagNormalizer.Normalize(input));
}
